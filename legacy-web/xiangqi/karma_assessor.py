"""
象棋（人道）业力评估器 —— 薄封装。

【复用逻辑说明】
    本文件从原完整实现重构为「共享基类 + 棋类参数」薄封装。
    全部业力评估逻辑（prompt 组装、DeepSeek 调用、识破概率、业力增减等）
    已上移至 shared/karma_assessor_base.py 的 KarmaAssessorBase；
    本子类仅声明棋类专属参数，消除 6 份之间约 98.9% 的重复代码。
"""
from shared.karma_assessor_base import KarmaAssessorBase


class KarmaAssessor(KarmaAssessorBase):
    """象棋业力评估器（人道）。"""

    GAME_TYPE = "xiangqi"

    EXAMPLES_TEXT = """
### 象棋具体示例（必须参考）
- "把我的马变成炮"：53 点（C 类 ×1.0）
- "让我的兵可以倒着走"：45 点（C 类 ×1.0，强度较低）
- "创建一个能飞的将"：120 点（C+ 类）
- "让我的车可以走日字"：75 点（C 类，高强度）
- "给对手加一个额外回合（负面）"：68 点（A 类）
- "改棋盘背景颜色"：15 点（D 类）
- "让我的相可以过河"：53 点（C 类）
- "把我的两个马都变成炮"：105 点（C 类 ×2.0）
- "创建一个全图瞬移的帅"：150 点（C+ 类，超规格）
- "你好" / "讲个笑话"：1 点（E 类，固定）
"""

