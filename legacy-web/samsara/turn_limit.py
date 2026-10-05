from .state import SamsaraState


class TurnLimitSystem:
    def __init__(self, state: SamsaraState):
        self.state = state

    def tick(self) -> bool:
        self.state.increment_turn()
        return self.is_over()

    def get_remaining(self) -> int:
        limit = self.state.get("turn_limit", 20)
        current = self.state.get("current_turn", 0)
        return max(0, limit - current)

    def reset(self, limit: int = 20):
        self.state.set_turn_limit(limit)
        self.state.reset_turn()

    def is_over(self) -> bool:
        return self.get_remaining() <= 0

    def get_progress(self) -> float:
        limit = self.state.get("turn_limit", 20)
        current = self.state.get("current_turn", 0)
        return min(100, int(current / limit * 100))