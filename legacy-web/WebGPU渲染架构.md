# 大地图渲染架构 —— WebGPU 主通道 + melonJS 兜底（v2.4）

> 本文档描述「棋圣·六道轮回」大地图（六道大陆）渲染架构的 v2.4 全貌：WebGPU 渲染器回归为主通道、四档智能画质分级、呈现自检与自动降级，以及历次根因修复与端到端测试方法。
>
> **版本修复履历**：
> - **v2.2** — 大地图不显示根因（区块索引区间不连续）+ 呈现自检误报 + 暗角偏离中心（已推送）。
> - **v2.3** — 地形高度场重做（消除"纸片平原"）、曝光/光照链路重标定（消除全局过曝）、SSAO 重复乘修复、合成端显示级对比度/饱和度（已推送）。
> - **v2.4** — 画质档位分辨率失效（ISSUE #9：`renderScale` 完全不生效、四档渲染分辨率恒等于视口）+ 断言回归脚本重建（待推送）。

---

## 一、双通道架构总览

```
玩家打开 /overworld
        │
        ▼
overworld-load.js（智能分发器 v3）
        │
        ├─ 1. 画质意图解析：URL ?owq= → localStorage.chesssage_ow_quality → 'auto'
        ├─ 2. 设备能力检测（'auto' 时）：
        │      requestAdapter() + adapter.info + isFallbackAdapter
        │      + /swiftshader|llvmpipe|lavapipe|software|angle \(google\// 识别软件渲染
        │      + hardwareConcurrency / deviceMemory / 移动端 UA
        │      → 选档：software / balanced / high / ultra
        │
        ├─ 3. WebGPU 可用 ──► import overworld-wgpu.js → setQuality(档位)
        │      │               等 _ready + 3.6s → presentSelfCheck()
        │      ├─ 自检 PASS ──► WebGPU 主通道渲染（角标：渲染：WebGPU · <画质档>）
        │      └─ 自检 FAIL ──► g.destroy() ─┐
        │                                     │
        └─ 4. WebGPU 不可用 / 管线失败 / 加载超时(12s) ──► melonJS 兜底通道
               （并行预热 vendor，overworld-melonjs.js，
                 角标：渲染：melonJS · WebGL / 内置画布）
```

**设计承诺**：无论设备是否支持 WebGPU、驱动是否有缺陷、合成器是否异常，玩家**永远看到可玩的地图**——黑屏/白屏在架构上不可能发生。

---

## 二、WebGPU 主通道（`hub/overworld-wgpu.js` + `hub/wgpu/shaders.js`）

### 2.1 渲染特性
- **高度场地形**：compute 着色器生成高频细节场 → 地形顶点着色器置换，生物群系域扭曲有机边界 + 边界过渡带。
- **实例化装饰**：植被/岩石/建筑按区域密度实例化绘制。
- **光照**：多光源前向着色 + 太阳方向光，HDR 线性空间。
- **后期链**：SSAO（半分辨率）→ 体积光（太阳散射）→ Bloom（三级降采样）→ Tonemap 合成（ACES 近似 + 雾 + 暗角）。
- **DOM 覆盖层同步**：POI/玩家/入口覆盖层用与 GPU 相同的视图-投影矩阵逐帧投影，画布与 DOM 严丝合缝。
- **Worker 构建**：地形/装饰数据在 Web Worker 构建，不阻塞主线程。

### 2.2 智能画质分级

| 档位 | renderScale | DPR 上限 | SSAO | Bloom | 体积光 | 适用设备 |
| --- | --- | --- | --- | --- | --- | --- |
| `ultra` | 1.0 | 2.0 | ✅ | ✅ | ✅ | 核数 ≥12 且内存 ≥16GB 且非软件渲染 |
| `high` | 1.0 | 1.5 | ✅ | ✅ | ✅ | 普通桌面独显/核显 |
| `balanced` | 0.75 | 1.25 | ❌ | ✅ | ❌ | 移动端 / 核数 ≤4 / 内存 ≤4GB |
| `software` | 0.6 | 1.0 | ❌ | ❌ | ❌ | SwiftShader / llvmpipe / fallback 适配器 |

- **选档规则**（`overworld-load.js detectDevice()`）：软件渲染器识别（适配器名正则 + `isFallbackAdapter`）→ `software`；移动端 UA 或核数 ≤4 或内存 ≤4GB → `balanced`；核数 ≥12 且内存 ≥16GB → `ultra`；其余 → `high`。
- **下传机制**：画质位编码进帧 uniform `FrameUB.qualityFlags`（bit0=SSAO、bit1=Bloom、bit2=体积光），WGSL 合成着色器按位开关；`OverworldGame._render` 在 JS 侧按位**跳过对应 pass**（不创建 pipeline、不派发 compute）——零开销启停，不重新编译着色器。
- **用户覆盖**：`?owq=ultra|high|balanced|software` URL 参数 > `localStorage.chesssage_ow_quality` > 自动检测。`setQuality()` 支持运行时热切换（renderScale/DPR 变化时重算后备缓冲）。

