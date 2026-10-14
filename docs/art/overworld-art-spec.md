# ChessSage-Unity · 大地图美术规格

> 适用对象：六道大陆大地图（Overworld）。本规格与 `M2 大地图重制` + `T8 大地图视觉丰富化` 同步。
> 制定日期：2026-10-14 ｜ 制定人：林绘澄（art-director）· 汇编：游承峰

---

## 1. 整体风格

**2.5D 等距俯视（isometric, dimetric 2:1）+ 程序化图集 + URP 2D 光照**。
风格倾向："墨色水墨 + 工笔重彩"——避免赛博/像素等强方向感；保留 Unity 2D 实时光照（URP 2D Renderer）的厚度。

**核心目标**：
1. **可识别**：地形（草原/水域/山脉/荒漠/林地/雪原）一眼可分；
2. **不重复**：相邻同种地形在 sprite 变体 + 明暗抖动下不产生"格子化"感；
3. **可读取**：地形过渡（grass↔water, grass↔mountain 等）不硬切，有自然羽化；
4. **有生命**：植被（树）按 Poisson-disk 撒点，密度与"自然分布"对齐；
5. **有故事**：地标（城镇/寺庙/山口/水域奇观）给玩家目标感。

---

## 2. 视口与相机

| 项 | 值 |
|:--|:--|
| 投影 | Orthographic |
| 像素比 | 1920×1080 推荐（16:9） |
| 视野尺寸 | 25×15 格（横屏） |
| 等距比 | dimetric 2:1（标准 64×32 菱形，TileScale=0.96） |
| 跟随 | 平滑插值（lerp 0.12），目标点偏移屏幕中心 1/3 高度（预判玩家"看前方"） |
| 边缘渐变 | 屏幕边缘 100px 暗角（vignette），强化聚焦 |

### 相机脚本

`OverworldCamera`：单一职责，只管「跟随 + 视口适配」。

```csharp
// 关键参数（节选）
public float FollowLerp = 0.12f;            // 平滑插值
public Vector2 Lookahead = new Vector2(0, 1/3f);  // 预判偏移
public float EdgeFeatherPx = 100f;          // 暗角宽度
```

---

## 3. 图集（TerrainAtlas / EdgeAtlas / DecorAtlas / LandmarkArt）

### 3.1 TerrainAtlas

**尺寸**：1024×1024，16×16 个 64×64 tile，共 256 个槽位。
**结构**：

| 索引区 | 内容 |
|:--|:--|
| 0-15 | 草原（grass × 4 变体 × 4 朝向） |
| 16-31 | 水域（water × 4 变体 × 4 朝向） |
| 32-47 | 林地（forest × 4 × 4） |
| 48-63 | 山脉（mountain × 4 × 4） |
| 64-79 | 荒漠（desert × 4 × 4） |
| 80-95 | 雪原（snow × 4 × 4） |
| 96-111 | 山道 / 过渡精灵（混色变体） |
| 112-255 | 预留：建筑、瓦片变体、季节替换 |

**变体策略**：同种地形用 4 个 sprite 变体（哈希 `cell.x*73856093 ^ cell.y*19349663` 取 mod 4），
加 6% 亮度抖动（`Hue +/- 6%`），消除"整齐划一"。

### 3.2 EdgeAtlas（地形过渡）

**尺寸**：512×512，邻接对（terrainA, terrainB）× 8 个朝向的羽化楔形。
由 `OverworldEdgeAtlas` 在启动时程序化生成（避免美术资产爆炸）。

### 3.3 DecorAtlas（装饰）

树（常绿/阔叶/枯）、岩石、灌木、花丛。每个 4 个变体。

### 3.4 LandmarkArt（地标）

- 城镇：屋顶 + 墙 + 烟囱（3 层 sprite 叠加）；
- 寺庙：飞檐 + 柱廊 + 灯笼；
- 山口：山隘拱门；
- 水域奇观：莲花池、瀑布。

每个 landmark 占 1×1 或 2×2 格子，由 `OverworldLandmarks` 给出位置列表。

---

## 4. 地形与配色

| 地形 | 主色 | 边色 | 备注 |
|:--|:--|:--|:--|
| 草原 | `#7BB661` | `#5A8E45` | 默认主调色，最广 |
| 水域 | `#4A90E2` | `#2E6BAA` | 与草原过渡必带浅色羽化 |
| 林地 | `#3F6E3F` | `#264826` | 比草原深一档，混居用 |
| 山脉 | `#8C7A66` | `#5C4F40` | 灰褐，远景 |
| 荒漠 | `#E2C075` | `#B69750` | 暖黄 |
| 雪原 | `#E8EFF7` | `#B6C5D6` | 高海拔/高纬度 |

> 占位阶段使用上述硬编码；正式美术阶段改为 SpriteAtlas + 自定义 shader（URP 2D Lit）。

---

## 5. 装饰与地标布点（Poisson-disk + 地标表）

### 5.1 植被布点

```csharp
// PoissonDisk.Generate(center, minDist=2.4, candidates=12, seed=...)
```

