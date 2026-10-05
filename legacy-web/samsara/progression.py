from .state import SamsaraState, REALMS


class ProgressionSystem:
    def __init__(self, state: SamsaraState):
        self.state = state

    def resolve_level(self, won: bool, no_cheat: bool, boss_defeated: bool = False) -> dict:
        rewards = {"skill_points": 0, "bonus_reasons": []}
        # 关卡结束（无论胜负）：计算本局业力溢出，叠加到本道下一局
        overshoot = self.state.record_level_end()
        rewards["overshoot_carryover"] = overshoot
        if not won:
            return rewards

        if self.state.is_sandbox_mode():
            rewards["skill_points"] += 1
            rewards["bonus_reasons"].append("沙盒模式胜利")
            self.state.add_skill_point(rewards["skill_points"])
            return rewards

        rewards["skill_points"] += 1
        rewards["bonus_reasons"].append("基础通关奖励")
        if no_cheat:
            rewards["skill_points"] += 2
            rewards["bonus_reasons"].append("无AI通关奖励")
        if not self.state.get("overdraft_count", 0) > 0:
            rewards["skill_points"] += 1
            rewards["bonus_reasons"].append("未透支奖励")
        if boss_defeated and "boss_" + self.state.get("current_realm") not in self.state.get("bosses_defeated", []):
            rewards["skill_points"] += 1
            rewards["bonus_reasons"].append("首次击败Boss")
            self.state.mark_boss_defeated("boss_" + self.state.get("current_realm"))
        self.state.add_skill_point(rewards["skill_points"])
        self.state.set("total_levels_completed", self.state.get("total_levels_completed", 0) + 1)

        self.state.increment_realm_levels_passed(self.state.get("current_realm"))
        current_realm = self.state.get("current_realm")
        levels_passed = self.state.get("realm_progress", {}).get(current_realm, {}).get("levels_passed", 0)
        total_levels = self._get_realm_level_count(current_realm)
        if levels_passed >= total_levels:
            self.state.mark_realm_completed(current_realm)
            self.state.unlock_sandbox(current_realm)
            rewards["sandbox_unlocked"] = True
        return rewards

    def advance_realm(self) -> dict:
        current_realm = self.state.get("current_realm")
        current_index = self.state.get_realm_index()
        realm_progress = self.state.get("realm_progress", {}).get(current_realm, {})
        levels_passed = realm_progress.get("levels_passed", 0)
        total_levels = self._get_realm_level_count(current_realm)
        result = {"advanced": False, "new_realm": None, "direction": None}
        if levels_passed >= total_levels:
            self.state.mark_realm_completed(current_realm)
            if current_index < len(REALMS) - 1:
                overdraft_count = self.state.get("overdraft_count", 0)
                if overdraft_count == 0:
                    new_index = min(current_index + 2, len(REALMS) - 1)
                    result["direction"] = "up_2"
                else:
                    new_index = current_index + 1
                    result["direction"] = "up_1"
                new_realm = REALMS[new_index]
                self.state.set_realm(new_realm)
                self.state.reset_level_state()
                result["advanced"] = True
                result["new_realm"] = new_realm
                modifiers = self.state.get_skill_modifiers()
                if modifiers["free_cheat_on_realm_change"]:
                    result["free_cheat"] = True
            else:
                result["advanced"] = True
                result["new_realm"] = "heaven"
                result["direction"] = "transcend"
        return result

    def retreat_realm(self) -> dict:
        current_index = self.state.get_realm_index()
        if current_index > 0:
            new_realm = REALMS[current_index - 1]
            self.state.set_realm(new_realm)
            self.state.reset_level_state()
            return {"retreated": True, "new_realm": new_realm}
        return {"retreated": False, "new_realm": None}

    def _get_realm_level_count(self, realm: str) -> int:
        counts = {
            "hell": 5,
            "hungry": 5,
            "animal": 5,
            "human": 6,
            "asura": 6,
            "heaven": 6,
        }
        return counts.get(realm, 5)