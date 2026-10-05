# Unity 迁移可行性评估与架构决策记录（ADR）

- **状态**：已决策（Accepted）— 采用 Unity 全量重制
- **决策日期**：2026-10-05
- **责任方**：项目所有者（stellarissss）；本地校验由本仓库 CI / 手工冒烟执行
- **关联实现**：`legacy-web/`（原网页版，冻结参考）

---

## 1. 决策问题

> 是否将《棋圣 · 六道轮回》（Python FastAPI + 原生 JS / 自研 WebGPU 渲染的网页游戏）**全量重制为 Unity 版本**，并以 Linux 交叉编译出 Windows 桌面版？

**结论：可行（Feasible），推荐执行。** 属于中等偏上工作量的一次性引擎迁移（one-way door），但数据与素材层可复用度高，显著降低风险。

---

## 2. 现状盘点（基于对 `legacy-web/` 的实际核查）

| 维度 | 实测结果 |
|:---|:---|
| 仓库体积 | 约 **291 MB**（其中 `shared/assets/` 279 MB） |
| 源码规模 | **129** 个 Python 文件、**29** 个 JavaScript 文件 |
| 后端接口 | **约 272 处** FastAPI 路由定义，分布于 **16** 个文件 |
| 服务拓扑 | 1 个 Hub（:8080）+ **12** 个按需拉起的棋类服务（RPG 8000–8005 / Sandbox 8010–8015）+ `samsara` 挂载于 `/samsara` |
| 资源 | 角色立绘/动画 208 MB、CG/视频 46 MB、背景 13 MB、字体 4.9 MB、音效/BGM 3.3 MB、地图 3 MB、UI 1.9 MB |

### 2.1 系统分层（Bounded Context Map）

| 上下文 | 载体（原实现） | 职责 | 迁移难度 |
|:---|:---|:---|:---|
| 规则内核 | `*/rule_engine.py`、`shared/` | 走法生成、合法性、吃子/胜负、可修改机制修饰符 | 中（纯逻辑，可逐字节翻译为 C#） |
| 本地 AI | `*/chess_ai.py` | Minimax + Alpha-Beta 选子 | 低（算法直译） |
| **AI 编排（核心）** | `shared/ai_orchestrator_base.py`、`*/ai_orchestrator.py`、`prompts.py` | 自然语言 → 意图解析 → **JSON Patch(RFC6902)** → Schema 校验 → 落盘/生效；业力评估与拦截 | **高（迁移重点）** |
| 机制引擎 | `*/mechanism_engine.py`、`shared/mechanism_engine_base.py` | 数据驱动的可改写规则（`can_capture`/`eatable`/`liberty_cap`/`uncapturable`…） | 中 |
| RPG 层 | `samsara/*.py`（state/progression/skills/karma/endings/bosses/detection/levels/objectives/turn_limit/memory_fragments/story_api） | 业力、技能树、六道关卡、结局、Boss、存档（JSON 持久化） | 中 |
| Web 前端 | `hub/*.{html,js,css}`、`*/static/app.js` | 标题页、2.5D 大地图、对局容器、剧情对话、结局、教程 | 高（渲染层重写） |
| 大地图渲染 | `hub/overworld-wgpu.js` + `wgpu/shaders.js`（主）/ `overworld-melonjs.js`（兜底）/ `vendor/iso-engine` | 高度场地形、SSAO、体积光、Bloom、HDR+ACES、等距图集 | **高（自研 WebGPU 管线，需在 Unity 重制）** |
| 数据配置 | `configs/*.json`、`*/configs/*.json`（含 Schema） | 棋盘/棋子/规则/UI/剧情/技能树/关卡池 | **低（可直接复用）** |
| 素材 | `shared/assets/*`（PNG/JPG/WAV/MP4/字体） | 立绘、动画帧与视频、CG、背景、音效、字体、地图、UI | **低（可直接导入 Unity）** |
| 打包 | `nuitka_build.py`、`pyinstaller/*`、`build_windows.bat` | 原为 Python 冻结为桌面程序 | 由 Unity 构建管线取代 |

### 2.2 数据流（原）

```
玩家输入自然语言
   └─> 前端 app.js ──HTTP──> 棋类 main.py (/api/command)
          └─> AIOrchestrator.process_command
                 ├─ 意图解析（LLM）
                 ├─ 生成 JSON Patch（LLM）
                 ├─ Schema 校验 + 硬校验
                 ├─ 应用 Patch 到 board/rules/pieces/ui_config
                 └─ RPG 模式：KarmaAssessor 并行评估业力 → 拦截/增业 → 识破概率
          └─> samsara 状态同步（业力/识破/技能门控）
```

---

## 3. 目标与非目标

**目标**
1. 玩法语义与数值**尽量等价**地迁移（规则、业力、识破、技能、结局）。
2. 单一可执行程序交付 Windows 桌面版；Linux 上交叉编译。
3. 复用全部数据配置与美术/音频素材。
4. 保留「数据驱动 + AI 可改规则」的扩展性，便于后续持续加棋类/改规则。

