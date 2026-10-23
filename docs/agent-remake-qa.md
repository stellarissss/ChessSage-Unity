# Agent 重构 QA 策略与质量门禁（REMAKE-QA-02）

> **负责人**：严守真（Yan Soujin）· 游戏质量保障与测试工程师
> **委托**：游承峰（主理人）
> **状态**：v2（REMAKE-QA-02）——用户四项裁决（2026-10-23）已回填：投射超限 clamp 放行替换「拒绝」、设计侧 §1.4 锚点表为校准合同、减费技能定为业力乘数、20s 性能 mandate；§7 决策点 8/8 已裁决，残留确认项见 §7.1-R
> **范围**：双层级 LLM 流水线（ChatAI 意图分类 → KarmaEstimator 业力预估 → CodeAI JSON Patch 生成 + Schema 重试）→ **AI Agent + 进程内 MCP 风格工具** 重构的测试策略、回归防线、覆盖率矩阵与发布门控
> **约束遵守**：纯设计任务。本文档基于对仓库的**只读**勘察写成（证据索引见附录 B）；未改动任何 `.cs` / `.json` / 工程文件，未执行 git 操作，未运行任何修改状态的命令。

---

## 0. 执行摘要

### 0.1 质量门判定（advisory）

| 项 | 判定 | 说明 |
|:--|:--|:--|
| 当前基线 | **PASS** | L1（7 探针）/ L2（4 程序集）/ L3（Unity EditMode **262/262 全绿**，`Logs/test-results.xml` total=262 passed=262 failed=0） |
| 重构后可达性 | **CONCERNS** | 新架构的 5 个测试难点（多轮 tool_calls 确定性、token 累计、波动因子复现、轨迹断言、锚点校准）均有可行解；用户四项裁决（2026-10-23）已消除策略分歧（锚点合同 / clamp 放行 / 技能乘数 / 20s mandate），但**自动化的地基仍全部依赖技术侧提供 4 个注入缝**（§7.2）；缝不到位则 agent 只能做黑盒手动测试，质量门失效 |
| 性能 mandate（新增） | **新增门禁项** | 20s 硬超时收尾、前缀稳定性、轮次/延迟遥测入场景矩阵 S10–S12（§2.5）、L1 探针通过标准（§4.4.1）与 Playtest 观察项 MAN-05…12（§6）；与校准有依赖链：**先定型性能配置再重拟合锚点**（§3.6） |
| 最大回归风险 | 已识别 | ① `SamsaraSkillTests.AllowedClassifications_MatchPythonGolden` 与 `SamsaraOrchestrationTests` 的 4 项 KarmaEstimator 测试直接依赖被删除机制（任务书只说"三个测试文件"，实读发现**第 4、5 个隐性受影响文件**，见 §4.2）；② 黄金夹具有**两处**小节跨接被替换轴（§4.1） |

### 0.2 核心结论（6 条）

1. **非确定性 agent 完全可以确定性测试**，前提是把随机性全部挤到三个可注入点上：mock LLM 响应序列（`MockToolHandler` 预编排）、波动因子 RNG（`IRandom` + 固定 seed）、循环上限（maxRounds / tokenBudget 可注入）。三点齐备后，agent 的行为空间在测试中是有限的、可逐场景枚举的（§2.5 十二场景矩阵，含裁决 #3 放行改写与 #4 收尾/预算场景）。
2. **"黄金夹具只覆盖 RPG 层、不受 agent 替换影响"这一判断，经独立实读基本成立，但有两处精确例外**：`samsara_karma.json` 的 `assessor_fallback` 小节与 `samsara_skills.json` 的 `allowed_classifications` 小节。夹具生成器本身（`gen-samsara-fixtures.py`）零 orchestration 依赖、无需改动；但**消费这两个小节的测试必须迁移/重写**（§4.1 有完整证据链）。
3. **待改造/受影响的测试实为 5 个文件**（迁移总览见 §4.2）：`OrchestratorTests`（→ `AgentEngineTests`，真正重写）+ `CheatPanelTests`（仅 Harness 适配）+ `SamsaraOrchestrationTests`（4 删 4 留）+ `SamsaraSkillTests`（1 重写）+ `SaveSystemTests`（+2 增补）；任务书曾列的 `SamsaraKarmaTests` 经独立实读确认 **0 改动**（§4.2.2，RPG 层公式保留不动——裁决 D2）。其中真正需要重写的只有 OrchestratorTests；其余多为"原样保留 + Harness 构造适配"（§4.2 逐文件映射表）。
4. **业力校准合同已签署（裁决 #2）**：设计侧 `docs/agent-remake-design.md` §1.4 六档锚点表（E:1 / D:16 / B:32 / A:48 / C:55 / C+:91；触顶 120 ≈ 25K token；激增 T≥20K·p=0.3·×1.25–1.5；波动 0.8–1.3；chat_cap=5 / 零写入 floor 1）经用户裁决确认为**验收合同**；本文档及夹具中引用工程侧幂律系数的地方一律以该表为准。QA 侧夹具 schema 与测试脚手架对齐该表，治理流程（生产速度配置定型 → 遥测 → 重拟合 → 重签 → 夹具 version+1）见 §3.5/§3.6。
5. **发布放行不是"测试全绿"就够**：新 UX（透明轨迹、业力波动手感）必须过两轮 Playtest 并有量化退出准则；识破锁死是永久性状态，其出现频率直接决定留存，必须用中位关卡进度指标卡住（§6）。
6. **性能是新的质量维度，且与校准存在依赖链**：20s 硬超时收尾、前缀稳定性（字节级）、轮次/延迟遥测已入场景矩阵（S10–S12）与 L1 探针通过标准（§4.4.1）；**先定型性能配置再校准锚点**——`reasoning_effort` 分级与轮次压缩直接改变 T_eff 分布，配置漂移会使六档锚点作废（§3.6 治理流程第 1 步）。

---

## 1. 变更面与测试影响面盘点（基于实读证据）

### 1.1 组件去留矩阵

| 组件 | 处置 | 测试影响 |
|:--|:--|:--|
| `Orchestration/OrchestratorEngine.cs`（1661 行）+ `OrchestratorActions.cs`（1453 行，partial class） | **替换** | OrchestratorTests 18 项全拆；`ProcessCommandAsync(text, context, ct)` 签名与 `GetLogs/GetTokenStats` 需在新引擎上保留等价物 |
| `Orchestration/PromptBuilder.cs`（1388 行） | **删除** | `PromptBuilder.IntentParserSystem` 常量被 FakeLlm 用作分流键（OrchestratorTests.cs:64）——随 mock 方案重写 |
| `Orchestration/KarmaEstimator.cs`（414 行） | **删除** | SamsaraOrchestrationTests 4 项 + OrchestratorTests 3 项（`KarmaDelta_*`、`Offline_*` 的 FallbackAssess 断言） |
| `Orchestration/IntentModels.cs` 的 Classification（A/B/C/C+/D/E/F） | **删除** | OrchestratorTests.BuildIntent 全链路；`SamsaraState.GetAllowedClassifications`（SamsaraState.cs:869-875）换轴；`samsara_skills.json` 夹具 `allowed_classifications` 小节 |
| `Orchestration/LlmClient.cs` | **保留 + 扩展** | 新测试的 mock 地基（§2.2）；`MockHandler` / `HasMock` / `IsAvailable` / `TokenUsageCallback` 契约保留 |
| `Orchestration/SchemaValidator.cs` | **保留** | 6 项 Schema 测试原样保留；新架构下 patch 闸门语义不变 |
| `Cheat/CheatController.cs`（两段式契约） | **保留** | CheatPanelTests 15 项几乎全保留；`SubmitAsync`（预估）→ `Confirm`（入改规栈→应用→IncreaseKarma→HandleOverdraft）顺序是被测核心不变量 |
| `Cheat/CheatErrorTranslator` / `CheatDiffRenderer` / `CheatHistory` / `CheatModels` / `CheatPresetLibrary` | **保留** | 对应测试原样保留；`CostPreview.Cost` 的数据来源从"LLM 预估/预置价目"变为"token 函数"，`CheatCostCalculator.Compute` 签名不变 |
| `Samsara/KarmaAssessor.cs` / `DetectionModel.cs` | **保留** | SamsaraKarmaTests 14 项 + SamsaraDetectionTests 8 项全保留（Δ = C×O^α、溢出结算公式不动） |
| `Presentation/Bootstrap.cs:184`（`WireCheat`） | **改造** | `new OrchestratorEngine(new LlmClient(), ConfigsRoot()) { GameType = variantId }` 装配点；L2 编译 + L3 冒烟覆盖 |
| `Presentation/Cheat/CheatCommandPanel` / `CheatUiKit` | **改造** | 新 UX（透明轨迹）新增面板态；`HardTimeoutSeconds=20` / `SoftHintSeconds=8` / `SimulateConfirm` / `SimulateOpenAndSubmit` 测试钩保留；Playtest 主战场 |
| 存档（`SaveManager.SamsaraSaveAdapter.Capture → GetFullState()`） | **迁移评估** | `allowed_classifications` 是 `GetFrontendState()` 的**派生字段**（SamsaraState.cs:1173），不在持久化 `_data` 中 ⇒ 存档迁移风险低；但换轴后 `GetFrontendState` 键名变化需迁移测试（§4.2-REG-04） |

### 1.2 现有测试对旧机制的依赖点（证据索引）

| 依赖点 | 位置 | 旧机制耦合 |
|:--|:--|:--|
| FakeLlm 三路分流 | OrchestratorTests.cs:50-78 | 按 system prompt 分意图/业力/补丁三股响应——双层级流水线的直接映射 |
| `BuildIntent()` 构造 A-F 分类 JSON | OrchestratorTests.cs:608-632 | 意图分类体系 |
| 日志步骤产物断言 | OrchestratorTests.cs:131-146 | `intent_analysis.parsed_result` / `code_generation.patch_mode` / `patch_operations` |
| `Context()` 组 `allowed_classifications` | OrchestratorTests.cs:585 | 分类门控轴 |
| `KarmaEstimator` 直接构造与断言 | OrchestratorTests.cs:246,262,267,360,375 / SamsaraOrchestrationTests.cs:21,33,45,63 | LLM 业力预估器 |
| `FallbackAssess("C")==40 / "E"==1 / "C+"==80` | OrchestratorTests.cs:406-408 / SamsaraOrchestrationTests.cs:21-29 | 按分类的兜底价目表 |
| Harness 构造 `new OrchestratorEngine(client, null)` | OrchestratorTests.cs:97 / CheatPanelTests.cs:38 | 引擎构造签名 |

---

## 2. Agent 循环测试策略

### 2.1 总纲：把非确定性 agent 变成确定性测试的五条军规

