import json
import asyncio
import os
import sys
import httpx
from pathlib import Path
from .state import SamsaraState

# ── 模型/地址真源导入（复用逻辑说明）────────────────────────────
# samsara 这一层位于 RPG 全局包装器，不经过棋类 main.py 的 sys.path 注入，
# 因此此处显式把 <repo>/shared 加入 sys.path，再复用同一份 ai_config。
# 这样「模型名 / Base URL / 密钥」与 12 个棋类完全同源，换模型一处生效。
# 以前这里硬编码了完整 URL 与模型名，是历史遗留的配置分裂点，现统一。
# ────────────────────────────────────────────────────────────
_SHARED = Path(__file__).resolve().parent.parent / "shared"
if _SHARED.exists() and str(_SHARED) not in sys.path:
    sys.path.insert(0, str(_SHARED))

from ai_config import get_api_key as _cfg_get_api_key  # noqa: E402
from ai_config import get_base_url, get_model, get_no_think_params  # noqa: E402

BASE_DIR = Path(__file__).resolve().parent.parent
CONFIG_FILE = BASE_DIR / "config.json"

KARMA_ASSESS_PROMPT = """
你是一个业力评估AI。你的任务是评估玩家作弊指令产生的"业障"值（业力增加量）。

## 背景
- 业力 = 玩家作弊产生的业障，初始为50，安全阈值为120
- 业力 ≤ 120 时安全；超出120的部分会非线性增加识破概率
- 超出越多，识破概率增长越快（非线性）
- 作弊越强力，业障越重
- 下棋事件（吃子/将军/三连等）会减少业力（消业）

## 当前状态
棋类: {game_type}
作弊指令: "{instruction}"
意图分类: {intent_class}
当前局势: {board_summary}
当前业力: {karma}/{max_karma}（越接近上限越危险）
单次增加上限: {max_single}（超出此值的指令将被直接拦截，不予执行）

## 评估标准（必须严格遵守）

### 基础分类价目表
- E 类（聊天/搞笑）：1 点（固定）
- D 类（界面修改/外观）：8-23 点
- A 类（机制修改）：30-60 点
- B 类（棋盘变换/棋子位置）：23-53 点
- C 类（规则修改/棋子走法）：45-90 点
- C+ 类（创建新棋子）：75-120 点

### 强度倍数（乘以基础价）
- 改 1 个棋子/1 条规则：×1.0
- 改 2 个棋子/2 条规则：×2.0
- 改 3 个及以上：×3.0

### 具体示例（必须参考）
{game_examples}

### 局势调整
- 玩家大优时（优势 >50%）：×1.2（更重）
- 玩家劣势时（优势 <30%）：×0.9（稍轻）

### 守道者加价（如果有）
- 畜生道：改高等级棋子额外 +20 点

### 边界约束（绝对不可违反）
- 最低 1 点（即使评估为 0 或负数，也必须输出 1）
- 最高 120 点（即单次上限；即使评估超过 120，也必须输出 120）

输出一个整数，不要任何解释。
"""