**非目标（本期）**
- 不做联网多人、不做移动端、不做在线服务化后端。
- 不改动玩法设计本身（迁移优先于创新）。
- 不追求与原版像素级一致（渲染层允许重制与风格升级）。

**约束**
- 目标平台为 Windows（Linux 交叉编译）；构建机为无 GPU 的 Headless Linux。
- Unity 使用需**许可证激活**（见 §8 环境准备）。
- 大模型调用有延迟/成本约束，原版已通过「关闭推理」等优化解决，需在 C# 侧沿用。

---

## 4. 替代方案与取舍

| 方案 | 说明 | 结论 |
|:---|:---|:---|
| **A. Unity 全量重制（选定）** | C# 重写全部逻辑 + Unity 渲染/UI | ✅ 交付原生 Windows 桌面版，跨平台打包成熟，可复用素材与数据 |
| B. 保留 Web + Electron/WebView 包装 | 用桌面壳承载现有网页 | ❌ 非真正 Unity；未满足「彻底改为 Unity」的目标；但仍可作为过渡验证 |
| C. 迁移到其他引擎（如 Godot） | 同为跨平台引擎 | ❌ 用户明确要求 Unity；团队/生态预期为 Unity |
| D. 混合：Unity 外壳 + 内嵌 WebView 跑旧网页 | 复用旧前端 | ❌ 治标不治本，长期维护双栈；仅可作**临时对照**用于验收比对 |

**决定性理由**：用户目标为「全量、彻底」迁移到 Unity 并交叉编译 Windows，方案 A 是唯一直接满足者；素材与配置的高复用率使其风险可控。

---

## 5. ADR 决策

**Decision**：采用 Unity（LTS）+ URP 进行全量重制；**单一进程**架构取代原 12 服务拓扑；数据配置与素材直接复用；AI 编排沿用原 Prompt/JSON-Patch 流程并用 C# 重实现。

**Consequences**

| 正面 | 负面 |
|:---|:---|
| 交付原生 Windows 桌面版；无浏览器依赖 | 自研 WebGPU 大地图管线需在新渲染栈重新实现 |
| 进程内系统消除端口/懒加载/子进程管理复杂度 | C# 侧需自建 JSON Patch + Schema 校验等价实现 |
| `configs/*.json`、`shared/assets/*` 高复用，减少返工 | 129 个 Python 文件的逻辑需逐系统移植与回归 |
| 单一存档与状态机，便于调试与发布 | 一次性引擎迁移成本高，迁移期双栈并存 |
| LLM 接入点集中，便于密钥/成本治理 | — |

**Reversibility（可逆性）**
- 成本：**较高**（引擎选择属于 one-way door）；但 `legacy-web/` 完整保留，网页版仍可独立运行与发布。
- 重新考量触发条件：若在「规则内核 + 首个棋类端到端」阶段发现等价性无法达成，或 Unity 许可证/构建在本环境长期不可用，则回退到「方案 B/D 过渡」并重评。

**责任**：项目所有者；每阶段以 §7 的 fitness functions 本地校验，未通过不进入下一阶段。

---

## 6. 迁移分批与防回退（Migration Plan）

遵循「先内核、后表现、可验证、可回滚」的分批策略：

| 批次 | 内容 | 完成判定 |
|:---|:---|:---|
| B0 环境 | Unity + Windows 构建模块、工程骨架、数据加载层 | 能在 Linux 构建出空的 Windows 可执行文件 |
| B1 规则内核 | 6 棋类 `rule_engine` + 走法/胜负/吃子 → C# | 移植原 `tests/*.py` 为 C# 测试，全部通过 |
| B2 AI 编排 | 意图解析 → JSON Patch → Schema 校验 → 生效；业力/识破 | 同一批指令在两端产生**等价 Patch 与业力值** |
| B3 对局闭环 | 单棋类端到端（棋盘渲染 + 落子 + 作弊指令面板） | 象棋对局可完整通关一次 |
| B4 其余棋类 | 复制 B3 模式到其余 5 类 + 沙盒纯净版 | 6 类均可对局 |
| B5 RPG 层 | samsara 状态机/技能/关卡/结局/Boss/存档 | 一遍完整轮回可跑通并落存档 |
| B6 表现层 | 2.5D 大地图、立绘动画、CG、音频、教程 | 视觉/音频验收通过 |
| B7 发布 | Windows x64 交叉编译 + 冒烟 | 产物在 Windows 实机启动并游玩 |

**防回退**：`legacy-web/` 冻结为参考实现（只读），新功能只在 Unity 侧开发；两端的配置 JSON 以 Schema 为契约，避免格式漂移。

---

## 7. Fitness Functions（可测试的架构不变量）