1. **mock 优先于真模型**：所有自动测试零网络、零真实 API key（延续现有 `LlmClient("") + MockHandler` 传统，OrchestratorTests.cs:60）。
2. **响应序列预编排**：mock 不是"返回一个值"，而是"按轮次队列返回一整局脚本"，含错误轮与幻觉轮（§2.2）。
3. **随机性单点注入**：波动因子是唯一的运行时随机源，必须走 `IRandom` 缝 + 固定 seed（§2.4）。
4. **上限可注入**：maxRounds / tokenBudget / 单轮 maxTokens 通过配置注入，测试里压到小值以秒级触发边界（§2.2-L3）。
5. **断言"序列 + 终态 + 账本"三元组**：工具调用序列（顺序与参数）、最终 patch 与对局状态、token 账本（T_eff 与 karma）——缺任何一元都堵不住一类 bug。

### 2.2 LlmClient MockHandler 扩展方案

**现状**（LlmClient.cs:50）：`MockHandler : Func<system, user, temp, maxTokens, model, noThink, Task<LlmRawResponse>>`；`LlmRawResponse { Content, PromptTokens, CompletionTokens }`；用量经 `TokenUsageCallback(prompt, completion)` 上报（LlmClient.cs:44,163-167）。**三个不足**：无 messages 历史（mock 看不到第几轮）、无 tool_calls 载体、无显式错误注入位。

**扩展契约**（交程基磐实现，接口要求详见 §7.2）：

```csharp
// 1) 原始响应扩展：携带本轮工具调用（OpenAI tool_calls 风格）
public sealed class LlmRawResponse {
    public string Content;                       // 纯文本回复（final 轮）
    public JArray ToolCalls;                     // [{id, name, arguments(JSON string)}]
    public int PromptTokens;
    public int CompletionTokens;                 // 含 thinking token（DeepSeek 按输出计费）
    public int ThinkingTokens;                   // 仅 mock 分列展示用；实现合并进 CompletionTokens
    public bool TransportError;                  // true ⇒ ChatToolsAsync 返回 Ok=false（模拟断网/5xx）
}

// 2) 多轮工具调用入口（agent 循环专用；旧 MockHandler/ChatAsync 原样保留给其它调用方）
public Task<LlmResult> ChatToolsAsync(
    IList<ChatMessage> messages, JArray tools,
    double? temperature = null, int? maxTokens = null,
    string model = null, bool noThink = false,
    CancellationToken ct = default);

// 3) 多轮 mock 缝：看得见完整消息历史，按轮次脚本化
public Func<IList<ChatMessage>, JArray, double, int, string, bool, Task<LlmRawResponse>> MockToolHandler;
```

**预编排脚本的三种写法**（测试基建 `tests/AgentTestRig.cs`，随新测试文件提交）：

```csharp
// A. 队列式：严格按调用次序出牌；耗尽后抛 "mock 脚本已耗尽"（防止静默错位）
var script = new LlmScript()
    .Round(tool: "read_board",      args: "{}",                       prompt: 120, completion: 40, thinking: 15)
    .Round(tool: "apply_json_patch", args: PATCH_CHARIOT,             prompt: 260, completion: 90, thinking: 30)
    .Round(final: "已把黑车挪到中路。",                                prompt: 180, completion: 25, thinking: 0);
client.MockToolHandler = script.Handler;

// B. 函数式：按 (轮次, 消息历史) 动态决策——用于「第二轮必然出错、第三轮修正」类场景
client.MockToolHandler = (msgs, tools, t, m, mdl, nt) => Task.FromResult(
    CountToolResultMessages(msgs) == 0 ? Ok("apply_json_patch", OOB_PATCH)   // 首轮故意越界
  : CountToolResultMessages(msgs) == 1 ? Ok("apply_json_patch", GOOD_PATCH)  // 收到错误后修正
  : Final("搞定。"));

// C. 故障式：TransportError=true（断网/超时）；或返回幻觉工具名 / 错乱参数 JSON
```

**token 累计断言**：agent 循环每轮经 `TokenUsageCallback` 上报；`AgentResult` 暴露 `TokenEff`（= Σ(prompt+completion)，thinking 含在 completion 内）与 `Rounds`。测试断言：

- `AGT-02`：脚本 3 轮 → `TokenEff == (120+40)+(260+90)+(180+25)`，且 karma 输入恰为 `TokenEff`（防止实现偷偷重算/漏轮）。
- `KRM-14`：mock 显式分列 `ThinkingTokens=30`，实现侧 `CompletionTokens` 必须含它——用「关 thinking 的对照轮」双跑，差值应恰为 thinking 量。

**多轮对话完整性**：`ChatToolsAsync` 的 messages 必须包含历史 tool 结果消息，否则 agent 无法"从错误中学习"（S2/S7 场景的前提）。mock 的 B 式写法依赖这一点。

### 2.3 黄金轨迹测试（Golden Trajectory）

**定义**：给定（棋种, 玩家输入, seed, mock 脚本）→ 断言（工具调用序列、最终 patch、karma 值/区间、结果类型）。它是 agent 时代的"主链路回归"（取代 OrchestratorTests.MainChain_* 的生态位）。

**轨迹文件格式与存放**：`Assets/ChessSage/Tests/Fixtures/trajectories/<id>.json`（与既有 `Fixtures/*.json` 同目录同评审纪律）。

```json
{
  "id": "TRJ-move-chariot",
  "version": 1,
  "game_type": "xiangqi",
  "seed": 20260101,
  "input": "把我的黑车挪到中路",
  "skill_ids": [],
  "karma_before": 50,
  "rounds": [
    { "round": 1, "tool_calls": [{ "name": "read_board", "arguments": {} }],
      "prompt_tokens": 120, "completion_tokens": 40, "thinking_tokens": 15 },
    { "round": 2, "tool_calls": [{ "name": "apply_json_patch",
        "arguments": [{"op":"replace","path":"/pieces/0/position","value":[6,4]}] }],
      "prompt_tokens": 260, "completion_tokens": 90, "thinking_tokens": 30 },
    { "round": 3, "content": "已把黑车挪到中路。", "final": true,
      "prompt_tokens": 180, "completion_tokens": 25, "thinking_tokens": 0 }
  ],
  "expected": {
    "tool_sequence": ["read_board", "apply_json_patch"],
    "result_type": "applied",
    "final_patch": { "board_state": { "pieces": [ { "id": "b_chariot_1", "position": [6,4] } ] } },
    "karma_exact": 17,
    "karma_range": [14, 20],
    "detection_delta": 0.0
  }
}
```

**字段约定**：

- `karma_exact`：种子化 + 锚点校准后应可精确到整数（clamp(round(...)) 本身是确定函数）；`karma_range` 作为锚点未签署期间的宽断言，签署后二选一，推荐**保留 range 为主、exact 为辅**——exact 会把测试绑死在 f 曲线的具体系数上，曲线微调即全红，失去回归意义。
- `final_patch` 只断言**语义等价**（棋盘上 b_chariot_1 落点），不断言字节序——用现有 `JToken.DeepEquals` 或语义化比较，防 JSON 键序噪声。
- 轨迹**只增不改**：修改已冻结轨迹 = 新 version 文件 + 在 PR 说明中登记理由；回放用「录制模式」（跑真实 agent 录制）→ 人工评审 → 冻结 → CI 只读回放。

**初始轨迹集**（5 条，覆盖 §2.5 正常子集）：

| ID | 场景 | 轮数 | 断言重点 |
|:--|:--|:--|:--|
| TRJ-01 | 移子（黑车到中路） | 3 | read→patch→final；applied；karma 落区间 |
| TRJ-02 | 改走法（马横走，等价旧 FlagshipHorsePatch） | 3 | pieces 配置增量；Confirm 后真实生效 |
| TRJ-03 | 多轮重试后成功（首轮越界被 Schema 拒→修正） | 4 | 序列含"错误轮"；最终 patch 合法 |
| TRJ-04 | 部分失败（两个子目标一个越权） | 4 | partial 语义 + PartialNote；未落地越权项 |
| TRJ-05 | 纯对话（无 patch 的闲聊/提问） | 2 | 无 ModifiedConfigs；零业力；fun/info 路径 |
| TRJ-06 | 投射顶格放行（裁决 #3） | 4 | 警示事件非空；循环未提前停；终值 clamp==120；Confirm 后可确认 |
| TRJ-07 | 20s 收尾半成品（虚拟时钟，裁决 #4） | 3+ | 到点即停；partial 预览 + PartialNote；karma=f(T_stop) ≤ 完整价；无写入对照轨零业力 |

### 2.4 种子化 RNG（波动因子必须可复现）

**契约**：`VolatilityRoller : IRandom { double NextDouble(); }`，生产用 `SystemRandom`（不播种），测试用 `SeededRandom(int seed)`。karma 计算函数签名：

```csharp
public static int ComputeKarma(int tokenEff, IRandom rng);   // 纯函数：无隐藏状态、无时钟、无 IO
```

**测试三件套**：

- `KRM-05 波动区间统计`：seed=20260101，抽 10,000 次 → `min ≥ 0.8 − 1e-9`、`max ≤ 1.3 + 1e-9`、`mean ∈ [1.0, 1.1]`（区间中点 1.05 附近容差 ±0.05）。防"区间写反/端点漂移"。
- `KRM-06 种子复现`：同 seed 重放两次，karma 序列逐项相等；两个不同固定 seed 产出**不同**序列（防"seed 被忽略"的空转测试——这是 flaky 测试最常见的伪装）。
- `KRM-03/04 clamp 边界`：`tokenEff=0, factor 强制取下界` → karma=1（**下界是 1 不是 0**：任何成功作弊至少 1 点）；`tokenEff=10^9` → karma=120。用可注入的 `FixedRandom(value)` 精确压端点，不靠统计碰运气。

### 2.5 场景矩阵（S1–S12）

