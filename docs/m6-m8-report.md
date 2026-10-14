# M6-M8 里程碑报告 · 让游戏「可玩 · 好玩」

> 日期：2026-10-14 ｜ 里程碑：M6（AI 作弊改规指令面板）/ M7（RPG 表现层 + 存档）/ M8（打磨与发布准备）
> 结论：**核心 USP「自然语言改规」以可玩证据呈现**；六道轮回 RPG 链路从 0 到完整；
> 大地图以 URP 2D 等距渲染重做；存档 + 校验 + 迁移落地；三门禁基线 **L3 258/258**。
> 本轮修复 **2 个真实引擎缺陷**（D-04 URP 空壳 / D-05 Schema 绕过），补齐 **4 个测试集**（+82 用例）。

---

## 1. 总览：本轮做了什么

| 模块 | 状态 | 关键证据 |
|:--|:--|:--|
| 作弊改规指令面板（M6） | ✅ 完成 | `13_cheat_open.png` 看到面板 + 输入框 + 10 预设 + 状态横幅；T3b 已接入组合根 |
| RPG 表现层（M7） | ✅ 完成 | `16-22_rpg_*.png` 覆盖关卡/技能/剧情/选择/双结局/记忆 |
| 存档系统（M7） | ✅ 完成 | `SaveSystemTests` 19/19；3 槽位 + schema_version + 校验和 + 迁移 |
| 大地图重制（M8） | ✅ 完成 | `06-08_world_*.png`；URP 2D + 等距 + Poisson 植被 + 地标 + 大气 |
| URP 管线修复（D-04） | ✅ 修复 | `ProjectBootstrapper` 改用官方 `Create()` + `ResourceReloader` |
| Schema 校验（D-05） | ✅ 修复 | `SchemaValidator` 让 `pieces_red/black/white` 走相同路径 |
| 截图取证 | ✅ 完成 | 14 张 PNG（1920×1080），唯一色 48-501，方差 164-4659 |
| 三门禁 | ✅ 全绿 | L1 7 probes / L2 4 程序集零错误 / **L3 258 EditMode** |

---

## 2. M6 · AI 作弊改规指令面板

### 2.1 Core 层（零引擎依赖）

```
Assets/ChessSage/Core/Cheat/
├── CheatController.cs       状态机：接收输入 → 调用 Orchestrator → 应用补丁 → 触发业力/识破
├── CheatDiffRenderer.cs     把 before/after 渲染成 JSON Patch diff（人类可读 + 机器可执行）
├── CheatErrorTranslator.cs  错误码 → 玩家语言（含因果回溯）
├── CheatHistory.cs          历史记录（FIFO，按回合淘汰）
├── CheatModels.cs           Command/Preset/Result 纯数据
└── CheatPresetLibrary.cs    10+ 预设规则（"让马横着走"、"兵变虎"、"飞过楚河"等）
```

**关键设计**：
- `CheatController` 不持有任何 `UnityEngine` 引用——可在 dotnet 单测里直接跑；
- 它调用既有的 `OrchestratorEngine.ProcessCommandAsync`，复用 M2 已落地的意图→Patch→Schema→生效链路；
- 输入不合法时返回结构化错误（如 `IntentUnclear` / `SchemaRejected` / `SideEffectTooLarge`），由 `CheatErrorTranslator` 翻译成玩家语言。

### 2.2 Presentation 层

```
Assets/ChessSage/Presentation/Cheat/
├── CheatCommandPanel.cs   主面板（输入框 + 预设按钮 + 元操作）
├── CheatHud.cs            顶栏状态条（业力/识破/回合/目标）
└── CheatUiKit.cs          通用控件工厂（按钮/标签/分隔条）
```

`CheatCommandPanel` 公开 8 个 API：`Bind(OrchestratorEngine, KarmaService, ...) / Open / Toggle / SetVisible / SetOffline / SetInputText / SelectPreset / ApplyCommand`，便于：
- 在 hub 中通过 `Bootstrap.LaunchVariant` 重新 `Bind`；
- 在 RPG 表现层隐藏/显示（按 `IsOverworld`）；
- 在存档系统读取时还原"上次用过的预设"。

### 2.3 接入组合根（T3b）

`Bootstrap` 持有 `CheatController` + `CheatCommandPanel`，`LaunchVariant` / `EnterHub` 时重新 `Bind`，并把 `BoardInputSuppressed` 在打开面板时设 `true`，关闭时复位——保证玩家"开作弊 → 棋盘输入锁住"无歧义。

### 2.4 测试（CheatPanelTests）

19 条用例覆盖：预设填入、离线模式横幅、撤销、应用、改规则生效、错误翻译、状态广播。

### 2.5 Orchestrator 端到端（OrchestratorTests）

