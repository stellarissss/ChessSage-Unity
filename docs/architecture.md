# ChessSage Unity 版架构设计

> 本文档定义 Unity 重制版的**模块化结构**与依赖方向约束，是 M1 起的施工依据与长期护栏。
> 决策背景见 [`unity-port-feasibility.md`](unity-port-feasibility.md)（ADR）。

---

## 1. 分层与依赖方向（硬约束）

```
        ┌──────────────────────────────────────────────┐
        │  ChessSage.Editor        (Editor-only 工具)   │
        │  构建 / 场景生成 / 资源导入 / 诊断             │
        └───────────────┬──────────────────────────────┘
                        │ 引用
        ┌───────────────▼──────────────┐
        │  ChessSage.App   组合根       │  ← 唯一允许「组装一切」的地方
        │  启动 / 流程流转 / 服务装配   │
        └───────────────┬──────────────┘
                        │ 引用
        ┌───────────────▼──────────────┐
        │  ChessSage.Presentation      │  ← 只做「把状态画出来 / 把输入翻译成命令」
        │  棋盘 / 大地图 / UI / 演出     │
        └───────────────┬──────────────┘
                        │ 引用
        ┌───────────────▼──────────────┐
        │  ChessSage.Core              │  ← 零 UnityEngine 依赖的纯逻辑
        │  规则 / AI编排 / 业力 / RPG   │
        └──────────────────────────────┘
```

**依赖只能自上而下，禁止反向与横向。**

| 程序集 | `noEngineReferences` | 可否被引擎外单测 | 职责 |
|:---|:---:|:---:|:---|
| `ChessSage.Core` | ✅ true | ✅ 可以 | 全部游戏逻辑与数据契约 |
| `ChessSage.Presentation` | false | ❌ | 渲染与 UI，把 Core 状态可视化 |
| `ChessSage.App` | false | ❌ | 组合根、流程状态机、服务装配 |
| `ChessSage.Editor` | false | ❌ | Editor-only 构建与生成工具 |
| `ChessSage.Tests` | false | ✅ 可以 | 黄金等价性测试（Editor-only，EditMode） |

> **`ChessSage.Core` 零引擎依赖是本项目最重要的架构决策。**
> 它让 1.6 万行游戏逻辑可以在**没有 Unity 编辑器的环境**中被 `dotnet test` 真实编译与回归验证
> （见 [`tools/verify/dotnet/`](../tools/verify/dotnet/)），并让多棋类规则引擎可被并行开发与独立测试。

---

## 2. 目录结构

```
Assets/ChessSage/
├── Core/                     # ── ChessSage.Core（纯逻辑）
│   ├── Model/                #    BoardState / Piece：棋盘与棋子数据模型
│   ├── Config/               #    ConfigLoader / GameConfig：JSON 真源加载
│   ├── Json/                 #    JsonPatch(RFC6902) / JsonSchemaLite
│   ├── Rules/                #    规则引擎：基类 + 6 棋类实现
│   ├── Mechanism/            #    机制引擎：数据驱动的可改写规则
│   ├── Ai/                   #    棋力 AI（Minimax + Alpha-Beta）
│   ├── Orchestration/        #    ★ AI 作弊编排：意图→Patch→校验→生效
│   ├── Samsara/              #    ★ RPG 元层：业力/识破/技能/关卡/结局/Boss
│   ├── Game/                 #    GameSession：回合流程编排
│   └── Util/                 #    DeterministicRng / GameLog
│
├── Presentation/             # ── ChessSage.Presentation（表现层）
│   ├── Board/                #    棋盘视图：支持全部 6 棋类的通用渲染
│   ├── Overworld/            #    ★ 六道大陆（2.5D 等距大地图，全面重做）
│   ├── UI/                   #    UI Toolkit 界面（UXML + USS）
│   ├── Cinematics/           #    CG 视频 / 立绘演出
│   ├── Audio/                #    音效与 BGM
│   ├── Art/                  #    美术导入设置（SpriteAtlas 等）
│   └── Scenes/               #    场景（Main.unity 为启动场景）
│
├── App/                      # ── ChessSage.App（应用层）
│   ├── Bootstrap/            #    组合根：服务注册与启动
│   ├── Flow/                 #    流程状态机：标题→大陆→选关→对局→剧情→结局
│   └── Services/             #    对局服务 / 存档服务 / 音频服务 / AI 服务
│
├── Editor/                   # ── ChessSage.Editor（Editor-only）
│   ├── BuildScript.cs        #    无 GUI 构建入口（tools/build.sh 调用）
│   ├── ProjectBootstrapper   #    首次导入时生成 URP 资产与启动场景
│   └── AssetImporters/       #    美术/音频批量导入设置
│
└── Tests/                    # ── ChessSage.Tests（EditMode）
    ├── *GoldenTests.cs       #    6 棋类黄金等价性测试
    ├── JsonPatchTests.cs     #    RFC6902 实现测试
    └── Fixtures/             #    黄金数据（由 tools/golden/*.py 从 Python 引擎生成）
```

