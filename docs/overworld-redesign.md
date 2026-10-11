# 六道大陆 · 大地图重制方案

> 状态：**已落地**（2026-10-11）
> 目标读者：后续维护者

---

## 1. 为什么必须重做

原网页版的大地图（`legacy-web/hub/overworld-wgpu.js` + `overworld-melonjs.js`）
在生产环境中**跑不通**。`legacy-web/docs/overworld/RENDERING_REPORT.md` 记录了根因：

| 问题 | 表现 |
|:--|:--|
| **数据契约断裂** | WebGPU 主通道的 `bootstrapGeometry()` 仍在读取已被删除的旧字段（`water_overlays` / `river_snow` / `solid_regions`），渲染的是旧大陆或残缺大陆 |
| **通道优先级错误** | 呈现自检「检测到全黑/全白就降级」会在缺陷驱动/无头环境里**误杀正常工作的 GPU** |
| **依赖不可控** | WebGPU 需要浏览器 + 适配器 + 着色器编译；无 WebGPU 时降级到 melonJS 兜底，而兜底通道又是另一套代码 —— 双份实现、双份缺陷 |
| **一次性预渲染** | melonJS 通道把整张 3584×2688 画布离屏渲染，内存与首屏成本固定 |

用户诉求是「用 Unity 的功能/拓展进行全面重制」，因此本方案不是修复 WebGPU，而是**换掉整条管线**。

---

## 2. 方案对比与选型

| 方案 | 结论 |
|:--|:--|
| 在 Unity 里重建 WebGPU 等价管线（compute 高度场 + SSAO/Bloom + 实例化装饰） | ❌ 工作量巨大、跨平台不可控、把 web 的复杂度原样搬过来 |
| **URP 2D Tilemap + 等距投影 + 自定义着色** | ✅ **采用**。性能可预测、跨平台确定、可增量扩展 |
| 用 `Tilemap` 的正交四方向模式（贴 web 版） | ❌ 视觉仍是俯视贴图，"2.5D 等距"的定位没兑现 |

---

## 3. 落地产物

### 3.1 数据层（`Assets/ChessSage/Core/Overworld/`）

**关键决策：直接复用 legacy 的 `baked` 数据，不重写生成器。**
`Configs/global/overworld.json` 的 `baked` 字段已是高质量成果（种子 20260828 确定性生成，
含海岸线 SDF、九大区域、湖泊、山脊、河流、路网），重写只会丢失迭代成果并引入不一致。

| 文件 | 职责 |
|:--|:--|
| `OverworldMap.cs` | 数据模型 + 解析。承载 terrain / region / elevation 三条行优先字节串；查询 O(1) |
| `OverworldNav.cs` | `ChunkManager`（分块窗口）、`FogOfWar`（位图迷雾）、`FlowField`（四向 BFS 流场） |

**踩过的坑（已修正并写进注释）**：`baked` 里三种字段用了**两套编码**——
`terrain`/`region` 是十进制数字字符（`'0'`~`'9'`，便于人工 diff），
`height` 是 Latin-1 原始字节（连续量，单字符表达不了 0~255）。
统一按字符码位解析会导致地形全错、高程越界。解析器现在按字段区分编码并在长度不匹配时立即抛异常。

### 3.2 表现层（`Assets/ChessSage/Presentation/Overworld/`）

| 文件 | 职责 |
|:--|:--|
| `IsoGrid.cs` | 等距投影正/逆变换 + 13 种地形调色板 |
| `TerrainAtlas.cs` | **程序化生成** 13 种地形图集（顶面 + 两个侧面），代码绘制 |
| `OverworldChunkRenderer.cs` | 16×16 分块渲染 + 对象池 + 视野窗口 + 画家算法排序 |
| `OverworldPlayer.cs` | 格子插值移动（四向）+ 屏幕→网格方向逆投影 |
| `OverworldCamera.cs` | 平滑跟随 + 地图边界钳制 + 缩放 |
| `OverworldFog.cs` | 迷雾位图 → 单Sprite，差分更新 |
| `OverworldAtmosphere.cs` | 昼夜循环色调 + POI 标记层 |
| `OverworldBootstrap.cs` | 大地图组合根 |

---

## 4. 相对 web 版的实质性改进

