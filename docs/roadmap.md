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

### M6 · AI 作弊改规指令面板（核心 USP）— ✅ 已完成（2026-10-14）

**目标**：玩家可在对局中用自然语言改规，系统走完「意图 → JSON Patch → Schema 校验 → 生效 → 业力结算 → 识破判定」全链路。

| 任务 | 负责 | 产出路径 | 状态 |
|:--|:--|:--|:--:|
| 指令输入面板 UX 规格 | `design-strategist` | `docs/ux/cheat-command-panel.md` | ✅ |
| Core 状态机（Controller / Diff / ErrorTranslator / History / Models / PresetLibrary） | `engineering-lead` | `Assets/ChessSage/Core/Cheat/` | ✅ |
| 面板实现 + Orchestrator 接入 + 业力/识破 HUD（CheatCommandPanel / CheatHud / CheatUiKit） | `engineering-lead` | `Assets/ChessSage/Presentation/Cheat/` | ✅ |
| T3b 接入组合根，叠加层 UI 可被截图捕获 | `engineering-lead` | `Bootstrap.cs` + `PlaythroughCapture.cs` | ✅ |
| Orchestrator 端到端测试（含 D-05 修复 + 业力公式 Δ = C × O^α） | `quality-lead` | `Assets/ChessSage/Tests/OrchestratorTests.cs` / `CheatPanelTests.cs` | ✅ |

**质量门**：L1 7/7 探针 + L2 4/4 + **L3 258/258**（含 CheatPanelTests 19 + OrchestratorTests 12）
**可玩证据**：`Logs/shots-sw/13_cheat_open.png` / `14_cheat_preview.png` / `15_cheat_applied.png`（唯一色 193-241，方差 4516-4659）
**本轮修复真实缺陷**：D-04（URP 空壳致整屏纯色）/ D-05（`pieces_red/black/white` 绕过 Schema 校验）
**详细报告**：`docs/m6-m8-report.md` §2

### M7 · RPG 表现层 + 存档系统 — ✅ 已完成（2026-10-14）

| 任务 | 负责 | 产出路径 | 状态 |
|:--|:--|:--|:--:|
| 存档系统（3 槽位 + schema_version + 校验和 + 迁移） | `engineering-lead` | `Assets/ChessSage/Core/Save/` | ✅ |
| 选关 / 技能树 / 剧情 / 双结局 / 记忆碎片（6 个表现层组件） | `engineering-lead` | `Assets/ChessSage/Presentation/Samsara/` | ✅ |
| UI 视觉规格 + 大地图美术规格 | `art-director` | `docs/art/{ui-visual-spec,overworld-art-spec}.md` | ✅ |
| SaveSystemTests（19）+ SamsaraSkillViewTests（15） | `quality-lead` | `Assets/ChessSage/Tests/` | ✅ |

**质量门**：同 M6，L3 258/258 全绿
**可玩证据**：`Logs/shots-sw/16_rpg_levels.png` → `22_rpg_memory.png`（7 张，唯一色 48-352，方差 296-927）

### M8 · 打磨与发布准备 — ✅ 已完成（2026-10-14）

| 任务 | 负责 | 产出路径 | 状态 |
|:--|:--|:--|:--:|
| 大地图视觉丰富化（地形过渡 + Poisson 植被 + 地标 + 大气） | `art-director` + `engineering-lead` | `Assets/ChessSage/Presentation/Overworld/*` + `Core/Overworld/*` | ✅ |
| 修复 5 个真实大地图缺陷（D-OW-1..5） | `engineering-lead` | 见 `docs/m6-m8-report.md` §4.1 | ✅ |
| 截图取证基建（PlaythroughCapture Overlay→Camera RT 切换） | `engineering-lead` | `Editor/PlaythroughCapture.cs` | ✅ |
| 软渲染全链路（Xvfb + Mesa llvmpipe） | `engineering-lead` | `tools/render-playthrough.sh` | ✅ |
| OverworldRichnessTests 17 条 | `quality-lead` | `Assets/ChessSage/Tests/OverworldRichnessTests.cs` | ✅ |
| 发布工具链（push_via_rest.py 4 轮修复 + 幂等性） | `release-ops-lead` | `tools/push_via_rest.py` | ✅ |