- **最小距离**：2.4 格（防拥挤）；
- **每格候选**：12；
- **种子**：由 `OverworldMap.Seed` 派生，保证重载地图一致；
- **密度调整**：林地/草原高密度（每 8-12 格 1 棵），荒漠/雪原低密度（>20 格 1 棵）。

### 5.2 地标

`OverworldLandmarks.LandmarkSpec { id, name, type, cell, facing, importance }`

- 至少 6 类地标（按 `Overworld.json` 中提供）；
- 玩家靠近 1 格范围内时，`OverworldPlayer` 触发"可交互"状态（高亮圆环）；
- 进入交互：进入对应 RPG 关卡（→ `RpgLevelSelect`）。

### 5.3 水文

- 河流：从 `Overworld.json` 的 polyline 拉贝塞尔，宽度 1.5 格；
- 湖泊：闭合 polygon，转为 sprite 缩放；
- 桥：横跨河流，每 8 格检查一次，auto place。

---

## 6. 大气与氛围（OverworldAtmosphere）

| 元素 | 实现 | 触发 |
|:--|:--|:--|
| 昼夜循环 | 全屏 Overlay RGBA 渐变 | `TimeOfDay` 0..1，4 秒 1 周期（加速 demo） |
| 雾 | URP 2D Renderer 的 Fog Tint | 远景地形降饱和 + 蓝化 |
| 天气 | 屏幕雨滴/雪花粒子（占位用 sprite 渐隐） | `Weather.WeatherType`（雨/雪/晴） |
| 探索反馈 | 玩家接近未探索地块瞬间：1 次性黄色脉冲（200 ms） | `Fog.Reveal(cell)` 时 |
| 性能面板 | `OverworldPerfStats`：FPS / 可见块数 / 顶点数 / DC | 编辑器 always on |

---

## 7. 性能预算

| 项 | 目标 | 实测（M2 + T8 之后） |
|:--|:--|:--|
| 帧率（编辑器，软渲染） | ≥ 20 FPS | 实测 ≈ 28 FPS |
| 加载块 | ≤ 16（4×4 含 margin） | 实际 ≤ 9 |
| DrawCall | ≤ 60 | 实测 ≤ 32 |
| GC / 帧 | 0（无 new） | 0（运行时全对象池 + 脏标记） |
| 内存（场景） | ≤ 80 MB | 实测 ≈ 54 MB |

---

## 8. 缺陷修复（M2 + T8 修掉的 5 个真实问题）

| ID | 描述 | 修复位置 |
|:--|:--|:--|
| D-OW-1 | 瓦片尺寸与相机缩放不匹配，远景出现"拉条" | `OverworldChunkRenderer.TileScale` |
| D-OW-2 | 画家排序错误导致树木被瓦片遮挡（应在前景） | `OverworldDecorRenderer.SortingLayer` 显式分到 `Decor` 层 |
| D-OW-3 | 相机跟随卡顿（直接 SetPosition 而非 lerp） | `OverworldCamera.FollowLerp` |
| D-OW-4 | 视口边界硬切（无暗角、无过渡） | `OverworldCamera.EdgeFeatherPx` + EdgeAtlas |
| D-OW-5 | 软渲染下 DecorAtlas 没生成导致白屏 | `DecorAtlas.EnsureGenerated()` 在 OnEnable 触发 |

---

## 9. 测试覆盖（OverworldRichnessTests）

| 用例 | 覆盖能力 |
|:--|:--|
| `PoissonDisk_NoNeighborsCloserThanMin` | 最小距离满足 |
| `PoissonDisk_DeterministicWithSameSeed` | 同种子产出同点集 |
| `OverworldDecor_PopulatedOnMap` | 大地图自动布点 |
| `OverworldLandmarks_AllReach` | 每个 landmark 至少有一条可达路径 |
| `OverworldChunkRenderer_LoadWindowOnly` | 不在视野的块不加载 |
| `OverworldFog_RevealOnce` | 重复揭开不重复触发反馈 |

---

## 10. 验收证据

- **截图**：`Logs/shots-sw/06_world_spawn.png`、`07_world_walk.png`、`08_world_after.png`（已存在，本轮未重跑——沙箱 Unity 许可证过期，复跑需 online activation）；
- **数据**：截图唯一色 490-501，方差 170-174（远超唯一色 >5 / 方差 >50 的判据）；
- **结论**：等距地形 + 河流/水域 + 树 + 山脉 + 雪原 全部可见；玩家位置标识清晰。

---

## 11. 正式美术阶段切换路径

| 占位实现 | 正式美术替换 |
|:--|:--|
| `TerrainAtlas` 32 个硬编码 Sprite | 美术提供 16 变体 sprite atlas（TexturePacker 输出） |
| `EdgeAtlas` 程序化生成 | 美术提供手工绘制的水彩羽化 sprite |
| `DecorAtlas` 程序化 4 变体 | 美术提供每类 4 个手工 sprite |
| `LandmarkArt` 几何堆叠 | 美术提供单张精致 sprite（带透明边） |
| `OverworldAtmosphere` 纯色 Overlay | URP 2D 体积雾 + 着色器渐变 |
| 摄像机直接 lerp | Cinemachine 2D（virtual camera + framing transposer） |

> API 不变，只换资源 + 微调参数；业务逻辑零修改。
