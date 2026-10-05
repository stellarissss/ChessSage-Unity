using System;
using System.Collections.Generic;
using ChessSage.Core.Ai;
using ChessSage.Core.Config;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Util;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Game
{
    public sealed class MoveResult
    {
        public bool Success;
        public string Message;
        public bool AiControlled;
        public bool NotPlayerControlled;
        public bool Ended;
        public string Winner;
        public readonly List<string> Captured = new List<string>();
        public List<string> Mechanisms = new List<string>();
    }

    public sealed class AiMoveResult
    {
        public bool Success;
        public string Message;
        public bool IsRandom;
        public bool Passed;
        public ChessAi.Move Move;
        public int[] Placed;
        public List<string> Mechanisms = new List<string>();
    }

    /// <summary>
    /// 对局会话：把配置、规则引擎、机制引擎、AI 引擎组合成完整的回合流程。
    /// 通过 <see cref="RuleEngineFactory"/> 支持全部棋类；机制/AI 性格沿用象棋语义，
    /// 移动/落子/胜负判定全部下放到统一的 <see cref="VariantRuleBase"/>。
    /// </summary>
    public sealed class GameSession
    {
        const int MaxUndoDepth = 64;

        public GameConfig Config { get; private set; }
        public VariantRuleBase Rules { get; private set; }
        public MechanismEngine Mechanisms { get; private set; }
        public ChessAi Ai { get; private set; }
        public BoardState State { get; private set; }

        /// <summary>玩家默认控制方（象棋为红方，黑白棋类为黑方）。</summary>
        public string PlayerSide { get; private set; }

        public bool IsPlacementGame => Rules.IsPlacementGame;

        readonly List<BoardState> _undoStack = new List<BoardState>();
        readonly DeterministicRng _rng;

        public GameSession(GameConfig config, MechanismEngine mechanismEngine, int aiSeed = 0)
            : this(config, mechanismEngine, null, aiSeed) { }

        public GameSession(GameConfig config, MechanismEngine mechanismEngine, ChessAi ai)
            : this(config, mechanismEngine, ai, 0) { }

        GameSession(GameConfig config, MechanismEngine mechanismEngine, ChessAi ai, int aiSeed)
        {
            Config = config;
            Rules = RuleEngineFactory.Create(config);
            Mechanisms = mechanismEngine ?? new MechanismEngine(config.Rules);
            _rng = new DeterministicRng(aiSeed == 0 ? Environment.TickCount : aiSeed);

            State = BoardState.FromJson(config.BoardState);
            if (State.Board == null) State.Board = Config.Board;

            PlayerSide = Rules.DefaultPlayerSide;
            ConfigureSides();

            Ai = ai ?? new ChessAi(Rules, config.Rules,
                config.Rules["ai_difficulty"]?["current"]?.Value<string>() ?? "medium", aiSeed);
            Ai.RegisterCustomPieces(CustomPieces(config));
            Mechanisms.ApplyPersonalityToAi(Ai);
        }

        /// <summary>把阵营标签同步到机制引擎，使 HUD / 机制摘要显示正确的中文方名。</summary>
        void ConfigureSides()
        {
            Mechanisms.DefaultPlayerSide = PlayerSide;
            foreach (var side in Rules.Sides)
                if (!Mechanisms.SideLabels.ContainsKey(side))
                    Mechanisms.SideLabels[side] = SideLabelFor(side);
        }

        static string SideLabelFor(string side)
            => side == "red" ? "红方" : side == "white" ? "白方" : side == "black" ? "黑方" : side;

        static IEnumerable<JObject> CustomPieces(GameConfig config)
        {
            if (config.PiecesA?["custom_pieces"] is JArray a)
                foreach (var t in a) if (t is JObject o) yield return o;
            if (config.PiecesB?["custom_pieces"] is JArray b)
                foreach (var t in b) if (t is JObject o) yield return o;
        }

        public bool IsEnded => State.GameStatus["state"]?.Value<string>() == "ended";
        public string CurrentTurn => State.CurrentTurn;

        /// <summary>当前回合是否应由 AI 走棋。</summary>
        public bool IsAiTurn
        {
            get
            {
                if (IsEnded) return false;
                if (Mechanisms.IsAiControlled(State, CurrentTurn)) return true;
                return !Mechanisms.IsPlayerControlled(State, CurrentTurn);
            }
        }

        public List<int[]> GetValidMoves(string pieceId)
        {
            var piece = State.GetPieceById(pieceId);
            if (piece == null || !piece.IsAlive) return new List<int[]>();
            return Rules.GetValidMoves(piece, State);
        }

        /// <summary>当前回合方的全部合法落点（落子类棋种）。</summary>
        public List<int[]> GetValidPlacements() => Rules.GetValidPlacements(State, CurrentTurn);

        // ══════════════════════════════════════════════════════════════
        // 玩家动作
        // ══════════════════════════════════════════════════════════════

        public MoveResult MakePlayerMove(string pieceId, int toX, int toY)
        {
            var r = new MoveResult();
            if (IsEnded) { r.Message = "游戏已结束"; return r; }

            string currentTurn = State.CurrentTurn;
            if (!CheckPlayerTurn(currentTurn, r)) return r;

            var piece = State.GetPieceById(pieceId);
            if (piece == null || !piece.IsAlive) { r.Message = "棋子不存在"; return r; }
            if (piece.Side != currentTurn) { r.Message = "不是该方回合"; return r; }

            PushUndo();
            int fromX = piece.X, fromY = piece.Y;
            var outcome = Rules.ApplyAction(State, new GameAction("move", pieceId, toX, toY), currentTurn);
            if (!outcome.Ok)
            {
                PopUndo();
                r.Message = outcome.Message;
                return r;
            }

            RecordMove(pieceId, fromX, fromY, toX, toY, outcome, "move");
            return FinishPlayerAction(currentTurn, r, outcome);
        }

        public MoveResult MakePlayerPlace(int x, int y)
        {
            var r = new MoveResult();
            if (IsEnded) { r.Message = "游戏已结束"; return r; }

            string currentTurn = State.CurrentTurn;
            if (!CheckPlayerTurn(currentTurn, r)) return r;

            PushUndo();
            var outcome = Rules.ApplyAction(State, new GameAction("place", null, x, y), currentTurn);
            if (!outcome.Ok)
            {
                PopUndo();
                r.Message = outcome.Message;
                return r;
            }

            RecordMove(null, -1, -1, x, y, outcome, "place");
            return FinishPlayerAction(currentTurn, r, outcome);
        }

        bool CheckPlayerTurn(string currentTurn, MoveResult r)
        {
            if (Mechanisms.IsAiControlled(State, currentTurn))
            {
                r.Message = "本回合由AI接管中，请等待AI走棋";
                r.AiControlled = true;
                return false;
            }
            if (!Mechanisms.IsPlayerControlled(State, currentTurn))
            {
                r.Message = "当前方不由玩家控制";
                r.NotPlayerControlled = true;
                return false;
            }
            return true;
        }

        MoveResult FinishPlayerAction(string currentTurn, MoveResult r, ActionOutcome outcome)
        {
            r.Captured.AddRange(outcome.CapturedIds);
            FinalizeTurn(currentTurn);
            r.Success = true;
            r.Message = outcome.Message;
            r.Ended = IsEnded;
            r.Winner = State.GameStatus["winner"]?.Value<string>();
            r.Mechanisms = Mechanisms.GetActiveMechanismsSummary(State);
            return r;
        }

        // ══════════════════════════════════════════════════════════════
        // AI 动作
        // ══════════════════════════════════════════════════════════════

        public AiMoveResult MakeAiMove()
        {
            var r = new AiMoveResult();
            string currentTurn = State.CurrentTurn;
            if (IsEnded) { r.Message = "游戏已结束"; return r; }
            if (!IsAiTurn) { r.Message = "不是AI回合"; return r; }

            return Rules.IsPlacementGame ? MakeAiPlace(currentTurn, r) : MakeAiMoveInternal(currentTurn, r);
        }

        AiMoveResult MakeAiMoveInternal(string currentTurn, AiMoveResult r)
        {
            ChessAi.Move move;
            if (Mechanisms.IsRandomMoveRequired(State, currentTurn))
            {
                r.IsRandom = true;
                var moves = Ai.GenerateAllMoves(State, currentTurn);
                move = moves.Count == 0 ? null : moves[_rng.Next(moves.Count)];
            }
            else
            {
                move = Ai.GetBestMove(State);
            }

            if (move == null)
            {
                SetEnded(Rules.Opposite(currentTurn), "stalemate", false);
                r.Success = true;
                r.Message = $"AI无棋可走，{SideLabelFor(Rules.Opposite(currentTurn))}获胜";
                return r;
            }

            var piece = State.GetPieceById(move.PieceId);
            if (piece == null) { r.Message = "AI移动异常"; return r; }

            PushUndo();
            var outcome = Rules.ApplyAction(State, new GameAction("move", move.PieceId, move.ToX, move.ToY), currentTurn);
            if (!outcome.Ok) { PopUndo(); r.Message = "AI移动异常"; return r; }

            RecordMove(move.PieceId, move.FromX, move.FromY, move.ToX, move.ToY, outcome, "move");
            FinalizeTurn(currentTurn);

            r.Success = true;
            r.Move = move;
            r.Message = "AI走棋成功";
            r.Mechanisms = Mechanisms.GetActiveMechanismsSummary(State);
            return r;
        }

        AiMoveResult MakeAiPlace(string currentTurn, AiMoveResult r)
        {
            var placements = Rules.GetValidPlacements(State, currentTurn);
            if (placements.Count == 0)
            {
                PushUndo();
                r.Passed = true;
                r.Success = true;
                r.Message = "AI无子可落，过手";
                AdvanceTurn(currentTurn);
                ResolveOutcomeOrAutoPass();
                r.Mechanisms = Mechanisms.GetActiveMechanismsSummary(State);
                return r;
            }

            var choice = PlacementAi.Choose(Rules, State, currentTurn, placements, _rng);
            PushUndo();
            var outcome = Rules.ApplyAction(State, new GameAction("place", null, choice[0], choice[1]), currentTurn);
            if (!outcome.Ok) { PopUndo(); r.Message = "AI落子异常"; return r; }

            RecordMove(null, -1, -1, choice[0], choice[1], outcome, "place");
            FinalizeTurn(currentTurn);

            r.Success = true;
            r.Placed = choice;
            r.Message = "AI落子成功";
            r.Mechanisms = Mechanisms.GetActiveMechanismsSummary(State);
            return r;
        }

        // ══════════════════════════════════════════════════════════════
        // 回合收尾（胜负判定 + 机制 + 换手 + 跳过 + 将死）
        // ══════════════════════════════════════════════════════════════

        void FinalizeTurn(string currentTurn)
        {
            var outcome = Rules.CheckOutcome(State);
            if (outcome.Ended) { SetEnded(outcome.Winner, outcome.Condition, outcome.Draw); return; }

            var postInfo = Mechanisms.ApplyPostMoveMechanisms(State, currentTurn);
            if (!postInfo.SwitchTurn) return;

            AdvanceTurn(currentTurn);

            if (Rules.SupportsCheckRules && Rules.IsCheckmate(State.CurrentTurn, State))
                SetEnded(Rules.Opposite(State.CurrentTurn), "checkmate", false);

            if (!IsEnded && Rules.IsPlacementGame) ResolveOutcomeOrAutoPass();
        }

        void AdvanceTurn(string fromTurn)
        {
            State.CurrentTurn = Rules.Opposite(fromTurn);

            int skipCount = 0;
            while (Mechanisms.ShouldSkipTurn(State, State.CurrentTurn))
            {
                Mechanisms.ApplyPreTurnMechanisms(State, State.CurrentTurn);
                skipCount++;
                if (skipCount > 4) break;
                State.CurrentTurn = Rules.Opposite(State.CurrentTurn);
            }
        }

        /// <summary>落子类：双方都无合法落子时结算，单方无子则由对方继续。</summary>
        void ResolveOutcomeOrAutoPass()
        {
            var outcome = Rules.CheckOutcome(State);
            if (outcome.Ended) { SetEnded(outcome.Winner, outcome.Condition, outcome.Draw); return; }

            int guard = 0;
            while (Rules.GetValidPlacements(State, State.CurrentTurn).Count == 0 && !IsEnded && guard++ < 4)
                State.CurrentTurn = Rules.Opposite(State.CurrentTurn);

            outcome = Rules.CheckOutcome(State);
            if (outcome.Ended) SetEnded(outcome.Winner, outcome.Condition, outcome.Draw);
        }

        void SetEnded(string winner, string condition, bool draw)
        {
            State.GameStatus = new JObject
            {
                ["state"] = "ended",
                ["winner"] = winner == null ? JValue.CreateNull() : new JValue(winner),
                ["win_condition"] = condition,
                ["draw"] = draw,
                ["custom_rules_active"] = State.GameStatus["custom_rules_active"]?.DeepClone() ?? new JArray(),
            };
        }

        void RecordMove(string pieceId, int fromX, int fromY, int toX, int toY, ActionOutcome outcome, string kind)
        {
            State.MoveHistory.Add(new JObject
            {
                ["piece_id"] = pieceId == null ? JValue.CreateNull() : new JValue(pieceId),
                ["from"] = fromX < 0 ? JValue.CreateNull() : new JArray(fromX, fromY),
                ["to"] = new JArray(toX, toY),
                ["kind"] = kind,
                ["captured"] = outcome.CapturedIds.Count > 0 ? new JValue(outcome.CapturedIds[0]) : JValue.CreateNull(),
                ["flipped"] = new JArray(outcome.FlippedIds),
            });
        }

        // ══════════════════════════════════════════════════════════════
        // 悔棋（整盘快照，兼容吃子 / 翻转 / 过手等副作用）
        // ══════════════════════════════════════════════════════════════

        void PushUndo()
        {
            _undoStack.Add(State.Clone());
            if (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
        }

        void PopUndo()
        {
            if (_undoStack.Count > 0) _undoStack.RemoveAt(_undoStack.Count - 1);
        }

        public bool CanUndo => _undoStack.Count > 0;

        public bool UndoLastMove()
        {
            if (_undoStack.Count == 0) return false;
            State = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            if (State.Board == null) State.Board = Config.Board;
            return true;
        }

        // ══════════════════════════════════════════════════════════════
        // 作弊：应用配置变更后重建引擎与棋盘
        // ══════════════════════════════════════════════════════════════

        public void ApplyConfigUpdate(JObject modifiedConfigs)
        {
            if (modifiedConfigs == null) return;

            if (modifiedConfigs["board"] is JObject board) Config.Board = board;
            if (modifiedConfigs["rules"] is JObject rules) Config.Rules = rules;
            if (modifiedConfigs["pieces_red"] is JObject pr) Config.PiecesA = pr;
            if (modifiedConfigs["pieces_black"] is JObject pb)
            {
                if (Config.IsRedBlack) Config.PiecesB = pb; else Config.PiecesA = pb;
            }
            if (modifiedConfigs["pieces_white"] is JObject pw) Config.PiecesB = pw;
            if (modifiedConfigs["board_state"] is JObject bs)
            {
                State = BoardState.FromJson(bs);
                if (State.Board == null) State.Board = Config.Board;
            }
            Rebuild();
        }

        public void Rebuild()
        {
            Rules = RuleEngineFactory.Create(Config);
            Mechanisms.UpdateRules(Config.Rules);
            var difficulty = Config.Rules["ai_difficulty"]?["current"]?.Value<string>() ?? "medium";
            Ai = new ChessAi(Rules, Config.Rules, difficulty);
            Ai.RegisterCustomPieces(CustomPieces(Config));
            Mechanisms.ApplyPersonalityToAi(Ai);
        }
    }
}