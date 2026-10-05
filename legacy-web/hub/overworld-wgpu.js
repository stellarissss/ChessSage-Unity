/* ═══════════════════════════════════════════════════════════════
   WebGPU 大地图渲染器（六道大陆）
   作为 OverworldGame 的 WebGPU 实现，替换 iso-engine 的 CSS 3D 阶段：

    渲染管线（次世代式，前向 GBuffer 精简版）：
      · 地形(高度场网格) + 实例化装饰(树/石/雪/水/植被/灵粒) → 颜色(HDR)+法线+世界坐标
      · SSAO(compute) → 屏幕空间环境光遮蔽
      · 体积光(fullscreen 沿太阳步进) → God Ray 近似
      · Bloom(亮部提取 + 逐级下采样)
      · 合成：HDR + ACES + Gamma + 暗角 + 抖动

    工程特性：
      · bind group / 实例化 / 分块视口剔除（只绘制可见 chunk 的索引/实例区间）
      · 资源构建在 Web Worker 中执行（多线程），失败则退回主线程同步构建
      · DOM 覆盖层(玩家/POI/告示牌) 用与 GPU 相同的 mvp 逐帧投影，保证严丝合缝
      · frame uniform 每帧上传（相机/光照/雾/动画时间）

   与 overworld-ui.js 兼容：保留 getSamsara / syncSamsara / refreshFromUI 等接口。
   ═══════════════════════════════════════════════════════════════ */
'use strict';
import './wgpu/shaders.js';

var CELL = 46;          // 世界坐标每格长度（与 DOM 版本一致，方便 POI 换算）
var WALL = 6;           // 边缘群山外障厚度（格）
var CHUNK = 16;         // 分块尺寸（格）
var EXPLORED_KEY = 'chesssage_ow_explored';
var UI = null;          // DOM OverworldUI 句柄（必须提前声明：ES Module 中对未声明标识符的读取会抛 ReferenceError，
                        // 否则在 WebGPU 初始化失败时「if (UI) removeLoading」反倒把原始错误"吞"成 UI is not defined）
var S = window.OWSHADERS;

var REALM_NAMES = {
  hell: '地狱道', hungry: '饿鬼道', animal: '畜生道',
  human: '人道', asura: '阿修罗道', heaven: '天道'
};

var BIOME_LIST = [
  { id: 'nw_forest',  c: [0x2e, 0x6e, 0x3b], h: 1.5 },
  { id: 'n_snow',     c: [0xdc, 0xe6, 0xef], h: 1.7 },
  { id: 'ne_pasture', c: [0x8f, 0xbf, 0x54], h: 0.9 },
  { id: 'w_waste',    c: [0xb8, 0xa0, 0x6a], h: 0.7 },
  { id: 'c_plain',    c: [0x6c, 0xa6, 0x59], h: 0.65 },
  { id: 'east_ridge', c: [0x7d, 0x93, 0x84], h: 2.4 },
  { id: 'sw_dungeon', c: [0x24, 0x1c, 0x30], h: 0.2 },
  { id: 's_desert',   c: [0xd8, 0xb2, 0x5a], h: 0.5 },
  { id: 'se_battle',  c: [0xa4, 0x44, 0x3f], h: 0.9 }
];
var C_WATER = [0x1E, 0x6A, 0x96];
var C_ROAD = [0xC9, 0xB3, 0x7E];
var C_ROCK = [0x7F, 0x7A, 0x73];
// 索引对齐 BIOME_LIST 顺序的区域植被类型表
var BIOME_KINDS = [
  ['pine', 'tree', 'grass', 'rock'],   // nw_forest
  ['snow', 'pine', 'snow', 'rock'],    // n_snow
  ['grass', 'tree', 'grass', 'rock'],  // ne_pasture
  ['dead', 'rock', 'dune'],            // w_waste
  ['tree', 'grass', 'rock', 'tree'],   // c_plain
  ['rock', 'pine', 'rock'],            // east_ridge
  ['ruin', 'rock', 'dead'],            // sw_dungeon
  ['cacti', 'dune', 'rock'],           // s_desert
  ['dead', 'rock', 'ruin']             // se_battle
];
// 索引对齐 BIOME_LIST 的区域装饰密度（区分繁茂/荒芜，避免全图均一 5%）
var BIOME_DENSITY = [0.16, 0.10, 0.14, 0.06, 0.12, 0.08, 0.055, 0.06, 0.07];

/* ═══════════════ 数学辅助（mat4，列主序 Float32Array） ═══════════════ */
function m4Identity() { var m = new Float32Array(16); m[0] = m[5] = m[10] = m[15] = 1; return m; }
function m4Mul(a, b) {
  var o = new Float32Array(16);
  for (var c = 0; c < 4; c++) for (var r = 0; r < 4; r++) {
    o[c * 4 + r] = a[0 * 4 + r] * b[c * 4 + 0] + a[1 * 4 + r] * b[c * 4 + 1]
                 + a[2 * 4 + r] * b[c * 4 + 2] + a[3 * 4 + r] * b[c * 4 + 3];
  }
  return o;
}
function m4LookAt(eye, center, up) {
  var zx = eye[0] - center[0], zy = eye[1] - center[1], zz = eye[2] - center[2];
  var len = Math.hypot(zx, zy, zz) || 1; zx /= len; zy /= len; zz /= len;
  var ux = up[0], uy = up[1], uz = up[2];
  var xx = uy * zz - uz * zy, xy = uz * zx - ux * zz, xz = ux * zy - uy * zx;
  len = Math.hypot(xx, xy, xz) || 1; xx /= len; xy /= len; xz /= len;
  var yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;
  var m = m4Identity();
  m[0] = xx; m[1] = yx; m[2] = zx;
  m[4] = xy; m[5] = yy; m[6] = zy;
  m[8] = xz; m[9] = yz; m[10] = zz;
  m[12] = -(xx * eye[0] + xy * eye[1] + xz * eye[2]);
  m[13] = -(yx * eye[0] + yy * eye[1] + yz * eye[2]);
  m[14] = -(zx * eye[0] + zy * eye[1] + zz * eye[2]);
  return m;
}
function m4Ortho(l, r, b, t, n, f) {
  var m = m4Identity();
  m[0] = 2 / (r - l); m[5] = 2 / (t - b); m[10] = -2 / (f - n);
  m[12] = -(r + l) / (r - l); m[13] = -(t + b) / (t - b); m[14] = -(f + n) / (f - n);
  return m;
}
function m4Invert(m) {
  var o = new Float32Array(16);
  m4InvertTo(m, o); return o;
}
function m4InvertTo(m, invOut) {
  var a00 = m[0], a01 = m[1], a02 = m[2], a03 = m[3],
      a10 = m[4], a11 = m[5], a12 = m[6], a13 = m[7],
      a20 = m[8], a21 = m[9], a22 = m[10], a23 = m[11],
      a30 = m[12], a31 = m[13], a32 = m[14], a33 = m[15],
      b00 = a00 * a11 - a01 * a10, b01 = a00 * a12 - a02 * a10,
      b02 = a00 * a13 - a03 * a10, b03 = a01 * a12 - a02 * a11,
      b04 = a01 * a13 - a03 * a11, b05 = a02 * a13 - a03 * a12,
      b06 = a20 * a31 - a21 * a30, b07 = a20 * a32 - a22 * a30,
      b08 = a20 * a33 - a23 * a30, b09 = a21 * a32 - a22 * a31,
      b10 = a21 * a33 - a23 * a31, b11 = a22 * a33 - a23 * a32,
      det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
  if (!det) { return; }
  det = 1 / det;
  invOut[0] = (a11 * b11 - a12 * b10 + a13 * b09) * det;
  invOut[1] = (a02 * b10 - a01 * b11 - a03 * b09) * det;
  invOut[2] = (a31 * b05 - a32 * b04 + a33 * b03) * det;
  invOut[3] = (a22 * b04 - a21 * b05 - a23 * b03) * det;
  invOut[4] = (a12 * b08 - a10 * b11 - a13 * b07) * det;
  invOut[5] = (a00 * b11 - a02 * b08 + a03 * b07) * det;
  invOut[6] = (a32 * b02 - a30 * b05 - a33 * b01) * det;
  invOut[7] = (a20 * b05 - a22 * b02 + a23 * b01) * det;
  invOut[8] = (a10 * b10 - a11 * b08 + a13 * b06) * det;
  invOut[9] = (a01 * b08 - a00 * b10 - a03 * b06) * det;
  invOut[10] = (a30 * b04 - a31 * b02 + a33 * b01) * det;
  invOut[11] = (a21 * b02 - a20 * b04 - a23 * b01) * det;
  invOut[12] = (a11 * b07 - a10 * b09 - a12 * b06) * det;
  invOut[13] = (a00 * b09 - a01 * b07 + a02 * b06) * det;
  invOut[14] = (a31 * b01 - a30 * b03 - a32 * b00) * det;
  invOut[15] = (a20 * b03 - a21 * b01 + a22 * b00) * det;
}
function m4Transform(m, v) {
  var w = m[3] * v[0] + m[7] * v[1] + m[11] * v[2] + m[15];
  return [
    (m[0] * v[0] + m[4] * v[1] + m[8] * v[2] + m[12]) / w,
    (m[1] * v[0] + m[5] * v[1] + m[9] * v[2] + m[13]) / w,
    (m[2] * v[0] + m[6] * v[1] + m[10] * v[2] + m[14]) / w
  ];
}

/* ═══════════════ buildWorld：在 Worker（或主线程）中构建网格/实例 ═══
   自包含函数：可序列化为字符串注入 Blob Worker。不引用任何外部变量。 */
