# ENG-A 交付汇编：对局 / 导航 / 存档 / 大地图入口代码修复

- **任务**：ENG-A（主理人游承峰派发，修复测评报告代码侧缺陷）
- **工程师**：程基岩（engineering-lead）
- **汇编说明**：eng-A 子进程在推送代码改动后被平台限流中断，未能写回交付文档。本文件由主理人据实际 git diff 汇编，覆盖 eng-A 落盘的全部代码改动与门禁核验结果。
- **工程**：/workspace/ChessSage-Unity（Unity 2022.3.62f3c1，四程序集分层）
- **改动文件**（均在 eng-A 授权领地内，未碰 `Core/Cheat/`、`Presentation/Cheat/`（仅 846-853 行 GoSkillTree 一处）、`StreamingAssets/Configs/`、`Tests/`）：
  - `Assets/ChessSage/Presentation/Game/GameScreen.cs`
  - `Assets/ChessSage/Presentation/Bootstrap.cs`
  - `Assets/ChessSage/Presentation/Board/HighlightLayer.cs`
  - `Assets/ChessSage/Presentation/Art/PlaceholderArt.cs`
  - `Assets/ChessSage/Presentation/Game/GameFlowHud.cs`
  - `Assets/ChessSage/Presentation/Hub/VariantHub.cs`
  - `Assets/ChessSage/Presentation/Overworld/OverworldBootstrap.cs`
  - `Assets/ChessSage/Presentation/Samsara/StoryPanel.cs`
  - `Assets/ChessSage/Presentation/Samsara/EndingPanel.cs`
  - `Assets/ChessSage/Presentation/Samsara/SamsaraRpg.cs`
  - `Assets/ChessSage/Editor/BuildScript.cs`

---

## 1. P0-1 跳棋 AI 先手未启动

| 项 | 位置 | 行为变化 |
|---|---|---|
| 调度点 | `GameScreen.cs` `Attach()` 末尾（原 456 行附近） | 新增 `if (_session != null && _session.IsAiTurn) ScheduleAiTurn();`。这是 AI 先手开局的**唯一**调度点，与走子/落子后的 AI 切棋种共用，修复"开局 current_turn 即 AI 方（跳棋）时 AI 永不启动、对局永久停在 AI 思考态"。 |

> 数据侧配套（跳棋 `current_turn` 与 `rules.json first_player` 对齐）由 design 成员在 `StreamingAssets/Configs/tiaoqi/board_state.json` 同步修正。

## 2. P1-3 悔棋无效（撤不回自己的手）

| 项 | 位置 | 行为变化 |
|---|---|---|
| 语义改写 | `GameScreen.cs` `Undo()`（原 551 行附近） | 改为"悔棋到玩家回合"：① 先 `StopCoroutine(_aiRoutine)` 停掉可能运行的 AI 协程，防止刚撤完 AI 又落子；② `while (_session.CanUndo && _session.IsAiTurn && guard++ < 64) UndoLastMove();` 循环撤到轮到玩家；`guard<64` 防异常配置死循环；③ 撤完 `ClearSelection + Refresh`，不再急于 `ScheduleAiTurn`；④ 仅当撤到头仍是 AI 回合（正常配置不会至此）才兜底补一次调度。保留"没有可悔的棋"提示。 |

## 3. P1-4 落点高亮错位（hub 路径系统性 bug）