| # | 场景 | mock 编排 | 核心断言 | 用例 |
|:--|:--|:--|:--|:--|
| S1 | 正常流 | 队列式 3 轮（§2.3 TRJ-01） | 序列正确；patch 落地；karma=clamp(round(f(T_eff)×factor))；Confirm 后对局真实改写 | AGT-01/02, TRJ-01 |
| S2 | Schema 失败重试 | 函数式：首轮越界 patch→工具回错（含坐标与 0-8 范围）→次轮修正 | 错误信息可操作（"超出棋盘范围"+具体值，禁通用兜底——继承旧断言 OrchestratorTests.cs:210-212）；最终 applied；无半成品落地 | AGT-12, TRJ-03 |
| S3 | 越权 patch 拒绝 | 单轮返回含 `/karma`、`/schema`、引擎内部字段的 patch | 拒绝且**零状态变化**（Samsara+Session 深比较）；错误经 CheatErrorTranslator 为人读文案（无残留 JSON Pointer） | AGT-11 |
| S4 | 轮次上限 | 队列无限续（脚本耗尽即失败——见 §2.2-A）→ 注入 maxRounds=8 | 第 8 轮后停止；结果 error；**无 patch、无 karma、无改规栈入栈**；错误文案提示"天道斟酌太久" | AGT-06 |
| S5 | token 上限早停 | 每轮 prompt/completion 拉到 10^5 量级 | T_eff 越 budget 即刻停；同上"三无"；`AgentResult.TokenEff` 如实记录（不截断造假） | AGT-07, KRM-13 |
| S6 | 离线降级 | 无 mock 且空 key；对照组注入 mock | `IsAvailable=false` → offline preview、`Failure.Offline=true`、**零 LLM 调用**（继承 OrchestratorTests.cs:419 断言）；注入 mock 后同一路径恢复可用 | AGT-09 |
| S7 | Agent 幻觉 | 返回不存在工具名 / arguments 非 JSON / 缺必填字段 | 不 crash、不 apply、不扣业力；结构化 error 回灌给 agent（消息历史含 tool result）；要么自我纠正（S2 同款）要么优雅失败；UI 得到可翻译失败 | AGT-04/05 |
| S8 | 玩家中途叫停 | 第 2 轮后 `CancellationToken` 取消 | `OperationCanceledException` 被 CheatController.SubmitAsync 捕获（CheatController.cs:118-122 已有先例）；无状态变化；无 karma；ConsecutiveFailures 语义不被污染 | AGT-08 |
| S9 | 投射超限放行 + Confirm 溢出识破（**裁决 #3 改写**，原「投射超限拒绝」类断言作废） | 脚本产出高 token：投射阶段 raw > singleMaxEff（如 C+ 21K×σ×激增 ≈125）→ 循环**不拒绝**，继续到 final | ① 投射阶段**顶格警示已发出**（警示事件/字段非空，文案含"触顶 120"语义，继承设计 §1.7）；② 循环未提前停止、无 error 预览、无"三无"；③ Confirm 时终值 **clamp 到 120 放行**（终值恰为 120，非 >120、非拒绝、状态可确认）；④ 两段式契约完整走完：入栈→应用→IncreaseKarma→HandleOverdraft→识破结算/锁死/判词（继承 OrchestratorTests.cs:501-528 的断言矩阵，仅换 karma 来源）；⑤ 玩家「收手」早跳车路径另由设计 §1.7 覆盖（有补丁→半价预览；无补丁→零业力失败卡） | AGT-17, TRJ-06 |
| S10 | 超时收尾（20s 硬顶，裁决 #4） | 虚拟时钟推进到 20s 到点；A：到点时 sandbox 已有合法写入；B：到点时无任何写入（生产行为由工程侧定，QA 两用例齐备） | A：**尽力交付当前沙箱补丁**——partial 预览（PartialNote 非空、patch 合法、karma=f(T_stop) 且 ≤ 完整运行价，继承设计 §1.7-A7）；Confirm 契约不变。B：**中断**——即停无预览、零状态变化、零业力、失败卡文案可翻译（"天道自行斩断推演"）。到点由 `HardTimeoutSeconds`(20) 取消令牌触发；测试用虚拟时钟零 wall-clock | AGT-19/20, TRJ-07 |
| S11 | 前缀稳定性（前缀缓存最大化的可观测代理） | mock 捕获每轮 messages 序列；同输入跑两个 rig 实例 | ① 同输入两请求的系统提示 + 工具 schema 序列化 **SHA-256 字节级相等**（禁时钟/随机/会话 id 渗入前缀）；② 同请求相邻两轮前缀为**增量追加**（第 N 轮 messages == 第 N-1 messages + 本轮追加，滚动哈希链断言，无重排/无改写历史）；③ 遥测 `cached_prompt` 字段有记录 | AGT-21/22 |
| S12 | 轮次与延迟预算 | 注入 maxRounds 小值；单轮 maxTokens 拉小；脚本每轮带延迟模型 | ① **maxRounds 熔断**：到点即停；有写入 → partial 预览；无写入 → error；`Rounds` == maxRounds；② **每轮 maxTokens** 截断 → 结构化处理不 crash、不 apply 半截 JSON；③ **遥测字段完整性**：TTFT（首字）/ 轮均延迟 / 总延迟 / 前缀命中（cached_prompt）/ 轮次（Rounds）五字段非空非负，总延迟 ≥ Σ 轮均；④ 典型任务 2-4 轮预算冒烟通过 | AGT-23/24 |

> **S5 vs S9 边界补注（裁决 #3）**：token 预算硬停（60K 工程保险）与业力投射是两条独立闸门——前者到点仍然"三无"停止（S5），后者超 120 只警示不拦截（S9）。勿把 S5 的拒绝语义误扩到业力投射。
> **S10 测试纪律**：虚拟时钟（可控延迟 mock handler / `ITestClock`）把 20s 压到毫秒级，遵守 §4.3 flaky 政策的零 wall-clock 纪律。

---

## 3. 业力校准测试

### 3.1 被测契约（冻结）

```
T_eff = 全轮 prompt+completion 累计（completion 含 DeepSeek thinking token）
raw   = f(T_eff) × σ × surge        σ ∈ [0.8, 1.3]；surge = T_eff ≥ 20,000 时 p=0.3 触发 ×[1.25, 1.5]（混合模型，裁决 #2）
折扣   = 减费技能为业力乘数 m（非 token 折扣，裁决 #6/D6），应用顺序：f × surge × σ × m
karma = clamp(round(raw × m), 1, singleMaxEff)   下限 1（成功即有价/零写入 floor 1），上限 120（= 单次上限 karma_single_max；chat_cap=5 封顶纯对话）
```

> 裁决 #3：投射阶段 raw > singleMaxEff **不拒绝、不拦截**——顶格警示 + 循环继续，Confirm 时 clamp 到 singleMaxEff 放行（§2.5-S9）。singleMaxEff 可被 `karma_capacity_t2a` / `cheat_mastery_t3b` 抬高（+40/次）；clamp 测试以基准 120 为准并参数化（§3.3）。

### 3.2 f 单调性与凸性（KRM-01/02）

表驱动（`[TestCase]` 或夹具驱动，与既有 `SamsaraKarmaTests.DetectionDelta_Formula_Cases_MatchPythonGolden` 的数据驱动风格一致）：

- **单调性**：T_eff ∈ {0, 100, 500, 1000, 2000, 4000, 8000, 16000, 32000} → f 严格递增（相邻对断言 `<`，非 `≤`——T=0 与 T=100 之间也必须严格）。
- **凸性**：同一 C/α 意义上 f 的二阶差分 ≥ 0（采样点密于单调性用例）；凸性是"大改动惩罚非线性"的手感来源，写退化了必须红灯。
- **独立推导原则**（继承旧测试传统：期望值不调用实现同一函数——OrchestratorTests.cs:245 注释）：期望值由测试侧用字面量系数独立计算，f 的系数以命名常量形式出现在测试中并注释出处（设计签署的锚点表）。

### 3.3 clamp 边界（KRM-03/04）

| 用例 | 输入 | 期望 |
|:--|:--|:--|
| KRM-03 下界 | T_eff=0（或最小有效轮），FixedRandom(0.8) | karma=1 |
| KRM-03b 下界不含 0 | 任意 T_eff，FixedRandom(0.8)，round 后为 0 | karma=1（clamp 先于 round 之后的边界值） |
| KRM-04 上界 | T_eff=10^9，FixedRandom(1.3) | karma=120 |
| KRM-04b 上限与单次上限一致 | karma=120 时 `IncreaseKarma(120)` 不溢出（karma=120 恰为安全线——继承 SamsaraKarmaTests.cs:227-240 的 inclusive 边界语义） | 不溢出 |

### 3.4 波动区间（KRM-05/06）

见 §2.4。补充：**分布形状**抽查——10k 样本直方图落 5 个桶，每桶计数 > 0（防"只返回端点两值"的假随机）。

**激增分量（surge，裁决 #2）**：T_eff ≥ 20,000 时 p=0.3 触发 ×[1.25, 1.5]（混合模型：均匀保底 + 阈值激增）。surge 与 σ 从**同一 seed 流派生**（固定抽取顺序：先 σ 后 surge），KRM-06 种子复现断言自动覆盖激增路径。KRM-05 区间统计按有无激增分两支：T_eff=10,000（<20K 无激增）样本 min ≥ 0.8−ε、max ≤ 1.3+ε、mean ∈ [1.0, 1.1]；T_eff=22,000（≥20K）样本 max ≤ 1.95+ε（=1.3×1.5）且**激增触发率 ∈ [0.25, 0.35]**（10k 样本，p=0.3 ± 0.05）。

### 3.5 六档锚点（KRM-07…KRM-12）——数据驱动（合同 = 设计 §1.4，已签署）

锚点是**验收标准**。经用户裁决 #2（2026-10-23），合同为设计侧 `docs/agent-remake-design.md` §1.4 六档校准锚点表（v1，文策渊交付）；**本文档及夹具中引用工程侧幂律系数（c/β）的地方一律以设计 §1.4 表值为准**。QA 侧夹具 schema 与测试脚手架对齐该表：

`Assets/ChessSage/Tests/Fixtures/karma_calibration.json`：

```json
{
  "_meta": {
    "contract": "docs/agent-remake-design.md §1.4 (v1)",
    "signed_off_by": "文策渊（设计）；合同地位经用户裁决 #2 确认（2026-10-23）",
    "formula": "karma=clamp(round(f(T_eff)*sigma*surge*discount),1,singleMaxEff)"
  },
  "anchors": [
    { "id": "E",  "label": "问讯/纯对话（谁现在占优？）", "token_eff": 2500,  "karma_target": 1,  "karma_min": 1,  "karma_max": 5,  "rationale": "chat_cap=5；零写入 floor 1" },
    { "id": "D",  "label": "微调（棋子都变成金色）",     "token_eff": 8000,  "karma_target": 16, "karma_min": 13, "karma_max": 21, "rationale": "读 ui_config + 一个 patch" },
    { "id": "B",  "label": "移子（黑车到中路）",         "token_eff": 12000, "karma_target": 32, "karma_min": 26, "karma_max": 42, "rationale": "读 board_state + 移子补丁" },
    { "id": "A",  "label": "机制（冻结 AI 一回）",       "token_eff": 15000, "karma_target": 48, "karma_min": 38, "karma_max": 62, "rationale": "mechanisms 补丁 + 多半轮确认" },
    { "id": "C",  "label": "改规（马变成炮）",           "token_eff": 16000, "karma_target": 55, "karma_min": 44, "karma_max": 72, "rationale": "读 pieces_* + rules + 校验" },
    { "id": "C+", "label": "铸子（造一个能拐弯的棋子）", "token_eff": 21000, "karma_target": 91, "karma_min": 73, "karma_max": 120, "rationale": "读全量配置 + 完整新子定义；激增可顶格 120" }
  ]
}
```

