using System.Collections;
using System.Collections.Generic;
using ChessSage.Core.Board;
using ChessSage.Core.Game;
using ChessSage.Presentation.Board;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ChessSage.Presentation.Game
{
    /// <summary>
    /// 对局画面：把 <see cref="GameSession"/>（纯逻辑）与棋盘视图（表现）粘合起来，
    /// 形成可玩闭环。按棋种类型分两条交互分支，共用一个回合/终局/悔棋骨架：
    ///   · <b>走子类</b>（象棋/动物棋/跳棋）：点棋子 → 点目标 → AI 应手；
    ///   · <b>落子类</b>（围棋/五子棋/黑白棋）：点击格 → 若为合法落点则落子 → AI 应手，
    ///     玩家回合常驻显示全部合法落点高亮，落子后在 HUD 提示提子/翻转反馈。
    ///
    /// 严格单向数据流：<b>输入 → GameSession → 刷新视图</b>。
    /// 表现层从不修改 BoardState，所有变更都必须经过 GameSession 的方法，
    /// 因此"UI 改坏棋局"这类缺陷在结构上不可能发生。
    /// </summary>
    public sealed class GameScreen : MonoBehaviour
    {
        [Header("场景引用")]
        public BoardView Board;
        public HighlightLayer Highlights;
        public BoardInputRouter Input;
        public GameFlowHud Hud;

        [Header("节奏")]
        [Tooltip("AI 思考的最小可见时长（秒），避免瞬发造成 AI 瞬间出手的廉价感。")]
        [Min(0f)] public float MinAiThinkTime = 0.35f;

        GameSession _session;
        string _selectedPieceId;
        List<int[]> _selectedMoves = new List<int[]>();
        List<int[]> _placementCache = new List<int[]>();
        Coroutine _aiRoutine;

        public GameSession Session => _session;

        /// <summary>注入对局会话。由组合根（<c>Bootstrap</c>）在启动 / 切换棋种时调用。</summary>
        public void Attach(GameSession session)
        {
            _session = session;
            _selectedPieceId = null;
            _selectedMoves.Clear();
            _placementCache.Clear();

            if (Board != null)
            {
                Board.gameObject.SetActive(true);
                if (session != null)
                {
                    // 按棋种配置选择布局：矩形 / 六角星由 BoardLayoutFactory 数据驱动判定，
                    // 视图不再假设矩形——跳棋的六角星格位由此正确排布。
                    var layout = BoardLayoutFactory.FromBoardConfig(session.Config.Board);
                    Board.Bind(session.State, layout);
                }
                else
                {
                    Board.Bind(null, 9, 10);
                }
            }
            SubscribeInput();
            Refresh();
        }

        /// <summary>
        /// 分离当前会话（返回棋种 hub 时调用）：停掉 AI 协程、清空选择态与高亮、隐藏棋盘。
        /// 之所以必须真正置空会话而不是只隐藏视图：<see cref="BoardInputRouter"/> 用的是原始鼠标事件，
        /// 不经过 EventSystem，若不置空会话，玩家在 hub 界面上的点击会穿透到棋盘误触发落子。
        /// </summary>
        public void Detach()
        {
            if (_aiRoutine != null) { StopCoroutine(_aiRoutine); _aiRoutine = null; }
            _session = null;
            _selectedPieceId = null;
            _selectedMoves.Clear();
            _placementCache.Clear();
            if (Highlights != null) Highlights.Clear();
            if (Board != null) Board.gameObject.SetActive(false);
            if (Hud != null) Hud.Bind(null);
        }

        // ── 输入接线 ──────────────────────────────────────────────────────────

        bool _inputSubscribed;

        /// <summary>
        /// 临时屏蔽棋盘<b>走子</b>输入。作弊面板需要玩家在棋盘上「点选一个目标格」取址时置 true，
        /// 避免该次点击被当成走子/落子误触。
        ///
        /// 【为何是「早退」而非「退订」】屏蔽期间<b>不解除</b> <see cref="SubscribeInput"/> 建立的
        /// 订阅（那个订阅一旦缺失，表现就是 L3 曾实测抓到的「整个游戏点不动」）。这里只在
        /// <see cref="OnCellClicked"/> 入口早退，<see cref="BoardInputRouter"/> 的事件仍照常派发给
        /// 其他订阅者（如作弊面板的取址监听），语义最小、互不干扰。
        /// </summary>
        public bool BoardInputSuppressed { get; set; }

        /// <summary>
        /// 订阅输入事件。<b>不能放在 OnEnable</b>：组合根在同一帧先 AddComponent 再
        /// 注入依赖，OnEnable 执行时 <see cref="Input"/> 还是 null，订阅会被静默跳过——
        /// 表现为"整个游戏点不动"。L3 试玩实测抓到此缺陷，订阅移到注入完成后的
        /// <see cref="Attach"/>，并做幂等保护。
        /// </summary>
        void SubscribeInput()
        {
            if (_inputSubscribed || Input == null) return;
            Input.CellClicked += OnCellClicked;
            Input.SelectionCancelled += OnCancel;
            _inputSubscribed = true;
        }

        void UnsubscribeInput()
        {
            if (!_inputSubscribed || Input == null) return;
            Input.CellClicked -= OnCellClicked;
            Input.SelectionCancelled -= OnCancel;
            _inputSubscribed = false;
        }

        void OnDisable()
        {
            UnsubscribeInput();
            if (_aiRoutine != null) { StopCoroutine(_aiRoutine); _aiRoutine = null; }
        }

        // ── 输入分发 ──────────────────────────────────────────────────────────

        void OnCellClicked(int x, int y)
        {
            Debug.Log($"[对局] 点击 ({x},{y})：回合={_session?.CurrentTurn ?? "无会话"}");
            if (BoardInputSuppressed) return;   // 作弊面板取址期间：放行事件给面板，本处不处理走子
            if (_session == null || _session.IsEnded) return;
            if (_session.IsAiTurn) { SetHint("AI 正在思考…"); return; }

            if (_session.IsPlacementGame) PlacementBranch(x, y);
            else MoveBranch(x, y);
        }

        void OnCancel() => ClearSelection();

        // ══════════════════════════════════════════════════════════════
        // 走子类分支（行为与 M5 之前保持一致）
        // ══════════════════════════════════════════════════════════════

        void MoveBranch(int x, int y)
        {
            // 已选中棋子 → 先判定是否为合法落点
            if (!string.IsNullOrEmpty(_selectedPieceId))
            {
                var move = FindMove(x, y);
                if (move != null) { CommitMove(move[0], move[1]); return; }
            }

            // 否则尝试选中本方棋子
            var piece = _session.State.GetPieceAt(x, y);
            if (piece == null || !piece.IsAlive || piece.Side != _session.CurrentTurn)
            {
                ClearSelection();
                return;
            }
            if (piece.Side != _session.PlayerSide)
            {
                SetHint("这是对方的棋子");
                return;
            }

            _selectedPieceId = piece.Id;
            _selectedMoves = _session.GetValidMoves(piece.Id);
            ShowHighlights();
            SetHint($"已选中 {piece.Name}（{_selectedMoves.Count} 个可走点）");
        }

        int[] FindMove(int x, int y)
        {
            for (int i = 0; i < _selectedMoves.Count; i++)
                if (_selectedMoves[i][0] == x && _selectedMoves[i][1] == y) return _selectedMoves[i];
            return null;
        }

        /// <summary>把当前选中的可走点投影到世界坐标，交给标记层渲染（可吃子点用红色）。</summary>
        void ShowHighlights()
        {
            if (Highlights == null || Board == null) return;
            if (_selectedMoves.Count == 0) { Highlights.Clear(); return; }

            var moves = new List<Vector3>(_selectedMoves.Count);
            var capture = new List<bool>(_selectedMoves.Count);
            if (_session != null)
            {
                var st = _session.State;
                for (int i = 0; i < _selectedMoves.Count; i++)
                {
                    var t = st.GetPieceAt(_selectedMoves[i][0], _selectedMoves[i][1]);
                    capture.Add(t != null && t.IsAlive);
                }
            }

            // 标记层是世界空间：直接给世界坐标，棋盘移动/缩放时标记自动跟随。
            for (int i = 0; i < _selectedMoves.Count; i++)
                moves.Add(Board.transform.TransformPoint(Board.CellToLocal(_selectedMoves[i][0], _selectedMoves[i][1])));

            Highlights.SetMoves(moves, capture);
        }

        void CommitMove(int toX, int toY)
        {
            var id = _selectedPieceId;
            ClearSelection();

            var result = _session.MakePlayerMove(id, toX, toY);
            Debug.Log($"[对局] 走子 {id}→({toX},{toY}) 成功={result.Success} {result.Message} 历史={_session.State.MoveHistory.Count}");
            if (!result.Success)
            {
                SetHint(result.Message);
                return;
            }

            Refresh();
            if (result.Ended) { OnGameEnd(result.Winner); return; }
            ScheduleAiTurn();
        }

        // ══════════════════════════════════════════════════════════════
        // 落子类分支（围棋/五子棋/黑白棋）
        // ══════════════════════════════════════════════════════════════

        void PlacementBranch(int x, int y)
        {
            if (!IsLegalPlacement(x, y))
            {
                SetHint($"({x},{y}) 不是合法落点");
                return;
            }
            CommitPlacement(x, y);
        }

        /// <summary>点击格是否在缓存的合法落点集合中（缓存由 <see cref="Refresh"/> 维护，点击时无需重算）。</summary>
        bool IsLegalPlacement(int x, int y)
        {
            for (int i = 0; i < _placementCache.Count; i++)
                if (_placementCache[i][0] == x && _placementCache[i][1] == y) return true;
            return false;
        }

        void CommitPlacement(int x, int y)
        {
            var result = _session.MakePlayerPlace(x, y);
            Debug.Log($"[对局] 落子 ({x},{y}) 成功={result.Success} {result.Message} 提子={result.Captured.Count} 历史={_session.State.MoveHistory.Count}");
            if (!result.Success)
            {
                SetHint(result.Message);
                return;
            }

            SetHint(DescribePlacementFeedback(result));
            Refresh();
            if (result.Ended) { OnGameEnd(result.Winner); return; }
            ScheduleAiTurn();
        }

        /// <summary>落子反馈文案：提子（围棋）与翻转（黑白棋）都要在 HUD 明确体现。</summary>
        string DescribePlacementFeedback(MoveResult result)
        {
            var parts = new List<string> { $"{SideLabel(_session.PlayerSide)}落子成功" };
            if (result.Captured.Count > 0) parts.Add($"提子 {result.Captured.Count} 枚");

            int flipped = LastMoveFlippedCount();
            if (flipped > 0) parts.Add($"翻转 {flipped} 枚");

            return string.Join("，", parts);
        }

        /// <summary>最近一手被翻转的棋子数（黑白棋记在 move_history 末条的 flipped 数组里）。</summary>
        int LastMoveFlippedCount()
        {
            var hist = _session.State.MoveHistory;
            if (hist.Count == 0) return 0;
            var last = hist[hist.Count - 1] as JObject;
            return last?["flipped"] is JArray flipped ? flipped.Count : 0;
        }

        /// <summary>
        /// 玩家回合常驻显示全部合法落点。落子类落点可能很多（围棋开局 361 个），
        /// 因此这里只在状态变更（<see cref="Refresh"/>）时重算一次，不进入每帧路径；
        /// 高亮层本身对象池 + 脏标记驱动，重建不产生逐帧分配。
        /// </summary>
        void UpdatePlacementHighlights()
        {
            if (Highlights == null || Board == null) return;

            if (_session == null || _session.IsEnded || _session.IsAiTurn)
            {
                _placementCache.Clear();
                Highlights.Clear();
                return;
            }

            _placementCache = _session.GetValidPlacements();
            if (_placementCache.Count == 0) { Highlights.Clear(); return; }

            var points = new List<Vector3>(_placementCache.Count);
            for (int i = 0; i < _placementCache.Count; i++)
                points.Add(Board.transform.TransformPoint(Board.CellToLocal(_placementCache[i][0], _placementCache[i][1])));

            // 落子类无"可吃子"语义，capture 传 null → 统一用可走点色（绿）。
            Highlights.SetMoves(points, null);
        }

        // ══════════════════════════════════════════════════════════════
        // 共用骨架：AI 回合 / 刷新 / 终局 / 悔棋（两条分支复用，不复制粘贴）
        // ══════════════════════════════════════════════════════════════

        void ScheduleAiTurn()
        {
            if (_aiRoutine != null) StopCoroutine(_aiRoutine);
            _aiRoutine = StartCoroutine(AiTurnRoutine());
        }

        IEnumerator AiTurnRoutine()
        {
            // 给 AI 一个最短可见时长，观感更从容，也让"机制被 AI 接管"有铺垫。
            if (MinAiThinkTime > 0f) yield return new WaitForSeconds(MinAiThinkTime);

            // 机制可能让连续数回合由 AI 掌握，逐回合推进直到轮到玩家或终局。
            int guard = 0;
            while (_session != null && !_session.IsEnded && _session.IsAiTurn && guard++ < 16)
            {
                var r = _session.MakeAiMove();
                Debug.Log(r.Placed != null
                    ? $"[对局] AI 落子 ({r.Placed[0]},{r.Placed[1]}) 成功={r.Success} {r.Message}"
                    : $"[对局] AI 走子 成功={r.Success} {r.Message}");
                if (!r.Success) break;
                Refresh();
                if (MinAiThinkTime > 0f && guard < 16) yield return new WaitForSeconds(MinAiThinkTime);
            }

            _aiRoutine = null;
            if (_session != null && !_session.IsEnded)
                SetHint(_session.IsPlacementGame ? "轮到你落子" : "轮到你走棋");
        }

        void Refresh()
        {
            if (_session == null) return;
            if (Board != null) Board.MarkDirty();
            if (Hud != null) Hud.Bind(_session);
            if (_session.IsPlacementGame) UpdatePlacementHighlights();
        }

        /// <summary>
        /// 只重绘棋盘视图，<b>不改变回合、选择态与终局态</b>。
        ///
        /// 用途：作弊面板「确认施展 / 撤回AI修改」改的是棋子走法与形态，不改「轮到谁」，
        /// 因此无需走完整 <see cref="Refresh"/>（它会 rebind HUD 并重算落子高亮，可能抹掉玩家
        /// 当前的选择高亮）。改规后仅需让棋盘按最新配置重绘即可，故单独开一个最小入口，
        /// 把「改规重绘」与「回合推进重绘」两种语义区分开。
        /// </summary>
        public void RefreshBoardOnly()
        {
            if (Board != null) Board.MarkDirty();
        }

        void ClearSelection()
        {
            _selectedPieceId = null;
            _selectedMoves.Clear();
            if (Highlights != null) Highlights.Clear();
            SetHint(null);
        }

        void OnGameEnd(string winner)
        {
            var label = string.IsNullOrEmpty(winner) ? "和局" : SideLabel(winner);
            SetHint($"对局结束：{label} 胜");
        }

        static string SideLabel(string side)
            => side == "red" ? "红方" : side == "black" ? "黑方" : side == "white" ? "白方" : side;

        void SetHint(string text)
        {
            if (Hud != null) Hud.SetHint(text);
        }

        /// <summary>悔棋（U 键或 HUD 按钮）。两条分支共用：回到玩家回合后自动重排 AI 回合。</summary>
        public void Undo()
        {
            if (_session == null || _session.IsAiTurn) return;
            if (!_session.UndoLastMove())
            {
                SetHint("没有可悔的棋");
                return;
            }
            ClearSelection();
            Refresh();
            if (_session.IsAiTurn) ScheduleAiTurn();
        }
    }
}
