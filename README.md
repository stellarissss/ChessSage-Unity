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
│   └── unity-port-feasibility.md   # 可行性评估 + 架构决策记录（ADR）+ 迁移路线图
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

- [x] 完成原网页版架构盘点与迁移可行性评估（见 [ADR](docs/unity-port-feasibility.md)）
- [x] 建立本仓库并将原实现整体归档至 `legacy-web/`
- [x] 安装 **Unity 2022.3.62f3c1** + **Windows 交叉编译模块**（WindowsStandaloneSupport / Linux IL2CPP），见 [ADR §8.1](docs/unity-port-feasibility.md)
- [x] 激活 **Unity Personal** 许可证，并实测 **Linux → Windows x64 交叉编译成功**（产物为 PE32+ 可执行文件），见 [ADR §8.4](docs/unity-port-feasibility.md)
- [ ] 搭建 Unity 工程骨架（数据层 / 规则引擎 / AI 编排 / UI 框架）
- [ ] 逐系统迁移（详见 ADR 的分阶段路线图）

> **迁移尚未开始编码。** 在 Unity 工程骨架落地并跑通第一个棋类之前，`legacy-web/` 是唯一可运行版本。

## 四、技术决策（概要）

| 维度 | 决策 | 依据 |
|:---|:---|:---|
| 引擎 | Unity（LTS） | 跨平台桌面打包成熟、2D/3D/UI/视频/音频一体，可在 Linux 交叉编译 Windows |
| 渲染 | URP（Universal Render Pipeline） | 原 WebGPU 自研管线需在 Unity 重新实现；URP 覆盖地形/光照/后处理/2D |
| UI | UI Toolkit / uGUI | 取代原 HTML/CSS 页面（标题页、大地图 HUD、对局面板、剧情对话、结局） |
| AI 接入 | C# HTTP 客户端调用大模型（沿用原 Prompt 体系） | 原 `prompts.py` / 编排流程可直接迁移；密钥仍走混淆配置 + 环境变量 |
| 数据 | 复用 `legacy-web/configs/*.json` + `shared/assets/*` | 数据驱动设计已成型，是可复用性最高的部分 |
| 架构 | **单一进程**（原 12 个微服务收敛为进程内系统） | 消除进程编排/端口/懒加载复杂度，桌面单机场景无需 HTTP 微服务 |
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

## 六、开发环境（Unity + Windows 交叉编译）

已安装 **Unity 2022.3.62f3c1**（China 分支）于 `/opt/unity/editor/Editor/`，配有 **Windows Build Support (Mono)** 与 **Linux Build Support (Mono/IL2CPP)** 模块，并已激活 **Unity Personal** 授权。

交叉编译命令形态：

```bash
/opt/unity/editor/Editor/Unity -batchmode -nographics -quit -accept-apiupdate \
  -projectPath <proj> -buildTarget StandaloneWindows64 \
  -executeMethod <BuildScript.Build> -logFile -
```

> **已验证**：在 Linux Headless 上构建空工程成功，产出 `ChessSage.exe + UnityPlayer.dll` 等 PE32+ x86-64 文件。细节见 [ADR §8](docs/unity-port-feasibility.md)。

## 七、安全说明

- 原网页版使用的大模型密钥采用「静态混淆 + 运行时还原」机制（详见 `legacy-web/shared/ai_config.py`），**仓库中静态存放的字符串不可直接调用**。
- 真实密钥应放在未纳入版本控制的 `config.json` 或环境变量 `DEEPSEEK_API_KEY` 中。
- 若曾提交过真实密钥，请在模型平台**轮换（revoke）**；历史提交无法靠新提交抹除。

## 八、来源与版权

本项目玩法、设定、文案、美术与音频资产来自原作者仓库 `stellarissss/CHessGAme`（网页版），Unity 版为其迁移与重制。原始网页版说明见 [`legacy-web/README.md`](legacy-web/README.md)。