> **合同要点（裁决 #2）**：f 为分段线性插值过上述锚点的凸函数，T<2,500 为平地价区（f=1），T>21,000 以末段斜率 0.0072/token 延伸；触顶点 f=120 落在 T_eff ≈ 25,000 token；激增 T≥20K·p=0.3·×1.25–1.5；波动 0.8–1.3；chat_cap=5（纯对话业价封顶）/ 零写入 floor 1。原"签署前 `[Explicit]` 挂起"条款作废；**生产速度配置定型后的重拟合治理见 §3.6——先定型性能配置再校准，否则 T_eff 分布漂移、锚点作废**。

**每锚点测试体**（KRM-07…KRM-12 同构）：

```csharp
[TestCaseSource(nameof(AnchorCases))]
public void Anchor_TokenToKarma_LandsInSignedRange(string id, int tokenEff, int min, int max)
{
    for (int seed = 1; seed <= 64; seed++)            // 全波动区间扫描，非单点碰运气
    {
        int karma = KarmaCalculator.ComputeKarma(tokenEff, new SeededRandom(seed));
        Assert.GreaterOrEqual(karma, min, $"{id} seed={seed} 低于区间");
        Assert.LessOrEqual(karma, max, $"{id} seed={seed} 高于区间");
    }
}
```

64 个 seed × 6 锚点 = 384 次断言/轮，毫秒级成本，买"整个波动带内都落在目标区间"的强保证——这比"某 seed 恰好命中"强一个量级。E 档（chat_cap=5）由同一扫描覆盖上界 5；零写入 floor 1 与纯对话零业力路径另由 TRJ-05 与 AGT 系列断言（§5.2）。

### 3.6 校准治理

- 锚点变更 = 设计签署 + 夹具 version+1 + 全轨迹 karma 区间复核（轨迹文件的 `karma_range` 派生自锚点）。
- `f` 系数改动必须同步 §3.2 独立推导期望值，PR 中两处 diff 同时出现才允许合入（防单边改）。

**生产速度配置下重拟合（裁决 #4 联动，治理流程）**：

1. **前置依赖（写进依赖说明）**：性能配置先定型——`reasoning_effort` 分级（工具轮 low / 终轮 high）、轮次压缩（典型任务目标 2-4 轮）、前缀缓存方案、20s 收尾生产行为，由 REMAKE-ENG-02 性能专章交付并冻结。**先定型性能配置再校准，否则锚点作废**：thinking 开关与轮次策略直接改变 T_eff 分布（设计侧 §6.4「先测 S，再拟合」同向；工程侧 D-8 已注明同一依赖）。
2. **遥测采集**：每次提交落盘 (T_eff, karma 终值, 轮数, TTFT, 轮均延迟, cached_prompt, 收手/超时标记)（工程侧联调遥测需求，`token_stats.json` 既有路径，BaseDir 不变）。
3. **校准报告**：各档 P50 对锚点 ±15% 带（设计 §6.4）；QA 平衡巡检项——三改齐发业力分布（防其成为主导策略，超差则提高末段斜率，参数化可调）、临界地带（K+C 略超 120 的手数）占比 30-50%。
4. **参数更新**：超差只改 `agent_remake.json` 锚点/系数，不动代码。
5. **重签**：设计侧签署新锚点表（version+1）。
6. **夹具与门禁同步**：`karma_calibration.json` version+1 → KRM-07…12 期望值更新 → 全轨迹 `karma_range` 复核 → L1 `KarmaAnchorProbe` 内置黄金镜像同步（version 不一致即探针 FAIL，见 §4.4.1-P2）→ §4.3 基线对账。

### 3.7 减费技能：业力乘数（KRM-15…17，裁决 #6/D6）

`realm_insight_t1a` 等减费技能按用户裁决 #6 定为**业力乘数 m**（非 token 折扣——凸函数下 token 折扣更慷慨、会破坏旧技能平衡）。应用顺序（工程侧 §7.3）：`karma = clamp(round(f(T_eff) × surge × σ × m), 1, singleMaxEff)`，折扣在 σ 之后、clamp 之前。

**测试写法**（同场景两跑 + 纯函数钉死 σ）：

```csharp
[Test] public void CostReductionSkill_IsKarmaMultiplier_NotTokenDiscount()
{
    var script  = LlmScript.Standard3Round();               // 同一 mock 脚本两跑
    var bare    = RunTask(script, skill: null,            rng: new SeededRandom(20261023));
    var skilled = RunTask(script, skill: RealmInsightT1a, rng: new SeededRandom(20261023));

    // KRM-17：乘数不得触 token 账本（两跑 T_eff 逐位相等）
    Assert.AreEqual(bare.TokenEff, skilled.TokenEff, "业力乘数不得改变 token 账本");

    // KRM-15：终值之比 = 乘数 m（0.75 = 本道 -25%）；round/clamp 容差 ±1
    double ratio = skilled.Karma / (double)bare.Karma;
    Assert.AreEqual(0.75, ratio, 1.0 / bare.Karma, "终值之比应等于乘数");

    // KRM-16：应用顺序钉死（σ 后、clamp 前）——FixedRandom 免统计
    Assert.AreEqual(
        Clamp((int)Math.Round(F(16000) * 1.0 * 0.75), 1, 120),   // 期望：测试侧独立推导
        KarmaCalculator.ComputeKarma(16000, new FixedRandom(1.0), discount: 0.75));
}
```

- KRM-17 是裁决 #6 的**判别性断言**：若实现误把技能做成 token 折扣（改脚本使 T_eff 缩小），两跑 TokenEff 不等即红灯。
- KRM-15 的比例断言对 clamp 敏感：bare 已触 120 时比例失真——用例选 T_eff=16,000（f=55，远离两端）；乘后越界的组合由 KRM-16 覆盖。
- 技能抬高上限类（`karma_capacity_t2a` +40 / `cheat_mastery_t3b` +40）走 singleMaxEff 参数化：KRM-04b 同款 inclusive 边界断言换 cap=160 再跑一遍（§3.3）。
- 乘数取值（0.75）归设计侧平衡表；QA 只测"比例恒等 + 顺序正确 + token 不动"三条不依赖具体数值的性质，数值变更不需改测试。

---

## 4. 回归防线

### 4.1 黄金夹具独立性验证（独立实读结论）

**结论：判断成立，但需两处精确豁免。**

**证据链**：

1. `tools/verify/gen-samsara-fixtures.py` 的全部 import（第 43-57 行）：`samsara.state / karma / skills / levels / progression / endings / bosses / heaven_boss / detection / objectives / choices / memory_fragments / turn_limit` + `shared.karma_assessor_base`。**零** orchestration / intent / prompt / llm 依赖；输出 9 个 `samsara_*.json`（第 986-1000 行）。
2. 夹具内容逐节核对：`samsara_karma`（状态机 + KarmaAssessorBase 纯函数镜像）、`samsara_detection`、`samsara_skills`、`samsara_levels`、`samsara_progression`、`samsara_endings`、`samsara_boss`、`samsara_state`、`samsara_misc`——全部是 RPG 状态机与确定性数学，无 LLM、无网络、无分类成本逻辑。
3. C# 消费方（实读）：`SamsaraKarmaTests / SamsaraDetectionTests / SamsaraSkillTests / SamsaraLevelTests / SamsaraProgressionTests / SamsaraEndingTests / SamsaraBossTests / SamsaraStateJsonTests / SamsaraMiscTests`——全部走 `SamsaraFixture`（临时目录隔离 state.json），不碰 Orchestration。
4. 棋种黄金夹具（`tools/golden/gen_*_golden.py` → `xiangqi_golden.json` 等）源自规则引擎，与 agent 无关。

**两处例外（跨接被替换轴，必须登记）**：

| 夹具小节 | 生成源 | 现消费方 | 处置 |
|:--|:--|:--|:--|
| `samsara_karma.json` → `assessor_fallback`（键 A/B/C/C+/D/E/F/ZZ） | legacy `karma_assessor_base._fallback_assess` | `SamsaraOrchestrationTests.KarmaEstimator_FallbackTable_MatchesPythonGolden`；`OrchestratorTests.cs:406-408` | KarmaEstimator 删除 ⇒ **两处消费方删除/迁移**；夹具小节保留（legacy 源未动）但标注 orphan |
| `samsara_skills.json` → `allowed_classifications`（fresh/with_t1a/with_t1a_t1b） | legacy `state.get_allowed_classifications` | `SamsaraSkillTests.AllowedClassifications_MatchPythonGolden` | 换轴 ⇒ **重写为新轴断言**；夹具小节按 §7.1-D5 处置 |

**结论性动作**：agent 重构期间 **禁止重新生成** `samsara_*.json`（免噪声）；RPG 层小节一个字节都不用动；例外两小节走上面的迁移/重写流程。

### 4.2 待改造测试文件逐文件迁移方案

**迁移状态总览（2026-10-23，REMAKE-QA-02 盘点；本任务为纯文档任务、仓库零改动，五个文件全部维持「待迁移」）**：

| 文件 | 旧项数 | 迁移后 | 状态 |
|:--|:--:|:--|:--|
| `OrchestratorTests.cs` → `AgentEngineTests.cs` | 18 | ≈6 原样迁移 + 4 删除 + 7 重写（AGT/TRJ）+ 1 技能用例按裁决 #6 重写（KRM-15…17） | 待迁移 |
| `CheatPanelTests.cs` | 15 | 14 原样 + 1 适配（Harness 构造签名） | 待迁移 |
| `SamsaraOrchestrationTests.cs` | 8 | 4 删除 + 4 保留（可选更名 `SchemaValidatorTests`） | 待迁移 |
| `SamsaraSkillTests.cs` | 8 | 7 保留 + 1 重写（新工具权限轴） | 待迁移 |
| `SaveSystemTests.cs` | 现有 | +2 增补（REG-04：v1.0.1 旧档读入 + `GetFrontendState` 新键快照） | 待迁移 |

> 状态列随 REMAKE-ENG-02/03 推进更新：**待迁移** → **迁移中**（Harness 已换新引擎、部分用例已跑绿）→ **已迁移**（新套件全绿且旧断言逐项对账 §4.3-2）。

#### 4.2.1 `OrchestratorTests.cs` → `AgentEngineTests.cs`（18 项 → 拆散重写；迁移状态：**待迁移**）

