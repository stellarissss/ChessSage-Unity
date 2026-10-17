# ENG-B 交付说明：修复作弊面板缺陷（P0-2a / P0-2b）

- **任务**：ENG-B（主理人游承峰派发）
- **工程师**：程基岩（game technology & engine）
- **工程**：/workspace/ChessSage-Unity（Unity 2022.3.62f3c1）
- **改动文件**（仅两个，均在授权领地内）：
  - `Assets/ChessSage/Core/Cheat/CheatErrorTranslator.cs`
  - `Assets/ChessSage/Presentation/Cheat/CheatCommandPanel.cs`
- **未改动**：`Assets/ChessSage/Core/Cheat/CheatController.cs`（保底核验后确认无需改，见 §3）；`CheatCommandPanel.cs` 的 GoSkillTree 分支（846-848 行，eng-A 领地）保持原样；未碰 `Tests/`、`StreamingAssets/Configs`、`GameScreen.cs` / `Bootstrap.cs` / `PlaceholderArt.cs`。**未 git commit、未推送。**

---

## 1. P0-2a 密钥欠费 / 无效翻译分支（CheatErrorTranslator.cs）

| # | 位置（改后行号） | 行为变化 |
|---|---|---|
| 1 | 102-104 行（类文档注释） | 补一行说明：402/401·403 与 "HTTP `<code>`" 同文本但语义是「重试也无用」，必须先于超时/网络分支判断，出路指向预置指令而非重新施展。 |
| 2 | 194-206 行（402 分支） | 新增：`msg` 含 "402" / "Insufficient Balance"（大小写不敏感）/ "余额不足" → `CheatFailure{ Title="🔋 天道密钥额度已用完", Reason="自然语言改规暂不可用（服务密钥欠费）。你仍可使用全部预置改规与完整对局功能。", Actions=[打开预置指令(OpenPresets), 知道了(None)] }`。 |
| 3 | 207-219 行（401/403 分支） | 新增：`msg` 含 "401" / "403" / 正则 `Invalid.*key`（忽略大小写）/ "无效" / "未授权" → `Title="🔑 天道密钥无效或未授权"`，Reason 与动作结构同 402 分支。 |
| 4 | 189-193 行 + 分支顺序 | 两个新分支插入在原 `type=="error"` 的「未设置密钥」分支之后、**220 行超时/网络分支（`msg.Contains("HTTP")`）之前**。"HTTP 402: …" 同时含 "402" 与 "HTTP"，顺序保证先命中密钥级故障，不再落入"天道迟迟没有回应 → 重新施展"的误导分支。 |
| 5 | 114-122 行 + 265-269 行（Reason 截断） | `Present` 改为薄包装：调 `PresentCore` 后在出口统一 `ClampReason`（>120 字截为前 119 字 + "…"）。应对交付项 2d 的溢出风险：DeepSeek 402 错误体（`HTTP 402: {…body…}`）可能数百字，失败卡 Reason 文本区仅 1780×260，超长必溢出。截断后技术细节（状态码、错误体原文）不入玩家文案。 |

**硬性约束核对**：文件无 `using UnityEngine`（Core 零引擎依赖保持）；新增正则复用文件既有 `using System.Text.RegularExpressions`；注释为中文"为什么"风格。既有用例 `ErrorTranslator_MapsEngineResultTypes` / `ErrorTranslator_SchemaOob_TranslatedToChinese` 未受影响（仅 Contains 断言，且两条 Schema 用例消息均 <120 字）。

## 2. P0-2b 失败卡可恢复（CheatCommandPanel.cs，四项全部落地）

