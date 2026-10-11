# 迁移进度看板

> 最后更新：2026-10-11
> 口径：**「已完成」= 有可执行证据**（测试通过 / 编译通过），不含主观判断。

---

## 1. 总体状态

| 层| 完成度 | 说明 |
|:--|:--:|:--|
| 规则引擎（6 棋类） | 🟢 完成 | 走法生成 / 机制 / 胜负判定，黄金用例全绿 |
| AI 编排 | 🟢 完成 | 自然语言 → 意图 → JSON Patch → Schema → 生效 |
| RPG 层（samsara） | 🟢 完成 | 业力 / 技能树 / 关卡 / 结局 / Boss |
| 工程架构 | 🟢 完成 | 4 程序集模块化分层 + 三层质量门禁 |
| 象棋垂直切片 | 🟢 完成 | 端到端可玩闭环 |
| 六道大陆大地图 | 🟢 完成 | 数据 / 分块 / 迷雾 / 寻路 / 昼夜全部落地 |
| 其余 5 棋类表现层 | 🟡 进行中 | 表现层已数据驱动，缺逐棋种调优 |
| RPG 表现层 | 🔴 待办 | 选关/ 技能树 / HUD / 剧情 / 结局 / 成就 / 教程 |
| 音频与演出 | 🔴 待办 | BGM / 音效 / CG / 立绘 |
| 正式美术 | 🔴 待办 | 现为程序化占位 |

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
| P0 | 在有许可证的环境执行 L3 | 补齐验证缺口：EditMode 测试、渲染确认、Windows 出包 |
| P0 | 更新 ADR §8.3/§8.4 勘误状态 | 已加勘误章节，待复验后更新 |
| P1 | 5 个棋类的表现层接入 | 表现层已就绪，需逐棋种调优格距与配色 |
| P1 | AI 作弊指令面板 | 核心玩法「用自然语言让大模型作弊」尚未在 UI 中暴露 |
| P1 | 业力/ 识破 HUD | 把Karma / Detection 数值可视化 |
| P2 | RPG 表现层全套 | 选关 / 技能树 / 剧情对话 / 记忆相册 / 结局 / 成就 / 8 章教程 |
| P2 | 导入正式美术 | `legacy-web/shared/assets/` 的 PNG/WAV/MP4 |
| P2 | 大地图装饰物 | 植被 Poisson-disk 团簇、河流、桥梁 |
| P3 | 存档系统 | 当前无持久化 |
| P3 | 成就与统计 | 已有探索度统计钩子，未接入 |

---

## 4. 风险登记

| 风险 | 等级 | 缓解 |
|:--|:--:|:--|
| L3 未验证，渲染与性能未知 | 高 | L2 兜住编译；表现层做薄以降低运行期风险；需尽快本地验证 |
| 正式美术缺位 | 中 | 程序化占位保证可运行；美术导入为纯替换动作 |
| AI 编排依赖外部大模型 | 中 | 无密钥时明确报错而非静默失败；编排逻辑本身已移植 |
| 大地图 9408 格的对象数量 | 中 | 已分块 + 对象池，视野内约 25 draw call |
| 存档缺失影响体验闭环 | 中 | P3，但建议在 P1 阶段后立即补 |