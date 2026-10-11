# 三层验证体系与执行指南

> 本文件是本工程的质量基线。任何人改动代码后，都应能按下面的命令在**一分钟内**确认自己没有破坏任何东西。

---

## 1. 为什么需要三层

Unity 的常规验证链路是「启动编辑器 → Test Runner → 构建」。这条链路**必须有许可证**，
在 CI、无头容器、新同事的第一次 clone 等场景下常常不可用。

本工程的应对是：**把可脱离引擎的验证尽可能前移**，形成三层递进的护栏。

| 层 | 手段 | 需要 Unity | 需要许可证 | 能抓什么 | 不能抓什么 |
|:--|:--|:--:|:--:|:--|:--|
| **L1** | `dotnet test` 编译并运行 `Assets/ChessSage/Core` + `Tests` | ❌ | ❌ | 逻辑错误、行为等价性、算法复杂度、AI 决策耗时 | 一切与 Unity API 有关的问题 |
| **L2** | `tools/verify/compile-check.sh`（Unity 自带 Roslyn + 参考程序集） | 仅需文件 | ❌ | **全工程编译错误**、类型/签名/API 误用 | 运行期行为 |
| **L3** | `tools/build.sh`（真实编辑器） | ✅ | ✅ | 渲染结果、EditMode 测试、出包产物 | — |

关键洞察：**L2 覆盖了「代码能不能编译」这个最常见、最高频的错误类别**，
而它完全不需要许可证。因此绝大多数低级失误会在提交前被自动拦下。

---

## 2. L1：逻辑回归（引擎外）

### 执行

```bash
cd ChessSage-Unity
CHESSSAGE_ASSETS_ROOT=$PWD/Assets \
  dotnet test tools/verify/dotnet/ChessSage.Core.Verify.csproj -v q --nologo
```

> `CHESSSAGE_ASSETS_ROOT` 用于告诉测试去哪儿找 `Assets/StreamingAssets/Configs`。
> 在 Unity 内运行时不需要设这个变量——`TestEnv` 会自动退化为 `Application.dataPath`。

### 覆盖范围（当前 57 项，全部通过）

| 测试文件 | 项数 | 覆盖 |
|:--|--:|:--|
| `RuleEngineGoldenTests` + `DongwuqiGoldenTests` + `TiaoqiGoldenTests` | 3 | 象棋 / 动物棋 / 跳棋黄金等价 |
| `WeiqiGoldenTests` + `WuziqiGoldenTests` + `HeibaiqiGoldenTests` | 6 | 围棋 / 五子棋 / 黑白棋黄金等价 |
| `MechanismAndSessionTests` | 13 | 机制引擎 + `GameSession` 回合流程 |
| `OverworldTests` | 13 | 大地图数据口径 + 全图流场寻路 + 迷雾位图 + 分块窗口 |
| `VerticalSliceTests` | 7 | 象棋垂直切片端到端不变量（输入 → 走子 → AI → 悔棋） |
| `IsoGridMathTests` | 6 | 等距投影正/逆变换往返一致性（112×84 全图逐格校验） |
| `AiPerformanceTests` | 6 | AI 决策耗时护栏、确定性、预算截断、浅克隆不污染棋局 |
| `JsonPatchTests` | 3 | RFC 6902 JSON Patch 应用 |
| **合计** | **57** | 全部通过 |

### 设计要点

- `Tests/TestEnv.cs` **不含任何 `UnityEngine` 引用**，因此同一套测试既能在 Unity Test Runner
  内运行，也能在纯 .NET 下运行——这是 L1 层能够存在的前提。
- 大地图测试**直接校验真实数据**（`Configs/global/overworld.json`），
  把 `legacy-web/docs/overworld/` 里的设计口径（外圈 6 格 wall ≈23.5%、POI 可达等）
  固化为可执行断言，防止回归。

---

## 3. L2：编译门禁（无需许可证）

### 执行

```bash
bash tools/verify/compile-check.sh
# 详细输出（含全部警告）
VERBOSE=1 bash tools/verify/compile-check.sh
```

### 原理

