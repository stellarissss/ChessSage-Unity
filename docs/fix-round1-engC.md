# 交付说明 · Task ENG-C（小范围 / 阻塞型）

**目标**：在《ChessSage》RPG 关卡目标系统新增 `most_discs`（终局子多者胜）目标类型，并补回归测试。
**根因**：六道轮回 RPG「地狱道」(global/level_pools.json 的 `hell` 道，`game_type=heibaiqi`) 关卡目标误用 `checkmate`，黑白棋无将死概念，HUD 显示"将死对方"且目标永远无法判定。正确目标为 `most_discs`，与 `heibaiqi/rules.json` 的 `win_conditions.most_discs` 同名同义。
**策略**：先补引擎代码（本任务），design 随后改 JSON（不在此任务范围内，且未触碰 `Assets/StreamingAssets/Configs/`）。

---

## 1. 改动文件 + 行号

| 文件 | 状态 | 改动 |
|------|------|------|
| `Assets/ChessSage/Core/Samsara/Objectives.cs` | 修改 | switch 新增分支 + 新增 `CheckMostDiscs` 方法 |
| `Assets/ChessSage/Tests/ObjectivesTests.cs` | 新增 | 4 个回归测试（不动任何既有测试文件） |

### Objectives.cs 具体改动
- **`Check(objective, gameState)` 的 switch（约 100–118 行）**：在 `checkmate` 之后、`capture_count` 之前新增分支
  - 第 **104** 行：`case "most_discs": return CheckMostDiscs(objective, gameState);`
- **新增 `CheckMostDiscs(JObject, JObject)` 方法（第 144–181 行，含中文"为什么"注释）**：
  - 第 **157** 行：`private static JObject CheckMostDiscs(JObject objective, JObject gameState)`
  - 第 159–180 行：敌方固定 `"black"`、遍历 `pieces` 按 `side` 数子、严格大于判定、返回 `Result(...)`

> 说明：既有 `CheckXxx` 均返回 `JObject`（含 `completed/progress/message`），本任务严格"按现有代码为准"——`CheckMostDiscs` 同样返回 `JObject`，未引入 `bool`/`ObjectiveResult` 等假设类型。

---

## 2. CheckMostDiscs 语义与 player/opponent 判定方式

### 语义
遍历 `gameState["pieces"]`，按 `side` 累加计数（黑白棋仅黑/白两色，忽略空格/空 side），**玩家侧棋子数严格大于对方侧**时目标达成（`completed = true`）；相等或更少均 `false`。

- `progress`：玩家占全盘比例 `playerDiscs / (playerDiscs + opponentDiscs) * 100`（空盘为 0），供 HUD 展示领先度。
- `message`：`"我方X子 / 对方Y子"`。
- **数子时不看 `is_alive`**：黑白棋的棋子只翻转不死亡，"数子"应统计盘面全部棋子（与 `capture_count` 只看 `!IsAlive` 的口径不同，见下方判定依据）。

### player / opponent 判定方式（关键决策）
经核查，目标系统**没有**任何玩家阵营来源：
- `ObjectiveSystem.Check(objective, gameState)` 只接收 `objective` + `gameState`，二者都不携带 `PlayerSide`；
- 上层调用方（`SamsaraEngine.CheckObjective`/`CheckObjectiveDetail`）也未传入玩家阵营；
- `SamsaraState` 中无 player side 字段；heibaiqi 的 `board_state.json` 也无 `player_side`（仅有 `current_turn`、`game_status.winner`）。

因此与既有实现**保持口径一致**：参照 `CheckCaptureCount` / `CheckAssassination` 的约定——**敌方固定为 `"black"`**，玩家侧即盘面上"非 black（且非空格）"的另一色。对 heibaiqi 而言玩家侧即 `"white"`，与 `rules.json` 的 `turn_order: ["black","white"]` 不冲突（"对方=黑"只用于目标判定，不影响先后手）。

> 这是 additive、与现有 9 种类型同构的设计；若后续 design 在 `objective` 中显式带 `target_side`/`player_side`，再按该字段覆盖默认值即可（当前 `capture_count` 的 `target_side:"opponent"` 在代码中同样被忽略，故本任务不提前引入额外约定）。

---

## 3. 新增测试清单（`Assets/ChessSage/Tests/ObjectivesTests.cs`）

沿用既有 `SamsaraMiscTests` / `SamsaraKnownDefectsTests` 风格：用 `SamsaraFixture`（隔离引擎，临时状态文件，无需 UnityEngine/MonoBehaviour 上下文）构造最小 heibaiqi 风格棋盘状态，经 `f.Engine.Objectives.Check(objective, gameState)` 取 `completed` 断言。

| 测试方法 | 棋盘（白=玩家 / 黑=对方） | 期望 `completed` |
|----------|--------------------------|------------------|
| `MostDiscs_PlayerHasMoreDiscs_CompletedTrue` | 白 3 / 黑 1 | `true` |
| `MostDiscs_PlayerHasFewerDiscs_CompletedFalse` | 白 1 / 黑 3 | `false` |
| `MostDiscs_EqualDiscs_CompletedFalse` | 白 2 / 黑 2 | `false`（严格大于） |
| `MostDiscs_EmptyBoard_CompletedFalse` | 0 / 0 | `false`（边界，不抛异常、不误判） |

覆盖：玩家多→达成、玩家少→不达成、平子→不达成（严格大于语义）、空盘边界。

---

## 4. 门禁三项计数（原样）

| 门禁 | 命令 | 结果 |
|------|------|------|
| L1 | `bash tools/verify/core-harness.sh` | **7 / 7** 探针全 PASS（Dongwuqi / Heibaiqi / Session / Tiaoqi / Weiqi / Wuziqi / Xiangqi） |
| L2 | `bash tools/verify/compile-check.sh` | **4 / 4** 程序集零错误（Core / Presentation / App / Editor） |
| L3 | `bash tools/build.sh test` | **total=262, passed=262, failed=0**（258 现有用例 + 本任务新增 4 个，全绿，无回归） |

---

## 5. 合规与约束自检
- ✅ Core 零引擎依赖：改动仅在 `Objectives.cs`（使用 `System` / `System.Collections.Generic` / `ChessSage.Core.Json` / `Newtonsoft.Json.Linq`），无 `using UnityEngine`。
- ✅ 未改动 `Assets/StreamingAssets/Configs/`（design 领地）；未改动 `Presentation/`；未改动 `Tests/` 下任何既有文件（仅新建 `ObjectivesTests.cs`）。
- ✅ 未执行 `git commit` / `git push`（存量其他未提交改动亦未触碰）。
- ✅ 新增代码经 L1/L2/L3 三重门禁验证，未引入任何失败。

## 6. 待主理人 / design 后续
- design 将 `global/level_pools.json` 的 hell 道各关 `objective.type` 由 `checkmate` 改为 `most_discs`（及 `hell_4` 维持 `capture_count` 等），改完即生效，无需二次改码。
- 建议 design 在 JSON 中明确 `most_discs` 关卡的玩家阵营若非"白"，需同步在 objective 增加 `player_side` 字段并在代码中读取——本实现当前以"对方=黑"为默认口径。
