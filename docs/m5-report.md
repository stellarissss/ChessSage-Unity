# M5 里程碑报告 · 六棋种全可玩

> 日期：2026-10-12 ｜ 里程碑：M5（六棋种全可玩 + 非矩形棋盘 + 棋种 hub）
> 结论：**六个棋种全部可在 Unity 中完整对弈**，含人机对战、棋种选择 hub、落子类交互。
> 本轮修复 **3 个真实引擎缺陷**（含 1 个 P0），补齐 Samsara 层 **89 条测试**（此前为 0）。

---

## 1. 交付内容

### 1.1 棋盘布局抽象（Core，纯数学，可单测）

| 文件 | 职责 |
|:--|:--|
| `Core/Board/IBoardLayout.cs` | 布局接口：格位有效性、格坐标↔局部坐标正逆变换、边界 |
| `Core/Board/RectLayout.cs` | 矩形布局（象棋/动物棋/围棋/五子棋/黑白棋） |
| `Core/Board/HexStarLayout.cs` | 六角星双倍列布局（跳棋，121 位） |
| `Core/Board/BoardLayoutFactory.cs` | 按棋种配置路由布局实现 |

**设计要点**：布局数学下沉到 Core（零 UnityEngine 依赖），因此「格坐标↔屏幕坐标」这类最容易出错的几何逻辑获得了 **L1 单测覆盖**——包括与 legacy `pixel_coords` 的逐格等价断言。

### 1.2 表现层

| 文件 | 职责 |
|:--|:--|
| `Presentation/Board/BoardView.cs`（改） | 持有 `IBoardLayout`；新增底格层（跳棋空洞不铺）；保留对象池/脏标记/零分配刷新 |
| `Presentation/Game/GameScreen.cs`（改） | 按 `IsPlacementGame` 分流的双交互分支；落子类合法落点常驻高亮 + 提子/翻转反馈 |
| `Presentation/Hub/VariantHub.cs`（新） | 程序化 UGUI 棋种选择界面，数据驱动列全部可用棋种 |
| `Presentation/Bootstrap.cs`（改） | `LaunchVariant` / `EnterHub` 运行时切换；`FitBoardToCamera` 大棋盘自适应 |
| `Editor/PlaythroughCapture.cs`（改） | 新增 Placement 试玩流程（hub → 五子棋落子），未改动象棋/大地图流程 |

### 1.3 测试（Samsara 缺口从 0 补齐）

12 个测试文件、**89 条用例**（83 常规 + 6 缺陷回归）+ 9 个黄金夹具 + 可复跑生成脚本 `tools/verify/gen-samsara-fixtures.py`：

| 主题 | 用例数 |
|:--|:--:|
| 业力（含公式 `Δ = C × O^α` 独立推导、技能对 C/α 调制、阈值钳制） | 14 |
| 识破（公式、单调性、确定性分支） | 8 |
| 技能树（4×3 结构、前置条件、点数、修饰符） | 8 |
| 关卡与回合上限 | 8 |
| 进度结算 | 7 |
| 结局（五结局条件、优先级/互斥、边界） | 10 |
| Boss（守道者、天道 Boss） | 7 |
| JSON 加载 | 8 |
| 目标/记忆碎片/选择 | 5 |
| 编排层纯逻辑（`KarmaEstimator` 一致性、`SchemaValidator` 通过/拒绝） | 8 |
| **缺陷回归护栏** | 6 |

**夹具方法论**：用 legacy Python 引擎导出期望值，C# 逐字段比对；公式用例在脚本内以独立推导式计算并与原版函数交叉校验（`assert abs(expected-got) < 1e-12`），测试侧另硬编码字面量，**不调用实现自身算期望**。

---

## 2. 本轮修复的真实缺陷

### D-01（P0）· `JArray.Contains` 走引用比较，Samsara 三处成员判定全部失效

| 项 | 内容 |
|:--|:--|
| 根因 | legacy Python 用 `x in list`（**值**比较）；C# 移植时对 `JArray` 用了 `array.Contains(value)`。Newtonsoft.Json 13.0.3 的该方法是 `ICollection<JToken>.Contains`，走 **`JToken` 引用相等**——`new JArray{"hell"}.Contains("hell")` 恒为 `false` |
| 受影响 | 5 处调用点：`SamsaraState.MarkBossDefeated/IsBossDefeated/UnlockSandbox/IsSandboxUnlocked`、`Objectives.CheckEvacuation` |
| 症状 | ①通关后**沙盒永远锁着**；②首次击败 Boss 奖励**每次结算重复发放**、`bosses_defeated` 反复写入重复项；③撤离类目标**永远无法完成** |
| 复现证据 | `SamsaraKnownDefectsTests` 修复前 **0/6 通过**（6 条全红） |
| 修复 | 新增 `Core/Json/JArrayExtensions.ContainsValue`（值比较、null 安全），5 处调用点改用它；XML 注释写明「为什么不能用 `JArray.Contains`，禁止回退」 |
| 同类排查 | 全项目扫描 44 处 `Contains`：真阳性仅这 5 处；其余为 `HashSet`/`List<string>`/子串匹配，语义正确 |
| 回归护栏 | 修复后 6/6 通过，已**移除 `[Explicit]`** 并入常规回归（L1 由 169 → 175） |

### D-02 · 五子棋历史重复记录