| 维度 | web 版 | Unity 版 |
|:--|:--|:--|
| 渲染依赖 | WebGPU（+ melonJS 兜底），双通道 | **仅 URP SpriteRenderer**，无设备能力依赖 |
| 首屏/ 常驻开销 | 整图 3584×2688 画布常驻 | **仅视野内分块**（半径 2 → 5×5 块）；其余卸载回池 |
| Draw call | 离屏画布重绘 | 每块静态合批 ≈ **25 个 draw call**（视野 5×5） |
| 节点数 | DOM 覆盖层 + Canvas | 视野内约 2万 SpriteRenderer，池化复用、切块零 GC |
| 地形美术 | 图集瓦片 | 程序化生成，**无美术资源也能开箱即有画面** |
| 可测性 | 需浏览器 | 投影数学纳入 L1 回归（112×84 全图往返校验） |
| 移动手感 | `speed 160`，像素级自由移动 | 格子插值 + `SmoothStep` 缓动，**不会卡在两格之间** |

---

## 5. 关键设计决策

### 5.1 为什么是「等距」而不是 web 版的「非等距」

web 版文档明确写「移动：四向（4 邻接），非等距」。改为等距的理由：

1. **2.5D 等距是产品定位**（README 第一段即写「2.5D 等距自由探索大地图」），俯视贴图不兑现定位。
2. 等距下的格子↔像素变换仍是**纯仿射变换**，拾取与移动依然 O(1) 且精确，
   不会因为"看起来立体"而付出精度代价。
3. 高程（`baked.height`）可直接映射为 Z 偏移，无需重建碰撞网格。

### 5.2 为什么相机**不允许旋转**

等距投影下"正确的视角"是固定的俯角。允许玩家旋转会立刻破坏
「格子 ↔ 屏幕」的映射直觉—— web 版正是因为可旋转才导致方向键手感与地图错位。
需要表现纵深时用**平移**（相机上下左右跟随）而非旋转。

### 5.3 为什么屏幕方向要逆投影

等距下屏幕上/下**不再是**地图 y+1。若直接用 `Input.GetAxis("Vertical")` 驱动网格 y，
玩家按「上」会向斜下方走。`OverworldPlayer.ScreenToGridDir` 把输入向量投影到两个网格轴取主轴，
并由 `IsoGridMathTests` 的 `ScreenInputProjectionPicksDominantAxis` 锁死该规则
（含「反向输入必须得到反向方向」的断言）。

### 5.4 为什么只有外圈群山阻挡

沿用 web 版踩坑后确定的口径（`RENDERING_REPORT.md` §4.2）：水面、山体、岩浆**均不阻挡**，
只有最外 6 格 wall 阻挡。这保证了「全图可达」，避免大地图被切成孤岛。
`OverworldTests.Nav_BlockedByOuterWallOnly` 把这条口径固化为断言。

### 5.5 为什么玩家不做自由模拟

格子插值而非像素级自由移动，带来三个确定性收益：
与离散地图严格对齐（POI 交互无需容差）、天然可打断（作弊机制的「强制移动」可复用同一接口）、
掉帧时用 `deltaTime` 补步不会卡在两格之间。

---

## 6. 已知限制

| 限制 | 影响 | 后续 |
|:--|:--|:--|
| 装饰物（植被/河流/桥梁）未实现 | 画面偏素| P2：Poisson-disk 团簇 + 样条河流 |
| 无地形过渡（硬边拼接） | 观感一般 | P2：Tilemap 瓦片过渡规则或自定义着色器 |
| 昼夜仅作用于顶点色 | 无光照方向 | P3：接 URP 2D 光照 |
| 迷雾为「已探索/未探索」二态 | 无动态视野 | P3：加「当前可见」独立位图 |
| 尚未在真实 Unity 中运行验证 | 渲染结果未知 | **P0：本地执行 L3** |

---

## 7. 扩展指引

新增地形种类只需三步：
1. 在 `Core/Overworld/OverworldMap.cs` 的 `TerrainKind` 加枚举值（**追加到末尾**，不要插入中间，
   因为枚举值即 `baked` 数据的字码）。
2. 在 `TerrainPalette` 的 `Base` 数组尾部加一个颜色。
3. 如需程序化细节，在 `TerrainAtlas.Build` 的 `Speckle` 调用处加一条规则。

新增装饰物：在 `OverworldChunkRenderer.BuildChunk` 中按 `TerrainKind` 追加装饰 Renderer，
并复用现有的对象池与 sortingOrder 约定（`baseOrder = (x+y)*4`，侧面 `+1`，顶面 `+2`）。