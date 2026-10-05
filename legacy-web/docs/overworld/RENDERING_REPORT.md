# 六道大陆 · 大地图渲染情况报告

> 版本：v3.1 ｜ 日期：2026-09-13 ｜ 项目：棋圣·六道轮回（CHessGAme）
> 配套架构文档：[`../WebGPU渲染架构.md`](../WebGPU渲染架构.md) ｜ 地图设计：[`overworld_landscape_design.md`](overworld_landscape_design.md)

---

## 一、一句话结论

大地图（六道大陆）当前的生产渲染架构是 **「WebGPU 主通道 + melonJS 兜底」**，由 `overworld-load.js` 智能分发。**WebGPU 是真实的 GPU 管线（compute 高度场 + 实例化装饰 + SSAO/Bloom/体积光/ACES），不是 CSS**。用户提到的"WebGPU 应该用 CSS 渲染"实际指的是**已废弃的 `iso-engine`（CSS 3D Transform）**——它只用于本地开发验证，不进入生产分发。

本次改动修复了一个关键问题：**上一轮地图重制只让 melonJS 消费了新地图数据，而 WebGPU 主通道仍在读取已被删除的旧字段，导致它渲染的是旧大陆（或视觉残缺），在部分环境下表现为"WebGPU 被回退 / 只给回退方案做了地图"**。同时按需求移除了大地图的全部障碍阻挡、并把 WebGPU 的优先级提到"永不主动降级"的位置。

---

## 二、三套渲染框架 / 方案对比

| 方案 | 文件 | 渲染技术 | 在生产分发中的角色 | 状态 |
| --- | --- | --- | --- | --- |
| **WebGPU 渲染器** | `hub/overworld-wgpu.js` + `hub/wgpu/shaders.js` | 真·GPU：compute 着色器高度场、实例化装饰、多光源前向、SSAO/Bloom/体积光/ACES 合成、Worker 构建、DOM 覆盖层与 GPU 同 mvp 投影 | **主通道**（有 WebGPU 时首选） | ✅ 本次修复后正常消费新地图 |
| **melonJS 渲染器** | `hub/overworld-melonjs.js` + `hub/vendor/melonjs/` | WebGL/Canvas 兜底：图集瓦片离屏预渲染 + DOM 覆盖层 | **兜底通道**（无 WebGPU / 初始化异常时） | ✅ 移除障碍后正常 |
| **iso-engine（CSS）** | `hub/overworld-iso.js` + `hub/vendor/iso-engine/` | **CSS 3D Transform** 渲染整片大陆 | **仅 `_dev_server.py` 本地验证用，不进生产分发** | 🗄 遗留，未部署 |

> **澄清**：`overworld-wgpu.js` 第 3 行明确写着它"替换 iso-engine 的 CSS 3D 阶段"。所以"WebGPU 用 CSS 渲染"是对历史方案（iso-engine）的印象，当前 WebGPU 主通道与 CSS 无关。生产环境里玩家看到的三维大陆、水体、山峦、植被全部由 GPU 实时生成。

---

## 三、分发与画质方案（`overworld-load.js` 智能分发器 v3）

```
玩家打开 /overworld
      │
      ├─ 1. 画质意图解析：URL ?owq= → localStorage → 'auto'
      ├─ 2. 设备能力检测（'auto' 时）：navigator.gpu + adapter.info
      │      + 软件渲染识别 + 核数/内存/移动端 UA → ultra/high/balanced/software
      │
      ├─ 3. WebGPU 可用 ──► import overworld-wgpu.js → setQuality(档位)
      │      ├─ 初始化异常 / 适配器缺失 / 设备丢失 → 兜底 melonJS（"明确报错"才回退）
      │      └─ 呈现自检：默认【非破坏】，仅记录告警；?owselfcheck=1 可启用破坏性降级
      └─ 4. 无 WebGPU ──► melonJS 兜底通道
```

**画质档位**（仅影响 WebGPU 主通道的后处理开关，不影响 melonJS）：

