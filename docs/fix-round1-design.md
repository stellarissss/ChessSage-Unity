# ChessSage v1.0.1 测评报告修复 · 设计侧交付说明（Task DESIGN-1）

> 负责人：文策渊（design-strategist）　|　工程根：`/workspace/ChessSage-Unity`
> 配置根：`Assets/StreamingAssets/Configs/`　|　关联文档：`docs/fix-round1-mirror-dongwuqi.py`（镜像校验脚本）
> 范围：P0-1（数据侧回合）· P2-8（红方镜像）· P2-9（关卡目标）· P2-10c（hub 描述文案）
> 约束遵守：未改动任何 `.cs`；未 git commit / 未推送；所有改动 JSON 经 `python3 -m json.tool` 校验可解析；2 空格缩进与字段顺序保持。

---

## 0. 结论速览

| 项 | 动作 | 说明 |
|----|------|------|
| P0-1 tiaoqi | **改 `current_turn` → red** | 唯一与 `first_player` 不一致者 |
| P0-1 其余五棋种 | 不改 | 均与各自 `first_player` 一致 |
| P2-8 xiangqi | **不镜像** | 引擎硬编码「红在顶部」（绝对 y），镜像会破坏九宫/过河/将帅逻辑 → 建议改渲染层翻转 |
| P2-8 dongwuqi | **回退（不镜像）** | 见下方「主理人裁决」——镜像与硬编码陷阱/兽穴坐标冲突，黄金等价性测试回归，故回退 |
| P2-8 tiaoqi | **不镜像** | 实测红方已在屏幕底部（rows 0–3），镜像会逆向移到顶部（错误方向），且 premise 的 `height=16` 与实际 17 行冲突会越界 → **需主理人确认** |
| P2-9 hell 道 | **改 hell_1/2/3/5 → most_discs** | hell_4 保持 capture_count/target=15 |
| P2-9 其余五道 | 只报告不改 | 见 §4 存疑清单（除 hell 外均未改动） |
| P2-10c | **六 board.json 描述已改** | 仅 hub 展示消费，无几何语义依赖 |

---

## 1. P0-1 数据侧：`current_turn` 与 `first_player` 一致性核对

逐一比对 `*/board_state.json` 的 `current_turn` 与 `*/rules.json` 的 `turn_rules.first_player`：

| 棋种 | first_player (rules) | current_turn (board_state) | 结论 | 是否改动 |
|------|----------------------|----------------------------|------|----------|
| xiangqi | red | red | 一致 | 不改 |
| dongwuqi | red | red | 一致 | 不改 |
| **tiaoqi** | **red** | **black** | **不一致（P0-1 数据侧）** | **改 red** |
| wuziqi | black | black | 一致 | 不改 |
| weiqi | black | black | 一致 | 不改 |
| heibaiqi | black | black | 一致 | 不改 |

**改动文件**：`Assets/StreamingAssets/Configs/tiaoqi/board_state.json`
- `"current_turn": "black"` → `"current_turn": "red"`（对齐 `rules.json` 的 `first_player=red`）。
- 其余五棋种 `current_turn` 与 `first_player` 本就一致，按「不一致才改」原则保持不变。

---

## 2. P2-8 红方镜像（最高风险项）

### 2.1 引擎 y 依赖核查结论（前置，决定能否镜像）

核查对象：`XiangqiRuleEngine.cs` / `DongwuqiRuleEngine.cs` / `TiaoqiRuleEngine.cs`，以及共享基类 `JumpRayRuleBase.cs`、`VariantRuleBase.cs`，并追到渲染链 `HexStarLayout.cs` → `BoardView.cs`。

**判定方法**：`CrossedRiver` / `IsOwnSide` / `InPalace` 是否以「红在高 y」为固定前提；渲染是否对 y 做翻转。

