# L3 玩家视角试玩报告

> 日期：2026-10-11 ｜ 环境：Unity 2022.3.62f3c1 · Linux headless · Xvfb + 软件渲染
> 结论：**对局闭环在真实 Unity 运行时走通**，并借此抓到 3 个真实缺陷（均已修复）；画面审阅又触发一轮深挖，确认 1 处引擎行为合法、发现 1 处上游遗留缺陷（黑白棋棋子 id 重复，影响 53/60 步）。画面验证由数据一致性渲染提供（无头环境 GPU 输出受限，见 §4）。

---

## 1. 前置：许可证激活（补齐 L3 缺口）

| 步骤 | 结果 |
|:--|:--|
| `Unity.Licensing.Client --activate-all --include-personal`（用户账号登录） | `ASSIGN_SEAT 200`，tag `UnityPersonal` |
| `Unity.Licensing.Client --activate-ulf` | `Successfully activated ULF license` |
| 编辑器验证 | `Successfully updated license`，工程正常创建（原 `No valid license` 错误消失） |

ADR §8.3/§8.4 的勘误状态由此更新：许可证问题已真正落地解决。

## 2. 真实 Unity 中的验证结果

### 2.1 工程引导（ProjectBootstrapper，batchmode 实跑）

- URP 14.0.12 包成功解析导入；
- `Main.unity` / `Overworld.unity` 两个场景生成；
- `UniversalRenderPipelineAsset.asset` + `UniversalRenderer2D.asset` 创建并挂载。

### 2.2 EditMode 测试（Unity Test Runner，非 dotnet 模拟）

| 轮次 | 结果 |
|:--|:--|
| 第 1 轮 | 56/58（2 项 AI 耗时超标） |
| 第 2 轮 | 56/58（预算检查只作用于层间，单层无法中断） |
| **第 3 轮** | **58/58 全绿** |
|  第 4 轮（新增 `AiMoveLegalityTests` 3 项后） | **61/61 全绿** |

### 2.3 播放模式试玩（脚本化玩家操作）

日志证据（`play-main6.log`）：

```
[对局] 点击 (0,9)：回合=red
[对局] 点击 (0,8)：回合=red
[对局] 走子 r_chariot_1→(0,8) 成功=True 历史=1
[对局] 点击 (0,8)：回合=red
[对局] 点击 (1,8)：回合=red
[对局] 走子 r_chariot_1→(1,8) 成功=True 历史=3
[试玩] 象棋流程走通：回合=red，历史=4 步
[试玩] 完成，正常退出
```

完整闭环：**进入场景 → 点击选子 → 高亮可走点 → 落子 → AI 应手 → 二回合 → 退出**，历史 4 步（玩家×2 + AI×2）与纯逻辑探针一致。

## 3. 试玩抓到的真实缺陷（全部已修复）

| # | 缺陷 | 根因 | 修复 |
|:--:|:--|:--|:--|
| 1 | **AI 单步卡顿 387-779ms**（Unity Mono 比桌面 .NET 慢 4 倍；且预算检查只在迭代加深层间，单层不可中断） | `MaxSearchMilliseconds` 400ms 形同虚设 | 时间检查下沉到 Minimax 节点级（每 256 节点抽样取钟）；预算降为 250ms |
| 2 | **整个游戏点不动**（试玩点击全部无效，回合恒 red、历史恒 0） | `GameScreen.OnEnable` 订阅输入事件时 `Input` 还是 null（组合根同帧先 AddComponent 后注入），订阅被静默跳过。**真实玩家同样中招** | 订阅移到 `Attach`（注入完成后），加幂等保护 |
| 3 | 试玩工具自身：反射 C# 事件的 private backing field 不可靠 | `GetField("CellClicked", Public)` 拿不到委托 | `BoardInputRouter.SimulateClick()` 显式公开 API |
| 4 | 试玩工具自身：`WaitForEndOfFrame` 在 batchmode 永不返回 → 协程无声卡死 | batchmode 无真实帧提交 | 改为帧计数 |

> #1、#2 是**只有真实运行时才能暴露**的缺陷——L1（纯逻辑）与 L2（纯编译）在结构上不可能发现它们。这正是 L3 层的价值证明。

## 3.1 画面审阅触发的新一轮排查（缺陷 #5）

玩家视角审阅第2 帧（`chess_3_第二回合完毕.png`）时出现一个"视觉上不可能"的局面：
**黑方炮出现在红方底线 y=9，且红方底线两匹马都消失了**。

按象棋规则，炮确实能隔子吃远子，于是逐层排查：