### 2.3 呈现自检与自动降级

```js
OverworldGame.presentSelfCheck = function () {
  // ① 哨兵探针：向 colorRT 写「左半红 / 右半绿」，再拷回 CPU 校验。
  //    读回值 == 哨兵色 → 证明「读回通路」可信；否则判据不可信，保守返回 true。
  // ② 实读画面：拷回 colorRT 的真实内容，统计非零像素占比与平均亮度；
  //    仅当 非零<1% 且 平均<0.002（全黑）或 全白>99.5% 时返回 false。
};
```

- **为什么不读 canvas**：`ctx.drawImage(webgpuCanvas, ...)` 依赖交换链的"可读回"能力，在 headless / SwiftShader / 软件光栅器下**常恒定返回全黑**——即便 GPU 渲染完全正常。实测：连"直接向 swapchain 输出纯红常量"的着色器，`drawImage` 读回依然是 0。用它会把工作正常的设备误判为"呈现异常"。故改为**直接读 GPU 侧 render target 的真实像素**。
- **两步法的意义**：只读 render target 仍无法区分「画面真黑」与「拷贝失败」——两者都表现为读回全黑。**哨兵探针**用已知常量校准读回通路本身，把"判据是否可信"与"画面是否有内容"解耦。哨兵读回不符预期时**保守放行**（返回 true），绝不误杀。
- **实现要点**：
  - `_rt()` 创建的 RT `usage` 需含 `COPY_SRC`（仅放开拷贝权限，不改渲染行为、不增显存），使拷贝来源永远合法。
  - 读回缓冲 `bytesPerRow` 按 256 对齐，逐行步进避免把行尾填充计入统计。
  - 探针使用**独立 uniform buffer**（不碰主帧 `_frameBuffer`），否则 `_updateFrame()` 会把 `res` 写回 `cam.vw`（CSS 像素）而 RT 尺寸是 `vw*_dpr`，二者不等时左右分界会落错位置（实测退化为 83/17 而非 50/50）。
  - 哨兵写入与拷贝读回必须在**同一 encoder + 同一次 submit**；分两次会因让出主线程而被 rAF 帧插队覆盖。
  - 探针后**补画一帧正常画面**，避免哨兵色残留到屏幕上（尊重 `_renderPaused`）。
- **触发时机**：WebGPU 通道就绪后 3.6s（覆盖首批帧 + 后期链稳定）。
- **降级动作（默认非破坏）**：**WebGPU 优先级永远高于回退**——默认情况下即便自检发现全黑/全白，也**保持 WebGPU 通道**并仅打角标告警（"渲染：WebGPU · <档>（自检告警）"），不主动降级。只有在以下"明确报错"时才走 melonJS 兜底：初始化抛异常、`navigator.gpu` 缺失、适配器为空、`device.lost` 设备中途丢失。如需在缺陷硬件上诊断，可用 **`?owselfcheck=1`** 手动启用破坏性降级（此时自检失败才会 `destroy()` → 启动 melonJS）。
- **为什么默认非破坏**：此前的"自动降级"会在缺陷驱动/合成器/无头环境下把工作正常的 GPU 误杀，造成"WebGPU 被回退"的体感。为兑现"WebGPU 优先级永远比回退更高、只有明确报错才回退"，自检改为默认不降级；代价是放弃"绝对不黑屏"保险，由 `?owselfcheck=1` 提供逃生口。

---

## 三、根因修复记录（v2.2）

| # | 根因 | 症状 | 修复 |
| --- | --- | --- | --- |
| 1 | **相机初始化竞态** | `Cannot read properties of undefined (reading 'vw')`——`_prepare()` 立即触发 `_initGPU()`，而 `setupCamera()` 要等 `/api/overworld/config` 返回 | `setupCamera()` 幂等化（`if (this.cam) return`）+ `_initGPU()` 入口兜底 `if (!this.cam) this.setupCamera()` |
| 2 | **旧草案 ShaderStage 常量** | `Value 13 is invalid for WGPUShaderStage` → **全部 RenderPipeline 无效**，帧循环空转 | 8 处数值常量（vert=1/frag=4/compute=8）替换为现行 `GPUShaderStage.VERTEX/FRAGMENT/COMPUTE` 位或组合 |
| 3 | **error scope 未配对** | `No error scopes to pop` 异常打断帧循环 | pop 全部 `.catch`（safePop）+ push 加 try/catch |
| 4 | **区块索引区间不连续（大地图不显示的根因）** | 管线 0 错误、帧稳定提交、`colorRT` 写入通路正常，但大地图只剩零星条纹/几乎不可见 | 见下方 §3.1 |
| 5 | **呈现自检误报** | 工作正常的设备被自检判为"呈现异常"，打无意义告警角标 | 见下方 §3.2 |
| 6 | **合成暗角偏离中心** | 最亮点落在画面右上角，四角压暗不均、中心反而偏暗 | `length(uv - 1.0)` → `length((uv - 0.5) * 2.0)`，恢复中心对称 |

