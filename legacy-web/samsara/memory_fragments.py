"""记忆碎片系统（v1.4）

管理六道记忆碎片的解锁与展示：
- 解锁条件：某道全程无作弊通关（no_cheat_full_clear）
- 每道对应一段林夜过去的关键记忆
- 集齐全部 6 个记忆碎片是真我结局的必要条件
"""
import json
from pathlib import Path
from .state import SamsaraState, REALMS

BASE_DIR = Path(__file__).resolve().parent.parent
STORY_FILE = BASE_DIR / "configs" / "story.json"


class MemoryFragmentSystem:
    def __init__(self, state: SamsaraState):
        self.state = state
        self._story = self._load_story()

    def _load_story(self) -> dict:
        if STORY_FILE.exists():
            try:
                return json.loads(STORY_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return {}

    def get_fragment_data(self, realm: str) -> dict:
        """获取某道的记忆碎片剧情数据"""
        realm_data = self._story.get("realms", {}).get(realm, {})
        return realm_data.get("memory_fragment", {})

    def check_unlock_condition(self, realm: str) -> bool:
        """检查某道记忆碎片是否满足解锁条件"""
        return self.state.is_realm_no_cheat_clear(realm)

    def try_unlock(self, realm: str) -> dict:
        """尝试解锁某道的记忆碎片。

        Returns:
            {
                "success": bool,
                "already_unlocked": bool,
                "fragment": dict,  # 碎片数据
                "reason": str,
            }
        """
        frags = self.state.get_memory_fragments_unlocked()

        # 已解锁
        if frags.get(realm):
            return {
                "success": False,
                "already_unlocked": True,
                "fragment": self.get_fragment_data(realm),
                "reason": "已解锁",
            }

        # 检查条件
        if not self.check_unlock_condition(realm):
            return {
                "success": False,
                "already_unlocked": False,
                "fragment": None,
                "reason": "需全程无作弊通关此道",
            }

        # 解锁
        self.state.unlock_memory_fragment(realm)
        return {
            "success": True,
            "already_unlocked": False,
            "fragment": self.get_fragment_data(realm),
            "reason": "解锁成功",
        }

    def get_all_fragments_status(self) -> list:
        """返回全部 6 道记忆碎片状态"""
        frags = self.state.get_memory_fragments_unlocked()
        result = []
        for realm in REALMS:
            frag_data = self.get_fragment_data(realm)
            result.append({
                "realm": realm,
                "unlocked": frags.get(realm, False),
                "title": frag_data.get("title", ""),
                "id": frag_data.get("id", ""),
                "cg": frag_data.get("cg", ""),
                "condition_met": self.check_unlock_condition(realm),
            })
        return result

    def get_unlocked_fragments(self) -> list:
        """返回已解锁的记忆碎片完整数据"""
        frags = self.state.get_memory_fragments_unlocked()
        result = []
        for realm in REALMS:
            if frags.get(realm):
                frag_data = self.get_fragment_data(realm)
                frag_data["realm"] = realm
                result.append(frag_data)
        return result

    def all_collected(self) -> bool:
        """是否集齐全部 6 个记忆碎片"""
        return self.state.all_memory_fragments_collected()

    def get_unlock_count(self) -> int:
        frags = self.state.get_memory_fragments_unlocked()
        return sum(1 for v in frags.values() if v)
