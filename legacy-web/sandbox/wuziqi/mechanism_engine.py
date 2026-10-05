"""
五子棋（天道）机制引擎 —— 薄封装。

【复用逻辑说明】
    本文件从原完整实现重构为「共享基类 + 棋类参数」薄封装。
    5 种机制原语（skip_turns / ai_control / random_moves / extra_turns /
    move_limits）与 AI 性格系统的实现已上移至
    shared/mechanism_engine_base.py 的 MechanismEngineBase；
    本子类仅声明棋类专属的阵营命名与玩家控制语义，消除 12 份
    （顶层 6 + sandbox 6）之间约 96.4% 的重复代码。
"""
from shared.mechanism_engine_base import MechanismEngineBase


class MechanismEngine(MechanismEngineBase):
    """五子棋机制引擎（天道）。"""

    SIDE_LABELS = {"white": "白方", "black": "黑方"}
    DEFAULT_PLAYER_SIDE = None

    def side_label(self, side: str) -> str:
        """普通机制的阵营中文标签（与原文严格一致）。"""
        return "白方" if side == "white" else "黑方"
    PC_REASON_DEFAULT = "玩家接管"
    PC_ICON = "👥"
    PC_SHOW_REMAINING = True

    def player_control_label(self, side: str) -> str:
        """player_control 机制的阵营标签（五子棋：非白即黑）。"""
        return "白方" if side == "white" else "黑方"

    def is_player_controlled(self, board_state: dict, side: str) -> bool:
        """判断某方当前回合是否由玩家接管（玩家操控 AI 方）。

        五子棋语义：仅当 player_control 机制激活时才为真，
        是 is_ai_controlled 的对称，无「默认玩家方」概念。
        """
        mech = self._get_mechanisms(board_state)
        return self._find_active_mechanism(mech["player_control"], side) is not None