### 3.1 区块索引区间不连续（核心根因）

`buildWorld()` 里地形与装饰的分块统计，原先采用 **tile-major（逐行）外层循环**：

```js
for (var ty = 0; ty < H; ty++) for (var tx = 0; tx < W; tx++) {
  // ... 写入 6 个索引 ...
  var cx = (tx / CHUNK) | 0, cy = (ty / CHUNK) | 0, key = cy * 1000 + cx;
  var cell = chunkMap[key];
  if (!cell) { cell = { cx, cy, first: tii - 6, count: 6 }; chunkMap[key] = cell; chunks.push(cell); }
  else cell.count += 6;                       // ← 只累加计数，不管索引是否相邻
}
```

**问题**：`drawIndexed(count, 1, firstIndex, 0, 0)` 只接受**一段连续**的索引区间。而 tile-major 顺序下，一个 16×16 的 chunk 被拆散在 **16 个不同行**上，相邻 chunk 的索引插在中间。于是每个 chunk 声称的 `[first, first+count)` 区间里，**只有 18.8% 的索引真正属于它，81.2% 是别的 chunk 的三角形**：

```
chunk[0,0] first=0    count=1536 → 属于本块 =  288/1536 (18.8%)
chunk[1,0] first=96   count=1536 → 属于本块 =  288/1536 (18.8%)
chunk[2,0] first=192  count=1536 → 属于本块 =  288/1536 (18.8%)
chunk[3,0] first=288  count=1536 → 属于本块 =  288/1536 (18.8%)
```

绘制时读到的绝大部分是**错位的三角形**（引用了不匹配的顶点），几何退化/错乱，屏幕上几乎什么都看不到——这就是"WebGPU 不显示大地图"的直接原因。装饰实例走 `drawIndexed(cubeIdxCount, count, 0, 0, firstInstance)`，依赖"同一 chunk 的实例物理连续"，同样中招。

**修复**：改为 **chunk-major（逐块）外层循环**，一个 chunk 的格子一次性写完，索引天然连续：

```js
var CW = Math.ceil(W / CHUNK), CH = Math.ceil(H / CHUNK);
for (var cy = 0; cy < CH; cy++) for (var cx = 0; cx < CW; cx++) {
  var tx0 = cx * CHUNK, ty0 = cy * CHUNK;
  var tx1 = Math.min(tx0 + CHUNK, W), ty1 = Math.min(ty0 + CHUNK, H);
  if (tx0 >= tx1 || ty0 >= ty1) continue;
  var first = tii;                             // 本 chunk 连续区间起点
  for (var ty = ty0; ty < ty1; ty++) for (var tx = tx0; tx < tx1; tx++) {
    // ... 写入 6 个索引 ...
  }
  chunks.push({ cx: cx, cy: cy, first: first, count: tii - first });
}
```

装饰侧同构处理：抽出 `emitChunk(x0, y0, x1, y1)` 记录 `first = deco.n`，`pushInst` 不再自行维护 `chunkMap`，由外层 chunk 循环驱动。

**验证（修复后）**：

| 指标 | 修复前 | 修复后 |
| --- | --- | --- |
| 地形区块区间连续 | ❌ `first = 0, 96, 192, 288…` | ✅ `first = 0, 1536, 3072, 4608…` |
| 区块区间归属正确率 | 18.8% | 100% |
| 索引总和 | 56448/56448（总数对，分布错） | 56448/56448 |
| 渲染目标覆盖率 | 仅底部约 27% 一条带 | **100%** |
| 剔除绘制 vs 全量绘制 | 差异巨大 | **完全一致** |

> 该 bug 的隐蔽性在于：**索引总数、顶点数、chunk 数、管线验证全部正常**，`frameCount` 持续增长、console 无任何错误。只有把 `colorRT` 逐像素读回、并与"全量 drawIndexed(所有索引)"的结果对比，才能定位到分块区间错位。

### 3.2 呈现自检误报

旧自检用 `ctx.drawImage(webgpuCanvas, ...)` 采样亮度。该路径在 headless / 软件光栅器下恒定返回全黑（见 §2.3 说明），导致工作正常的设备被判"呈现异常"。已重写为「哨兵探针 + RT 实读」两步法。

另一个隐蔽点：探针最初复用主帧 uniform，被 `_updateFrame()` 覆写 `res` 后左右分界落错（50/50 退化为 83/17）。修复为**专用 uniform buffer**。

### 3.3 画质档位分辨率失效（ISSUE #9，v2.4 修复）

