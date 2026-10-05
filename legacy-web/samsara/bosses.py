import json
import random
from pathlib import Path
from .state import SamsaraState

BASE_DIR = Path(__file__).resolve().parent.parent
CONFIGS_DIR = BASE_DIR / "configs"
BOSS_DEFINITIONS_FILE = CONFIGS_DIR / "boss_definitions.json"
STORY_FILE = CONFIGS_DIR / "story.json"


class BossSystem:
    def __init__(self, state: SamsaraState):
        self.state = state
        self.bosses = self._load_bosses()
        # 守道者 name 为剧情字段，以 story.json 为权威源覆盖
        self._overlay_story_names()

    def _load_story(self) -> dict:
        if STORY_FILE.exists():
            try:
                return json.loads(STORY_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return {}

    def _load_bosses(self):
        if BOSS_DEFINITIONS_FILE.exists():
            try:
                return json.loads(BOSS_DEFINITIONS_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return self._get_default_bosses()

    def _overlay_story_names(self):
        """从 story.json.realms.{realm}.guardian.name 覆盖守道者名，
        确保剧情角色名由 story.json 统一控制（机械字段仍取 boss_definitions.json）。
        """
        story = self._load_story()
        realms = story.get("realms", {})
        for realm, boss in self.bosses.items():
            guardian = realms.get(realm, {}).get("guardian", {})
            gname = guardian.get("name")
            if gname:
                boss["name"] = gname

    def _get_default_bosses(self):
        return {
            "hell": {
                "id": "flipper",
                "name": "翻覆者",
                "title": "地狱道守道者",
                "skill": "flip",
                "skill_description": "修改规则有30%概率被翻转效果",
                "ai_personality": "aggressive",
                "icon": "☯",
            },
            "hungry": {
                "id": "glutton",
                "name": "饕餮者",
                "title": "饿鬼道守道者",
                "skill": "take_more",
                "skill_description": "每作弊2次，额外偷改1次",
                "ai_personality": "aggressive",
                "icon": "👹",
            },
            "animal": {
                "id": "orderer",
                "name": "秩序者",
                "title": "畜生道守道者",
                "skill": "rank_lock",
                "skill_description": "高等级棋子修改业力消耗额外+20点",
                "ai_personality": "defensive",
                "icon": "🐅",
            },
            "human": {
                "id": "calculator",
                "name": "算计者",
                "title": "人道守道者",
                "skill": None,
                "skill_description": "无特殊技能，最公平的对决",
                "ai_personality": "normal",
                "icon": "🧠",
            },
            "asura": {
                "id": "chaos",
                "name": "狂乱者",
                "title": "阿修罗道守道者",
                "skill": "chaos",
                "skill_description": "作弊后25%概率随机规则变化",
                "ai_personality": "aggressive_random",
                "icon": "⚔️",
            },
            "heaven": {
                "id": "zen",
                "name": "禅定者",
                "title": "天道守道者",
                "skill": "purify",
                "skill_description": "每局3次，直接清除最近1条修改",
                "ai_personality": "defensive_hard",
                "icon": "☸️",
                "skill_uses": 3,
            },
        }

    def get_current_boss(self):
        realm = self.state.get("current_realm")
        return self.bosses.get(realm)

    def trigger_boss_skill(self, cheat_count: int) -> dict:
        realm = self.state.get("current_realm")
        boss = self.bosses.get(realm)
        if not boss or not boss.get("skill"):
            return {"triggered": False}
        modifiers = self.state.get_skill_modifiers()
        reduction = 0.3 if modifiers["boss_skill_reduction"] else 0.0
        skill = boss["skill"]
        result = {"triggered": False, "skill": skill}
        if skill == "flip":
            if random.random() < (0.3 - reduction):
                result["triggered"] = True
                result["effect"] = "规则效果被翻转"
        elif skill == "take_more":
            if cheat_count > 0 and cheat_count % 2 == 0:
                result["triggered"] = True
                result["effect"] = "Boss额外偷改了一次"
        elif skill == "rank_lock":
            result["triggered"] = True
            result["effect"] = "高等级棋子修改费用+20"
        elif skill == "chaos":
            if random.random() < (0.25 - reduction):
                result["triggered"] = True
                result["effect"] = "随机规则变化"
        elif skill == "purify":
            uses = boss.get("skill_uses", 3)
            if uses > 0:
                boss["skill_uses"] -= 1
                result["triggered"] = True
                result["effect"] = "清除了最近1条修改"
        return result

    def reset_boss_skills(self):
        for boss in self.bosses.values():
            if "skill_uses" in boss:
                boss["skill_uses"] = 3