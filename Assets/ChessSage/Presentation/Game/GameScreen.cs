using System.Collections;
using System.Collections.Generic;
using ChessSage.Core.Game;
using ChessSage.Presentation.Board;
using UnityEngine;

namespace ChessSage.Presentation.Game
{
    /// <summary>
    /// 对局画面：把 <see cref="GameSession"/>（纯逻辑）与棋盘视图（表现）粘合起来，
    /// 形成"点棋子 → 点目标 → 走子 → AI 应手"的最小可玩闭环。
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
        Coroutine _aiRoutine;

        public GameSession Session => _session;

        /// <summary>注入对局会话。由组合根（<c>Bootstrap</c>）在启动时调用。</summary>
        public void Attach(GameSession session)
        {
            _session = session;
            _selectedPieceId = null;
            _selectedMoves.Clear();

            if (Board != null) Board.Bind(session.State, session.Rules.Width, session.Rules.Height);
            SubscribeInput();
            Refresh();
        }

        // ── 输入接线 ──────────────────────────────────────────────────────────

        bool _inputSubscribed;

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

        // ── 输入处理 ──────────────────────────────────────────────────────────

        void OnCellClicked(int x, int y)
        {
            Debug.Log($"[对局] 点击 ({x},{y})：回合={_session?.CurrentTurn ?? "无会话"}");
            if (_session == null || _session.IsEnded) return;
            if (_session.IsAiTurn) { SetHint("AI 正在思考…"); return; }

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

        void OnCancel() => ClearSelection();

        void ClearSelection()
        {
            _selectedPieceId = null;
            _selectedMoves.Clear();
            if (Highlights != null) Highlights.Clear();
            SetHint(null);
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

        // ── AI 回合 ───────────────────────────────────────────────────────────

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
                if (!r.Success) break;
                Refresh();
                if (MinAiThinkTime > 0f && guard < 16) yield return new WaitForSeconds(MinAiThinkTime);
            }

            _aiRoutine = null;
            if (_session != null && !_session.IsEnded) SetHint("轮到你走棋");
        }

        // ── 刷新 ──────────────────────────────────────────────────────────────

        void Refresh()
        {
            if (_session == null) return;
            if (Board != null) Board.MarkDirty();
            if (Hud != null) Hud.Bind(_session);
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

        /// <summary>悔棋（U 键或 HUD 按钮）。回到玩家回合后自动重排 AI 回合。</summary>
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