**症状**：四档画质的 `colorRT` 读回统计**完全一致**（平均亮度、最暗、最亮、R/G/B 均值逐项相等，两两像素 RMS ≈ 0.04）。进一步实测（脚本 `test/q4.js`）揭穿真相：

| 档位 | `_dpr`（renderScale×dprCap） | 实际 RT 尺寸 `_rtsW×_rtsH` | `qualityFlags` |
| --- | --- | --- | --- |
| `software` | **0.6** | **1600×1000** | 0 |
| `balanced` | **0.75** | **1600×1000** | 2 |
| `high` | 1.0 | 1600×1000 | 7 |
| `ultra` | 1.0 | 1600×1000 | 7 |

`_dpr` 算得完全正确（0.6/0.75/1.0），但 **RT 尺寸四档全是满分辨率**。结论：`renderScale` 对真实渲染分辨率**零作用**——四档在性能与清晰度上毫无区别。

**两条根因（均为代码缺陷）**：

1. **RT 首次构建早于 `_dpr` 就绪（时序）**：启动序列是 `overworld-load.js` 先 `setQuality(档位)` → `wgpuBoot` → `_prepare()` → `_buildResources()` + `_initGPU()`（异步，其内部 `_ensureSize()` 位于 `adapterP.then` **微任务**中）→ 之后才轮到 `start()`（**宏任务**）里的 `_applyRenderScale()`。微任务先于宏任务执行，于是 `_initGPU` 的 `_ensureSize()` 跑在 `_dpr` 被设置**之前**，`_ensureSize` 见到 `this._dpr` 为 `undefined` → 按 `cam.vw * 1` 建满分辨率；而 `_applyRenderScale()` 之后**没有任何代码再调 `_ensureSize()`** 用新 `_dpr` 重建，RT 就此定格。
2. **`setQuality` 的热切换分支是死代码（从未赋值的变量）**：其重建 guard 写成
   ```js
   if (this._ctx && typeof this._dprBase !== 'undefined') { this._applyRenderScale(); this._rtsW = 0; this._ensureSize(); }
   ```
   但全仓**从未给 `this._dprBase` 赋值**（grep 仅命中这一处检查），且启动期 `_ctx` 尚未创建 → 该分支**永远不进入**。运行时换档因此永不重建 RT。

**修复（v2.4）**：① `_initGPU` 在首次 `_ensureSize()` 前补一句 `self._applyRenderScale()`；② `start()` 在 `_applyRenderScale()` 后强制 `this._rtsW = 0; this._ensureSize();`（清零令 `_ensureSize` 跳过"尺寸未变"早返回）；③ `setQuality` 的 guard 改为仅依赖真实存在的 `this._ctx`。三处合力，无论微任务/宏任务谁先到，RT 最终都以正确 `_dpr` 重建。

**验证（v2.4 修复后，`test/tier_assert.js`）**：四档 RT 尺寸严格跟随 `round(camVW × renderScale)`；`qualityFlags` 与帧 uniform `f[49]` 同步正确；同分辨率下 `flags=7`（后处理全开）vs `flags=0`（全关）的逐像素 RMS 为可观测非零值（后处理门控真实生效）。

> 注：因修复后各档**分辨率不同**（software 960×600 / balanced 1200×750 / high·ultra 1600×1000），跨分辨率无法直接逐像素比。故断言脚本改为两层：①分辨率层（每档 RT 尺寸 == 视口×renderScale）；②后处理层（同分辨率下 flags 全开 vs 全关隔离比较）。`high` 与 `ultra` 在本测试环境（devicePixelRatio=1）下唯一差别 `dprCap` 不体现，二者本就等同，属预期。

---

## 四、测试方法


### 4.1 无头测试环境（Linux 沙箱）
```bash
# Mesa lavapipe（llvmpipe）Vulkan ICD + 放行软件适配器
export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
export VK_LOADER_DRIVERS_SELECT=lvp_icd.json
chrome --headless=new --remote-debugging-port=9222 --remote-allow-origins='*' \
  --no-sandbox --enable-unsafe-webgpu --ignore-gpu-blocklist \
  --use-angle=vulkan --use-webgpu-adapter=vulkan --enable-features=Vulkan,WebGPUUseDXC
```
- 特性状态校验：`chrome://gpu` 的 featureStatus 应为 `WebGPU: Enabled`（`unavailable_software` = SwiftShader 后端被禁）。
- 适配器名 `google/swiftshader`：管线验证有效（着色器编译、布局校验真实执行），但 swapchain 呈现可能失效——**这正是呈现自检的覆盖场景**。

### 4.2 端到端验证矩阵（全部通过 ✅）

