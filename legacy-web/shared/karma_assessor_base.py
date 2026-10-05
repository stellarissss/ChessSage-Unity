"""
业力评估器 —— 共享基类（KarmaAssessorBase）。

────────────────────────────────────────────────────────────────────
【本文件的由来 —— 复用逻辑说明】

    本模块是从 6 个棋类目录（xiangqi / weiqi / wuziqi / tiaoqi /
    heibaiqi / dongwuqi）各自独立的 karma_assessor.py 中提取出来的
    公共基类。原 6 份文件的逻辑经 difflib 量化，平均相似度高达
    98.9%，几乎完全相同；真正的差异只有 3 处：

        1. KARMA_ASSESS_PROMPT 中的「棋类具体示例」文案
           （例：象棋写“把我的马变成炮”，围棋写“让棋子走在已有棋子上”）
           → 参数化为类属性 EXAMPLES_TEXT。
        2. __init__ 的 game_type 默认值（"xiangqi" / "weiqi" / ...）
           → 参数化为类属性 GAME_TYPE。
        3. 动物棋（dongwuqi）在 assess() 末尾多一段「畜生道加价」
           （当前 realm 为 animal 且分类为 C/C+ 时额外 +20 点）
           → 抽象为可覆盖的钩子方法 _post_assess_hook()。

    因此，各棋类的 karma_assessor.py 现在只需继承本基类并声明
    GAME_TYPE / EXAMPLES_TEXT 两个类属性（动物棋再覆盖
    _post_assess_hook），即可复用全部约 350 行业力评估逻辑，
    而无需各自复制一份。

【子类契约】
    1. 定义 GAME_TYPE 类属性（该棋类的 game_type 字符串）。
    2. 定义 EXAMPLES_TEXT 类属性（该棋类的 prompt 示例文案块）。
    3. 可选覆盖 _post_assess_hook(amount, intent_class, skill_modifiers)
       以追加棋类专属的业力调整（如动物棋的畜生道加价）。

【行为等价性约束】
    本基类的全部方法签名、默认值、返回结构与原 xiangqi 版本逐行
    等价；仅将差异点抽为参数。任何棋类的对外行为不得改变。
────────────────────────────────────────────────────────────────────
"""
from __future__ import annotations

import json  # noqa: F401  （保留以兼容可能的扩展，原文件存在此 import）
import random
import time  # noqa: F401  （保留以兼容可能的扩展，原文件存在此 import）
from pathlib import Path  # noqa: F401  （保留以兼容可能的扩展，原文件存在此 import）
from typing import Optional

import httpx

# 模型/地址真源：统一从 ai_config 读取，禁止在本文件写死模型字符串。
from .ai_config import get_base_url, get_model, get_no_think_params

# 原 karma_assessor.py 中保留了 BASE_DIR 供潜在路径引用；
# 基类统一保留该语义（指向 shared/ 的父目录，即仓库根）。
BASE_DIR = Path(__file__).resolve().parent.parent

# ────────────────────────────────────────────────────────────────────
# Prompt 模板：拆分为「公共头部 + 棋类示例(EXAMPLES_TEXT) + 公共尾部」，
# 使 6 个棋类只需提供不同的示例文案，其余 prompt 正文完全复用。
# ────────────────────────────────────────────────────────────────────

_PROMPT_HEAD = """
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
"""

# 「局势调整」块：所有棋类完全一致。
_PROMPT_ADJUST = """
### 局势调整
- 玩家大优时（优势 >50%）：×1.2（更重）
- 玩家劣势时（优势 <30%）：×0.9（稍轻）
"""

# 「边界约束 + 输出指令」块：所有棋类完全一致。
_PROMPT_TAIL = """
### 边界约束（绝对不可违反）
- 最低 1 点（即使评估为 0 或负数，也必须输出 1）
- 最高 120 点（即单次上限；即使评估超过 120，也必须输出 120）

输出一个整数，不要任何解释。
"""


