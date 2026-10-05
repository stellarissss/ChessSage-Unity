import json
from pathlib import Path
from .state import SamsaraState

BASE_DIR = Path(__file__).resolve().parent.parent
CONFIGS_DIR = BASE_DIR / "configs"
KARMA_EVENTS_FILE = CONFIGS_DIR / "karma_events.json"


class KarmaSystem:
    """业障模型：作弊增加业力，下棋消业减少业力。"""

    def __init__(self, state: SamsaraState):
        self.state = state
        self.events = self._load_events()

    def _load_events(self):
        if KARMA_EVENTS_FILE.exists():
            try:
                return json.loads(KARMA_EVENTS_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return self._get_default_events()

    def _get_default_events(self):
        return {
            "xiangqi": {
                "capture_pawn": 8, "capture_medium": 15, "capture_rook": 25,
                "check": 20, "checkmate": 35, "pawn_cross": 10, "captured": 5,
            },
            "wuziqi": {
                "three": 10, "four": 20, "block_three": 8, "block_four": 18,
                "double_three": 15, "win": 35,
            },
            "weiqi": {
                "capture_small": 10, "capture_large": 20, "life": 15,
                "captured": 5, "corner": 12, "endgame": 8,
            },
            "dongwuqi": {
                "capture_normal": 10, "capture_overrank": 25, "captured": 5,
                "approach": 12, "win": 35,
            },
            "tiaoqi": {
                "jump_3": 10, "jump_5": 20, "home": 15, "single_move": 3, "all_home": 35,
            },
            "heibaiqi": {
                "flip_small": 8, "flip_medium": 15, "flip_large": 25,
                "corner": 20, "flipped": 5, "win": 35,
            },
        }

    def recover(self, game_type: str, event_type: str, event_data: dict = None) -> int:
        """消业：下棋事件减少业力。"""
        game_events = self.events.get(game_type, {})
        base_amount = game_events.get(event_type, 0)
        if base_amount <= 0:
            return 0
        modifiers = self.state.get_skill_modifiers()
        multiplier = modifiers["karma_recover_multiplier"]
        amount = int(base_amount * multiplier)
        self.state.decrease_karma(amount)
        return amount

    def consume(self, amount: int, allow_overdraft: bool = True) -> tuple[int, bool, float]:
        """作弊：增加业力。返回 (actual, is_overdraft, overshoot_amount)。"""
        current_karma = self.state.get_karma()
        max_single = self.state.get("karma_single_max", 120)
        modifiers = self.state.get_skill_modifiers()
        max_single += modifiers["karma_single_max_bonus"]
        if amount > max_single:
            return 0, False, 0.0
        actual, is_overdraft, overshoot = self.state.increase_karma(amount)
        return actual, is_overdraft, overshoot

    def refund(self, amount: int) -> None:
        """退还业力（作弊失败时全额退还）。"""
        self.state.decrease_karma(amount)

    def get_state(self) -> dict:
        modifiers = self.state.get_skill_modifiers()
        return {
            "current": self.state.get_karma(),
            "max": self.state.get("karma_max", 120) + modifiers["karma_max_bonus"],
            "single_max": self.state.get("karma_single_max", 120) + modifiers["karma_single_max_bonus"],
            "initial": self.state.get("initial_karma", 50) - modifiers["initial_karma_reduction"],
        }

    def can_cheat(self) -> bool:
        return self.state.get_karma() >= 0