function buildWorld(spec) {
  var W = spec.W, H = spec.H, CELL = spec.CELL, CHUNK = 16;
  var flags = spec.flags, biome = spec.biome; // Int32Array
  var NV = (W + 1) * (H + 1);

  // hash / noise
  function rng(x, y, salt) {
    var h = (x * 374761393 + y * 668265263 + salt * 2246822519) | 0;
    h = (h ^ (h >>> 13)) * 1274126177; h = h ^ (h >>> 16);
    return (h >>> 0) / 4294967295;
  }
  function noise(nx, ny) { // value noise, unit cell
    var xi = Math.floor(nx), yi = Math.floor(ny);
    var xf = nx - xi, yf = ny - yi;
    var u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
    var a = rng(xi, yi, 40), b = rng(xi + 1, yi, 40), c = rng(xi, yi + 1, 40), d = rng(xi + 1, yi + 1, 40);
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
  }
  function fbm(x, y) { return noise(x, y) * 0.6 + noise(x * 2.3, y * 2.3) * 0.3 + noise(x * 5.3 + 4, y * 5.3 + 3) * 0.1; }

  var F_WATER = 1, F_ROAD = 2, F_MOUNT = 4, F_WALL = 8, F_SOLID = 16;

  /* 生物群系采样：用分形噪声做域扭曲（domain warp），把原本由轴对齐矩形逐格填充的
     平直线边界“卷”成有机、自然过渡的曲线边缘——景观之间不再是笔直的线。 */
  function warpBiome(tx, ty) {
    var s = 4.5;  // 扭曲幅度（格），越大边界越蜿蜒
    var wx = tx + (fbm(tx * 0.115 + 7.3, ty * 0.115 + 1.1) - 0.5) * s * 2;
    var wy = ty + (fbm(tx * 0.115 + 3.7, ty * 0.115 + 5.9) - 0.5) * s * 2;
    if (wx < 0) wx = 0; else if (wx >= W - 1) wx = W - 1;
    if (wy < 0) wy = 0; else if (wy >= H - 1) wy = H - 1;
    return biome[(wy | 0) * W + (wx | 0)];
  }

  /* ── 地形高度场（v2.3 重做）────────────────────────────────────────────
     原实现高度幅度只有 1.4（平原）~7（山墙）世界单位，且噪声频率极低
     （0.08，每 12.5 格才一个周期），而相邻顶点间距 CELL=46。坡度
     dh/(2*CELL) 被稀释到几乎为 0：实测所有材质法线倾斜 < 2°，等价于法线恒为
     (0,0,1) → 光照处处相同、画面"平"得像一张纸（顶点微位移也救不回来）。

     决定坡度的是【幅度 × 频率 / 格距】，光放大幅度不够（上面实测：freq=0.045
     时 amp 拉到 120 也只有中位 3.14°）。故按地貌生成的通行做法（Houdini
     HeightField / 分形地形的 element size + amplitude 两级控制）重新标定：
       ① 噪声频率提到 0.18（约每 5.5 格一个周期）——接近"一格一坡"的采样上限，
          再高会因顶点间距过大而产生走样（aliasing），故取此值。
       ② 幅度按材质分级：平原 110、山地 210、山墙 260 世界单位。
       ③ 宏观(0.06)×0.55 + 中频(0.18)×0.45 + 细节(0.42)×0.12 三级叠加：
          大走向 + 地表起伏 + 细碎质感，避免单一低频显得光滑。
       ④ 各倍频均为连续噪声，相邻格连续，不破坏区块索引连续性。
     实测法线倾斜中位：平原 ≈10°、山地 ≈19°、山墙 ≈23°（p90 分别 ≈18°/36°/41°），
     落在自然地貌观感区间。 */
  function tileH(tx, ty) {
    var i = ty * W + tx;
    var f = flags[i];
    var rid = warpBiome(tx, ty);
    var base = BIOME_BASE[rid] || 0.6;
    // 三级地貌：宏观走向 + 中频起伏 + 高频细节（系数和 ≈ 1.12，控制在可接受范围）
    // ⚠ 只可用 buildWorld 作用域内已有的 noise()/fbm()。noise2 是 WGSL 侧的
    //   着色器函数，JS 里不存在（曾误用导致 Worker 抛 ReferenceError →
    //   _world=undefined → 渲染器初始化失败并降级 melonJS）。
    var macro = fbm(tx * 0.060, ty * 0.060);                    // 大尺度走向
    var mid = fbm(tx * 0.180 + 11.3, ty * 0.180 + 5.7);          // 中尺度起伏
    var fine = noise(tx * 0.420 + 3.1, ty * 0.420 + 8.9);        // 高频细节
    var land = macro * 0.55 + mid * 0.45 + fine * 0.12;
    if (f & F_WATER) return (f & F_WALL) ? 34 + rng(tx, ty, 50) * 12 : -1.6;
    // ⚠ 基准归零：相机 eye.z = 44、正交投影 near/far = ∓600，地形必须留在相机
    //   附近的小范围内。故各材质的高度都围绕 0 展开（山峰正、谷地负），
    //   而不是整体抬高——否则山峰会穿出平截头体或把玩家埋进地里。
    if (f & F_WALL) return land * 505 - 252;                     // 山墙：高耸连绵 ±252
    if (f & F_MOUNT) return land * 408 - 204;                    // 山地：明显隆起 ±204
    // 平原/丘陵：叠加 ±214 的起伏（基准 0）
    return (base * 115 - 69) + land * 214 - 107;
  }
  function tileC(tx, ty) {
    var i = ty * W + tx;
    var f = flags[i];
    if (f & F_ROAD) return C_ROAD;
    if (f & F_MOUNT) return C_ROCK;
    if (f & F_WALL) return C_ROCK;
    if (f & F_WATER) return C_WATER;
    var c = BIOME_C[warpBiome(tx, ty)];
    // 轻微噪声色偏，让地面色块更自然、减少“贴纸感”
    if (c) {
      var sh = (noise(tx * 0.05 + 2.2, ty * 0.05 + 9.4) - 0.5) * 18;
      return [Math.max(0, Math.min(255, Math.round(c[0] + sh))),
              Math.max(0, Math.min(255, Math.round(c[1] + sh))),
              Math.max(0, Math.min(255, Math.round(c[2] + sh)))];
    }
    return [0x5f, 0x9e, 0x4e];
  }

  // —— 高度场顶点 ——
  var tH = [], tC = [];
  for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) {
    tH.push(tileH(x, y)); tC.push(tileC(x, y));
  }
  var idxAt = function (x, y) { return y * W + x; };
  var vertPos = new Float32Array(NV * 10);
  var vi = 0;
  for (var j = 0; j <= H; j++) for (var i = 0; i <= W; i++) {
    // 顶点高度/颜色 = 邻近 4 格平均（越界回绕 → 视为墙）
    var xs = [Math.max(0, i - 1), Math.min(W - 1, i)];
    var ys = [Math.max(0, j - 1), Math.min(H - 1, j)];
    var hAcc = 0, cr = 0, cg = 0, cb = 0, n = 0;
    xs.forEach(function (ax) { ys.forEach(function (ay) {
      var k = idxAt(ax, ay);
      hAcc += tH[k]; var c = tC[k]; cr += c[0]; cg += c[1]; cb += c[2]; n++;
    }); });
    var hh = hAcc / n, r = cr / n / 255, g = cg / n / 255, b = cb / n / 255;
    vertPos[vi++] = i * CELL; vertPos[vi++] = j * CELL; vertPos[vi++] = hh;
    vertPos[vi++] = r; vertPos[vi++] = g; vertPos[vi++] = b; vertPos[vi++] = 1;
    vertPos[vi++] = 0; vertPos[vi++] = 0; vertPos[vi++] = 0;
  }
  // 计算法线
  function gv(i, j) { return j * (W + 1) + i; }
  var hAt = function (i, j) { return vertPos[gv(i, j) * 10 + 2]; };
  for (var j2 = 0; j2 <= H; j2++) for (var i2 = 0; i2 <= W; i2++) {
    var il = Math.max(0, i2 - 1), ir = Math.min(W, i2 + 1);
    var jd = Math.max(0, j2 - 1), ju = Math.min(H, j2 + 1);
    var dx = (hAt(ir, j2) - hAt(il, j2)) / (2 * CELL);
    var dy = (hAt(i2, ju) - hAt(i2, jd)) / (2 * CELL);
    var nx = -dx, ny = -dy, nz = 1;
    var l = Math.hypot(nx, ny, nz) || 1;
    var o = gv(i2, j2) * 10;
    vertPos[o + 7] = nx / l; vertPos[o + 8] = ny / l; vertPos[o + 9] = nz / l;
  }
  // —— 索引 + 分块 ——
  // 【分块必须"连续"】drawIndexed(count, 1, firstIndex, 0, 0) 只接受一段连续索引区间，
  // 因此同一 chunk 的索引在 terrIdx 里必须物理连续。若按 tile 的 row-major 顺序直接写入，
  // 一个 16×16 的 chunk 会散布在 16 个不同行上（相邻 chunk 的索引插在中间），
  // 导致每个 chunk 声称的 [first, first+count) 区间里 81% 是别的 chunk 的三角形 →
  // 整片地形三角形错乱退化，画面上只剩零星条纹（"大地图不显示"的根因）。
  // 解法：以 chunk 为外层循环，把每个 chunk 的 16×16 格一次性写完，
  // 这样 finalize 一个 chunk 后再开下一个，索引天然连续，且不增加任何显存。
  var terrIdx = new Uint32Array(W * H * 6);
  var terrChunks = [];
  var tii = 0;
  var CW = Math.ceil(W / CHUNK), CH = Math.ceil(H / CHUNK);
  for (var cy2 = 0; cy2 < CH; cy2++) for (var cx2 = 0; cx2 < CW; cx2++) {
    var tx0 = cx2 * CHUNK, ty0 = cy2 * CHUNK;
    var tx1 = Math.min(tx0 + CHUNK, W), ty1 = Math.min(ty0 + CHUNK, H);
    if (tx0 >= tx1 || ty0 >= ty1) continue;
    var tfirst = tii;                      // 本 chunk 的连续索引区间起点
    for (var ty2 = ty0; ty2 < ty1; ty2++) for (var tx2 = tx0; tx2 < tx1; tx2++) {
      var a = gv(tx2, ty2), bb = gv(tx2 + 1, ty2), c2 = gv(tx2, ty2 + 1), d2 = gv(tx2 + 1, ty2 + 1);
      terrIdx[tii++] = a; terrIdx[tii++] = bb; terrIdx[tii++] = c2;
      terrIdx[tii++] = bb; terrIdx[tii++] = d2; terrIdx[tii++] = c2;
    }
    terrChunks.push({ cx: cx2, cy: cy2, first: tfirst, count: tii - tfirst });
  }
  // —— 实例化装饰（树/石/雪/水/植被/灵粒） ——
  // 每对象类型 → 实例数组；每实例 20 floats:
  // [pos3][scale3][yaw,flag][colT.rgb,alpha][colF.rgb,alpha][colR.rgb,alpha]
  // 【实例分块同样必须连续】装饰走 drawIndexed(cubeIdxCount, count, 0, 0, firstInstance)，
  // 依赖"同一 chunk 的实例在实例缓冲里物理连续"。按 tile row-major 顺序 push 同样会把
  // 一个 chunk 的实例打散到 16 行上 → 实例区间错位、绘制出别的 chunk 的树石
  // （表现与地形同源：大地图上装饰错乱/缺失）。
  // 解法与地形一致：以 chunk 为外层循环，逐 chunk 写完再进下一个；
  // 归属区间由调用方 emitChunk 记录，pushInst 不再自行维护 chunkMap。
  function pushInst(arr, cx, cy, pos, scale, yaw, flag, ct, cf, cr, alpha) {
    var b = arr.base;
    for (var q = 0; q < pos.length; q++) b.push(pos[q]);
    for (q = 0; q < scale.length; q++) b.push(scale[q]);
    b.push(yaw, flag);
    b.push(ct[0], ct[1], ct[2], alpha, cf[0], cf[1], cf[2], alpha, cr[0], cr[1], cr[2], alpha);
    arr.n++;
  }
  function obj(color) { return [color[0] / 255, color[1] / 255, color[2] / 255]; }
  var deco = { base: [], n: 0, chunks: [] };

  // 单个 chunk 的实例发射（区间连续），供外层 chunk 循环调用
  function emitChunk(x0, y0, x1, y1) {
    var first = deco.n;
    for (var y3 = y0; y3 < y1; y3++) for (var x3 = x0; x3 < x1; x3++) {
      var idx = y3 * W + x3;
      var f = flags[idx], rid = biome[idx], cx3 = (x3 / CHUNK) | 0, cy3 = (y3 / CHUNK) | 0;
      var gc = (x3 + 0.5) * CELL, gy = (y3 + 0.5) * CELL;
      var groundH = tH[idxAt(x3, y3)];
      var r = rng;

      if (f & F_WATER) {
        // 水面（薄片）+ 微光灵粒
        pushInst(deco, cx3, cy3, [gc, gy, groundH + 0.15], [CELL, CELL, 0.4], 0, 1, obj(C_WATER.map(function (v) { return v * 0.9; })), obj(C_WATER), obj(C_WATER), 0.82);
        if (r(x3, y3, 60) < 0.05) addMote(gc + (r(x3, y3, 61) - 0.5) * CELL * 0.6, gy + (r(x3, y3, 62) - 0.5) * CELL * 0.6, groundH + 2);
        continue;
      }
      if (f & F_WALL) {
        // 群山外障：密布巨岩，形成不可逾越的山墙
        if (r(x3, y3, 5) > 0.82) continue;
        var wd = CELL * (0.9 + r(x3, y3, 6) * 1.1);
        pushInst(deco, cx3, cy3, [gc, gy, groundH], [CELL * 0.85, CELL * 0.85, wd], 0, 0, obj([0x6f, 0x6a, 0x62]), obj([0x57, 0x52, 0x49]), obj([0x3e, 0x3a, 0x34]), 1);
        continue;
      }
      if (f & F_MOUNT) {
        if (r(x3, y3, 5) > 0.55) continue;
        var mh = CELL * (0.6 + r(x3, y3, 6) * 0.9);
        pushInst(deco, cx3, cy3, [gc, gy, groundH], [CELL * 0.62, CELL * 0.62, mh], 0, 0, obj([0x8f, 0x8a, 0x82]), obj([0x72, 0x6d, 0x65]), obj([0x57, 0x52, 0x4a]), 1);
        continue;
      }
      if (f & F_SOLID) continue;
      var kinds = BIOME_KINDS[rid];
      if (!kinds) continue;
      var dens = BIOME_DENSITY[rid] || 0.05;
      if (r(x3, y3, 1) > dens) {
        // 稀疏灵粒/野花点缀，让大地更有生气
        if (r(x3, y3, 63) < 0.02) addMote(gc, gy, groundH + 2);
        continue;
      }
      var kind = kinds[Math.floor(r(x3, y3, 2) * kinds.length)];
      var s = 0.6 + r(x3, y3, 3) * 0.7;
      place(gc, gy, groundH, kind, s, cx3, cy3, r);
      // 设计化的景观过渡：紧邻不同生物群系的边界格，以噪声概率补一棵矮灌/一颗小石，
      // 把直线分界弱化为有层次的植物过渡带（对应旧 iso 路径的 buildBoundaries）。
      var wr = warpBiome(x3, y3);
      if (wr !== rid && r(x3, y3, 31) < 0.16) {
        var edgeK = r(x3, y3, 32) < 0.5 ? 'grass' : 'rock';
        place(gc + (r(x3, y3, 33) - 0.5) * CELL * 0.4, gy + (r(x3, y3, 34) - 0.5) * CELL * 0.4,
              groundH, edgeK, 0.55 + r(x3, y3, 35) * 0.4, cx3, cy3, r);
      }
    }
    var cnt = deco.n - first;
    if (cnt > 0) deco.chunks.push({ cx: (x0 / CHUNK) | 0, cy: (y0 / CHUNK) | 0, first: first, count: cnt });
  }
  // chunk-major 外层循环：保证每个 chunk 的实例物理连续
  for (var cyD = 0; cyD < CH; cyD++) for (var cxD = 0; cxD < CW; cxD++) {
    var dx0 = cxD * CHUNK, dy0 = cyD * CHUNK;
    var dx1 = Math.min(dx0 + CHUNK, W), dy1 = Math.min(dy0 + CHUNK, H);
    if (dx0 >= dx1 || dy0 >= dy1) continue;
    emitChunk(dx0, dy0, dx1, dy1);
  }
  function addMote(x, y, z) {
    var s = CELL * 0.05;
    pushInst(deco, (x / CELL / CHUNK) | 0, (y / CELL / CHUNK) | 0, [x, y, z], [s, s, s], 0, 2, [0xff, 0xe6, 0x8a].map(function (v) { return v / 255; }), [0xff, 0xdd, 0x66].map(function (v) { return v / 255; }), [0xff, 0xcc, 0x44].map(function (v) { return v / 255; }), 0.9);
  }
  function place(gcx, gcy, gz, kind, scale, cx, cy, R) {
    var c = function (hc) { return [hc[0] / 255, hc[1] / 255, hc[2] / 255]; };
    var k;
    switch (kind) {
      case 'tree': case 'pine': {
        var trunkW = CELL * 0.22;
        pushInst(deco, cx, cy, [gcx, gcy, gz], [CELL * 0.22, CELL * 0.22, CELL * 0.30], R(gcx | 0, gcy | 0, 70), 0, c([0x6b, 0x4a, 0x2f]), c([0x5a, 0x3d, 0x26]), c([0x49, 0x32, 0x1e]), 1);
        var leaf = (kind === 'tree' ? [0x4a, 0x8f, 0x3a] : [0x2f, 0x6b, 0x3a]);
        pushInst(deco, cx, cy, [gcx, gcy, gz + CELL * 0.3], [CELL * 0.6 * scale, CELL * 0.6 * scale, CELL * 0.6 * scale], R(gcx | 0, gcy | 0, 71), 0, c(leaf), c(leaf.map(function (v) { return v * 0.8; })), c(leaf.map(function (v) { return v * 0.62; })), 1);
        break; }
      case 'rock': case 'ruin': {
        var rw = CELL * 0.5 * scale, rd = CELL * 0.4 * scale;
        var tc = kind === 'ruin' ? [0x7b, 0x6f, 0x64] : [0x9a, 0x94, 0x8c];
        pushInst(deco, cx, cy, [gcx, gcy, gz], [rw, rw, rd], R(gcx | 0, gcy | 0, 72), 0, c(tc), c([0x5f, 0x55, 0x4c]), c([0x61, 0x5c, 0x55]), 1);
        break; }
      case 'snow': {
        var sw = CELL * 0.5 * scale, sd = CELL * 0.38 * scale;
        pushInst(deco, cx, cy, [gcx, gcy, gz], [sw, sw, sd], R(gcx | 0, gcy | 0, 73), 0, c([0xff, 0xff, 0xff]), c([0xdf, 0xe9, 0xf2]), c([0xb8, 0xcc, 0xdc]), 1);
        break; }
      case 'grass':
        pushInst(deco, cx, cy, [gcx, gcy, gz], [CELL * 0.5 * scale, CELL * 0.5 * scale, CELL * 0.18 * scale], R(gcx | 0, gcy | 0, 74), 2, c([0x7f, 0xc0, 0x4e]), c([0x66, 0xa5, 0x3e]), c([0x4f, 0x83, 0x30]), 0.95);
        break;
      case 'dead':
        pushInst(deco, cx, cy, [gcx, gcy, gz], [CELL * 0.5 * scale, CELL * 0.5 * scale, CELL * 0.14 * scale], R(gcx | 0, gcy | 0, 75), 2, c([0x9b, 0x8b, 0x5a]), c([0x7d, 0x70, 0x47]), c([0x5f, 0x56, 0x36]), 0.95);
        break;
      case 'cacti':
        pushInst(deco, cx, cy, [gcx, gcy, gz], [CELL * 0.26, CELL * 0.26, CELL * 0.9 * scale], R(gcx | 0, gcy | 0, 76), 0, c([0x5a, 0x90, 0x40]), c([0x4a, 0x7a, 0x35]), c([0x3a, 0x64, 0x29]), 1);
        pushInst(deco, cx, cy, [gcx + CELL * 0.18, gcy - CELL * 0.1, gz + CELL * 0.5 * scale], [CELL * 0.34, CELL * 0.2, CELL * 0.16 * scale], R(gcx | 0, gcy | 0, 77), 0, c([0x5a, 0x90, 0x40]), c([0x4a, 0x7a, 0x35]), c([0x3a, 0x64, 0x29]), 1);
        break;
      case 'dune':
        pushInst(deco, cx, cy, [gcx, gcy, gz], [CELL * 0.55 * scale, CELL * 0.55 * scale, CELL * 0.22 * scale], R(gcx | 0, gcy | 0, 78), 0, c([0xe0, 0xbb, 0x6a]), c([0xc8, 0xa0, 0x52]), c([0xa9, 0x8a, 0x40]), 1);
        break;
    }
  }

  // 收紧数组
  var inst = new Float32Array(deco.n * 20);
  for (var i3 = 0; i3 < deco.n * 20; i3++) inst[i3] = deco.base[i3];

  function chunksToArray(list) {
    var a = new Uint32Array(list.length * 4);
    for (var i4 = 0; i4 < list.length; i4++) { var c = list[i4]; a[i4 * 4] = c.cx; a[i4 * 4 + 1] = c.cy; a[i4 * 4 + 2] = c.first; a[i4 * 4 + 3] = c.count; }
    return a;
  }

  // 补齐 10-float 步进的 mvp 之外不需要的 padding —— 无。
  return {
    terrainVerts: vertPos, terrainIdx: terrIdx,
    terrainChunks: chunksToArray(terrChunks),
    decoInst: inst, decoChunks: chunksToArray(deco.chunks),
    terrainVertCount: NV, terrainIdxCount: W * H * 6,
    decoCount: deco.n, terrainChunkCount: terrChunks.length, decoChunkCount: deco.chunks.length
  };
}
// buildWorld 引用的全局查找表 —— 作为模块级常量，注入 Worker 时一并带上
var BIOME_BASE = {}; var BIOME_C = {}; var BIOME_ID = {};
(function () {
  for (var i = 0; i < BIOME_LIST.length; i++) {
    var bm = BIOME_LIST[i];
    BIOME_C[i] = bm.c; BIOME_BASE[i] = bm.h;
  }
  BIOME_ID = {};
  for (var jj = 0; jj < BIOME_LIST.length; jj++) BIOME_ID[BIOME_LIST[jj].id] = jj;
})();

