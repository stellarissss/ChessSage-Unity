#if UNITY_EDITOR
using System;
using System.Collections;
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
            yield return ShotAsync(Path.Combine(outDir, "01_boot.png"), $"启动：{scene}");

            if (scene == "Main") yield return PlayChess(outDir);
            else if (scene == "Overworld") yield return PlayOverworld(outDir);
            else if (scene == "Placement") yield return PlayPlacement(outDir);
            else yield return PlayOverworld(outDir);

            Debug.Log("[试玩] 完成，正常退出");
            EditorApplication.Exit(0);
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

            Debug.Log($"[试玩] 大地图流程走通：最终位置 ({boot.Player.CellX},{boot.Player.CellY})");
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
        /// 截屏：直接把主相机渲染进 RenderTexture 后读像素存 PNG。
        ///
        /// 不用 ScreenCapture.CaptureScreenshot —— L3 实测它在 batchmode/软件渲染下
        /// 静默不落盘（日志说成功但文件不存在）。手动 Render 的方式确定性强。
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

            // 把相机指向 RT 后交给**正常帧循环**渲染 3 帧。
            // 实测踩过的坑：
            //   · cam.Render() 绕开 SRP → 全黑；
            //   · backbuffer ReadPixels → 灰屏（batchmode 无真实呈现）；
            //   · ScreenCapture.CaptureScreenshot → 日志成功但文件不落盘。
            // 相机 targetTexture 是 URP 官方支持的离屏渲染路径，帧循环会正常出图。
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
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            Destroy(rt);
            Debug.Log($"[试玩] 截屏：{label} → {path}");
        }

        static void Fail(string msg)
        {
            Debug.LogError("[试玩] " + msg);
            EditorApplication.Exit(3);
        }
    }
}
#endif