| 属性 | 度量 | 阈值/规则 | 来源 | 频率 | 失败响应 | 本地校验路径 |
|:---|:---|:---|:---|:---|:---|:---|
| 规则等价性 | 黄金用例通过率 | 100% | 移植自 `legacy-web/tests/*.py` | 每次改动 | 阻断合入 | `dotnet test` / Unity Test Runner |
| 配置兼容性 | `configs/*.json` 加载 + Schema 校验 | 全部通过 | `shared/schema_validator.py` 的 Schema | 每次改动 | 修正 Schema 映射 | C# 单测 |
| AI 编排等价性 | 同指令 → Patch/业力值一致率 | ≥ 目标样本集 100% | 录制原版响应样本 | 每批次 | 阻断该批次 | 回归脚本 |
| 依赖方向 | 规则内核/表现层无反向依赖 | 0 违规 | 程序集依赖 | 每次提交 | 阻断 | 程序集引用检查 |
| Windows 构建 | 交叉编译产物可启动 | 成功 | Unity Build 结果 | 每批次 | 阻断发布 | CI 构建日志 |
| 性能 | 对局帧率/大模型往返时延 | 交互可接受（沿用原版时延指标） | Profiler / 计时 | 里程碑 | 优化 | Profiler 采样 |

---

## 8. 环境准备（Unity 安装 + Windows 交叉编译）

**目标环境**：Ubuntu 22.04 x86_64（3 vCPU / 5.8 GB RAM / 1.1 TB 空闲磁盘，Headless、无 GPU）。

**计划步骤**
1. 安装 Unity Hub（Linux），用它获取 Unity **LTS** 版本 Editor。
2. 为 Editor 附加模块：**Windows Build Support (Mono) 与 (IL2CPP)**（交叉编译 Windows 所需）+ Linux Build Support（本机自测）。
3. 以命令行/Headless 方式验证：`Unity -batchmode -quit -projectPath <proj> -buildTarget StandaloneWindows64`。
4. 后续在 CI 中复用同一命令产出 Windows 构建。

**已知约束与风险（重要）**
- **许可证**：Unity 需激活（Personal/Pro 均可）。Headless 激活需提供 Unity 账号或许可证文件；无许可证时**只能安装 Editor，无法执行构建**。→ 需项目所有者提供 Unity 账号或许可证（.ulf）。
- **无 GPU**：本沙箱用于**构建**（交叉编译不需要 GPU 渲染）；实际画面验收建议在带显卡的 Windows 机器进行。
- **下载体积与时延**：Editor + 模块为多 GB 级下载，受沙箱代理/带宽影响。
- **Headless 运行**：图形化 Editor 操作不可用；以批处理/命令行驱动构建与测试。

---

## 9. 风险登记

| 风险 | 可能性 | 影响 | 缓解 |
|:---|:---|:---|:---|
| Unity 许可证无法在沙箱激活 | 中 | 高（阻塞构建） | 由所有者提供账号/`.ulf`；或改用可用的 CI 构建机 |
| 自研 WebGPU 大地图管线重制成本高 | 高 | 中 | 用 URP 标准能力替代，分阶段降级（先 2D 等距图集，后增强） |
| C# 缺少 RFC6902 JSON Patch / JSON Schema 等价库 | 中 | 中 | 自研最小实现或用开源库；以原 `shared/json_patch_utils.py` + `schema_validator.py` 为规范 |
| 129 文件规则移植引入行为偏差 | 中 | 高 | 以原 `tests/` 为黄金用例；逐棋类验收 |
| 大模型时延/成本 | 中 | 中 | 沿用原「关闭推理」等优化；集中配置与限额 |
| 迁移期双栈维护成本 | 中 | 低 | `legacy-web/` 冻结；仅 Unity 侧演进 |

---

## 10. 决策表

| 场景 | 默认 | 备选/例外 |
|:---|:---|:---|
| 引擎 | Unity LTS + URP | 若许可证不可用 → 暂停编码，先解决授权 |
| 前后端拓扑 | 单一进程 | 若未来需联网 → 再评估独立服务 |
| 渲染 | URP（先 2D 等距图集，后增强） | 高性能需求 → 再评估 HDRP/自研 SRP |
| UI | UI Toolkit 为主，uGUI 兜底 | 视频/复杂动效 → uGUI |
| 数据 | 直接复用 `legacy-web/configs/*.json` | 格式冲突 → 以 Schema 为契约适配 |

---

## 11. 后续待决问题（Follow-up）

1. **Unity 版本与许可证**：确定具体 LTS 版本与激活方式（账号或 `.ulf`）；未定前不进入 B1。
2. **C# JSON Patch / Schema 方案**：自研 vs 开源库，需在 B2 前定稿并以原实现为规范做等价性验证。

---

## 附：结论摘要

- **可行性：可行。** 核心玩法与 AI 编排逻辑是「可翻译的确定性逻辑」，数据与素材复用度高。
- **最大工作量**：自研 WebGPU 大地图渲染的重制；**最大阻塞风险**：Unity 许可证在沙箱的可用性。
- **建议**：先打通「环境 + 规则内核 + 单个棋类端到端」，再横向复制，最后攻坚表现层。