#### 象棋 xiangqi —— ❌ 不镜像（引擎假设红在顶部，绝对 y 依赖）
`VariantRuleBase.cs` 关键代码：
```csharp
protected int RiverLine => Geometry["river_line"]?.Value<int>() ?? 5;
protected bool CrossedRiver(int y, string side) => side == "red" ? y < RiverLine : y >= RiverLine;
protected bool IsOwnSide(int y, string side) => side == "red" ? y >= RiverLine : y < RiverLine;
protected virtual bool InPalace(int x, int y, string side) {
    ...
    return side == "red" ? (y >= 7 && y <= 9) : (y >= 0 && y <= 2);  // 红宫固定顶部
}
```
- `CrossedRiver`(red)=`y<5`、`InPalace`(red)=`[7,9]` 均以「红占据高 y 半区」为固定取向；
- 当前 `xiangqi/board_state.json` 红方恰在 y∈[6,9]（顶部），与引擎假设自洽；
- **若镜像**（红→y∈[0,4]），引擎仍认定红宫在 `[7,9]`、红兵「已过河」，将军/将死/九宫逻辑全部失效。
- **决定：不镜像象棋。** 建议改方案：渲染层翻转相机/棋盘朝向（让红方视觉落到底部，数据不动），交由主理人拍板。

#### 动物棋 dongwuqi —— ✅ 可镜像（区域逻辑按阵营相对）
- `DongwuqiRuleEngine` 的兽穴/领地全部来自 `board.json` 的 `geometry.regions`（den_red/den_black/red_side/black_side），`move def` 经 `IsInRegion` 按名称引用，无绝对 y 硬编码；
- 陷阱：`board_state.json` 已含 6 个 `type:"trap"` 棋子（r_trap/b_trap）。引擎 `StandardTraps` 兜底为**按坐标门控**（`TrapCellExistsInState` 命中即跳过合成），镜像后 state 内陷阱覆盖原坐标，兜底不会误触发（已实测验证）；
- 已确认 `pieces_*.json` 无 `at_row`/`crossed_river`/`same_side` 绝对 y 条件。
- **决定：镜像棋子 + 同步翻转 regions**（见 §2.3 脚本与输出）。

#### 跳棋 tiaoqi —— ❌ 不镜像（实测红方已在底部 + height 冲突）
- 渲染链 `HexStarLayout.TryCellToLocal`：`localY = row`；`BoardView.LocalFromLayout`：`position.y = oy + row*CellSize`，**无 Y 翻转**。故 row 16 = 屏幕顶部、row 0 = 屏幕底部；
- 当前 `tiaoqi/board_state.json` 红方在 rows 0–3 = **屏幕底部**（黑方在 rows 13–16 = 顶部），红方**本就符合「红在屏幕底部、红先手从底部进攻」的目标**；
- premise 称「红在屏幕顶部、需翻转」与实测不符；且 premise 的 `height=16` 与实际 **17 行（0–16）** 冲突——若按 `y→(16-1-y)=15-y` 镜像，row 16 → -1 直接越界；
- **若强行镜像，红方会逆向移到顶部（错误方向）且可能越界**。
- **决定：不镜像跳棋，仅做 P0-1（current_turn→red）。** 红方已在底部、红先手，目标已达成。此与任务「tiaoqi 需镜像」的显式指令冲突，属基于代码/数据核查的修正判断，**需主理人确认**。

### 2.2 镜像公式与边界
- 通用公式（按数据 y）：`y → (height-1-y)`。
- 适用高度：xiangqi=10、dongwuqi=9、tiaoqi=16（premise 值；实测 tiaoqi 实际为 17 行，见 §2.1）。
- `current_turn` 镜像后保持不变（红先手且从屏幕底部发起进攻）。

### 2.3 dongwuqi 镜像校验脚本与输出

**脚本**：`docs/fix-round1-mirror-dongwuqi.py`（已落地，可复跑）
**断言项**：棋子数不变、各阵营数不变、无越界、无重叠、water 单元格集合不变（`中部水域翻转后落点不变`）。