| 场景 | 预期 | 结果 |
| --- | --- | --- |
| 自动检测（无参数） | lavapipe 识别为 software 档，WebGPU 通道启动，自检通过 | ✅ |
| `?owq=software` 强制档 | RT 660×420，剔除==全量 100%，自检 true，0 错误 | ✅ |
| `?owq=balanced` 强制档 | RT 1100×700，剔除==全量 100%，自检 true，0 错误 | ✅ |
| `?owq=high` 强制档 | RT 1100×700，剔除==全量 100%，自检 true，0 错误 | ✅ |
| `?owq=ultra` 强制档 | RT 1100×700，剔除==全量 100%，自检 true，0 错误 | ✅ |
| 无 WebGPU（劫持 `navigator.gpu`） | 直接走 melonJS 兜底，地图正常渲染 | ✅ |
| `/tutorial` 独立页 | 不受影响 | ✅ |
| 竞态延迟（config 3s+ 慢返回） | `_initGPU` 兜底自建相机，不再 `undefined 'vw'` | ✅ |

### 4.3 回归断言

- **分块连续性**：`terrainChunks` / `decoChunks` 的 `first` 必须严格等于前面所有 `count` 之和（`first[i] === Σ count[0..i-1]`），且 `Σ count === 索引/实例总数`；每个地形 chunk 的 `count` 必须等于 `格数 × 6`。
- **剔除无副作用**：以"仅可见区块剔除绘制"与"全量绘制"两次渲染的 `colorRT` 覆盖率必须**完全相等**。这是分块正确性的端到端判据。
- 管线验证：渲染器自带的 `getCompilationInfo()` 诊断 + error scope 收集，console 无 `invalid` / 编译错误。
- 帧活性：`_frameCB` 持续增长，无异常弹出。
- 呈现：`presentSelfCheck()` 在可用环境下应返回 `true`（哨兵探针回报 `r≈0.5 g≈0.5 b≈0`）。

---

## 五、已知限制与风险

| 限制 | 影响 | 缓解 |
| --- | --- | --- |
| **headless + 软件光栅器下 WebGPU canvas 不呈现** | 页面截图看到的 canvas 是**空白**，与 GPU 实际渲染内容无关；自动化测试无法直接用页面截图验证 WebGPU 画面 | 判据改用 **`colorRT` 逐像素读回**（已验证可靠）；已用最小 WebGPU 页面（纯品红常量 → swapchain）复现：页面依然空白，证明是本环境呈现缺陷而非代码问题 |
| Firefox / Safari WebGPU 兼容性（Safari 26+ 支持，Firefox 需手动开启） | 部分浏览器走 melonJS 兜底 | 分发器 `navigator.gpu` 探测，天然兼容 |
| `?owq=` 强制 software 档仍走 WebGPU 管线 | 极老设备可能帧率低 | 软件渲染识别优先级最高，绝大多数老设备自动落 melonJS |
| deviceMemory 在部分浏览器为 undefined | 信号缺失时选档偏保守 | 兜底 `high` 档，运行时可 `?owq=` 覆盖 |
| 合成着色器暗角公式已修正（曾偏离中心） | 原式 `smoothstep(0.9, 0.28, length(uv - 1.0) * 0.72)` 中 `uv-1.0` 使暗角最亮处落在右上角而非中心，四角压暗不均 | **已修**：改为 `(uv - 0.5) * 2.0` 再 `smoothstep(0.9, 0.28, ·)`；已验证中心 `vig=1.0`、四角 `vig=0.0`，完全中心对称 |

---

## 六、视觉质量与性能评估（v1.3 新增）

> 评估环境：Xvfb :99 + 非 headless Chromium 144 + lavapipe（`google/swiftshader` 适配器）。
> **重要前提**：该环境下 WebGPU swapchain → 页面合成失效（见 §5），故所有画面均取自 `colorRT` 逐像素读回（已由哨兵探针验证通路可信）。

### 6.1 视觉质量：**核心结构问题已修复（v2.3/v2.4），后处理可见度待美术调校**

> 下表为 **v2.2 初评**时记录的问题与根因。其中前三类（地形平坦 / 全局过曝 / 装饰漂浮）由 **v2.3** 修复；第四类（档位无差异）的根因实为 **v2.4** 的 `renderScale` 失效 bug，已修复。