/* ═══════════════════════ OverworldGame（WebGPU） ═══════════════════════ */
var OverworldGame = {

  getSamsara: function () { return this.samsara; },
  getGames: function () { return this.games; },
  syncSamsara: function (state) {
    if (!state) return;
    this.samsara = state;
    this._syncBadges((state.realm_progress || {}));
    if (UI) UI.refreshHUD();
  },
  refreshSamsara: function () {
    var self = this;
    return fetch('/samsara/api/state').then(function (r) { return r.json(); })
      .then(function (s) { self.syncSamsara(s); return s; }).catch(function () { return null; });
  },
  refreshFromUI: function () { return this.refreshSamsara(); },
  startPolling: function () { var self = this; this.pollTimer = setInterval(function () { self.refreshSamsara(); }, 8000); },
  stopPolling: function () { if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = null; } },
  isOpen: function () { return UI ? UI.isModalOpen() : false; },

  /* ── 渲染暂停开关 ──
     供全屏遮挡场景（将来若有）真正停掉每帧重绘，而不只是屏蔽输入。
     玩法教程已改为独立标签页，当前无调用方，保留为渲染器的通用能力。 */
  setRenderPaused: function (v) { this._renderPaused = !!v; },

  /* ── 世界几何引导 ── */
  bootstrapGeometry: function (ow) {
    var self = this;
    this.W = ow.world.width; this.H = ow.world.height; this.ow = ow;
    this.regionByTile = [];
    for (var i = 0; i < this.H; i++) this.regionByTile.push(new Array(this.W));
    (ow.regions || []).forEach(function (rg) {
      var rects = rg.rects && rg.rects.length ? rg.rects : (rg.rect ? [rg.rect] : []);
      for (var k = 0; k < rects.length; k++) {
        var r = rects[k];
        var x0 = r[0], x1 = r[2], y0 = r[1], y1 = r[3];
        for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
          if (x >= 0 && x < self.W && y >= 0 && y < self.H) self.regionByTile[y][x] = rg;
      }
    });

    // 顶点属性 + flags/biome 码
    // 障碍机制已移除：大地图不再有任何阻挡玩家通行的障碍物；
    // 仅最外 6 格 F.WALL 边框（叠加越界判定）用于阻止玩家走出地图外。
    this.flags = new Int32Array(this.W * this.H);
    this.biome = new Int32Array(this.W * this.H);
    var fl = this.flags, bm = this.biome;

    // —— 从 baked.terrain（单一真相源）推导地形 flags ——
    // terrain 编码：0=DEEP 1=SHALLOW 2=BEACH 3=GRASS 4=HILL 5=MOUNT 6=ALPINE 7=LAVA 8=WALL 9=ICE
    var tStr = (ow.baked && ow.baked.terrain) || "";
    if (tStr.length === this.W * this.H) {
      for (var ty0 = 0; ty0 < this.H; ty0++) for (var tx0 = 0; tx0 < this.W; tx0++) {
        var tc = tStr.charCodeAt(tx0 + ty0 * this.W) - 48;
        if (tc === 0 || tc === 1 || tc === 7 || tc === 9) fl[ty0 * this.W + tx0] |= 1; // DEEP/SHALLOW/LAVA/ICE → 水面
        else if (tc === 5 || tc === 6) fl[ty0 * this.W + tx0] |= 4;                     // MOUNT/ALPINE
        else if (tc === 8) fl[ty0 * this.W + tx0] |= 8;                                 // WALL（边框）
      }
    }

    // —— 道路（新 schema 仍保留 ow.roads）——
    (ow.roads || []).forEach(function (r) {
      if (r.rect) for (var i2 = r.rect[0]; i2 <= r.rect[2]; i2++) for (var j2 = r.rect[1]; j2 <= r.rect[3]; j2++)
          if (i2 >= 0 && i2 < self.W && j2 >= 0 && j2 < self.H) fl[j2 * self.W + i2] |= 2;
      else if (r.x !== undefined) for (var j3 = r.y0; j3 <= r.y1; j3++) if (j3 >= 0 && j3 < self.H && r.x >= 0 && r.x < self.W) fl[j3 * self.W + r.x] |= 2;
      else if (r.y !== undefined) for (var i3 = r.x0; i3 <= r.x1; i3++) if (i3 >= 0 && i3 < self.W && r.y >= 0 && r.y < self.H) fl[r.y * self.W + i3] |= 2;
    });

    // —— 地图外边界（最外 6 格 WALL）+ biome（区域归属，优先 baked.region 字符码 0-8 = BIOME_LIST 顺序）——
    var WALL = 6;
    var rStr2 = (ow.baked && ow.baked.region) || "";
    for (var y5 = 0; y5 < this.H; y5++) for (var x5 = 0; x5 < this.W; x5++) {
      if (x5 < WALL || x5 >= this.W - WALL || y5 < WALL || y5 >= this.H - WALL) fl[y5 * this.W + x5] |= 8;
      var bcode = 4; // 默认 c_plain
      if (rStr2.length === self.W * self.H) bcode = rStr2.charCodeAt(x5 + y5 * self.W) - 48;
      else { var rg = self.regionByTile[y5][x5]; if (rg && BIOME_ID[rg.id] !== undefined) bcode = BIOME_ID[rg.id]; }
      bm[y5 * this.W + x5] = (bcode >= 0 && bcode <= 8) ? bcode : 4;
    }
    this.playerPos = { x: ((this.ow.player && this.ow.player.initial) || { x: 58, y: 46 }).x + 0.5, y: ((this.ow.player && this.ow.player.initial) || { x: 58, y: 46 }).y + 0.5 };
    this.playerSpeed = ((this.ow.player && this.ow.player.speed) || 160) / 5;

    // 启动资源构建（首推 Worker，多线程；失败主线程兜底）
    this._prepare();

    // 同步构建可立即完成的部分（小地图）
    if (UI) UI.setLoadingProgress(38);
  },

  _prepare: function () {
    var self = this;
    this._ready = this._buildResources().then(function () {
      return self._initGPU();
    }).catch(function (err) {
      console.error('[WebGPU] init failed:', err);
      // 1) 无条件移除加载遮罩：UI 未初始化时直接操作 DOM，避免 loading-overlay 盖住按钮
      try {
        var ov = document.getElementById('loading-overlay');
        if (ov) ov.classList.add('hidden');
      } catch (e) { /* ignore */ }
      if (UI && typeof UI.removeLoading === 'function') UI.removeLoading();
      // 2) 渲染分发起存在时优先走其统一诊断/升级路径，能产出更明确的 WebGPU 失败原因。
      if (typeof window.__owFallbackToIso === 'function') {
        try { window.__owFallbackToIso(err); } catch (e) { console.error(e); }
        return null;
      }
      if (UI && typeof UI.showError === 'function') {
        UI.showError('未能启动次世代渲染，请刷新重试，或确认浏览器开启了 WebGPU。');
      } else {
        try {
          var uia = document.getElementById('renderer-badge');
          if (uia) uia.textContent = 'WebGPU 初始化失败：' + (err && err.message ? err.message : err);
        } catch (e) { /* ignore */ }
      }
      throw err;
    });
    return this._ready;
  },

  _buildResources: function () {
    var self = this;
    var spec = { W: this.W, H: this.H, CELL: CELL, flags: this.flags, biome: this.biome };
    var runMain = function () { self._world = buildWorld(spec); return Promise.resolve(); };
    if (!(typeof Worker === 'function')) return runMain();
    return new Promise(function (resolve) {
      // 将 buildWorld 及其依赖表内联注入 Blob Worker
      var src = '(' + buildWorld.toString() + ')';
      var tables = 'var BIOME_BASE=' + JSON.stringify(BIOME_BASE) + ';' +
                   'var BIOME_C=' + JSON.stringify(BIOME_C) + ';' +
                   'var BIOME_ID=' + JSON.stringify(BIOME_ID) + ';' +
                   'var BIOME_KINDS=' + JSON.stringify(BIOME_KINDS) + ';' +
                   'var BIOME_DENSITY=' + JSON.stringify(BIOME_DENSITY) + ';' +
                   'var C_ROAD=' + JSON.stringify(C_ROAD) + ';' +
                   'var C_WATER=' + JSON.stringify(C_WATER) + ';' +
                   'var C_ROCK=' + JSON.stringify(C_ROCK) + ';';
      var workerSrc = tables + 'self.onmessage=function(e){var build=' + src + ';var r=build(e.data);self.postMessage(r,[r.terrainVerts.buffer,r.terrainIdx.buffer,r.terrainChunks.buffer,r.decoInst.buffer,r.decoChunks.buffer]);};';
      var w;
      try {
        var url = URL.createObjectURL(new Blob([workerSrc], { type: 'application/javascript' }));
        w = new Worker(url);
      } catch (e) { w = null; }
      if (!w) { resolve(); return runMain().finally(function () {}); }
      // Worker 成功：结果需含 terrainVerts 才认为有效，否则退回主线程重算。
      // （曾出现 Worker 内 ReferenceError → postMessage 收到 undefined →
      //   _world 为 undefined 却照常 resolve，下游 _uploadWorld 读 terrainVerts
      //   抛错、整个 WebGPU 通道初始化失败并降级 melonJS。此处加显式校验。）
      var validResult = function (r) {
        return !!(r && r.terrainVerts && r.terrainIdx && r.terrainChunks && r.decoInst && r.decoChunks);
      };
      w.onmessage = function (ev) {
        if (validResult(ev.data)) {
          self._world = ev.data;
          resolve();
        } else {
          console.warn('[WebGPU] Worker 返回结果不完整，回退主线程构建地形');
          runMain().then(resolve);
        }
        try { w.terminate(); } catch (e) { }
      };
      // Worker 报错：必须【等待】主线程构建完成后再 resolve，否则 _world 尚为
      // undefined 就被下游消费（原实现 runMain().finally() 未 await，是竞态）。
      w.onerror = function (e) {
        try { console.warn('[WebGPU] Worker 构建失败，回退主线程：', (e && e.message) || e); } catch (e2) { }
        try { w.terminate(); } catch (e2) { }
        runMain().then(resolve);
      };
      // 转换 spec → 可结构化克隆的传输（用拷贝，避免 detach 掉 this.flags / this.biome）
      var flagsCopy = spec.flags.slice(), biomeCopy = spec.biome.slice();
      w.postMessage({ W: spec.W, H: spec.H, CELL: CELL, flags: flagsCopy, biome: biomeCopy }, [flagsCopy.buffer, biomeCopy.buffer]);
    });
  }
};

/* ═══════════════════════════════════════════════════════════════
   ══════════  下列方法通过 `OverworldGame.方法 = fn` 追加  ══════════
   （GPU 初始化 / 相机 / 渲染循环 / DOM 覆盖层 / 玩家 / 小地图 / 交互）
   ═══════════════════════════════════════════════════════════════ */

/* ── 基础立方体（供实例化装饰/水面使用）── */
function makeCube() {
  var verts = [], idx = [], faces = [
    [0, 0, 1, 0, 0, 1], [0, 0, -1, 0, 0, -1], [1, 0, 0, 1, 0, 0], [-1, 0, 0, -1, 0, 0], [0, 1, 0, 0, 1, 0], [0, -1, 0, 0, -1, 0]
  ];
  var u = [[-0.5, -0.5], [0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]];
  faces.forEach(function (f) {
    var n = [f[3], f[4], f[5]];
    var dir = [f[0], f[1], f[2]];
    var base = verts.length / 6;
    for (var i = 0; i < 4; i++) {
      // 沿面轴铺开 u/v，法线方向 dir 决定 ±0.5 偏移，构成单位立方体角点
      var e1 = [0, 0, 0], e2 = [0, 0, 0];
      if (dir[0] !== 0) { e1 = [0, 1, 0]; e2 = [0, 0, 1]; }
      else if (dir[1] !== 0) { e1 = [1, 0, 0]; e2 = [0, 0, 1]; }
      else { e1 = [1, 0, 0]; e2 = [0, 1, 0]; }
      var u0 = u[i][0] > 0 ? 0.5 : -0.5, u1 = u[i][1] > 0 ? 0.5 : -0.5;
      var rx = dir[0] !== 0 ? dir[0] * 0.5 : e1[0] * u0 + e2[0] * u1;
      var ry = dir[1] !== 0 ? dir[1] * 0.5 : e1[1] * u0 + e2[1] * u1;
      var rz = dir[2] !== 0 ? dir[2] * 0.5 : e1[2] * u0 + e2[2] * u1;
      verts.push(rx, ry, rz, n[0], n[1], n[2]);
    }
    idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
  });
  return { verts: new Float32Array(verts), idx: new Uint32Array(idx), indexCount: idx.length, vertexCount: verts.length / 6 };
}

/* ── 相机 ── */
OverworldGame.setupCamera = function () {
  if (this.cam) return; // 幂等：start() 可能重复调用，避免重置相机与重复挂 resize 监听
  var c = this.cam = { zoom: 1.6, vw: window.innerWidth, vh: window.innerHeight };  var vp = document.getElementById('iso-viewport');
  if (vp) { c.vw = vp.clientWidth; c.vh = vp.clientHeight; }
  var self = this;
  this._onResize = (function () {
    var cc = this.cam;
    cc.vw = document.getElementById('iso-viewport').clientWidth;
    cc.vh = document.getElementById('iso-viewport').clientHeight;
    this._ensureSize();
    this.applyCamera(true);
  }).bind(this);
  window.addEventListener('resize', this._onResize);
  if (UI) UI.setLoadingProgress(45);
};

// 以玩家为世界中心计算 view/proj 矩阵
OverworldGame._updateView = function () {
  var c = this.cam, z = c.zoom;
  var tx = this.playerPos.x * CELL, ty = this.playerPos.y * CELL;
  // 正向俯视（与图标/小地图同向）：相机垂直俯视，不再斜 45°
  //   · 世界 +x（东）→ 屏幕右
  //   · 世界 +y（南，即小地图下方）→ 屏幕下
  // 这样大陆地形与世界图标、小地图方向完全一致，玩家不再看到"斜向大陆"。
  var eye = [tx, ty, 44 + (z - 1) * 6];
  var target = [tx, ty, 0];
  var view = m4LookAt(eye, target, [0, 1, 0]);
  var hl = c.vw / (2 * z), vt = c.vh / (2 * z);
  // b/t 互换实现 y 翻转：+y（南）落到屏幕下方，与俯视小地图轴向一致
  var proj = m4Ortho(-hl, hl, vt, -vt, -600, 600);
  this._view = view; this._proj = proj;
  this._mvp = m4Mul(proj, view);
  this._invMvp = m4Invert(this._mvp);
  this._camPos = eye;
};

// World → 屏幕像素（与 GPU 同用一套 mvp，保证 DOM 覆盖层严丝合缝）
OverworldGame.project = function (wx, wy, wz) {
  if (!this._mvp) return { x: 0, y: 0, on: false, scale: this.cam ? this.cam.zoom : 1 };
  var clip = m4Transform(this._mvp, [wx, wy, wz]);
  var c = this.cam;
  return {
    x: (clip[0] * 0.5 + 0.5) * c.vw,
    y: (1 - (clip[1] * 0.5 + 0.5)) * c.vh,
    scale: c.zoom,
    on: clip[2] >= -1 && clip[2] <= 1
  };
};
OverworldGame._project = function () { return { x: 0, y: 0 }; }; // 兼容旧调用，实际用 project()

