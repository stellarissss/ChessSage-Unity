"""选择系统（v1.4）

处理剧情中的玩家选择：
- 应用选择效果（alignment 变化、karma 变化）
- 记录选择历史
- 返回选择后的响应（包括后续对话）
"""
import json
from pathlib import Path
from .state import SamsaraState

BASE_DIR = Path(__file__).resolve().parent.parent
STORY_FILE = BASE_DIR / "configs" / "story.json"


class ChoiceSystem:
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

    def get_realm(self, realm: str) -> dict:
        return self._story.get("realms", {}).get(realm, {})

    def get_level(self, realm: str, level: str) -> dict:
        return self.get_realm(realm).get("levels", {}).get(level, {})

    def get_choices_for_level(self, realm: str, level: str) -> list:
        """获取某关卡的选择面板列表"""
        level_data = self.get_level(realm, level)
        return level_data.get("choices", [])

    def apply_choice(self, realm: str, level: str, choice_index: int, option_index: int) -> dict:
        """应用玩家选择。

        Args:
            realm: 当前道
            level: 当前关卡号（字符串）
            choice_index: 选择面板索引
            option_index: 选项索引

        Returns:
            {
                "success": bool,
                "effect_applied": dict,  # 应用的效果
                "response": dict,         # 选项的后续对话（若有）
                "ending": str,            # 若选择直接触发结局
                "is_final": bool,         # 是否最终选择
            }
        """
        choices = self.get_choices_for_level(realm, level)
        if choice_index >= len(choices):
            return {"success": False, "message": "选择面板不存在"}

        choice_panel = choices[choice_index]
        options = choice_panel.get("options", [])
        if option_index >= len(options):
            return {"success": False, "message": "选项不存在"}

        option = options[option_index]
        effect = option.get("effect", {})

        # 应用 alignment 效果
        if effect:
            self.state.add_alignment(effect)

        # 记录选择
        self.state.record_choice({
            "realm": realm,
            "level": level,
            "choice_index": choice_index,
            "option_index": option_index,
            "option_text": option.get("text", ""),
            "hint": option.get("hint", ""),
            "effect": effect,
        })

        # 更新 story_progress
        self.state.update_story_progress(
            last_choice_made={
                "realm": realm,
                "level": level,
                "choice_index": choice_index,
                "option_index": option_index,
            }
        )

        result = {
            "success": True,
            "effect_applied": effect,
            "response": option.get("response"),
            "ending": option.get("ending"),
            "is_final": choice_panel.get("is_final", False),
            "option_text": option.get("text", ""),
            "hint": option.get("hint", ""),
        }

        return result

    def should_skip_choice(self, realm: str, level: str, choice_index: int) -> bool:
        """检查某选择面板是否应跳过（识破路径下跳过最终选择）"""
        choices = self.get_choices_for_level(realm, level)
        if choice_index >= len(choices):
            return False
        choice_panel = choices[choice_index]
        if choice_panel.get("skip_if_exposure_path"):
            return self.state.is_exposure_path_triggered()
        return False

    def get_alignment_summary(self) -> dict:
        """返回当前 alignment 摘要"""
        align = self.state.get_alignment()
        return {
            "enlightenment": align.get("enlightenment", 0),
            "corruption": align.get("corruption", 0),
            "rationality": align.get("rationality", 0),
            "emotion": align.get("emotion", 0),
            "dominant": self._get_dominant_alignment(align),
        }

    def _get_dominant_alignment(self, align: dict) -> str:
        """返回主导 alignment"""
        enlightenment = align.get("enlightenment", 0)
        corruption = align.get("corruption", 0)
        if enlightenment > corruption:
            return "enlightenment"
        elif corruption > enlightenment:
            return "corruption"
        return "neutral"