class KarmaAssessorBase:
    """业力评估器 —— 共享基类（本地运行，与 ChatAI 并行处理，业障模型）。

    子类必须声明 GAME_TYPE 与 EXAMPLES_TEXT；可选覆盖 _post_assess_hook。
    """

    # 子类覆盖：该棋类的 game_type 标识（用于日志 / 缓存键 / prompt 占位）。
    GAME_TYPE: str = "xiangqi"

    # 子类覆盖：该棋类的「具体示例」prompt 文案块（含"### 具体示例"标题行）。
    EXAMPLES_TEXT: str = """
### 象棋具体示例（必须参考）
- "把我的马变成炮"：53 点（C 类 ×1.0）
"""

    # 子类可选覆盖：插在「局势调整」与「边界约束」之间的棋类专属追加文案。
    # 仅动物棋（dongwuqi）使用：加入「守道者加价」说明。
    EXTRA_PROMPT: str = ""

    def __init__(self, api_key: str = "", game_type: Optional[str] = None):
        self.api_key = api_key
        # Base URL / 模型名均取自配置真源（根 config.json / 环境变量），
        # 与 ai_orchestrator / chess_ai 保持完全一致，避免三处配置漂移。
        self.base_url = get_base_url()
        # game_type：优先使用显式传入值，否则回退到子类 GAME_TYPE 常量。
        self.game_type = game_type if game_type is not None else self.GAME_TYPE
        self._cache = {}
        self._local_karma = 50
        self._local_karma_max = 120
        self._local_karma_single_max = 120
        self._initial_karma = 50
        self._realm_detection = 0.0
        self._current_realm = "human"
        # 一次性技能（stealth_t2a 首次透支免判 / stealth_t3a 金蝉脱壳）本关已消耗记录。
        # Samsara 服务端为权威消耗源（one_time_skill_usage，随关卡重置）；此处为棋类侧
        # 进程内的镜像记录：即使与 Samsara 的同步（/api/karma/consume）失败，也保证
        # "每关一次"语义不被重复触发。
        self._used_one_time_skills: set = set()

    def set_api_key(self, api_key: str):
        self.api_key = api_key

    def set_game_type(self, game_type: str):
        self.game_type = game_type

    def set_local_karma_state(self, karma: int, karma_max: int, single_max: int):
        """设置本地业力状态（关卡内变量）"""
        self._local_karma = karma
        self._local_karma_max = karma_max
        self._local_karma_single_max = single_max

    def set_realm_detection(self, detection: float, realm: str):
        """设置道级识破概率（全局变量）"""
        self._realm_detection = detection
        self._current_realm = realm

    def get_local_karma(self) -> int:
        return self._local_karma

    def get_local_karma_max(self) -> int:
        return self._local_karma_max

    def get_realm_detection(self) -> float:
        return self._realm_detection

    # ────────────────────────────────────────────────────────────────
    # prompt 组装：公共头部 + 子类示例 + 公共尾部
    # ────────────────────────────────────────────────────────────────

    def _full_prompt_template(self) -> str:
        """拼装完整 prompt 模板（头部 + 棋类示例 + 局势调整 + 可选追加 + 尾部）。"""
        return (
            _PROMPT_HEAD
            + self.EXAMPLES_TEXT
            + _PROMPT_ADJUST
            + self.EXTRA_PROMPT
            + _PROMPT_TAIL
        )

    def _build_prompt(self, instruction: str, intent_class: str, board_summary: str) -> str:
        return self._full_prompt_template().format(
            game_type=self.game_type,
            instruction=instruction,
            intent_class=intent_class,
            board_summary=board_summary,
            karma=self._local_karma,
            max_karma=self._local_karma_max,
            max_single=self._local_karma_single_max,
        )

    async def assess(self, instruction: str, intent_class: str, board_summary: str,
                     skill_modifiers: dict = None) -> int:
        """
        评估业力增加量（1~120）
        与ChatAI并行调用，不依赖samsara API
        """
        if skill_modifiers is None:
            skill_modifiers = {}

        # E 类固定 1 点
        if intent_class == "E":
            return 1

        cache_key = f"{self.game_type}:{instruction}:{intent_class}"
        if cache_key in self._cache:
            return self._cache[cache_key]

        if not self.api_key:
            amount = self._fallback_assess(intent_class)
        else:
            prompt = self._build_prompt(instruction, intent_class, board_summary)
            try:
                async with httpx.AsyncClient(timeout=30) as client:
                    response = await client.post(
                        f"{self.base_url}/chat/completions",
                        headers={"Authorization": f"Bearer {self.api_key}"},
                        json={
                            "model": get_model(),
                            "messages": [{"role": "user", "content": prompt}],
                            "temperature": 0.3,
                            # 关闭推理：业力评估是简单数值映射，无需思考链。
                            # 实测开启推理时延 ~21s 且 content 常为空；关闭后 ~0.65s。
                            **get_no_think_params(),
                            # 保留适度预算，兼容未关闭推理的兼容模型。
                            "max_tokens": 512,
                        },
                    )
                    response.raise_for_status()
                    data = response.json()
                    content = data["choices"][0]["message"]["content"].strip()
                    amount = int(content)
            except Exception:
                amount = self._fallback_assess(intent_class)

        # 技能折扣 / 道级折扣（与原文一致）
        if skill_modifiers.get("efficiency_fraud") and self._should_discount():
            amount = int(amount * 0.7)

        if self._current_realm in ("hell", "hungry") and skill_modifiers.get("hell_hungry_discount"):
            amount = int(amount * 0.75)

        if self._current_realm in ("heaven", "asura") and skill_modifiers.get("heaven_asura_discount"):
            amount = int(amount * 0.75)

        # 棋类专属钩子：动物棋在此追加「畜生道加价」逻辑
        amount = self._post_assess_hook(amount, intent_class, skill_modifiers)

        amount = max(1, min(amount, 120))
        self._cache[cache_key] = amount
        return amount

    def _post_assess_hook(self, amount: int, intent_class: str,
                          skill_modifiers: dict) -> int:
        """棋类专属业力调整钩子。

        默认无操作；动物棋（dongwuqi）覆盖此方法，在畜生道且分类为
        C / C+ 时额外 +20 点。
        """
        return amount

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
        return random.random() < 0.3

    def increase_karma(self, amount: int, skill_modifiers: dict = None,
                       allow_overdraft: bool = True) -> dict:
        """
        增加业力（作弊产生业障）。
        返回: {actual_increased, is_overdraft, overdraft_amount, new_karma, success}
        """
        if skill_modifiers is None:
            skill_modifiers = {}

        max_single = self._local_karma_single_max + skill_modifiers.get("karma_single_max_bonus", 0)
        if amount > max_single:
            return {"actual_increased": 0, "is_overdraft": False,
                    "overdraft_amount": 0, "new_karma": self._local_karma, "success": False}

        threshold = self._local_karma_max + skill_modifiers.get("karma_max_bonus", 0)
        old_karma = self._local_karma
        self._local_karma += amount
        overshoot = max(0, self._local_karma - threshold)

        return {
            "actual_increased": amount,
            "is_overdraft": overshoot > 0,
            "overdraft_amount": float(overshoot),
            "new_karma": self._local_karma,
            "success": True,
        }

    def decrease_karma(self, amount: int, skill_modifiers: dict = None) -> int:
        """减少业力（下棋消业）。最小为0。返回实际减少量。"""
        if skill_modifiers is None:
            skill_modifiers = {}
        multiplier = skill_modifiers.get("karma_recover_multiplier", 1.0)
        actual = int(amount * multiplier)
        old = self._local_karma
        self._local_karma = max(0, self._local_karma - actual)
        return old - self._local_karma

    def refund_karma(self, amount: int, skill_modifiers: dict = None) -> None:
        """退还业力（作弊失败时全额退还）。1:1退还，无加成。"""
        self._local_karma = max(0, self._local_karma - amount)

    # 向后兼容
    def consume_karma(self, amount: int, allow_overdraft: bool = True,
                      skill_modifiers: dict = None) -> dict:
        return self.increase_karma(amount, skill_modifiers, allow_overdraft)

    def recover_karma(self, amount: int, skill_modifiers: dict = None) -> int:
        return self.decrease_karma(amount, skill_modifiers)

    def calculate_detection_delta(self, overshoot_amount: float,
                                  skill_modifiers: dict = None) -> float:
        """计算识破概率增长（道级全局变量）

        公式: Δ = C × O^α
        其中 O = 业力超出安全阈值的部分
        C = 0.1, α = 1.5（默认）
        当 O = 100 时，Δ = 100%（超出100点对应100%识破概率增加）
        """
        if skill_modifiers is None:
            skill_modifiers = {}
        if overshoot_amount <= 0:
            return 0.0

        C = skill_modifiers.get("detection_coefficient", 0.1)
        alpha = skill_modifiers.get("detection_alpha", 1.5)
        delta = C * (overshoot_amount ** alpha)

        if skill_modifiers.get("mist_fog") and self._realm_detection > 70:
            if random.random() < 0.3:
                return 0.0

        return delta

    def handle_overdraft(self, overshoot_amount: float,
                         skill_modifiers: dict = None) -> dict:
        """
        处理业力超阈值，更新识破概率（道级全局变量）
        返回: {detected, delta, current, escaped, reset, message, skip}
        """
        if skill_modifiers is None:
            skill_modifiers = {}

        if overshoot_amount <= 0:
            return {
                "detected": False,
                "delta": 0.0,
                "current": self._realm_detection,
            }

        # 一次性技能（每关一次）：本关已消耗则不再生效
        if (skill_modifiers.get("first_overdraft_skip")
                and "stealth_t2a" not in self._used_one_time_skills):
            self._used_one_time_skills.add("stealth_t2a")
            return {
                "detected": False,
                "delta": 0.0,
                "current": self._realm_detection,
                "skip": True,
            }

        delta = self.calculate_detection_delta(overshoot_amount, skill_modifiers)
        if delta <= 0:
            return {
                "detected": False,
                "delta": 0.0,
                "current": self._realm_detection,
            }

        self._realm_detection = min(self._realm_detection + delta, 100.0)
        current = self._realm_detection

        roll = random.random() * 100
        detected = roll < current

        if detected:
            if (skill_modifiers.get("golden_escape")
                    and "stealth_t3a" not in self._used_one_time_skills):
                self._used_one_time_skills.add("stealth_t3a")
                self._realm_detection = current * 0.5
                return {
                    "detected": False,
                    "delta": delta,
                    "current": current * 0.5,
                    "escaped": True,
                }
            else:
                return {
                    "detected": True,
                    "delta": delta,
                    "current": 0.0,
                    "reset": True,
                    "message": "天道识破 · 妄改天规者，罚入轮回",
                }

        return {
            "detected": detected,
            "delta": delta,
            "current": current,
        }

    def reset_level_karma(self, skill_modifiers: dict = None, carryover: int = 0):
        """重置关卡业力（每个关卡开始时调用）
        业力 = max(0, 初始值 - 净身减免) + 本道溢出叠加
        """
        if skill_modifiers is None:
            skill_modifiers = {}
        reduction = skill_modifiers.get("initial_karma_reduction", 0)
        base = max(0, self._initial_karma - reduction)
        self._local_karma = base + max(0, carryover)
        self._cache.clear()
        # 新关卡开始：一次性技能（首次透支免判/金蝉脱壳）恢复可用
        self._used_one_time_skills.clear()

    def get_state(self, skill_modifiers: dict = None) -> dict:
        """获取当前业力和识破状态"""
        if skill_modifiers is None:
            skill_modifiers = {}
        return {
            "karma": {
                "current": self._local_karma,
                "max": self._local_karma_max + skill_modifiers.get("karma_max_bonus", 0),
                "single_max": self._local_karma_single_max + skill_modifiers.get("karma_single_max_bonus", 0),
                "initial": max(0, self._initial_karma - skill_modifiers.get("initial_karma_reduction", 0)),
            },
            "detection": self._realm_detection,
            "realm": self._current_realm,
        }


__all__ = ["KarmaAssessorBase", "BASE_DIR"]
