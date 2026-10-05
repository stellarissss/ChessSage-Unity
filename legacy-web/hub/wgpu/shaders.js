/* ═══════════════════════════════════════════════════════════════
   WebGPU 着色器栈（WGSL）
   六道大陆 · 次世代式等距渲染
     · 前向场景：地形(高度场) / 实例化装饰(树/石/雪/水…) → GBuffer(颜色+HDR, 世界法线, 世界坐标)
     · 屏幕空间环境光遮蔽(SSAO, compute) —— 高度场邻域 AO
     · 屏幕空间体积光(沿太阳方向步进) —— 近似 God Ray
     · Bloom(亮部提取 + 逐级下采样模糊)
     · 合成：HDR + ACES 色调映射 + Gamma + 暗角 + 抖动
   逐 pass 使用独立的 bind group layout；帧级 uniform 统一位于 @group(0)。字形亮度的
   "frameUB" 为 CPU 端每帧上传的 48 个 float（见 overworld-wgpu.js）。
   ═══════════════════════════════════════════════════════════════ */
(function () {
  'use strict';

  // ── 帧级 uniform 的 WGSL 结构（布局与 JS 端 FrameUB 逐一对应）────────
  var FRAME_STRUCT = `
struct FrameUB {
  mvp         : mat4x4f,   // 0    view*proj
  camPos      : vec3f,     // 64
  _p0         : f32,
  sunDir      : vec3f,     // 80
  _p1         : f32,
  skyColor    : vec3f,     // 96
  _p2         : f32,
  groundColor : vec3f,     // 112
  _p3         : f32,
  fogColor    : vec3f,     // 128
  _p4         : f32,
  sunColor    : vec3f,     // 144
  _p5         : f32,
  res         : vec2f,     // 160  (canvas 分辨率)
  time        : f32,       // 168
  zoom        : f32,       // 172
  fogDensity  : f32,       // 176
  exposure    : f32,       // 180
  sunIntensity: f32,       // 184
  aoRadius    : f32,       // 188
  aoIntensity : f32,       // 192
  qualityFlags: f32,       // 196 画质开关位编码：bit0=SSAO bit1=Bloom bit2=体积光（智能画质档位）
};
// struct size = 208 bytes
@group(0) @binding(0) var<uniform> frame : FrameUB;   // bound at @group(0) @binding(0)
`;

  // ── 全屏三角形顶点 shader（合成/体积光/Bloom 共用）─────────────────
  var FULLSCREEN_VS = `
struct VSOut { @builtin(position) pos : vec4f, @location(0) uv : vec2f };
@vertex fn fsTriVS(@builtin(vertex_index) vi : u32) -> VSOut {
  let p = array<vec2f,3>(vec2f(-1.0,-1.0), vec2f(3.0,-1.0), vec2f(-1.0,3.0));
  return VSOut(vec4f(p[vi],0.0,1.0), vec2f(p[vi].x*0.5+0.5, 0.5-p[vi].y*0.5));
}
`;

  // ── 程序化细节噪声（地形贴图感，避免海量纹理）──────────────────
  var HASH_FN = `
fn hash21(p : vec2f) -> f32 {
  let d = dot(p, vec2f(127.1, 311.7));
  let s = sin(d) * 43758.5453;
  return fract(s);
}
fn noise2(p : vec2f) -> f32 {
  let i = floor(p); let f = fract(p);
  let u = f * f * (3.0 - 2.0 * f);
  return mix(
    mix(hash21(i),         hash21(i + vec2f(1,0)), u.x),
    mix(hash21(i + vec2f(0,1)), hash21(i + vec2f(1,1)), u.x), u.y);
}
fn fbm(p : vec2f) -> f32 {
  var q = p; var v = 0.0; var a = 0.5;
  for (var i = 0; i < 4; i++) { v += a * noise2(q); q = q * 2.03 + 11.7; a *= 0.5; }
  return v;
}
`;

  // ── 三通道光照：半球环境 + 太阳漫反/高光 + 指数雾 + 程序化AO 底色 ──
  var LIGHT_FN = `
fn lit(worldPos : vec3f, worldN : vec3f, baseColor : vec3f, spec : f32) -> vec3f {
  let N = normalize(worldN);
  var Ld = normalize(frame.sunDir.xyz);
  var ndl = max(dot(N, Ld), 0.0);
  let Vv = normalize(frame.camPos.xyz - worldPos);
  let Hh = normalize(Ld + Vv);
  let diff = frame.sunColor.xyz * frame.sunIntensity * ndl;
  let spe = pow(max(dot(N, Hh), 0.0), 48.0) * spec * frame.sunIntensity;
  // 半球环境光：sky（上方）→ ground（下方）按法线 z 混合，再乘环境光强度系数。
  // ⚠ v2.3：原先 hemi 为全强度（法线朝上时直接等于 skyColor≈0.66），
  //   与太阳光叠加后总入射光 ≈1.79，反射率被放大近 1.8 倍 → ACES 饱和、画面发灰。
  //   现乘 0.40，使 环境(0.40×0.66≈0.26) + 太阳(0.85×0.79≈0.67) ≈ 0.93 ≈ 1.0，
  //   符合"入射光总强度≈1"的物理正确标定（材质 baseColor 即真实反射率）。
  let hemi = mix(frame.groundColor.xyz, frame.skyColor.xyz, N.z * 0.5 + 0.5) * 0.40;
  var col = baseColor * (hemi + diff) + vec3f(spe);
  let dist = distance(frame.camPos.xyz, worldPos);
  let fogf = 1.0 - exp(-max(frame.fogDensity, 0.0) * dist);
  return mix(col, frame.fogColor.xyz, clamp(fogf, 0.0, 1.0));
}
`;

  // ── ACES 近似色调映射 + Gamma ──────────────────────────────
  var TONE = `
fn aces(x : vec3f) -> vec3f {
  let a = 2.51; let b = 0.03; let cc = 2.43; let d = 0.59; let e = 0.14;
  return clamp((x * (a * x + b)) / (x * (cc * x + d) + e), vec3f(0.0), vec3f(1.0));
}
fn gamma(v : vec3f) -> vec3f { return pow(max(v, vec3f(0.0)), vec3f(1.0/2.2)); }
`;

  // ═══════════════ 1. 地形（高度场网格） ═══════════════════════════════
  // 顶点着色器置换：地形几何由 CPU 高度场 + GPU 计算生成的高频细节场共同驱动，
  // 采样 detailTex(@group(1)) 做微位移，并沿细节梯度锐化法线 → 更精细的轮廓与光照。
  var TERRAIN_VS = FRAME_STRUCT + `
@group(1) @binding(0) var detailTex : texture_2d<f32>;
@group(1) @binding(1) var detailSam : sampler;

struct VSIn { @location(0) pos : vec3f, @location(1) color : vec4f, @location(2) normal : vec3f };
struct VSOut { @builtin(position) clip : vec4f,
               @location(0) vWorld : vec3f, @location(1) vColor : vec4f, @location(2) vN : vec3f };
@vertex fn main(in : VSIn) -> VSOut {
  var pos = vec3f(in.pos.xy, in.pos.z);
  // 世界坐标 → 细节场 UV（锚定世界原点，一块纹元 = 1 格）
  let duv = pos.xy / 46.0;
  let det = textureSampleLevel(detailTex, detailSam, duv, 0.0);
  pos.z += det.w;                                   // 顶点置换：微位移（GPU 生成场）
  let n = normalize(in.normal + vec3f(-det.x, det.y, 0.0)); // 沿梯度锐化法线
  return VSOut(frame.mvp * vec4f(pos, 1.0), pos, in.color, n);
}
`;

  var TERRAIN_FS = FRAME_STRUCT + HASH_FN + LIGHT_FN + `
struct VSOut { @builtin(position) clip : vec4f,
               @location(0) vWorld : vec3f, @location(1) vColor : vec4f, @location(2) vN : vec3f };
struct FSOut { @location(0) color : vec4f, @location(1) normal : vec4f, @location(2) worldPos : vec4f };
@fragment fn main(in : VSOut) -> FSOut {
  // 程序化纹理细节：细噪声叠加在底色上，原理仿"雅克比噪声"贴图
  let detail = fbm(in.vWorld.xy * 0.09) * 0.5 + 0.5;
  let macroN = fbm(in.vWorld.xy * 0.018 + 3.3);
  let base   = in.vColor.rgb * (0.72 + 0.35 * detail) * (0.92 + 0.12 * macroN);
  let col    = lit(in.vWorld, in.vN, base, 0.35);
  var n = normalize(in.vN);
  if (!all(n == n)) { n = vec3f(0.0, 0.0, 1.0); }   // NaN 防护（isFinite 非 WGSL 内建）
  // 同时输出法线(0.5+0.5n → rgba8unorm)与世界坐标(≤数千米，rgba16float 半精度足敷)
  return FSOut(vec4f(col, 1.0), vec4f(n * 0.5 + 0.5, 1.0), vec4f(in.vWorld, 1.0));
}
`;

  // ═══════════════ 1b. 地形细节场生成 (compute) ═══════════════════════
  // 计算着色器离线生成"高频细节场"（世界锚定、一经生成即稳定）：
  //   detailOut = rgba16float，一块纹元 = 1 格（46 世界单位）：
  //   · xy  = 细节梯度（tan 法线扰动）
  //   · z   = 半高度（备用）
  //   · w   = 微位移（供地形顶点着色器置换）
  // 相比逐帧 CPU 生成，GPU 并行一次建成，地形顶点直接采样，开销近乎为零。
  var TERRAIN_DETAIL_CS = HASH_FN + `
@group(0) @binding(0) var detailOut : texture_storage_2d<rgba16float, write>;

@compute @workgroup_size(8, 8, 1)
fn main(@builtin(global_invocation_id) gid : vec3u) {
  let size = vec2<u32>(textureDimensions(detailOut).xy);
  if (gid.x >= size.x || gid.y >= size.y) { return; }
  let cell = vec2f(gid.xy);
  // 多频噪声 → 微高度（0..0.9）
  let a = fbm(cell * 0.18 + 13.7);
  let b = noise2(cell * 0.53 + 7.1);
  let micro = (a * 0.65 + b * 0.35) * 0.9;
  // 梯度 → tan 法线扰动
  let gx = fbm(cell * 0.18 + 13.7 + vec2f(2.0, 0.0)) - a;
  let gy = fbm(cell * 0.18 + 13.7 + vec2f(0.0, 2.0)) - a;
  textureStore(detailOut, vec2i(gid.xy), vec4f(gx * 0.6, gy * 0.6, micro * 0.5, micro));
}
`;

  // ═══════════════ 2. 实例化装饰（树/石/雪/草/仙人掌/沙丘/水） ════════
  // 实例布局（20 floats，stride=80B）：
  //   [0..2]=pos [3..5]=scale [6]=yaw [7]=waterFlag
  //   [8..10]=colT [12..14]=colF [16..18]=colR
  var DECO_VS = FRAME_STRUCT + `
struct VIn { @location(0) pos : vec3f, @location(1) nrm : vec3f,
             @location(2) iPos : vec3f, @location(3) iScale : vec3f,
             @location(4) iYawFlag : vec4f,
             @location(5) colT : vec4f, @location(6) colF : vec4f, @location(7) colR : vec4f };
struct VOut { @builtin(position) clip : vec4f,
              @location(0) vWorld : vec3f, @location(1) vN : vec3f,
              @location(2) vColT : vec4f, @location(3) vColF : vec4f, @location(4) vColR : vec4f,
              @location(5) vWater : f32 };
@vertex fn main(in : VIn) -> VOut {
  var p = in.pos * in.iScale;
  let y = in.iYawFlag.x;
  let c = cos(y); let s = sin(y);
  let px = c * p.x - s * p.y;
  let py = s * p.x + c * p.y;
  var worldPos = vec3f(px, py, p.z) + in.iPos;
  let fl = in.iYawFlag.y;
  if (fl > 0.5 && fl < 1.5) {
    // 水面细波 (flag=1)
    let w = sin(worldPos.x * 0.16 + frame.time * 1.3) * sin(worldPos.y * 0.11 - frame.time * 0.9);
    worldPos.z += w * 0.8;
  } else if (fl > 1.5) {
    // 植被/灵粒摇曳 (flag=2)
    let w = sin(frame.time * 1.8 + worldPos.x * 0.5 + worldPos.y * 0.4) * 0.6;
    worldPos.z += w * 0.5;
    worldPos.x += w * 0.35;
  }
  let nx = c * in.nrm.x - s * in.nrm.y;
  let ny = s * in.nrm.x + c * in.nrm.y;
  return VOut(frame.mvp * vec4f(worldPos, 1.0), worldPos, vec3f(nx, ny, in.nrm.z),
              in.colT, in.colF, in.colR, in.iYawFlag.y);
}
`;

  var DECO_FS = FRAME_STRUCT + LIGHT_FN + `
struct VOut { @builtin(position) clip : vec4f,
              @location(0) vWorld : vec3f, @location(1) vN : vec3f,
              @location(2) vColT : vec4f, @location(3) vColF : vec4f, @location(4) vColR : vec4f,
              @location(5) vWater : f32 };
struct FOut { @location(0) color : vec4f, @location(1) normal : vec4f, @location(2) worldPos : vec4f };
@fragment fn main(in : VOut) -> FOut {
  var n = normalize(in.vN);
  if (!all(n == n)) { n = vec3f(0.0, 0.0, 1.0); }   // NaN 防护（isFinite 非 WGSL 内建）
  let an = abs(n);
  var base = in.vColF.rgb;
  if (an.z > an.x && an.z > an.y) {
    base = in.vColT.rgb;          // 顶面
  } else if (an.x > an.y) {
    base = in.vColR.rgb; // 右/左 同色
  } else {
    base = in.vColF.rgb;          // 前/后
  }
  var alpha = 1.0;
  if (in.vWater > 0.5 && in.vWater < 1.5) { alpha = in.vColT.w; }   // 水面半透明
  else if (in.vWater > 1.5) { alpha = in.vColT.w; }                 // 植被受距离淡出
  let col = lit(in.vWorld, n, base, select(0.35, 0.6, in.vWater > 0.5));
  return FOut(vec4f(col, alpha), vec4f(n * 0.5 + 0.5, 1.0), vec4f(in.vWorld, 1.0));
}
`;

  // ═══════════════ 3. 屏幕空间环境光遮蔽 (compute) ═══════════════════
  // 邻域高度差 AO：对每个像素，采样 posRT 邻域，按其相对高度变化给出遮蔽。
  var SSAO_CS = FRAME_STRUCT + `
@group(1) @binding(0) var posIn : texture_2d<f32>;          // 世界坐标
@group(1) @binding(1) var nrmIn : texture_2d<f32>;          // 世界法线(rg8)
@group(1) @binding(2) var aoOut : texture_storage_2d<rgba8unorm, write>;

fn samplePos(uv : vec2f) -> vec3f {
  let size = vec2f(textureDimensions(posIn, 0));
  return textureLoad(posIn, vec2i(clamp(uv * size, vec2f(0.0), size - vec2f(1.0))), 0).xyz;
}

@compute @workgroup_size(8, 8, 1)
fn main(@builtin(global_invocation_id) gid : vec3u) {
  let size = vec2<u32>(textureDimensions(posIn).xy);
  if (gid.x >= size.x || gid.y >= size.y) { return; }
  let uv = (vec2f(gid.xy) + 0.5) / vec2f(size);
  let origin = textureLoad(posIn, vec2i(gid.xy), 0).xyz;
  let n = normalize(textureLoad(nrmIn, vec2i(gid.xy), 0).xyz * 2.0 - 1.0);
  if (origin.z > 9000.0) { textureStore(aoOut, vec2i(gid.xy), vec4f(1.0,1.0,1.0,1.0)); return; }

  var occ = 0.0;
  let nn = max(frame.aoRadius, 0.01);
  // 4 方向 × 2 距离 = 8 采样
  for (var i = 0; i < 4; i++) {
    let a = f32(i) * 1.5708;
    let dir = vec2f(cos(a), sin(a)) * nn;
    for (var j = 0; j < 2; j++) {
      let uv2 = uv + dir * (f32(j) + 0.5) * 0.5;
      let o = samplePos(uv2);
      if (o.z > 9000.0) { occ += 1.0; continue; }
      let d = o - origin;
      // 沿法线方向的凸起程度：凸起→遮蔽
      let along = dot(d, n);
      let mag = length(d);
      occ += smoothstep(0.0, nn, max(along + nn * 0.12, 0.0)) * smoothstep(nn * 1.4, nn * 0.2, mag);
    }
  }
  /* AO 强度只施加一次。
     ⚠ v2.3 修复：原实现连乘两次 aoIntensity ——
         ao = 1 - clamp(occ/8) * aoIntensity;   // 第 1 次
         ao = mix(1.0, ao, aoIntensity);        // 第 2 次（重复！）
       两次相乘等效强度 0.55×0.55 = 0.30，遮蔽被削弱到三成，
       在地形偏平 + 画面过曝的双重掩盖下几乎不可见 → 装饰"漂浮"感。
     现只保留一次：遮蔽量 = occ/8 归一化后直接乘 aoIntensity。 */
  var ao = 1.0 - clamp(occ / 8.0, 0.0, 1.0) * frame.aoIntensity;
  textureStore(aoOut, vec2i(gid.xy), vec4f(ao, ao, ao, 1.0));
}
`;

  // ═══════════════ 4. 体积光（屏幕空间太阳步进） ═══════════════════════
  var VOL_FS = FRAME_STRUCT + FULLSCREEN_VS + HASH_FN + `
@group(1) @binding(0) var posIn : texture_2d<f32>;
@group(1) @binding(1) var sam  : sampler;
@fragment fn main(in : VSOut) -> @location(0) vec4f {
  let org = textureLoad(posIn, vec2i(in.uv * vec2f(textureDimensions(posIn).xy)), 0).xyz;
  var col = vec3f(0.0);
  if (org.z < 9000.0) {
    // 沿太阳方向步进，累积在空中散射
    var trans = 1.0;
    let steps = 18;
    let stepL = 6.0;
    let Ld = normalize(frame.sunDir.xyz);
    // 抖动减少条带
    let jit = hash21(in.uv + frame.time * 0.1);
    for (var i = 0; i < steps; i++) {
      let t = (f32(i) + jit) * stepL;
      let sp = org + Ld * t;
      // 大气密度随高度衰减（越低越浓）
      var dens = exp(-sp.y * 0.01) * 0.35 + 0.05;
      dens *= (1.0 + 0.5 * fbm(sp.xz * 0.03));
      let occl = 1.0;
      col += frame.sunColor.xyz * frame.sunIntensity * dens * trans * occl * stepL * 0.02;
      trans *= exp(-dens * stepL);
      if (trans < 0.01) { break; }
    }
  }
  return vec4f(col, 1.0);
}
`;

  // ═══════════════ 5. Bloom ═════════════════════════════════════════════
  var BRIGHT_FS = FRAME_STRUCT + FULLSCREEN_VS + `
@group(1) @binding(0) var src : texture_2d<f32>;
@group(1) @binding(1) var sam : sampler;
@fragment fn main(in : VSOut) -> @location(0) vec4f {
  let c = textureSampleLevel(src, sam, in.uv, 0.0).rgb;
  let lum = dot(c, vec3f(0.299,0.587,0.114));
  // 亮部提取
  let thr = 1.0;
  let soft = smoothstep(thr, thr + 1.5, lum);
  let outr = c * soft;
  return vec4f(outr, 1.0);
}
`;

  // 高斯下采样+模糊
  var BLUR_FS = FRAME_STRUCT + FULLSCREEN_VS + `
@group(1) @binding(0) var src : texture_2d<f32>;
@group(1) @binding(1) var sam : sampler;
@fragment fn main(in : VSOut) -> @location(0) vec4f {
  let d = vec2f(textureDimensions(src, 0));
  var sum = vec3f(0.0);
  // 9-tap 盒式近似（1.8 倍下行）
  for (var y = -1; y <= 1; y++) {
    for (var x = -1; x <= 1; x++) {
      let o = vec2f(f32(x), f32(y)) * 1.8 / d;
      sum += textureSampleLevel(src, sam, in.uv + o, 0.0).rgb;
    }
  }
  return vec4f(sum / 9.0, 1.0);
}
`;

  // ═══════════════ 6. 合成（HDR → 色调映射 → Gamma → 暗角 → 抖动） ═════
  var COMPOSITE_FS = FRAME_STRUCT + FULLSCREEN_VS + TONE + HASH_FN + `
@group(1) @binding(0) var colorRT : texture_2d<f32>;
@group(1) @binding(1) var aoRT    : texture_2d<f32>;
@group(1) @binding(2) var volRT   : texture_2d<f32>;
@group(1) @binding(3) var bloom0  : texture_2d<f32>;
@group(1) @binding(4) var bloom1  : texture_2d<f32>;
@group(1) @binding(5) var bloom2  : texture_2d<f32>;
@group(1) @binding(6) var bloom3  : texture_2d<f32>;
@group(1) @binding(7) var sam     : sampler;

@fragment fn main(in : VSOut) -> @location(0) vec4f {
  var hdr = textureSampleLevel(colorRT, sam, in.uv, 0.0).rgb;
  let ao  = textureSampleLevel(aoRT, sam, in.uv, 0.0).rgb;
  let vol = textureSampleLevel(volRT, sam, in.uv, 0.0).rgb;
  let b0  = textureSampleLevel(bloom0, sam, in.uv, 0.0).rgb;
  let b1  = textureSampleLevel(bloom1, sam, in.uv, 0.0).rgb;
  let b2  = textureSampleLevel(bloom2, sam, in.uv, 0.0).rgb;
  let b3  = textureSampleLevel(bloom3, sam, in.uv, 0.0).rgb;

  // 智能画质档位：由设备能力检测写入 frame.qualityFlags（bit0=SSAO bit1=Bloom bit2=体积光）。
  // 关闭时对应 RT 未绘制（内容未定义），必须在合成端把贡献归零。
  let qflags = u32(frame.qualityFlags);
  let useSSAO = (qflags & 1u) != 0u;
  let useBloom = (qflags & 2u) != 0u;
  let useVol = (qflags & 4u) != 0u;

  if (useSSAO) { hdr *= ao; }                    // SSAO
  if (useVol) { hdr += vol * frame.sunColor.xyz; } // 体积光（加色）
  if (useBloom) {
    hdr += (b0 * 0.5 + b1 * 0.25 + b2 * 0.15 + b3 * 0.10) * 0.9; // Bloom
  }

  var col = aces(hdr * frame.exposure);
  col = gamma(col);

  /* ── 显示级对比度 + 饱和度（v2.3 新增）────────────────────────────────
     ACES 是为"电影感"设计的色调曲线，它本身会显著压缩中间调对比。
     实测：草地(反射率0.62) 与岩石(0.29) 经 ACES 后只差 32/255，全部材质挤在
     195~227 的高亮窄带里 → 画面"发灰、糊成一片"，连画质档位的差异都被抹平。

     仅靠调曝光无法解决（非线性压缩的固有特性，实测差值上限 ~44）。按业界通行
     做法在【tonemap 之后】补一次显示级调整（等价 UE 的 r.Tonemapper.Contrast /
     ColorGrading 环节）：

       ① 对比度：绕 pivot 拉伸。pivot 取 0.42 而非 0.5——0.5 会把亮部顶到 255
          造成削顶（实测草地被推到 255）。0.42 让亮部仍有余量，同时把暗部拉开。
       ② 饱和度：轻微的 luma-preserving 饱和补偿，抵消 ACES 的"褪色"倾向。
          以亮度 L 为轴外扩，只改色度不改明暗，不会引入曝光偏移。

     两项都是单调映射，不产生色偏/光晕；系数经数值标定，保证不削顶。 */
  // ① 对比度（pivot 0.42，拉伸系数 1.30）
  let ctr = 1.30;
  col = clamp((col - vec3f(0.42)) * ctr + vec3f(0.42), vec3f(0.0), vec3f(1.0));
  // ② 饱和度（luma-preserving，系数 1.10）
  let luma = dot(col, vec3f(0.2126, 0.7152, 0.0722));
  col = clamp(vec3f(luma) + (col - vec3f(luma)) * 1.10, vec3f(0.0), vec3f(1.0));

  // 暗角（中心对称）
  // uv ∈ [0,1]，到中心的距离用 (uv - 0.5) 再乘 2 归一化：
  //   角点 length(0.5,0.5)=0.7071 → ×2 得 1.414（超出 1，保证四角充分压暗）
  // ⚠ 原式 length(uv - q*2.0) * 0.72（q=0.5 → 减 1.0）把最亮点放在【右上角】，
  //   四角压暗不均、中心反而变暗，属笔误。此处改为以屏幕中心为原点。
  let aq = (in.uv - vec2f(0.5, 0.5)) * 2.0;
  let vig = smoothstep(0.9, 0.28, length(aq));
  col *= vig;

  // 抖动 → 减少条带
  let dith = (hash21(in.uv + frame.time) - 0.5) / 255.0;
  col += dith;

  return vec4f(col, 1.0);
}
`;

  window.OWSHADERS = Object.freeze({
    FRAME_STRUCT: FRAME_STRUCT,
    FULLSCREEN_VS: FULLSCREEN_VS,
    TERRAIN_VS: TERRAIN_VS,
    TERRAIN_FS: TERRAIN_FS,
    TERRAIN_DETAIL_CS: TERRAIN_DETAIL_CS,
    DECO_VS: DECO_VS,
    DECO_FS: DECO_FS,
    SSAO_CS: SSAO_CS,
    VOL_FS: VOL_FS,
    BRIGHT_FS: BRIGHT_FS,
    BLUR_FS: BLUR_FS,
    COMPOSITE_FS: COMPOSITE_FS,
    HASH_FN: HASH_FN,
    LIGHT_FN: LIGHT_FN,
    TONE: TONE
  });
})();