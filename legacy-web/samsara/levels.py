import json
from pathlib import Path
from .state import SamsaraState, REALM_NAMES

BASE_DIR = Path(__file__).resolve().parent.parent
CONFIGS_DIR = BASE_DIR / "configs"
LEVEL_POOLS_FILE = CONFIGS_DIR / "level_pools.json"
PUZZLES_FILE = CONFIGS_DIR / "puzzles.json"


class LevelSystem:
    def __init__(self, state: SamsaraState):
        self.state = state
        self.level_pools = self._load_level_pools()
        self.puzzles = self._load_puzzles()

    def _load_level_pools(self):
        if LEVEL_POOLS_FILE.exists():
            try:
                return json.loads(LEVEL_POOLS_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return self._get_default_level_pools()

    def _load_puzzles(self):
        if PUZZLES_FILE.exists():
            try:
                return json.loads(PUZZLES_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return {}

    def _get_default_level_pools(self):
        return {}

    def load_level(self, realm: str = None, level_index: int = None) -> dict:
        if realm is None:
            realm = self.state.get("current_realm")
        if level_index is None:
            level_index = self.state.get("current_level")
        pool = self.level_pools.get(realm)
        if not pool or level_index >= len(pool["levels"]):
            return None
        level = pool["levels"][level_index]
        result = {
            **level,
            "realm": realm,
            "realm_name": REALM_NAMES.get(realm, ""),
            "game_type": pool["game_type"],
            "realm_icon": pool["icon"],
            "level_index": level_index,
            "total_levels": len(pool["levels"]),
        }
        if level.get("type") == "puzzle" and level.get("puzzle_id"):
            puzzle = self.puzzles.get(pool["game_type"], {}).get(level["puzzle_id"])
            if puzzle:
                result["puzzle_data"] = puzzle
        return result

    def load_sandbox(self, realm: str = None) -> dict:
        if realm is None:
            realm = self.state.get("current_realm")
        pool = self.level_pools.get(realm)
        if not pool:
            return None
        sandbox_config = pool.get("sandbox", {})
        return {
            "type": "sandbox",
            "realm": realm,
            "realm_name": REALM_NAMES.get(realm, ""),
            "game_type": pool["game_type"],
            "realm_icon": pool["icon"],
            "name": sandbox_config.get("name", f"{REALM_NAMES.get(realm, '')} · 沙盒"),
            "description": sandbox_config.get("description", ""),
            "ai_personality": sandbox_config.get("ai_personality", "normal"),
            "ai_depth": sandbox_config.get("ai_depth", 3),
            "turn_limit": 40 if pool["game_type"] == "weiqi" else 20,
            "objective": {"type": "checkmate"},
        }

    def get_current_level(self) -> dict:
        return self.load_level()

    def get_total_levels(self, realm: str = None) -> int:
        if realm is None:
            realm = self.state.get("current_realm")
        pool = self.level_pools.get(realm)
        return len(pool["levels"]) if pool else 0

    def advance_to_next_level(self) -> dict:
        realm = self.state.get("current_realm")
        current_level = self.state.get("current_level")
        total = self.get_total_levels(realm)
        if current_level < total - 1:
            self.state.advance_level()
            return {"success": True, "new_level": self.load_level()}
        return {"success": False, "reason": "已到达最后一关"}

    def get_realm_progress(self, realm: str = None) -> dict:
        if realm is None:
            realm = self.state.get("current_realm")
        pool = self.level_pools.get(realm)
        progress = self.state.get("realm_progress", {}).get(realm, {})
        sandbox_unlocked = self.state.is_sandbox_unlocked(realm) if hasattr(self.state, "is_sandbox_unlocked") else False
        return {
            "realm": realm,
            "name": pool["name"] if pool else "",
            "icon": pool["icon"] if pool else "",
            "game_type": pool["game_type"] if pool else "",
            "total_levels": len(pool["levels"]) if pool else 0,
            "levels_passed": progress.get("levels_passed", 0),
            "completed": progress.get("completed", False),
            "sandbox_unlocked": sandbox_unlocked,
            "levels": pool["levels"] if pool else [],
        }

    def get_all_realms_progress(self) -> list:
        result = []
        for realm in self.level_pools:
            result.append(self.get_realm_progress(realm))
        return result

    def get_realm_levels(self, realm: str) -> dict:
        pool = self.level_pools.get(realm)
        if not pool:
            return None
        progress = self.state.get("realm_progress", {}).get(realm, {})
        levels_passed = progress.get("levels_passed", 0)
        sandbox_unlocked = self.state.is_sandbox_unlocked(realm) if hasattr(self.state, "is_sandbox_unlocked") else False
        levels_with_status = []
        for i, level in enumerate(pool["levels"]):
            level_copy = {**level}
            level_copy["index"] = i
            level_copy["status"] = "completed" if i < levels_passed else ("current" if i == levels_passed else "locked")
            levels_with_status.append(level_copy)
        return {
            "realm": realm,
            "name": pool["name"],
            "icon": pool["icon"],
            "game_type": pool["game_type"],
            "levels": levels_with_status,
            "total_levels": len(pool["levels"]),
            "levels_passed": levels_passed,
            "completed": progress.get("completed", False),
            "sandbox_unlocked": sandbox_unlocked,
            "sandbox": pool.get("sandbox", {}),
        }