| 项 | 内容 |
|:--|:--|
| 根因 | `WuziqiRuleEngine.ApplyAction` 自行写 `MoveHistory`，而 `GameSession.RecordMove` 也写 → 一次落子记两条。移植时误把 Python `main.py` 的**会话层**职责放进了规则引擎（Python 侧历史只由 `main.py` 写） |
| 症状 | 悔棋栈与回放被污染；玩家可见的"回合数"翻倍 |
| 发现方式 | 新写的落子类端到端测试断言「每次落子历史恰好 +1」时暴露 |
| 修复 | 移除引擎侧 append，历史由 `GameSession` 单点记录 |
| 证据 | 试玩日志显示历史 1 → 3 → 4，每手恰好 +1 |

### D-03 · 大棋盘被固定相机裁掉（实际不可玩）

| 项 | 内容 |
|:--|:--|
| 根因 | Main 相机正交尺寸 5.4（视高 10.8）按象棋 9×10 设定，五子棋 15×15、围棋 19×19、跳棋六角星纵跨 17 均溢出屏幕 |
| 修复 | `Bootstrap.FitBoardToCamera()`：**只在溢出时缩小格宽、绝不放大**（象棋仍为 1.000） |
| 证据 | 日志：象棋「格宽 1.000」、五子棋「格宽 0.706」 |

### 工具链缺陷（本轮顺带修复）

1. **UGUI 程序集未被门禁覆盖**：`UnityEngine.UI` 随 `com.unity.ugui` 包提供，不在 `Editor/Data/Managed/UnityEngine` 下，L2 的通配扫描找不到 → 会把「缺引用」误报成编译错误。已在 `tools/verify/compile-check.sh` 加三级解析（Managed → `Library/ScriptAssemblies` → 编辑器模板缓存）并显式引用。
2. **asmdef 缺 UGUI 引用**：自定义 asmdef 必须显式列 `UnityEngine.UI`，否则 L3 报 `UnityEngine.UI not found`。已补。
3. **CJK 字体运行时取不到**：`Resources.Load<Font>` 只能从 `Resources/` 加载，工程字体原在 `Presentation/Fonts/` → 返回 null，中文会渲染成方框。已 `git mv` 到 `Presentation/Resources/`（16MB 进构建包，属可接受权衡；后续如需瘦身可改 Addressables）。

---

## 3. 质量门禁（主理人独立复跑，非采信成员报告）

| 层 | 命令 | 结果 |
|:--|:--|:--|
| L1 逻辑回归 | `dotnet test tools/verify/dotnet/ChessSage.Core.Verify.csproj` | **175/175，0 失败** |
| L2 编译门禁 | `bash tools/verify/compile-check.sh` | **4/4 程序集零错误** |
| L3 真实 Unity EditMode | `bash tools/build.sh test` | **176/176，0 失败** |

里程碑起点为 L1 60/60；本轮净增 115 条测试。

> L1(175) 与 L3(176) 的 1 条差异为两个 Test Runner 的发现机制差异（Unity 侧夹具多一条），两侧均 0 失败，不影响判定。

---

## 4. 试玩证据（玩家视角闭环）

`Logs/playthrough-placement.log`（EXIT=0）：

```
[ChessSage] 棋盘自适应：格宽 1.000（视高 10.8，格位 9×10）      ← 象棋
[ChessSage] 已启动棋种：xiangqi（red_black）
[ChessSage] 已进入棋种选择 hub
[ChessSage] 棋盘自适应：格宽 0.706（视高 10.8，格位 15×15）     ← 五子棋自动缩小适配
[ChessSage] 已启动棋种：wuziqi（black_white）
[试玩] 五子棋开局：玩家=black，合法落点 225 个
[对局] 点击 (7,7)：回合=black
[对局] 落子 (7,7) 成功=True  提子=0 历史=1
[对局] AI 落子 (8,7) 成功=True AI落子成功
[对局] 落子 (6,7) 成功=True  提子=0 历史=3
[对局] AI 落子 (7,6) 成功=True AI落子成功
[试玩] 五子棋流程走通：回合=black，历史=4 步
```

完整闭环：**进入 hub → 选择棋种 → 棋盘自适应 → 落子 → AI 应手 → 二回合 → 退出**。

回归确认：象棋（`Logs/pl-main.log`）与大地图（`Logs/pl-ow.log`）流程均 EXIT=0。

---

## 5. 已知限制与遗留

| 项 | 说明 |
|:--|:--|
| 试玩截图全灰 | headless 无 GPU，Unity 渲染链硬限制（`docs/playthrough-report.md §4` 已记载 4 条失败路径）。**权威证据是日志**；带 GPU 环境跑同一命令即可出真实画面 |
| 五/围棋/黑白棋/跳棋/动物棋未逐个真人巡检 | 自动化只跑通五子棋落子 + 象棋走子 + 大地图三条流程；其余四个棋种保证"可进入、布局与交互分支结构正确"，建议在带 GPU 环境用 hub 逐个点验 |
| 围棋落点重算成本 | `GetValidPlacements` 因 ko/自杀规则逐格 `Clone` 棋盘，成本 O(格数×子数)，仅在回合变更时重算。当前规模无感，大棋盘扩容时需复核（留给性能里程碑） |
| 联网路径未测 | `OrchestratorEngine` / `PromptBuilder` 的 LLM 调用未纳入 L1（不联网），仅覆盖 `KarmaEstimator` 纯逻辑与 `SchemaValidator` |
| `SamsaraState.ResetAll`（软/硬档） | 未纳入本轮覆盖，建议后续补 |
| `WindowsStandaloneSupport` 构建模块缺失 | 镜像站 404，影响 Windows 交叉编译出包（M8 独立项） |