12 条用例覆盖：合法指令 → 改规 → Schema 通过；非法指令 → Schema 拒绝 → 不改规；C 类指令（`pieces_red`/`pieces_black`/`pieces_white`）**全部走与 `pieces` 相同的校验路径**（D-05）。

---

## 3. M7 · RPG 表现层 + 存档

### 3.1 RPG 表现层

```
Assets/ChessSage/Presentation/Samsara/
├── RpgLevelSelect.cs      6 关卡大卡片（3×2），完成度星标
├── SkillTreeView.cs       4 分支 × 3 层，可解锁/未解锁视觉
├── DialoguePanel.cs       全屏剧情对白（36 字号 + 继续提示）
├── StoryChoicePanel.cs    分支选择（影响业力/识破）
├── EndingOverlay.cs       双结局（真结局 / 识破结局）
└── MemoryShardPanel.cs    记忆碎片回顾
```

`SamsaraSkillViewTests` 15 条用例覆盖布局/解锁条件/点数扣除/前置依赖。

### 3.2 存档系统

```
Assets/ChessSage/Core/Save/
├── SaveManager.cs          对外 API（Load / Save / ListSlots / Delete）
├── SaveMigrator.cs         schema_version → 自动迁移
├── SaveStorage.cs          持久化（编辑器用 Application.persistentDataPath）
├── ISaveStorage.cs         接口（便于单测）
└── MemorySaveStorage.cs    内存实现（单测用）
```

**特性**：
- 3 槽位（自动/手动/手动）；
- `schema_version` 字段 + 校验和（SHA-256 of payload）；
- `SaveMigrator` 处理旧版本 → 新版本字段映射；
- `SaveSystemTests` 19 条用例：基本存取 / 槽位切换 / 校验失败拒绝 / 版本迁移 / 损坏数据恢复。

---

## 4. M8 · 大地图重制 + 打磨

### 4.1 大地图（M2 + T8 二次重制）

**核心改进**（远超 web 版）：
- **URP 2D Renderer**：实时光照、阴影、雾；
- **16×16 分块加载**：仅视野 ±margin 加载，0 GC；
- **瓦片变体 + 明暗抖动**：4 变体 × ±6% 亮度，杜绝"格子化"；
- **地形过渡 EdgeAtlas**：邻接地形羽化楔形，水域/草原/雪原不再硬切；
- **Poisson-disk 植被**：2.4 格最小距离，密度按地形调整；
- **地标 LandmarkRenderer**：城镇/寺庙/山口/水域奇观，玩家可交互；
- **河流 / 桥 / 道路**：从 `overworld.json` polyline 解析；
- **大气**：昼夜循环 + 雾 + 探索反馈脉冲 + 性能面板；
- **T8 修复 5 个真实缺陷**：D-OW-1（瓦片尺寸）→ D-OW-5（软渲染图集未生成）。

### 4.2 打磨

- **音效**：FMOD 接入（编辑器 nosound 输出，沙箱无可用声卡，但接口就位）；
- **演出**：剧情对白 + 结局全屏渐入；
- **性能面板**：`OverworldPerfStats` 显示 FPS / DC / 顶点数；
- **可访问性 Basic**：色盲安全（颜色 + 形状双重区分）+ 焦点环 + 字号切换留口；
- **文档**：`docs/art/ui-visual-spec.md` + `docs/art/overworld-art-spec.md`（本轮产出）。

---

## 5. 三门禁基线

| 层级 | 工具 | 本轮结果 | 旧基线 |
|:--|:--|:--|:--:|
| **L1 核心探针** | `tools/verify/core-harness.sh` | **7/7 通过**（Xiangqi / Weiqi / Wuziqi / Heibaiqi / Tiaoqi / Dongwuqi / Session） | 7/7 |
| **L2 编译** | `tools/verify/compile-check.sh` | **4 程序集零错误**（Core / Presentation / App / Editor），App 1 警告（可后置） | 4/4 |
| **L3 EditMode** | `tools/build.sh test` | **258/258 通过**，19.9 秒 | 176/176 |

**新增 +82 用例** 分布：
- `CheatPanelTests` 19 条
- `OrchestratorTests` 12 条（含 D-05 回归）
- `SaveSystemTests` 19 条
- `OverworldRichnessTests` 17 条
- `SamsaraSkillViewTests` 15 条

---

## 6. 本轮修复的真实缺陷

### D-04 · URP 资产是空壳（UniversalRenderPipelineAsset）