| 旧测试 | 旧断言要点 | 新归属与映射 |
|:--|:--|:--|
| `MainChain_BoardStateMove_IntentPatchSchemaApply` | 意图分类日志→patch→schema→确认生效 | 拆四路：① 序列/日志 → **AGT-01 + TRJ-01**（工具序列取代"分类日志"）；② schema 独立复验 → 移入 `SamsaraOrchestrationTests`（SchemaValidator 保留）；③ Confirm 生效 → **AGT-17/S9**；④ 意图断言 → **删除**（分类体系不存在） |
| `MainChain_RuleChange_PatchAddsMoveAndAppliesToPiecesConfig` | C 类改走法全链路 | **TRJ-02**（黄金轨迹：改走法） |
| `SchemaReject_OutOfBounds_ReturnsActionableError_NotGeneric` | 越界拒+可操作文案+ErrorTranslator | **AGT-12**（错误文案断言原样继承：含"超出棋盘范围"、点名坐标、禁通用文案、禁残留 Pointer）；后半段 ErrorTranslator 断言 → 并入 `CheatPanelTests`（已有同类用例） |
| `SchemaReject_Validator_SurfacesCoordinateAndRange` | 校验器错误串三要素 | **原样移入** `SamsaraOrchestrationTests`（零改动，SchemaValidator 未替换） |
| `KarmaDelta_DefaultCoefficients_MatchesIndependentFormula` | KarmaEstimator Δ 公式 | **删除**；等价覆盖由 `SamsaraKarmaTests.DetectionDelta_Formula_MatchesHandDerivedLiterals`（RPG 层公式未变）承担 |
| `KarmaDelta_LoweredBySkillTree_MatchesIndependentFormula` | 技能调 C/α | **删除**；等价覆盖由 `SamsaraKarmaTests.SkillTree_Modulates_*` 承担 |
| `Detection_ThresholdBoundaries_AreDeterministic` | Check(0/100)/锁死 | **原样移入** `SamsaraDetectionTests`（该文件已有 8 项，去重后合并，防双份漂移） |
| `Detection_NonPositiveOvershoot_NoSettlement` / `AccumulatesDelta_AndSettles` / `OvershootOf100_AlwaysDetected_AndResetsRealmDetection` | 溢出结算三态 | 同上，并入 `SamsaraDetectionTests` |
| `CostReductionSkill_LowersOvershootAndDelta` | 减费技能→溢出→Δ | 已裁决 #6（D6=**业力乘数**，非 token 折扣）→ 重写为 **KRM-15…17**（§3.7：两跑比例 + 顺序钉死 + token 不动）；RPG 层部分（技能→C/α）已由 SamsaraKarmaTests 覆盖 |
| `Offline_NoApiKey_ReturnsErrorWithoutNetworkOrThrow` | 离线 error + 兜底价目 | 拆：离线 error → **AGT-09**；`FallbackAssess` 三断言 → **删除**（KarmaEstimator 删除）；离线预置价目 → `CheatPanelTests.OfflineController_UsesPresetsWithoutLlm` 已覆盖 |
| `Offline_CheatController_DoesNotCallLlm_OnSubmit` | 离线零调用 | **原样保留**（CheatController 契约未动；断言 0 次 LLM 调用改数 mock 调用计数） |
| `FakeLlm_InjectedClient_IsAvailableWithoutRealKey` | mock 可用性 | **改写**为 `MockToolHandler` 版（AGT-09 一部分） |
| `RuleUndo_RefundsNoKarma_AndLeavesMoveHistoryUntouched` / `RuleStack_IsIndependent_OfGameMoveUndoStack` | 双栈独立 | **原样保留**，仅 Harness 换新引擎（这些是 CheatController 契约测试，与 LLM 无关——未来应迁入 CheatPanelTests 同宗） |
| `Confirm_GuaranteedOvershoot_TriggersDetectionSettlement` | 确认后溢出识破全矩阵 | **S9/AGT-17**：karma 来源从 `Fake.KarmaText="120"` 改为 token 脚本顶到上限；断言矩阵（overshoot/Δ/detected/locked/judgment）逐条继承 |
| `D05_CClassRuleConfig_IsValidatedNotBypassed` | pieces_* 校验不绕过 | **原样保留**（SchemaValidator 未动；D-05 修复回归价值不随架构过期） |

净结果：18 项 → 约 6 项原样迁移（换 Harness）+ 4 项删除（公式/价目，RPG 层已有等价）+ 7 项重写为 agent/轨迹用例 + 1 项技能用例按裁决 #6 重写为 KRM-15…17（原挂起项已消解）。**删除的每一项都在本表登记理由**（审计轨迹，见 §4.3）。

#### 4.2.2 `SamsaraKarmaTests.cs`（14 项 → **0 改动**）

全部走 `SamsaraFixture` + `samsara_karma.json`，纯 RPG 层（KarmaSystem 状态机 / KarmaAssessor 本地镜像 / DetectionModel）。新架构明确保留 `KarmaAssessor` 与 `DetectionSystem`，Δ=C×O^α 与溢出结算不动 ⇒ **14 项全绿保持**。监控点：若设计把 Δ 公式本身也改了（任务书说保留，但要在 §7.1-D2 书面确认），本文件才需动。

#### 4.2.3 `CheatPanelTests.cs`（15 项 → **14 项原样 + 1 项适配**；迁移状态：**待迁移**，唯一适配点 = Harness 构造签名）

CheatController 两段式契约、DiffRenderer、CostCalculator、ErrorTranslator、PresetLibrary 全部保留。唯一变动：Harness 第 38 行 `new OrchestratorEngine(client, null) { GameType = variant }` 的构造签名（§7.2 接口要求：保持同形构造或同步改 Bootstrap+此文件）。`SubmitPreset` 路径不经 LLM ⇒ `AllPresets_ApplyCleanlyToTheirVariant` 等 6 项预置测试天然免疫。

#### 4.2.4 隐性第 4 个文件：`SamsaraOrchestrationTests.cs`（8 项；迁移状态：**待迁移**，4 删 4 留）

| 旧测试 | 处置 |
|:--|:--|
| `KarmaEstimator_FallbackTable_MatchesPythonGolden` | **删除**（KarmaEstimator 删除；`assessor_fallback` 夹具小节 orphan 化，见 §4.1） |
| `KarmaEstimator_DetectionDelta_MatchesFormula` / `IncreaseDecrease_ConsistentWithSamsara` / `SingleMax_Rejected` | **删除**（与 SamsaraKarmaTests 的 Assessor 等价用例重复；留 RPG 层那份） |
| `SchemaValidator_*`（4 项） | **原样保留**，并接收 §4.2.1 移入的 2 项 Schema 断言 ⇒ 该文件更名为语义准确的 `SchemaValidatorTests`（可选，低优先） |

#### 4.2.5 隐性第 5 个文件：`SamsaraSkillTests.cs`（8 项中 1 项；迁移状态：**待迁移**，1 重写 7 留）

`AllowedClassifications_MatchPythonGolden`（第 96-116 行）依赖 `SamsaraState.GetAllowedClassifications`（A-F 轴）。**换轴后重写**为新轴断言（轴语义归设计，§7.1-D5）：同一测试骨架（fixture 驱动 + CollectionAssert），期望值来源从 `samsara_skills.json → allowed_classifications` 改为新夹具小节（或手写推导注释）。其余 7 项（树形/解锁/修饰符）纯 RPG 层，原样保留。

#### 4.2.6 存档与前端状态迁移（REG-04；迁移状态：**待迁移**，`SaveSystemTests` +2 增补）

`allowed_classifications` 是 `GetFrontendState()` 派生字段（SamsaraState.cs:1146-1173），**不在** `GetFullState()` 持久化数据内 ⇒ v1.0.1 旧存档读取无字段冲突。但换轴后 `GetFrontendState` 输出键变化，需新增：① 旧存档读入新引擎不炸（`SaveSystemTests` 已有 v1→v2 迁移用例可参照）；② 新 `GetFrontendState` 键集合快照测试（含新轴键、无旧分类键）。

### 4.3 262 全绿保持策略

**冻结清单（agent 迭代期只读，禁改）**：

```
Assets/ChessSage/Core/{Samsara,Rules,Board,Game,Json,Config,Mechanism,Model,Util,Overworld,Save}/
Assets/ChessSage/Tests/{Samsara*,SaveSystemTests,BoardLayoutTests,Mechanism*,ObjectivesTests,
  PlacementGameTests,JsonPatchTests,Ai*,IsoGridMathTests,Overworld*,VerticalSliceTests,
  RuleEngineGoldenTests,*GoldenTests.cs}
```

**四道闸**：

1. **基线快照对比**：合并前以现有 `Logs/test-results.xml`（262/262）为基线；合并后必须 `total ≥ 262` 且 `failed == 0`；`total` 下降仅当本文件 §4.2 登记过的删除项，逐项对账。
2. **删除审计**：任何被删/改写的旧测试必须在本文件 §4.2 表与 PR 描述中双登记；无登记的失败一律视为回归 bug，不是"预期变化"。
3. **快速全量前置**：每次 agent 侧提交先跑 `dotnet test tools/verify/dotnet/ChessSage.Core.Verify.csproj`（同一批测试源码、无需 Unity 许可证、分钟级），再进 L3；把 262 的守门成本前移到零许可环境。
4. **防顺手破坏**：agent 迭代中 RPG 层测试失败 = 停手检查，禁止"顺手调测试"——RPG 层是黄金夹具锚定的，动它等于动单源真源。

**flaky 政策**：新测试必须零网络、零 wall-clock、种子化；mock 脚本耗尽即失败（§2.2-A）而非静默重试；发现 flaky → 立即 `[Explicit]` 隔离 + 开 Minor 以上 bug，**禁止** CI 静默 retry（污染信号的先例正是旧架构被替换的原因之一）。

### 4.4 L1/L2/L3 门禁扩展点

| 门 | 现状 | 扩展 |
|:--|:--|:--|
| **L1** `tools/verify/core-harness.sh`（7 探针：Xiangqi/Session/Weiqi/Wuziqi/Heibaiqi/Tiaoqi/Dongwuqi；反射发现 `*Probe.Run()`，Program.cs:18-27） | 编译 Core + probes，Mono 运行 | **+4 个探针**（agent 代码在 Core 层，天然被 L1 覆盖）：`AgentSmokeProbe` / `KarmaAnchorProbe` / `TrajectoryReplayProbe` / `LegacyFreeProbe`（7 → 11 探针）。逐个的输入、量化通过标准与 FAIL 判定见 **§4.4.1**；命名遵循 `*Probe` + `public static Run()` 约定即被自动发现 |
| **L2** `tools/verify/compile-check.sh`（4 程序集：Core/Presentation/App/Editor） | 编译期全量 | 机械无需改；`LegacyFreeProbe` 归入 L1 四新探针之一（见 §4.4.1-P4）——**防旧机制复辟**（重构中期最常见的回潮） |
| **L3** `tools/build.sh test`（EditMode，`test-results.xml`） | 262/262 | 新测试文件全部经 asmdef 纳入；门禁脚本增加"test count 单调性"检查（`total ≥ 262`，删除项对账 §4.3-1）；轨迹回放与锚点测试进常规套件（毫秒级，不配 [Explicit]）；新增用例 AGT-19…24 / KRM-15…17 / TRJ-06/07 全部进常规套件（零 wall-clock、虚拟时钟） |
| **dotnet verify**（L1 逻辑层，`tools/verify/dotnet/`） | Core+Tests 同源编译 | 新 agent 测试同样零引擎依赖（纪律：agent 测试文件禁 `using UnityEngine`——延续 OrchestratorTests.cs:32-33 的传统），自动获得双跑能力 |

### 4.4.1 四个 L1 新探针的通过标准（量化）