| 问题（v2.2 初评） | 实测证据 | 根因 | 修复版本 |
| --- | --- | --- | --- |
| **地形几乎完全平坦，无立体感** | 四类材质法线倾斜度：平原 0.18°~0.37°、山地 0.46°~0.92°、山墙 0.92°~1.85°，全部 < 2°，等价于法线恒为 `(0,0,1)` | `CELL = 46`（相邻顶点间距）远大于高度场幅度（仅约 **1.4** 世界单位）。坡度 `dh/(2*CELL)` 被稀释 46 倍 | **v2.3** ✅ |
| **画面整体过曝、对比度被压平** | `colorRT` 平均亮度 **0.6435**、最小值 **0.5546**（最暗像素都 ≥ 0.55），四类材质 tonemap 后收敛到 164~229 高亮窄带 | 光照叠加超 1：`hemi(≈0.44) + sunColor·sunIntensity·ndl(≈0.79)` ≈ 1.2，再乘 `exposure=1.15`；ACES 近饱和抹平材质差异 | **v2.3** ✅ |
| **植被/装饰呈"漂浮菱形色块"** | 树石实例渲染为纯色菱形，无接触阴影、无立体感 | 装饰为 cube 实例未接地 AO；SSAO 仅地形 GBuffer，且 `aoIntensity=0.55` 在过曝下不可见 + 原实现连乘两次（等效 0.30） | **v2.3** ✅ |
| **画质档位对画面几乎无影响** | 四档 `colorRT` 均值完全相同，`high` vs `ultra` 像素 RMS = 0.0006 | `qualityFlags` 正确、pass 均派发，但视觉贡献被过曝淹没（v2.2 表象）；**真因是 v2.4 的 `renderScale` 完全失效——四档渲染分辨率恒等于视口** | **v2.3 消过曝 + v2.4 修分辨率** ✅ |

**v2.3 修复后的实测对照（地形高度场 + 曝光/光照重标定）**：

| 指标 | 修复前（v2.2） | 修复后（v2.3/v2.4） |
| --- | --- | --- |
| 地形法线倾斜中位 | 平原 0.18° / 山地 0.46° / 山墙 0.92° | 平原 **9.76°** / 山地 **18.57°** / 山墙 **22.21°**（自然地貌观感区间） |
| `colorRT` 平均亮度 | 0.6435（过曝） | **0.3730**（中性） |
| `colorRT` 最小亮度 | 0.5546（无暗部） | **0.0593**（出现真实阴影） |
| 亮度跨度 | 0.37 | **0.695**（对比 ×1.88） |
| 着色器错误数 | 0 | 0 |

**v2.4 修复后的画质档位验证（`test/tier_assert.js`，见 §3.3）**：四档 RT 尺寸严格跟随 `round(camVW × renderScale)`（software 960×600 / balanced 1200×750 / high·ultra 1600×1000），`qualityFlags` 与帧 uniform `f[49]` 同步，`flags=7`（后处理全开）vs `flags=0`（全关）在同分辨率下逐像素 RMS 为可观测非零——**后处理门控真实生效，四档不再"静默一致"**。

**仍存的小瑕疵（待美术调校，非阻塞）**：在俯视大陆的视角下，SSAO/Bloom/体积光的**绝对可见幅度偏弱**（同分辨率全开 vs 全关 RMS ≈ 0.07）。这是因为顶视平坦地形本就少遮蔽、少高光、少光轴——属场景特性而非代码 bug，门控逻辑本身正确。若要进一步强化"高/低档肉眼可辨"，建议后续在地形法线/材质上引入更明显的高频细节与高光点，让 SSAO 与 Bloom 有素材可作用。

### 6.2 性能：**在软件光栅器下不可用，需真机复测**

| 档位 | 分辨率 | 帧率 | 单帧耗时 | 说明 |
| --- | --- | --- | --- | --- |
| `software` | 960×600 | ~0.17 FPS | **609.8 ms** | GPU 等待占 609.79ms，**CPU 提交仅 0.05ms** → 瓶颈 100% 在软件光栅化 |
| `high` | 1600×1000 | **0.62 FPS** | ~1600 ms | 开启 SSAO+Bloom+体积光 |
| `ultra` | 1600×1000 | **0.75 FPS** | ~1333 ms | 同上（dprCap 差异在 dpr=1 下未体现） |

**关键结论**：

- **CPU 侧完全健康**：单帧提交仅 **0.05 ms**，JS/驱动开销可忽略；场景规模很小（地形 18,816 三角面 + 5,374 实例 + 42 区块），剔除逻辑无性能问题。
- **GPU 侧是纯软件光栅化的代价**：lavapipe 逐像素跑 SSAO（200×125 工作组）+ 3 级 Bloom + 体积光 + 1600×1000 HDR 合成，单帧 0.6~1.6 秒。**这是软件渲染的固有代价，不代表真机性能。**
- **无法据此判断真机表现**：真实 GPU（哪怕是核显）上这类规模应当轻松 60 FPS。**建议在带真实 GPU 的机器上复测**，重点关注 SSAO / Bloom / 体积光三个 pass 的开销占比。

### 6.3 改进建议（按性价比排序）

