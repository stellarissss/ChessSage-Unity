# 棋圣 · 六道轮回（ChessSage · SAMSARA）v3.1.0 · 六道大陆

> **六重棋境，一念改规。一方大陆，六道藏匿；业力为媒，规则为网，在轮回中修行，在棋局中悟道。**

以六种棋类为战斗场景、「AI 作弊改规」为核心玩法的 Roguelike 大游戏。剧情模式入口为 2.5D 等距自由探索大地图「**六道大陆**」，玩家在其中四向行走、寻找六道入口、与菩提老者兑换技能、在大陆各区域（雪原/密林/湖泊/平原/丘陵/沙漠/地牢/石林/海岸）间自由穿行。

**双入口（标题页 `hub/title.html`）**：
- **进入世界**（金色）——剧情模式，首次进入先播序章，之后直达六道大陆 `/overworld`。
- **核心玩法速览**（玉色）——**一步直入纯净象棋对局**（无剧情、无业力束缚）：点击即按需拉起沙盒象棋（`/api/lazy/start?mode=sandbox&game=xiangqi`，端口 8010）后新标签打开，最快体验"纯棋艺对弈 + 一句话让 AI 改规"的核心玩法。适合比赛演示/快速上手。

**内置玩法教程**：标题页与大地图均提供「📖 玩法教程」入口（大地图位于顶部上边栏、标题页为右下悬浮按钮），**进入大地图时首次自动弹出一次**（`localStorage` 记忆，之后可随时手动打开）。教程共 8 章：游戏总览 / 对局基础 / 业力 / 识破概率 / AI 修改（七类分类价目表）/ 技能树 / 六道与守道者 / 修行与结局。教程内容与逻辑集中在可复用模块 `hub/tutorial.js`（`window.OverworldTutorial`），标题页与大地图共用同一份内容，样式复用 `hub/style.css` 的 `.tutorial-*`。

**大陆渲染（v3 · WebGPU 优先 + 智能画质 + melonJS 兜底）**：`overworld-load.js` 作为智能分发器，按设备能力自动选择渲染通道与画质档位。**WebGPU 优先级永远高于回退**——仅在"明确报错"（无 WebGPU / 初始化异常 / 适配器缺失 / 设备丢失）时才降级 melonJS；呈现自检默认非破坏（即便画面异常也保持 WebGPU，仅打角标告警），可用 `?owselfcheck=1` 在缺陷硬件上手动降级。
- **主通道 · WebGPU 次世代管线（`overworld-wgpu.js` + `wgpu/shaders.js`）**：高度场地形（compute 生成高频细节场 + 顶点置换）、实例化装饰、SSAO（compute）、体积光 God Ray、Bloom mip 链、HDR + ACES 色调映射 + 暗角 + 抖动；DOM 覆盖层与 GPU 共用同一套 mvp 矩阵逐帧投影，严丝合缝。v2.2 修复了旧版三大缺陷：① 初始化竞态（配置接口变慢时相机未就绪 → `reading 'vw'` 黑屏，现在 `_initGPU` 前置相机兜底）；② WGSL 全部管线无效（旧草案 `visibility: 13/8/4` 常量 → 现行 `GPUShaderStage.*`）；③ error scope 未配对异常。
- **智能画质分级（设备自适应）**：加载时检测 WebGPU 适配器（fallback/软件渲染器识别）+ 硬件信号（核数/内存/移动端），自动选择四档画质——`ultra`（renderScale 1.0、DPR≤2、SSAO+Bloom+体积光全开）、`high`（DPR≤1.5 全开）、`balanced`（0.75 倍渲染、无 SSAO/体积光）、`software`（0.6 倍渲染、纯几何）；画质开关以位编码写入帧 uniform，在 compute/渲染 pass 与合成着色器三处生效（跳过即零开销）。可用 URL `?owq=ultra|high|balanced|software|auto` 或 `localStorage.chesssage_ow_quality` 覆盖。
- **呈现自检（默认非破坏）**：WebGPU 初始化成功后 3.6 秒对画面中心采样；默认仅记录告警并保持 WebGPU 通道（角标"自检告警"），**不主动降级**。唯有 `?owselfcheck=1` 开启时才会在全黑/全白时销毁设备并切换 melonJS。
- **兜底通道 · melonJS v20（`overworld-melonjs.js`）**：图集整图预渲染 + 逐帧一次 `drawImage` 切片 + 昼夜/火把/道境动态光，WebGL 优先、Canvas2D 自动兜底；仅在"明确报错"（无 WebGPU / 初始化失败 / 设备丢失）时落到此通道；`?owselfcheck=1` 下的呈现异常也会触发。
- 左上角角标实时显示当前通道与画质档位（如「渲染：WebGPU · high」/「渲染：melonJS · 内置画布」）。

**RPG 剧情系统**：主角林夜被吸入六道轮回，在棋局中直面愧疚、贪婪、本能、算计、愤怒与禅定。真心祈求会招致天道识破，五种结局等待抉择。

**对局与机制增强（v2.1）**
- **入口主推/不推荐标识**：大地图与纯净模式标题页对「象棋（人道·无限制象棋 / 纯净象棋）」标注 `★ 主推`，对「跳棋（饿鬼道 / 纯净跳棋）」标注 `⚠ 不推荐的测试`，一目了然。
- **象棋可吃子红点**：选中棋子时，除绿色可落点外，可吃子目标格以同定位的红点提示（rpg 与沙盒一致）。动物棋（纯净+沙盒）同样支持可吃子红点提示。
- **吃子逻辑原子化（可修改性）**：数据驱动的机制修饰符贯穿对局——象棋 `can_capture`（某子/某类型无法吃子）与 `eatable`/`invulnerable`（某子/某类型无法被吃）；围棋 `liberty_cap`（某色/某符号限气，气计算按封顶）与 `uncapturable`（某色/某符号不可被吃/不可断气）。落子、围、吃、气全部以可被 AI/玩家修改的结构化配置表达，随 AI 修改即时生效；`prompts.py` 同步说明，rpg 与沙盒两模式一致（默认规则下行为不变）。
- **AI 助手显示最终结果 + 改子即时刷新**：AI 助手面板显示每次指令的「最终结果」（含成功/被拦截/校验失败）；codeAI 修改棋子 `type`/`name` 后棋盘立即按最新名重绘；全棋类（rpg 与沙盒）统一修复。
- **AI 改规支持棋子外观 + 闲聊可查询棋局**：D 类界面修改支持棋子颜色/大小/字形（自定义样式注入）；E 类闲聊/查询会结合棋局实况（双方存活棋子、当前回合、胜负倾向）如实回答「局面 / 谁占优 / 预测胜负」等，不再一律拒绝。
- **规则缓存提速**：各棋类一键读最新规则/棋子/棋盘配置入内存缓存，对局、合法移动与 AI 下棋只读缓存（不再逐次读盘）；仅当玩家请求 AI 修改（任意分类且成功应用）时整体重载一次以使用最新规则。核心玩法语义不变。

---

## 一、核心玩法（剧情模式 · 六道大陆）

点击标题页「**进入世界**」即进入 2.5D 大陆大地图。玩家操控像素骑士在大陆自由四向行走（WASD / 方向键），按 **E** 与附近的六道入口 / 技能 NPC / 沙盒训练场交互。