/* ── GPU 初始化 ── */
OverworldGame._initGPU = function () {
  var self = this;
  // ── 详细诊断日志（便于定位黑屏：适配器 / 特性 / 设备 / 管线）──
  function diag(msg) {
    try { console.log('[WebGPU] ' + msg); } catch (e) {}
  }
  if (!navigator.gpu) return Promise.reject(new Error('no WebGPU'));
  diag('navigator.gpu 可用，开始 requestAdapter');
  // 相机是 _ensureSize / _updateFrame 的前置依赖。正常时序由 start() 调用 setupCamera()，
  // 但 start() 在 /api/overworld/config 返回后才执行，而 _initGPU 由 _prepare() 在 bootstrapGeometry()
  // 里立即触发。配置接口一旦变慢，_initGPU 就会先于 setupCamera 完成 → cam 为 undefined →
  // _ensureSize 抛 TypeError → WebGPU 初始化失败（黑屏/白屏的根因之一）。此处补上兜底。
  if (!this.cam) this.setupCamera();
  // 复用分发器设备检测阶段已取得的 adapter：Chrome 对同时存活的 GPUAdapter 有数量限制，
  // 并发/二次 requestAdapter 会让后续 requestDevice() 一直挂起（本渲染器黑屏的根因）。
  var preAdapter = window.__owDetectedAdapter || null;
  window.__owDetectedAdapter = null; // 取用一次即清，避免跨次启动残留
  var adapterP = preAdapter ? Promise.resolve(preAdapter)
                            : navigator.gpu.requestAdapter();
  if (preAdapter) diag('复用分发器探测到的适配器（跳过二次 requestAdapter）');
  return adapterP.then(function (adapter) {
    if (!adapter) throw new Error('no adapter');
    try {
      diag('适配器: ' + (adapter.info ? adapter.info.description || adapter.info.architecture || '' : '') + ' | vendor=' + (adapter.info ? adapter.info.vendor : '?'));
      var feats = [];
      try { feats = Array.from(adapter.features || []); } catch (e) {}
      diag('适配器特性(' + feats.length + '): ' + feats.join(','));
      self._float32Renderable = !!(adapter.features && typeof adapter.features.has === 'function' && adapter.features.has('float32-renderable'));
      diag('float32-renderable 特性可用 = ' + self._float32Renderable);
    } catch (e) { diag('读取适配器信息失败: ' + e.message); }
    // 关键：WebGPU 特性必须在 requestDevice 时显式声明才会启用，否则 device.features 里
    // 永远没有对应项（只读 adapter.features 会造成“看起来支持、实际未启用”的错位）。
    // 位置缓冲若要用高精度 rgba32float，需要 float32-renderable；此处按适配器能力请求。
    var reqFeat = [];
    try {
      if (self._float32Renderable && adapter.features.has('float32-renderable')) reqFeat.push('float32-renderable');
    } catch (e) {}
    return adapter.requestDevice(reqFeat.length ? { requiredFeatures: reqFeat } : undefined);
  }).then(function (device) {
    self.device = device;
    self.presentFormat = navigator.gpu.getPreferredCanvasFormat();
    // 监听设备丢失与未捕获错误：黑屏时能第一时间在控制台看到明确原因，而非常无声。
    try {
      device.onuncapturederror = function (ev) {
        try { diag('❗ device.onuncapturederror: ' + JSON.stringify(ev && ev.error) + ' — ' + (ev && ev.error && ev.error.message)); } catch (e) {}
      };
      device.lost.then(function (info) {
        try { diag('⚠ device lost: ' + JSON.stringify(info) + (info && info.reason ? ' (reason=' + info.reason + ')' : '')); } catch (e) {}
        // 设备中途丢失（非页面卸载、也非主动销毁）时自动重载，避免界面永久停在黑屏。
        // 用 sessionStorage 限频（每标签页会话至多 2 次），防止极端不稳的设备触发重载风暴。
        try {
          if (document.visibilityState !== undefined && document.visibilityState === 'hidden') return;
          if (self._unloading) return;
          if (info && (info.reason === 'destroyed' || info.reason === 'forced')) return;
          if (self._recoveryScheduled) return;
          var tries = 0;
          try { tries = parseInt(sessionStorage.getItem('chesssage_ow_devicelost') || '0', 10) || 0; } catch (e) {}
          if (tries >= 2) { diag('⚠ 设备多次丢失，已停止自动重载；请刷新页面重试。'); return; }
          try { sessionStorage.setItem('chesssage_ow_devicelost', String(tries + 1)); } catch (e) {}
          self._recoveryScheduled = true;
          diag('↻ 检测到设备丢失，自动重新加载页面以恢复地图……');
          setTimeout(function () { try { location.reload(); } catch (e) {} }, 300);
        } catch (e) {}
      });
    } catch (e) {}
    // 位置缓冲（世界坐标）缺省首选 rgba32float（高精度），但该格式仅当设备具备
    // float32-renderable 特性时才允许作为渲染目标。该特性已在上面 requestDevice({requiredFeatures})
    // 显式请求，故此处读 device.features 即可反映真实启用状态。
    // 注意：rgba32float 的采样类型只有 "unfilterable-float"，绑定它的 bind group layout
    // 必须显式声明 sampleType:'unfilterable-float' + non-filtering 采样器（见 _mkFsLayout），
    // 否则会报 "None of the supported sample types (UnfilterableFloat) ... (Float)" 并导致黑屏。
    // 不具备该特性的设备退化为 rgba16float（可过滤，坐标为有界范围 ≤数千，半精度足敷使用）。
    try {
      self._float32Renderable = !!(device.features && typeof device.features.has === 'function' && device.features.has('float32-renderable'));
      diag('设备 float32-renderable = ' + self._float32Renderable);
    } catch (e) { self._float32Renderable = !!self._float32Renderable; }
    self._posFormat = self._float32Renderable ? 'rgba32float' : 'rgba16float';
    try { diag('位置缓冲格式 = ' + self._posFormat + ' | presentFormat = ' + self.presentFormat); } catch (e) {}
    self._canvas = document.createElement('canvas');
    var vp = document.getElementById('iso-viewport');
    if (!vp) throw new Error('no viewport');
    // 清掉 iso-stage（DOM 版本占位），换上渲染 canvas
    while (vp.firstChild) vp.removeChild(vp.firstChild);
    vp.appendChild(self._canvas);
    self._canvas.style.position = 'absolute'; self._canvas.style.inset = '0'; self._canvas.style.width = '100%'; self._canvas.style.height = '100%';
    // canvas 永远不拦截鼠标：交互走 window 键鼠 + 独立的 DOM 标点层，避免画布盖住 HUD 按钮
    self._canvas.style.pointerEvents = 'none';
    self._ctx = self._canvas.getContext('webgpu');
    self._ctx.configure({ device: device, format: self.presentFormat, alphaMode: 'opaque' });
    self._sampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear' });
    // 非过滤采样器：位置缓冲可能是 rgba32float（仅有 "unfilterable-float" 采样类型，
    // 不支持线性过滤），绑定它的 sampler 必须声明为 non-filtering，否则 createBindGroup
    // 会抛 "None of the supported sample types (UnfilterableFloat) ... match ... (Float)"。
    // 采样位置纹理本就只用 texelFetch / 就近取样，nearest 采样完全等价。
    self._nfSampler = device.createSampler({ magFilter: 'nearest', minFilter: 'nearest' });
    // 用验证队捕获资源/管线/附件的创建期校验错误：若渲染目标格式、纹理视图 aspect、
    // bind group 布局等在真实设备上不合法，这里会弹出明确错误，而非静默黑屏。
    device.pushErrorScope('validation');
    device.pushErrorScope('out-of-memory');
    try {
      self._buildPipelines();
      self._uploadWorld();
      // ⚠ v2.4：在首次构建 RT 前先按已设置的档位算出 _dpr。否则 _ensureSize 会用
      //   undefined→1.0 的 _dpr 把 RT 建满分辨率，使得后续的 renderScale 完全失效
      //   （四档画质渲染分辨率恒等于视口尺寸，ISSUE #9 的根因之一）。
      self._applyRenderScale();
      self._ensureSize();
      self._makeOverlay();
    } catch (e) {
      diag('❗ GPU 初始化（管线/资源）抛出异常: ' + (e && e.message ? e.message : e));
      throw e;
    } finally {
      self._checkScope('init', diag);
    }
    self._canvas.addEventListener('webglcontextlost', function (e) { e.preventDefault(); });
    try { diag('GPU 初始化完成，管线/资源构建成功'); } catch (e) {}
    return device;
  });
};

// 帧级 uniform：52 floats
OverworldGame._checkScope = function (tag, diag) {
  if (!this.device) return;
  try {
    var dev = this.device;
    var safePop = function (type) {
      try {
        var p = dev.popErrorScope(type);
        if (p && typeof p.then === 'function') {
          return p.then(function (err) {
            if (err) { try { (diag || console.log).call(null, '[WebGPU][' + tag + '] ' + type + ' scope error: ' + (err.message || '')); } catch (e) {} }
            return null;
          }).catch(function () { return null; /* scope 未配对时静默（如驱动重置吞掉 scope） */ });
        }
      } catch (e) { /* 同上 */ }
      return null;
    };
    safePop('out-of-memory').then(function () { return safePop('validation'); });
  } catch (e) {}
};
/* ── 智能画质档位（由 overworld-load.js 设备能力检测后调用） ──
   profile: { renderScale, dprCap, ssao, bloom, volumetric }
   - renderScale: 渲染分辨率缩放（0.6~1.0），低配设备降低 RT 尺寸换帧率
   - dprCap:      devicePixelRatio 上限（高分屏低配设备避免 4x 像素负担）
   - ssao/bloom/volumetric: 后处理开关，关闭时跳过对应 pass 并在合成端归零 */
OverworldGame.QUALITY_PRESETS = {
  ultra:    { renderScale: 1.0,  dprCap: 2.0,  ssao: true,  bloom: true,  volumetric: true  },
  high:     { renderScale: 1.0,  dprCap: 1.5,  ssao: true,  bloom: true,  volumetric: true  },
  balanced: { renderScale: 0.75, dprCap: 1.25, ssao: false, bloom: true,  volumetric: false },
  software: { renderScale: 0.6,  dprCap: 1.0,  ssao: false, bloom: false, volumetric: false }
};
OverworldGame.setQuality = function (name) {
  var p = typeof name === 'string' ? this.QUALITY_PRESETS[name] : name;
  if (!p) p = this.QUALITY_PRESETS.high;
  this._quality = p;
  this._qualityName = typeof name === 'string' ? name : 'custom';
  // 位编码写入帧 uniform：bit0=SSAO bit1=Bloom bit2=体积光（与 shaders.js FrameUB 对应）
  this._qualityFlags =
    (p.ssao ? 1 : 0) | (p.bloom ? 2 : 0) | (p.volumetric ? 4 : 0);
  // 运行时热切换：若 GPU 已就绪（_ctx 存在），立即按新档位重算 _dpr 并重建 RT；
  // 启动前（_ctx 尚为空）由 _initGPU / start() 在各自时机统一重建，避免空上下文报错。
  // ⚠ v2.4：原 guard 依赖【从未被赋值】的 this._dprBase，导致此分支永远是死代码——
  //   运行时换档永远不重建 RT。现已改为仅依赖 _ctx 是否存在这一真实条件。
  if (this._ctx) {
    this._applyRenderScale();
    this._rtsW = 0; // 强制 _ensureSize 重建 RT
    this._ensureSize();
  }
  try { console.log('[WebGPU] 画质档位 = ' + (this._qualityName || 'custom') +
    ' (renderScale=' + p.renderScale + ' dprCap=' + p.dprCap +
    ' ssao=' + p.ssao + ' bloom=' + p.bloom + ' volumetric=' + p.volumetric + ')'); } catch (e) {}
  return p;
};
OverworldGame._applyRenderScale = function () {
  var p = this._quality || this.QUALITY_PRESETS.high;
  var dpr = Math.min(p.dprCap, window.devicePixelRatio || 1);
  this._dpr = Math.max(0.5, dpr * p.renderScale);
};

OverworldGame._updateFrame = function () {
  var f = this._frameBuf; if (!f) return;
  f.fill(0);
  f.set(this._mvp, 0);
  f[16] = this._camPos[0]; f[17] = this._camPos[1]; f[18] = this._camPos[2];
  // sunDir (世界指向太阳)，黄昏暖阳略低
  var sd = [-0.5, -0.35, 0.78]; var sl = Math.hypot(sd[0], sd[1], sd[2]);
  f[20] = sd[0] / sl; f[21] = sd[1] / sl; f[22] = sd[2] / sl;
  f[24] = 0.52; f[25] = 0.66; f[26] = 0.82;         // skyColor（天顶半球光）
  // ⚠ groundColor 是"地面反弹光"（半球光的向下分量），真实场景中远弱于天空。
  //   原值 (0.20,0.22,0.24) 偏高，把环境光基准整体抬高 → 暗部被填平、对比丧失。
  f[28] = 0.10; f[29] = 0.11; f[30] = 0.14;         // groundColor（压暗反弹光）
  f[32] = 0.50; f[33] = 0.54; f[34] = 0.60;         // fogColor
  f[36] = 1.0; f[37] = 0.80; f[38] = 0.52;          // sunColor
  f[40] = this.cam.vw; f[41] = this.cam.vh;         // res
  f[42] = this._time;
  f[43] = this.cam.zoom;
  f[44] = 0.0016;                                   // fogDensity
  /* 曝光/光照标定（v2.3 修正）
     原理：材质 baseColor 是【反射率】(0~1)，入射光总强度应 ≈1.0，
     这样 baseColor×1.0 才等于真实反射亮度，中灰材质才能落到中灰。
     原实现 hemi(1.0) + sunColor·sunIntensity·ndl(1.0×0.79) ≈ 1.79，
     反射率被整体放大 1.79 倍，再乘 exposure=1.15 → ACES 严重饱和：
     实测最暗像素 0.5546、四类材质全部挤在 164~229 高亮窄带，画质档位差异被抹平。
     现按"总光强≈1.0"重新标定：
       hemi(0.40×sky≈0.34) + sun(0.85×0.79≈0.67) ≈ 1.0  ✔
     配合合成端新增的对比度/饱和度（shaders.js 合成 pass），
     实测草地/岩石色差由 32/255 提升到约 53/255（+66%），画面恢复层次。 */
  f[45] = 0.95;                                     // exposure（配合总光强 1.0）
  f[46] = 0.85;                                     // sunIntensity（原 1.0）
  // AO 半径：必须与【地形高度场尺度】匹配，否则采样点落在同一平面上 → 无遮蔽。
  // v2.3 高度场重做后幅度达 ±200 世界单位、格距 CELL=46，故半径取约 1.5 格 = 70，
  // 采样能跨到相邻坡面/树根，产生真实的接触阴影（原值 3.0 在旧尺度下尚可、
  // 在新尺度下过小）。配合着色器端"强度只乘一次"的修复，AO 才真正可见。
  f[47] = 70.0;                                     // aoRadius（原 3.0）
  f[48] = 0.85;                                     // aoIntensity（原 0.55）
  f[49] = this._qualityFlags != null ? this._qualityFlags : 7; // 画质开关位（默认全开）
  this.device.queue.writeBuffer(this._frameBuffer, 0, f);
};

/* ── 附件尺寸 / 重建 RT ── */
OverworldGame._ensureSize = function () {
  if (!this._ctx) return;
  var w = Math.max(2, Math.floor(this.cam.vw * (this._dpr || 1)));
  var h = Math.max(2, Math.floor(this.cam.vh * (this._dpr || 1)));
  if (w === this._rtsW && h === this._rtsH) return;
  // canvas 尺寸变更会重置/失效当前 swapchain 纹理，而旧 RT/View 可能正被上一帧占用。
  // 先 device.queue.onSubmittedWorkDone() 无法在同步函数里 await，故此处只做尺寸同步 +
  // configure；真正 awaiting 的 RT 重建在 _rebuildSized()（异步）里完成之外的部分同步完成。
  this._rtsW = w; this._rtsH = h;
  this._canvas.width = w; this._canvas.height = h;
  // WebGPU swapchain 必须在 canvas 尺寸变化后重新 configure，否则呈现的纹理尺寸仍是
  // 最初 configure 时的值（首次 _initGPU 里 configure 用默认 300×150），
  // 导致 canvas 尺寸变大全靠缩放开，某些环境输出为空/白屏。
  //
  // 注意：configure 后**不要立即 getCurrentTexture()**——低端/软件适配器（SwiftShader、
  // 部分 Linux vulkan 驱动）在尺寸刚变更时会跨进程失效整个 WebGPU Instance，表现为
  //   OperationError: A valid external Instance reference no longer exists
  // 一旦该异常发生在帧内，device 即被判为丢失、后续所有 mapAsync 全部 AbortError，
  // 画布永久空白。这里保持 configure，纹理在下一帧 _render() 中按需获取（见 _render）。
  this._ctx.configure({ device: this.device, format: this.presentFormat, alphaMode: 'opaque' });
  this._colorRT = this._rt('rgba16float', w, h);
  this._normalRT = this._rt('rgba8unorm', w, h);
  this._posRT = this._rt(this._posFormat || 'rgba32float', w, h);
  this._depthRT = this.device.createTexture({ size: [w, h], format: 'depth32float', usage: GPUTextureUsage.RENDER_ATTACHMENT });
  this._aoRT = this.device.createTexture({ size: [w, h], format: 'rgba8unorm', usage: GPUTextureUsage.STORAGE_BINDING | GPUTextureUsage.TEXTURE_BINDING });
  this._volRT = this._rt('rgba16float', w, h);
  // 缓存附件 View（避免每帧 createView 的分配/GC 开销）
  this._views = {
    color: this._colorRT.createView(), normal: this._normalRT.createView(), pos: this._posRT.createView(),
    depth: this._depthRT.createView(), vol: this._volRT.createView()
  };
  // 预计算 SSAO / 细节 compute 派发数
  this._ssaoDispatchW = Math.ceil(w / 8); this._ssaoDispatchH = Math.ceil(h / 8);
  // Bloom mip 链
  this._bloomRTs = [];
  var bw = w, bh = h;
  for (var i = 0; i < 4; i++) { bw = Math.max(4, bw >> 1); bh = Math.max(4, bh >> 1); this._bloomRTs.push(this._rt('rgba16float', bw, bh)); }
  this._buildPostBindGroups();
};
OverworldGame._rt = function (fmt, w, h) {
  // COPY_SRC：让 RT 可被 copyTextureToTexture 读回，供「呈现自检」做实像素校验。
  // 仅放开拷贝来源权限，不改变渲染行为、不额外占用显存。
  return this.device.createTexture({
    size: [w, h], format: fmt,
    usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_SRC
  });
};