> 通则：**任一探针 FAIL ⇒ L1 门禁 FAIL**（advisory：判定由 QA 给出，最终放行权在用户）；探针必须零网络、零 wall-clock（虚拟时钟/固定延迟模型）、单探针秒级；mock 脚本耗尽即 FAIL（禁静默错位，§2.2-A）。

**P1 `AgentSmokeProbe`（AgentSmoke 端到端）**

- **探针脚本输入**：内置 3 轮 mock 脚本（`read_board` → `apply_json_patch` → final）+ 固定 seed + 两跑副本（前缀稳定性缩影）；另跑一版「顶格脚本」（token 编排到投射 >120）。
- **通过标准（量化，全部满足）**：① 正常跑：工具序列 == [read_board, apply_json_patch]；patch 落地且 Schema 复验通过；karma ∈ [1,120] 且 == clamp(round(f(T_eff)×σ))；Confirm 后对局真实改写。② 顶格跑：投射阶段**顶格警示事件非空**（文案含"触顶 120"语义）；循环未被提前拒绝、走到 final；终值 karma == 120（clamp 放行，非 >120、非 error）；Confirm 可执行。③ 两跑副本的系统提示 + 工具 schema 序列化 **SHA-256 相等**。④ 零未捕获异常；探针墙钟 < 5s。
- **FAIL 判定**：任一断言失败 / mock 脚本耗尽 / 抛异常 / 哈希不等——出现任一即 L1 FAIL，不进 L2/L3。
- **裁决对齐**：#3（顶格跑即 clamp 放行 + 顶格警示的行为契约）；#2（karma 断言用设计 §1.4 锚点同源夹具）；#4（20s 收尾生产行为由工程侧定，探针只保证正常流不回归）。

**P2 `KarmaAnchorProbe`（KarmaAnchor 抽查）**

- **探针脚本输入**：探针内置六档锚点黄金镜像（合同 = 设计 §1.4：E:1@2,500 / D:16@8,000 / B:32@12,000 / A:48@15,000 / C:55@16,000 / C+:91@21,000，带 version 标签）+ FixedRandom 序列 + clamp 边界点（T=0、T=10^9）。
- **通过标准（量化）**：① 每档 roll=1.0（FixedRandom(1.0)）时 f(T_anchor) 舍入后 == 锚点值，容差 ±1（round 语义）。② 每档 32 个 seed 全波动扫描，karma 全落设计区间（E:1–5 / D:13–21 / B:26–42 / A:38–62 / C:44–72 / C+:73–120）。③ clamp：T=0 → 1；T=10^9 → 120。④ 激增触发率抽查：T=22,000 抽 1,000 次，触发率 ∈ [0.25, 0.35]。⑤ 探针耗时 < 1s。
- **FAIL 判定**：任一档点估偏差 > ±1 / 任一 seed 出界 / clamp 端点错 / 触发率出界 / **镜像 version 与实现系数 version 不一致**（后者直接 FAIL 并提示走 §3.6 治理，禁"先跑着"）。
- **裁决对齐**：#2（锚点合同，改锚点必须镜像 version+1 双改）；#3（clamp 端点是"放行"不是"拒绝"——120 必须可达且可确认）；#6（乘数技能不进本探针，由 KRM-15…17 在 L3 覆盖，保探针秒级）。

**P3 `TrajectoryReplayProbe`（TrajectoryReplay 回放）**

- **探针脚本输入**：`Assets/ChessSage/Tests/Fixtures/trajectories/*.json` 中探针子集（TRJ-01/02/03/05/06/07；虚拟时钟回放，含 S10 收尾轨与顶格放行轨）。
- **通过标准（量化）**：① 每轨回放：工具序列 == `expected.tool_sequence`（逐项相等）；终态 == `final_patch` 语义相等（`JToken.DeepEquals`）；karma ∈ `karma_range`（有 `karma_exact` 时取 exact）；`result_type` 匹配。② 确定性：同轨回放两次，`AgentResult` 除计时字段外逐位相等。③ 全部回放耗时 < 10s。
- **FAIL 判定**：任一轨迹断言失败 / 轨迹 version > 探针支持版本（**明确 FAIL，禁静默 skip**）/ 两次回放不一致 / 轨迹引用未知工具名。
- **裁决对齐**：#3（TRJ-06 钉死"警示 + 继续 + clamp 120 + 可确认"）；#4（TRJ-07 钉死 20s 收尾语义：有写入 → partial 预览按 f(T_stop) 计价且 ≤ 完整价；无写入 → 零业力失败卡）；#2（`karma_range` 派生自已签署锚点）。

**P4 `LegacyFreeProbe`（LegacyFree 防旧机制复辟）**

- **探针脚本输入**：无外部输入；反射 ChessSage.Core 程序集 +（L1 环境有源码树时）源码扫描 + 运行时上下文键探测。
- **通过标准（量化）**：① 反射：`IntentModels.Classification`、`KarmaEstimator`、`PromptBuilder`、`OrchestratorEngine` 四类型在 Core 程序集中**不存在**。② 运行时：构造 cheat 上下文字典，键集合不含 `allowed_classifications`。③ 源码扫描（可用时）：`Assets/ChessSage/{Core,Presentation}` 下 `allowed_classifications` / `FallbackAssess` / `IntentParserSystem` **零出现**。④ `samsara_skills.json` 的 `allowed_classifications` 小节无 C# 消费方（反射/扫描双重确认）。
- **FAIL 判定**：任一旧类型 / 旧键 / 旧符号 / 旧消费方存在——即 L1 FAIL（重构中期回潮是最大隐性风险，此探针是唯一自动守卫）。
- **裁决对齐**：#1（零依赖自建薄循环——不得回潮到双层级流水线或引回旧编排器）；#2（分类体系已废，锚点合同不经 Classification 轴）；D5（工具权限轴落地即旧轴彻底死亡的守卫，REG-03 的执行化）。

---

## 5. 覆盖率矩阵

**工具面**（8 个进程内工具）：T1 `read_board` · T2 `read_config_slice` · T3 `apply_json_patch` · T4 `move_piece` · T5 `place_piece` · T6 `set_objective` · T7 `rollback` · T8 `query_karma`
**错误路径面**：E1 幻觉工具名 · E2 参数错乱 · E3 目标不存在 · E4 越权目标 · E5 业务非法（schema/走法/占格）· E6 重复/空栈 · E7 读工具零突变
**业力面**：K1–K6 锚点 · C-clamp · V-波动

### 5.1 工具 × 错误路径矩阵

| 用例 | 工具 | 错误路径 | 场景/断言要点 | 类型 | 门禁 |
|:--|:--|:--|:--|:--|:--|
| AGT-10 | T1/T2/T8 | E7 | 三次读操作前后 GameSession+Samsara 状态 `JToken.DeepEquals` 深比较，零突变 | 集成 | L1/L3 |
| AGT-04 | 任意 | E1 | mock 返回 `name:"delete_everything"` → 结构化 error 回灌；不 crash/不 apply；随后自我纠正或优雅失败 | 集成 | L1/L3 |
| AGT-05 | T3/T4/T5 | E2 | arguments 非 JSON / 缺必填 / 类型错（string 当 int）三种变体，同上断言 | 集成 | L1/L3 |
| AGT-13 | T3 | E3 | patch 指向不存在 piece id → 错误含 id 与可用列表提示 | 单测 | L3 |
| AGT-11 | T3 | E4 | patch 触及 `/karma`、schema 文件、引擎内部键 → 拒绝 + 零状态变化 + 人读文案 | 集成 | L1/L3 |
| AGT-12 | T3 | E5 | 越界坐标 → 错误含"超出棋盘范围"+具体值+合法范围（0-8/0-9）；次轮修正后 applied | 集成 | L1/L3 |
| AGT-14 | T4 | E5 | 非法走法（马走田外）/ 落己方 occupied 格 → 拒；合法 → 过 | 单测 | L3 |
| AGT-15 | T5 | E5 | 占格放置拒；空位放置过；放置类棋种（wuziqi）冒烟 | 单测 | L3 |
| AGT-16 | T6 | E3/E5 | 未知目标类型拒；合法目标（checkmate/capture_count）设入并可读回 | 单测 | L3 |
| AGT-13b | T7 | E6 | 回退一步 patch → 状态精确回到快照；空栈回退 → 结构化错误，不 crash | 集成 | L1/L3 |
| AGT-03 | T3 | E6 | 同一 patch 二次 apply → 第二次无变化（diff 空 → "和当前棋规没什么两样"反馈，继承 CheatPanelTests.cs:103-108 语义） | 集成 | L3 |
| AGT-01 | T1→T3 | — | S1 正常流：序列 read→patch→final；对局真实改写 | 集成 | L1/L3 |
| AGT-02 | — | — | token 账本：T_eff=Σ(prompt+completion)；karma 输入=TokenEff | 单测 | L1/L3 |
| AGT-06 | — | — | S4 轮次上限：maxRounds=8 注入；停后续错、三无（无 patch/karma/栈） | 集成 | L1/L3 |
| AGT-07 | — | — | S5 token 上限早停；三无；TokenEff 如实记录 | 集成 | L1/L3 |
| AGT-08 | — | — | S8 取消：中途 ct 取消 → 捕获 → 零状态变化 | 集成 | L3 |
| AGT-09 | — | — | S6 离线：无 mock 无 key → offline 预览 + 零调用；注入 mock → 恢复 | 集成 | L1/L3 |
| AGT-17 | — | — | S9 投射顶格放行 + Confirm 溢出识破全矩阵（裁决 #3 改写；继承 OrchestratorTests.cs:501-528 断言矩阵，karma 来源换 token 脚本） | 集成 | L3 |
| AGT-18 | T1-T8 | 全部 | **轨迹日志完整性**：每轮工具名+参数+结果入 `AgentResult.Trajectory`，UX 可渲染（透明轨迹的数据契约） | 单测 | L3 |
| AGT-19 | — | — | S10-A 超时尽力交付：虚拟时钟 20s 到点 + sandbox 已有合法写入 → partial 预览（PartialNote 非空、patch 合法、karma=f(T_stop) ≤ 完整运行价）；Confirm 契约不变 | 集成 | L3 |
| AGT-20 | — | — | S10-B 超时中断：到点即停、无预览 → 零状态变化、零业力、失败卡文案可翻译；与 AGT-19 共用脚本只换终态期望（生产行为归工程侧定稿） | 集成 | L3 |
| AGT-21 | — | — | S11 前缀字节级一致：同输入两请求系统提示+工具 schema 序列化 SHA-256 相等（禁时钟/随机/会话 id 渗入前缀） | 单测 | L1/L3 |
| AGT-22 | — | — | S11 增量追加：同请求相邻两轮 messages 前缀哈希链一致（历史只追加、不重排、不改写） | 单测 | L1/L3 |
| AGT-23 | — | — | S12 maxRounds 熔断（到点停；有写入→partial / 无写入→error）+ 单轮 maxTokens 截断时结构化处理不 crash | 集成 | L3 |
| AGT-24 | — | — | S12 遥测字段完整性：TTFT/轮均延迟/总延迟/cached_prompt/Rounds 五字段非空非负、总延迟 ≥ Σ轮均；典型任务 2-4 轮预算冒烟（虚拟时钟） | 单测 | L3 |