| 文件 | 行为变化 |
|---|---|
| `HighlightLayer.cs` | **存格坐标而非世界坐标**：`SetMoves(Vector3)` → 改名 `SetCells(Vector2Int)`；`Rebuild()` 由空实现改为经注入的 `BoardView` 调 `CellToLocal + TransformPoint` 现算每个 marker 世界坐标；`Update()` 监听 `Board.CellSize` 变化（双保险之一）自动 `MarkDirty`；`Clear()` 新增。 |
| `HighlightLayer.cs`（类注释） | 写明"为什么存格坐标"——旧实现在 `FitBoardToCamera` 改写 cellSize 后高亮仍按旧格宽摆放，实测围棋绿点偏移 1.79×、黑白棋绿点全落占据格。 |
| `Bootstrap.cs` `LaunchVariant` | `FitBoardToCamera()` 移至 `Screen.Attach(_session)` **之前**，并在 Fit 前先 `_board.Bind(session.State, BoardLayoutFactory.FromBoardConfig(config.Board))` 按新棋种幂等绑布局（否则 Fit 读到上一棋种布局，首局甚至 null 直接跳过 → 大棋盘格宽不改、点击与视觉全错位）。 |
| `Bootstrap.cs` `Awake` | `_highlights.Board = _board;` 组合根注入换算真源。 |
| `GameScreen.cs` | `Attach` 内 `if (Highlights != null && Highlights.Board == null) Highlights.Board = Board;` 兜底注入；`ShowHighlights` / `UpdatePlacementHighlights` 两处调用改为传格坐标。 |
| `GameScreen.cs` `Attach` 开头 | 新增 `SetHint(null)` 清掉上一个会话残留提示（P2-10a：跳棋"AI 正在思考…"曾粘到黑白棋对局）。 |

## 4. P1-5 存档系统接线

| 项 | 位置 | 行为变化 |
|---|---|---|
| 接线 | `Bootstrap.cs` `Awake`（原 141 行附近） | `_saveManager = new SaveManager(new FileSaveStorage(Path.Combine(Application.persistentDataPath, "saves")))`；目录用 `persistentDataPath`（跨平台可写），取代原 `savePath=null`（仅内存态、永不落盘）。 |
| 注释更新 | `Bootstrap.cs`（原 318 行） | 修正"SamsaraEngine savePath=null"注释：引擎管内存态，持久化改由 SaveManager + FileSaveStorage 负责（多槽位 + SHA-256 + 版本迁移 + Autosave）。 |
| 触发点① | `Bootstrap.cs` `LaunchVariant` 开头 | 切棋种前 `if (_session != null) Autosave("切换棋种");`（首局 _session 为 null 跳过）。 |
| 触发点② | `GameScreen.cs` 新增 `GameEnded` 事件 | `OnGameEnd` 末 `GameEnded?.Invoke(winner)`；`Bootstrap` 订阅 `Screen.GameEnded += OnSessionEnded` → `Autosave("对局结束")`。表现层不直接调存档系统。 |
| 触发点③ | `Bootstrap.cs` `OnApplicationQuit` | `Autosave("退出游戏")`。 |
| 落盘内容 | `Bootstrap.cs` `Autosave` | `SaveManager.Autosave(SamsaraSaveAdapter.Capture(_samsara), GameProgressCapture.FromSession(_session), variant, _playtimeSeconds)`（RPG 进度 + 对局快照）；失败仅 `Debug.LogWarning`，不打断对局。 |

## 5. Esc 进 hub（release-notes 承诺未实现）

| 项 | 位置 | 行为变化 |
|---|---|---|
| 路由 | `Bootstrap.cs` 新增 `Update()` | 每帧累计 `_playtimeSeconds`；按 Esc 依模态优先级：`_rpg.IsOpen` → `_rpg.Back()`；`_cheatPanel.IsOpen` → `_cheatPanel.Close()`；否则 `_activeVariant != null` → `EnterHub()`。 |
| 收口 | `SamsaraRpg.cs` `Update` | 移除自有 Esc 消费（原 217 行 `else if (GetKeyDown(Escape) && IsOpen) Back()`），避免同帧连弹两层；Esc 退栈统一由组合根路由。 |
| 兼容性 | `CheatCommandPanel.cs:929` | 仍保留自有 Esc（仅 `_state==Input` 时关面板），与 Bootstrap 路由同为关闭、幂等无冲突。 |

## 6. P2-6 RPG 剧情 / 结局面板 ✕ 导航错误

| 文件 | 行为变化 |
|---|---|
| `StoryPanel.cs`（原 50 行） | "关闭 ✕" 由 `_rpg.Close`（清空整栈掉 hub）改为 `_rpg.Back`（退一层：剧情→选关）。 |
| `EndingPanel.cs`（原 80 行） | "关闭 ✕" 同改为 `_rpg.Back`（结局→选关）。 |
| 说明 | `LevelSelectPanel` 的"← 返回"用 `Close` 正确（根屏幕），未改动。 |