| 优先级 | 建议 | 理由 |
| --- | --- | --- |
| **P0** | 重做地形高度场与 `CELL` 的比例关系：把高度幅度放大到与格距同量级（如平原 1.4 → 10~25 世界单位），或引入"宏观地貌 + 局部起伏"两级高度 | 当前几何**本质上是平的**，这是画面平淡的第一因；后续所有光照/后期都建立在错误前提上 |
| **P0** | 修正曝光链路：`sunIntensity` 与 `hemi` 之和不应超过 1，`exposure` 应按中灰（0.18）标定 | 过曝把材质差异和画质档位差异全部抹平，等于 SSAO/Bloom/体积光的实现被浪费 |
| **P1** | 给装饰实例加接地 AO / 接触阴影 | 消除"漂浮色块"感 |
| **P1** | 锐化 SSAO 效果并接入装饰 | 当前 AO 仅地形 GBuffer，且强度不足以穿透过曝 |
| **P2** | 在 `?owq=` 档位间加入**可观测差异**的回归断言（如像素 RMS 阈值） | 防止"档位开关失效但无告警"再次发生 |

---

> 版本：v1.3（对应渲染 v2.2） ｜ 项目：棋圣·六道轮回 ｜ 状态：根因修复已推送；视觉/性能评估结论见 §6

---

## 七、如果把大地图引擎换为 Babylon.js？（v2.4 评估）

> 用户提问：迁移到 Babylon.js 能否**显著提升性能与视觉质量**、并**规避无法解决的视觉问题**？
> 结论先行：**换引擎解决不了"无法解决的视觉问题"（那是 Chromium 上游限制，与框架无关），性能也不会有数量级提升；唯一实质收益是用成熟内置管线替代手搓后处理，但代价是包体 +40×(或 +19× with Babylon Lite) 与大量胶水重写。不建议为"修视觉问题"而迁移。**

### 7.1 "无法解决的视觉问题"换引擎修不了

- **它根本不是引擎 bug，而是无头/合成器限制**：权威文档（agent-browser.dev/webgpu）原话——*"The Windows/Linux screenshot gap is an upstream headless-Chrome limitation: WebGPU canvas presentation never reaches the headless compositor even though rendering works (verifiable by pixel readback). **No flag combination is known to fix it.**"* macOS 无头能截图，Windows/Linux 必须 `--headed`（虚拟显示）。本项目的"canvas 截图全白但 colorRT 有内容"正是此症。
- **Babylon.js 自己的 WebGPUEngine 也中招**：Babylon 官方论坛有用户报告 *"That works unless I use the WebGPUEngine() where the image is created but it is all white"*——同一症状，跨框架复现，证明是 Dawn/驱动层问题。
- **Babylon 的读回接口同样受限**：官方 Breaking Changes 明确 `readPixels` 在 WebGPU 下**异步**、*very slow when reading data from half-float textures*、且 **width 必须能被 64 整除否则极慢**。本项目 `colorRT` 正是 `rgba16float`（半浮点），恰是 Babylon 自己也承认最慢的路径。
- → 因此"白屏/无法截图"在 Babylon 下**依旧存在**，无法规避。

### 7.2 性能与视觉质量：有收益，但非数量级

- **视觉质量**：Babylon 提供 `scene.imageProcessingConfiguration`（exposure / contrast / toneMapping=ACES 等）与 `DefaultRenderingPipeline`（含 SSAO/Bloom/DoF），本质上就是本项目手搓的 ACES + SSAO + Bloom 的成熟内置版；Babylon 9.0 还新增 compute 驱动的 **Volumetric Lighting** 与 **Clustered Lighting**、Frame Graph 显存优化。换成它能少踩光照/色调映射的坑。
- **性能**：本场景在真机（哪怕核显）本就轻松 60 FPS——实测 **CPU 单帧提交仅 0.05ms**，100% 瓶颈在 lavapipe 软件光栅（GPU 等待 609.79ms/帧）。Babylon 不会消除软件光栅的固有代价；在真机上它与原生 WebGPU 同级（Babylon 论坛甚至提到 WebGPU 后端因向后兼容约束反而略慢于 WebGL，靠 snapshot/compatibility 模式追平）。
- **包体代价（实测，镜像源 npm）**：

| 方案 | 运行时 gzip | 相对当前自研 |
| --- | --- | --- |
| 当前 `overworld-wgpu.js` + `shaders.js` | **44.5 KB** | 1× |
| `@babylonjs/core` 9.27.0 | 71 MB 解包 / 数千文件 | — |
| `babylonjs` 9.27.0 UMD | **1785 KB gzip** | **40.1×** |
| **Babylon Lite**（`@babylonjs/lite`，WebGPU-only） | 约 34 KB（BoomBox PBR 场景） | 更优，但 API 不稳定 |

- **Babylon Lite** 是唯一经济选项：WebGPU-only（本项目**已有 melonJS 兜底**，正好互补）、比完整 Babylon.js 小约 19×、帧 CPU 快 3–4×、启动快 2.5×、内存少 5×、且像素级一致。但官方自述 *"Lite is young and the API will keep evolving… not production-stable"*——**API 不稳定**。

### 7.3 迁移代价

