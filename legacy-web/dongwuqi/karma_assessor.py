"""
动物棋（畜生道）业力评估器 —— 薄封装。

【复用逻辑说明】
    本文件从原完整实现重构为「共享基类 + 棋类参数」薄封装。
    全部业力评估逻辑（prompt 组装、DeepSeek 调用、识破概率、业力增减等）
    已上移至 shared/karma_assessor_base.py 的 KarmaAssessorBase；
    本子类仅声明棋类专属参数，消除 6 份之间约 98.9% 的重复代码。
"""
from shared.karma_assessor_base import KarmaAssessorBase


class KarmaAssessor(KarmaAssessorBase):
    """动物棋业力评估器（畜生道）。"""

    GAME_TYPE = "dongwuqi"

    EXAMPLES_TEXT = """
### 动物棋具体示例（必须参考）
- "把我的狼变成象"：60 点（C 类 ×1.0）
- "让我的鼠可以在岸上吃象"：53 点（C 类 ×1.0，强度较低）
- "创建一个能飞的狮"：120 点（C+ 类）
- "让我的虎可以斜着跳河"：75 点（C 类，高强度）
- "给对手加一个额外回合（负面）"：68 点（A 类）
- "改棋盘背景颜色"：15 点（D 类）
- "让我的象可以进入水域"：60 点（C 类）
- "把我的两个狼都变成象"：120 点（C 类 ×2.0）
- "创建一个全图瞬移的人"：150 点（C+ 类，超规格）
- "你好" / "讲个笑话"：1 点（E 类，固定）
"""
    EXTRA_PROMPT = """
### 守道者加价（如果有）
- 畜生道：改高等级棋子（狮/虎/象）额外 +20 点
"""

    def _post_assess_hook(self, amount: int, intent_class: str,
                          skill_modifiers: dict) -> int:
        """畜生道加价：改高等级棋子（C/C+ 类）额外 +20 点。"""
        if self._current_realm == "animal" and intent_class in ("C", "C+"):
            amount += 20
        return amount