**探索与集成**
- 标题页只保留单一「进入世界」入口，剧情模式与纯净沙盒统一收拢进大陆。
- 右下角常驻**小地图**：渲染大陆缩略图、玩家实时位置与各入口标点——未交互过的入口显示「❓」，已交互过的入口显示其对应 emoji；沙盒训练场固定直接显示，常驻醒目。探索状态写入 `localStorage`（键 `chesssage_ow_explored`），关闭游戏后仍保留。
- 大陆中央平原设有放大版的「**沙盒训练场**」，以 iso-cube 聚合多面体（阶梯塔式演武平台 + 四角立柱）渲染；按 E 进入「纯净模式选棋类」（`/sandbox`）界面，返回时直接回到大陆。

**探索架构**

- 地图尺寸 112 × 84 瓦片，等距渲染（CELL=46px），配色以区域主题色块区分景观（绿/黄绿/黄/红/黑/白/灰）。
- 九大地理区域：北境雪原、西北密林、幽邃湾、中央平原、东部丘陵、南部沙漠、西南地牢、东南石林、怒涛海岸。
- 非规则大陆形状：天然海域 / 河流 / 山脉屏障分割地域，半岛、海湾、谷地错落分布，均以色块 + 叠色映射。
- 碰撞系统：游戏允许水域 / 障碍物自由穿越，仅**地图边缘群山外障**（`wallGrid`，四周 6 格厚）与 `solid_regions` 指定区为不可通行屏障，玩家按格中心 + 半径碰撞判定。

**六道入口（大陆角落，金色呼吸光圈引导）**

| 道 | 分布 | 大陆坐标（瓦片） | 景观 |
|:--:|:----|:----------------:|:----|
| 👹 饿鬼道 | 西北密林 · 幽邃湾 | (6, 22) | 密林小径尽头，被古树环绕的海湾凹地 |
| ☸️ 天道 | 北境雪原 · 天墙脚下 | (50, 10) | 雪原中央，冰川围绕的山巅之门 |
| 🐅 畜生道 | 怒涛海岸 · 东南海角 | (106, 20) | 海岸最东端的礁石半岛 |
| ☯ 地狱道 | 西南地牢 · 熔岩裂隙 | (14, 68) | 地牢深处、山涧背角的封印门 |
| 🧠 人道 | 南部沙漠 · 绿洲南缘 | (58, 78) | 绿洲边缘，沙漠南部开阔处 |
| ⚔️ 阿修罗道 | 东南石林 · 赤岩峰 | (104, 72) | 石林深处的赤色峭壁 |

**技能兑换**：中央平原西北侧的「**菩提老者**」(56, 42) 处按 E 打开技能树，用通关获得的技能点解锁四维 12 项技能。

**沙盒训练场**：中央平原 (56, 44) 放大版的阶梯塔式演武平台——聚合多面体（基座 + 四角立柱 + 中央阶梯塔顶缀金灯）。按 E 进入纯净模式选棋类界面；该入口在小地图上固定直接显示（不参与「❓」探索）。

**玩家出生点**：中央平原 · 生灭台 (58, 46) — 距离六道入口与菩提老者均有行程，鼓励探索。

### 核心循环

```
下好棋消业 → 积累业力空间 → 花业力作弊 → 改写局势取胜 → 通关获技能点 → 下一关
                ↑                                            │
                └──────────── 胜/负都把业力溢出叠加给下一关 ────┘
```

### 一局流程

1. **关卡开始**：业力 = `max(0, 初始值 50 - 净身减免) + 本道溢出叠加`
2. **下棋消业**：吃子 / 将军 / 三连 / 翻转 / 跳子等事件减少业力
3. **作弊增业**：AI 评估作弊指令，在 1-120 区间产生业障增量
   - 评估值 ≤ 120：增加业力，可继续执行
   - 评估值 > 120：直接拦截，不予执行，业力不变
4. **溢出识破**：业力 > 安全阈值（120）的部分非线性增加本道识破概率
5. **关卡结束**：胜负都记录溢出量 = `max(0, 当前业力 - 安全阈值)`，叠加给本道下一关

---

## 二、业力系统（业障模型）

业力是**单局变量**，仅在关卡内独立生效，主页不显示。

| 参数 | 默认值 | 说明 |
|:----|:------:|:----|
| `initial_karma` | 50 | 关卡起始业力（受"净身"技能减免） |
| `karma_max` | 120 | 安全阈值；超出后识破概率非线性增长 |
| `karma_single_max` | 120 | 单次作弊业力增量上限；超出则拦截 |

### 增业（作弊）

- AI 评估作弊强度，输出 1-120 之间的整数
- E 类（闲聊 / 搞笑）固定 1 点
- D 类 8-23 / A 类 30-60 / B 类 23-53 / C 类 45-90 / C+ 类 75-120
- 强度倍数：改 1 项 ×1.0，改 2 项 ×2.0，改 3+ 项 ×3.0
- 评估值 > 单次上限：直接拦截，业力不变（等效未发生）

### 消业（下棋事件）

各棋类特定事件触发，每个事件减少对应业力：

| 棋类 | 事件类型 | 数值（点） |
|:----|:--------|:----------|
| **象棋** | capture_pawn / capture_medium / capture_rook | 8 / 15 / 25 |
|        | check / checkmate / pawn_cross / captured | 20 / 35 / 10 / 5 |
| **围棋** | capture_small / capture_large / life | 10 / 20 / 15 |
|        | captured / corner / endgame | 5 / 12 / 8 |
| **五子棋** | three / four / block_three / block_four | 10 / 20 / 8 / 18 |
|          | double_three / win | 15 / 35 |
| **动物棋** | capture_normal / capture_overrank / captured | 10 / 25 / 5 |
|          | approach / win | 12 / 35 |
| **跳棋** | jump_3 / jump_5 / home | 10 / 20 / 15 |
|        | single_move / all_home | 3 / 35 |
| **黑白棋** | flip_small / flip_medium / flip_large | 8 / 15 / 25 |
|          | corner / flipped / win | 20 / 5 / 35 |

消业数值受技能修饰符 `karma_recover_multiplier`（"业力潮汐" +30%）影响。

### 溢出叠加（章节=道）

- **章节定义**：六道轮回中的一个"道"为一个章节
- **关卡开始**：`level_karma = max(0, 初始值 - 净身减免) + 本道溢出叠加`
- **关卡结束**（胜负都执行）：`溢出叠加 = max(0, 关卡结束业力 - 安全阈值)`
- **换道**：溢出叠加清零，新章节重新从初始值开始
- **被识破**：业力锁死为 0、识破概率锁死为 0，进入"识破结局"分支（详见 §三/§十二）；通关六道后触发天道 Boss 战

### 退还机制

- 闲聊（E 类）增加 1 点业力，若 ChatAI 拒绝则全额退还
- 作弊修改失败（type=rejected/error）全额退还，业力不增加
- 单次上限拦截（karma_blocked）业力不变，等效未发生

---

## 三、识破概率系统

### 公式

```
Δ = C × O^α
```

- `O` = 业力超出安全阈值的部分（overshoot）
- `C = 0.1`（默认惩罚系数，可被"藏锋"技能降至 0.07）
- `α = 1.5`（默认非线性指数，可被"缓冲"技能降至 1.3）

### 校准

| 溢出量 O | Δ（默认 C=0.1, α=1.5） |
|:--------:|:----------------------:|
| 10 | 3.16% |
| 30 | 16.4% |
| 50 | 35.4% |
| 80 | 71.6% |
| 100 | **100%** |
| 150 | 183.7%（封顶 100%） |

**超出 100 点 = 识破概率增长 100%**。轻微透支可控，巨额透支几乎必死。

### 触发判定