---

## 3. 表现层设计原则

### 3.1 严格单向数据流

```
玩家输入 ──> InputAdapter ──> Core 命令 ──> Core 状态变更 ──> View 刷新
                                    ▲
                            （不反向：View 绝不修改 Core）
```

- **View 无状态**：每次刷新都是「读 Core 当前状态 → 重建视觉」。这杜绝了 UI 与逻辑状态不一致。
- **命令式 API**：`GameSession` 对外暴露 `MakeMove` / `RequestAiCommand` 等命令方法，返回结果对象供 UI 展示。

### 3.2 通用棋盘视图

`BoardView` **不为 6 个棋类各写一套**，而是数据驱动：

- 棋盘尺寸、格子坐标、棋子集合全部来自 `BoardState` / `GameConfig`；
- 各棋类差异由 `VariantRuleBase` 的能力标志（`IsPlacementGame`、`SupportsCheckRules`、`BoardWidth/Height`）驱动分支；
- 新增棋类 = 新增一个 `VariantRuleBase` 子类，**无需改动任何表现层代码**。

### 3.3 六道大陆重制方案

原网页版用**自研 WebGPU 管线**（高度场 + compute 细节场 + SSAO + 体积光）渲染大地图，该管线在浏览器上实际无法稳定运行。Unity 重制方案：

| 维度 | 方案 |
|:---|:---|
| 网格 | Unity **Tilemap**（`Grid` + `Tilemap`），等距网格（Isometric） |
| 渲染 | **URP 2D Renderer**；地形用 Tilemap，动态光照用 2D Light |
| 地形生成 | **完全程序化**（噪声 + 区域主题），取代原版预制 `overworld.json` 色块 |
| 角色/物件 | `SpriteRenderer` + Y 轴排序；对象池复用 |
| 昼夜与氛围 | `Volume` 后处理 + 区域主题色调 |
| 探索迷雾 | 未访问区域以 Tilemap 遮罩 + 着色器实现「❓→图标」揭示 |
| 相机 | 固定等距角，跟随平滑 + 边界钳制 + 缩放 |
| 碰撞 | Tilemap 障碍层 + 圆形碰撞（格中心 + 半径），对齐原版语义 |

**性能要点**：Tilemap 合批绘制（单 draw call 量级）、对象池、SpriteAtlas、摄像机剔除、静态地形不每帧重建。

---

## 4. 验证策略（三层护栏）

| 层 | 手段 | 覆盖范围 | 是否需要 Unity |
|:---:|:---|:---|:---:|
| **L1** | `dotnet test tools/verify/dotnet/ChessSage.Core.Verify.csproj` | Core 全量逻辑 + 黄金等价性 | ❌ |
| **L2** | `csc` + Unity 参考程序集编译全工程 | Core + Presentation + App + Editor 的**编译期**正确性 | 需 Editor 文件（无需许可证） |
| **L3** | `bash tools/build.sh test` / `build.sh windows` | EditMode 测试、播放器构建、交叉编译 | ✅ 需许可证 |

> **L2 的价值**：Unity 参考程序集（`UnityEngine.*.dll`）随编辑器分发，无需激活许可证即可用 Roslyn 编译整个工程。
> 这让我们在无许可证环境下依然能发现**全部编译错误**，把「不可验证」的风险压到最低。

---

## 5. 质量门禁（每次提交前必须全绿）

1. `dotnet test tools/verify/dotnet/ChessSage.Core.Verify.csproj` → 0 失败
2. `bash tools/verify/compile-check.sh` → 0 编译错误
3. 改动若涉及规则/编排/业力，必须有对应**新增或更新的黄金用例**
4. 依赖方向检查：`Presentation` 不引用 `App`；`Core` 不含任何 `UnityEngine` 引用