/* ── 管线构建 ── */
OverworldGame._buildPipelines = function () {
  var d = this.device, sh = S, modConfig = { module: this._module };
  var self = this;
  function diag(msg) { try { console.log('[WebGPU] ' + msg); } catch (e) {} }
  // 编译 shader 后立即校验 WGSL 编译信息：出错时打印精确的行/列/诊断，便于定位黑屏。
  function module(src, label) {
    var m = d.createShaderModule({ code: src });
    try {
      if (typeof m.getCompilationInfo === 'function') {
        m.getCompilationInfo().then(function (info) {
          if (!info || !info.messages || !info.messages.length) return;
          info.messages.forEach(function (msg) {
            var level = msg.level || 'error';
            if (level === 'error' || level === 'warning') {
              diag('WGSL[' + (label || 'shader') + '] ' + level + ' L' + (msg.lineNum || '?') + ':' + (msg.linePos || '?') + ' — ' + (msg.message || ''));
              try {
                var lines = src.split('\n');
                var ln = (msg.lineNum || 0) - 1;
                for (var k = Math.max(0, ln - 2); k <= Math.min(lines.length - 1, ln + 2); k++) {
                  diag('   ' + (k + 1) + ': ' + lines[k]);
                }
              } catch (e) {}
            }
          });
        }).catch(function () {});
      }
    } catch (e) {}
    return m;
  }
  var posFmt = this._posFormat || 'rgba32float';
  this._frameLayout = d.createBindGroupLayout({ entries: [{ binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } }] }); // vert(1)|frag(4)|compute(8)
  // 地形细节场布局：group(1) = 计算生成的细节纹理 + 采样器（顶点置换 + 法线锐化）
  this._detailLayout = d.createBindGroupLayout({ entries: [
    { binding: 0, visibility: GPUShaderStage.VERTEX, texture: {} }, { binding: 1, visibility: GPUShaderStage.VERTEX, sampler: {} }] }); // vertex 采样
  this._detailWriteLayout = d.createBindGroupLayout({ entries: [
    { binding: 0, visibility: GPUShaderStage.COMPUTE, storageTexture: { format: 'rgba16float', access: 'write-only' } }] });
  // terrain（group(0)=frame，group(1)=细节场）
  this._pipeTerrain = d.createRenderPipeline({
    layout: d.createPipelineLayout({ bindGroupLayouts: [this._frameLayout, this._detailLayout] }),
    vertex: { module: module(sh.TERRAIN_VS, 'TERRAIN_VS'), buffers: [{ arrayStride: 40, attributes: [
      { shaderLocation: 0, offset: 0, format: 'float32x3' }, { shaderLocation: 1, offset: 12, format: 'float32x4' }, { shaderLocation: 2, offset: 28, format: 'float32x3' }] }] },
    fragment: { module: module(sh.TERRAIN_FS, 'TERRAIN_FS'), targets: [this._fmt('rgba16float', null), this._fmt('rgba8unorm', null), this._fmt(posFmt, null)] },
    primitive: { topology: 'triangle-list', cullMode: 'none' },
    depthStencil: { format: 'depth32float', depthWriteEnabled: true, depthCompare: 'less' }
  });
  // deco
  this._pipeDeco = d.createRenderPipeline({
    layout: d.createPipelineLayout({ bindGroupLayouts: [this._frameLayout] }),
    vertex: { module: module(sh.DECO_VS, 'DECO_VS'), buffers: [
      { arrayStride: 24, attributes: [{ shaderLocation: 0, offset: 0, format: 'float32x3' }, { shaderLocation: 1, offset: 12, format: 'float32x3' }] },
      { arrayStride: 80, stepMode: 'instance', attributes: [
        { shaderLocation: 2, offset: 0, format: 'float32x3' }, { shaderLocation: 3, offset: 12, format: 'float32x3' },
        { shaderLocation: 4, offset: 24, format: 'float32x4' }, { shaderLocation: 5, offset: 32, format: 'float32x4' },
        { shaderLocation: 6, offset: 48, format: 'float32x4' }, { shaderLocation: 7, offset: 64, format: 'float32x4' }] }] },
    fragment: { module: module(sh.DECO_FS, 'DECO_FS'), targets: [
      this._fmt('rgba16float', { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha' }),
      this._fmt('rgba8unorm', null), this._fmt(posFmt, null)] },
    primitive: { topology: 'triangle-list', cullMode: 'none' },
    depthStencil: { format: 'depth32float', depthWriteEnabled: true, depthCompare: 'less' }
  });
  // 全屏后处理（vol / bright / blur / composite 共用顶点三角形）
  // 全屏 pass 的 pipeline layout 必须与所用 bind group 的 layout 完全一致：
  //   VOL_FS 采样位置纹理（rgba32float 时不可过滤）→ 用 _postLayoutU；
  //   BRIGHT_FS / BLUR_FS 采样颜色/bloom 纹理（rgba16float，可过滤）→ 用 _postLayoutF。
  if (!this._postLayoutF) this._mkFsLayout();
  var fsLayoutU = d.createPipelineLayout({ bindGroupLayouts: [this._frameLayout, this._postLayoutU] });
  var fsLayoutF = d.createPipelineLayout({ bindGroupLayouts: [this._frameLayout, this._postLayoutF] });
  this._pipeVol = d.createRenderPipeline({ layout: fsLayoutU, vertex: { module: module(sh.FULLSCREEN_VS, 'FULLSCREEN_VS') }, fragment: { module: module(sh.VOL_FS, 'VOL_FS'), targets: [this._fmt('rgba16float', null)] }, primitive: { topology: 'triangle-list', cullMode: 'none' } });
  this._pipeBright = d.createRenderPipeline({ layout: fsLayoutF, vertex: { module: module(sh.FULLSCREEN_VS, 'FULLSCREEN_VS') }, fragment: { module: module(sh.BRIGHT_FS, 'BRIGHT_FS'), targets: [this._fmt('rgba16float', null)] }, primitive: { topology: 'triangle-list', cullMode: 'none' } });
  this._pipeBlur = d.createRenderPipeline({ layout: fsLayoutF, vertex: { module: module(sh.FULLSCREEN_VS, 'FULLSCREEN_VS') }, fragment: { module: module(sh.BLUR_FS, 'BLUR_FS'), targets: [this._fmt('rgba16float', null)] }, primitive: { topology: 'triangle-list', cullMode: 'none' } });
  // 合成
  var compLayout = d.createPipelineLayout({ bindGroupLayouts: [this._frameLayout, this._compLayout || this._mkCompLayout()] });
  this._pipeComposite = d.createRenderPipeline({ layout: compLayout, vertex: { module: module(sh.FULLSCREEN_VS) }, fragment: { module: module(sh.COMPOSITE_FS), targets: [this._fmt(this.presentFormat, null)] }, primitive: { topology: 'triangle-list', cullMode: 'none' } });
  // SSAO compute
  this._ssaoLayout = this._mkSsaLayout();
  var ssaoPipeLayout = d.createPipelineLayout({ bindGroupLayouts: [this._frameLayout, this._ssaoLayout] });
  this._pipeSsa = d.createComputePipeline({ layout: ssaoPipeLayout, compute: { module: module(sh.SSAO_CS) } });
  // 地形细节场 compute（生成高频细节场，供地形顶点置换）
  this._pipeDetail = d.createComputePipeline({ layout: d.createPipelineLayout({ bindGroupLayouts: [this._detailWriteLayout] }), compute: { module: module(sh.TERRAIN_DETAIL_CS) } });
  // 实例几何
  var cube = makeCube();
  this._cubeVert = d.createBuffer({ size: cube.verts.byteLength, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.VERTEX });
  d.queue.writeBuffer(this._cubeVert, 0, cube.verts);
  this._cubeIdx = d.createBuffer({ size: cube.idx.byteLength, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.INDEX });
  d.queue.writeBuffer(this._cubeIdx, 0, cube.idx);
  this._cubeIdxCount = cube.indexCount;
};

OverworldGame._fmt = function (format, blend) {
  return blend ? { format: format, blend: { color: { operation: 'add', srcFactor: blend.srcFactor, dstFactor: blend.dstFactor }, alpha: { operation: 'add', srcFactor: 'one', dstFactor: 'one-minus-src-alpha' } } } : { format: format };
};
// 全屏单一纹理 pass 的 bind group layout（src + sampler）
OverworldGame._mkFsLayout = function () {
  // 位置缓冲（_posRT）格式可能是 rgba32float 或 rgba16float：
  //   · rgba32float 的采样类型只有 "unfilterable-float"，用默认的 "float" 会校验失败；
  //   · rgba16float 同时兼容 "float" 与 "unfilterable-float"。
  // 因此凡绑定 _posRT 的条目一律显式声明 sampleType:'unfilterable-float' 并配 non-filtering 采样器，
  // 两种格式下都合法（这正是 "None of the supported sample types" 报错的修复点）。
  // 注意：binding 顺序必须与 WGSL 一致。_postLayoutF 用于 VOL/BRIGHT/BLUR；
  //       VOL_FS 采样的是位置纹理（可能不可过滤），故单独用 _postLayoutU。
  this._postLayoutF = this.device.createBindGroupLayout({ entries: [
    { binding: 0, visibility: GPUShaderStage.FRAGMENT, texture: {} },
    { binding: 1, visibility: GPUShaderStage.FRAGMENT, sampler: {} }] });                     // 可过滤（colorRT/bloomRT）
  this._postLayoutU = this.device.createBindGroupLayout({ entries: [
    { binding: 0, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'unfilterable-float' } },
    { binding: 1, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'non-filtering' } }] }); // 不可过滤（posRT）
  this._postLayout = this._postLayoutF;   // 默认（历史调用点兼容）
  this._compLayout = this.device.createBindGroupLayout({ entries: [
    { binding: 0, visibility: GPUShaderStage.FRAGMENT, texture: {} }, { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: {} }, { binding: 2, visibility: GPUShaderStage.FRAGMENT, texture: {} },
    { binding: 3, visibility: GPUShaderStage.FRAGMENT, texture: {} }, { binding: 4, visibility: GPUShaderStage.FRAGMENT, texture: {} }, { binding: 5, visibility: GPUShaderStage.FRAGMENT, texture: {} },
    { binding: 6, visibility: GPUShaderStage.FRAGMENT, texture: {} }, { binding: 7, visibility: GPUShaderStage.FRAGMENT, sampler: {} }] });
  // SSAO：binding0 采样 _posRT（可能是 rgba32float → unfilterable-float），binding1 采样 _normalRT（rgba8unorm）
  this._ssaLayout = this.device.createBindGroupLayout({ entries: [
    { binding: 0, visibility: GPUShaderStage.COMPUTE, texture: { sampleType: 'unfilterable-float' } },
    { binding: 1, visibility: GPUShaderStage.COMPUTE, texture: {} },
    { binding: 2, visibility: GPUShaderStage.COMPUTE, storageTexture: { format: 'rgba8unorm', access: 'write-only' } }] });
  return this._postLayout;
};
OverworldGame._mkCompLayout = function () { return this._compLayout; };
OverworldGame._mkSsaLayout = function () { return this._ssaLayout; };