每次业力溢出后，系统随机 roll（0-100），若 roll < 当前识破概率则被识破：
- **被识破**：业力锁死为 0、识破概率锁死为 0，保留全部进度（技能树/技能点/Boss 记录/已通关道/成就/记忆碎片/结局），进入"识破结局"分支；通关六道后强制触发天道 Boss 战
- **金蝉脱壳**：识破后识破概率回退到触发前的 50%，免死一次

---

## 四、技能树（4 分支 × 3 层）

技能点通过通关获得（基础 +1，无作弊通关 +2，未透支 +1，首杀 Boss +1）。所有技能效果通过 `get_skill_modifiers()` 注入业力 / 识破 / 作弊各系统。

### 业力掌控 💫

| 层 | 技能 | 效果 |
|:--:|:-----|:-----|
| T1 | 安全阈值+20 | `karma_max_bonus += 20` |
| T2a | 单次上限+30 | `karma_single_max_bonus += 30` |
| T2b | 业力潮汐 | `karma_recover_multiplier = 1.3` |
| T3a | 缓冲 | `detection_alpha = 1.3`（非线性指数下降） |
| T3b | 净身 | `initial_karma_reduction = 25`（初始业力从 50 降至 25） |

### 隐匿之术 👻

| 层 | 技能 | 效果 |
|:--:|:-----|:-----|
| T1 | 藏锋 | `detection_coefficient = 0.07` |
| T2a | 首次透支免判 | `first_overdraft_skip = True`（每局一次） |
| T2b | 连续规避 | `consecutive_avoid = True`（连续 3 回合不作弊后透支惩罚减半） |
| T3a | 金蝉脱壳 | `golden_escape = True`（被识破后 1 次复活） |
| T3b | 迷雾 | `mist_fog = True`（识破概率 > 70% 时 30% 概率 Δ=0） |

### 作弊精通 🎲

| 层 | 技能 | 效果 |
|:--:|:-----|:-----|
| T1a | 自定义棋子 | 允许 C+ 类作弊 |
| T1b | 前端修改 | 允许 D 类作弊 |
| T2a | 效率欺诈 | `efficiency_fraud = True`（30% 概率降 30%） |
| T2b | 高级规则 | 允许修改胜利条件 / 核心规则 |
| T3a | 白嫖 | 每局 1 次不消耗业力 |
| T3b | 深层作弊 | `karma_single_max_bonus += 40` |

### 六道悟道 🔮

| 层 | 技能 | 效果 |
|:--:|:-----|:-----|
| T1a | 地狱之眼 | 地狱道 / 饿鬼道作弊业力消耗 -25% |
| T1b | 天道之耳 | 天道 / 阿修罗道透支惩罚 -25% |
| T2a | 守道者之隙 | Boss 技能触发概率 -30% |
| T2b | 轮回记忆 | 每次轮回开局自带 1 个随机临时 buff |
| T3a | 超脱之种 | 清业通关后全局透支惩罚系数 -0.05 |
| T3b | 六道轮转 | 升降道时额外 1 次免费作弊机会 |

> T1 默认仅解锁 `stealth_t1`；`karma_capacity_t1` 自 v3→v4 迁移起改为手动点亮（保证初始上限恒为 120），其余需技能点解锁。

---

## 五、六道关卡

| 道 | 棋类 | 主题 | 关卡数 | 守道者 | Boss 技能 |
|:--:|:----:|:----|:------:|:------|:----------|
| ☯ 地狱道 | 黑白棋 | 痛苦·翻转 | 5 | 翻覆者 | 修改规则 30% 概率被翻转效果 |
| 👹 饿鬼道 | 跳棋 | 贪婪·无尽之路 | 5 | 饕餮者 | 每作弊 2 次额外偷改 1 次 |
| 🐅 畜生道 | 动物棋 | 愚痴·等级秩序 | 5 | 秩序者 | 高等级棋子修改业力 +20 |
| 🧠 人道 | 象棋 | 算计·平衡 | 6 | 算计者 | 无（最公平的对决） |
| ⚔️ 阿修罗道 | 围棋 | 战斗·混沌 | 6 | 狂乱者 | 作弊后 25% 概率随机规则变化 |
| ☸️ 天道 | 五子棋 | 禅定·五连登仙 | 6 | 禅定者 | 每局 3 次清除最近 1 条修改 |

### 关卡类型

- `standard`：标准对局，回合限制 20（围棋 40）
- `puzzle`：预制残局，特定目标（吃子数 / 撤离 / 覆盖率等）
- `boss`：守道者战，附加 Boss 反作弊技能

### 胜利条件类型

11 种目标：`checkmate`（将死）/ `capture_count`（吃子数）/ `turn_limit`（竞速）/ `evacuation`（撤离）/ `board_coverage`（覆盖率）/ `color_coverage`（颜色覆盖率）/ `formation`（阵型）/ `survival`（生存）/ `assassination`（刺杀）/ `escort`（护送）/ `compound`（复合条件）。

### 关卡推进

剧情模式采用「**大地图自由探索 + 道内线性关卡**」。六道入口在大地图上独立分布，玩家在任何入口均可交互进入选关弹窗。

- **道内推进**：每道由 5-6 个预制关卡组成，最后一关为守道者 Boss。进入关卡后胜利则 `levels_passed += 1`，可挑战下一关；失败不推进，可随时重试。
- **整道通关**：完成本道最后一关（Boss 战胜利），该道标记为 `completed`，自动解锁对应棋类的**沙盒模式**入口。
- **自由选关**：取消节点路径制，道内关卡以线性列表展示在选关弹窗中（完成 ✓ / 当前 ▶ / 锁定 🔒）。玩家可在大陆自由穿梭，顺序不做强制规定。
- **剧情前导（植物大战僵尸式）**：进入任一关（含 Boss）后先播放本关剧情——每关打开一张「任务面板」写明关卡**背景**与**目标**，再进入人物对白（本境反转者 vs 林夜），随后自动载入棋局对弈；胜利后返回大地图。剧情文本只读 `configs/story.json`，改对白/立绘无需改代码。

---

## 六、架构

### 整体架构

```
                    ┌───────────────────────────────┐
                    │  轮回之门（Hub） 端口 8080      │
                    │  hub/title.html                │
                    │  ├ 标题页 / 成就 / 结局        │
                    │  ├ RPG 对话 / 记忆 / Boss战    │
                    │  └ 六道大陆                     │
                    │    hub/overworld.html          │
                    │    hub/overworld-load.js(分发) │
                    │      ├ WebGPU → overworld-wgpu │
                    │      └ 兜底 → overworld-melonjs │
                    │    hub/overworld-ui.js (DOM)   │
                    └──────────────┬────────────────┘
                                   │ FastAPI
                                   │ /api/overworld/config
         ┌─────────────────────────┼─────────────────────────┐
         │                         │                         │
  ┌──────▼──────┐           ┌──────▼──────┐          ┌──────▼──────┐
  │ 人道·象棋    │           │ 天道·五子棋  │          │ 阿修罗·围棋  │
  │ xiangqi/    │           │  wuziqi/     │          │   weiqi/    │
  │ 端口 8000   │           │ 端口 8001    │          │ 端口 8002   │
  └─────────────┘           └──────────────┘          └─────────────┘
         │                         │                         │
  ┌──────▼──────┐           ┌──────▼──────┐          ┌──────▼──────┐
  │ 畜生·动物棋  │           │ 饿鬼·跳棋    │          │ 地狱·黑白棋  │
  │ dongwuqi/   │           │  tiaoqi/     │          │  heibaiqi/   │
  │ 端口 8003   │           │ 端口 8004    │          │ 端口 8005   │
  └─────────────┘           └──────────────┘          └─────────────┘
```

