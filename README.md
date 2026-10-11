# 棋圣 · 六道轮回 · Unity 版（ChessSage · SAMSARA — Unity）

> **六重棋境，一念改规。一方大陆，六道藏匿；业力为媒，规则为网，在轮回中修行，在棋局中悟道。**

本仓库是《棋圣 · 六道轮回》的 **Unity 重制版**。原作为 Python(FastAPI) + 原生 JavaScript(WebGPU/melonJS) 的网页游戏，现已整体复制到 [`legacy-web/`](legacy-web/) 作为**只读参考实现**。本仓库的目标是：**把原游戏全量、彻底地迁移为 Unity 版本**，并支持在 Linux 上交叉编译出 **Windows** 桌面版。

- 原始网页版源码（参考实现，勿在本目录继续开发）：[`legacy-web/`](legacy-web/)
- Unity 可行性评估与架构决策记录（ADR）：[`docs/unity-port-feasibility.md`](docs/unity-port-feasibility.md)

---

## 一、项目定位

| 项 | 说明 |
|:---|:---|
| 玩法内核 | 六种棋类（象棋/五子棋/围棋/动物棋/跳棋/黑白棋）为战斗场景，核心玩法是「**用自然语言让 AI 大模型作弊改规**」 |
| 元系统 | Roguelike RPG：业力系统、识破概率、技能树、六道关卡、多结局；2.5D 等距自由探索大地图「六道大陆」 |
| 原实现 | Python FastAPI 后端（1 个 hub + 12 个按需拉起的棋类服务）+ Web 前端（自研 WebGPU 渲染管线，melonJS 兜底） |
| 目标实现 | Unity 单一进程：C# 承载全部规则引擎 / AI 编排 / RPG 状态机；Unity 渲染与 UI 取代 Web 前端 |
| 主目标平台 | Windows 桌面（在 Linux 上交叉编译），后续可扩展 Linux / macOS |

## 二、仓库结构

```
ChessSage-Unity/
├── README.md                       # 本文件
├── docs/
│   ├── architecture.md             # 分层架构与模块化结构（程序集依赖、质量门禁）
│   ├── progress.md                 # 迁移进度看板与验收记录
│   ├── verification.md             # 三层验证体系与本地执行指南
│   ├── overworld-redesign.md       # 大地图重制方案（URP 2D 等距 Tilemap）
│   └── unity-port-feasibility.md   # 可行性评估 + 架构决策记录（ADR）
├── Assets/
│   ├── ChessSage/
│   │   ├── Core/                   # 纯逻辑层（零 UnityEngine 依赖，可在 dotnet 下回归测试）
│   │   │   ├── Model/ Rules/ Mechanism/ Ai/       规则引擎
│   ││   ├── Orchestration/         # AI 编排流水线（自然语言 → JSON Patch → 校验）
│   │   │   ├── Samsara/             # RPG 层（业力/技能/关卡/结局）
│   │   │   ├── Overworld/           # 六道大陆地图数据 / 分块 / 迷雾 / 流场寻路
│   │   │   └── Game/                # GameSession 对局会话
│   │   ├── Presentation/           # 表现层（棋盘 / 大地图 / HUD / 程序化美术）
│   │   ├── App/                    # 组合根
│   │   ├── Editor/                 # Editor 工具（构建脚本 / 工程引导器）
│   │   └── Tests/                  # 回归测试（含六棋类黄金等价性）
│   └── StreamingAssets/Configs/    # 数据真源（复用 legacy-web 的 JSON）
├── tools/
│   ├── build.sh                    # L3：Unity 构建（需许可证）
│   └── verify/
│       ├── compile-check.sh        # L2：编译门禁（无需许可证）
│       └── dotnet/                 # L1：引擎外逻辑回归（无需 Unity）
├── legacy-web/                     # 原网页版全量源码（参考实现，冻结不再演进）
│   ├── main.py                     # 原统一启动器（hub + 12 棋类服务）
│   ├── hub/                        # 原 Web 前端（标题页 / 大地图 / 对局 / 剧情 / 结局）
│   ├── xiangqi/ wuziqi/ ...        # 原 6 个 RPG 棋类（各自的规则/AI/编排）
│   ├── sandbox/                    # 原 6 个沙盒棋类（纯净版）
│   ├── samsara/                    # 原 RPG 层（业力/技能/关卡/结局/Boss）
│   ├── shared/                     # 原共享基类 + 美术/音频/字体资源（可复用素材）
│   └── configs/                    # 原数据驱动配置（棋盘/棋子/规则/剧情/技能树…）
└── .gitignore                      # Unity 工程忽略规则
```

