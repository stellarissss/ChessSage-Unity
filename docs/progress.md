# 迁移进度看板

> 最后更新：2026-10-15
> 口径：**「已完成」= 有可执行证据**（测试通过 / 编译通过），不含主观判断。
> 当前门禁基线：**L1 7/7 探针 · L2 4/4 零错误 · L3 258/258**（详见 `docs/m6-m8-report.md`）

---

## 1. 总体状态

| 层| 完成度 | 说明 |
|:--|:--:|:--|
| 规则引擎（6 棋类） | 🟢 完成 | 走法生成 / 机制 / 胜负判定，黄金用例全绿 |
| AI 编排 | 🟢 完成 | 自然语言 → 意图 → JSON Patch → Schema → 生效；**D-05 修复** pieces_red/black/white 走相同校验 |
| RPG 层（samsara） | 🟢 完成 | 业力 / 技能树 / 关卡 / 结局 / Boss；89 条测试 + 15 条 SkillView |
| 工程架构 | 🟢 完成 | 4 程序集模块化分层 + 三层质量门禁 |
| 象棋垂直切片 | 🟢 完成 | 端到端可玩闭环 |
| 六道大陆大地图 | 🟢 完成 | URP 2D 等距 + 16 分块 + Poisson 植被 + 地标 + 大气；**T8 二次重制** |
| **六棋种全可玩** | 🟢 完成 | 落子类交互 + 六角星棋盘 + 棋种 hub；六棋种全部可对弈 |
| **AI 作弊改规指令面板** | 🟢 完成 | **M6 核心 USP**：Core 6 文件 + Presentation 3 文件 + 19 用例；可玩证据 `13_cheat_open.png` |
| **RPG 表现层** | 🟢 完成 | **M7**：选关 / 技能树 / 剧情 / 选择 / 双结局 / 记忆碎片；证据 `16-22_rpg_*.png` |
| **存档系统** | 🟢 完成 | **M7**：3 槽位 + schema_version + 校验和 + 迁移；19 条 SaveSystemTests |
| 音频与演出 | 🟡 进行中 | FMOD 接口就位（沙箱 nosound）；CG / 立绘待正式美术 |
| 正式美术 | 🟡 进行中 | spec 已出 `docs/art/{ui-visual-spec,overworld-art-spec}.md`；程序化占位兜底 |

### 里程碑进度

| 里程碑 | 状态 | 报告 |
|:--|:--:|:--|
| M1 工程脚手架 + 象棋切片 | ✅ | `docs/roadmap.md` |
| M2 大地图重制 | ✅ | `docs/overworld-redesign.md` |
| M3/M4 工具链 + 试玩验证 | ✅ | `docs/playthrough-report.md` |
| M5 六棋种全可玩 | ✅ | `docs/m5-report.md` |
| **M6 AI 作弊改规指令面板** | ✅ | **`docs/m6-m8-report.md` §2** |
| **M7 RPG 表现层 + 存档系统** | ✅ | **`docs/m6-m8-report.md` §3** |
| **M8 打磨与发布准备** | ✅ | **`docs/m6-m8-report.md` §4** |
| **v1.0.1 修复轮（测评报告 14 项缺陷）** | 🟡 进行中 | **`docs/release-notes-v1.0.1.md`** |

---

## 2. 已完成项与验收证据

### B1 规则引擎

- 6 个 `VariantRuleBase` 子类，覆盖 jump / ray 两类规则原语。
- 证据：`RuleEngineGoldenTests` 等 9 项黄金等价测试通过。

### B2 AI 编排

- `OrchestratorEngine.ProcessCommandAsync`：意图解析 → 代码生成 → Schema 校验 → 应用配置。
- 业力（Karma）与识破概率：`Δ = C × O^α`，C / α 可由技能树下调。
- 证据：`JsonPatchTests`、`MechanismAndSessionTests` 通过。

### B5 RPG 层

- `Assets/ChessSage/Core/Samsara/`：业力、技能树（4 分支 × 3 层）、六道关卡、Boss、结局。

### M1 工程脚手架

| 产物 | 说明 |
|:--|:--|
| 4 个 `.asmdef` | `Core`（`noEngineReferences: true`）→ `Presentation` → `App` → `Editor`，严格单向依赖 |
| `Packages/manifest.json` | 包版本取自编辑器自带权威 manifest（URP = **14.0.12**，非推测值） |
| `ProjectBootstrapper` | 幂等生成 URP 资产 + 两个场景 + Build Settings |
| `tools/verify/compile-check.sh` | L2 编译门禁，4 程序集零错误 |