| 档位 | renderScale | SSAO | Bloom | 体积光 | 触发 |
| --- | --- | --- | --- | --- | --- |
| `ultra` | 1.0 | ✅ | ✅ | ✅ | 核数≥12 且内存≥16GB 且非软件渲染 |
| `high` | 1.0 | ✅ | ✅ | ✅ | 普通桌面 |
| `balanced` | 0.75 | ❌ | ✅ | ❌ | 移动端 / 核数≤4 / 内存≤4GB |
| `software` | 0.6 | ❌ | ❌ | ❌ | SwiftShader / llvmpipe / fallback 适配器 |

**覆盖方式**：`?owq=ultra|high|balanced|software` ＞ `localStorage.chesssage_ow_quality` ＞ 自动检测。

---

## 四、本次改动清单（对照用户诉求）

### 4.1 修复"只给回退方案做了地图" + "WebGPU 被回退"

**根因**：上一轮重制把共享配置 `configs/overworld.json` 改写为新的 `baked`（地形/区域/高度/装饰）单一真相源，并改造了 melonJS 去消费它；但 `overworld-wgpu.js` 的 `bootstrapGeometry()` 仍在读取**已被删除的旧字段**（`water_overlays` / `river_snow` / `river_ridge` / `mountain_overlays` / `solid_regions`）。结果是 WebGPU 主通道渲染的是旧大陆（或视觉残缺），而用户多数时候只能看到 melonJS 兜底——也就是"只给回退方案做了地图""WebGPU 被回退"的体感来源。

**修复**：仅改造 `overworld-wgpu.js` 的 **`bootstrapGeometry()`**，把地形 flags 与 biome 改为从 `baked.terrain` / `baked.region` 推导（纯数据适配）。**WebGPU 渲染管线（网格构建、实例化、着色器、后期链）一字未改**——完全符合"保持现有 WebGPU 渲染不变"的要求。

- `baked.terrain` 字符码（0=深海 1=浅水 2=沙滩 3=草地 4=丘陵 5=山地 6=雪峰 7=熔岩 8=墙 9=冰） → flags（水面 / 山体 / 边框）
- `baked.region` 字符码（0–8 = 九大区域顺序） → biome 索引（直接对应 `BIOME_LIST`）
- 道路仍从 `ow.roads` 读取；地图外 6 格 `F.WALL` 边框保留为"地图边界"

**验证**（Node 抽取 `buildWorld` 跑真实数据，无需浏览器）：

| 项 | 结果 |
| --- | --- |
| 地形顶点网格 | 9605 顶点 / 56448 索引，索引最大值 9604 < 顶点数 ✅ 无 NaN |
| 实例化装饰 | 5374 个实例 / 42 个分块，无 NaN ✅ |
| 地形 flags 统计 | 水面 2182、山体 1140、边框 2208（恰等于 `baked` 中 '8'=墙 的数量，说明全部为边界、无内部墙）、实心 0 |

### 4.2 移除大地图障碍机制（玩家完全不被障碍阻隔，但无法走出地图外）

- **melonJS**（`_isBarrier`，`overworld-melonjs.js`）：碰撞判定从 `WALL|WATER|MOUNT|SOLID` 收紧为 **仅 `WALL`**。水面、山体、熔岩、实心区均不再阻挡玩家。
- **WebGPU**（`_isBarrier`，`overworld-wgpu.js`）：经核查本就只拦 `F.WALL` + 越界，内部无障碍，无需改动。
- **边界守卫**：两渲染器都保留"地图最外 6 格 `F.WALL` 边框 + 越界判定"，确保玩家**无法走出地图外**。
- **退役 `solid_regions`**：从 `configs/overworld.json` 删除 `solid_regions` 字段，并移除两渲染器中设置 `F.SOLID` 的代码（该标志已无任何消费者）。

### 4.3 确保 WebGPU 优先级永远高于回退（只有明确报错才回退）

`overworld-load.js` 的呈现自检（`presentSelfCheck`）原为"检测到全黑/全白即销毁 WebGPU 并降级 melonJS"。这会在缺陷驱动/合成器/无头环境下把**工作正常的 GPU 误杀**，正是"被回退"的体感之一。本次改为：

- **默认【非破坏】**：即便自检发现画面异常，也**保持 WebGPU 通道**，仅打角标告警（"渲染：WebGPU · <档> （自检告警）"），绝不主动降级。
- **只在"明确报错"时回退**：初始化抛异常、`navigator.gpu` 缺失、适配器为空、`device.lost`（设备中途丢失自动重载）这几类确定性错误才走 melonJS 兜底。
- **逃生口保留**：`?owselfcheck=1` 仍可手动启用破坏性降级，便于在缺陷硬件上诊断。

