"""结局系统（v1.4）

五种结局判定：
1. enlightenment  悟道结局·破茧成蝶（good）
2. corruption     堕落结局·永堕轮回（bad）
3. samsara        轮回结局·继续修行（neutral）
4. true_me        真我结局·与自己和解（true）
5. exposed        识破结局·天道审判（worst）

判定优先级：
  exposed（识破路径 + Boss 战胜） >
  true_me（全记忆碎片 + 悟道线 + 无作弊 + 无祈求） >
  enlightenment / corruption / samsara（根据 alignment 与最终选择）

get_ending_data() 现会注入 runtime 字段（如 prayer_count）
并替换对白中的 `{prayer_count}` 占位符，确保 story.json 中的占位符被
忠实替换为实际数值。
"""
import copy
import json
import re
from pathlib import Path
from .state import SamsaraState

BASE_DIR = Path(__file__).resolve().parent.parent
STORY_FILE = BASE_DIR / "configs" / "story.json"


class EndingSystem:
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

    def get_ending_data(self, ending_id: str) -> dict:
        """获取结局剧情数据。

        返回 deep-copy 后的结局数据，并注入 runtime 字段：
        - `prayer_count`：当前玩家累计祈求次数（用于替换对白占位符）。

        所有对白文本（dialogues_before / dialogues / epilogue_dialogues）
        中的 `{prayer_count}` 占位符会被替换为实际数值，确保 story.json
        写的对白被忠实渲染。
        """
        data = self._story.get("endings", {}).get(ending_id, {})
        if not data:
            return {}

        # 深拷贝，避免污染 story.json 缓存
        data = copy.deepcopy(data)

        # 注入 runtime 字段（供前端使用）
        prayer_count = self.state.get_prayer_count()
        data["prayer_count"] = prayer_count

        # 替换对白文本中的 {prayer_count} 占位符
        placeholders = {"prayer_count": str(prayer_count)}
        for key in ("dialogues_before", "dialogues", "epilogue_dialogues"):
            dialogues = data.get(key)
            if not isinstance(dialogues, list):
                continue
            for d in dialogues:
                if not isinstance(d, dict):
                    continue
                text = d.get("text")
                if not isinstance(text, str):
                    continue
                d["text"] = self._substitute_placeholders(text, placeholders)
        return data

    @staticmethod
    def _substitute_placeholders(text: str, placeholders: dict) -> str:
        """将文本中的 {key} 占位符替换为实际值（仅替换已知 key）。"""
        def repl(m):
            key = m.group(1)
            return placeholders.get(key, m.group(0))
        return re.sub(r"\{(\w+)\}", repl, text)

    def get_all_endings_status(self) -> dict:
        """返回所有结局的解锁状态"""
        unlocked = self.state.get_endings_unlocked()
        result = {}
        for ending_id, is_unlocked in unlocked.items():
            data = self.get_ending_data(ending_id)
            result[ending_id] = {
                "unlocked": is_unlocked,
                "name": data.get("name", ending_id),
                "type": data.get("type", ""),
            }
        return result

    def check_exposed_ending(self) -> bool:
        """识破结局：使用过祈求 + 天道Boss战胜利"""
        ds = self.state.get_detection_state()
        boss = self.state.get_tiandao_boss_state()
        return (
            self.state.get_prayer_count() >= 1
            and ds.get("exposure_path_triggered", False)
            and boss.get("defeated", False)
        )

    def check_true_me_ending(self, final_choice: str = None) -> bool:
        """真我结局：全记忆碎片 + 悟道线最终选择 + Boss战无作弊 + 无祈求"""
        if self.state.get_prayer_count() > 0:
            return False
        if not self.state.all_memory_fragments_collected():
            return False
        if final_choice and final_choice != "enlightenment":
            return False
        align = self.state.get_alignment()
        if align.get("enlightenment", 0) < 9:
            return False
        return True

    def check_enlightenment_ending(self, final_choice: str = None) -> bool:
        """悟道结局：悟道值 > 堕落值 3+ + 选悟道 + 业力<100 + 无祈求"""
        if self.state.get_prayer_count() > 0:
            return False
        if final_choice and final_choice != "enlightenment":
            return False
        align = self.state.get_alignment()
        e = align.get("enlightenment", 0)
        c = align.get("corruption", 0)
        if e - c < 3:
            return False
        if self.state.get_karma() >= 100:
            return False
        return True

    def check_corruption_ending(self, final_choice: str = None) -> bool:
        """堕落结局：堕落值 > 悟道值 3+ + 选堕落 + 业力>200 + 无祈求"""
        if self.state.get_prayer_count() > 0:
            return False
        if final_choice and final_choice != "corruption":
            return False
        align = self.state.get_alignment()
        e = align.get("enlightenment", 0)
        c = align.get("corruption", 0)
        if c - e < 3:
            return False
        if self.state.get_karma() <= 200:
            return False
        return True

    def check_samsara_ending(self, final_choice: str = None) -> bool:
        """轮回结局：|悟道-堕落| < 3 + 选轮回 + 100≤业力≤200 + 无祈求"""
        if self.state.get_prayer_count() > 0:
            return False
        if final_choice and final_choice != "samsara":
            return False
        align = self.state.get_alignment()
        e = align.get("enlightenment", 0)
        c = align.get("corruption", 0)
        if abs(e - c) >= 3:
            return False
        karma = self.state.get_karma()
        if karma < 100 or karma > 200:
            return False
        return True

    def determine_ending(self, final_choice: str = None, no_cheat_final: bool = True) -> dict:
        """综合判定结局。

        Args:
            final_choice: 最终选择（"enlightenment"/"corruption"/"samsara"），可为 None
            no_cheat_final: 最终 Boss 战是否无作弊

        Returns:
            {
                "ending_id": str,
                "ending_data": dict,
                "reason": str,
            }
        """
        # 优先级 1：识破结局
        if self.check_exposed_ending():
            self.state.unlock_ending("exposed")
            return {
                "ending_id": "exposed",
                "ending_data": self.get_ending_data("exposed"),
                "reason": "使用真心祈求 + 天道Boss战胜利 → 识破结局",
            }

        # 优先级 2：真我结局
        if self.check_true_me_ending(final_choice) and no_cheat_final:
            self.state.unlock_ending("true_me")
            return {
                "ending_id": "true_me",
                "ending_data": self.get_ending_data("true_me"),
                "reason": "全记忆碎片 + 悟道线 + 无作弊 + 无祈求 → 真我结局",
            }

        # 优先级 3：悟道结局
        if self.check_enlightenment_ending(final_choice):
            self.state.unlock_ending("enlightenment")
            return {
                "ending_id": "enlightenment",
                "ending_data": self.get_ending_data("enlightenment"),
                "reason": "悟道线 + 悟道值领先 + 业力低 → 悟道结局",
            }

        # 优先级 4：堕落结局
        if self.check_corruption_ending(final_choice):
            self.state.unlock_ending("corruption")
            return {
                "ending_id": "corruption",
                "ending_data": self.get_ending_data("corruption"),
                "reason": "堕落线 + 堕落值领先 + 业力高 → 堕落结局",
            }

        # 优先级 5：轮回结局
        if self.check_samsara_ending(final_choice):
            self.state.unlock_ending("samsara")
            return {
                "ending_id": "samsara",
                "ending_data": self.get_ending_data("samsara"),
                "reason": "轮回选择 + alignment 接近 + 业力中 → 轮回结局",
            }

        # 兜底：根据 alignment 强制判定（无最终选择时）
        align = self.state.get_alignment()
        e = align.get("enlightenment", 0)
        c = align.get("corruption", 0)

        if self.state.get_prayer_count() > 0:
            # 有祈求但未击败天道 Boss：不允许结束（应先打 Boss）
            return {
                "ending_id": None,
                "ending_data": None,
                "reason": "已使用祈求但未完成天道Boss战，无法判定结局",
            }

        if e > c:
            self.state.unlock_ending("enlightenment")
            return {
                "ending_id": "enlightenment",
                "ending_data": self.get_ending_data("enlightenment"),
                "reason": "兜底：悟道值高于堕落值 → 悟道结局",
            }
        elif c > e:
            self.state.unlock_ending("corruption")
            return {
                "ending_id": "corruption",
                "ending_data": self.get_ending_data("corruption"),
                "reason": "兜底：堕落值高于悟道值 → 堕落结局",
            }
        else:
            self.state.unlock_ending("samsara")
            return {
                "ending_id": "samsara",
                "ending_data": self.get_ending_data("samsara"),
                "reason": "兜底：alignment 均衡 → 轮回结局",
            }

    def should_trigger_tiandao_boss(self) -> bool:
        """是否应该触发天道Boss战（六道通关 + 祈求≥1）"""
        if not self.state.all_realms_completed():
            return False
        return self.state.get_prayer_count() >= 1

    def get_ending_preview(self) -> dict:
        """返回当前状态下的结局预览（不锁定）。

        结局显示名从 story.json.endings.{id}.name 读取，确保与剧情权威源一致。
        """
        align = self.state.get_alignment()
        prayer_count = self.state.get_prayer_count()
        all_realms = self.state.all_realms_completed()
        all_frags = self.state.all_memory_fragments_collected()

        def ending_name(eid: str) -> str:
            return self._story.get("endings", {}).get(eid, {}).get("name", eid)

        if prayer_count > 0:
            if all_realms:
                return {
                    "predicted": "exposed",
                    "name": ending_name("exposed"),
                    "condition": "六道通关 + 使用过祈求 → 天道Boss战 → 识破结局",
                }
            return {
                "predicted": "exposed_pending",
                "name": "识破路径（待通关）",
                "condition": f"已祈求 {prayer_count} 次，通关六道后触发天道Boss战",
            }

        if all_frags and align.get("enlightenment", 0) >= 9:
            return {
                "predicted": "true_me",
                "name": ending_name("true_me"),
                "condition": "全记忆碎片 + 悟道值≥9 + 无祈求",
            }

        e = align.get("enlightenment", 0)
        c = align.get("corruption", 0)
        if e - c >= 3:
            return {
                "predicted": "enlightenment",
                "name": ending_name("enlightenment"),
                "condition": f"悟道值({e}) - 堕落值({c}) ≥ 3 + 无祈求",
            }
        if c - e >= 3:
            return {
                "predicted": "corruption",
                "name": ending_name("corruption"),
                "condition": f"堕落值({c}) - 悟道值({e}) ≥ 3 + 无祈求",
            }
        return {
            "predicted": "samsara",
            "name": ending_name("samsara"),
            "condition": f"悟道值({e}) ≈ 堕落值({c}) + 无祈求",
        }