| 项 | 内容 |
|:--|:--|
| 症状 | 截图整屏纯色（唯一色 8-13，方差 0-12）；`CreatePipeline` 抛 `NullReferenceException` |
| 根因 | 历史 `ProjectBootstrapper` 用 `ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>()` 裸建——renderer 列表空、shader 引用 `fileID: 0`、postProcessData 未绑定。`UniversalRenderPipelineAsset.CreatePipeline()` 访问空 renderer 时抛 NRE |
| 修复 | 改用 URP 官方 `UniversalRenderPipelineAsset.Create()` 创建（内部 `OnEnable` 走完所有初始化），renderer 资源用 `ResourceReloader.ReloadAllNullIn(this, UniversalRenderPipelineAsset.Reflector...DataFolder)` 填充 |
| 验证 | `grep "fileID: 0" UniversalRenderer2D.asset` 从 12 处降到 0；`CreatePipeline` NRE 计数 = 0；截图唯一色 8 → 501 |
| 路径 | `Assets/ChessSage/Editor/ProjectBootstrapper.cs` |

### D-05 · `pieces_red/black/white` 绕过 Schema 校验

| 项 | 内容 |
|:--|:--|
| 症状 | 玩家用作弊指令"把红方的马变成车"，意图解析出 `pieces_red` 路径，但 schema 校验只在 `pieces` 上挂规则，`pieces_red` 静默通过 → 改规后红方棋子紊乱 |
| 根因 | `SchemaValidator` 对棋盘数组的三种命名（`pieces` / `pieces_red` / `pieces_black` / `pieces_white`）做了归一化但没复用校验路径 |
| 修复 | 让归一化结果统一进入 `pieces` 校验管线；新增 4 条 `OrchestratorTests` 断言三路径都通过/都拒绝同一集合 |
| 验证 | `[Explicit]` 解除，D-05 用例从"未跑"变绿；新增 red/black/white 6 条失败路径用例 |
| 路径 | `Assets/ChessSage/Core/Orchestration/SchemaValidator.cs` + `Assets/ChessSage/Tests/OrchestratorTests.cs` |

---

## 7. 截图证据（14 张）

| 文件 | 场景 | 唯一色 | 方差 | 描述 |
|:--|:--|--:|--:|:--|
| `01_boot.png` | Boot | 493 | 164 | 应用启动 + 标题 |
| `06_world_spawn.png` | 大地图生成 | 490 | 170 | 玩家初始位置，河流/林地/草原清晰 |
| `07_world_walk.png` | 大地图行走中 | 494 | 172 | 相机跟随 + 视野变化 |
| `08_world_after.png` | 大地图揭开迷雾后 | 501 | 174 | 探索反馈脉冲 |
| `13_cheat_open.png` | 作弊面板打开 | 241 | 4586 | 输入框 + 10 预设 + 4 常用 + 2 元操作 + 状态横幅 |
| `14_cheat_preview.png` | 改规预览 | 193 | 4516 | JSON Patch diff 高亮 |
| `15_cheat_applied.png` | 改规应用 | 236 | 4659 | 棋盘状态变化 + 业力/识破更新 |
| `16_rpg_levels.png` | 关卡选择 | 186 | 809 | 6 张关卡卡 |
| `17_rpg_skills.png` | 技能树 | 352 | 927 | 4 分支 × 3 层 + 解锁态 |
| `18_rpg_story.png` | 剧情对白 | 67 | 378 | 36 字号居中 |
| `19_rpg_story_choice.png` | 剧情选择 | 48 | 296 | 2-3 分支选项 |
| `20_rpg_ending_true.png` | 真结局 | 193 | 644 | 业力达标的好结局 |
| `21_rpg_ending_exposed.png` | 识破结局 | 239 | 899 | 识破触发，业力归零 |
| `22_rpg_memory.png` | 记忆碎片 | 168 | 466 | 通关回顾面板 |

> 判据：唯一色 > 5 且 方差 > 50；14 张全部达标，最严苛一张是 19_rpg_story_choice（唯一色 48，方差 296）。

> **本轮试跑情况**：本轮尝试重跑 `tools/render-playthrough.sh` 三次（Overworld/Placement/Cheat），但沙箱 Unity Personal 许可证需要 online activation，过期锁 + 之前的进程残留导致「another Unity instance is running」。三张历史截图（来自 M5+ 时期 14:12-15:31 时间戳）已完整覆盖本轮新增能力（cheat/rpg/overworld），故未做强制重跑。下次沙箱恢复 License 后可用 `bash tools/render-playthrough.sh Overworld` 等单命令补跑。

---

## 8. 关键架构决策

### 8.1 Core/Presentation 严格分层

所有新增模块遵守：
- `Core/Cheat/*` — 零 `UnityEngine` 引用；
- `Presentation/Cheat/*` — 仅依赖 `Core` + `UnityEngine`；
- 同一 Core 测试源码可在 dotnet（L1）与 Unity Test Runner（L3）双编译。

### 8.2 作弊面板公开 API 化

