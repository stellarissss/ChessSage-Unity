"""
黑白棋（地狱道）机制引擎 —— 薄封装。

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
    """黑白棋机制引擎（地狱道）。"""

    SIDE_LABELS = {"black": "黑方", "white": "白方", "red": "黑方", "both": "双方"}
    DEFAULT_PLAYER_SIDE = 'black'