| 项 | 位置（改后行号） | 行为变化 |
|---|---|---|
| a | 798-806 行（`ShowFailure`） | 渲染完翻译器动作后**总是追加**「知道了」关闭按钮（点击 → `SetFailureVisible(false)`）；若 `failure.Actions` 最后一个动作已是 `CheatActionKind.None`（如「取消」「知道了」）则跳过重复追加。覆盖零动作路径：空输入（"天道静候"）、`OnPreviewReady` 的 null 预览、预置无变化、`Present(null)`、fun 气泡。 |
| b | 822-825 行（`HandleAction`） | 新增 `case CheatActionKind.None: SetFailureVisible(false); break;`。修复"取消"按钮（rejected/业障过重分支的「取消」、402/401 分支的「知道了」）点击无反应——此前 switch 无此 case，命中后静默返回。 |
| c | 四处复位 `_failureVisible`（改前全代码仅声明/置 true/读取三处、无任何复位） | ① `Close()` 122-123 行：收起抽屉时清标志位——修复"关闭面板再打开红色失败卡原样重现"（实测复现项，根因即此）；② `SubmitRoutine()` 450-453 行：进入 Processing 时清（防御性：processing 中卡片本就被 `ApplyState` 隐藏，清标志位防恢复时残留）；③ `UndoRule()` 704-706 行：撤回成功、`SetState(State.Input)` 之前清；④ `EnterOffline()` 727-728 行：转入离线时清（离线引导卡随后由调用方 `ShowFailure` 自行展示，旧卡标志位不残留）。 |
| d | 布局与溢出结论 | **未改布局**：FailureGroup 与 InputGroup 同矩形（28,100,1864,440）是设计使然（失败时失败卡替代输入区），补上永远存在的「知道了」后玩家可随时返回输入区，无需挪动。溢出风险由 §1-5 的 120 字截断消除：23px 中文在 1780px 宽文本区每行约 77 字、260px 高约 8 行，容量约 600 字，120 字远未触顶。 |

**状态机核对**：`ApplyState` 856 行（改后行号）`_failureGroup.SetActive(_state == State.Input && _failureVisible)` 未动；所有新复位点都与"卡片只在 Input 态可见"的既有契约一致。`OnPreviewReady` 的离线路径先 `EnterOffline()`（清）后 `ShowFailure()`（置 true + SetState(Input)），顺序安全。

## 3. 错误路径动作保底走查（CheatController.cs —— 未改动）

对 `CheatController.SubmitAsync`（92-139 行）两条 catch 逐一走查（改前代码即如此，新分支不改变结论、只增强）：

1. **`OperationCanceledException` → `ErrorPreview("请求超时或被取消")`**（118-122 行）：
   → `Present`：`type=="error"`，不含 "402/401/403/Insufficient Balance/无效"，含 "超时" → 超时分支 → Actions = [重新施展(Retry), 改用预置指令(UsePreset)]。**≥1 动作 ✔**
2. **`Exception` → `ErrorPreview("意图解析失败: " + e.Message)`**（123-127 行）：
   → `Present`：消息必含前缀 "意图解析失败" → 「天道没听懂」分支 → Actions = [换个说法(ChangeWording), 打开预置指令(OpenPresets)]。**≥1 动作 ✔**
   即便 `e.Message` 本身含 "HTTP 402/401/403"（例如引擎把 HTTP 异常包装后抛出），改后 402/401/403 分支先命中，动作 = [打开预置指令, 知道了]，仍有 ≥1 动作 ✔。

**结论：测评报告所称"错误走了通用 catch 未生成动作"现象，在当前 main 已不存在**——两条 catch 都经 `ErrorPreview` → `CheatErrorTranslator.Present`，各产出 2 个动作按钮。真正零动作的路径是**本地直接构造的 `CheatFailure`**（`InfoPreview` 空输入提示、`OnPreviewReady` 的 null 预览、`ConvertResult`/`SubmitPreset` 的"没有实质变化"、`Present(null)`、fun 气泡），这些正是"部分错误路径未生成动作按钮"的实测来源，已由 §2-a 面板侧「知道了」保底全覆盖。因此按"保底需要时才改"原则，**未改 CheatController.cs**。

## 4. 门禁自验（原样计数）

| 门禁 | 命令 | 结果 |
|---|---|---|
| L1 | `bash tools/verify/core-harness.sh` | **7 通过 / 0 失败**（7/7） |
| L2 | `bash tools/verify/compile-check.sh` | **4 个程序集全部零错误**（4/4；App 的 1 个警告与基线一致，非本次引入） |
| L3 | `bash tools/build.sh test` | **total="258" passed="258" failed="0"**；`CheatPanelTests` 15 个用例全绿，含既有 `ErrorTranslator_MapsEngineResultTypes`、`ErrorTranslator_SchemaOob_TranslatedToChinese` |

## 5. 风险与后续建议

- **知识缺口**：无。全部改动使用既有 API（`CheatUiKit.Button`、`CheatAction`、`Regex`），未引入引擎新特性。
- **建议 quality-lead 补单测**（本次按约定不加）：402/401/403 消息 → 期望 Title 与动作列表；`Present` 出口 >120 字截断；"HTTP 402" 不落入超时分支的顺序回归。
- **建议后续**（非本次范围）：把 402/401/403 命中计入 `CheatController.ConsecutiveFailures` 前先做一次密钥健康探测并引导至设置页；HUD 增加"天道通道降级"持久提示；402 命中发遥测事件便于运营及时发现欠费。