### 5.2 黄金轨迹矩阵

| 用例 | 轨迹 | 断言序列 | 断言终态 | 断言业力 | 门禁 |
|:--|:--|:--|:--|:--|:--|
| TRJ-01 | 移子 | [read_board, apply_json_patch] | b_chariot_1→(6,4)，Schema 复验通过 | range（签署后加 exact） | L1/L3 |
| TRJ-02 | 改走法 | [read_config_slice, apply_json_patch] | horse moves +1；Confirm 后 PiecesA 真实+1 | range | L1/L3 |
| TRJ-03 | 重试后成功 | [read_board, apply_json_patch(越界), apply_json_patch(修正)] | 最终 patch 合法；无半成品 | range | L1/L3 |
| TRJ-04 | 部分失败 | [read_config_slice, apply_json_patch, apply_json_patch(越权)] | partial 语义；越权项未落地；PartialNote 非空 | range | L3 |
| TRJ-05 | 纯对话 | []（无工具） | 无 ModifiedConfigs；fun/info 路径；**零业力** | 0 | L3 |
| TRJ-06 | 投射顶格放行 | [read_board, apply_json_patch, apply_json_patch, final] | 警示事件非空；循环未提前停；终值 clamp==120；Confirm 后溢出结算 | 120（clamp） | L1/L3 |
| TRJ-07 | 20s 收尾半成品 | [read_board, apply_json_patch] + 到点截断 | partial 预览 + PartialNote；karma=f(T_stop) ≤ 完整价；无写入对照轨零业力 | range(T_stop) | L1/L3 |

### 5.3 业力 × 场景矩阵

| 用例 | 目标 | 断言 | 门禁 |
|:--|:--|:--|:--|
| KRM-01 | f 单调性 | 9 点严格递增（独立推导期望） | L1/L3 |
| KRM-02 | f 凸性 | 二阶差分 ≥ 0 | L1/L3 |
| KRM-03 | clamp 下界 | T=0/factor=0.8 → 1；round 后为 0 → 1 | L1/L3 |
| KRM-04 | clamp 上界 | T=10^9 → 120；120 为可达终值且 Confirm **放行**（裁决 #3：不拒绝、不拦截，仅投射阶段顶格警示） | L1/L3 |
| KRM-05 | 波动区间 | 10k 样本 min≥0.8−ε，max≤1.3+ε，mean∈[1.0,1.1]，5 桶直方图非空 | L3 |
| KRM-06 | 种子复现 | 同 seed 同序列；异 seed 异序列 | L1/L3 |
| KRM-07…12 | 六档锚点 K1–K6 | 每档 64 seed 全落 [min,max]（区间值待设计签署，§3.5） | L1/L3（签署后） |
| KRM-13 | 溢出早停联动 | T_eff 超预算 → karma 不结算、不改规（与 AGT-07 双断言） | L1/L3 |
| KRM-14 | thinking 计费 | completion 含 thinking；关 thinking 对照轮差值恰为 thinking 量 | L3 |
| KRM-15 | 减费技能业力乘数 | 同场景两跑：终值之比 == m（0.75，round/clamp 容差 ±1，§3.7） | L3 |
| KRM-16 | 乘数应用顺序 | FixedRandom 钉死：clamp(round(f×σ×m),1,120)——σ 后、clamp 前（工程 §7.3） | L3 |
| KRM-17 | 乘数不触 token | 两跑 TokenEff 逐位相等（判别 token 折扣误实现，裁决 #6） | L3 |

### 5.4 回归/门禁矩阵

| 用例 | 目标 | 门禁 |
|:--|:--|:--|
| REG-01 | 黄金夹具 RPG 层 250+ 项不受影响（§4.1 结论的执行化断言） | L1/L3 |
| REG-02 | CheatController 两段式契约 14 项保持（§4.2.3） | L3 |
| REG-03 | 旧类型/旧上下文键消失守卫（LegacyFreeProbe，§4.4） | L1 |
| REG-04 | 存档迁移：v1.0.1 旧档读入 + GetFrontendState 新键快照（§4.2.6） | L3 |
| REG-05 | 装配点：Bootstrap WireCheat 新引擎构造 + 面板绑定冒烟 | L2/L3 |
| REG-06 | 262 基线单调性（§4.3-1） | L3 脚本 |
| MAN-01…12 | Playtest 手动观察项（§6；MAN-05…12 = 20s 完成率/等待焦虑/收手/顶格警示等性能项） | 人工 |

---

## 6. 发布前门控：Playtest 与 QA 签字

### 6.1 Playtest 第 1 轮（内部焦点组，6–8 人，含 ≥2 名全新玩家）

**目标**：新 UX 首次接触——**透明轨迹**（玩家能否看懂 agent 做了什么）与**业力波动手感**（代价是否可解释、可预期）。

| 观察指标 | 采集方式 | 退出准则 |
|:--|:--|:--|
| 轨迹可读性 | 每局后问"刚才天道替你改了什么"，按复述准确度评分（全对/部分/说不出） | **≥70% 全对或部分对**；无人把 agent 行为描述为"随机乱改" |
| 业力可解释性 | 出示 3 笔历史业力，问"为什么这手 23 点" | ≥70% 能引用"改动大小/用掉的计算量"任一正确维度 |
| 确认决策时长 | 面板埋点（预览出现 → 确认/取消） | 中位数 ≤45s；Danger 档无 <5s 的冲动确认 |
| 误确认率 | 取消后追问"你想改的是这个吗" | ≤15% |
| 取消净残留 | 自动化守卫 AGT-08/S8 + 人工抽查 | **0 例状态残留（硬性）** |
| 轨迹面板性能 | 帧时间/卡顿投诉 | 无 P1 以上性能投诉 |
| MAN-05 20s 内完成率 | 面板埋点（提交→预览/失败卡墙钟） | **≥95% 任务 ≤20s**；任一 >20s = P1 bug（硬性） |
| MAN-06 等待焦虑评分 | 每局后 1–5 量表 + 追问 | 中位 ≤2.5；8s 软提示（SoftHintSeconds）被注意率 ≥60% |
| MAN-07 收手功能使用率 | 埋点（收手按钮/Esc） | ≥50% 玩家至少主动收手 1 次；收手者能复述"省下后续推演" ≥70% |
| MAN-08 顶格警示时的玩家决策 | 警示出现后 收手/继续/取消 埋点 + 追问 | 三分布记录在案；继续者对 clamp 120 放行的反馈归类（"天道容下" vs "顶格还放行？"）——数据回流设计 §1.7 文案 |

**轮次间动作**：轨迹面板文案、业力提示文案、锚点区间微调（设计侧）；**不允许**为过准则而改公式方向。

### 6.2 Playtest 第 2 轮（外部/新鲜样本，8–12 人，中盘系统）

**目标**：难度曲线与业力压力——token 业力是否把"作弊"塑造成**有张力的资源决策**而非惩罚或白给。

| 观察指标 | 采集方式 | 退出准则 |
|:--|:--|:--|
| 业力获取曲线 | 每手 karma 落点 vs 六档锚点预期分布 | ≥80% 落点与锚点档位吻合（校准偏差即设计返工） |
| 溢出频率 | 会话内 Overshoot>0 占比 | 中位 20–40%（低于 20% 无压力，高于 40% 过惩罚） |
| 识破遭遇与锁死 | Detection 锁死时的关卡进度分布 | **中位锁死进度 ≥ 第 3 关**；锁死率 ≤25%/会话 |
| 识破情感反应 | 锁死时刻访谈（戏剧性 vs 挫败） | ≥60% 描述含"紧张/刺激/我的错"类归因；无人称"莫名其妙" |
| Danger 档二次确认 | 强制二次确认的通过与改小步率 | 误确认 ≤10%；≥30% 选择拆小步（说明档位可读） |
| 离线降级发现率 | 断网场景下预置指令入口发现 | ≥80% 在 30s 内找到并使用预置 |
|  quit 点 | 退出时刻归因 | 无因"看不懂业力/轨迹"退出者 |
| MAN-09 分档位 20s 完成率 | 埋点按 D/B/A/C/C+ 分档统计 | D/B 中位 ≤8s；A/C ≤12s；C+ ≤18s；总体 ≥95% ≤20s |
| MAN-10 收手作为资源决策 | 收手率 + 收手后半价预览确认率 | 收手后确认率 ≥40%（收手是有效策略非惩罚）；收手率 10–40% 为健康带，两个极端都标记设计侧 |
| MAN-11 顶格警示决策分布 | C+/多改请求警示后行为 | 记录 收手/继续/取消 三分布；≥80% 继续 → 警示文案失效返工；≥80% 收手 → 张力不足 |
| MAN-12 流式进度观感 | 帧时间/首字等待投诉 | 无 P1 以上性能投诉；首字等待 ≤8s 占比 ≥90% |

### 6.3 QA 签字放行条件清单

QA 签字 = 以下**全部**满足（advisory 判定，最终放行权在用户）：

1. L1（含 4 个新探针，§4.4.1）/ L2 / L3 全绿；L3 `test-results.xml` `failed=0` 且 `total ≥ 262 + 新增项`，删除项与 §4.2 登记逐项对账。
2. §5 覆盖率矩阵中 L1/L3 项 **100% 实施**；`[Explicit]` 挂起项清零（锚点已签署——裁决 #2；性能配置定型后完成一轮重拟合，§3.6）。
3. 开放 Bug：**Blocker/Critical = 0**；Major 全部有规避方案或设计确认接受。
4. flaky = 0：连续 3 次全量 L3 无间歇失败；无被静默 retry 的用例。
5. 六档锚点已签署（裁决 #2 = 设计 §1.4）；`karma_calibration.json` version 与实现系数一致（§3.6 双边 diff 规则）；**性能配置定型后已完成一轮重拟合**（§3.6 治理流程）——签字前性能配置若有变更，锚点复核为签字前置项。
6. Playtest R1/R2 退出准则全部达成（含性能观察项 MAN-05…12）；UX 文案改动已回填。
7. 存档迁移用例（REG-04）绿；v1.0.1 旧档实测读取通过。
8. 发布说明含"业力算法变更"玩家可见说明（波动区间、上限、识破规则），经设计过目。
9. 性能 mandate 达标：遥测/Playtest 实测 20s 内完成率 ≥95%，无 P1 性能投诉（MAN-05/09）；`reasoning_effort` 分级与轮次压缩配置已冻结（"先定型性能配置再校准"的依赖链闭合，§3.6）。

---

## 7. 待拍板决策点与接口要求

### 7.1 决策点（需主理人/用户拍板，附 QA 推荐）

