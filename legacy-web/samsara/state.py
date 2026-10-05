import json
import re
import copy
from pathlib import Path
from datetime import datetime

BASE_DIR = Path(__file__).resolve().parent.parent
CONFIGS_DIR = BASE_DIR / "configs"
STATE_FILE = CONFIGS_DIR / "samsara_state.json"
SKILL_TREE_FILE = CONFIGS_DIR / "skill_tree.json"

REALMS = ["hell", "hungry", "animal", "human", "asura", "heaven"]

# 一次性技能（每局/每关一次，关卡开始时重置）：
#   stealth_t2a 首次透支免判、stealth_t3a 金蝉脱壳
ONE_TIME_SKILL_IDS = ("stealth_t2a", "stealth_t3a")


def _load_skill_ids() -> set:
    """从 configs/skill_tree.json 加载全部权威技能 id（单一事实来源）。"""
    try:
        tree = json.loads(SKILL_TREE_FILE.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return set()
    ids: set = set()
    for branch in tree.get("branches", {}).values():
        for tier_data in branch.get("tiers", {}).values():
            if "options" in tier_data:
                for opt in tier_data["options"]:
                    if opt.get("id"):
                        ids.add(opt["id"])
            elif tier_data.get("id"):
                ids.add(tier_data["id"])
    return ids


_SKILL_IDS = _load_skill_ids()


def normalize_skill_key(skill_id: str) -> str:
    """把任意历史格式的技能键规范化为技能树权威 id。

    权威格式：configs/skill_tree.json 中的 id（如 karma_capacity_t1 / stealth_t2a）。
    历史格式：权威 id + "_t{tier}" 冗余后缀（如 karma_capacity_t1_t1，由旧版
    unlock_skill 写入）→ 剥离后缀还原权威 id；无法识别时原样返回。
    """
    sid = str(skill_id).strip()
    if sid in _SKILL_IDS:
        return sid
    m = re.fullmatch(r"(.+?)_t\d+", sid)
    if m and m.group(1) in _SKILL_IDS:
        return m.group(1)
    return sid
REALM_NAMES = {
    "hell": "地狱道",
    "hungry": "饿鬼道",
    "animal": "畜生道",
    "human": "人道",
    "asura": "阿修罗道",
    "heaven": "天道",
}


class SamsaraState:
    def __init__(self):
        self._data = self._load_state()
        self._init_defaults()

    def _load_state(self):
        if STATE_FILE.exists():
            try:
                return json.loads(STATE_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return {}

    def _init_defaults(self):
        defaults = {
            "version": 3,
            "current_realm": "hell",
            "current_level": 0,
            "skill_points": 0,
            "karma_max": 120,
            "karma_single_max": 120,
            "initial_karma": 50,
            "realm_overshoot_carryover": 0,
            "realm_detections": {r: 0.0 for r in REALMS},
            "skills": {},
            "one_time_skill_usage": {},   # 一次性技能消耗记录 {skill_id: {used_at, context}}
            "current_turn": 0,
            "turn_limit": 20,
            "cheat_count": 0,
            "overdraft_count": 0,
            "no_cheat_this_level": True,
            "total_levels_completed": 0,
            "bosses_defeated": [],
            "sandbox_unlocked": [],
            "sandbox_mode": False,
            "realm_progress": {r: {"completed": False, "levels_passed": 0, "no_cheat_full_clear": False} for r in REALMS},
            "level_karma": 50,
            # ── RPG 字段 ──
            "alignment": {
                "enlightenment": 0,   # 悟道值
                "corruption": 0,      # 堕落值
                "rationality": 0,     # 理性值（人道独立累计）
                "emotion": 0,         # 情感值（人道独立累计）
            },
            "detection_state": {
                "is_detected": False,             # 是否被天道识破
                "detection_locked": False,        # 识破概率是否锁死为0
                "trigger_boss_on_complete": False,# 通关六道后是否触发天道Boss战
                "exposure_path_triggered": False, # 是否已进入识破结局路径
            },
            "memory_fragments_unlocked": {
                "hell": False, "hungry": False, "animal": False,
                "human": False, "asura": False, "heaven": False,
            },
            "choices_made": [],                  # 玩家选择历史
            "prayer_count": 0,                   # 真心祈求次数（使用AI修改次数）
            "endings_unlocked": {
                "enlightenment": False, "corruption": False, "samsara": False,
                "true_me": False, "exposed": False,
            },
            "tiandao_boss_state": {
                "defeated": False,        # 是否击败天道
                "attempt_count": 0,       # 尝试次数
                "current_battle_active": False,  # 当前是否正在Boss战中
            },
            "story_progress": {
                "prologue_seen": False,
                "current_dialogue_realm": None,
                "current_dialogue_level": None,
                "last_choice_made": None,
            },
            "playthrough_count": 1,
            "last_modified": datetime.now().isoformat(),
        }
        for k, v in defaults.items():
            if k not in self._data:
                self._data[k] = v

        # 技能键规范化：把旧格式 "{id}_t{tier}" 统一为权威 id（就地迁移，见 normalize_skill_key）。
        # 必须先于 v3→v4 迁移执行，保证 pop("karma_capacity_t1") 能匹配到旧格式键。
        normalized_skills: dict = {}
        skills_changed = False
        for key, meta in self._data.get("skills", {}).items():
            norm = normalize_skill_key(key)
            if norm != key:
                skills_changed = True
            if norm in normalized_skills:
                # 同一技能出现两种格式：保留更早的 unlocked_at
                old = normalized_skills[norm].get("unlocked_at", "")
                new = meta.get("unlocked_at", "")
                if new and (not old or new < old):
                    normalized_skills[norm] = meta
            else:
                normalized_skills[norm] = meta
        if skills_changed or len(normalized_skills) != len(self._data.get("skills", {})):
            self._data["skills"] = normalized_skills
            self._save()

        # 迁移：v1→v2 业障模型
        if self._data.get("karma_max") == 150:
            self._data["karma_max"] = 120
        # 单次上限统一为 120：历史存档（80 / 旧版 150）一并归一化
        if self._data.get("karma_single_max", 120) != 120:
            self._data["karma_single_max"] = 120
        if "initial_karma" not in self._data:
            self._data["initial_karma"] = 50
        if "realm_overshoot_carryover" not in self._data:
            self._data["realm_overshoot_carryover"] = 0
        if self._data.get("version", 1) < 2:
            self._data["version"] = 2
            self._data["level_karma"] = self._data["initial_karma"]
        # 迁移：v2→v3 RPG 字段
        if self._data.get("version", 1) < 3:
            self._data["version"] = 3
        # 迁移：v3→v4 业力上限统一为 120（此前空技能表会自动下发 karma_capacity_t1，
        # 使其 +20 后变为 140，导致上限在 120/140 之间漂移）。该技能改为需玩家手动点亮，
        # 已自动下发的一次性回收，保证初始上限恒为 120。
        if self._data.get("version", 1) < 4:
            self._data["version"] = 4
            if "karma_capacity_t1" in self._data.get("skills", {}):
                self._data["skills"].pop("karma_capacity_t1", None)
            # 单次上限同样为 120（与总上限、业力评估口径一致），初始上限恒为 120
        # 补齐 realm_progress 子字段（向后兼容）
        for r in REALMS:
            rp = self._data["realm_progress"].get(r, {})
            if "no_cheat_full_clear" not in rp:
                rp["no_cheat_full_clear"] = False
            self._data["realm_progress"][r] = rp

        if not self._data["skills"]:
            self._data["skills"] = {
                "stealth_t1": {
                    "unlocked_at": datetime.now().isoformat(),
                    "tier": 1,
                },
            }
            self._save()

    def _save(self):
        self._data["last_modified"] = datetime.now().isoformat()
        STATE_FILE.write_text(json.dumps(self._data, ensure_ascii=False, indent=2), encoding="utf-8")

    def get(self, key, default=None):
        return self._data.get(key, default)

    def set(self, key, value):
        self._data[key] = value
        self._save()

    def update(self, data):
        self._data.update(data)
        self._save()

    def get_full_state(self):
        return copy.deepcopy(self._data)

    def reset_level_state(self):
        """关卡开始：业力 = 初始值 + 本道溢出叠加"""
        modifiers = self.get_skill_modifiers()
        reduction = modifiers.get("initial_karma_reduction", 0)
        base = max(0, self._data.get("initial_karma", 50) - reduction)
        carryover = self._data.get("realm_overshoot_carryover", 0)
        self._data["level_karma"] = base + carryover
        self._data["current_turn"] = 0
        self._data["cheat_count"] = 0
        self._data["overdraft_count"] = 0
        self._data["no_cheat_this_level"] = True
        # 一次性技能（首次透支免判/金蝉脱壳）为"每关一次"，关卡开始时重置
        if self._data.get("one_time_skill_usage"):
            self._data["one_time_skill_usage"] = {}
        self._save()

    def record_level_end(self):
        """关卡结束（胜利/失败）：计算本局溢出量，叠加到本道下一局。
        溢出量 = max(0, level_karma - karma_max)。胜负都叠加。
        """
        threshold = self._data.get("karma_max", 120)
        modifiers = self.get_skill_modifiers()
        threshold += modifiers.get("karma_max_bonus", 0)
        current = self._data.get("level_karma", 0)
        overshoot = max(0, current - threshold)
        self._data["realm_overshoot_carryover"] = overshoot
        self._save()
        return overshoot

    def clear_overshoot_carryover(self):
        """清零本道溢出叠加（换道/被识破时调用）"""
        self._data["realm_overshoot_carryover"] = 0
        self._save()

    def advance_level(self):
        self._data["current_level"] += 1
        self._save()

    def set_realm(self, realm):
        """切换道：清零溢出叠加（章节=道，换道不携带业力溢出）"""
        self._data["current_realm"] = realm
        self._data["current_level"] = 0
        self._data["realm_overshoot_carryover"] = 0
        self._save()

    def add_skill_point(self, count=1):
        self._data["skill_points"] += count
        self._save()

    def spend_skill_point(self, count=1):
        if self._data["skill_points"] >= count:
            self._data["skill_points"] -= count
            self._save()
            return True
        return False

    def refund_skill_point(self, count=1):
        """退还技能点（扣点成功但解锁未生效时的回滚路径）。"""
        self._data["skill_points"] += count
        self._save()

    def unlock_skill(self, skill_id, tier):
        """解锁技能：以技能树权威 id（如 stealth_t2a）作为存储键。

        旧版曾存储 "{id}_t{tier}"（如 stealth_t2a_t2），导致 get_skill_modifiers /
        is_skill_unlocked 按裸 id 匹配永远落空、整棵技能树失效；现统一存权威 id，
        旧格式存档由 _init_defaults 的 normalize_skill_key 迁移。
        """
        skill_key = normalize_skill_key(skill_id)
        if self._data["skills"].get(skill_key):
            return False
        self._data["skills"][skill_key] = {
            "unlocked_at": datetime.now().isoformat(),
            "tier": tier,
        }
        self._save()
        return True

    def is_skill_unlocked(self, skill_id, tier=None):
        """判断技能是否已解锁（skill_id 为技能树权威 id，tier 仅保留兼容签名）。"""
        return normalize_skill_key(skill_id) in self._data["skills"]

    # ── 一次性技能（每关一次，关卡开始时随 reset_level_state 重置） ──

    def consume_one_time_skill(self, skill_id: str, context: str = "") -> bool:
        """消耗一次性技能。首次消耗返回 True；已消耗过返回 False（幂等）。"""
        skill_key = normalize_skill_key(skill_id)
        usage = self._data.setdefault("one_time_skill_usage", {})
        if skill_key in usage:
            return False
        usage[skill_key] = {
            "used_at": datetime.now().isoformat(),
            "context": context,
        }
        self._save()
        return True

    def is_one_time_skill_used(self, skill_id: str) -> bool:
        return normalize_skill_key(skill_id) in self._data.get("one_time_skill_usage", {})

    def record_cheat(self):
        self._data["cheat_count"] += 1
        self._data["no_cheat_this_level"] = False
        self._save()

    def record_overdraft(self):
        self._data["overdraft_count"] += 1
        self._save()

    def increase_karma(self, amount):
        """增加业力（作弊产生业障）。可超过安全阈值，超出部分触发识破。"""
        self._data["level_karma"] += amount
        threshold = self._data.get("karma_max", 120)
        overshoot = max(0, self._data["level_karma"] - threshold)
        self._save()
        return amount, overshoot > 0, float(overshoot)

    def decrease_karma(self, amount):
        """减少业力（下棋消业/退还）。最小为0。"""
        old = self._data["level_karma"]
        self._data["level_karma"] = max(0, old - amount)
        actual = old - self._data["level_karma"]
        self._save()
        return actual

    def refund_karma(self, amount):
        """退还业力（作弊失败时全额退还）。"""
        self.decrease_karma(amount)

    # 向后兼容
    def add_karma(self, amount):
        return self.increase_karma(amount)

    def consume_karma(self, amount):
        actual = self.decrease_karma(amount)
        return actual, False

    def get_karma(self):
        return self._data.get("level_karma", 0)

    def set_detection(self, value):
        realm = self._data.get("current_realm", "hell")
        if "realm_detections" not in self._data:
            self._data["realm_detections"] = {r: 0.0 for r in REALMS}
        self._data["realm_detections"][realm] = value
        self._save()

    def get_detection(self):
        realm = self._data.get("current_realm", "hell")
        if "realm_detections" not in self._data:
            self._data["realm_detections"] = {r: 0.0 for r in REALMS}
        return self._data["realm_detections"].get(realm, 0.0)

    def increment_detection(self, delta):
        realm = self._data.get("current_realm", "hell")
        if "realm_detections" not in self._data:
            self._data["realm_detections"] = {r: 0.0 for r in REALMS}
        self._data["realm_detections"][realm] = min(
            self._data["realm_detections"].get(realm, 0.0) + delta, 100.0
        )
        self._save()

    def get_realm_detection(self, realm: str) -> float:
        if "realm_detections" not in self._data:
            self._data["realm_detections"] = {r: 0.0 for r in REALMS}
        return self._data["realm_detections"].get(realm, 0.0)

    def set_realm_detection(self, realm: str, value: float):
        if "realm_detections" not in self._data:
            self._data["realm_detections"] = {r: 0.0 for r in REALMS}
        self._data["realm_detections"][realm] = value
        self._save()

    def increment_turn(self):
        self._data["current_turn"] += 1
        self._save()

    def reset_turn(self):
        self._data["current_turn"] = 0
        self._save()

    def set_turn_limit(self, limit):
        self._data["turn_limit"] = limit
        self._save()

    def mark_realm_completed(self, realm):
        self._data["realm_progress"][realm]["completed"] = True
        self._save()

    def increment_realm_levels_passed(self, realm):
        self._data["realm_progress"][realm]["levels_passed"] += 1
        self._save()

    def mark_boss_defeated(self, boss_id):
        if boss_id not in self._data["bosses_defeated"]:
            self._data["bosses_defeated"].append(boss_id)
            self._save()

    def get_skill_modifiers(self):
        modifiers = {
            "karma_max_bonus": 0,
            "karma_single_max_bonus": 0,
            "karma_recover_multiplier": 1.0,
            "detection_coefficient": 0.1,
            "detection_alpha": 1.5,
            "first_overdraft_skip": False,
            "consecutive_avoid": False,
            "golden_escape": False,
            "mist_fog": False,
            "efficiency_fraud": False,
            "free_cheat_count": 0,
            "initial_karma_reduction": 0,
            "hell_hungry_discount": False,
            "heaven_asura_discount": False,
            "boss_skill_reduction": False,
            "reincarnation_buff": False,
            "transcendence_bonus": 0,
            "free_cheat_on_realm_change": False,
        }
        # 规范化存档键（兼容旧格式 "{id}_t{tier}"），再做技能→modifier 映射
        skills = {
            normalize_skill_key(k): v
            for k, v in self._data.get("skills", {}).items()
        }
        # 一次性技能：已在本关消耗过则视为失效
        used_one_time = self._data.get("one_time_skill_usage", {})
        if "karma_capacity_t1" in skills:
            modifiers["karma_max_bonus"] += 20
        if "karma_capacity_t2a" in skills:
            modifiers["karma_single_max_bonus"] += 30
        if "karma_capacity_t2b" in skills:
            modifiers["karma_recover_multiplier"] = 1.3
        if "karma_capacity_t3a" in skills:
            modifiers["detection_alpha"] = 1.3
        if "karma_capacity_t3b" in skills:
            modifiers["initial_karma_reduction"] = 25
        if "stealth_t1" in skills:
            modifiers["detection_coefficient"] = 0.07
        if "stealth_t2a" in skills and "stealth_t2a" not in used_one_time:
            modifiers["first_overdraft_skip"] = True
        if "stealth_t2b" in skills:
            modifiers["consecutive_avoid"] = True
        if "stealth_t3a" in skills and "stealth_t3a" not in used_one_time:
            modifiers["golden_escape"] = True
        if "stealth_t3b" in skills:
            modifiers["mist_fog"] = True
        if "cheat_mastery_t2a" in skills:
            modifiers["efficiency_fraud"] = True
        if "cheat_mastery_t3a" in skills:
            modifiers["free_cheat_count"] += 1
        if "cheat_mastery_t3b" in skills:
            modifiers["karma_single_max_bonus"] += 40
        if "realm_insight_t1a" in skills:
            modifiers["hell_hungry_discount"] = True
        if "realm_insight_t1b" in skills:
            modifiers["heaven_asura_discount"] = True
        if "realm_insight_t2a" in skills:
            modifiers["boss_skill_reduction"] = True
        if "realm_insight_t2b" in skills:
            modifiers["reincarnation_buff"] = True
        if "realm_insight_t3a" in skills:
            modifiers["transcendence_bonus"] -= 0.05
        if "realm_insight_t3b" in skills:
            modifiers["free_cheat_on_realm_change"] = True
        return modifiers

    def get_realm_index(self):
        return REALMS.index(self._data["current_realm"])

    def is_sandbox_unlocked(self, realm: str) -> bool:
        return realm in self._data.get("sandbox_unlocked", [])

    def unlock_sandbox(self, realm: str):
        if realm not in self._data.get("sandbox_unlocked", []):
            self._data.setdefault("sandbox_unlocked", []).append(realm)
            self._save()

    def set_sandbox_mode(self, enabled: bool):
        self._data["sandbox_mode"] = enabled
        self._save()

    def is_sandbox_mode(self) -> bool:
        return self._data.get("sandbox_mode", False)

    def get_allowed_classifications(self) -> set:
        """返回当前技能树解锁的所有作弊分类

        基础分类(E/F/A/B/C)始终可用；
        C+(自定义棋子)需要 cheat_mastery_t1a；
        D(前端修改)需要 cheat_mastery_t1b。
        """
        allowed = {"E", "F", "A", "B", "C"}
        if self.is_skill_unlocked("cheat_mastery_t1a", 1):
            allowed.add("C+")
        if self.is_skill_unlocked("cheat_mastery_t1b", 1):
            allowed.add("D")
        return allowed

    def reset_on_detection(self):
        """被识破后不再重置进度，而是标记被识破状态。
        识破概率锁死在0，通关六道后触发天道Boss战。
        """
        self._data["detection_state"] = {
            "is_detected": True,
            "detection_locked": True,
            "trigger_boss_on_complete": True,
            "exposure_path_triggered": True,
        }
        for r in REALMS:
            self._data["realm_detections"][r] = 0.0
        self._save()

    # ══════════════════════════════════════════════════════════════
    # RPG 字段访问方法
    # ══════════════════════════════════════════════════════════════

    def get_alignment(self) -> dict:
        return self._data.setdefault("alignment", {
            "enlightenment": 0, "corruption": 0, "rationality": 0, "emotion": 0,
        })

    def add_alignment(self, effect: dict):
        """根据 effect dict 调整 alignment。
        支持 key: enlightenment/corruption/rationality/emotion/karma_delta
        """
        align = self.get_alignment()
        for key in ("enlightenment", "corruption", "rationality", "emotion"):
            if key in effect:
                align[key] = align.get(key, 0) + effect[key]
        # karma_delta 影响单局业力（人道第3关情感选择会增加业力）
        if "karma_delta" in effect and effect["karma_delta"]:
            self.increase_karma(effect["karma_delta"])
        self._save()

    def get_detection_state(self) -> dict:
        return self._data.setdefault("detection_state", {
            "is_detected": False,
            "detection_locked": False,
            "trigger_boss_on_complete": False,
            "exposure_path_triggered": False,
        })

    def is_detection_locked(self) -> bool:
        return self.get_detection_state().get("detection_locked", False)

    def is_exposure_path_triggered(self) -> bool:
        return self.get_detection_state().get("exposure_path_triggered", False)

    def get_memory_fragments_unlocked(self) -> dict:
        return self._data.setdefault("memory_fragments_unlocked", {
            r: False for r in REALMS
        })

    def unlock_memory_fragment(self, realm: str) -> bool:
        frags = self.get_memory_fragments_unlocked()
        if realm not in frags:
            return False
        if frags[realm]:
            return False  # 已解锁
        frags[realm] = True
        self._save()
        return True

    def get_choices_made(self) -> list:
        return self._data.setdefault("choices_made", [])

    def record_choice(self, choice_record: dict):
        self._data.setdefault("choices_made", []).append({
            "timestamp": datetime.now().isoformat(),
            **choice_record,
        })
        self._save()

    def get_prayer_count(self) -> int:
        return self._data.get("prayer_count", 0)

    def increment_prayer(self) -> int:
        self._data["prayer_count"] = self._data.get("prayer_count", 0) + 1
        # 一旦祈求过，标记识破路径（即使概率锁死0，结局路径已确定）
        ds = self.get_detection_state()
        if not ds.get("exposure_path_triggered"):
            ds["exposure_path_triggered"] = True
            ds["trigger_boss_on_complete"] = True
        self._save()
        return self._data["prayer_count"]

    def get_endings_unlocked(self) -> dict:
        return self._data.setdefault("endings_unlocked", {
            "enlightenment": False, "corruption": False, "samsara": False,
            "true_me": False, "exposed": False,
        })

    def unlock_ending(self, ending_id: str) -> bool:
        endings = self.get_endings_unlocked()
        if ending_id not in endings:
            return False
        if endings[ending_id]:
            return False
        endings[ending_id] = True
        self._save()
        return True

    def get_tiandao_boss_state(self) -> dict:
        return self._data.setdefault("tiandao_boss_state", {
            "defeated": False, "attempt_count": 0, "current_battle_active": False,
        })

    def update_tiandao_boss_state(self, **kwargs):
        s = self.get_tiandao_boss_state()
        s.update(kwargs)
        self._save()

    def get_story_progress(self) -> dict:
        return self._data.setdefault("story_progress", {
            "prologue_seen": False,
            "current_dialogue_realm": None,
            "current_dialogue_level": None,
            "last_choice_made": None,
        })

    def update_story_progress(self, **kwargs):
        p = self.get_story_progress()
        p.update(kwargs)
        self._save()

    def mark_realm_no_cheat_clear(self, realm: str):
        """标记某道全程无作弊通关（用于记忆碎片解锁条件）"""
        rp = self._data["realm_progress"].get(realm, {})
        rp["no_cheat_full_clear"] = True
        self._data["realm_progress"][realm] = rp
        self._save()

    def is_realm_no_cheat_clear(self, realm: str) -> bool:
        return self._data["realm_progress"].get(realm, {}).get("no_cheat_full_clear", False)

    def all_realms_completed(self) -> bool:
        """六道是否全部通关"""
        return all(
            self._data["realm_progress"].get(r, {}).get("completed", False)
            for r in REALMS
        )

    def all_memory_fragments_collected(self) -> bool:
        frags = self.get_memory_fragments_unlocked()
        return all(frags.get(r, False) for r in REALMS)

    def check_exposure_path(self) -> bool:
        """检查是否应该触发识破路径（六道通关 + 祈求≥1次）"""
        if not self.all_realms_completed():
            return False
        return self.get_prayer_count() >= 1

    def increment_playthrough(self):
        self._data["playthrough_count"] = self._data.get("playthrough_count", 1) + 1
        self._save()

    # ══════════════════════════════════════════════════════════════
    # 存档重置（软档 / 硬档）
    # ══════════════════════════════════════════════════════════════

    def reset_all(self, full: bool = False) -> None:
        """重置存档。

        full=False (soft): 保留技能、技能点、已解锁结局、记忆碎片、成就类存档字段，
                          只清空当前进度（道 / 关卡 / 业力 / alignment / 识破 /
                          溢出叠加 / 关卡计数 / 选择历史 / Boss 状态 / 剧情进度）。
                          playthrough_count 自增 1，其它保持。
        full=True  (hard): 所有字段恢复到默认值（只保留 version 不变，
                           备份旧存档为 .bak 后缀）。

        无论软硬档：写 STATE_FILE.bak 作为回滚备份，完成后 reset_level_state 重置
        单局业力等字段。
        """
        # 写备份：整文件备份
        try:
            bak = STATE_FILE.with_suffix(STATE_FILE.suffix + ".bak")
            if STATE_FILE.exists():
                bak.write_bytes(STATE_FILE.read_bytes())
        except OSError:
            pass  # 备份失败不应阻止重置

        if full:
            # 硬档：先默认结构，再把 version 设成原值，再调用 _init_defaults 补迁移
            version = self._data.get("version", 3)
            self._data = {"version": version}
            self._init_defaults()
            self.reset_level_state()
            self._save()
            return

        # soft 档：需保留的字段
        keep_keys = {
            "version", "skills", "skill_points", "karma_max", "karma_single_max",
            "initial_karma", "endings_unlocked", "memory_fragments_unlocked",
            "sandbox_unlocked",
        }
        keep_sub = {
            "skills": copy.deepcopy(self._data.get("skills", {})),
            "skill_points": self._data.get("skill_points", 0),
            "karma_max": self._data.get("karma_max", 120),
            "karma_single_max": self._data.get("karma_single_max", 120),
            "initial_karma": self._data.get("initial_karma", 50),
            "endings_unlocked": copy.deepcopy(
                self._data.get("endings_unlocked", {
                    "enlightenment": False, "corruption": False, "samsara": False,
                    "true_me": False, "exposed": False,
                })
            ),
            "memory_fragments_unlocked": copy.deepcopy(
                self._data.get("memory_fragments_unlocked", {r: False for r in REALMS})
            ),
            "sandbox_unlocked": list(self._data.get("sandbox_unlocked", [])),
            "_playthrough_count": self._data.get("playthrough_count", 1),
        }
        version = self._data.get("version", 3)
        # 清空再默认化
        self._data = {"version": version}
        self._init_defaults()
        # 恢复保留字段
        for k, v in keep_sub.items():
            if k in ("skills", "_playthrough_count"):
                continue
            self._data[k] = v
        # skills 合并：保留字段覆盖默认（默认含 tier1 起始技能）
        merged_skills = {**self._data.get("skills", {}), **keep_sub["skills"]}
        self._data["skills"] = merged_skills
        # 进度字段明确归零（playthrough_count = 原值 + 1）
        self._data["current_realm"] = "hell"
        self._data["current_level"] = 0
        self._data["realm_overshoot_carryover"] = 0
        self._data["playthrough_count"] = keep_sub["_playthrough_count"] + 1
        self._data["alignment"] = {
            "enlightenment": 0, "corruption": 0, "rationality": 0, "emotion": 0,
        }
        self._data["detection_state"] = {
            "is_detected": False, "detection_locked": False,
            "trigger_boss_on_complete": False, "exposure_path_triggered": False,
        }
        self._data["realm_detections"] = {r: 0.0 for r in REALMS}
        self._data["choices_made"] = []
        self._data["total_levels_completed"] = 0
        self._data["bosses_defeated"] = []
        self._data["prayer_count"] = 0
        self._data["tiandao_boss_state"] = {
            "defeated": False, "attempt_count": 0, "current_battle_active": False,
        }
        self._data["story_progress"] = {
            "prologue_seen": False, "current_dialogue_realm": None,
            "current_dialogue_level": None, "last_choice_made": None,
        }
        self._data["realm_progress"] = {
            r: {"completed": False, "levels_passed": 0, "no_cheat_full_clear": False}
            for r in REALMS
        }
        self.reset_level_state()
        self._save()

    def get_frontend_state(self) -> dict:
        """返回总坛 / 棋类前端共同需要的精简状态字段。"""
        modifiers = self.get_skill_modifiers()
        allowed = sorted(self.get_allowed_classifications())
        karma_max = self._data.get("karma_max", 120) + modifiers.get("karma_max_bonus", 0)
        karma_single_max = self._data.get("karma_single_max", 120) + modifiers.get("karma_single_max_bonus", 0)
        return {
            "karma": self._data.get("level_karma", 0),
            "karma_max": karma_max,
            "karma_single_max": karma_single_max,
            "detection": self.get_detection(),
            "realm_detections": {r: self.get_realm_detection(r) for r in REALMS},
            "current_realm": self._data.get("current_realm", "hell"),
            "current_realm_name": REALM_NAMES.get(self._data.get("current_realm", "hell"), "地狱道"),
            "current_level": self._data.get("current_level", 0),
            "level_turn_limit": self._data.get("turn_limit", 20),
            "current_turn": self._data.get("current_turn", 0),
            "total_levels_completed": self._data.get("total_levels_completed", 0),
            "skill_points": self._data.get("skill_points", 0),
            "no_cheat_this_level": self._data.get("no_cheat_this_level", True),
            "cheat_count": self._data.get("cheat_count", 0),
            "overdraft_count": self._data.get("overdraft_count", 0),
            "sandbox_mode": self._data.get("sandbox_mode", False),
            "allowed_classifications": allowed,
            "skill_modifiers": modifiers,
            "alignment": self.get_alignment(),
            "detection_state": self.get_detection_state(),
            "memory_fragments_unlocked": self.get_memory_fragments_unlocked(),
            "endings_unlocked": self.get_endings_unlocked(),
            "realm_progress": self._data.get("realm_progress", {}),
            "sandbox_unlocked": list(self._data.get("sandbox_unlocked", [])),
            "prayer_count": self.get_prayer_count(),
            "version": self._data.get("version", 3),
        }