> `legacy-web/` 中的 `configs/`、`shared/assets/` 是本项目**最重要的可复用资产**：数据配置（JSON）与美术/音频素材（PNG/JPG/WAV/MP4/字体）可直接导入 Unity 使用。

## 三、当前状态

> 详细看板与逐项验收记录见 **[docs/progress.md](docs/progress.md)**。

### 已完成

- [x] 完成原网页版架构盘点与迁移可行性评估（见 [ADR](docs/unity-port-feasibility.md)）
- [x] 建立本仓库并将原实现整体归档至 `legacy-web/`
- [x] 安装 **Unity 2022.3.62f3c1** + **Windows 交叉编译模块**
- [x] **B1 规则内核**：6 种棋类的走法生成 / 机制引擎 / 胜负判定移植为 C#，黄金用例全绿
- [x] **B2 AI 编排**：自然语言 → 意图解析 → JSON Patch（RFC 6902）→ Schema 校验 → 生效
- [x] **B5 RPG 层**：`samsara`（业力系统 / 技能树 / 关卡 / 结局 / Boss）移植为 C#
- [x] **M1 工程脚手架**：4 个 asmdef 模块化分层、URP 包配置、`ProjectBootstrapper` 一键引导
- [x] **M1-4 象棋垂直切片**：棋盘渲染 → 选中 → 落子 → AI 应手 → 悔棋，端到端可玩闭环
- [x] **M2 大地图全面重制**：等距 Tilemap + 分块流式加载 + 探索迷雾 + 流场寻路 + 昼夜氛围
- [x] **M1-3 Core 性能硬化**：AI 决策 **2231 ms → 93 ms（24×）**，并加入硬性时间/节点预算
- [x] **三层质量门禁**：L1 逻辑回归 57 项 · L2 全工程编译校验（均已跑通）

### 进行中 / 待办

- [ ] **B4 对局 UI**：5 个棋类的表现层接入（棋盘视图已数据驱动，复制即可）
- [ ] **RPG 表现层**：选关弹窗、技能树、业力/识破 HUD、剧情对话、记忆相册、结局、成就、8 章教程
- [ ] **音频演出**：BGM、音效、CG 视频、立绘演出
- [ ] **正式美术**：以占位美术为基线，导入 `legacy-web/shared/assets/` 的正式素材
- [ ] **大地图内容扩展**：装饰物植被（Poisson-disk 团簇）、河流、桥梁、区域边界的视觉呈现

> **验证边界（重要）**：L1（逻辑回归）与 L2（全工程编译）已在无许可证环境中跑通并全绿；
> L3（Unity 运行 / EditMode 测试 / 出包）因本环境无 Unity 许可证而**尚未执行**，需在本地执行。

## 四、技术决策（概要）

| 维度 | 决策 | 依据 |
|:---|:---|:---|
| 引擎 | Unity（LTS） | 跨平台桌面打包成熟、2D/3D/UI/视频/音频一体，可在 Linux 交叉编译 Windows |
| 渲染 | URP（Universal Render Pipeline） | 原 WebGPU 自研管线需在 Unity 重新实现；URP 覆盖地形/光照/后处理/2D |
| UI | UI Toolkit / uGUI | 取代原 HTML/CSS 页面（标题页、大地图 HUD、对局面板、剧情对话、结局） |
| AI 接入 | C# HTTP 客户端调用大模型（沿用原 Prompt 体系） | 原 `prompts.py` / 编排流程可直接迁移；密钥仍走混淆配置 + 环境变量 |
| 数据 | 复用 `legacy-web/configs/*.json` + `shared/assets/*` | 数据驱动设计已成型，是可复用性最高的部分 |
| 架构 | **模块化单一进程**（原 12 个微服务收敛为 4 个程序集：Core→Presentation→App→Editor） | 消除进程编排/端口/懒加载复杂度；Core 层零引擎依赖可在 dotnet 下回归测试 |
| 大地图 | **URP 2D 等距 Tilemap**（替代 web 版跑不通的 WebGPU 主通道） | 彻底移除 WebGPU 依赖；16×16 分块流式加载，视野内约 25 个 draw call |
| 地形美术 | **程序化图集**（13 种地形，代码生成顶面/侧面） | 无正式美术时也能开箱即有画面，且不阻塞 GPU 资源导入 |
| 主目标 | Windows x64 桌面版，Linux 交叉编译 | 用户明确要求 |