**六道大陆（iso-engine · 等距色块渲染）**

```
地图数据 → configs/overworld.json
├ world:  112×84 瓦片，等距渲染（CELL=30px）
├ tilesets: town / farm / battle / dungeon（Kenney CC0 atlas，地图主体改用等距色块）
├ regions: 九大区域 + 子区域（REG_COLOR 主题色块：绿/黄绿/黄/红/黑/白/灰）
├ water_overlays / river_snow / river_ridge: 非规则水域/河流（蓝色叠层）
├ mountain_overlays: 非规则山脉屏障（灰岩立方体）
├ roads: 连接 POI 的道路（米色道路格）
├ pois: 6 个 realm 入口 + 1 个 sandbox 训练场 + 1 个 skill NPC + 1 个 spawn 生灭台（DOM Overlay 标点）
├ 探索小地图: 右下角常驻（缩略图 = 区域色块 canvas，玩家点 + 各入口 ❓/emoji 标点，localStorage 持久化）
└ player: initial / speed / radius_px / interact_tiles（格中心 + 半径碰撞）
```

大陆景观物体依区域主题散布（岩石 / 草丛 / 松树 / 阔叶树 / 雪堆 / 沙丘 / 仙人掌 / 枯木 / 废墟），以 iso-cube 三面明暗呈现；光影由屏幕环境光 + 暗角（`#iso-lighting`）合成。

### 每棋类统一骨架

```
{棋类}/
├── main.py                  # FastAPI 路由（走棋 / 指令 / 配置 / 业力事件）
├── ai_orchestrator.py       # 两级 AI 编排器（意图解析 + 代码生成 + 业力拦截）
├── karma_assessor.py        # 本地业力评估 AI（与 ChatAI 并行调用）
├── chess_ai.py              # AI 走棋（Minimax + Alpha-Beta）
├── rule_engine.py           # 规则引擎（jump/ray 原语解析）
├── mechanism_engine.py      # 机制引擎（A2 类运行时机制）
├── prompts.py               # System Prompts
├── ai_config.py             # API Key 配置
├── configs/                 # 6 个 JSON 配置（全部可 AI 实时修改）
│   ├── board_state.json     # 棋盘运行时状态
│   ├── board.json           # 棋盘静态结构
│   ├── pieces_red.json      # 红方棋子定义
│   ├── pieces_black.json    # 黑方棋子定义
│   ├── rules.json           # 规则与机制
│   └── ui_config.json       # 前端 UI 配置
└── static/                  # 前端（Web Components + Shadow DOM）
```

### 灵活编码（Flexible Coding）核心

- **两级 AI 流水线**：意图解析器 → 代码生成器 → JSON Patch → Schema 校验 → 应用配置
- **规则原语**：`jump`（离散跳跃）+ `ray`（射线滑行）+ `where` 条件表达式
- **地形棋子原语**：`category: "terrain"` 支持陷阱等动态场地效果
- **机制原语**：`skip_turns` / `ai_control` / `random_moves` / `extra_turns` 等运行时机制
- **修改协议**：RFC 6902 JSON Patch + JSON Schema 校验
- **意图分类**：E（聊天）/ D（前端）/ A（机制）/ B（棋盘）/ C（规则）/ C+（新棋子）/ F（失败）

---

## 七、快速开始

### 环境要求

- Python 3.10+
- DeepSeek API Key（或兼容 OpenAI 格式的模型服务）

### 安装

```bash
cd /workspace
pip install -r requirements.txt
```

### 配置 API Key

复制模板生成配置文件：

```bash
cp config.example.json config.json
```

然后编辑根目录的 `config.json`（该文件已被 `.gitignore` 忽略，**不会被提交**）：

```json
{
  "api_key": "sk-xxxxxxxxxxxxxxxx",
  "base_url": "https://api.deepseek.com/v1",
  "model": "deepseek-flash"
}
```

`api_key` 支持两种写法，运行时经统一还原逻辑处理后**都能正常工作**：

| 写法 | 示例 | 说明 |
|:-----|:-----|:-----|
| 真实密钥 | `sk-xxxxxxxxxxxxxxxx` | 直接填写，推荐本地开发使用 |
| 混淆形式 | `sk-b317699fz4b...5911c8a0` | 含迷惑字符，静态泄露后无法直接盗用 |

还原逻辑见 `shared/ai_config.py` 顶部「密钥防泄露」章节。也可用环境变量 `DEEPSEEK_API_KEY`
覆盖配置文件（优先级最高），CI / 生产环境推荐此方式：

```bash
export DEEPSEEK_API_KEY="sk-xxxxxxxxxxxxxxxx"
```