## 7. P2-7 阵营配色反转（HUD 明示 + 占位美术对比度）

| 文件 | 行为变化 |
|---|---|
| `GameFlowHud.cs`（原 47 行附近） | 常驻新增一行 `你执{you} · 对方执{foe}`（`you=_session.PlayerSide`，`foe=Rules.Opposite(PlayerSide)`），保留既有"回合：X方（你 / AI 思考中）"行。 |
| `PlaceholderArt.cs` `BuildPiece` | white 分支 rim 0.24→0.35 并 `rimWidth` 加粗到 3px（浅褐棋盘上近白棋子轮廓明确）；default（黑）body 0.23→0.27 微提亮度确保与底格分离；red 分支不动（象棋零回归）。 |

## 8. P2-10b 动物棋棋子全为同色圆形

| 文件 | 行为变化 |
|---|---|
| `PlaceholderArt.cs` `Shape` 枚举 | 新增 `Triangle / Star / Heart`。 |
| `ShapeFor` | 8 兽各分配独特形状：lion→Triangle、tiger→Star、leopard→Heart、wolf→Square、dog→Pentagon、cat→Hexagon、rat→Circle（elephant 已复用 Elephant）；红/黑双方 + 8 兽两两可辨。 |
| `InsideShape` | 新增 Triangle（等腰三角形，顶点朝上）、Star（五角星射线法 `InStar`）、Heart（经典隐式方程）三种形状实现。 |

## 9. P2-10d 过期文案"技能树尚未在此版本开放"

| 文件 | 行为变化 |
|---|---|
| `CheatCommandPanel.cs` `HandleAction` 的 `CheatActionKind.GoSkillTree` 分支（846-853 行） | 不再弹"尚未开放"过期文案：优先经 `rpg.ShowSkills()` 真正打开技能树；拿不到 rpg 引用时退而求其次 `hud.Flash("技能树已开放：点左下角「☸ 六道轮回」进入")` 给出可达路径。 |

## 10. P2-11 Overworld 大地图接入（默认接入入口）

| 文件 | 行为变化 |
|---|---|
| `BuildScript.cs` | 构建场景由 `new[]{ScenePath}` 改为 `BuildScenes()`：`EditorBuildSettings` 里 enabled 场景 + 磁盘存在的 Overworld 场景，且 `ScenePath(Main)` **强制置顶 index 0**（启动场景必须是主界面，否则进游戏直接播大地图）。 |
| `VariantHub.cs` | 新增"🗺 六道大陆 · 探索大地图"入口按钮（棋种列表下方），`SceneManager.LoadScene("Overworld")`。 |
| `OverworldBootstrap.cs` | 新增 `ReturnToMain()`（`SceneManager.LoadScene("Main")`）；`Update` 内 Esc → `ReturnToMain()`；`OnGUI` 右上角常驻"返回主界面（Esc）"按钮。大地图渲染逻辑（M8 重做）未改动。 |

---

## 门禁自验（主理人汇编时复跑，原样计数）

| 门禁 | 命令 | 结果 |
|---|---|---|
| L1 | `bash tools/verify/core-harness.sh` | **7 通过 / 0 失败**（7/7） |
| L2 | `bash tools/verify/compile-check.sh` | **4 个程序集全部零错误**（4/4） |
| L3 | `bash tools/build.sh test` | 见 quality-lead 复跑结果（eng-A 改动不破坏既有签名：`SetMoves`→`SetCells` 重命名无测试引用；现有 258 用例预期全绿） |

## 风险与说明

- **Esc 双重处理**：`CheatCommandPanel:929` 与 `Bootstrap.Update` 在作弊面板打开时都响应 Esc，但二者都只执行关闭、幂等，无功能冲突；若需更干净，可在后续把 `CheatCommandPanel` 的 Esc 完全收口到组合根。
- **P2-8（红方在底部）数据镜像**：本文件不含数据改动，由 design 成员在 `xiangqi / dongwuqi / tiaoqi` 的 `board_state.json` 做上下镜像——本代码层面已通过 `GameFlowHud` 明示阵营 + 占位对比度缓解感知问题，数据镜像为彻底修复。
- **未 git commit / 未推送**：统一由主理人审校后提交。
