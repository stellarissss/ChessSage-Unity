"""识破系统（v1.4 概率判定式）

机制说明：
- 识破概率是一个概率值（0-100），随业障溢出累积上升
- 每次使用 AI 修改（真心祈求 / ChatAI 输出后），用随机数结算一次：
    random() * 100 < current_detection  →  命中即被识破
- 被识破后：识破概率锁死为 0，通关六道后触发天道 Boss 战
- 锁死为 0 时，永不命中（但识破结局路径已确定）

识破判词文本从 configs/story.json.detection 读取（judgment_text /
reset_message），确保剧情文案由 story.json 统一控制。
"""
import json
import random
from pathlib import Path
from .state import SamsaraState

BASE_DIR = Path(__file__).resolve().parent.parent
STORY_FILE = BASE_DIR / "configs" / "story.json"

_DEFAULT_JUDGMENT_TEXT = "天道识破 · 你不是渴求胜利的战士，是作弊成性的怪物。"
_DEFAULT_RESET_MESSAGE = "天道识破 · 妄改天规者，罚入轮回"


class DetectionSystem:
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

    def get_judgment_text(self) -> str:
        """识破命中时的判词（从 story.json.detection.judgment_text 读取）"""
        return self._story.get("detection", {}).get(
            "judgment_text", _DEFAULT_JUDGMENT_TEXT
        )

    def get_reset_message(self) -> str:
        """/api/detection/reset 返回的判词（从 story.json.detection.reset_message 读取）"""
        return self._story.get("detection", {}).get(
            "reset_message", _DEFAULT_RESET_MESSAGE
        )

    # ── 概率累积（业障溢出 → 识破概率上升） ──

    def calculate_delta(self, overdraft_amount: float) -> float:
        """根据业障溢出量计算识破概率增量"""
        if overdraft_amount <= 0:
            return 0.0
        modifiers = self.state.get_skill_modifiers()
        C = modifiers["detection_coefficient"]
        alpha = modifiers["detection_alpha"]
        delta = C * (overdraft_amount ** alpha)
        # 雾隐技能：高识破时有概率不累积
        if modifiers["mist_fog"] and self.state.get_detection() > 70:
            if random.random() < 0.3:
                return 0.0
        return delta

    # ── 概率结算 ──

    def check(self, current_detection: float) -> bool:
        """核心概率判定：random * 100 < detection 即命中。
        锁死状态下永返 False。
        """
        # 锁死状态：识破概率视为 0，永不命中
        if self.state.is_detection_locked():
            return False
        if current_detection <= 0:
            return False
        roll = random.random() * 100
        return roll < current_detection

    def check_on_prayer(self) -> dict:
        """每次使用 AI 修改（真心祈求）后调用。
        用当前识破概率进行随机结算，命中即被识破。
        返回判定结果详情。
        """
        # 先累加祈求次数（由 story_api 调用 increment_prayer 后再调用本方法）
        # 此处仅做概率结算
        ds = self.state.get_detection_state()

        # 已识破 / 已锁死：不再判定
        if ds.get("is_detected"):
            return {
                "detected": False,
                "already_detected": True,
                "current": 0.0,
                "prayer_count": self.state.get_prayer_count(),
            }

        current = self.state.get_detection()
        detected = self.check(current)

        if detected:
            # 命中：标记识破，锁死概率，触发 Boss 战路径
            self.state.reset_on_detection()
            return {
                "detected": True,
                "current": 0.0,
                "locked": True,
                "message": self.get_judgment_text(),
                "prayer_count": self.state.get_prayer_count(),
                "trigger_boss_on_complete": True,
            }

        return {
            "detected": False,
            "current": current,
            "locked": False,
            "prayer_count": self.state.get_prayer_count(),
        }

    # ── 业障溢出处理（保留向后兼容，但不再直接导致"识破=重置"） ──

    def handle_overdraft(self, overdraft_amount: float) -> dict:
        """业障溢出时累积识破概率。
        不在溢出时直接判定识破，改为累积概率。
        实际判定在 check_on_prayer() 中进行（每次 AI 修改后）。
        但仍保留概率即时判定入口，以兼容旧调用链。
        """
        if overdraft_amount <= 0:
            return {"detected": False, "delta": 0.0, "current": self.state.get_detection()}

        # 锁死状态：不累积，不判定
        if self.state.is_detection_locked():
            return {
                "detected": False,
                "delta": 0.0,
                "current": 0.0,
                "locked": True,
            }

        modifiers = self.state.get_skill_modifiers()

        # 首次溢出豁免（一次性技能 stealth_t2a：每关一次，消耗持久化到 SamsaraState）
        if modifiers["first_overdraft_skip"]:
            self.state.consume_one_time_skill("stealth_t2a", context="overdraft_skip")
            return {
                "detected": False,
                "delta": 0.0,
                "current": self.state.get_detection(),
                "skip": True,
            }

        delta = self.calculate_delta(overdraft_amount)
        if delta <= 0:
            return {"detected": False, "delta": 0.0, "current": self.state.get_detection()}

        # 累积识破概率
        self.state.increment_detection(delta)
        self.state.record_overdraft()
        current = self.state.get_detection()

        # 溢出时也做一次概率判定（兼容旧链路）
        detected = self.check(current)

        if detected:
            # 金蝉脱壳技能：首次被识破可减半概率逃过（一次性技能 stealth_t3a，消耗持久化）
            if modifiers["golden_escape"]:
                self.state.consume_one_time_skill("stealth_t3a", context="golden_escape")
                self.state.set_detection(current * 0.5)
                return {
                    "detected": False,
                    "delta": delta,
                    "current": current * 0.5,
                    "escaped": True,
                }
            else:
                # 命中：标记识破路径
                self.state.reset_on_detection()
                return {
                    "detected": True,
                    "delta": delta,
                    "current": 0.0,
                    "locked": True,
                    "message": self.get_judgment_text(),
                    "trigger_boss_on_complete": True,
                }

        return {
            "detected": detected,
            "delta": delta,
            "current": current,
        }

    # ── 工具方法 ──

    def get_status(self) -> dict:
        """返回当前识破系统完整状态（供前端展示）"""
        ds = self.state.get_detection_state()
        return {
            "detection": self.state.get_detection(),
            "is_detected": ds.get("is_detected", False),
            "detection_locked": ds.get("detection_locked", False),
            "trigger_boss_on_complete": ds.get("trigger_boss_on_complete", False),
            "exposure_path_triggered": ds.get("exposure_path_triggered", False),
            "prayer_count": self.state.get_prayer_count(),
        }