技术细节、替代方案与风险见 [ADR](docs/unity-port-feasibility.md)。

## 五、迁移路线图（概要）

1. **环境与骨架**：安装 Unity + Windows 构建模块，建立工程分层与数据加载层（读取 `configs/*.json`）。
2. **规则内核**：把 6 个棋类的 `rule_engine` / 走法生成 / 胜负判定移植为 C#，用原 `tests/` 作为黄金用例验证等价性。
3. **AI 编排**：移植「自然语言 → 意图解析 → JSON Patch → Schema 校验 → 落盘/生效」流水线（含业力评估与识破概率）。
4. **对局与 UI**：单个棋类端到端跑通（棋盘渲染、落子、AI 作弊指令面板），再复制到其余棋类。
5. **RPG 层**：迁移 `samsara`（业力/技能树/关卡/结局/Boss/记忆碎片）与存档。
6. **大地图与表现层**：2.5D 等距大地图、动画立绘、CG 视频、音频、教程。
7. **打包发布**：Linux → Windows x64 交叉编译、产物验证。

各阶段的验收标准（fitness functions）见 ADR。

## 六、开发环境与构建

**Unity 2022.3.62f3c1**（China 分支），已配置 URP 14.0.12 / Input System / Addressables /
Localization / Cinemachine / Timeline / Windows 交叉编译工具链。包版本取自编辑器自带的权威
manifest，不是猜测值。

### 三层质量门禁

| 层 | 命令 | 是否需要 Unity 许可证 | 覆盖内容 |
|:--|:--|:--|:--|
| **L1** | `CHESSSAGE_ASSETS_ROOT=$PWD/Assets dotnet test tools/verify/dotnet/ChessSage.Core.Verify.csproj` | 否 | 57 项逻辑回归：6 棋类黄金等价性、机制、编排、大地图数据、AI 决策护栏 |
| **L2** | `bash tools/verify/compile-check.sh` | 否 | 用 Unity 自带 Roslyn + 参考程序集编译全部 4 个程序集，捕获全部编译错误 |
| **L3** | `bash tools/build.sh test` / `bash tools/build.sh windows` | **是** | Unity EditMode 测试、Linux → Windows x64 出包 |

L1 与 L2 **已在当前环境跑通并全绿**；L3 需在本地执行。详见 [docs/verification.md](docs/verification.md)。

### 首次打开工程

由于 `.unity` 场景与 URP `.asset` 必须由 Unity 序列化器生成，无法手写 YAML，
工程提供了幂等的引导器：菜单 **ChessSage → 工程引导 → 一键引导（幂等）**。
它会自动创建 URP 渲染管线资产、启动场景 `Scenes/Main.unity` 与大地图场景 `Scenes/Overworld.unity`，
并写入 Build Settings。也可在 batchmode 下调用：

```bash
/opt/unity/Editor/Unity -batchmode -nographics -quit -projectPath <proj> \
  -executeMethod ChessSage.EditorTools.ProjectBootstrapper.BootstrapAll -logFile -
```

> **交叉编译命令形态**：
> ```bash
> /opt/unity/Editor/Unity -batchmode -nographics -quit -accept-apiupdate \
>   -projectPath <proj> -buildTarget StandaloneWindows64 \
>   -executeMethod ChessSage.Build.BuildScript.BuildWindows -logFile -
> ```

## 七、安全说明

- 原网页版使用的大模型密钥采用「静态混淆 + 运行时还原」机制（详见 `legacy-web/shared/ai_config.py`），**仓库中静态存放的字符串不可直接调用**。
- 真实密钥应放在未纳入版本控制的 `config.json` 或环境变量 `DEEPSEEK_API_KEY` 中。
- 若曾提交过真实密钥，请在模型平台**轮换（revoke）**；历史提交无法靠新提交抹除。

## 八、来源与版权

本项目玩法、设定、文案、美术与音频资产来自原作者仓库 `stellarissss/CHessGAme`（网页版），Unity 版为其迁移与重制。原始网页版说明见 [`legacy-web/README.md`](legacy-web/README.md)。