| 环节 | 结论 |
|:--|:--|
| 渲染层坐标 / 阵营映射 | 无问题 |
| `pieces_black.json` 中cannon 的走法定义 | 正确（`screens:0`→空格移动，`screens:1`→吃子） |
| `JumpRayRuleBase.RayMoves` 实现 | 正确 |
| x=1 纵列初始占用 | 黑炮(1,2)、红炮(1,7)、红马(1,9) |
| **判定** | **合法**——黑炮以红炮为炮架，从 (1,2) 吃到 (1,9) 的红马，是一步合法杀棋 |

结论：**引擎无缺陷，是我的初始断言写错了**。原本写的护栏「开局炮不可能到对方底线」本身就是一条错误规则，已删除。

替换为更有价值的护栏——**独立几何校验**（`Xiangqi_Opening_EveryPlyMatchesIndependentGeometry`）：
不看规则引擎怎么说，而是按象棋走法定义（车直线无阻挡、炮需恰好一个炮架、马蹩腿、象塞象眼不过河、士将九宫、卒过河可横移）从盘面自行判定每一步。避免了「用引擎校验引擎」的循环论证。

## 3.2 排查中意外发现的真实缺陷 #5：黑白棋棋子 id 重复

新护栏在跑黑白棋时连续报红，追查后定位到**真实缺陷**：

| 项 | 内容 |
|:--|:--|
| 现象 | 棋盘上出现两个 `white_disc_3` |
| 根因 | `HeibaiqiRuleEngine.NextDiscId` 统计编号时按 `p.Side != side` 过滤，但**翻面机制**（`ApplyFlipCaptures`）会把白子改成黑子，于是"白方已用的最大编号"被漏算，`max+1` 重新发出已占用的 id |
| 影响 | 悔棋、翻转记录、存档回放按 id 索引时会指向错误棋子；实测 60 步对局中**53 步的 placed_id + 45 步的 flipped 数组受影响** |
| 上游 | `legacy-web/heibaiqi/main.py` 的 `_place_disc_and_flip` 有**完全相同**的缺陷，C# 移植忠实复现 |
| 修复 | 编号扫描改为**只按 id 字面前缀统计，与阵营无关**，保证 id 全局唯一 |
| 夹具 | 按用户决策「修正实现 + 更新夹具」：`heibaiqi_golden.json` 的 `placed_id` / `flipped` 已更新为修正值，原 Python 值分别保留在 `placed_id_python_original` / `flipped_python_original` 字段，`_note` 记录偏离原因 |

新增护栏 `AiMoveLegalityTests` 共 3 项（每手id 唯一性、连续对弈合法性、逐手几何校验），本次共修 4 个文件。

## 4. 画面验证说明（诚实披露）

无 GPU 的 headless 沙箱中，Unity 渲染链存在硬限制（本轮实测排除了 4 条路径）：

| 尝试 | 结果 |
|:--|:--|
| `ScreenCapture.CaptureScreenshot` | 日志成功但文件不落盘 |
| `cam.Render()` → ReadPixels | 全黑（绕开 SRP） |
| backbuffer `ReadPixels` | 灰屏（batchmode 无真实呈现） |
| 相机 `targetTexture` + 帧循环 | 黑（llvmpipe 下 URP 出图仍不可靠） |

**采取的替代方案**：用确定性渲染器从**同一份真实数据**生成玩家视角画面（`Logs/shots/*.png`）——

- 大地图：直接读取 Unity 运行时用的 `overworld.json` baked 数据，按与 `IsoGrid.cs`/`TerrainPalette.cs` 一致的等距投影与调色板绘制（112×84 全图 + 九大区域 + POI + 道路 + 出生点视野圈）；
- 象棋 3 帧：由 C# `GameSession`（真实规则引擎 + 真实 AI）逐帧导出棋局，按棋盘几何渲染。

因此画面**不是** Unity GPU 输出，但数据链路与渲染规格与游戏本体一致，可作为玩法/视觉的可审查证据。带 GPU 的本地环境跑 `PlaythroughCapture` 即可获得 Unity 真实渲染截图（同一命令，无需改动）。

## 5. 产物清单

| 文件 | 内容 |
|:--|:--|
| `Logs/shots/overworld_iso.png` | 六道大陆等距全图（真实 baked 数据） |
| `Logs/shots/chess_1_开局.png` | 象棋开局（红先） |
| `Logs/shots/chess_2_AI应手完毕.png` | 玩家走車、AI 应手后 |
| `Logs/shots/chess_3_第二回合完毕.png` | 第二回合完成 |
| `Logs/shots/chess_frames.json` | 逐帧棋局快照（C# 导出） |
| `Assets/ChessSage/Editor/PlaythroughCapture.cs` | 可复用的试玩录制工具 |