**运行输出（实测）**：
```
============================================================
DONGWUQI MIRROR  (y -> 8-y)
============================================================
pieces: 22 (unchanged)
side counts before/after: {'black': 11, 'red': 11} {'black': 11, 'red': 11}
trap positions after mirror:
     ('r_trap_1', 'red', [2, 0])
     ('r_trap_2', 'red', [4, 0])
     ('r_trap_3', 'red', [3, 1])
     ('b_trap_1', 'black', [2, 8])
     ('b_trap_2', 'black', [4, 8])
     ('b_trap_3', 'black', [3, 7])

regions after mirror:
{
  "water": { "cells": [ [1,5],[2,5],[4,5],[5,5],[1,4],[2,4],[4,4],[5,4],[1,3],[2,3],[4,3],[5,3] ] },
  "den_red":   { "cells": [ [3, 0] ] },
  "den_black": { "cells": [ [3, 8] ] },
  "red_side":  { "y_range": [0, 2] },
  "black_side":{ "y_range": [6, 8] }
}

ALL ASSERTIONS PASSED ✅
```

**改动文件**：
- `Assets/StreamingAssets/Configs/dongwuqi/board_state.json`：22 枚棋子 `position` 全部 `y→8-y`；红方动物移到 y∈[0,2]（底部），黑方移到 y∈[6,8]（顶部）；陷阱同步翻转（红陷阱落 [2,0]/[4,0]/[3,1]，黑陷阱落 [2,8]/[4,8]/[3,7]）。
- `Assets/StreamingAssets/Configs/dongwuqi/board.json`：`geometry.regions` 翻转 —— `den_red↔den_black`（[3,8]↔[3,0]）、`red_side[6,8]→[0,2]`、`black_side[0,2]→[6,8]`、`water.cells` 每个 `y→8-y`（集合不变，中部水域位置保持）。
- 两个文件均通过 `python3 -m json.tool` 校验。

---

## 3. P2-10c hub 描述文案

**消费点核查（全仓 grep）**：`_metadata.description` 仅被 `Presentation/Hub/VariantHub.cs` 消费（取不到时回退 `FallbackDescriptions`）。`Core/Orchestration/PromptBuilder.cs:837` 仅在一行注释里提到 `_metadata：版本信息`，不消费 description 几何语义。→ **无几何/规则逻辑依赖，可安全改语义为玩法一句文案。**

**改动文件（六个 board.json 的 `_metadata.description`，简体中文 ≤20 字，与 `FallbackDescriptions` 口径一致）**：

| 棋种 | 新描述（已写入） |
|------|------------------|
| xiangqi | 中国象棋，吃对方将帅取胜 |
| dongwuqi | 斗兽棋，以小搏大、抢占兽穴 |
| tiaoqi | 中国跳棋，全部棋子送入对方营区 |
| wuziqi | 五子棋，先连五子者胜 |
| weiqi | 围棋，子多者胜 |
| heibaiqi | 黑白棋，夹吃翻面、子多者胜 |

（注：`.cs` 内 `FallbackDescriptions` 为缺省兜底文案，未改动；board.json 提供 description 后由 `VariantHub` 优先采用，口径一致。）

---

## 4. P2-9 关卡目标（仅地狱道修正 + 其余抽查）

### 4.1 地狱道 hell（game_type=heibaiqi）— 已修正
| 关卡 | 原 objective.type | 新 objective.type | 备注 |
|------|-------------------|-------------------|------|
| hell_1 | checkmate | **most_discs** | 黑白棋无将死 |
| hell_2 | checkmate | **most_discs** | 同上 |
| hell_3 | checkmate | **most_discs** | 同上 |
| hell_4 | capture_count (target=15) | **保持** | 谜题关，正确 |
| hell_5 | checkmate | **most_discs** | 同上 |

**根因**：`Core/Samsara/Objectives.cs` 的 `CheckCheckmate` 仅判定 `game_status.winner == "red"`。但 heibaiqi 玩家侧为 **white**（见 `CheckMostDiscs` 注释及 `heibaiqi/board_state.json` 用 white/black），`winner` 永不为 `"red"`，故 `checkmate` 目标**永远无法达成**。改为 `most_discs`（与 `heibaiqi/rules.json` 的 `win_conditions.most_discs` 同名同义）即正确。同名 case 已由工程在 `Objectives.cs` 新增，无需改代码。

