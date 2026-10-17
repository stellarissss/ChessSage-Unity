using ChessSage.Core.Game;
using UnityEngine;

namespace ChessSage.Presentation.Game
{
    /// <summary>
    /// 对局 HUD：回合、提示、悔棋按钮。
    ///
    /// 刻意使用 Unity 内置 <c>OnGUI</c> 而非 UGUI 预制体：ProjectBootstrapper 能在
    /// 无人工干预的情况下生成场景，而 .prefab/.uxml 依赖编辑器序列化，脚本无法可靠手写。
    /// 待正式 UI 美术与本地化（Localization 包）就位后，可整体替换为 UGUI/UI Toolkit，
    /// 上层 <see cref="GameScreen"/> 不需要任何改动。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameFlowHud : MonoBehaviour
    {
        public KeyCode UndoKey = KeyCode.U;

        GameSession _session;
        string _hint;
        bool _hidden;

        public void Bind(GameSession session) => _session = session;

        public void SetHint(string text) => _hint = text;

        /// <summary>
        /// 临时隐藏 HUD。RPG 全屏界面（<c>SamsaraRpg</c>）打开时调用，避免本 HUD 的 legacy
        /// <c>OnGUI</c>（绘制在 UGUI 之上）盖住演出层；关闭界面时恢复为 true。
        /// </summary>
        public void SetVisible(bool visible) => _hidden = !visible;

        void Update()
        {
            if (Input.GetKeyDown(UndoKey) && _session != null)
            {
                var screen = GetComponent<GameScreen>();
                if (screen != null) screen.Undo();
            }
        }

        void OnGUI()
        {
            if (_hidden || _session == null) return;

            GUILayout.BeginArea(new Rect(16, 16, 320, 200), GUI.skin.box);
            GUILayout.Label($"棋种：{DisplayVariant(_session.Config.Variant)}");
            // P2-7：常驻明示玩家所属阵营。围棋/黑白棋玩家执黑、落子渲染偏白时，
            // 没有这行玩家会把颜色与回合对不上号（测评实测的「配色反转」感知问题）。
            string you = SideLabel(_session.PlayerSide);
            string foe = _session.Rules != null ? SideLabel(_session.Rules.Opposite(_session.PlayerSide)) : "对方";
            GUILayout.Label($"你执{you} · 对方执{foe}");
            GUILayout.Label($"回合：{SideLabel(_session.CurrentTurn)}" +
                            (_session.IsEnded ? "（已结束）" : _session.IsAiTurn ? "（AI 思考中）" : "（你）"));
            if (_session.CanUndo) GUILayout.Label($"悔棋：按 {UndoKey} 或点击按钮");
            if (!string.IsNullOrEmpty(_hint)) GUILayout.Label(_hint);
            if (GUILayout.Button("悔棋")) GetComponent<GameScreen>()?.Undo();
            if (GUILayout.Button("重新开局")) GetComponent<GameScreen>()?.Attach(_session.Config != null
                ? new GameSession(_session.Config, new ChessSage.Core.Mechanism.MechanismEngine(_session.Config.Rules))
                : null);
            GUILayout.EndArea();
        }

        static string SideLabel(string side)
            => side == "red" ? "红方" : side == "black" ? "黑方" : side == "white" ? "白方" : side;

        static string DisplayVariant(string id)
        {
            var info = ChessSage.Core.Config.VariantCatalog.Get(id);
            return info?.DisplayName ?? id;
        }
    }
}