> 设计取舍：这放弃了"绝对不会黑屏"的保险，换取用户要求的"WebGPU 优先级永远高于回退"。在真实带 GPU 的浏览器中 WebGPU 正常工作，不会触发降级；仅极个别缺陷驱动环境可能黑屏，可用 `?owselfcheck=1` 兜底。

---

## 五、地图情况（两渲染器统一消费同一份数据）

新大陆由 `scripts/gen_overworld.py` 确定性生成（种子 20260828），写入 `configs/overworld.json` 的 `baked` 字段：

- **尺寸**：112×84 格，瓦片 16px。
- **九大区域**（与两渲染器的 biome 一一对应）：西北密林 / 北境雪原 / 东北牧场 / 西部荒原 / 中央平原 / 东部丘陵 / 西南地牢 / 南部沙漠 / 东南战场。
- **地形语义**：深海、浅水、沙滩、草地、丘陵、山地、雪峰、熔岩、冰、墙（边界）。
- **自然结构**：海岸线（Catmull-Rom 闭合 + SDF）、湖泊（≥6 处）、山脊线 + 雪线 + 垭口、河流样条（上游 1 格→下游 3–4 格）、路网（控制点平滑）、Poisson-disk 植被团簇。
- **POI（11 个）**：spawn / 6 道入口（人/畜/饿/修/天/地狱）/ 沙盒训练场 / 技能 NPC / 告示牌 / 成就殿堂。障碍移除后**全图可达**，不再有被墙围死、走不到的入口。
- **单一真相源**：melonJS 用 `baked.terrain`+`baked.height`+图集瓦片着色；WebGPU 用 `baked.terrain`→flags、`baked.region`→biome、`ow.roads`。两者现在渲染的是**同一张新大陆**。

---

## 六、验证矩阵与已知限制

| 场景 | 预期 | 结果 |
| --- | --- | --- |
| 语法校验（node --check） | 三个 JS 改动文件无语法错误 | ✅ |
| wgpu 几何构建（Node 跑 `buildWorld` 真实数据） | 网格/装饰无 NaN、索引合法 | ✅ 5374 实例 / 9605 顶点 |
| melonJS 障碍移除 | 内部无障碍、边界仍拦 | ✅ `_isBarrier` 仅 `F.WALL` |
| JSON 配置 | `solid_regions` 已删、baked 完整 | ✅ |
| 真机 WebGPU 呈现 | 角标"渲染：WebGPU · <档>"，新大陆可见 | 需在带 GPU 的浏览器实测 |
| 缺陷驱动/无头环境 | 自检告警但保持 WebGPU；`?owselfcheck=1` 可降级 | 需实测 |

**已知限制 / 风险**

1. **真实 GPU 浏览器实测尚未在此沙箱完成**（沙箱无 GUI WebGPU）。Node 已验证数据路径与几何构建，但管线着色器/后处理的最终画面需在带 GPU 的 Chrome/Edge 中确认。
2. **熔岩/冰**在 WebGPU 下复用"水面"材质（蓝色）；熔岩仅 2 格、冰 42 格（多在远洋），视觉影响极小；保留 GPU 管线不变的取舍。
3. 放弃"绝不黑屏"保险后，极端缺陷驱动环境可能黑屏，已用 `?owselfcheck=1` 提供逃生口。

---

## 七、改动文件

| 文件 | 改动 |
| --- | --- |
| `hub/overworld-wgpu.js` | `bootstrapGeometry()` 从 `baked.terrain`/`baked.region` 推导 flags/biome（修复主通道渲染旧地图） |
| `hub/overworld-melonjs.js` | `_isBarrier` 仅拦 `F.WALL`（移除内部障碍）；删除 `F.SOLID` 设置 |
| `hub/overworld-load.js` | 呈现自检默认非破坏，WebGPU 优先级永高于回退；`?owselfcheck=1` 逃生口 |
| `configs/overworld.json` | 删除 `solid_regions` 字段（障碍机制退役） |
| `docs/overworld/RENDERING_REPORT.md` | 本报告 |
| `WebGPU渲染架构.md` | 同步 §2.3 呈现自检为"默认非破坏" |
