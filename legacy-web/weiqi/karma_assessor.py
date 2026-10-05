"""
围棋（阿修罗道）业力评估器 —— 薄封装。

【复用逻辑说明】
    本文件从原完整实现重构为「共享基类 + 棋类参数」薄封装。
    全部业力评估逻辑（prompt 组装、DeepSeek 调用、识破概率、业力增减等）
    已上移至 shared/karma_assessor_base.py 的 KarmaAssessorBase；
    本子类仅声明棋类专属参数，消除 6 份之间约 98.9% 的重复代码。
"""
from shared.karma_assessor_base import KarmaAssessorBase


class KarmaAssessor(KarmaAssessorBase):
    """围棋业力评估器（阿修罗道）。"""

    GAME_TYPE = "weiqi"

    EXAMPLES_TEXT = """
### 围棋具体示例（必须参考）
- "让我的棋子可以走在已有棋子上"：60 点（C 类 ×1.0）
- "创建一个能提子的特殊棋"：120 点（C+ 类）
- "让我的棋子有两口气"：53 点（C 类）
- "改棋盘大小"：23 点（D 类）
- "让我的棋子可以斜着连"：68 点（C 类，高强度）
- "把棋盘从19×19改成13×13"：30 点（B 类）
- "你好" / "讲个笑话"：1 点（E 类，固定）
"""

