using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Json;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    public sealed class MechanismEngineTests
    {
        MechanismEngine _mech;
        BoardState _state;

        [SetUp]
        public void SetUp()
        {
            _mech = new MechanismEngine(TestEnv.LoadConfig("xiangqi", "rules"));
            _state = new BoardState();
            BoardState.EnsureMechanismKeys(_state.Mechanisms);
        }

        [Test]
        public void DefaultPlayerControlIsRed()
        {
            Assert.IsTrue(_mech.IsPlayerControlled(_state, "red"));
            Assert.IsFalse(_mech.IsPlayerControlled(_state, "black"));
        }

        [Test]
        public void SkipTurnDecrementsAndExpires()
        {
            _mech.AddMechanism(_state, "skip_turns", "black", 1, "冻结");
            Assert.IsTrue(_mech.ShouldSkipTurn(_state, "black"));

            var info = _mech.ApplyPreTurnMechanisms(_state, "black");
            Assert.IsTrue(info.Skipped);
            Assert.AreEqual("冻结", info.SkipReason);
            Assert.IsFalse(_mech.ShouldSkipTurn(_state, "black"), "remaining 用尽后应清除");
        }

        [Test]
        public void AiControlConsumedOnPostMove()
        {
            _mech.AddMechanism(_state, "ai_control", "red", 2, "接管");
            Assert.IsTrue(_mech.IsAiControlled(_state, "red"));
            _mech.ApplyPostMoveMechanisms(_state, "red");
            var arr = (JArray)_state.Mechanisms["ai_control"];
            Assert.AreEqual(1, arr.Count);
            Assert.AreEqual(1, arr[0]["remaining"].Value<int>());
        }

        [Test]
        public void ExtraTurnSuppressesSwitch()
        {
            _mech.AddMechanism(_state, "extra_turns", "red", 1, "额外");
            var info = _mech.ApplyPostMoveMechanisms(_state, "red");
            Assert.IsTrue(info.ExtraTurnGranted);
            Assert.IsFalse(info.SwitchTurn);
        }

        [Test]
        public void MoveLimitsSuppressSwitchUntilExhausted()
        {
            _mech.AddMechanism(_state, "move_limits", "red", -1, "多步");
            var limit = (JObject)((JArray)_state.Mechanisms["move_limits"])[0];
            limit["limit"] = 2;

            _mech.ApplyPreTurnMechanisms(_state, "red");
            Assert.AreEqual(2, limit["remaining_moves"].Value<int>());

            var first = _mech.ApplyPostMoveMechanisms(_state, "red");
            Assert.IsFalse(first.SwitchTurn);
            Assert.AreEqual(1, first.MovesRemaining);

            var second = _mech.ApplyPostMoveMechanisms(_state, "red");
            Assert.IsTrue(second.SwitchTurn);
        }

        [Test]
        public void SummaryContainsAllPrimitives()
        {
            _mech.AddMechanism(_state, "skip_turns", "black", -1, "冻结");
            _mech.AddMechanism(_state, "ai_control", "red", 2, "接管");
            _mech.AddMechanism(_state, "random_moves", "black", 3, "乱走");
            _mech.AddMechanism(_state, "extra_turns", "red", 1, "额外");
            _mech.AddMechanism(_state, "move_limits", "red", 0, "多步");
            ((JObject)((JArray)_state.Mechanisms["move_limits"])[0])["limit"] = 3;

            var summary = _mech.GetActiveMechanismsSummary(_state);
            Assert.AreEqual(5, summary.Count);
            Assert.IsTrue(summary.Exists(s => s.Contains("无限")));
            Assert.IsTrue(summary.Exists(s => s.Contains("每回合3步")));
        }
    }

    public sealed class GameSessionTests
    {
        GameSession NewSession()
        {
            var loader = new ConfigLoader(TestEnv.ConfigsRoot);
            var config = loader.LoadVariant("xiangqi");
            return new GameSession(config, new MechanismEngine(config.Rules), aiSeed: 12345);
        }

        [Test]
        public void InitialTurnIsRedHuman()
        {
            var s = NewSession();
            Assert.AreEqual("red", s.CurrentTurn);
            Assert.IsFalse(s.IsAiTurn);
        }

        [Test]
        public void PlayerMoveSwitchesToBlackAi()
        {
            var s = NewSession();
            var result = s.MakePlayerMove("r_soldier_1", 0, 5);
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual("black", s.CurrentTurn);
            Assert.IsTrue(s.IsAiTurn);
        }

        [Test]
        public void IllegalMoveRejected()
        {
            var s = NewSession();
            var result = s.MakePlayerMove("r_soldier_1", 0, 4);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("非法移动", result.Message);
        }

        [Test]
        public void AiProducesLegalMoveAndAlternates()
        {
            var s = NewSession();
            s.MakePlayerMove("r_soldier_1", 0, 5);
            var ai = s.MakeAiMove();
            Assert.IsTrue(ai.Success, ai.Message);
            Assert.IsNotNull(ai.Move);
            Assert.AreEqual("red", s.CurrentTurn);
        }

        [Test]
        public void UndoRestoresPieceAndTurn()
        {
            var s = NewSession();
            s.MakePlayerMove("r_soldier_1", 0, 5);
            Assert.IsTrue(s.UndoLastMove());
            Assert.AreEqual("red", s.CurrentTurn);
            var piece = s.State.GetPieceById("r_soldier_1");
            Assert.AreEqual(0, piece.X);
            Assert.AreEqual(6, piece.Y);
        }
    }

    public sealed class JsonSchemaTests
    {
        [Test]
        public void BoardStateConfigPassesItsSchema()
        {
            var schema = TestEnv.LoadJson(System.IO.Path.Combine(
                TestEnv.SchemasRoot, "board_state.schema.json"));
            var instance = TestEnv.LoadConfig("xiangqi", "board_state");
            var errors = JsonSchemaLite.Validate(schema, instance);
            Assert.IsEmpty(errors, string.Join("\n", errors));
        }

        [Test]
        public void InvalidTypeIsReported()
        {
            var schema = JObject.Parse("{\"type\":\"object\",\"required\":[\"a\"],\"properties\":{\"a\":{\"type\":\"integer\"}}}");
            var instance = JObject.Parse("{\"a\":\"text\"}");
            var errors = JsonSchemaLite.Validate(schema, instance);
            Assert.IsNotEmpty(errors);
        }
    }
}