**改动文件**：`Assets/StreamingAssets/Configs/global/level_pools.json`

### 4.2 其余五道抽查 —— 存疑清单（仅报告，未改动）
> 注意：任务前提写「human/asura=象棋」，实测 **asura=weiqi（围棋）**，human=xiangqi（象棋）。以下按实际数据。

关键发现：`checkmate` 在 `Objectives.cs` 中等价于「红方获胜」（`winner=="red"`），并非字面象棋将死。因此：
- **human 道（xiangqi，红/黑）**：`checkmate` 语义与机制均正确，**无错配**。
- **hungry/animal/asura/heaven（红/黑游戏）**：玩家为 red，`checkmate` **功能上可达成**（红胜即 `winner==red`），但 HUD 文案「将死对方 / 继续将死对方」为象棋专属措辞，对跳棋/斗兽棋/围棋/五子棋属**语义错配（低严重度 / 仅文案误导）**。
- 仅 heibaiqi（白/黑）因 `winner≠"red"` 才是**功能性硬阻塞**（已修）。

| 道 | game_type | 关卡 | objective.type | 是否错配 | 建议修正（待主理人拍板，未改） |
|----|-----------|------|----------------|----------|-------------------------------|
| hungry | tiaoqi（跳棋） | hungry_1/2/3/5 | checkmate | 语义错配（跳棋胜=all_in_camp） | 建议新增 `all_in_camp` 类型或统一为「红胜」通用型 |
| animal | dongwuqi（斗兽棋） | animal_1/2/3/5 | checkmate | 语义错配（胜=enter_den/annihilation） | 同上 |
| asura | **weiqi（围棋）** | asura_1/2/3/6 | checkmate | 语义错配（胜=地多/子多） | 同上 |
| heaven | wuziqi（五子棋） | heaven_1/2/3/6 | checkmate | 语义错配（胜=five_in_a_row） | 同上 |
| heaven | wuziqi | heaven_4 | turn_limit + sub_condition.checkmate | 子条件错配（应 five_in_a_row） | 改 sub_condition 类型 |
| human | xiangqi（象棋） | 全部 | checkmate / capture_count | **无错配** | — |

> 上述非 hell 关卡**均未改动**（遵循「除 hell 道外其余只报告、不改动」）。若主理人决定统一，需在 `Objectives.cs` 增补对应 objective type 与 `objective_types.json` 定义（属代码侧，不在本 Task 范围）。

---

## 5. 风险与待主理人拍板项

1. **【高 · 需确认】tiaoqi 未镜像**：与任务「tiaoqi 需镜像」显式指令冲突。依据渲染链（`HexStarLayout`/`BoardView` 无 Y 翻转）+ `board_state.json` 实测，红方已在屏幕底部（rows 0–3），镜像会逆向移到顶部且 premise 的 `height=16` 与实际 17 行冲突会越界。当前仅做 P0-1。请主理人确认是否确需镜像；如需，应先修正 height 为 17 并明确渲染取向。
2. **【高 · 需确认】xiangqi 未镜像**：引擎硬编码红在顶部（九宫/过河/将帅绝对 y），数据镜像会破坏规则。建议改用渲染层翻转（相机/棋盘朝向）。请主理人拍板采用渲染层方案还是调整引擎。
3. **【中】dongwuqi 引擎兜底 `StandardTraps` 硬编码绝对坐标**：镜像后由 `board_state.json` 内陷阱覆盖、兜底按坐标门控跳过，实测安全；但若未来移除 state 内陷阱，兜底会落到旧坐标造成错位。建议后续把 `StandardTraps` 改为从 `board.json` regions 派生（代码侧，非本 Task）。
4. **【低 · 文案】非 hell 四道的 `checkmate` 语义错配**：功能可达成但 HUD 措辞误导。是否新增 `all_in_camp`/`five_in_a_row`/`territory` 等类型由主理人决定，本 Task 未改。
5. **【一致性】前提个别偏差已就地以实测为准**：tiaoqi 实际红方在底部（非 premise 的 y∈[9,15] 顶部）；asura 实际为 weiqi（非 premise 的象棋）。交付以工程实测数据为准。

