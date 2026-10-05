"""
跳棋（饿鬼道）业力评估器 —— 薄封装。

【复用逻辑说明】
    本文件从原完整实现重构为「共享基类 + 棋类参数」薄封装。
    全部业力评估逻辑（prompt 组装、DeepSeek 调用、识破概率、业力增减等）
    已上移至 shared/karma_assessor_base.py 的 KarmaAssessorBase；
    本子类仅声明棋类专属参数，消除 6 份之间约 98.9% 的重复代码。
"""
from shared.karma_assessor_base import KarmaAssessorBase


class KarmaAssessor(KarmaAssessorBase):
    """跳棋业力评估器（饿鬼道）。"""

    GAME_TYPE = "tiaoqi"

    EXAMPLES_TEXT = """
### 跳棋具体示例（必须参考）
- "让我的棋子可以跳两格"：53 点（C 类 ×1.0）
- "创建一个能连跳5格的特殊棋"：120 点（C+ 类）
- "改棋盘颜色"：15 点（D 类）
- "让我的棋子可以斜着跳"：45 点（C 类，强度较低）
- "让对手的棋子只能走一步"：68 点（A 类）
- "你好" / "讲个笑话"：1 点（E 类，固定）
"""

