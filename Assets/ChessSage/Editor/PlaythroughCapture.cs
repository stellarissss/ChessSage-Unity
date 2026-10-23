#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ChessSage.EditorTools
{
    /// <summary>
    /// 玩家视角试玩工具：在 batchmode 下进入播放模式，执行一段"脚本化玩家操作"，
    /// 每个关键节点截屏保存 PNG。
    ///
    /// 用途：无显示器的 CI / 远程沙箱里验证"游戏真的能玩"，产出可肉眼审查的画面证据。
    /// 读取环境变量 CHESSSAGE_SCENE 决定试玩哪个场景（Main / Overworld），
    /// 输出到 CHESSSAGE_SHOTS（默认 Logs/shots）。
    /// </summary>
    public static class PlaythroughCapture
    {
        [MenuItem("ChessSage/试玩录制/象棋垂直切片", priority = 100)]
        public static void CaptureChessMenu() => RunInEditor("Main");

        [MenuItem("ChessSage/试玩录制/六道大陆大地图", priority = 101)]
        public static void CaptureOverworldMenu() => RunInEditor("Overworld");

        [MenuItem("ChessSage/试玩录制/五子棋落子对局", priority = 102)]
        public static void CapturePlacementMenu() => RunInEditor("Placement");

        [MenuItem("ChessSage/试玩录制/作弊改规面板（离线预置）", priority = 103)]
        public static void CaptureCheatMenu() => RunInEditor("Cheat");

        [MenuItem("ChessSage/试玩录制/作弊面板（在线推演三态：处理/顶格/到点）", priority = 105)]
        public static void CaptureCheatDemoMenu() => RunInEditor("CheatDemo");

        [MenuItem("ChessSage/试玩录制/六道 RPG（选关/技能树/剧情/结局）", priority = 104)]
        public static void CaptureSamsaraMenu() => RunInEditor("Samsara");

        static void RunInEditor(string scene)
        {
            Environment.SetEnvironmentVariable("CHESSSAGE_SCENE", scene);
            // Editor 下通过 StartBatchPlaythrough 启动协程宿主
            var runner = new GameObject("PlaythroughRunner");
            var host = runner.AddComponent<PlaythroughRunner>();
            host.Begin();
        }

    /// <summary>播放模式钩子：进入播放模式时挂载试玩协程宿主。</summary>
    [InitializeOnLoad]
    internal static class PlayModeHook
    {
        static PlayModeHook()
        {
            EditorApplication.playModeStateChanged += st =>
            {
                if (st != PlayModeStateChange.EnteredPlayMode) return;
                if (UnityEngine.Object.FindObjectOfType<PlaythroughRunner>() != null) return;
                var go = new GameObject("PlaythroughRunner");
                go.AddComponent<PlaythroughRunner>().Begin();
                Debug.Log("[试玩] 播放模式已进入，试玩脚本已挂载");
            };
        }
    }

    /// <summary>batchmode 入口：-executeMethod ChessSage.EditorTools.PlaythroughCapture.BatchRun</summary>
    public static void BatchRun()
    {
        var scene = Environment.GetEnvironmentVariable("CHESSSAGE_SCENE") ?? "Main";
        Debug.Log($"[试玩] 启动播放模式试玩：{scene}");

        // 进播放模式前先自愈渲染管线（幂等）：修复历史遗留的 URP 空壳资产。
        // 否则 CreatePipeline() 抛 NRE、相机无内容、截图纯色（D-04）。资产健康时此调用为 no-op。
        ProjectBootstrapper.EnsureRenderPipeline();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var scenePath = scene == "Overworld"
            ? ProjectBootstrapper.OverworldScenePath
            : ProjectBootstrapper.ScenePath;

        EditorApplication.delayCall += () =>
        {
            var open = EditorSceneManager.OpenScene(scenePath);
            if (!open.IsValid())
            {
                Debug.LogError($"[试玩] 场景打开失败：{scenePath}");
                EditorApplication.Exit(2);
                return;
            }
            // 进入播放模式；runner 由 PlayModeHook 在 EnteredPlayMode 时挂载
            EditorApplication.isPlaying = true;
        };
    }
    }

    /// <summary>播放模式协程宿主：驱动试玩脚本并截屏。</summary>
    public sealed class PlaythroughRunner : MonoBehaviour
    {
        bool _started;

        public void Begin() => _started = true;

        void Update()
        {
            if (!_started) return;
            _started = false;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            var scene = Environment.GetEnvironmentVariable("CHESSSAGE_SCENE") ?? "Main";
            var outDir = Environment.GetEnvironmentVariable("CHESSSAGE_SHOTS")
                         ?? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs", "shots");
            Directory.CreateDirectory(outDir);
            Debug.Log($"[试玩] 开始：{scene} → {outDir}");

            // 给场景几帧时间完成 Awake。
            // 注意：不能等 WaitForEndOfFrame —— batchmode 下没有真实帧提交，
            // 该 yield 永远不会返回（协程会无声卡死，L3 实测确认）。
            for (int f = 0; f < 30; f++) yield return null;
            if (Environment.GetEnvironmentVariable("CHESSSAGE_DIAG") == "1") DumpSceneDiagnostics("boot");
            yield return ShotAsync(Path.Combine(outDir, "01_boot.png"), $"启动：{scene}");

            if (scene == "Main") yield return PlayChess(outDir);
            else if (scene == "Overworld") yield return PlayOverworld(outDir);
            else if (scene == "Placement") yield return PlayPlacement(outDir);
            else if (scene == "Cheat") yield return PlayCheatPanel(outDir);
            else if (scene == "CheatDemo") yield return PlayCheatDemo(outDir);
            else if (scene == "Samsara") yield return PlaySamsara(outDir);
            else yield return PlayOverworld(outDir);

            Debug.Log("[试玩] 完成，正常退出");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// 纯色截图排障：把当前场景的关键渲染要素打到日志（相机 / 渲染管线 / 精灵聚合）。
        /// 只在出图异常时读，正常试玩日志里就是几行。
        /// </summary>
        void DumpSceneDiagnostics(string phase)
        {
            var cam = Camera.main;
            var camInfo = cam == null
                ? "无主相机"
                : $"ortho={cam.orthographic} size={cam.orthographicSize} pos={cam.transform.position} " +
                  $"clear={cam.clearFlags} bg=({cam.backgroundColor.r:F2},{cam.backgroundColor.g:F2},{cam.backgroundColor.b:F2}) " +
                  $"cull=0x{cam.cullingMask:X8} enabled={cam.enabled} active={cam.gameObject.activeInHierarchy}";
            Debug.Log($"[DIAG:{phase}] camera: {camInfo}");
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            Debug.Log($"[DIAG:{phase}] currentRenderPipeline={(pipeline == null ? "null(Built-in)" : pipeline.GetType().Name)}");

            var srs = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
            Debug.Log($"[DIAG:{phase}] SpriteRenderers={srs.Length}");
            var agg = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var sr in srs)
            {
                var sp = sr.sprite;
                var key = $"sprite={(sp != null ? sp.name : "null")} " +
                          $"color=({sr.color.r:F2},{sr.color.g:F2},{sr.color.b:F2},{sr.color.a:F2}) " +
                          $"shader={(sr.sharedMaterial != null ? sr.sharedMaterial.shader.name : "null")} " +
                          $"tex={(sp != null && sp.texture != null ? sp.texture.width + "x" + sp.texture.height : "-")} " +
                          $"order={sr.sortingOrder} active={sr.gameObject.activeInHierarchy}";
                agg[key] = agg.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            foreach (var kv in agg) Debug.Log($"[DIAG:{phase}]   x{kv.Value}  {kv.Key}");

            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            Debug.Log($"[DIAG:{phase}] Canvases={canvases.Length}");

            // 占位美术纹理的实际像素（判断"颜色丢失"发生在纹理生成端还是渲染端）
            try
            {
                var cell = ChessSage.Presentation.Art.PlaceholderArt.Cell;
                var ct = cell.texture;
                var cp = ct.GetPixels32();
                Debug.Log($"[DIAG:{phase}] ph_cell tex {ct.width}x{ct.height} fmt={ct.format} gfx={ct.graphicsFormat} mid={cp[cp.Length / 2]} corner={cp[0]}");

                var ps = ChessSage.Presentation.Art.PlaceholderArt.Piece("red", "soldier");
                var pt = ps.texture;
                var pp = pt.GetPixels32();
                Debug.Log($"[DIAG:{phase}] ph_red_soldier tex {pt.width}x{pt.height} mid={pp[pp.Length / 2]} corner={pp[0]}");

                var sr = UnityEngine.Object.FindObjectOfType<SpriteRenderer>();
                if (sr != null && sr.sharedMaterial != null)
                {
                    var mt = sr.sharedMaterial.mainTexture;
                    Debug.Log($"[DIAG:{phase}] sample sr mat={sr.sharedMaterial.name} shader={sr.sharedMaterial.shader.name} " +
                              $"mainTex={(mt != null ? mt.name + " " + mt.width + "x" + mt.height : "null")} color={sr.color} " +
                              $"rendererData.shader={sr.sharedMaterial.shader.isSupported}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DIAG:{phase}] 纹理自检失败：{e.Message}");
            }
        }

        // ── 象棋试玩脚本 ──────────────────────────────────────────────────────

        IEnumerator PlayChess(string outDir)
        {
            var screen = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Game.GameScreen>();
            if (screen == null) { Fail("未找到 GameScreen"); yield break; }
            var board = screen.Board;
            var session = screen.Session;

            yield return Step(0.3f);

            // 玩家走一步：选中一子 → 落子
            var piece = FindMoveable(session);
            if (piece == null) { Fail("无可走棋子"); yield break; }
            var moves = session.GetValidMoves(piece.Id);
            var target = moves[0];

            yield return ShotAsync(Path.Combine(outDir, "02_chess_start.png"),
                 $"象棋开局：红先，选中 {piece.Name} 前");

            // 模拟点击：直接调用 GameScreen 的公开输入路径（不 hack 状态）
            ClickCell(board, piece.X, piece.Y);
            yield return Step(0.4f);
            yield return ShotAsync(Path.Combine(outDir, "03_chess_selected.png"),
                 $"选中 {piece.Name}，高亮 {moves.Count} 个可走点");

            ClickCell(board, target[0], target[1]);
            yield return Step(1.2f);
            yield return ShotAsync(Path.Combine(outDir, "04_chess_ai_moved.png"),
                 $"玩家走子完成，AI 已应手，当前回合：{session.CurrentTurn}");

            // 再走一步
            var piece2 = FindMoveable(session);
            if (piece2 != null)
            {
                var moves2 = session.GetValidMoves(piece2.Id);
                ClickCell(board, piece2.X, piece2.Y);
                yield return Step(0.3f);
                ClickCell(board, moves2[0][0], moves2[0][1]);
                yield return Step(1.2f);
            }
            yield return ShotAsync(Path.Combine(outDir, "05_chess_second_move.png"),
                 "第二回合完成");

            Debug.Log($"[试玩] 象棋流程走通：回合={session.CurrentTurn}，历史={session.State.MoveHistory.Count} 步");
        }

        static ChessSage.Core.Model.Piece FindMoveable(ChessSage.Core.Game.GameSession s)
        {
            foreach (var p in s.State.Pieces)
                if (p.IsAlive && p.Side == s.PlayerSide && s.GetValidMoves(p.Id).Count > 0) return p;
            return null;
        }

        /// <summary>模拟玩家点击：把格坐标转成屏幕坐标后调用输入路由。</summary>
        static void ClickCell(ChessSage.Presentation.Board.BoardView board, int x, int y)
        {
            var router = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Board.BoardInputRouter>();
            if (router == null)
            {
                Debug.LogError("[试玩] 未找到 BoardInputRouter，点击被跳过");
                return;
            }
            // L3 实测教训：反射 event 的 backing field（private）不可靠，
            // 曾导致整个试玩"点击全部无效"（回合恒 red、历史恒 0）。
            // 改用显式公开 API，走与真实鼠标完全相同的下游路径。
            router.SimulateClick(x, y);
        }

        // ── 落子类（五子棋）试玩脚本 ──────────────────────────────────────────

        /// <summary>
        /// 落子类试玩：先进入棋种 hub（验证运行时切换与返回入口），再选五子棋进入对局，
        /// 连续落两手并等待 AI 应手，产出可肉眼审查的画面证据。
        /// </summary>
        IEnumerator PlayPlacement(string outDir)
        {
            var screen = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Game.GameScreen>();
            if (screen == null) { Fail("未找到 GameScreen"); yield break; }

            var boot = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Bootstrap>();
            if (boot == null) { Fail("未找到 Bootstrap"); yield break; }

            // ① 进入 hub → 截图（验证 UGUI 选择界面在真实运行时不崩）
            boot.EnterHub();
            yield return Step(0.5f);
            yield return ShotAsync(Path.Combine(outDir, "09_hub.png"), "棋种选择 hub");

            // ② 选五子棋进入对局（验证运行时重建 GameSession + 重绑棋盘布局）
            boot.LaunchVariant("wuziqi");
            yield return Step(0.8f);
            var session = screen.Session;
            if (session == null || !session.IsPlacementGame) { Fail("未进入落子类对局"); yield break; }

            var board = screen.Board;
            var placements = session.GetValidPlacements();
            if (placements.Count == 0) { Fail("五子棋开局无合法落点"); yield break; }
            Debug.Log($"[试玩] 五子棋开局：玩家={session.PlayerSide}，合法落点 {placements.Count} 个");

            // ③ 玩家落中心附近一手 → AI 应手
            var first = Nearest(placements, session.Rules.Width / 2, session.Rules.Height / 2);
            ClickCell(board, first[0], first[1]);
            yield return Step(1.4f);
            yield return ShotAsync(Path.Combine(outDir, "10_wuziqi_place.png"),
                $"五子棋玩家落子 ({first[0]},{first[1]})，AI 应手后回合 {session.CurrentTurn}");

            // ④ 再落一手，确认对局可持续
            var after = session.GetValidPlacements();
            if (after.Count > 0)
            {
                var second = Nearest(after, session.Rules.Width / 2, session.Rules.Height / 2);
                ClickCell(board, second[0], second[1]);
                yield return Step(1.4f);
            }
            yield return ShotAsync(Path.Combine(outDir, "11_wuziqi_second.png"), "五子棋第二手完成");

            Debug.Log($"[试玩] 五子棋流程走通：回合={session.CurrentTurn}，历史={session.State.MoveHistory.Count} 步");
        }

        /// <summary>取离给定格坐标最近的合法落点（用于落在棋盘中心，观感更直观）。</summary>
        static int[] Nearest(System.Collections.Generic.List<int[]> cells, int cx, int cy)
        {
            var best = cells[0];
            long bestD = long.MaxValue;
            foreach (var c in cells)
            {
                long dx = c[0] - cx, dy = c[1] - cy;
                long d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        // ── 作弊改规面板试玩脚本（T3b：USP 取证）────────────────────────────

        /// <summary>
        /// 作弊改规面板试玩（核心 USP 取证）：
        ///   进入象棋对局 → 打开面板 → 选一条**离线预置**指令 → 展开预览 diff → 确认施展。
        ///
        /// 全程用<b>公开 API</b> 驱动（<c>SetOffline / Open / SelectPreset / SimulateConfirm</c>），
        /// 不反射任何私有字段——与 L3 试玩「点击全失效」的历史教训保持一致。
        /// 无 API Key 时走 <c>CheatPresetLibrary</c> 预置链路，完全绕开 LLM（离线降级，UX §10）。
        /// </summary>
        IEnumerator PlayCheatPanel(string outDir)
        {
            var screen = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Game.GameScreen>();
            if (screen == null) { Fail("未找到 GameScreen"); yield break; }
            var boot = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Bootstrap>();
            if (boot == null) { Fail("未找到 Bootstrap"); yield break; }

            var panel = boot.CheatPanel;
            var hud = boot.CheatHudView;
            if (panel == null) { Fail("未找到 CheatCommandPanel（面板未接入组合根？）"); yield break; }
            if (hud == null) { Fail("未找到 CheatHud"); yield break; }

            yield return Step(0.4f);

            // ① 强制离线（无 API key 的降级路径）并展开面板 → 截图应同时看到面板 UI 与顶栏 HUD
            panel.SetOffline(true);
            panel.Open();
            yield return Step(0.4f);
            Debug.Log($"[试玩] 作弊面板已展开：IsOpen={panel.IsOpen}，离线={boot.Cheat?.Offline}，" +
                      $"业力={boot.Cheat?.Karma}/{boot.Cheat?.KarmaMax}");
            yield return ShotAsync(Path.Combine(outDir, "13_cheat_open.png"),
                "作弊面板展开：输入框 + 示例 chips + 离线预置区，顶栏业力/识破 HUD 常驻");

            // ② 选一条预置指令（离线降级路径，不需 API key）→ 展开预览 diff
            var variant = boot.ActiveVariant ?? "xiangqi";
            var presets = ChessSage.Core.Cheat.CheatPresetLibrary.ForVariant(variant);
            if (presets.Count == 0) { Fail($"棋种 {variant} 无可用预置指令"); yield break; }
            var preset = presets[0];   // 象棋默认 =「让我的马可以横着走」（旗舰预置）
            var karmaBefore = boot.Cheat?.Karma ?? 0;
            Debug.Log($"[试玩] 选中预置：{preset.Text}（分类 {preset.Classification}，价目 {preset.Karma}）");
            panel.SelectPreset(preset);
            yield return Step(0.4f);
            yield return ShotAsync(Path.Combine(outDir, "14_cheat_preview.png"),
                $"改规预览（尚未生效）：{preset.Text} 的人读 diff 卡片 + 代价条");

            // ③ 确认施展 → 配置生效、业力 HUD 增长（离线预置价目 40，安全档不溢出）
            panel.SimulateConfirm();
            yield return Step(0.4f);
            var karmaAfter = boot.Cheat?.Karma ?? 0;
            yield return ShotAsync(Path.Combine(outDir, "15_cheat_applied.png"),
                $"确认施展后：改规生效，业力 {karmaBefore} → {karmaAfter}，识别 HUD 已刷新");

            Debug.Log($"[试玩] 作弊面板流程走通：预置「{preset.Text}」，业力 {karmaBefore} → {karmaAfter}" +
                      $"，可撤回改规={boot.Cheat?.HasRuleUndo}");
        }

        /// <summary>
        /// 在线推演取证（E3-S4 / 任务 C 的 Editor-only demo 钩子）：
        /// 配合本机 LLM 桩（<c>tools/verify/llm-stub.py</c>）驱动**真实** Agent 链路，
        /// 截取「处理态 / 顶格警示 / 20s 到点」三副面孔。
        ///
        /// 【为什么需要它】离线预置链路（<see cref="PlayCheatPanel"/>）不经过 AgentEngine，
        /// 因此 E2-S4 新增的三个状态（轨迹+计数器+收手 / 顶格警示 / 到点收尾）
        /// 在没有 API key 的环境里**根本不会出现**，无法目视验收。
        ///
        /// 【不碰生产行为】仅在 Editor 程序集内、且仅当 CHESSSAGE_SCENE=CheatDemo 时执行；
        /// 运行期代码零改动——面板只被公开 API 驱动
        /// （SetOffline / Open / SimulateOpenAndSubmit / 内部 RequestStop）。
        ///
        /// 环境变量：
        ///   CHESSSAGE_CHEAT_SCENARIO = cap | timeout_partial | timeout_fail（与桩服务同名场景）
        ///   CHESSSAGE_CHEAT_COMMAND  = 提交的自然语言指令
        ///   DEEPSEEK_BASE_URL        = http://127.0.0.1:8931（指向桩）
        ///   DEEPSEEK_API_KEY         = 任意非空串（让 IsAvailable=true，桩不校验）
        /// </summary>
        IEnumerator PlayCheatDemo(string outDir)
        {
            var boot = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Bootstrap>();
            if (boot == null) { Fail("未找到 Bootstrap"); yield break; }
            var panel = boot.CheatPanel;
            var cheat = boot.Cheat;
            if (panel == null || cheat == null) { Fail("作弊面板/控制器未接线"); yield break; }

            var scenario = Environment.GetEnvironmentVariable("CHESSSAGE_CHEAT_SCENARIO") ?? "cap";
            var command = Environment.GetEnvironmentVariable("CHESSSAGE_CHEAT_COMMAND") ?? "让我的马可以横着走";
            var baseUrl = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? "(默认线上)";

            yield return Step(0.4f);
            panel.SetOffline(false);      // 仅在 DEEPSEEK_API_KEY 非空时才真正在线
            cheat.Reconnect();
            panel.Open();
            yield return Step(0.4f);

            Debug.Log($"[取证] 场景={scenario} 指令「{command}」 base_url={baseUrl} 离线={cheat.Offline}");
            yield return ShotAsync(Path.Combine(outDir, "23_cheat_online_idle.png"),
                $"面板就绪（在线通道，{scenario}）");

            if (cheat.Offline)
            {
                Debug.LogWarning("[取证] 仍为离线（DEEPSEEK_API_KEY 为空）——处理态/顶格/到点不可达，请检查环境变量");
                yield break;
            }

            panel.SimulateOpenAndSubmit(command);

            // ① 处理态：让计数器先动起来（第 1 轮 usage 入账后）
            float t = 0f;
            while (t < 5f) { t += Time.unscaledDeltaTime; yield return null; if (t >= 1.0f) break; }
            yield return ShotAsync(Path.Combine(outDir, "24_cheat_processing.png"),
                "处理态：天道推演中 —— token 计数器 + 轨迹行 + 收手按钮");

            // ② 顶格警示：cap 场景第 1 轮即触顶（提示态，循环不拦截）
            if (scenario == "cap")
            {
                t = 0f;
                while (t < 8f) { t += Time.unscaledDeltaTime; yield return null; if (t >= 2.0f) break; }
                yield return ShotAsync(Path.Combine(outDir, "25_cheat_cap_warning.png"),
                    "顶格警示：投射 ≥ 单次上限 120 —— 计数器转警示色 + 警示行（循环继续，不拒绝）");
            }

            // ③ 收尾面孔：等本次提交结束（LastAgentResult 由 CheatController 在收尾时写入）
            t = 0f;
            while (t < 45f && cheat.LastAgentResult == null) { t += Time.unscaledDeltaTime; yield return null; }
            yield return Step(0.5f);

            var agent = cheat.LastAgentResult;
            var label = agent == null
                ? "未拿到 AgentResult（提交仍未收尾）"
                : $"type={agent.Type} rounds={agent.Rounds} karma={agent.EstimatedKarmaCost} " +
                  $"timedOut={agent.TimedOut} cap={agent.CapWarned}";
            Debug.Log($"[取证] 收尾面孔：{label}");
            yield return ShotAsync(Path.Combine(outDir, $"26_cheat_{scenario}_result.png"), $"收尾面孔：{label}");
        }

        // ── 大地图试玩脚本 ────────────────────────────────────────────────────

        IEnumerator PlayOverworld(string outDir)
        {
            var boot = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Overworld.OverworldBootstrap>();
            if (boot == null) { Fail("未找到 OverworldBootstrap"); yield break; }

            yield return Step(0.5f);
            yield return ShotAsync(Path.Combine(outDir, "06_world_spawn.png"),
                 $"六道大陆：出生点 ({boot.Player.CellX},{boot.Player.CellY})");

            // 向东北走几步（模拟玩家操作：直接调 TryStep 公开 API）
            var dirs = new (int dx, int dy)[]
            {
                (1, 0), (1, 0), (0, 1), (1, 0), (0, 1), (1, 0), (0, -1), (1, 0),
            };
            int i = 0;
            foreach (var (dx, dy) in dirs)
            {
                boot.Player.TryStep(dx, dy);
                // 等待走完该格
                float t = 0;
                while (boot.Player.IsMoving && t < 2f) { t += Time.unscaledDeltaTime; yield return null; }
                if (i == 3) yield return ShotAsync(Path.Combine(outDir, "07_world_walk.png"),
                                 $"行走中：({boot.Player.CellX},{boot.Player.CellY})，块加载 {boot.Chunks.LoadedChunkCount}");
                i++;
            }
            yield return ShotAsync(Path.Combine(outDir, "08_world_after.png"),
                 $"移动后：({boot.Player.CellX},{boot.Player.CellY})，" +
                 $"Renderer {boot.Chunks.ActiveRendererCount}，探索度 {boot.Fog.State.ExploredRatio() * 100:F1}%");

            // 诊断：确认相机确实跟随玩家（历史缺陷：相机写错物体，导致玩家飘出画面 + 视野窗口错位）。
            var cam = Camera.main;
            var pw = boot.Player.WorldPosition;
            var cp = cam != null ? cam.transform.position : Vector3.zero;
            Debug.Log($"[OW-CAM] playerWorld=({pw.x:F2},{pw.y:F2}) camPos=({cp.x:F2},{cp.y:F2}) " +
                      $"delta=({pw.x - cp.x:F2},{pw.y - cp.y:F2}) ortho={(cam != null ? cam.orthographicSize : -1)} " +
                      $"cell=({boot.Player.CellX},{boot.Player.CellY})");
            var playerSprites = boot.Player.GetComponentsInChildren<SpriteRenderer>();
            foreach (var sr in playerSprites)
                Debug.Log($"[OW-CAM] playerSprite name={sr.name} sprite={(sr.sprite != null ? sr.sprite.name : "null")} " +
                          $"size={(sr.sprite != null ? sr.sprite.bounds.size.x.ToString("F2") + "x" + sr.sprite.bounds.size.y.ToString("F2") : "-")} " +
                          $"order={sr.sortingOrder} active={sr.gameObject.activeInHierarchy} color={sr.color} " +
                          $"worldBounds={(sr.sprite != null ? sr.bounds.size.x.ToString("F2") + "x" + sr.bounds.size.y.ToString("F2") : "-")} " +
                          $"center={(sr.sprite != null ? sr.bounds.center.ToString() : "-")}");

            // 把程序化生成的玩家立绘导出成 PNG，便于肉眼核对（避免"尺寸/朝向猜错"）。
            // 注意：写到 <b>截图目录之外</b>——截图判据会扫描目录下所有 PNG，
            // 立绘多为透明像素、会被误判为"纯色截图"而让整体校验失败。
            try
            {
                var dumpDir = Directory.GetParent(outDir)?.FullName ?? outDir;
                foreach (var sr in playerSprites)
                    if (sr.sprite != null && sr.sprite.texture != null && sr.name == "player_body")
                        File.WriteAllBytes(Path.Combine(dumpDir, "player_sprite_dump.png"),
                            sr.sprite.texture.EncodeToPNG());
            }
            catch (Exception e) { Debug.LogWarning("[试玩] 导出立绘失败：" + e.Message); }

            Debug.Log($"[试玩] 大地图流程走通：最终位置 ({boot.Player.CellX},{boot.Player.CellY})");
        }

        // ── 六道 RPG 沉浸界面试玩脚本（T5：选关 / 技能树 / 剧情 / 结局）────────

        /// <summary>
        /// RPG 表现层试玩：选关 → 技能树 → 剧情对话（含分支选择）→ 结局演出（两种基调）→ 记忆相册。
        ///
        /// 全程用<b>公开 API</b> 驱动（<c>Bootstrap.Rpg</c> / <c>Bootstrap.Samsara</c> /
        /// <c>StoryPanel.Next/Choose</c>），不反射任何私有字段——与「点击全失效」的历史教训一致。
        /// 先 <c>EnterHub()</c> 分离对局，避免 legacy <c>GameFlowHud</c> 的 IMGUI 盖在演出层之上。
        /// </summary>
        IEnumerator PlaySamsara(string outDir)
        {
            var boot = UnityEngine.Object.FindObjectOfType<ChessSage.Presentation.Bootstrap>();
            if (boot == null) { Fail("未找到 Bootstrap"); yield break; }
            var rpg = boot.Rpg;
            if (rpg == null) { Fail("未找到 SamsaraRpg（RPG 层未接入组合根？）"); yield break; }

            boot.EnterHub();   // 分离对局：清空会话、隐藏棋盘，RPG 覆盖层独占屏幕
            yield return Step(0.5f);

            // ① 选关界面（六道 + 关卡目标/回合/难度）
            rpg.OpenLevelSelect();
            yield return Step(0.5f);
            Debug.Log($"[试玩] 选关界面已打开：IsOpen={rpg.IsOpen}");
            yield return ShotAsync(Path.Combine(outDir, "16_rpg_levels.png"),
                "RPG 选关：左列六道、右列关卡（名称/目标/回合上限/难度/状态）");

            // ② 技能树：给足技能点并预解锁若干，让「已解锁 / 可解锁 / 锁定」三态同屏可见
            boot.Samsara.State.AddSkillPoint(6);
            boot.Samsara.UnlockSkill("stealth_t2a", 2);       // 前置 stealth_t1 默认已解锁
            boot.Samsara.UnlockSkill("karma_capacity_t1", 1); // 解锁后其第 2 层转为「可解锁」
            rpg.ShowSkills();
            yield return Step(0.5f);
            var skills = boot.Samsara.State.GetFullState()["skills"] as Newtonsoft.Json.Linq.JObject;
            Debug.Log($"[试玩] 技能树：剩余技能点={boot.Samsara.GetSkillPoints()}，已解锁技能数={(skills == null ? 0 : skills.Count)}");
            yield return ShotAsync(Path.Combine(outDir, "17_rpg_skills.png"),
                "RPG 技能树：4 分支 × 3 层，三态配色 + 顶部业力折扣汇总");

            // ③ 剧情对话（地狱道第 3 关含分支选择）
            rpg.ShowStory("hell", 2);
            yield return Step(0.4f);
            rpg.StoryAdvanceToChoice();   // 一路推进到抉择
            yield return Step(0.4f);
            yield return ShotAsync(Path.Combine(outDir, "18_rpg_story.png"),
                "RPG 剧情：道中人对白推进后出现的「面对愧疚」分支选项");

            // 选择第 1 项 → 展示属性结算与后续对白
            rpg.StoryChoose(0);
            yield return Step(0.3f);
            yield return ShotAsync(Path.Combine(outDir, "19_rpg_story_choice.png"),
                "RPG 剧情：做出选择后的属性变化与后续对白");

            // ④ 结局演出（真我）：暖金基调
            rpg.Close();
            rpg.ShowEnding("true_me");
            yield return Step(0.5f);
            yield return ShotAsync(Path.Combine(outDir, "20_rpg_ending_true.png"),
                "RPG 结局：真我结局（暖金基调 + 对白 + 尾声）");

            // ⑤ 结局演出（识破）：墨红基调，验证「按结局类型切换视觉基调」
            rpg.Close();
            rpg.ShowEnding("exposed");
            yield return Step(0.5f);
            yield return ShotAsync(Path.Combine(outDir, "21_rpg_ending_exposed.png"),
                "RPG 结局：识破结局（墨红基调，与真我结局基调不同）");

            // ⑥（P1）记忆相册
            rpg.Close();
            rpg.ShowMemory();
            yield return Step(0.4f);
            yield return ShotAsync(Path.Combine(outDir, "22_rpg_memory.png"),
                "RPG 记忆相册：六道记忆碎片（已解锁显示旁白 / 未解锁显示条件）");

            Debug.Log("[试玩] RPG 沉浸界面流程走通（选关 / 技能树 / 剧情 / 结局 / 记忆）");
        }

        // ── 基础设施 ──────────────────────────────────────────────────────────

        /// <summary>等待若干物理帧 + 秒数。不使用 WaitForEndOfFrame（batchmode 永不返回）。</summary>
        IEnumerator Step(float seconds)
        {
            float t = 0f;
            while (t < seconds) { t += Time.unscaledDeltaTime; yield return null; }
            yield return null;
        }

        /// <summary>
        /// 截屏：优先抓取**完整帧缓冲**（含 Overlay UI），失败时回退到相机 Offscreen 渲染。
        ///
        /// 【为什么需要两条路径】
        /// 本项目全部 UI（VariantHub / CheatCommandPanel / CheatHud）都是
        /// <c>RenderMode.ScreenSpaceOverlay</c>。Unity 的渲染顺序是「所有相机 → Overlay Canvas」，
        /// Overlay 直接合成到**显示设备**，**不经过任何相机的 targetTexture**。
        /// 因此只抓 `cam.targetTexture` 时，**棋盘/大地图能出图，但 UI 永远是缺失的**——
        /// 实测：hub 界面截出来是纯色，一度被误判为"UI 没渲染"。
        ///
        /// 抓整屏用 <see cref="ScreenCapture.CaptureScreenshotAsTexture"/>：它读的是
        /// 后缓冲（backbuffer），含 Overlay UI。历史上它在本环境失败，是因为当时用了
        /// `-nographics`（无显示设备，后缓冲不存在）；现在经 Xvfb + Mesa llvmpipe
        /// 提供了真实显示，后缓冲可读。
        ///
        /// 环境变量 CHESSSAGE_CAPTURE 可强制指定：`screen`（只抓整屏）| `rt`（只抓相机）。
        /// 默认 `auto`：先整屏，拿不到有效像素再回退相机 RT。
        /// </summary>
        IEnumerator ShotAsync(string path, string label)
        {
            // 等一帧让场景状态稳定
            yield return null;

            const int W = 1920, H = 1080;
            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning($"[试玩] 截屏失败（{label}）：无主相机");
                yield break;
            }

            var mode = Environment.GetEnvironmentVariable("CHESSSAGE_CAPTURE") ?? "auto";

            // ── 路径 A：整屏（含 Overlay UI） ──────────────────────────────
            if (mode == "auto" || mode == "screen")
            {
                // 必须确保相机不指向 RT，否则后缓冲为空。
                if (cam.targetTexture != null) cam.targetTexture = null;
                for (int f = 0; f < 3; f++) yield return null;

                Texture2D shot = null;
                try { shot = ScreenCapture.CaptureScreenshotAsTexture(); }
                catch (Exception e) { Debug.LogWarning($"[试玩] 整屏捕获异常（{label}）：{e.Message}"); }

                if (shot != null)
                {
                    // 有效像素判据：不能被压成单色（部分驱动会返回 1x1 占位图）。
                    bool usable = shot.width >= 64 && shot.height >= 64 && HasVariation(shot);
                    if (usable || mode == "screen")
                    {
                        File.WriteAllBytes(path, shot.EncodeToPNG());
                        Debug.Log($"[试玩] 截屏（整屏·含UI）：{label} → {path} [{shot.width}x{shot.height}]");
                        Destroy(shot);
                        yield break;
                    }
                    Debug.LogWarning($"[试玩] 整屏捕获疑似无效（{shot.width}x{shot.height}，" +
                                     $"唯一色不足），回退相机 RT：{label}");
                    Destroy(shot);
                }
                else
                {
                    Debug.LogWarning($"[试玩] 整屏捕获返回 null，回退相机 RT：{label}");
                }
            }

            // ── 路径 B：相机 Offscreen RT ──────────────────────────────────
            // 难点：本项目 UI 是 RenderMode.ScreenSpaceOverlay，Unity 的渲染顺序是
            // 「所有相机 → Overlay Canvas」，Overlay 直接合成到显示设备、**不经过相机**，
            // 因此直接渲染相机 targetTexture 时 UI 恒缺失（实测 hub 截图纯色）。
            //
            // 对策（仅取证期生效，不改产品代码）：捕获瞬间把所有 Overlay Canvas
            // 临时切成 ScreenSpaceCamera 并指向主相机，让它经相机渲染进 RT，
            // 截完立刻还原。这样一帧里同时拿到「世界 + UI」，与玩家所见一致。
            var overlays = new List<(Canvas canvas, RenderMode mode, Camera worldCam, float plane)>();
            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (cv.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                overlays.Add((cv, cv.renderMode, cv.worldCamera, cv.planeDistance));
                cv.renderMode = RenderMode.ScreenSpaceCamera;
                cv.worldCamera = cam;
                // 放在近平面前方且远离世界几何，避免被场景挡到。
                cv.planeDistance = cam.nearClipPlane + 1f;
            }
            if (overlays.Count > 0)
            {
                Canvas.ForceUpdateCanvases();
                for (int f = 0; f < 2; f++) yield return null;
            }

            var rt = new RenderTexture(W, H, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            for (int f = 0; f < 3; f++) yield return null;

            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;

            cam.targetTexture = prev;

            // 还原 Overlay 渲染模式（务必还原：否则会永久改变运行期 UI 行为）
            foreach (var (canvas, savedMode, savedCam, savedPlane) in overlays)
            {
                if (canvas == null) continue;
                canvas.renderMode = savedMode;
                canvas.worldCamera = savedCam;
                canvas.planeDistance = savedPlane;
            }
            if (overlays.Count > 0) Canvas.ForceUpdateCanvases();

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            Destroy(rt);
            Debug.Log($"[试玩] 截屏（相机RT{((overlays.Count > 0) ? "·含UI" : "·不含UI")}）：{label} → {path}" +
                      (overlays.Count > 0 ? $"（{overlays.Count} 个 Overlay Canvas 已临时改道）" : ""));
            LogRenderStats(label);
        }

        /// <summary>
        /// 记录真实渲染统计（Editor 侧才有权威数字）。
        /// 任务把性能列为硬指标，而 <c>ProfilerRecorder("Draw Calls Count")</c> 在播放模式下时常拿不到值，
        /// 因此这里用 <see cref="UnityStats"/> 在每次截屏时刻打一行，作为可核对的性能证据。
        /// </summary>
        static void LogRenderStats(string label)
        {
            try
            {
                Debug.Log($"[OW-PERF-ED] {label} drawCalls={UnityStats.drawCalls} batches={UnityStats.batches} " +
                          $"tris={UnityStats.triangles}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[OW-PERF-ED] 无法读取 UnityStats：{e.Message}");
            }
        }

        /// <summary>抽样检测纹理是否有颜色变化（用于识别"被压成单色"的无效截图）。</summary>
        static bool HasVariation(Texture2D tex)
        {
            int stepX = Mathf.Max(1, tex.width / 64);
            int stepY = Mathf.Max(1, tex.height / 64);
            Color first = tex.GetPixel(0, 0);
            for (int y = 0; y < tex.height; y += stepY)
                for (int x = 0; x < tex.width; x += stepX)
                {
                    var c = tex.GetPixel(x, y);
                    if (Mathf.Abs(c.r - first.r) + Mathf.Abs(c.g - first.g) + Mathf.Abs(c.b - first.b) > 0.02f)
                        return true;
                }
            return false;
        }

        static void Fail(string msg)
        {
            Debug.LogError("[试玩] " + msg);
            EditorApplication.Exit(3);
        }
    }
}
#endif