### M1-3 Core 性能硬化

实测（Release，.NET 6，2 核容器）：

| 路径 | 优化前 | 优化后 | 提速 |
|:--|--:|--:|--:|
| `ChessAi.GetBestMove`（象棋 medium） | **2231 ms** | **93 ms** | **24×** |
| `OverworldMap.FromJson`（112×84） | 1.10 ms | 1.11 ms | — |
| `FlowField.Build`（全图 BFS） | 0.40 ms | 0.42 ms | — |
| `BoardState.Clone` | 0.042 ms | 0.042 ms | — |

**根因与修法**（均为行为等价改动，57 项测试验证）：

1. `BoardState.Clone()` 会对 4 个 `JObject` 做 `DeepClone`，而 AI 搜索在 depth=3 时
   产生约 6 万节点，累计成为绝对瓶颈 → 新增 `CloneForSearch()`：只拷贝棋子与当前方，
   JSON 字段按引用共享（搜索路径只读它们，正确性由 `SearchShallowClone_DoesNotCorruptRealBoard` 盯住）。
2. `MoveScore` 在 `List.Sort` 的比较器里被反复重算（O(n log n × 棋子数)）
   → 改为预计算一次并缓存进 `Move.Order`。
3. `Evaluate` 对**每个棋子**都线性扫描一次去找将，O(n²) → 提到循环外每方只查一次。
4. `Minimax` 内部节点未排序，α-β 剪枝命中差 → 内部节点同样排序。
5. 新增**迭代加深 + 硬预算**（`NodeBudget` / `MaxSearchMilliseconds`）：
   任一层超预算即返回该层最佳着法，保证单步耗时存在硬上限，永不因局面复杂而卡死。

> **一处刻意的取舍**：曾尝试给 `BoardState` 加 id→Piece 字典索引以把查找降到 O(1)，
> 但棋子可变字段散落在 6 个规则引擎的十余处赋值点，任何一处漏失效都会导致**走法规则静默出错**。
> 权衡后**放弃**该优化，保持线性扫描——正确性风险高于性能收益。

### M1-4 象棋垂直切片

链路：输入（`BoardInputRouter`）→ 选中（`GameScreen`）→ 合法走点高亮（`HighlightLayer`）
→ `GameSession` 走子 → AI 应手 → 刷新（`BoardView`，脏标记 + 对象池）→ 悔棋。

关键设计：`BoardView` **完全数据驱动**，格数取自 `Rules.Width/Height`，
不含任何棋类特判 → 其余 5 个棋种接入只需换 `GameSession` 的配置。

### M2 大地图全面重制

见 [overworld-redesign.md](overworld-redesign.md)。核心结论：
**彻底移除 WebGPU 依赖**，改为 URP 2D 等距 Tilemap + 16×16 分块流式加载。

---

## 3. 待办与优先级

| 优先级 | 项 | 说明 |
|:--|:--|:--|
| P0 | v1.0.1 修复轮（测评报告 14 项缺陷） | 2 个 P0 + 5 个 P1 + 4 项文案视觉 + 3 项新增；清单见 `docs/release-notes-v1.0.1.md` |
| P0 | 沙箱恢复 License 后补跑截图 | 沙箱 Unity Personal 许可证过期，无法在本轮重跑；证据已存档 |
| P1 | 正式美术替换 | spec 已就位（`docs/art/*`），程序化占位可独立运行 |
| P1 | 接入 GitHub Actions CI | 把三门禁接到 PR 必跑 |
| P1 | 本地化 | 英文 cheats、关卡名、剧情台词 |
| P2 | 移动端验证 | URP 2D 在 Android / iOS WebGL 未验证；当前仅 Linux Editor + Windows x64 交叉编译 |
| P2 | 音频演出 | BGM / 配音 / CG 演出 |
| P2 | Live Ops / 赛季 | 见 M8 之后待办 |

---

## 4. 风险登记

| 风险 | 等级 | 缓解 |
|:--|:--:|:--|
| L3 未验证，渲染与性能未知 | 高 | L2 兜住编译；表现层做薄以降低运行期风险；需尽快本地验证 |
| 正式美术缺位 | 中 | 程序化占位保证可运行；美术导入为纯替换动作 |
| AI 编排依赖外部大模型 | 中 | 无密钥时明确报错而非静默失败；编排逻辑本身已移植 |
| 大地图 9408 格的对象数量 | 中 | 已分块 + 对象池，视野内约 25 draw call |
| 存档缺失影响体验闭环 | 中 | P3，但建议在 P1 阶段后立即补 |