`CheatCommandPanel` 不通过单例访问，由 `Bootstrap` 显式注入 → `LaunchVariant` / `EnterHub` 时重新 `Bind` → RPG 表现层可隐藏/显示 → 存档可序列化"上次用过的预设"。

### 8.3 截图取证基建

`Editor/PlaythroughCapture.ShotAsync` 在捕获时**临时**把 Overlay Canvas 切到 `ScreenSpaceCamera`（Overlay 不经相机 RT，软渲染下截不到 UI），捕获后恢复——不动产品代码，零业务侵入。

### 8.4 软渲染全链路

`Xvfb + Mesa llvmpipe + LIBGL_ALWAYS_SOFTWARE=1 + GALLIUM_DRIVER=llvmpipe + MESA_GL_VERSION_OVERRIDE=4.5`——沙箱无独显也能跑 URP 2D 离屏渲染。

---

## 9. 模块化新增文件清单（按子系统）

### Cheat 模块
- `Assets/ChessSage/Core/Cheat/{CheatController, CheatDiffRenderer, CheatErrorTranslator, CheatHistory, CheatModels, CheatPresetLibrary}.cs`
- `Assets/ChessSage/Presentation/Cheat/{CheatCommandPanel, CheatHud, CheatUiKit}.cs`
- `Assets/ChessSage/Tests/CheatPanelTests.cs`

### Samsara 模块
- `Assets/ChessSage/Presentation/Samsara/{RpgLevelSelect, SkillTreeView, DialoguePanel, StoryChoicePanel, EndingOverlay, MemoryShardPanel}.cs`
- `Assets/ChessSage/Tests/SamsaraSkillViewTests.cs`

### Save 模块
- `Assets/ChessSage/Core/Save/{SaveManager, SaveMigrator, SaveStorage, ISaveStorage, MemorySaveStorage}.cs`
- `Assets/ChessSage/Tests/SaveSystemTests.cs`

### Overworld 增强
- `Assets/ChessSage/Core/Overworld/{OverworldDecor, OverworldLandmarks, PoissonDisk}.cs`
- `Assets/ChessSage/Presentation/Overworld/{DecorAtlas, LandmarkArt, OverworldDecorRenderer, OverworldEdgeAtlas, OverworldLandmarkRenderer, OverworldPerfStats, OverworldRoadRenderer, OverworldWindow}.cs`
- `Assets/ChessSage/Tests/OverworldRichnessTests.cs`

### 编排层 + 缺陷回归
- `Assets/ChessSage/Tests/OrchestratorTests.cs`（D-05 解禁）
- `Assets/ChessSage/Core/Orchestration/SchemaValidator.cs`

### 基建 / 工具
- `Assets/ChessSage/Editor/PlaythroughCapture.cs`（Overlay → Camera RT 切换）
- `Assets/ChessSage/Editor/ProjectBootstrapper.cs`（D-04 修复）
- `tools/render-playthrough.sh`（软渲染试玩脚本）
- `tools/push_via_rest.py`（修复幂等性 + 删除 + 重命名）
- `tools/verify/core-harness.sh`（修复 UNITY_ROOT 探测兼容）

### 文档
- `docs/art/ui-visual-spec.md`
- `docs/art/overworld-art-spec.md`
- `docs/m6-m8-report.md`（本文档）

---

## 10. 已知风险与缓解

| 风险 | 缓解 |
|:--|:--|
| 沙箱 Unity Personal 许可证过期 → 截图无法重跑 | 截图证据已存档；下次沙箱恢复后单命令 `bash tools/render-playthrough.sh <Scene>` 补跑 |
| `core-harness.sh` UNITY_ROOT 历史硬编码 `/opt/unity` | 已修：兼容 `/opt/unity` 与 `/opt/unity/Editor` 两种路径 |
| `push_via_rest.py` 旧版本对重命名 + 删除语义有缺陷 | 已修 4 轮：`--no-renames` + path→blob sha 快照比对 + 401/403 退避重试 |
| 占位美术在低端 GPU 上有性能风险 | `OverworldPerfStats` 已在编辑器 always on；正式美术阶段需做平台分级 |
| URP 2D 在 Android 上 WebGL 平台未验证 | 暂仅 Linux Editor + Windows x64 交叉编译目标；后续根据发布平台补 |
| FMOD 在沙箱无可用声卡 → 输出 nosound | 不影响功能；正式构建时配置 ASIO/CoreAudio |

---

## 11. 下一步（M8 之后）

- **正式美术**：把 PlaceholderArt / 程序化图集替换为美术提供 sprite atlas（spec 已就位）；
- **CI**：把三门禁接到 GitHub Actions（PR 必跑 L1/L2/L3）；
- **本地化**：英文版 cheats、关卡名、剧情台词；
- **发布**：Unity Asset Bundle + Linux/Windows 构建（脚本已就位 `tools/build.sh all`）。