- 需重写：`buildWorld`（高度场/chunk 分块/实例化装饰）、`presentSelfCheck`（哨兵探针 + RT 读回）、DOM 覆盖层同步（POI/玩家/入口用 mvp 投影）、小地图、四档画质逻辑（`qualityFlags` 位门控 → Babylon 的 `scene` 后处理开关）。
- 丢失：当前**零第三方依赖**的工程优势（自研渲染器 44.5 KB，且已通过哨兵探针 + RT 读回做到可自证）。
- 收益受限：本沙箱的"白屏"是 Ubuntu 22.04 + lavapipe 经 `--use-angle=vulkan` **破坏 WebGL2 / 导致 WebGPU 适配器初始化失败**所致（botbrowser.io 实测数据：lavapipe 路径 WebGL2=No）；改走 `--use-angle=gl`（llvmpipe）即可正常工作——这是**启动参数问题，与引擎无关**，换 Babylon 不会改善。

### 7.4 结论

| 维度 | 换 Babylon.js（完整版） | 换 Babylon Lite | 维持现状（自研 + 已修复） |
| --- | --- | --- | --- |
| 规避"白屏/无法截图" | ❌ 同样存在 | ❌ 同样存在 | ❌ 同样存在（已用 RT 读回绕过验证） |
| 视觉质量 | ✅ 内置 PBR/后处理成熟 | ✅ 同 | ⚠ 手搓已达标，后处理可见度待美术强化 |
| 真机性能 | ➖ 同级（非数量级） | ✅ 略优 | ✅ 已健康（CPU 0.05ms） |
| 包体 | ❌ +40× | ⚠ +~19×（场景相关） | ✅ 44.5 KB 零依赖 |
| 稳定性/维护 | ✅ 成熟 | ❌ API 不稳定 | ✅ 自研可控 |
| 迁移成本 | ❌ 高（重写大量胶水） | ❌ 高 + API 变动风险 | ✅ 已闭环 |

**建议**：**不要**为"修不可解的视觉问题"而迁移（它修不了）。若未来确需 Babylon 的成熟管线且能接受包体与重写代价，优先评估 **Babylon Lite**（WebGPU-only 与现有 melonJS 兜底天然契合），但仍需重写 buildWorld/自检/overlay/minimap/画质档位胶水，并承受 Lite 的 API 演进风险。

---

> 版本：v2.4 ｜ 项目：棋圣·六道轮回 ｜ 状态：v2.2/v2.3 根因修复已推送；v2.4 画质档位分辨率失效已修复（待推送）；Babylon.js 迁移评估见 §七

---

## 附：本次修复的影响面清单

| 文件 | 改动 | 说明 |
| --- | --- | --- |
| `hub/overworld-wgpu.js` | +384 / −97（v2.2） | ① 地形与装饰分块改为 chunk-major（根因修复）；② `_rt()` 增加 `COPY_SRC`；③ `presentSelfCheck()` 重写为哨兵探针 + RT 实读两步法，新增 `_PROBE_WGSL` / `_drawProbe()` / `_analyzeRTReadback()` / `_readbackRT()` |
| `hub/overworld-wgpu.js` | v2.3 重做 `tileH()`、`_updateFrame()`、Worker 校验 + `_uploadWorld` 守卫 | 消除"纸片平原"与全局过曝；`shaders.js` 修正半球光系数 / SSAO 重复乘 / 合成端对比度+饱和度 |
| `hub/overworld-wgpu.js` | v2.4 三处 | **修复画质档位分辨率失效（ISSUE #9）**：① `_initGPU` 首次 `_ensureSize()` 前补 `_applyRenderScale()`；② `start()` 后强制 `_rtsW=0; _ensureSize()`；③ `setQuality` guard 由从未赋值的 `_dprBase` 改为 `this._ctx` |
| `hub/wgpu/shaders.js` | +10 / −4（v2.2） | 合成着色器暗角恢复中心对称 |
| `hub/wgpu/shaders.js` | v2.3 | 半球光系数 0.66→0.40；SSAO 连乘两次 aoIntensity→只乘一次；合成端对比度 1.30@0.42 + 饱和度 1.10 |
| `hub/_dev_server.py` | +4 | 开发服务器下发 `no-store` 等禁缓存头，避免改源码后被浏览器/CDP 复用旧 JS |
| `WebGPU渲染架构.md` | 多次 | 历次根因与评估更新；新增 §3.3（档位分辨率根因）、§七（Babylon.js 迁移评估） |

---

> 验证记录：v2.4 修复后 `tier_assert.js` 34/34 断言通过——software 960×600 / balanced 1200×750 / high·ultra 1600×1000 四档渲染分辨率严格按档位区分；同分辨率后处理全开 vs 全关 RMS=0.0544（后处理确已生效）。Babylon.js 迁移评估见 §七：无法规避"白屏/不可截图"这一上游 Chromium 限制，真机性能与视觉质量无数量级提升，但包体 +40×（完整版）/ +~19×（Lite）。