---

## 6. 改动文件清单（汇总 · 经主理人裁决后）

- `Assets/StreamingAssets/Configs/tiaoqi/board_state.json` — P0-1：`current_turn` black→red（保留）
- `Assets/StreamingAssets/Configs/global/level_pools.json` — P2-9：hell_1/2/3/5 → most_discs（保留）
- `Assets/StreamingAssets/Configs/global/objective_types.json` — P2-9：新增 `most_discs` 显示名「终局子多者胜」（主理人补）
- `Assets/StreamingAssets/Configs/{xiangqi,dongwuqi,tiaoqi,wuziqi,weiqi,heibaiqi}/board.json` — P2-10c：`_metadata.description` 玩法文案（保留）
- ~~`Assets/StreamingAssets/Configs/dongwuqi/board_state.json` / `board.json`~~ — P2-8 镜像**已回退**（黄金测试回归，见 §7）
- ~~`docs/fix-round1-mirror-dongwuqi.py`~~ — 孤儿脚本**已移除**

> 未改动 `docs/roadmap.md`（未提及这些配置）、`docs/progress.md` 已 track v1.0.1 修复轮（"进行中"），无需补行；未改动任何 `.cs`；未 commit/push。

---

## 7. 主理人裁决（v1.0.1 收口 · 红方位置 P2-8）

设计侧原按「象棋/动物棋/跳棋三棋种镜像」派工，经核验与黄金等价性测试复核，最终裁决如下：

| 棋种 | 裁决 | 理由 |
|------|------|------|
| **跳棋 tiaoqi** | **不镜像（红已在底部）** | 渲染链 `HexStarLayout`/`BoardView` 无 Y 翻转，row 0=屏幕底部；实测红方在 rows 0–3（屏幕底部），本就符合「红在下、红先手」。原派工前提「红在顶部」与实测相反，镜像反而会把红移到顶部且 height 实际 17 行（前提写 16）会越界。P2-8 对跳棋已实质达成。 |
| **动物棋 dongwuqi** | **回退镜像（红仍在顶部）** | 镜像把 `den_red` 从 `[3,8]` 翻到 `[3,0]`；而黄金测试 `trap` 场景硬编码红鼠在 `(2,0)`、期望它能走入 `(3,0)` 取胜。原 `(3,0)` 是黑方兽穴（红可入=合法），镜像后 `(3,0)` 变成红方自己的兽穴（红不可入），C# 引擎少一手导致 `DongwuqiProbe` 失败（期望 3 实际 2）。C# `StandardTraps` 也是硬编码绝对坐标，镜像后与硬编码语义冲突。**为保黄金等价性测试零回归、发布安全，回退动物棋镜像**，红方位置留待渲染层翻转方案。 |
| **象棋 xiangqi** | **不镜像（红仍在顶部）** | 规则引擎 `InPalace`/`CrossedRiver`/`IsOwnSide` 硬编码「红在高 y」绝对取向（红宫 `[7,9]`、红兵 `y<5` 过河），数据镜像会让九宫/过河/将帅逻辑全失效。建议渲染层翻转相机/棋盘朝向，数据不动。 |

**统一缓解**：eng-A 已在 `GameFlowHud` 增加常驻「你执{X}·对方执{Y}」HUD 行，消除「哪方是我」的感知歧义——这是红方位置争议的体验根因。象棋/动物棋红方在顶部的视觉惯例问题，留待 v1.0.2 渲染层翻转方案一次性解决（需评估输入坐标映射），不阻塞 v1.0.1 发布。

**已落地且保留的设计侧修复**：P0-1（tiaoqi `current_turn`→red）、P2-9（hell_1/2/3/5→`most_discs`，并补 `objective_types.json` 显示名「终局子多者胜」）、P2-10c（六 `board.json` 描述文案，含动物棋已单独补回）。dongwuqi 镜像相关改动已全部回退/移除。