**质量门**：同 M6、L3 258/258；截图 14 张（唯一色 48-501，方差 164-4659）全部达标

### v1.0.1 · 修复轮（测评回应）— 🟡 进行中

**目标**：回应 v1.0.0 玩家测评报告的 14 项缺陷（含 2 个 P0），修复后发布 v1.0.1。

| 任务 | 负责 | 产出路径 | 状态 |
|:--|:--|:--|:--:|
| P0 ×2：跳棋 AI 先手不启动（对局永久思考）；改规面板 HTTP 402 文案误导 + 失败卡无恢复路径 | `engineering-lead` | `Assets/ChessSage/Presentation/Game/`、`Cheat/` | 🟡 进行中 |
| P1 ×5：悔棋回到玩家回合；hub 进入时落点高亮错位；RPG 面板「关闭✕」返回上一层；红方在下 + HUD 阵营常驻；跳棋初始回合对齐规则文件 | `engineering-lead` | `Assets/ChessSage/Presentation/` | 🟡 进行中 |
| P2 ×4：地狱道目标文案；动物棋物种形状 + 占位美术对比度；hub 玩法文案；改规面板「前往技能树」直达 | `engineering-lead` + `art-director` | `Assets/ChessSage/Presentation/` | 🟡 进行中 |
| 新增 ×3：RPG 进度自动存档（多槽位 + SHA-256 + 版本迁移）；Esc 进棋种 hub；大地图接入 hub 入口并随包构建 | `engineering-lead` | `Assets/ChessSage/Core/Save/`、`Presentation/`、`Overworld/` | 🟡 进行中 |
| 发行文档：v1.0.0 发行说明勘误（启动流程 / 解压目录）、v1.0.1 changelog 预起草、README.txt 同步建议、路线图与看板更新 | `release-ops-lead` | `docs/release-notes-v1.0.1.md` 等 | ✅ 已完成 |

**已知问题**：自然语言改规依赖的服务密钥欠费——已改为明确提示并引导预置指令，密钥续费后该功能自动恢复。

---

## 2. 质量门禁（贯穿全里程碑，不降标准）

| 层 | 命令 | 当前基线（M6-M8 后） |
|:--|:--|:--|
| L1 核心探针 | `bash tools/verify/core-harness.sh` | **7/7**（Xiangqi/Weiqi/Wuziqi/Heibaiqi/Tiaoqi/Dongwuqi/Session） |
| L2 编译门禁 | `bash tools/verify/compile-check.sh` | **4/4 零错误** |
| L3 EditMode 测试 | `bash tools/build.sh test` | **258/258**（19.9 秒，含 CheatPanelTests/SaveSystemTests/OverworldRichnessTests/SamsaraSkillViewTests/OrchestratorTests） |

**新增测试必须进 L1**（Core 零引擎依赖的设计红利：测试同时覆盖引擎内外两侧）。

## 3. 已知风险与缓解

| 风险 | 缓解 |
|:--|:--|
| 六角星棋盘视觉表达复杂（六角星 + 双倍列坐标） | 抽象 `IBoardLayout`，把"坐标→屏幕"变成可单测的纯函数；配 `IsoGridMathTests` 同级往返校验 |
| 落子类交互与走子类差异大（无"选中"阶段） | `GameScreen` 内按 `IsPlacementGame` 分流，共用 HUD 与回合状态机 |
| samsara 无测试直接做 UI 会反复返工 | 先用 `quality-lead` 补黄金测试（对照 legacy Python），再动表现层 |
| Orchestrator 涉及异步 LLM 调用，测试不确定 | 注入 `ILlmClient` 接口 + Fake 实现，保证 L1 可离线回归 |
| `WindowsStandaloneSupport` 模块缺失（镜像站 404） | 先完成 Linux 可玩链路；Windows 出包列为 M8 独立项，需另行获取模块 |