Unity 把用户脚本编译进 `Library/ScriptAssemblies`，这需要启动编辑器（因而需要许可证）。
但编辑器安装目录下的**参考程序集**与 **Roslyn 编译器**是纯文件：

```
$UNITY_ROOT/Editor/Data/
├── Managed/UnityEngine/*.dll              # 67 个引擎模块参考程序集
├── NetStandard/ref/2.1.0/netstandard.dll# BCL 参考程序集
└── DotNetSdkRoslyn/csc.dll# 编译器
```

于是可以在任意机器上直接调用 `csc`，按依赖顺序编译 4 个程序集：

```
ChessSage.Core (netstandard + Newtonsoft)
      ↓
ChessSage.Presentation (+ UnityEngine)
      ↓
ChessSage.App (+ Presentation)
      ↓
ChessSage.Editor (+ UnityEditor, -define:UNITY_EDITOR)
```

### 设计要点

- **不引用包程序集**是刻意的：`Packages/manifest.json` 里的 URP / Input System /
  Addressables 在离线环境下只以源码分发（无预编译 DLL），引用它们会导致无法离线编译。
  因此表现层代码只依赖**内置 UnityEngine 模块**，包通过场景配置而非代码依赖接入。
- 自动探测 `Editor` / `editor` 大小写（China 包为 `Editor`，官方 Linux 包历史为 `editor`）。
- 响应文件中的引用必须保留 `-r:` 前缀，否则会被当作源文件解析并报
  `CS2015: is a binary file instead of a text file`。

### 预期输出

```
==> [1/4] ChessSage.Core（纯逻辑，零引擎依赖）
  ✅ ChessSage.Core  (警告 0)
==> [2/4] ChessSage.Presentation（依赖 Core + UnityEngine）
  ✅ ChessSage.Presentation  (警告 0)
==> [3/4] ChessSage.App（组合根）
  ✅ ChessSage.App  (警告 1)
==> [4/4] ChessSage.Editor（Editor-only 工具链）
  ✅ ChessSage.Editor  (警告 0)
🎉 L2 编译门禁通过：4 个程序集全部零错误
```

---

## 4. L3：Unity 运行期（需许可证）

> ⚠️ **本次迁移环境中无有效 Unity 许可证，以下命令尚未执行过。**

```bash
# 首次打开：生成 URP 资产与两个场景（幂等）
bash tools/build.sh bootstrap

# Unity EditMode 测试
bash tools/build.sh test

# Linux → Windows x64 交叉编译
bash tools/build.sh windows
```

执行完毕后，请在本文件追加实际结果并把 ADR §8.3/§8.4 的勘误更新为「已复验」。

---

## 5. 新增代码时的门禁要求

| 你新增的东西 | 必须通过 |
|:--|:--|
| `Core/` 下的逻辑 | L1（新逻辑要有测试）+ L2；**且不得出现 `UnityEngine` 引用** |
| `Presentation/`、`App/`、`Editor/` | L2；若有可脱离引擎的算法，应下沉到 `Core` 并加 L1 测试 |
| 性能敏感路径 | L1 里加耗时断言（参考 `AiPerformanceTests`），防止悄悄退化 |
| 依赖包程序集的类型（如 URP 类型） | 只能用**反射**访问，否则 L2 会失败——这正是 `ProjectBootstrapper` 创建 URP 资产的方式 |

---

## 6. 已知的验证缺口

| 缺口 | 风险 | 缓解 |
|:--|:--|:--|
| 未在真实 Unity 中运行过 | 渲染结果、性能表现未知 | L2 保证无编译/类型错误；逻辑正确性由 L1 保证 |
| 表现层无自动化测试 | 表现层 bug 需手动发现 | 已刻意把表现层做到「薄」，逻辑全在 Core（L1 覆盖） |
| 无正式美术 | 画面为程序化占位 | 已提供 `PlaceholderArt` / `TerrainAtlas` 程序化生成，保证可运行 |
| 等距坐标手写数学 | 易错 | 已用 `IsoGridMathTests` 对 112×84 全图做正逆变换往返校验 |