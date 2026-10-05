"""
围棋（阿修罗道）机制引擎 —— 薄封装。

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
    """围棋机制引擎（阿修罗道）。"""

    SIDE_LABELS = {"black": "黑方", "white": "白方", "both": "双方"}
    DEFAULT_PLAYER_SIDE = 'black'

    def decrement_player_control(self, board_state: dict, side: str) -> None:
        """消耗一次玩家控制机制（围棋特有逻辑，其余棋类无此方法）。

        当玩家代替 AI 走了一步棋后，消耗一次 player_control 机制。
        remaining 为正数时递减，为 0 或负数时移除。
        """
        mech = self._get_mechanisms(board_state)
        pc_list = mech.get("player_control", [])

        for i, item in enumerate(pc_list):
            item_side = item.get("side", "")
            if item_side == side or item_side == "both":
                remaining = item.get("remaining", 1)
                if remaining > 0:
                    item["remaining"] = remaining - 1
                    if item["remaining"] <= 0:
                        pc_list.pop(i)
                break

