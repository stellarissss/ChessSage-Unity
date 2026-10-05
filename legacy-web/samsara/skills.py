import json
from pathlib import Path
from .state import SamsaraState

BASE_DIR = Path(__file__).resolve().parent.parent
CONFIGS_DIR = BASE_DIR / "configs"
SKILL_TREE_FILE = CONFIGS_DIR / "skill_tree.json"


class SkillSystem:
    def __init__(self, state: SamsaraState):
        self.state = state
        self.skill_tree = self._load_skill_tree()

    def _load_skill_tree(self):
        if SKILL_TREE_FILE.exists():
            try:
                return json.loads(SKILL_TREE_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return self._get_default_skill_tree()

    def _get_default_skill_tree(self):
        return {
            "branches": {
                "karma_capacity": {
                    "name": "业力掌控",
                    "description": "提升业力安全阈值与消业效率",
                    "icon": "💫",
                    "tiers": {
                        1: {
                            "id": "karma_capacity_t1",
                            "name": "安全阈值+20",
                            "description": "业力安全阈值+20（120→140）",
                            "cost": 1,
                        },
                        2: {
                            "options": [
                                {
                                    "id": "karma_capacity_t2a",
                                    "name": "单次上限+30",
                                    "description": "单次作弊业力增加上限+30",
                                    "cost": 1,
                                },
                                {
                                    "id": "karma_capacity_t2b",
                                    "name": "业力潮汐",
                                    "description": "所有消业事件+30%",
                                    "cost": 1,
                                },
                            ],
                        },
                        3: {
                            "options": [
                                {
                                    "id": "karma_capacity_t3a",
                                    "name": "缓冲",
                                    "description": "识破非线性指数从1.5降至1.3",
                                    "cost": 1,
                                },
                                {
                                    "id": "karma_capacity_t3b",
                                    "name": "净身",
                                    "description": "初始业力从50降至25",
                                    "cost": 1,
                                },
                            ],
                        },
                    },
                },
                "stealth": {
                    "name": "隐匿之术",
                    "description": "降低识破风险，提升逃跑能力",
                    "icon": "👻",
                    "tiers": {
                        1: {
                            "id": "stealth_t1",
                            "name": "藏锋",
                            "description": "识破惩罚系数从0.1降至0.07",
                            "cost": 1,
                        },
                        2: {
                            "options": [
                                {
                                    "id": "stealth_t2a",
                                    "name": "首次透支免判",
                                    "description": "每局第一次透支的识破判定跳过",
                                    "cost": 1,
                                },
                                {
                                    "id": "stealth_t2b",
                                    "name": "连续规避",
                                    "description": "连续3回合不作弊后，下次透支惩罚减半",
                                    "cost": 1,
                                },
                            ],
                        },
                        3: {
                            "options": [
                                {
                                    "id": "stealth_t3a",
                                    "name": "金蝉脱壳",
                                    "description": "被识破后1次复活（识破概率回退到触发前的50%）",
                                    "cost": 1,
                                },
                                {
                                    "id": "stealth_t3b",
                                    "name": "迷雾",
                                    "description": "识破概率>70%后，每次结算有30%概率Δ=0",
                                    "cost": 1,
                                },
                            ],
                        },
                    },
                },
                "cheat_mastery": {
                    "name": "作弊精通",
                    "description": "解锁高级作弊能力，降低作弊消耗",
                    "icon": "🎲",
                    "tiers": {
                        1: {
                            "options": [
                                {
                                    "id": "cheat_mastery_t1a",
                                    "name": "自定义棋子",
                                    "description": "允许C+类作弊",
                                    "cost": 1,
                                },
                                {
                                    "id": "cheat_mastery_t1b",
                                    "name": "前端修改",
                                    "description": "允许D类作弊",
                                    "cost": 1,
                                },
                            ],
                        },
                        2: {
                            "options": [
                                {
                                    "id": "cheat_mastery_t2a",
                                    "name": "效率欺诈",
                                    "description": "AI评估有30%概率降1档",
                                    "cost": 1,
                                },
                                {
                                    "id": "cheat_mastery_t2b",
                                    "name": "高级规则",
                                    "description": "允许修改胜利条件/核心规则",
                                    "cost": 1,
                                },
                            ],
                        },
                        3: {
                            "options": [
                                {
                                    "id": "cheat_mastery_t3a",
                                    "name": "白嫖",
                                    "description": "每局1次：不消耗业力（透支时仍有惩罚）",
                                    "cost": 1,
                                },
                                {
                                    "id": "cheat_mastery_t3b",
                                    "name": "深层作弊",
                                    "description": "单次作弊业力增加上限+40",
                                    "cost": 1,
                                },
                            ],
                        },
                    },
                },
                "realm_insight": {
                    "name": "六道悟道",
                    "description": "针对特定道的特殊能力",
                    "icon": "🔮",
                    "tiers": {
                        1: {
                            "options": [
                                {
                                    "id": "realm_insight_t1a",
                                    "name": "地狱之眼",
                                    "description": "地狱道/饿鬼道作弊业力消耗-25%",
                                    "cost": 1,
                                },
                                {
                                    "id": "realm_insight_t1b",
                                    "name": "天道之耳",
                                    "description": "天道/阿修罗道透支惩罚-25%",
                                    "cost": 1,
                                },
                            ],
                        },
                        2: {
                            "options": [
                                {
                                    "id": "realm_insight_t2a",
                                    "name": "守道者之隙",
                                    "description": "Boss技能触发概率-30%",
                                    "cost": 1,
                                },
                                {
                                    "id": "realm_insight_t2b",
                                    "name": "轮回记忆",
                                    "description": "每次轮回开局自带1个随机临时buff",
                                    "cost": 1,
                                },
                            ],
                        },
                        3: {
                            "options": [
                                {
                                    "id": "realm_insight_t3a",
                                    "name": "超脱之种",
                                    "description": "清业通关后，全局透支惩罚系数永久-0.05",
                                    "cost": 1,
                                },
                                {
                                    "id": "realm_insight_t3b",
                                    "name": "六道轮转",
                                    "description": "升降道时，额外1次免费作弊机会",
                                    "cost": 1,
                                },
                            ],
                        },
                    },
                },
            }
        }

    def get_skill_tree(self):
        result = {}
        for branch_id, branch in self.skill_tree["branches"].items():
            branch_data = {
                "name": branch["name"],
                "description": branch["description"],
                "icon": branch["icon"],
                "tiers": {},
            }
            for tier, tier_data in branch["tiers"].items():
                if "options" in tier_data:
                    tier_options = []
                    for opt in tier_data["options"]:
                        tier_options.append({
                            **opt,
                            "unlocked": self.state.is_skill_unlocked(opt["id"], tier),
                        })
                    branch_data["tiers"][tier] = {"options": tier_options}
                else:
                    branch_data["tiers"][tier] = {
                        **tier_data,
                        "unlocked": self.state.is_skill_unlocked(tier_data["id"], tier),
                    }
            result[branch_id] = branch_data
        return result

    def unlock_skill(self, skill_id, tier) -> bool:
        # 前端可能传字符串，统一转为 int
        try:
            tier = int(tier)
        except (TypeError, ValueError):
            return False
        branch_id = skill_id.split("_t")[0]
        branch = self.skill_tree["branches"].get(branch_id)
        if not branch:
            return False
        # 幂等短路：已解锁的技能直接拒绝，避免重复扣点
        # （下方 spend 成功后 unlock_skill 会因技能已存在返回 False）
        if self.state.is_skill_unlocked(skill_id):
            return False
        tier_key = str(tier)
        if tier > 1:
            prev_tier_key = str(tier - 1)
            prev_skill = f"{branch_id}_t{tier-1}"
            if not self.state.is_skill_unlocked(prev_skill, tier-1):
                prev_tier = branch["tiers"].get(prev_tier_key)
                if prev_tier and "options" in prev_tier:
                    has_prev = False
                    for opt in prev_tier["options"]:
                        if self.state.is_skill_unlocked(opt["id"], tier-1):
                            has_prev = True
                            break
                    if not has_prev:
                        return False
                else:
                    return False
        tier_data = branch["tiers"].get(tier_key)
        if tier_data:
            if "options" in tier_data:
                for opt in tier_data["options"]:
                    if opt["id"] == skill_id:
                        cost = opt["cost"]
                        if self.state.spend_skill_point(cost):
                            if self.state.unlock_skill(skill_id, tier):
                                return True
                            # 防御性回滚：扣点成功但解锁未生效时退还
                            self.state.refund_skill_point(cost)
                        return False
            else:
                if tier_data["id"] == skill_id:
                    cost = tier_data["cost"]
                    if self.state.spend_skill_point(cost):
                        if self.state.unlock_skill(skill_id, tier):
                            return True
                        # 防御性回滚：扣点成功但解锁未生效时退还
                        self.state.refund_skill_point(cost)
        return False

    def get_available_skills(self):
        available = []
        for branch_id, branch in self.skill_tree["branches"].items():
            for tier, tier_data in branch["tiers"].items():
                if "options" in tier_data:
                    for opt in tier_data["options"]:
                        if not self.state.is_skill_unlocked(opt["id"], tier):
                            available.append({
                                "id": opt["id"],
                                "branch": branch_id,
                                "branch_name": branch["name"],
                                "tier": tier,
                                "name": opt["name"],
                                "description": opt["description"],
                                "cost": opt["cost"],
                            })
                else:
                    if not self.state.is_skill_unlocked(tier_data["id"], tier):
                        available.append({
                            "id": tier_data["id"],
                            "branch": branch_id,
                            "branch_name": branch["name"],
                            "tier": tier,
                            "name": tier_data["name"],
                            "description": tier_data["description"],
                            "cost": tier_data["cost"],
                        })
        return available