// 上传世界几何（地形 + 装饰 + 分块）
OverworldGame._uploadWorld = function () {
  var d = this.device, wld = this._world;
  // 防御：地形数据缺失时给出明确错误，而不是 "Cannot read properties of undefined"。
  // 这类失败会让整个 WebGPU 通道静默失效并降级 melonJS，必须有可诊断信息。
  if (!wld || !wld.terrainVerts) {
    throw new Error('[WebGPU] 地形数据未就绪（_world 为空或缺少 terrainVerts）；' +
                    '通常是 buildWorld 构建失败，请查看上方 Worker/主线程报错。');
  }
  this._terrainVert = d.createBuffer({ size: wld.terrainVerts.byteLength, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
  d.queue.writeBuffer(this._terrainVert, 0, wld.terrainVerts);
  this._terrainIdx = d.createBuffer({ size: wld.terrainIdx.byteLength, usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST });
  d.queue.writeBuffer(this._terrainIdx, 0, wld.terrainIdx);
  this._decoInst = d.createBuffer({ size: wld.decoInst.byteLength, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
  d.queue.writeBuffer(this._decoInst, 0, wld.decoInst);
  this._terrainChunks = wld.terrainChunks;
  this._decoChunks = wld.decoChunks;
  this._terrainChunkCount = wld.terrainChunkCount;
  this._decoChunkCount = wld.decoChunkCount;
  this._frameBuffer = d.createBuffer({ size: 208, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
  this._frameBuf = new Float32Array(52);

  // 地形细节场：世界锚定纹理（1 纹元 = 1 格）+ 采样 bind group + 写入 bind group
  this._detailTex = d.createTexture({ size: [this.W, this.H], format: 'rgba16float', usage: GPUTextureUsage.STORAGE_BINDING | GPUTextureUsage.TEXTURE_BINDING });
  this._detailBG = d.createBindGroup({ layout: this._detailLayout, entries: [
    { binding: 0, resource: this._detailTex.createView() }, { binding: 1, resource: this._sampler }] });
  this._detailWriteBG = d.createBindGroup({ layout: this._detailWriteLayout, entries: [
    { binding: 0, resource: this._detailTex.createView() }] });
  // 一次性 compute：GPU 并行生成高频细节场
  var enc = d.createCommandEncoder();
  var cp = enc.beginComputePass();
  cp.setPipeline(this._pipeDetail); cp.setBindGroup(0, this._detailWriteBG);
  cp.dispatchWorkgroups(Math.ceil(this.W / 8), Math.ceil(this.H / 8));
  cp.end();
  d.queue.submit([enc.finish()]);
};

/* ── 后处理 bind group（随 RT 重建而重建） ── */
OverworldGame._buildPostBindGroups = function () {
  var d = this.device, pass = this._fsPassBG = { base: {}, comp: {}, ssao: {} };
  // vol: posRT —— 位置纹理可能为 rgba32float（unfilterable-float），用不可过滤 layout + 非过滤采样器
  this._volBG = d.createBindGroup({ layout: this._postLayoutU, entries: [
    { binding: 0, resource: this._posRT.createView() }, { binding: 1, resource: this._nfSampler }] });
  // bright: colorRT
  this._brightBG = d.createBindGroup({ layout: this._postLayout, entries: [
    { binding: 0, resource: this._colorRT.createView() }, { binding: 1, resource: this._sampler }] });
  // composite
  this._compBG = d.createBindGroup({ layout: this._compLayout, entries: [
    { binding: 0, resource: this._colorRT.createView() }, { binding: 1, resource: this._aoRT.createView() },
    { binding: 2, resource: this._volRT.createView() }, { binding: 3, resource: this._bloomRTs[0].createView() },
    { binding: 4, resource: this._bloomRTs[1].createView() }, { binding: 5, resource: this._bloomRTs[2].createView() },
    { binding: 6, resource: this._bloomRTs[3].createView() }, { binding: 7, resource: this._sampler }] });
  // ssao
  this._ssaoBG = d.createBindGroup({ layout: this._ssaLayout, entries: [
    { binding: 0, resource: this._posRT.createView() }, { binding: 1, resource: this._normalRT.createView() },
    { binding: 2, resource: this._aoRT.createView() }] });
  // 预创建 Bloom 逐级模糊 bind group（跨帧复用，避免每帧 createBindGroup）
  this._blurBGs = [];
  for (var k = 1; k < 4; k++) {
    this._blurBGs[k] = d.createBindGroup({ layout: this._postLayout, entries: [
      { binding: 0, resource: this._bloomRTs[k - 1].createView() }, { binding: 1, resource: this._sampler }] });
  }
  this._frameBG = {}; // frame 绑定打进 bind group，见 _frameBind
  this._frameBG = d.createBindGroup({ layout: this._frameLayout, entries: [{ binding: 0, resource: { buffer: this._frameBuffer } }] });
};

// 可见分块范围（以玩家为中心的轴对齐盒，保守覆盖屏幕）
OverworldGame._visibleChunks = function () {
  var z = this.cam.zoom;
  var hx = Math.ceil((this.cam.vw / (2 * z)) / CELL / CHUNK) + 1;
  var hy = Math.ceil((this.cam.vh / (2 * z)) / CELL / CHUNK) + 1;
  var cxp = Math.floor(this.playerPos.x / CHUNK), cyp = Math.floor(this.playerPos.y / CHUNK);
  return { cx0: cxp - hx, cx1: cxp + hx, cy0: cyp - hy, cy1: cyp + hy };
};

/* ── 渲染主流程 ── */
OverworldGame._render = function () {
  var d = this.device, c = this.cam;
  this._updateView(); this._updateFrame();
  var vp = this._views.color, nv = this._views.normal, pv = this._views.pos;
  var dv = this._views.depth;
  var enc = d.createCommandEncoder();
  // 1) 几何 pass
  var rp = enc.beginRenderPass({ colorAttachments: [
    { view: vp, loadOp: 'clear', clearValue: [0.05, 0.06, 0.08, 1], storeOp: 'store' },
    { view: nv, loadOp: 'clear', clearValue: [0.5, 0.5, 1, 1], storeOp: 'store' },
    { view: pv, loadOp: 'clear', clearValue: [0, 0, 9999, 1], storeOp: 'store' }],
    depthStencilAttachment: { view: dv, depthLoadOp: 'clear', depthClearValue: 1, depthStoreOp: 'store' } });
  // 地形（顶点着色器采样 GPU 生成细节场做置换）
  var vis = this._visibleChunks();
  rp.setPipeline(this._pipeTerrain);
  rp.setBindGroup(0, this._frameBG);
  rp.setBindGroup(1, this._detailBG);
  rp.setVertexBuffer(0, this._terrainVert);
  rp.setIndexBuffer(this._terrainIdx, 'uint32');
  var tc = this._terrainChunks;
  for (var i = 0; i < this._terrainChunkCount; i++) {
    var cx = tc[i * 4], cy = tc[i * 4 + 1];
    if (cx < vis.cx0 || cx > vis.cx1 || cy < vis.cy0 || cy > vis.cy1) continue;
    rp.drawIndexed(tc[i * 4 + 3], 1, tc[i * 4 + 2], 0, 0);
  }
  // 装饰（实例化）
  rp.setPipeline(this._pipeDeco);
  rp.setVertexBuffer(0, this._cubeVert);
  rp.setVertexBuffer(1, this._decoInst);
  rp.setIndexBuffer(this._cubeIdx, 'uint32');
  var dc = this._decoChunks;
  for (var j = 0; j < this._decoChunkCount; j++) {
    var dx = dc[j * 4], dy = dc[j * 4 + 1];
    if (dx < vis.cx0 || dx > vis.cx1 || dy < vis.cy0 || dy > vis.cy1) continue;
    rp.drawIndexed(this._cubeIdxCount, dc[j * 4 + 3], 0, 0, dc[j * 4 + 2]);
  }
  rp.end();
  // 2) SSAO compute（智能画质：低档设备跳过，合成端 ao 贡献归零）
  if (this._qualityFlags == null || (this._qualityFlags & 1)) {
    var cp = enc.beginComputePass();
    cp.setPipeline(this._pipeSsa);
    cp.setBindGroup(0, this._frameBG);
    cp.setBindGroup(1, this._ssaoBG);
    cp.dispatchWorkgroups(this._ssaoDispatchW || Math.ceil(c.vw / 8), this._ssaoDispatchH || Math.ceil(c.vh / 8));
    cp.end();
  }
  // 3) 体积光（智能画质：低档设备跳过）
  var vr = this._views.vol;
  if (this._qualityFlags == null || (this._qualityFlags & 4)) {
    var vpass = enc.beginRenderPass({ colorAttachments: [{ view: vr, loadOp: 'clear', clearValue: [0, 0, 0, 1], storeOp: 'store' }] });
    vpass.setPipeline(this._pipeVol); vpass.setBindGroup(0, this._frameBG); vpass.setBindGroup(1, this._volBG); vpass.draw(3); vpass.end();
  }
  // 4) Bloom: bright → m0；m0→m1→m2→m3 下采样（智能画质：低档设备跳过）
  if (this._qualityFlags == null || (this._qualityFlags & 2)) {
    var b0 = this._bloomRTs[0].createView();
    var bp = enc.beginRenderPass({ colorAttachments: [{ view: b0, loadOp: 'clear', clearValue: [0, 0, 0, 1], storeOp: 'store' }] });
    bp.setPipeline(this._pipeBright); bp.setBindGroup(0, this._frameBG); bp.setBindGroup(1, this._brightBG); bp.draw(3); bp.end();
    for (var k = 1; k < 4; k++) {
      var bv = this._bloomRTs[k].createView();
      var lbp = enc.beginRenderPass({ colorAttachments: [{ view: bv, loadOp: 'clear', clearValue: [0, 0, 0, 1], storeOp: 'store' }] });
      lbp.setPipeline(this._pipeBlur); lbp.setBindGroup(0, this._frameBG); lbp.setBindGroup(1, this._blurBGs[k]); lbp.draw(3); lbp.end();
    }
  }
  // 5) 合成 → swapchain
  //    getCurrentTexture() 在部分环境会抛（尺寸刚变更 / 软件适配器 swapchain 跨进程失效）：
  //    一旦异常冒泡出 _render，上层会误判 device 丢失并放弃整帧；这里就地降级为
  //    「本帧跳过呈现」，下一帧再试，绝不把设备判死。
  var cur = null;
  try {
    cur = this._ctx.getCurrentTexture();
  } catch (e) {
    this._presentFails = (this._presentFails || 0) + 1;
    if (this._presentFails <= 3 || this._presentFails % 120 === 0) {
      try { console.warn('[WebGPU] getCurrentTexture 失败（第 ' + this._presentFails + ' 次），本帧跳过呈现：', e && e.message ? e.message : e); } catch (e2) {}
    }
    if (this._presentFails === 1) {
      // 首帧失败通常意味着这里 configure 尚未被后续尺寸同步刷新；主动重配一次以便自愈。
      try { this._ctx.configure({ device: this.device, format: this.presentFormat, alphaMode: 'opaque' }); } catch (e3) {}
    }
    // 仍需提交本帧已编码的离屏 pass，避免命令编码器泄漏
    try { d.queue.submit([enc.finish()]); } catch (e4) {}
    return;
  }
  {
    var sp = enc.beginRenderPass({ colorAttachments: [{ view: cur.createView(), loadOp: 'clear', clearValue: [0, 0, 0, 1], storeOp: 'store' }] });
    sp.setPipeline(this._pipeComposite); sp.setBindGroup(0, this._frameBG); sp.setBindGroup(1, this._compBG); sp.draw(3); sp.end();
  }
  d.queue.submit([enc.finish()]);
  this._presentFails = 0;
};

/* ── 覆盖层（玩家/POI/告示牌/引导） ── */
OverworldGame._makeOverlay = function () {
  var ov = document.createElement('div');
  ov.className = 'wgpu-overlay';
  ov.style.cssText = 'position:absolute;inset:0;pointer-events:none;overflow:hidden;';
  this._canvas.parentNode.appendChild(ov);
  this.overlayEl = ov;
  this._buildPlayer();
};

OverworldGame._buildPlayer = function () {
  var pd = document.createElement('div');
  pd.className = 'player-marker';
  pd.innerHTML = '<span class="player-shade"></span><span class="player-avatar">☯</span>';
  this.overlayEl.appendChild(pd);
  this.playerEl = pd;
  this.facing = 1; this.moving = false; this.walkPhase = 0;
};

OverworldGame._buildPoisOverlay = function () {
  var self = this;
  this.pois = [];
  (this.ow.pois || []).forEach(function (p) {
    if (p.type === 'spawn') return;
    var marker = { poi: p, emoji: null, billboard: null, badge: null };
    if (p.type === 'billboard') {
      marker.billboard = self._buildBillboard(p);
    } else {
      var em = document.createElement('div');
      var isSandbox = p.type === 'sandbox';
      em.className = 'poi-emoji' + (p.type === 'realm' ? ' realm' : ' npc') + (isSandbox ? ' sandbox' : '');
      em.style.fontSize = (CELL * (isSandbox ? 1.85 : 1.15)) + 'px';
      em.style.lineHeight = '1';
      em.style.width = (CELL * (isSandbox ? 2.6 : 1.8)) + 'px'; em.style.height = (CELL * (isSandbox ? 2.6 : 1.8)) + 'px';
      em.style.display = 'flex'; em.style.alignItems = 'center'; em.style.justifyContent = 'center';
      em.textContent = p.emoji;
      em.setAttribute('data-poi', p.id || '');
      self.overlayEl.appendChild(em);
      marker.emoji = em;
      if (p.type === 'realm') {
        var badge = document.createElement('div');
        badge.className = 'realm-badge'; badge.style.width = (CELL * 1.8) + 'px';
        badge.style.display = 'flex'; badge.style.justifyContent = 'center'; badge.style.alignItems = 'center';
        var bt = document.createElement('span'); bt.textContent = '—'; badge.appendChild(bt);
        self.overlayEl.appendChild(badge);
        marker.badge = bt; marker.badgeEl = badge;
        (self.badgePool = self.badgePool || []).push({ realm: p.realm, el: badge, text: bt, done: false, sandbox: false });
      }
    }
    self.pois.push(marker);
  });
  this._syncBadges((this.samsara || {}).realm_progress || {});
};

OverworldGame._buildBillboard = function (p) {
  var bb = document.createElement('div');
  bb.className = 'ow-billboard glow' + (p.active ? ' active' : '');
  bb.setAttribute('data-poi', p.id || '');
  bb.innerHTML =
    '<div class="bb-post"><div class="bb-title">轮回修行</div><div class="bb-sub">SAMSARA</div><div class="bb-line"></div>' +
    '<div class="bb-rows"><div class="bb-row"><span>悟道</span><b data-k="enlight">—</b></div>' +
    '<div class="bb-row"><span>堕落</span><b data-k="corrupt">—</b></div>' +
    '<div class="bb-row"><span>祈求</span><b data-k="prayer">—</b></div>' +
    '<div class="bb-row"><span>记忆碎片</span><b data-k="frags">—</b></div></div>' +
    '<div class="bb-line"></div><div class="bb-legend">靠近按 [E] 查看详情</div></div>' +
    '<div class="bb-pole"></div><div class="bb-base"></div>';
  this.overlayEl.appendChild(bb);
  (this.billboards = this.billboards || []).push({
    id: p.id || 'billboard', el: bb,
    enlight: bb.querySelector('[data-k="enlight"]'), corrupt: bb.querySelector('[data-k="corrupt"]'),
    prayer: bb.querySelector('[data-k="prayer"]'), frags: bb.querySelector('[data-k="frags"]')
  });
  this._refreshBillboard();
  return bb;
};

OverworldGame._refreshBillboard = function () {
  if (!this.billboards) return;
  var s = this.samsara || {}, ra = s.alignment || {}, frags = s.memory_fragments || {};
  this.billboards.forEach(function (b) {
    if (b.enlight) b.enlight.textContent = ra.enlightenment || 0;
    if (b.corrupt) b.corrupt.textContent = ra.corruption || 0;
    if (b.prayer) b.prayer.textContent = s.prayer_count || 0;
    if (b.frags) b.frags.textContent = ((frags.unlocked_count || 0) + ' / ' + (frags.total || 6));
  });
};

OverworldGame._syncBadges = function (realmProgress) {
  var self = this;
  (this.badgePool || []).forEach(function (b) {
    var rp = (realmProgress && realmProgress[b.realm]) || {};
    var total = self._levelTotals && self._levelTotals[b.realm] !== undefined ? self._levelTotals[b.realm] : 5;
    var passed = rp.levels_passed || 0;
    b.done = !!rp.completed;
    b.text.textContent = b.done ? ('✓ 已通关') : (passed + ' / ' + total);
    b.text.style.color = b.done ? '#0a9396' : '#f4c542';
    b.el.classList.toggle('realm-done', !!b.done);
    /* 叠加棋类定位标签（主推 / 不推荐的测试） */
    var t = (window.OverworldUI && window.OverworldUI.realmTag) ? window.OverworldUI.realmTag(b.realm) : null;
    if (!b.tagEl) { b.tagEl = document.createElement('span'); b.tagEl.className = 'rec-tag'; b.el.appendChild(b.tagEl); }
    if (t) { b.tagEl.textContent = t.text; b.tagEl.className = 'rec-tag ' + t.cls; b.tagEl.style.display = ''; }
    else { b.tagEl.textContent = ''; b.tagEl.style.display = 'none'; }
  }, this);
};

// 逐帧：用与 GPU 相同的投影放置 DOM 覆盖元素
OverworldGame._updateOverlay = function () {
  if (!this.overlayEl) return;
  var self = this, z = this.cam.zoom;
  // 玩家
  var pp = this.project(this.playerPos.x * CELL, this.playerPos.y * CELL, 0);
  var pe = this.playerEl, av = pe.querySelector('.player-avatar');
  av.style.left = (pp.x - 23).toFixed(1) + 'px';
  av.style.top = (pp.y - 44).toFixed(1) + 'px';
  av.style.transform = (this.facing < 0 ? 'scaleX(-1) ' : '') +
    (this.moving ? 'translateY(' + (Math.abs(Math.sin(this.walkPhase)) * -6).toFixed(1) + 'px)' : '');
  // POI（用与 GPU 相同的投影放置）
  this.pois.forEach(function (m) {
    var wpos = self.project(m.poi.x * CELL, m.poi.y * CELL, 0);
    var sx = z / 1.6;
    if (!wpos.on || wpos.x < -120 || wpos.x > self.cam.vw + 120 || wpos.y < -160 || wpos.y > self.cam.vh + 160) {
      if (m.emoji) m.emoji.style.display = 'none';
      if (m.billboard) m.billboard.style.display = 'none';
      if (m.badgeEl) m.badgeEl.style.display = 'none';
      return;
    }
    if (m.emoji) {
      m.emoji.style.display = '';
      var big = m.poi.type === 'sandbox';
      m.emoji.style.left = (wpos.x - CELL * (big ? 1.3 : 0.9) * sx).toFixed(1) + 'px';
      m.emoji.style.top = (wpos.y - CELL * (big ? 1.75 : 1.35) * sx).toFixed(1) + 'px';
    }
    if (m.badgeEl) {
      m.badgeEl.style.display = '';
      m.badgeEl.style.left = (wpos.x - CELL * 0.9 * sx).toFixed(1) + 'px';
      m.badgeEl.style.top = (wpos.y - CELL * 2.05 * sx).toFixed(1) + 'px';
    }
    if (m.billboard) {
      m.billboard.style.display = '';
      m.billboard.style.left = (wpos.x - CELL * 2.05 * sx).toFixed(1) + 'px';
      m.billboard.style.top = (wpos.y - CELL * 3.2 * sx).toFixed(1) + 'px';
    }
  });
};

/* ── 小地图 ── */
OverworldGame._minimapColor = function (x, y) {
  var fl = this.flags[y * this.W + x];
  if (fl & 1) return [0x1E, 0x6A, 0x96];
  if (fl & 2) return [0xC9, 0xB3, 0x7E];
  var rid = this.biome[y * this.W + x];
  var c = BIOME_C[rid] ? BIOME_C[rid] : [0x5f, 0x9e, 0x4e];
  return c;
};
OverworldGame.initMinimap = function () {
  var c = document.getElementById('minimap-base');
  if (!c || !this.W) return;
  var W = this.W, H = this.H;
  c.width = W; c.height = H;
  var ctx = c.getContext('2d');
  var img = ctx.createImageData(W, H), d = img.data;
  for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) {
    var col = this._minimapColor(x, y), i = (y * W + x) * 4;
    d[i] = col[0]; d[i + 1] = col[1]; d[i + 2] = col[2]; d[i + 3] = 255;
  }
  ctx.putImageData(img, 0, 0);
  // 入口标点
  var poisEl = document.getElementById('minimap-pois');
  if (!poisEl) return;
  poisEl.innerHTML = '';
  this.minimapPois = {};
  var self = this;
  (this.pois || []).forEach(function (m) {
    var p = m.poi;
    if (!p) return;
    var div = document.createElement('div');
    var isSandbox = p.type === 'sandbox';
    div.className = 'minimap-poi' + (isSandbox ? ' sandbox' : '');
    div.style.left = ((p.x + 0.5) / W * 100) + '%';
    div.style.top = ((p.y + 0.5) / H * 100) + '%';
    if (p.label) div.setAttribute('title', p.label);
    poisEl.appendChild(div);
    self.minimapPois[p.id] = { div: div, base: p.emoji };
    if (isSandbox) { div.classList.add('explored'); div.textContent = p.emoji; }
    else if (self._isExplored(p.id)) { div.classList.add('explored'); div.textContent = p.emoji; }
    else { div.classList.add('unexplored'); div.textContent = '❓'; }
  });
  this._refreshMinimapPlayer();
};
OverworldGame._refreshMinimapPlayer = function () {
  var p = document.getElementById('minimap-player');
  if (!p || !this.playerPos || !this.W) return;
  p.style.left = (this.playerPos.x / this.W * 100) + '%';
  p.style.top = (this.playerPos.y / this.H * 100) + '%';
};
OverworldGame._isExplored = function (poiId) {
  try { var raw = localStorage.getItem(EXPLORED_KEY); var arr = raw ? JSON.parse(raw) : []; return arr.indexOf(poiId) >= 0; } catch (e) { return false; }
};
OverworldGame._markExplored = function (poiId) {
  if (!poiId) return;
  try { var raw = localStorage.getItem(EXPLORED_KEY); var arr = raw ? JSON.parse(raw) : []; if (arr.indexOf(poiId) < 0) { arr.push(poiId); localStorage.setItem(EXPLORED_KEY, JSON.stringify(arr)); } } catch (e) { }
  var rec = this.minimapPois && this.minimapPois[poiId];
  if (rec) { rec.div.classList.add('explored'); rec.div.classList.remove('unexplored'); rec.div.textContent = rec.base; }
};
OverworldGame._fetchGamesAndState = function () {
  var self = this;
  self.games = [];
  fetch('/api/games').then(function (r) { return r.json(); }).then(function (g) { self.games = g || []; if (UI) UI.setGames(self.games); }).catch(function () { });
  self.refreshSamsara();
};

/* ── 玩家移动 / 碰撞 / 交互 / 引导（与 DOM 版本同算法） ── */
OverworldGame.step = function (dt) {
  if (!this.playerPos) return;
  var paused = !!(UI && UI.isModalOpen());
  var keys = this.keys || {};
  var sx = 0, sy = 0;
  if (!paused) {
    if (keys['a']) sx -= 1; if (keys['d']) sx += 1;
    if (keys['w']) sy -= 1; if (keys['s']) sy += 1;
  }
  if (sx !== 0 || sy !== 0) {
    var inv = Math.hypot(sx, sy);
    this.moving = true;
    if (sx !== 0) this.facing = sx > 0 ? 1 : -1;
    var spd = this.playerSpeed * dt;
    // W/S/A/D 严格映射到世界轴向：W 向北（世界 -y），S 向南（+y），A 向西（-x），D 向东（+x），
    // 使俯视北向小地图与小地图移动方向一致（避免 W/S 呈 45° 斜移）。
    this._moveAxis((sx / inv) * spd, (sy / inv) * spd);
  } else this.moving = false;
  if (this.moving) this.walkPhase += 0.6;
  this._refreshMinimapPlayer();
  if (!paused) this._updateInteraction();
  if (!paused) this._updateRealmGuide();
  if (!paused && this.ePressed) { this._onInteract(); this.ePressed = false; }
};
OverworldGame._moveAxis = function (mx, my) {
  var nx = this.playerPos.x + mx;
  if (!this._willCollide(nx, this.playerPos.y)) this.playerPos.x = nx;
  var ny = this.playerPos.y + my;
  if (!this._willCollide(this.playerPos.x, ny)) this.playerPos.y = ny;
};
OverworldGame._willCollide = function (gx, gy) {
  var r = 0.28;
  return this._isBarrier(Math.floor(gx - r), Math.floor(gy - r)) ||
         this._isBarrier(Math.floor(gx + r), Math.floor(gy - r)) ||
         this._isBarrier(Math.floor(gx - r), Math.floor(gy + r)) ||
         this._isBarrier(Math.floor(gx + r), Math.floor(gy + r));
};
OverworldGame._isBarrier = function (tx, ty) {
  if (tx < 0 || ty < 0 || tx >= this.W || ty >= this.H) return true;
  return !!(this.flags && (this.flags[ty * this.W + tx] & 8));
};
OverworldGame._updateInteraction = function () {
  var self = this, reach = 3.2, closest = null, minD = reach + 1;
  this.pois.forEach(function (m) {
    var d = Math.hypot(m.poi.x - self.playerPos.x, m.poi.y - self.playerPos.y);
    if (d <= reach && d < minD) { minD = d; closest = m; }
  });
  var activeId = closest ? closest.poi.id : null;
  this.pois.forEach(function (m) {
    if (m.emoji) m.emoji.classList.toggle('active', m.poi.id === activeId);
    else if (m.billboard) m.billboard.classList.toggle('active', m.poi.id === activeId);
  });
  this.closestPoi = closest; this.activePoi = activeId;
  if (closest && UI) {
    var lbl;
    if (closest.poi.type === 'realm') lbl = '前往 ' + (REALM_NAMES[closest.poi.realm] || closest.poi.realm);
    else if (closest.poi.type === 'sandbox') lbl = '进入 ' + (closest.poi.label || '沙盒训练场');
    else if (closest.poi.type === 'billboard') lbl = '查看 ' + (closest.poi.label || '轮回修行告示牌');
    else if (closest.poi.type === 'achievements') lbl = '查看 ' + (closest.poi.label || '成就殿堂');
    else lbl = closest.poi.label || '互动';
    UI.setInteractHint(lbl);
  } else if (UI) UI.setInteractHint(null);
};
OverworldGame._updateRealmGuide = function () {
  if (!UI || !UI.setRealmGuide || !this.pois) return;
  var self = this, best = null, bestD = Infinity;
  this.pois.forEach(function (m) {
    if (m.poi.type !== 'realm') return;
    var d = Math.max(Math.abs((m.poi.x + 0.5) - self.playerPos.x), Math.abs((m.poi.y + 0.5) - self.playerPos.y));
    if (d < bestD) { bestD = d; best = m; }
  });
  if (this.closestPoi && this.closestPoi.poi.type === 'realm') { UI.setRealmGuide(null); return; }
  if (!best) { UI.setRealmGuide(null); return; }
  var ppA = this.project(this.playerPos.x * CELL, this.playerPos.y * CELL, 0);
  var ep = this.project(best.poi.x * CELL, best.poi.y * CELL, 0);
  var deg = Math.round(Math.atan2(ep.y - ppA.y, ep.x - ppA.x) * 180 / Math.PI);
  UI.setRealmGuide({ name: REALM_NAMES[best.poi.realm] || best.poi.realm, dist: Math.round(bestD), arrow: '➤', angle: deg });
};
OverworldGame._onInteract = function () {
  var entry = this.closestPoi;
  if (!entry || !UI) return;
  if (entry.poi.type === 'realm') { this._markExplored(entry.poi.id); UI.openRealmSelect(entry.poi.realm); }
  else if (entry.poi.type === 'sandbox') { this._markExplored(entry.poi.id); location.href = '/sandbox?r=' + Date.now(); }
  else if (entry.poi.type === 'npc') { this._markExplored(entry.poi.id); UI.openSkillTree(); }
  else if (entry.poi.type === 'billboard') { this._markExplored(entry.poi.id); UI.openRpgStats(); }
  else if (entry.poi.type === 'achievements') { this._markExplored(entry.poi.id); UI.openAchievements(); }
  else if (entry.poi.type === 'spawn') { if (UI.toast) UI.toast('生灭台：这里是旅途的起点。'); }
};

OverworldGame.bindInput = function () {
  var self = this;
  this.keys = {};
  window.addEventListener('keydown', function (e) {
    var k = e.key, lk = k.toLowerCase();
    if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].indexOf(k) >= 0) e.preventDefault();
    if (lk === 'e') self.ePressed = true;
    self.keys[lk] = true;
    if (k === '+' || k === '=') self.zoomBy(1.35);
    else if (k === '-' || k === '_') self.zoomBy(1 / 1.35);
  });
  window.addEventListener('keyup', function (e) { self.keys[e.key.toLowerCase()] = false; });
  window.addEventListener('wheel', function (e) {
    if (UI && UI.isModalOpen()) return;
    if (e.deltaY > 0) self.zoomBy(1 / 1.18); else if (e.deltaY < 0) self.zoomBy(1.18);
    e.preventDefault();
  }, { passive: false });
};

OverworldGame.zoomBy = function (f) {
  if (!this.cam) return;
  this.cam.zoom = Math.max(0.5, Math.min(4.2, this.cam.zoom * f));
  this._updateView(); this._updateFrame();
};

OverworldGame.focusRealm = function (realm) {
  if (!realm || !this.pois) return;
  var target = null;
  this.pois.forEach(function (m) { if (!target && m.poi.type === 'realm' && m.poi.realm === realm) target = m; });
  if (!target) return;
  if (target.emoji) { target.emoji.classList.add('realm-focus'); setTimeout(function () { target.emoji.classList.remove('realm-focus'); }, 1600); }
};

// 组装场景（在 GPU 就绪后）
OverworldGame.buildScene = function () {
  var self = this;
  this._fetchGamesAndState();
  this._ready.then(function () {
    if (UI) UI.setLoadingProgress(82);
    self._buildPoisOverlay();
    self.initMinimap(); // POI 就绪后重建小地图标点
  }).catch(function () { });
  this._ready.then(function () {
    setTimeout(function () { if (UI) UI.setLoadingProgress(100); if (UI) UI.removeLoading(); }, 1200);
  });
};

OverworldGame.start = function () {
  var self = this;
  this.setupCamera();
  this.bindInput();
  // 设备丢失恢复守卫：页面真正卸载时标记，避免在关闭/跳转时触发自动重载。
  // （unload 事件在新版 Chrome 被 Permissions Policy 禁用，pagehide/beforeunload 足够）
  this._unloading = false;
  this._recoveryScheduled = false;
  try {
    var mark = function () { self._unloading = true; };
    window.addEventListener('pagehide', mark);
    window.addEventListener('beforeunload', mark);
  } catch (e) {}
  // 智能画质：dprCap + renderScale（未设置档位时默认 high）
  if (!this._quality) this.setQuality('high');
  this._applyRenderScale();
  // ⚠ v2.4 修复（画质档位"静默失效"根因 #2）：
  //   _applyRenderScale() 已按当前档位算出正确的 _dpr（renderScale × dprCap），
  //   但此前此处没有再调用 _ensureSize()，于是 RT 一直停留在 _initGPU 首次构建时的
  //   满分辨率（彼时 _dpr 尚未被设置，按 1.0 计算）。结果四档画质的【渲染分辨率
  //   完全相同】，画质档位在视觉/性能上无差别（ISSUE #9）。
  //   强制重建：先清零 _rtsW 让 _ensureSize 跳过"尺寸未变"的早返回，再按新 _dpr
  //   重建所有 RT 与后处理绑定。若 _initGPU 尚未就绪（_ctx 为空），_ensureSize 内部
  //   会安全返回，真正的重建由 _initGPU 那次完成（那里也已补上 _applyRenderScale）。
  this._rtsW = 0;
  this._ensureSize();
  this._time = 0;
  var last = performance.now(), lastOverlay = 0;
  this._ready.then(function () {
    self._frameCB = 0;
    (function loop(now) {
      var dt = Math.min(0.05, (now - last) / 1000); last = now;
      self._time += dt;
      /* 渲染暂停时跳过整帧渲染（见 setRenderPaused 注释）。
         仍保留 rAF 心跳，恢复后可立即继续绘制。 */
      if (self._renderPaused) { requestAnimationFrame(loop); return; }
      self.step(dt);
      if (self.device && self._ctx && !self._destroyed) {
        // 帧循环必须与单帧渲染异常隔离：_render 里任何未捕获异常（swapchain / 管线校验失败）
        // 都不应中断 requestAnimationFrame 链，否则画布停在最后一帧、且 _frameCB 不再增长，
        // 外部自检/诊断会把它误读为「WebGPU 挂了」。
        try { self._updateView(); self._frameCB++; } catch (e) { try { console.error('[WebGPU] updateView error:', e); } catch (e2) {} }
        // 前 3 帧做严格 push/pop 配对的 validation 检查（SwiftShader/新驱动首帧易暴露问题）
        var scoped = self._frameCB <= 3;
        if (scoped) { try { self.device.pushErrorScope('validation'); } catch (e) { scoped = false; } }
        try { self._render(); } catch (e) {
          self._renderErrors = (self._renderErrors || 0) + 1;
          if (self._renderErrors <= 3) { try { console.error('[WebGPU] render error:', e); } catch (e2) {} }
        }
        try { self._updateOverlay(); } catch (e) { }
        if (scoped) self._checkScope('frame' + self._frameCB, null);
      }
      requestAnimationFrame(loop);
    })(performance.now());
  }).catch(function () { });
  this.startPolling();
};

/* ── 引导 ── */
function wgpuBoot() {
  if (window.__owWgpuBooted) return; // 幂等：模块可能被分发器与 DOMContentLoaded 双通道触发
  window.__owWgpuBooted = true;
  var game = window.OverworldGame = OverworldGame;
  try {
    if (window.OverworldUI) { window.OverworldUI.init(game); UI = window.OverworldUI; }
  } catch (e) {
    console.error('[wgpu] OverworldUI.init 失败，继续但禁用 UI 交互：', e);
  }
  if (UI && typeof UI.showLoading === 'function') UI.showLoading();
  // 补 WGPU overlay 样式
  injectStyles();
  fetch('/api/overworld/config').then(function (r) { return r.json(); }).then(function (ow) {
    if (!ow || !ow.world) {
      if (UI && typeof UI.showError === 'function') UI.showError('大陆配置加载失败，请重试');
      return;
    }
    if (UI && typeof UI.setLoadingProgress === 'function') UI.setLoadingProgress(30);
    game.bootstrapGeometry(ow);
    requestAnimationFrame(function () { requestAnimationFrame(function () {
      if (UI && typeof UI.setLoadingProgress === 'function') UI.setLoadingProgress(78);
      game.buildScene();
      game.initMinimap();
      game.start();
    }); });
    if (UI && typeof UI.refreshHUD === 'function') UI.refreshHUD();
  }).catch(function (e) {
    console.error('[wgpu] 大陆配置加载失败：', e);
    if (UI && typeof UI.showError === 'function') {
      UI.showError('大陆配置加载失败，请重试');
    } else {
      try {
        var uia = document.getElementById('renderer-badge');
        if (uia) uia.textContent = '大陆配置加载失败：' + (e && e.message ? e.message : e);
      } catch (_) {}
    }
  });
}
function injectStyles() {
  if (document.getElementById('wgpu-styles')) return;
  var st = document.createElement('style'); st.id = 'wgpu-styles';
  st.textContent = '.wgpu-overlay .poi-emoji{position:absolute;text-align:center;filter:drop-shadow(0 5px 6px rgba(0,0,0,0.6));}' +
    '.wgpu-overlay .poi-emoji.realm{animation:realmPulse 2.2s ease-in-out infinite;}' +
    '.wgpu-overlay .poi-emoji.active{animation:none;filter:drop-shadow(0 0 12px #fff) drop-shadow(0 0 6px rgba(255,255,255,0.9));}' +
    '.poi-emoji.sandbox{filter:drop-shadow(0 0 14px rgba(139,92,246,0.9));}' +
    '.wgpu-overlay .realm-badge{position:absolute;color:#d4af37;font-size:15px;text-align:center;text-shadow:0 1px 3px rgba(0,0,0,0.85);}' +
    '.wgpu-overlay .realm-badge.realm-done{color:#0d7377;}' +
    '.wgpu-overlay .player-marker{position:absolute;width:0;height:0;}' +
    '.wgpu-overlay .player-shade{position:absolute;left:-20px;top:-10px;width:40px;height:12px;border-radius:50%;background:radial-gradient(ellipse,rgba(0,0,0,0.5),rgba(0,0,0,0) 70%);}' +
    '.wgpu-overlay .player-avatar{position:absolute;left:-23px;top:-44px;width:46px;height:46px;display:flex;align-items:center;justify-content:center;font-size:30px;line-height:1;color:#ffe08a;background:radial-gradient(circle,rgba(212,175,55,0.32),rgba(15,15,22,0.62) 72%);border:2px solid rgba(255,224,138,0.9);border-radius:50%;box-shadow:0 5px 16px rgba(0,0,0,0.6),0 0 20px rgba(212,175,55,0.45);transform-origin:50% 100%;animation:playerFloat 2.4s ease-in-out infinite;}' +
    '@keyframes realmPulse{0%,100%{transform:translateY(0)}50%{transform:translateY(-4px)}}@keyframes playerFloat{0%,100%{transform:translateY(0)}50%{transform:translateY(-3px)}}' +
    '.wgpu-overlay .ow-billboard{position:absolute;transform:scale(0.5);transform-origin:center bottom;pointer-events:none;}' +
    '.wgpu-overlay .ow-billboard .bb-post{position:relative;background:linear-gradient(180deg,rgba(120,74,28,.96),rgba(90,55,20,.96) 70%,rgba(60,36,14,.96));border:2px solid #c9a35c;border-radius:6px;box-shadow:inset 0 0 24px rgba(0,0,0,0.5);width:188px;min-height:260px;display:flex;flex-direction:column;align-items:center;justify-content:flex-start;padding:14px 12px;text-align:center;color:#f7ecd0;box-sizing:border-box;}' +
    '.wgpu-overlay .bb-title{font-family:var(--font-display);font-size:24px;color:#d4af37;letter-spacing:.12em;margin:4px 0 2px;text-shadow:0 2px 4px rgba(0,0,0,.7);}' +
    '.wgpu-overlay .bb-sub{font-size:11px;letter-spacing:.28em;color:#d8c9a4;margin-bottom:8px;}' +
    '.wgpu-overlay .bb-line{width:80%;height:1px;background:rgba(212,175,55,.4);margin:8px 0;}' +
    '.wgpu-overlay .bb-rows{width:100%;font-size:12px;line-height:1.6;}' +
    '.wgpu-overlay .bb-row{display:flex;justify-content:space-between;gap:8px;color:#eaddc3;padding:2px;}' +
    '.wgpu-overlay .bb-row b{color:#d4af37;font-family:var(--font-tech);}' +
    '.wgpu-overlay .bb-legend{font-size:10px;color:#bfa97e;margin-top:8px;letter-spacing:.06em;}' +
    '.wgpu-overlay .bb-pole{width:12px;height:40px;background:linear-gradient(90deg,#4c3318,#6e4a24,#4c3318);}' +
    '.wgpu-overlay .bb-base{width:66px;height:10px;border-radius:3px;background:linear-gradient(90deg,#4c3318,#6e4a24,#4c3318);}';
  document.head.appendChild(st);
  var lit = document.getElementById('iso-lighting'); if (lit) lit.style.display = 'none'; // 着色器已含暗角
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wgpuBoot);
else wgpuBoot();

/* ── 主动销毁（呈现自检失败降级时调用）：停帧循环 + 释放设备 ── */
OverworldGame.destroy = function () {
  this._destroyed = true;
  try { this.stopPolling(); } catch (e) {}
  try { if (this._onResize) window.removeEventListener('resize', this._onResize); } catch (e) {}
  try { if (this.device) this.device.destroy(); } catch (e) {}
  this.device = null; this._ctx = null;
  try { console.log('[WebGPU] 渲染器已销毁（降级）'); } catch (e) {}
};

/* ── 呈现自检（供 overworld-load.js 调用）─────────────────────────
/* ── 呈现自检（供 overworld-load.js 调用）─────────────────────────
   目的：区分「GPU 真的没画出内容」与「canvas 呈现/合成器异常」。

   ⚠ 不能用 ctx.drawImage(webgpuCanvas, ...) 作为判据。WebGPU 画布的
   drawImage 依赖交换链"可读回"能力，在 headless / SwiftShader / 软件
   光栅器下常恒定返回全黑——即便 GPU 渲染完全正常。实测：连"直接向
   swapchain 输出纯红常量"的着色器，drawImage 读回依然是 0。用它会
   把工作正常的设备误判为"呈现异常"，产生无意义的告警角标。

   做法：直接读回 GPU 侧 render target（colorRT）的真实像素。为保证
   判据本身可信，读回分两步：

     ① 哨兵探针（决定性一步）：先向 colorRT 写"左半红 / 右半绿"再读回。
        这一步的正确答案是已知常量：
        · 读回 ≠ 哨兵色 → 「读回通路」本身是坏的（格式不可拷贝 / 实现
          缺陷 / 设备丢失…），判据不可信 → 返回 true（不误杀）并打印
          raw 读数便于定位。
        · 读回 == 哨兵色 → 读回通路可信，进入第 ② 步。
        缺了这一步，「读回全黑」的两种含义（画面真黑 / 拷贝失败）无法
        区分 —— 这正是此前自检会误报的第二个来源。

     ② 实读评估：拷回 colorRT 里的真实画面，统计非零像素占比与平均
        亮度，仅在确认「全黑」时才判异常。

   全程只读 + 一次两三角形的哨兵探针；探针之后会补画一帧正常画面，
   并尊重 _renderPaused，不干扰正常渲染路径。
   返回：true=有内容或无法判定；false=确认全黑。 */

/* 哨兵探针的 WGSL：覆盖全屏的三角形，按 x 分左右两半输出红/绿。
   与主渲染管线共享 FrameUB 绑定（binding 0），直接复用 _frameLayout。
   ⚠ struct 必须与 wgpu/shaders.js 里的 FrameUB 逐字段一致（vec3f 后
   须紧跟 f32 填充，共 208 字节）；否则 uniform 尺寸校验会失败：
   "bound with size 208 ... requires a buffer binding which is at least 224"。 */
OverworldGame._PROBE_WGSL = [
  'struct FrameUB {',
  '  mvp         : mat4x4f,',
  '  camPos      : vec3f,',
  '  _p0         : f32,',
  '  sunDir      : vec3f,',
  '  _p1         : f32,',
  '  skyColor    : vec3f,',
  '  _p2         : f32,',
  '  groundColor : vec3f,',
  '  _p3         : f32,',
  '  fogColor    : vec3f,',
  '  _p4         : f32,',
  '  sunColor    : vec3f,',
  '  _p5         : f32,',
  '  res         : vec2f,',
  '  time        : f32,',
  '  zoom        : f32,',
  '  fogDensity  : f32,',
  '  exposure    : f32,',
  '  sunIntensity: f32,',
  '  aoRadius    : f32,',
  '  aoIntensity : f32,',
  '  qualityFlags: f32,',
  '}' + ';',
  '@group(0) @binding(0) var<uniform> F: FrameUB;',
  '@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4f {',
  '  var p = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));',
  '  return vec4f(p[i], 0.0, 1.0);',
  '}',
  '@fragment fn fs(@builtin(position) c: vec4f) -> @location(0) vec4f {',
  '  // 左半屏红、右半屏绿：与画质档位无关的纯位置判据',
  '  if (c.x < F.res.x * 0.5) { return vec4f(1.0, 0.0, 0.0, 1.0); }',
  '  return vec4f(0.0, 1.0, 0.0, 1.0);',
  '}'
].join('\n');

/* 哨兵探针：写入左红右绿并【在同一 encoder 内立刻拷回】。
   ⚠ 两个必须如此的理由：
   · 用【独立的 uniform buffer】，绝不碰主帧 _frameBuffer。否则 _render()
     的 _updateFrame() 会把 res 写回 cam.vw（CSS 像素），而 RT 尺寸是
     vw*_dpr —— 二者不等时左右分界落错位置（实测会退化成 83/17）。
   · 写哨兵与拷贝读回必须在同一 encoder + 同一次 submit。若分两次，
     中间让出主线程会被 rAF 帧插队覆盖掉哨兵色，读到的就不是哨兵值。 */
OverworldGame._drawProbe = function () {
  var d = this.device, W = this._rtsW, H = this._rtsH;
  if (!this._probePipe) {
    var mod = d.createShaderModule({ code: this._PROBE_WGSL, label: 'present-probe' });
    this._probePipe = d.createRenderPipeline({
      layout: d.createPipelineLayout({ bindGroupLayouts: [this._frameLayout] }),
      vertex: { module: mod, entryPoint: 'vs' },
      fragment: { module: mod, entryPoint: 'fs', targets: [{ format: 'rgba16float' }] },
      primitive: { topology: 'triangle-list' },
      label: 'present-probe'
    });
  }
  // 专用 uniform：52 floats / 208 bytes，与 FrameUB 同尺寸同布局
  if (!this._probeUB) {
    this._probeUB = d.createBuffer({ size: 208, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    this._probeUBData = new Float32Array(52);
    this._probeBG = d.createBindGroup({ layout: this._frameLayout, entries: [{ binding: 0, resource: { buffer: this._probeUB } }] });
  }
  // res = RT 真实尺寸，供片元着色器判定左右分界
  this._probeUBData[40] = W; this._probeUBData[41] = H;
  d.queue.writeBuffer(this._probeUB, 0, this._probeUBData);

  var ro = d.createTexture({
    size: [W, H], format: 'rgba16float',
    usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST
  });
  var bpr = Math.ceil(W * 8 / 256) * 256;
  var buf = d.createBuffer({ size: bpr * H, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
  var enc = d.createCommandEncoder();
  var rp = enc.beginRenderPass({ colorAttachments: [
    { view: this._views.color, loadOp: 'clear', clearValue: [0, 0, 0, 1], storeOp: 'store' }] });
  rp.setPipeline(this._probePipe);
  rp.setBindGroup(0, this._probeBG);
  rp.draw(3); rp.end();
  enc.copyTextureToTexture({ texture: this._colorRT }, { texture: ro }, [W, H, 1]);
  enc.copyTextureToBuffer({ texture: ro }, { buffer: buf, bytesPerRow: bpr, rowsPerImage: H }, [W, H, 1]);
  d.queue.submit([enc.finish()]);

  var self = this;
  return buf.mapAsync(GPUMapMode.READ).then(function () {
    var res = OverworldGame._analyzeRTReadback(new Uint16Array(buf.getMappedRange().slice(0)), W, H, bpr);
    try { buf.unmap(); } catch (e) {}
    // 哨兵色已污染 colorRT，补一帧正常画面避免残留到屏幕
    try { if (!self._renderPaused) self._render(); } catch (e) {}
    return res;
  }).catch(function (e) {
    try { if (!self._renderPaused) self._render(); } catch (e2) {}
    return { err: String(e && e.message ? e.message : e) };
  });
};

/* 把一段 rgba16float 的 bpr 对齐像素缓冲解析成统计量。 */
OverworldGame._analyzeRTReadback = function (u, W, H, bpr) {
  function h2f(h) {
    var s = (h >> 15) & 1, ex = (h >> 10) & 31, m = h & 1023, v;
    if (ex === 0) v = (m / 1024) * Math.pow(2, -14);
    else if (ex === 31) return s ? -Infinity : Infinity;
    else v = (1 + m / 1024) * Math.pow(2, ex - 15);
    return s ? -v : v;
  }
  var n = W * H, sr = 0, sg = 0, sb = 0, nonZero = 0, white = 0;
  for (var y = 0; y < H; y++) {
    var row = (y * bpr) >> 1;                     // → Uint16 下标（逐行步进，跳过行尾填充）
    for (var x = 0; x < W; x++) {
      var i = row + x * 4;
      var r = h2f(u[i]), g = h2f(u[i + 1]), b = h2f(u[i + 2]);
      sr += r; sg += g; sb += b;
      if (r > 0.001 || g > 0.001 || b > 0.001) nonZero++;
      if (r > 0.98 && g > 0.98 && b > 0.98) white++;
    }
  }
  return { W: W, H: H, r: sr / n, g: sg / n, b: sb / n,
           nonZero: nonZero / n, white: white / n, avg: (sr + sg + sb) / (3 * n) };
};

/* 把 colorRT 的当前内容拷到 CPU（只读，不改 RT）。用于第 ② 步的实读评估。
   与产生该内容的绘制分开 submit —— 此时 draw 早已完成，顺序天然正确。 */
OverworldGame._readbackRT = function () {
  var d = this.device, W = this._rtsW, H = this._rtsH;
  var ro = d.createTexture({
    size: [W, H], format: 'rgba16float',
    usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST
  });
  var bpr = Math.ceil(W * 8 / 256) * 256;
  var buf = d.createBuffer({ size: bpr * H, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
  var enc = d.createCommandEncoder();
  enc.copyTextureToTexture({ texture: this._colorRT }, { texture: ro }, [W, H, 1]);
  enc.copyTextureToBuffer({ texture: ro }, { buffer: buf, bytesPerRow: bpr, rowsPerImage: H }, [W, H, 1]);
  d.queue.submit([enc.finish()]);
  return buf.mapAsync(GPUMapMode.READ).then(function () {
    var res = OverworldGame._analyzeRTReadback(new Uint16Array(buf.getMappedRange().slice(0)), W, H, bpr);
    try { buf.unmap(); } catch (e) {}
    return res;
  }).catch(function (e) {
    return { err: String(e && e.message ? e.message : e) };
  });
};

OverworldGame.presentSelfCheck = function () {
  var self = this, d = this.device;
  if (!d || !this._colorRT || !this._views || !this._rtsW || !this._rtsH) return Promise.resolve(true);
  var W = this._rtsW, H = this._rtsH;
  if (W < 8 || H < 8) return Promise.resolve(true);
  if (this._destroyed || this._deviceLost) return Promise.resolve(true);
  var log = function (m) { try { console.log('[WebGPU] ' + m); } catch (e) {} };
  var warn = function (m) { try { console.warn('[WebGPU] ' + m); } catch (e) {} };

  function evaluate(s) {
    if (s.err) { warn('呈现自检：读回失败(' + s.err + ')，判据不可信 → 保守放行'); return true; }
    var black = s.nonZero < 0.01 && s.avg < 0.002;
    var white = s.white > 0.995;
    log('呈现自检(实读): ' + W + '×' + H + ' 平均亮度=' + s.avg.toFixed(4) +
      ' 非零占比=' + (s.nonZero * 100).toFixed(1) + '%' +
      ' 全白占比=' + (s.white * 100).toFixed(1) + '%' +
      (black ? ' → 全黑，判呈现异常' : white ? ' → 全白，判呈现异常' : ' → 正常'));
    return !(black || white);
  }

  // ① 哨兵探针：先证明「读回通路」可信，否则任何读数都无意义
  return this._drawProbe().then(function (s) {
    if (!s || s.err) { warn('呈现自检：探针读回失败(' + (s && s.err) + ') → 保守放行'); return true; }
    // 期望：左半红 + 右半绿 → r≈0.5、g≈0.5、b≈0、非零≈100%
    var ok = s.r > 0.30 && s.g > 0.30 && s.b < 0.15 && s.nonZero > 0.60;
    log('呈现自检(哨兵探针): r=' + s.r.toFixed(3) + ' g=' + s.g.toFixed(3) +
      ' b=' + s.b.toFixed(3) + ' 非零=' + (s.nonZero * 100).toFixed(1) + '% → ' +
      (ok ? '读回通路可信' : '读回通路异常'));
    if (!ok) {
      warn('呈现自检：哨兵色未按预期读回，读回通路不可信，无法据此判定呈现 → 保守放行');
      return true;
    }
    // ② 读真实画面（探针已补画一帧正常内容）
    return self._readbackRT().then(evaluate);
  }).catch(function (e) {
    warn('呈现自检异常，保守放行: ' + (e && e.message ? e.message : e));
    return true;
  });
};

window.OverworldGame = OverworldGame;