# ChessSage-Unity 全量实施路线图

> 制定日期：2026-10-12 ｜ 制定人：主理人 游承峰
> 依据：用户指令「全量实施、先规划再开工、质量优先不计成本、一比一重制并有所提升、
> 大地图全面重制、加入 Unity 特有功能与拓展、模块化结构」
> 基线：`main = 1066597`（L1 60/60、L2 四程序集零错误、L3 EditMode 61/61）

---

## 0. 阶段诊断结论

当前处于**制作期（Production）中段**：六棋类规则引擎 + AI 编排 + RPG 逻辑层全绿，
象棋垂直切片与六道大陆大地图已可玩，三层质量门禁齐备。

距离「全量一比一重制」的缺口按优先级排列：

| # | 缺口 | 影响 | 判定 |
|:--:|:--|:--|:--|
| 1 | **落子类 3/6 棋种无法交互** | 围棋/五子棋/黑白棋在 Unity 里连"落子"都做不到——Presentation 层对 `IsPlacementGame` 零支持 | 🔴 阻断"全量" |
| 2 | **跳棋六角星棋盘不支持** | `rows:17 × max_col:24` 双倍列坐标，与矩形网格假设冲突 | 🔴 阻断 |
| 3 | **Samsara RPG 层零测试** | 已移植 19 个类到 Core，但 `Assets/ChessSage/Tests/` 无一条 samsara 测试 | 🔴 质量缺口 |
| 4 | **AI 作弊改规指令面板缺失** | 游戏核心 USP（自然语言改规）的 Orchestrator 已在 Core，但无 UI 入口，玩家碰不到 | 🟡 核心体验 |
| 5 | 棋种选择 hub、RPG 表现层、存档系统 | 完整体验链路 | 🟡 |
| 6 | 正式美术（现为程序化占位） | 观感 | 🟢 可后置 |

---

## 1. 里程碑规划

### M5 · 六棋种全可玩（落子类 + 非矩形棋盘 + 棋种 hub）— ✅ 已完成（2026-10-12）

**目标**：六个棋种全部可在 Unity 中完整对弈，含人机对战。

| 任务 | 负责 | 产出路径 | 状态 |
|:--|:--|:--|:--:|
| 落子类交互链路：`BoardInputRouter` → `GameScreen` 落子分支 → `HighlightLayer` 合法落点 → 提子/翻转反馈 | `engineering-lead` | `Assets/ChessSage/Presentation/Board/`、`Game/` | ✅ |
| `BoardView` 泛化：支持矩形网格 + 六角星双倍列坐标两套几何，映射下沉为可插拔 `IBoardLayout` | `engineering-lead` | `Assets/ChessSage/Core/Board/` | ✅ |
| 棋种选择 hub（六个入口 + 方案说明） | `engineering-lead` + `design-strategist` | `Assets/ChessSage/Presentation/Hub/` | ✅ |
| 落子类端到端测试 + samsara 黄金测试（对照 legacy Python 夹具） | `quality-lead` | `Assets/ChessSage/Tests/` | ✅ |

**质量门**：L1 **175/175** · L2 **4/4** · L3 **176/176**（全绿）
**试玩证据**：`Logs/playthrough-placement.log` 走通「hub → 五子棋落子 → AI 应手 → 二回合」
**本轮修复真实缺陷**：D-01（P0，`JArray.Contains` 引用比较致沙盒/Boss/撤离判定全失效）、D-02（五子棋历史重复记录）、D-03（大棋盘被相机裁掉）
**详细报告**：`docs/m5-report.md`

### M6 · AI 作弊改规指令面板（核心 USP）

**目标**：玩家可在对局中用自然语言改规，系统走完「意图 → JSON Patch → Schema 校验 → 生效 → 业力结算 → 识破判定」全链路。

| 任务 | 负责 | 产出路径 |
|:--|:--|:--|
| 指令输入面板 UX 规格（输入、改规预览 diff、确认/撤销、失败反馈） | `design-strategist` | `docs/ux/cheat-command-panel.md` |
| 面板实现 + Orchestrator 接入 + 业力/识破 HUD | `engineering-lead` | `Assets/ChessSage/Presentation/Cheat/` |
| Orchestrator 端到端测试（含 Schema 拒绝路径、业力公式 Δ = C × O^α） | `quality-lead` | `Assets/ChessSage/Tests/OrchestratorTests.cs` |

**质量门**：L1/L2/L3 全绿 + 试玩脚本走通「输入指令 → 改规生效 → 业力变化」。

### M7 · RPG 表现层 + 存档系统

| 任务 | 负责 | 产出路径 |
|:--|:--|:--|
| 存档系统（JSON + 版本迁移 + 校验，含 `SaveMigrator`） | `engineering-lead` | `Assets/ChessSage/Core/Save/` |
| 选关 / 技能树 / 业力 HUD / 剧情 / 结局 / 成就 / 教程 UI | `engineering-lead` + `design-strategist` | `Assets/ChessSage/Presentation/Samsara/` |
| UI 视觉规格与程序化美术规范 | `art-director` | `docs/art/ui-visual-spec.md` |

### M8 · 打磨与发布准备

`quality-lead` 三轮 Playtest + `engineering-lead` 性能剖析 + `release-ops-lead` 发布清单。

---

## 2. 质量门禁（贯穿全里程碑，不降标准）

| 层 | 命令 | 当前基线 |
|:--|:--|:--|
| L1 逻辑回归 | `dotnet test tools/verify/dotnet/ChessSage.Core.Verify.csproj` | 60/60 |
| L2 编译门禁 | `bash tools/verify/compile-check.sh` | 4/4 零错误 |
| L3 真实 Unity | `bash tools/build.sh test` | 61/61 |

**新增测试必须进 L1**（Core 零引擎依赖的设计红利：测试同时覆盖引擎内外两侧）。

## 3. 已知风险与缓解

| 风险 | 缓解 |
|:--|:--|
| 六角星棋盘视觉表达复杂（六角星 + 双倍列坐标） | 抽象 `IBoardLayout`，把"坐标→屏幕"变成可单测的纯函数；配 `IsoGridMathTests` 同级往返校验 |
| 落子类交互与走子类差异大（无"选中"阶段） | `GameScreen` 内按 `IsPlacementGame` 分流，共用 HUD 与回合状态机 |
| samsara 无测试直接做 UI 会反复返工 | 先用 `quality-lead` 补黄金测试（对照 legacy Python），再动表现层 |
| Orchestrator 涉及异步 LLM 调用，测试不确定 | 注入 `ILlmClient` 接口 + Fake 实现，保证 L1 可离线回归 |
| `WindowsStandaloneSupport` 模块缺失（镜像站 404） | 先完成 Linux 可玩链路；Windows 出包列为 M8 独立项，需另行获取模块 |
