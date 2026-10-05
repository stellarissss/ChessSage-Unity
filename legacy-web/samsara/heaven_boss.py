"""天道 Boss 战模块（v1.4）

隐藏 Boss 战：玩家通关六道后，若使用过真心祈求，进入与天道的象棋对决。
- 棋类：传统象棋，正常规则
- 玩家被禁止作弊（输入框画红叉，无法输入）
- 天道的"士"被替换成"车"（天道无士，士位全是车）
- 难度：nightmare（搜索深度 6）
- 胜利 → 触发识破结局
- 失败 → 无限重试

对白从 configs/tiandao_boss.json 迁移到 configs/story.json 的
tiandao.boss_dialogues 字段。本模块现在同时读取两源——
机械配置（棋子/规则/AI）从 tiandao_boss.json，对白从 story.json。
资产字段（bgm/background）优先用 story.json.tiandao.boss_battle，回退 tiandao_boss.json。
"""
import json
from pathlib import Path
from .state import SamsaraState

BASE_DIR = Path(__file__).resolve().parent.parent
BOSS_CONFIG_FILE = BASE_DIR / "configs" / "tiandao_boss.json"
STORY_FILE = BASE_DIR / "configs" / "story.json"


class HeavenBossSystem:
    def __init__(self, state: SamsaraState):
        self.state = state
        self._config = self._load_config()           # 机械配置（tiandao_boss.json）
        self._story = self._load_story()              # 剧情数据（story.json）
        self._tiandao = self._story.get("tiandao", {}) if self._story else {}

    def _load_config(self) -> dict:
        if BOSS_CONFIG_FILE.exists():
            try:
                return json.loads(BOSS_CONFIG_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return {}

    def _load_story(self) -> dict:
        if STORY_FILE.exists():
            try:
                return json.loads(STORY_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return {}

    def _get_dialogues(self, key: str) -> list:
        """从 story.json.tiandao.boss_dialogues 读取对白，回退到旧 tiandao_boss.json.dialogues"""
        story_dlg = self._tiandao.get("boss_dialogues", {}).get(key, [])
        if story_dlg:
            return story_dlg
        # 兼容回退：旧机械配置中残留的 dialogues 块
        return self._config.get("dialogues", {}).get(key, [])

    def get_config(self) -> dict:
        return self._config

    def can_enter(self) -> dict:
        """检查是否可以进入天道 Boss 战"""
        if not self.state.all_realms_completed():
            return {
                "can_enter": False,
                "reason": "六道尚未全部通关",
            }
        if self.state.get_prayer_count() < 1:
            return {
                "can_enter": False,
                "reason": "未使用过真心祈求（无需审判）",
            }
        boss = self.state.get_tiandao_boss_state()
        if boss.get("defeated"):
            return {
                "can_enter": False,
                "reason": "天道已被击败（识破结局已触发）",
                "already_defeated": True,
            }
        return {
            "can_enter": True,
            "reason": "六道通关 + 使用过祈求 → 天道Boss战",
        }

    def enter_battle(self) -> dict:
        """进入 Boss 战，返回初始配置"""
        check = self.can_enter()
        if not check["can_enter"]:
            return {"success": False, **check}

        self.state.update_tiandao_boss_state(
            current_battle_active=True,
        )
        # 增加尝试次数
        boss = self.state.get_tiandao_boss_state()
        self.state.update_tiandao_boss_state(
            attempt_count=boss.get("attempt_count", 0) + 1,
        )

        return {
            "success": True,
            "config": self._config,
            "dialogues_on_enter": self._get_dialogues("on_enter"),
            "dialogues_mid": self._get_dialogues("mid_battle"),
            "attempt_count": self.state.get_tiandao_boss_state().get("attempt_count", 1),
        }

    def get_initial_board(self) -> dict:
        """返回 Boss 战初始棋盘配置"""
        return self._config.get("initial_board", {})

    def get_rules(self) -> dict:
        """返回 Boss 战规则"""
        return self._config.get("rules", {})

    def get_pieces_config(self) -> dict:
        """返回棋子配置（天道无士，士位被车占据）"""
        return self._config.get("pieces", {})

    def on_win(self) -> dict:
        """Boss 战胜利处理"""
        self.state.update_tiandao_boss_state(
            defeated=True,
            current_battle_active=False,
        )
        boss_battle = self._tiandao.get("boss_battle", {})
        return {
            "success": True,
            "defeated": True,
            "on_win_action": "trigger_ending_exposed",
            "dialogues_on_win": self._get_dialogues("on_win"),
            "next": boss_battle.get("victory_triggers_ending", "exposed"),
            "message": "天道Boss战胜利 → 触发识破结局",
        }

    def on_lose(self) -> dict:
        """Boss 战失败处理（无限重试）"""
        boss = self.state.get_tiandao_boss_state()
        self.state.update_tiandao_boss_state(
            current_battle_active=False,
        )
        boss_battle = self._tiandao.get("boss_battle", {})
        return {
            "success": True,
            "defeated": False,
            "on_lose_action": boss_battle.get("on_lose_action", "retry"),
            "retry_limit": boss_battle.get("retry_limit", -1),
            "dialogues_on_lose": self._get_dialogues("on_lose"),
            "attempt_count": boss.get("attempt_count", 0),
            "can_retry": True,
            "message": "天道Boss战失败 → 无限重试",
        }

    def get_status(self) -> dict:
        """返回 Boss 战状态"""
        boss = self.state.get_tiandao_boss_state()
        return {
            "defeated": boss.get("defeated", False),
            "attempt_count": boss.get("attempt_count", 0),
            "current_battle_active": boss.get("current_battle_active", False),
            "can_enter": self.can_enter(),
        }

    def get_boss_info(self) -> dict:
        """返回 Boss 基础信息（供前端展示）。

        字段优先从 story.json.tiandao（含 boss_battle）取，回退到 tiandao_boss.json 机械配置。
        """
        boss_battle = self._tiandao.get("boss_battle", {})
        return {
            "boss_id": self._tiandao.get("id", self._config.get("boss_id", "tiandao")),
            "boss_name": self._tiandao.get("name", self._config.get("boss_name", "天道")),
            "chess_type": boss_battle.get("chess_type", self._config.get("chess_type", "xiangqi")),
            "description": self._tiandao.get("description", self._config.get("description", "")),
            "difficulty": self._config.get("ai_config", {}).get("difficulty", "nightmare"),
            "bgm": boss_battle.get("bgm", self._config.get("bgm", "")),
            "background": boss_battle.get("background", self._config.get("background", "")),
            "background_vortex": boss_battle.get("background_vortex", self._config.get("background_vortex", "")),
            "victory_condition": self._config.get("victory_condition", {}),
            "defeat_condition": self._config.get("defeat_condition", {}),
            "note": self._config.get("initial_board", {}).get("note", ""),
        }