class KarmaAssessor:
    def __init__(self, state: SamsaraState):
        self.state = state
        self._api_key = self._load_api_key()
        self._cache = {}

    def _load_api_key(self):
        # 密钥真源：统一走 ai_config（环境变量 DEEPSEEK_API_KEY > 根 config.json > 默认值），
        # 与 12 个棋类共用同一解析链，避免 samsara 层再维护一套密钥查找逻辑。
        return _cfg_get_api_key()

    def set_api_key(self, api_key):
        self._api_key = api_key

    def _get_game_examples(self, game_type: str) -> str:
        """根据棋类返回特定的业力消耗示例"""
        examples = {
            "xiangqi": """- "把我的一个马改成炮"：60 点（C 类 ×1.0）
- "让我的马可以斜着走"：53 点（C 类 ×1.0，强度较低）
- "创建一个能飞的象"：120 点（C+ 类）
- "让我的车可以穿墙"：75 点（C 类，高强度）
- "给我加一个额外回合"：75 点（A 类，高强度）
- "让对手跳过下一回合"：68 点（A 类）
- "改棋盘背景颜色"：15 点（D 类）""",
            "wuziqi": """- "让我连下两手"：75 点（A 类，高强度）
- "把对手的一颗棋子变成我的"：53 点（B 类 ×1.0）
- "让棋盘多出一排格子"：45 点（B 类）
- "创建一个可以斜着连的棋子"：105 点（C+ 类）
- "改棋盘背景颜色"：15 点（D 类）""",
            "weiqi": """- "让我的棋子免疫被提子"：90 点（C 类，高强度）
- "在棋盘中央额外放一颗子"：53 点（B 类）
- "创建一个可以当眼位的特殊棋子"：113 点（C+ 类）
- "让对手下一手必须下在边角"：68 点（A 类）
- "改棋盘背景颜色"：15 点（D 类）""",
            "dongwuqi": """- "把我的狼变成象"：40 点（C 类 ×1.0）
- "让我的鼠可以在岸上吃象"：53 点（C 类 ×1.0，强度较低）
- "创建一个能飞的狮"：120 点（C+ 类）
- "让我的虎可以斜着跳河"：75 点（C 类，高强度）
- "给对手加一个额外回合（负面）"：68 点（A 类）
- "改棋盘背景颜色"：15 点（D 类）
- "让我的象可以进入水域"：60 点（C 类）""",
            "tiaoqi": """- "让我的棋子可以多跳一步"：60 点（C 类）
- "创建一个可以斜着跳的棋子"：105 点（C+ 类）
- "把对手的一颗棋子移回起点"：53 点（B 类）
- "让我连掷两次骰子"：75 点（A 类）
- "改棋盘背景颜色"：15 点（D 类）""",
            "heibaiqi": """- "让我看对手的一颗背面棋子"：45 点（A 类）
- "把我的一个兵变成将"：75 点（C 类，高强度）
- "创建一个可以斜着翻的棋子"：105 点（C+ 类）
- "让对手的棋子翻过来面朝上"：68 点（B 类）
- "改棋盘背景颜色"：15 点（D 类）""",
        }
        return examples.get(game_type, examples["xiangqi"])

    def _build_prompt(self, game_type, instruction, intent_class, board_summary):
        karma_state = self.state.get_karma()
        max_karma = self.state.get("karma_max", 120)
        max_single = self.state.get("karma_single_max", 120)
        modifiers = self.state.get_skill_modifiers()
        max_karma += modifiers["karma_max_bonus"]
        max_single += modifiers["karma_single_max_bonus"]
        game_examples = self._get_game_examples(game_type)
        return KARMA_ASSESS_PROMPT.format(
            game_type=game_type,
            instruction=instruction,
            intent_class=intent_class,
            board_summary=board_summary,
            karma=karma_state,
            max_karma=max_karma,
            max_single=max_single,
            game_examples=game_examples,
        )

    async def assess(self, game_type: str, instruction: str, intent_class: str, board_summary: str) -> int:
        # E 类固定 1 点
        if intent_class == "E":
            return 1
        cache_key = f"{game_type}:{instruction}:{intent_class}"
        if cache_key in self._cache:
            return self._cache[cache_key]
        if not self._api_key:
            return self._fallback_assess(intent_class)
        prompt = self._build_prompt(game_type, instruction, intent_class, board_summary)
        try:
            async with httpx.AsyncClient(timeout=30) as client:
                response = await client.post(
                    f"{get_base_url()}/chat/completions",
                    headers={"Authorization": f"Bearer {self._api_key}"},
                    json={
                        "model": get_model(),
                        "messages": [{"role": "user", "content": prompt}],
                        "temperature": 0.3,
                        # 关闭推理：业力评估是简单数值映射，无需思考链（详见 shared/ai_config.py）
                        **get_no_think_params(),
                        "max_tokens": 512,
                    },
                )
                response.raise_for_status()
                data = response.json()
                content = data["choices"][0]["message"]["content"].strip()
                amount = int(content)
                modifiers = self.state.get_skill_modifiers()
                if modifiers["efficiency_fraud"] and self._should_discount():
                    amount = int(amount * 0.7)
                current_realm = self.state.get("current_realm")
                if current_realm == "animal" and intent_class in ("C", "C+"):
                    amount += 20
                if current_realm in ("hell", "hungry") and modifiers["hell_hungry_discount"]:
                    amount = int(amount * 0.75)
                if current_realm in ("heaven", "asura") and modifiers["heaven_asura_discount"]:
                    amount = int(amount * 0.75)
                amount = max(1, min(amount, 120))
                self._cache[cache_key] = amount
                return amount
        except Exception:
            return max(1, min(self._fallback_assess(intent_class), 120))

    def _fallback_assess(self, intent_class: str) -> int:
        prices = {
            "E": 1,
            "D": 10,
            "A": 30,
            "B": 25,
            "C": 40,
            "C+": 80,
        }
        return prices.get(intent_class, 30)

    def _should_discount(self) -> bool:
        import random
        return random.random() < 0.3