| # | 决策点 | QA 推荐 | 用户裁决（2026-10-23） |
|:--|:--|:--|:--|
| D1 | **六档锚点数值与 f 曲线形状**（设计侧所有权，QA 不臆造验收标准） | （历史推荐，已被裁决覆盖）provisional 六档由设计交付、QA 脚手架先行合入、签署后点亮 | ✅ **裁决 #2：设计侧 §1.4 锚点表即验收合同**（文策渊已交付，见 `agent-remake-design.md` §1.4）；脚手架先行、数值点亮走治理；生产速度配置定型后重拟合（§3.6） |
| D2 | **Δ = C×O^α 与溢出结算确属保留不动**（任务书表述为保留，需书面确认） | 书面确认；若动，`SamsaraKarmaTests`/`SamsaraDetectionTests` 共 22 项 + 夹具全链受影响，工作量翻倍——强烈建议保留 | ✅ 书面确认：保留不动 |
| D3 | **mock 注入方式**：扩展 `LlmClient`（`MockToolHandler` + `ChatToolsAsync`）vs 新建 `IToolCallingClient` | **扩展 LlmClient**：单一 mock 缝、L1/L2 零改、延续既有 FakeLlm 传统；旧 `MockHandler` 原样保留给非 agent 调用方 | ✅ 扩展 LlmClient |
| D4 | **轨迹文件治理**：`Fixtures/trajectories/*.json` 手工编写 vs 录制模式生成 | 录制模式（真实 agent 跑一轮录制）→ 人工评审 → 冻结 → CI 只读回放；只增不改，改即新 version | ✅ 录制治理 |
| D5 | **分类轴替代语义**：`GetAllowedClassifications`（A-F）换成什么、`samsara_skills.json` 的 `allowed_classifications` 小节怎么办 | 设计定义新轴（建议：按工具白名单档位，仍由 cheat_mastery 技能门控——保留"解锁更强作弊"的技能叙事）；夹具小节从 legacy 重新生成或手写带 derivation 注释 | ✅ 工具权限轴（工程 D-3a + 设计「天机型谱」，三方一致）；夹具小节重生成 |
| D6 | **减费技能在新公式下的归宿**：`realm_insight_t1a` 的"本道 -25%"改成 token 折扣、业力折扣、还是废弃 | 推荐**token 折扣**（技能减少 agent 工作量，语义自洽且可测：同场景带技能/不带技能两跑 T_eff 之比）；`CostReductionSkill` 测试随之重写而非删除 | ⚠️ 裁决：**业力乘数**（工程侧 §7.4，精确保留旧技能平衡——凸函数下 token 折扣更慷慨、会改变平衡）；QA 的 token 折扣留作备选，若试玩发现平衡问题再议 |
| D7 | **预置指令定价一致性**：离线预置固定价 vs 在线 token 业力，玩家是否感知割裂 | 推荐预置保持固定价但 UI 标注"预置价"，R2 Playtest 验证公平感；若访谈负面再统一换算 | ✅ 固定价 + UI 标注"预置价"（设计 D-3 同向） |
| D8 | **循环上限生产值**：maxRounds / tokenBudget 生产默认值 | 工程按 DeepSeek flash 实测定价建议；测试必须可注入（§2.1-4），生产值设计侧确认手感 | ✅ 由 REMAKE-ENG-02 与性能专章（20s mandate）一并定价 |

> **回填说明（2026-10-23，主理人）**：另新增用户性能 mandate（裁决 #4：单次任务硬超时 20s、越快越好、允许压缩思考）——场景矩阵 S 系列更新（clamp 放行替换原「拒绝」、新增超时收尾/前缀稳定性/轮次延迟预算）、L1 探针通过标准细化、校准治理补充，由 REMAKE-QA-02 执行。

**§7.1-R 残留确认项（裁决后新增，需主理人/用户或对应成员确认，附 QA 建议）**：

| # | 事项 | QA 建议 |
|:--|:--|:--|
| R1 | **S10 生产行为 A/B 选择**：20s 到点是「尽力交付当前沙箱补丁」（partial）还是「中断」（工程侧 §5.1 伪码倾向：轮次耗尽且有写入 → 按 sandbox 收尾） | 生产取 A（partial）；B 留作故障注入对照。AGT-19/20 两用例已备，门禁期望随工程定稿二选一 |
| R2 | **设计侧 §1.7/§6.3 的 45s 硬超时与裁决 #4 的 20s 冲突**（设计文档成文于裁决前） | 按裁决 #4 = 20s 执行；请文策渊回改设计文档表述，QA 门禁已按 20s |
| R3 | **maxRounds 生产值**：工程现提案 12（其 §5.2），设计 §1.7 提 8；裁决 #4 要求典型任务 2-4 轮 | REMAKE-ENG-02 性能专章定价；测试可注入（§2.1-4），门禁不绑死具体值 |
| R4 | **E 档区间粒度**：设计 §1.4 表 E 行"新波动区间=1"，裁决 #2 又定 chat_cap=5 | QA 按 [1,5] 执行（chat_cap 封顶 + floor 1）；请设计确认表述统一 |

### 7.2 技术侧接口要求（程基磐）

1. **LlmClient**：`LlmRawResponse` 增 `ToolCalls` / `ThinkingTokens` / `TransportError`；新增 `ChatToolsAsync(messages, tools, …)` 与 `MockToolHandler`（签名见 §2.2）；messages 历史对 mock 可见；`TokenUsageCallback` 每轮必达。
2. **Agent 循环**：`IRandom` 注入缝（波动因子）；maxRounds/tokenBudget/单轮 maxTokens 可注入；`CancellationToken` 贯通到每轮 LLM 调用；结果对象暴露 `TokenEff` / `Rounds` / `Trajectory`（有序工具调用+参数+结果，UX 与测试共用）。
3. **KarmaCalculator**：纯静态函数 `ComputeKarma(tokenEff, IRandom)`；无隐藏状态/时钟/IO；clamp 常量 [1,120] 命名化并与 `karma_single_max` 对齐。
4. **工具层**：显式注册表白名单（幻觉工具名天然被拒）；工具永不抛异常——错误以结构化结果（ok/errorCode/人读文案）返回，由循环回灌 agent；读工具零突变（可被 AGT-10 深比较证伪）。
5. **装配兼容**：`Bootstrap.WireCheat` 新引擎构造与 CheatController 绑定；`OrchestratorEngine` 旧构造签名保留同形或三处（Bootstrap/OrchestratorTests/CheatPanelTests）同步改。
6. **上下文键**：`context["allowed_classifications"]` 替换为新轴键（命名待 D5），旧键不得残留（REG-03 守卫）。

### 7.3 设计侧接口要求（文策渊）

1. 六档锚点表（D1）与 f 曲线形状（凸、递增、锚点经过）。
2. 新技能门控轴语义（D5）——技能描述文案同步。
3. 减费技能归宿（D6）与预置定价口径（D7）。
4. Playtest 两轮的简报材料（新 UX 说明、业力规则一页纸）与访谈提纲共审。

---

## 附录 A. 测试用例编号总表

| 编号段 | 数量 | 归属 |
|:--|:--|:--|
| AGT-01…24 | 24 | Agent 循环（§2.2/2.5/5.1；AGT-19…24 = 超时收尾/前缀稳定性/轮次与延迟预算） |
| TRJ-01…07 | 7 | 黄金轨迹（§2.3/5.2；TRJ-06 投射顶格放行 / TRJ-07 20s 收尾） |
| KRM-01…17 | 17 | 业力校准（§3/5.3；KRM-15…17 = 减费技能业力乘数，裁决 #6） |
| REG-01…06 | 6 | 回归与门禁（§4/5.4） |
| MAN-01…12 | 12 | Playtest 手动（§6；MAN-05…12 = 性能 mandate 观察项） |

## 附录 B. 证据索引（实读文件与关键行号）

| 文件 | 关键内容 |
|:--|:--|
| `Assets/ChessSage/Tests/OrchestratorTests.cs` | FakeLlm（50-78）、Harness（81-109）、MainChain（115-187）、SchemaReject（193-236）、KarmaDelta（242-280）、Detection（286-346）、CostReduction（352-387）、Offline（393-435）、双栈（441-498）、Confirm 溢出（500-528）、D-05（546-572）、Context（585） |
| `Assets/ChessSage/Tests/SamsaraKarmaTests.cs` | 夹具等价性全套（23-107）、公式独立推导（131-171）、技能调制（175-207）、钳制边界（226-273） |
| `Assets/ChessSage/Tests/CheatPanelTests.cs` | Harness（24-45）、DiffRenderer（51-108）、CostCalculator（112-130）、ErrorTranslator（134-169）、Presets（173-216）、两段式契约（220-317） |
| `Assets/ChessSage/Tests/SamsaraOrchestrationTests.cs` | KarmaEstimator 4 项（21-69）+ SchemaValidator 4 项（73-123） |
| `Assets/ChessSage/Tests/SamsaraSkillTests.cs` | AllowedClassifications（96-116） |
| `Assets/ChessSage/Tests/SamsaraTestHelper.cs` / `TestEnv.cs` | 夹具隔离与断言辅助（59-118）/ 路径解析（14-66） |
| `Assets/ChessSage/Core/Orchestration/LlmClient.cs` | MockHandler（50）、TokenUsageCallback（44,163）、ChatAsync（66-161） |
| `Assets/ChessSage/Core/Cheat/CheatController.cs` | 两段式契约注释（21-24）、SubmitAsync（92-139）、Confirm（204-246）、UndoRule（262-270）、BuildConfigs（275-286） |
| `Assets/ChessSage/Core/Samsara/SamsaraState.cs` | GetAllowedClassifications（869-875）、GetFrontendState（1146-1173） |
| `Assets/ChessSage/Core/Save/SaveManager.cs` | SamsaraSaveAdapter.Capture→GetFullState（272-284） |
| `Assets/ChessSage/Presentation/Bootstrap.cs` | WireCheat 装配点（~184） |
| `Assets/ChessSage/Presentation/Cheat/CheatCommandPanel.cs` | HardTimeoutSeconds=20/SoftHintSeconds=8（31-35）、Simulate 钩（758-767） |
| `tools/verify/gen-samsara-fixtures.py` | import 清单（43-57）、karma 生成（111-243）、skills 生成（328-430）、main（986-1000） |
| `tools/verify/core-harness.sh` / `compile-check.sh` / `build.sh` | L1 编译+探针 / L2 四程序集 / L3 EditMode |
| `tools/verify/probes/Program.cs` / `ProbeEnv.cs` | 反射发现 *Probe（18-27）/ 探针环境 |
| `tools/verify/dotnet/ChessSage.Core.Verify.csproj` | Core+Tests 同源编译（net6.0） |
| `Logs/test-results.xml` | total=262 passed=262 failed=0（基线证据） |

---

*本文档由严守真（QA）交付。所有门禁判定为 advisory；最终放行权在用户。*