> **安全提示**：`config.json` 与 `api密钥.txt` 均已在 `.gitignore` 中，且已从 git 跟踪中移除。
> 如果你曾在旧提交中提交过真实密钥，请前往 [DeepSeek 控制台](https://platform.deepseek.com)
> **轮换（revoke）该密钥** —— 历史记录无法通过新提交抹除。

### 启动

```bash
python main.py
```

启动后访问：

| 入口 | URL |
|:-----|:---|
| 标题页 / Hub | http://localhost:8080/ |
| **六道大陆 · 剧情模式** | http://localhost:8080/overworld |
| 人道·象棋 | http://localhost:8000/ |
| 天道·五子棋 | http://localhost:8001/ |
| 阿修罗·围棋 | http://localhost:8002/ |
| 畜生·动物棋 | http://localhost:8003/ |
| 饿鬼·跳棋 | http://localhost:8004/ |
| 地狱·黑白棋 | http://localhost:8005/ |

> **沙盒模式**（`sandbox/`，纯净对弈、无 RPG 规则）：象棋 `8010`、五子棋 `8011`、围棋 `8012`、动物棋 `8013`、跳棋 `8014`、黑白棋 `8015`。自 v1.5 起统一收拢进大陆：标题页只留「进入世界」，玩家在大陆南部的「沙盒训练场」按 E 进入纯净选棋界面，返回直达 `/overworld`。沙盒棋类由同一启动器在剧情模式之后一并拉起。

可选参数：`--browser`/`--no-window` 明确以浏览器模式启动（默认即此），`--window` 改用独立桌面窗口（pywebview），`--no-browser` 不自动打开浏览器，`HUB_PORT=8080` 自定义 Hub 端口。

### Windows 打包（PyInstaller onedir，免编译环境出 exe）

Windows 上直接产出 `棋圣.exe`（自带运行时，不含 Python 也免源依赖），无需 Nuitka：

```batch
pip install pyinstaller fastapi "uvicorn[standard]" httpx pydantic jsonschema jsonpatch
python pyinstaller\build_game.py        :: 或双击 pyinstaller\一键打包.bat
```

- spec：`pyinstaller/chesssage.spec`（入口为 `main.py`，后者内置冻结态判定与「启动器+子进程」双模式）。
- 产物 `dist/chesssage/`：`棋圣.exe` + 游戏源码/资源 + `config.json`（API Key 默认清空，自行填入）。
- PyInstaller **不支持交叉编译**——Windows 版 `.exe` 必须在 Windows 上构建（脚本跨平台，Windows 运行即出 exe）。

### 生产打包（Nuitka standalone）

```bash
# 安装打包依赖（与运行依赖分离，避免引入 rembg/PIL 等离线美术工具的重依赖）
pip install -r requirements-build.txt

# 生产打包：默认开启 LTO → 运行期最高性能；产物为 dist/棋圣/
.venv-build/bin/python nuitka_build.py --clean
```

- 产物为**多文件 standalone**（onedir）：入口 `棋圣`/`棋圣.exe` 自带 Python 3.14 运行时与全部 `.so/.pyd` 依赖，目标机器免装 Python；棋类服务由主进程以独立子进程 lazy 拉起（12 个 RPG+沙盒棋类各自独立进程、模块命名空间隔离）。
- **`--lto`**：默认开启链接期优化（生产最高性能）；如需快速出包可传 `--no-lto`（编译更快、性能略降）。
- Windows 构建：`build_windows.bat`（默认 Zig 免装 VS，详见 [docs/nuitka打包方案.md](docs/nuitka打包方案.md)）。Linux 需 `patchelf`（`apt install patchelf`）。
- 产物内 `config.json` 的 API Key 为**空**，请在游戏界面或该文件内填入自己的 Key。

---

## 八、项目结构

```
workspace/
├── main.py                      # 统一启动器 + Hub 后端（+ Overworld 路由）
├── config.json                  # API 密钥配置
├── requirements.txt
│
├── hub/                         # 六道众生总坛（轮回之门）
│   ├── title.html               # 首页（进入世界 + 核心玩法速览双入口 + 教程 FAB；/ 直出，/hub 重定向到 /overworld）
│   ├── overworld.html           # ★ 六道大陆页（渲染装载 + DOM HUD + 小地图）
│   ├── overworld-load.js        # ★ 渲染器智能分发：设备能力检测 → 画质分级 → WebGPU 优先（呈现自检）→ melonJS 兜底（无黑屏）
│   ├── overworld-melonjs.js     # ★ 大陆渲染·兜底通道（melonJS v20）：图集预渲染 + 逐帧一次 drawImage 切片 + 昼夜/火把/道境动态光 + 相机/碰撞/POI/小地图
│   ├── overworld-wgpu.js        # ★ 大陆渲染·主通道（WebGPU）：高度场 + compute 细节场 + 顶点置换 + 实例化 + SSAO/体积光/Bloom/HDR + 智能画质档位 + 呈现自检
│   ├── overworld-iso.js         # 大陆渲染（旧 iso-engine，仅保留作历史参考，默认不再启用）
│   ├── overworld-ui.js          # ★ 大陆 UI：HUD / 选关弹窗 / 技能树 / 总览 / 教程弹窗接入 / 错误遮罩
│   ├── tutorial.js              # ★ 内置玩法教程（8 章）+ 弹窗/FAB；标题页与大地图共用（window.OverworldTutorial）
│   ├── wgpu/                    # WebGPU 渲染器着色器栈（shaders.js：WGSL 管线 + 智能画质 qualityFlags）
│   ├── sandbox.html             # 纯净模式选棋类界面（由大陆沙盒训练场按 E 进入）
│   ├── sandbox.js               # 纯净棋类卡片渲染 + 状态检测
│   ├── vendor/melonjs/          # melonJS v20（ESM，本地内置，离线可用）
│   ├── _dev_server.py           # 开发用轻量静态服务器（沙盒验证，非成品）
│   ├── achievements.html        # 成就殿堂
│   ├── dialogue.html            # RPG 剧情对话系统
│   ├── dialogue.js              # 打字机/立绘/选择面板/履历·自动·隐藏逻辑
│   ├── memory_album.html        # 记忆相册
│   ├── memory_album.js          # 记忆碎片展示逻辑
│   ├── ending.html              # 结局展示
│   ├── ending.js                # 结局判定与展示逻辑
│   ├── heaven_boss.html         # 天道 Boss 战
│   ├── heaven_boss.js           # Boss 战对话与进入逻辑
│   ├── app.js                   # 前端逻辑（含 RPG 总览加载）
│   └── style.css
│
├── shared/                      # 共享模块
│   ├── assets/
│   │   └── map/                 # ★ Kenney 像素大地图瓦片
│   │       └── atlas/           # tiles_tiny-{town,farm,battle,dungeon}.png
│   ├── json_patch_utils.py      # RFC 6902 JSON Patch
│   ├── schema_validator.py      # JSON Schema 校验
│   └── achievement_checker.js   # 成就检测
│
├── samsara/                     # 六道轮回核心引擎
│   ├── api.py                   # FastAPI 路由（业力/识破/技能/关卡/进度）
│   ├── state.py                 # 轮回元状态管理（含 carryover + RPG 字段 + sandbox_unlocked）
│   ├── karma.py                 # 业力系统（业障模型）
│   ├── karma_assessor.py        # samsara 端 AI 业力评估
│   ├── detection.py             # 识破概率系统（概率判定式）
│   ├── bosses.py                # Boss 技能系统
│   ├── skills.py                # 技能树系统
│   ├── progression.py           # 关卡结算、技能点、沙盒解锁
│   ├── levels.py                # 六道线性关卡池（已移除路径制 map_* 方法）
│   ├── objectives.py            # 11 种目标判定
│   ├── turn_limit.py            # 回合限制系统
│   ├── story_api.py             # RPG 剧情 API
│   ├── choices.py               # 选择系统（alignment 变化）
│   ├── memory_fragments.py      # 记忆碎片系统
│   ├── endings.py               # 五种结局判定
│   └── heaven_boss.py           # 天道 Boss 战模块
│
├── configs/                     # 全局配置
│   ├── overworld.json           # ★ 六道大陆权威配置：区域/装饰/水域/山脉/POI/玩家
│   ├── samsara_state.json       # 轮回存档（含 RPG 字段）
│   ├── story.json               # RPG 剧情权威源
│   ├── tiandao_boss.json        # 天道 Boss 战机械配置
│   ├── boss_definitions.json    # Boss 定义
│   ├── skill_tree.json          # 技能树
│   ├── karma_events.json        # 业力事件映射（各棋类消业数值）
│   ├── level_pools.json         # 六道预制关卡
│   ├── objective_types.json     # 胜利条件 Schema
│   ├── formations.json          # 阵型定义库
│   └── puzzles.json             # 预制残局数据库
│
├── tests/                       # 单元/集成测试
│   ├── test_rules_cache.py      # 规则缓存一致性
│   ├── test_atomic_capture.py   # 原子提子（吃子）链路
│   ├── test_resize_board_b.py   # B 类棋盘缩放链路
│   ├── test_weiqi_atomic.py     # 围棋机制原语（限气 / 不可吃）
│   └── test_skill_system.py     # 技能树与业力上限（单次上限 120）
│
├── scripts/
│   └── build_map_atlas.py       # ★ Kenney 瓦片打包 → shared/assets/map/atlas/
│
├── xiangqi/                     # 人道·象棋（端口 8000）
├── wuziqi/                      # 天道·五子棋（端口 8001）
├── weiqi/                       # 阿修罗·围棋（端口 8002）
├── dongwuqi/                    # 畜生·动物棋（端口 8003）
├── tiaoqi/                      # 饿鬼·跳棋（端口 8004）
└── heibaiqi/                    # 地狱·黑白棋（端口 8005）
```

---

## 九、API

### Samsara API（挂载于 Hub `/samsara/*`）

| 方法 | 路径 | 说明 |
|:----:|:-----|:-----|
| GET | `/api/state` | 获取当前轮回状态（含 karma/detection/skill_modifiers/allowed_classifications） |
| GET | `/api/karma` | 获取业力状态（current/max/single_max/initial） |
| POST | `/api/karma/consume` | 作弊增加业力（业障模型，返回 overshoot 信息） |
| POST | `/api/karma/event` | 下棋消业事件（game_type + event_type → 自动减少业力） |
| POST | `/api/karma/refund` | 退还业力（作弊失败时全额退还） |
| POST | `/api/karma/assess` | 评估作弊业力消耗 |
| POST | `/api/karma/recover` | 旧版消业接口（兼容） |
| GET | `/api/detection` | 获取识破概率 |
| GET | `/api/skills` | 获取技能树 / 可用技能 / modifiers |
| POST | `/api/skills/unlock` | 解锁技能（消耗技能点） |
| GET | `/api/levels` | 获取当前道关卡列表与进度 |
| POST | `/api/levels/start` | 开始关卡（reset_level_state，使用 carryover） |
| POST | `/api/levels/advance` | 推进到下一关 |
| POST | `/api/levels/sandbox` | 进入沙盒模式 |
| GET | `/api/levels/realm/{realm}` | 获取指定道关卡列表 |
| GET | `/api/objectives` | 获取当前关卡目标 |
| POST | `/api/objectives/check` | 检查目标完成情况 |
| POST | `/api/turn/tick` | 推进回合 |
| GET | `/api/turn/status` | 获取回合状态 |
| GET | `/api/boss` | 获取当前道 Boss |
| POST | `/api/boss/trigger` | 触发 Boss 技能 |
| POST | `/api/progression/resolve` | 结算关卡（won/no_cheat/boss_defeated → rewards） |
| POST | `/api/progression/retreat` | 降道 |
| POST | `/api/cheat/record` | 记录作弊次数 + 触发 Boss 技能 |
| POST | `/api/detection/reset` | 被识破后重置进度 |
| GET | `/api/realms` | 获取六道列表 |
| POST | `/api/reset` | 重置轮回到地狱道第 1 关 |

### 棋类 API（每棋类独立端口）

| 方法 | 路径 | 说明 |
|:----:|:-----|:-----|
| GET | `/` | 返回棋类主页面 |
| GET | `/api/config/all` | 获取全部配置 |
| GET | `/api/config/{name}` | 获取单个配置（board_state / board / pieces_red / pieces_black / rules / ui_config） |
| POST | `/api/apikey` | 设置 API Key |
| GET | `/api/apikey/status` | 检查 API Key 状态 |
| POST | `/api/command` | 发送自然语言作弊指令（支持 dry_run=1 仅评估不写入） |
| POST | `/api/move` | 玩家走棋（触发消业事件） |
| POST | `/api/ai/move` | AI 走棋 |
| POST | `/api/valid_moves` | 获取棋子合法移动 |
| POST | `/api/reset` | 重置棋盘到初始状态（重置业力） |
| GET | `/api/logs` | 获取最近对话日志 |
| WebSocket | `/ws` | 实时事件推送（可选） |

### Hub API（端口 8080）

| 方法 | 路径 | 说明 |
|:----:|:-----|:-----|
| GET | `/` | 轮回之门首页（标题页） |
| GET | `/overworld` | **六道大陆 · 剧情模式大地图**（iso-engine 等距场景） |
| GET | `/api/overworld/config` | 六道大陆权威 JSON 配置（regions / tilesets / POI / 地形） |
| GET | `/achievements` | 成就殿堂 |
| GET | `/api/games` | 获取六棋类列表（含端口和 URL） |
| GET | `/api/health` | 健康检查 |
| GET | `/api/samsara/realms` | 获取六道映射 |
| GET | `/api/achievements` | 获取成就列表 |
| POST | `/api/achievements/unlock` | 解锁成就 |
| GET | `/api/achievements/stats` | 获取成就统计 |
| POST | `/api/achievements/stats` | 更新成就统计 |

---

## 十、作弊指令分类与价目

| 分类 | 含义 | 业力区间 | 是否需技能解锁 | 示例 |
|:----:|:-----|:--------:|:--------------:|:-----|
| E | 闲聊 / 搞笑 / 查询 | 1（固定） | 否 | "你好" / "讲个笑话" / "现在谁占优" |
| D | 界面修改 / 外观（含棋子颜色、大小） | 8-23 | 是（cheat_mastery_t1b） | "改棋盘背景颜色" / "把棋子放大、改成蓝色" |
| A | 机制修改 | 30-60 | 否 | "给对手加一个额外回合" |
| B | 棋盘变换 / 棋子位置 | 23-53 | 否 | "把我的车移到中线" |
| C | 规则修改 / 棋子走法 | 45-90 | 否 | "让我的马可以斜走" |
| C+ | 创建新棋子 | 75-120 | 是（cheat_mastery_t1a） | "创建一个能飞的狮" |
| F | 失败 / 拒绝 | 0 | - | AI 拒绝执行 |

**业力退还规则**：
- E 类被 ChatAI 拒绝：退还 1 点
- 修改失败（type=rejected/error）：全额退还
- 评估超出单次上限：直接拦截，业力不变

---

## 十一、技术栈

| 层 | 技术 |
|:---|:-----|
| **大地图前端（主通道）** | **原生 WebGPU**（Dawn）：高度场地形 + compute 细节场 + 顶点置换 + 实例化装饰 + SSAO/Bloom/体积光/HDR(ACES)；设备能力检测 + 四档智能画质（`?owq=` 可覆盖）+ 呈现自检自动降级 |
| **大地图前端（兜底）** | melonJS v20（AUTO：WebGL 优先 / Canvas2D 兜底）· 图集预渲染 + 单次 drawImage 切片 · Light2d 动态光影 |
| **UI / HUD** | 原生 HTML + CSS (DOM Overlay) · 无障碍弹窗 · A11y 焦点圈陷阱 |
| **对局前端** | 原生 Web Components + Shadow DOM |
| **大地图像素资产** | Kenney Tiny Farm / Tiny Town / Tiny Battle / Tiny Dungeon (CC0，atlas 留存、地图主体用等距色块) |
| **后端** | FastAPI + Uvicorn（多进程架构） |
| **AI** | DeepSeek（`deepseek-flash`，统一配置真源 `config.json`），两级流水线 |
| **配置修改** | RFC 6902 JSON Patch + JSON Schema |
| **存档** | JSON 文件持久化（`configs/samsara_state.json`） |
| **测试** | pytest + FastAPI TestClient；大地图 DOM-over-等距渲染用浏览器端到端验证 |

---

## 十二、RPG 剧情系统

在原有棋类 Roguelike 基础上叠加了完整的剧情 RPG 层。主角**林夜**是一名作弊成性的高中生，被吸入六道轮回后，在每道的棋局中直面自己的心魔。

### 剧情背景

林夜靠作弊赢了好友陈默无数次。某天一阵眩晕，他坠入六道轮回。在这里，他发现了更方便的作弊方式——**真心祈求**时天道会回应（对应游戏中的 AI 修改）。但每次祈求都暗藏代价：天道可能**识破**他作弊成性的本质。

### 每关剧情（33 关 · 含 6 Boss）

为让每关如同《植物大战僵尸》般在开局即讲清来龙去脉，`configs/story.json` 每个关卡均含一份独立的剧情数据，全部由 `story.json` 驱动（改对白/立绘即生效，无需碰代码）：

| 字段 | 说明 |
|:-----|:-----|
| `description` | 本关**背景**：此境现状、事件起因、反转者/引导者的话语铺垫 |
| `objective` | 本关**目标**：玩家在本局必须达成的事（剧情 + 棋理双层表述） |
| `dialogues` | 战前对白序列：`{speaker, portrait, text}`，场次按序播放 |
| `dialogues_after` | Boss 战后对白（战斗结束后继续剧情的旁白/反转者独白） |
| `choices` | 道选择（Boss 战后的 alignment 取舍） |
| `guide_whisper` | 天道低语（隐藏的映照提示） |

**进入流程**：选关 → `POST /api/levels/start` → `/dialogue?mode=level&realm=&level=&port=` → 先弹「任务面板」（背景 + 目标）→ 播放本关人物对白 → 自动进入 `/play` 棋局对弈 → 胜利后返回大地图。Boss 关则在战前对白后用独立窗口进入棋局，胜利后回对话页播放战后对白与道选择。

### 核心机制

#### 真心祈求（AI 修改的剧情化）

- 玩家使用 AI 修改（ChatAI 输出后）即视为一次"真心祈求"
- 每次祈求后，系统用当前识破概率进行**随机结算**：`random()*100 < detection` 命中即被识破
- 被识破后：识破概率锁死为 0，但识破结局路径已确定
- 通关六道后，若曾祈求过，进入隐藏的**天道 Boss 战**

#### Alignment 系统（道心倾向）

| 属性 | 说明 |
|:-----|:-----|
| `enlightenment` 悟道值 | 诚实面对、拒绝作弊时增加 |
| `corruption` 堕落值 | 逃避、沉沦、拥抱作弊时增加 |
| `rationality` 理性值 | 人道独立累计 |
| `emotion` 情感值 | 人道独立累计 |

每道 Boss 战后有"道选择"，决定 alignment 走向，最终影响结局判定。

#### 记忆碎片

- 每道全程无作弊通关（`no_cheat_full_clear`）可解锁一段林夜的过去记忆
- 集齐全部 6 个记忆碎片是**真我结局**的必要条件
- 记忆碎片在"记忆相册"页面查看

### 五种结局

| 结局 | 类型 | 条件 |
|:-----|:----:|:-----|
| 悟道结局·破茧成蝶 | good | 悟道值领先 3+ + 选悟道 + 业力<100 + 无祈求 |
| 堕落结局·永堕轮回 | bad | 堕落值领先 3+ + 选堕落 + 业力>200 + 无祈求 |
| 轮回结局·继续修行 | neutral | 悟道≈堕落 + 选轮回 + 业力 100-200 + 无祈求 |
| 真我结局·与自己和解 | true | 全记忆碎片 + 悟道值≥9 + 无祈求 + 无作弊 |
| 识破结局·天道审判 | worst | 使用过祈求 + 天道 Boss 战胜利 |

### 天道 Boss 战

通关六道后，若使用过真心祈求，进入与天道的最终对决：

- **棋类**：传统象棋，正常规则
- **禁止作弊**：输入框画红叉，无法输入
- **天道特殊**：无士，士位全部替换为车（共 4 车）
- **难度**：nightmare（搜索深度 6）
- **胜利** → 触发识破结局（最坏结局）
- **失败** → 无限重试，无法退出

### RPG 页面入口

| 页面 | 路径 | 说明 |
|:-----|:-----|:-----|
| 六道大陆大地图 | `/overworld` | 剧情模式 2.5D 大陆入口：自由行走、六道选关、技能兑换 |
| 序章/剧情对话 | `/dialogue?mode=prologue` | 序章 + 各道关卡对话 |
| 记忆相册 | `/memory-album` | 六道记忆碎片展示 |
| 结局展示 | `/ending` | 自动判定或指定结局 |
| 天道 Boss 战 | `/heaven-boss` | 最终审判（条件满足时开放） |

### RPG API（挂载于 `/samsara/story/*`）

| 方法 | 路径 | 说明 |
|:----:|:-----|:-----|
| GET | `/api/rpg/overview` | RPG 系统总览（Hub 入口展示用） |
| GET | `/api/story` | 获取完整剧情数据 |
| GET | `/api/story/prologue` | 获取序章数据 |
| GET | `/api/story/realm/{realm}` | 获取某道剧情数据 |
| GET | `/api/story/realm/{realm}/level/{level}` | 获取某关对话与选择 |
| GET | `/api/story/ending/{ending_id}` | 获取结局详情 |
| GET | `/api/choices/{realm}/{level}` | 获取选择面板 |
| POST | `/api/choices/apply` | 应用玩家选择（alignment 变化） |
| GET | `/api/memory` | 获取记忆碎片状态 |
| GET | `/api/memory/{realm}` | 获取某道记忆碎片 |
| POST | `/api/memory/{realm}/unlock` | 尝试解锁记忆碎片 |
| GET | `/api/endings` | 获取所有结局状态 |
| POST | `/api/endings/determine` | 综合判定结局 |
| GET | `/api/endings/preview` | 获取当前结局预览 |
| GET | `/api/heaven-boss` | 获取天道 Boss 战信息 |
| POST | `/api/heaven-boss/enter` | 进入 Boss 战 |
| POST | `/api/heaven-boss/win` | Boss 战胜利 |
| POST | `/api/heaven-boss/lose` | Boss 战失败（重试） |
| POST | `/api/prayer` | 真心祈求（AI 修改后调用，触发识破判定） |
| GET | `/api/prayer/status` | 获取祈求状态 |
| GET | `/api/story/progress` | 获取剧情进度 |
| POST | `/api/story/progress` | 更新剧情进度 |
| POST | /api/story/mark-prologue-seen | 标记序章已观看 |

### 资产与前端升级

> 本节记录对资产管线与前端表现的 13 项升级，均已落地到代码与资产目录。剧情权威源为 `configs/story.json`（含 `_meta`、`tiandao.boss_dialogues`、`endings[*].cg_video`、`realms[*].memory_fragment.cg_video`、`protagonist.animation`、`real_world_characters.陈默.animation` 等资产映射字段）；`configs/tiandao_boss.json` 现仅保留机械配置。

1. **rembg ML 抠图（含 alpha 二值化）**：`shared/assets/cutout_rembg.py`（rembg U2Net 语义分割），替代旧 `cutout_all.py`（颜色距离算法）；`cutout_all.py` 保留作回退。`requirements.txt` 已加 `rembg>=2.0.50`。v1.5 新增 alpha 二值化（阈值 128 + 1.2px 边缘羽化）修复 rembg 软蒙版在头发/衣服/皮肤等区域半透明的问题。
2. **类 Galgame 对话框（对标柚子社）**：`hub/dialogue.html` 重构消息窗——`.dialogue-box` 上半圆角、顶部 2px 金线 + 细金饰线、底部略方，半透渐变底 + 深阴影；左上沿**挂名牌**（`nameplate`，斜切角烫金，旁白用 `narrator` 变体）承载 `<span id="speaker-name">`；右上**工具栏**（履历 / 自动 / 隐藏）；正文用宣纸白 + 金色打字光标 + 下沿「点击或空格继续」与右下 ▶ 翻页指示。`.character-portrait` `78vh` 脚贴画面底缘、立绘 z-index:1 位于消息窗 z-index:2 之下；`.dialogue-box` 支持 `.hidden` 淡出（隐藏窗口）。立绘入场 `portraitIn` 上浮淡入。
3. **BGM 8 首清单**：新建 `shared/assets/audio/bgm/BGM清单.md`，共 8 首（序章 + 六道各一首 + 天道 Boss 战 1 首），文件名 `bgm_prologue/bgm_hell/bgm_hungry/bgm_animal/bgm_human/bgm_asura/bgm_heaven/bgm_tiandao_boss.mp3`。
4. **6 张像素画 UI**：新建 `shared/assets/ui/` 目录，含 6 张 AI 生成像素画 JPG（非 SVG）：`ui_dharma_wheel.jpg`（佛法转轮）、`ui_realm_icon_sheet.jpg`（六道图标表）、`ui_particle_star.jpg`（金色星光粒子）、`ui_particle_ember.jpg`（暗红余烬粒子）、`ui_particle_black_white.jpg`（黑白粒子）、`ui_portrait_frame.jpg`（立绘边框）。
5. **陈默形象统一**：可爱 + 温和并存，固定 CANON——齐肩黑色短发左侧别小发夹、柔和杏眼、白衬衫深蓝校服外套红色领结、胸前小棋子胸针。重新生成并抠图 9 张图：7 张陈默立绘（portrait/smile/thinking/surprised/silent/awkward/playing）+ `flipper_as_chenmo.png`（Boss 化陈默，带裂痕幻象特效）+ `cg/covers/cg_memory_hungry.jpg`（记忆 CG）。
6. **立绘 AI 连续帧动画**：v1.6 起立绘动画为 24FPS×48 帧（2 秒循环）连续帧——白色背景立绘经 Seedance 图生视频生成 2 秒微动视频，ffmpeg 抽帧 48 张（`{prefix}_{emotion}_f{1-48}.png`），rembg 抠图为透明 PNG；`dialogue.js` 的 `startPortraitAnimation` 探测 `_f1.png` 存在即预加载并循环已成功加载的帧，无动画帧回退静态抠图 PNG（含 v1.7 履历/自动/隐藏工具栏联动）。
7. **11 个 CG 视频**：11 张 CG（5 结局 + 6 记忆碎片）用 Seedance `doubao-seedance-1-0-pro-250528` 文生视频，参数 5s/720p/16:9/`camera_fixed`/无水印，生成脚本 `shared/assets/cg/generate_cg_videos.py`，输出到 `shared/assets/cg/videos/{cg名}.mp4`。前端 `hub/ending.html` 新增 `<video class="ending-cg-video" autoplay muted loop playsinline>` 全屏背景层，`hub/ending.js` 从 `ending.cg` 映射到视频路径；`hub/memory_album.js` 在详情弹窗顶部插入 `<video>`；原 `.fade-in`/`@keyframes fadeIn` CSS 动画已移除。
8. **大陆渲染迁移 melonJS（v2.0）→ WebGPU 智能双通道回归（v2.2）**：v2.0 曾因 WebGPU 版黑屏问题整体迁移 melonJS（图集预渲染 + 逐帧一次 `drawImage` 切片 + `ambientLight`/`Light2d` 动态光，WebGL 优先、Canvas 兜底，现作为**兜底通道**保留）。v2.2 起 WebGPU 渲染器完成三大根因修复（初始化竞态 / 旧草案 ShaderStage 常量导致全部管线无效 / error scope 未配对）后**回归为主通道**，并新增设备能力检测 + 四档智能画质分级 + 呈现自检自动降级——管线成功但画面全黑/全白（驱动/合成器缺陷）时主动销毁设备并无缝切到 melonJS，任何设备黑屏/白屏不可能。
9. **棋盘/棋子缩放与侧边栏**：全部棋类（含沙盒）棋盘 + 棋子缩至 75%（`#board-container` 尺寸包裹 `calc(...*0.75)`，棋子为 `%` 随棋盘联动缩放），右侧栏宽度提升至 150%（`shared/game_shared_rpg.js` 共享层 `.side-panel{width:277px}`）。
10. **棋类进程保活与主动回收**：仅靠兜底空闲超会误杀对局中的进程；新增 `/api/lazy/ping`（按端口心跳刷新 `last`），`shared/game_shared_rpg.js` 每 40s 向大厅心跳保活；`play.js`、胜负页"返回地图/返回大陆"按钮与**页面 `pagehide`（真正关闭网页/浏览器退出）**时调用 `/api/lazy/stop?port=` 立即回收。**只在玩家主动关闭界面/关闭网页时回收**：切标签、休眠、焦点移开走 `visibilitychange`（不触发 pagehide），不会在后台误回收；后端 `LAZY_IDLE_SECONDS` 兜底阈值加大（默认 1800s），仅回收异常遗留（如浏览器崩溃、pagehide 未送达）的进程。
11. **棋类启动加载进度条**：全部 12 个棋类 `static/index.html` 启动时先显示全屏"正在加载对局…"+进度条遮罩（`#boot-loader`），待棋盘组件发出 `ready` 事件后淡出（附 3s 走满 + 9s 兜底），不再白屏等待。
12. **界面缩放与大地图黑屏修复**：移除 `overworld.html` 与全部 12 个棋类 `index.html` 的整页 `html{zoom:0.75}`——该全局缩放会压缩/裁切布局、破坏棋盘居中留边，并与 WebGPU 画布（自行管理后备缓冲尺寸 + swapchain）冲突导致**打开即黑屏**。改为让棋盘按自身尺寸（如 `min(85vmin,650px)`）自然渲染并在屏幕居中、四周留出内边距；大地图（含 DOM 覆盖层）以真实视口缩放，为避免 W/S 在小地图呈 45° 斜移，WASD 改为严格世界轴向移动。
13. **动物棋陷阱吞噬规则（一次性陷阱）**：敌方动物踩中己方**真陷阱**格立即被吞噬（死亡），可有效阻挡其直捣兽穴；`rule_engine.py` 的 `_is_in_enemy_trap`/`_is_in_trap` 仅计真陷阱（排除兽穴、幻影格），动物进入己方兽穴/幻影格不会误杀（兽穴判胜仍走 enter_den）。陷阱随吞噬一并消耗：吞噬格子记入 `board_state.consumed_traps`，前端据此不再渲染该格陷阱（纯净/沙盒两模式一致），若该格存在陷阱棋子则同时置 `is_alive=False`。陷阱开关由 `rules.special_rules.trap_neutralizes_rank.enabled` 控制。
14. **WebGPU 智能画质分级（v2.2）**：`overworld-load.js` 加载时检测 WebGPU 适配器（fallback / SwiftShader / llvmpipe 软件渲染器识别）与硬件信号（核数 / 内存 / 移动端 UA），自动选择 `ultra / high / balanced / software` 四档画质（渲染缩放 1.0/1.0/0.75/0.6 × DPR 上限 2.0/1.5/1.25/1.0；SSAO、Bloom、体积光按档位启停）；画质位编码经帧 uniform（`FrameUB.qualityFlags`）下传，在 compute/渲染 pass 与合成着色器三处零开销跳过。用户可用 `?owq=` URL 参数或 `localStorage.chesssage_ow_quality` 覆盖自动检测。

---

## 十三、更多文档

- [整体设计书 v3.1](六道轮回_整体设计书_v3.1.md) — 完整设计与机制详解（含 RPG 扩展 + 天道终战）
- [RPG 化执行方案 v2.2](RPG化执行方案_v2.1.md) — RPG 开发规划（§9.0 WebGPU 主通道规范）
- [大地图渲染架构 v3](WebGPU渲染架构.md) — WebGPU 主通道 + 智能画质分级 + 呈现自检（默认非破坏）+ 测试方法
- [大地图渲染情况报告](docs/overworld/RENDERING_REPORT.md) — 三套渲染框架（WebGPU/melonJS/iso-engine）对比、分发与画质方案、本次修复与障碍移除说明
- [剧情实现草案 v1.4](轻RPG化剧情实现草案_v1.4.md) — 完整剧情设计（剧情权威源为 `configs/story.json`）
- [关卡内容报告书](关卡内容报告书.md) — 33 关 + 6 Boss 关卡详细设计
- [大地图景观设计书](docs/overworld/overworld_landscape_design.md) — 六道大陆景观与结构设计（生成器：`scripts/gen_overworld.py`，预览图：[`preview.png`](docs/overworld/preview.png)）

---

## License

MIT
