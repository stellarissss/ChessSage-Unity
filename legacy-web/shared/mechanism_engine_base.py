"""
机制引擎 —— 共享基类（MechanismEngineBase）。

────────────────────────────────────────────────────────────────────
【本文件的由来 —— 复用逻辑说明】

    本模块是从 12 份 mechanism_engine.py（顶层 6 棋类 + sandbox 6 副本）
    中提取的公共基类。这些文件经 difflib 量化，顶层 6 份平均相似度
    96.4%，且 sandbox 副本与顶层同名文件 100% 相同（diff 为 0）。

    5 种机制原语（skip_turns / ai_control / random_moves / extra_turns /
    move_limits）及 AI 性格系统的实现对所有棋类完全一致；真正的差异
    只集中在「阵营命名」与「玩家控制语义」上：

        1. 阵营颜色命名不同：
           象棋/动物棋/跳棋 用 red/black（红方/黑方），
           围棋/五子棋/黑白棋 用 black/white（黑方/白方）。
           → 参数化为类属性 SIDE_LABELS（颜色 → 中文标签映射）。
        2. 默认由玩家控制的一方不同：
           象棋/动物棋默认红方玩家控制，围棋/跳棋/黑白棋默认黑方。
           → 参数化为类属性 DEFAULT_PLAYER_SIDE。
        3. player_control 机制的前端摘要文案不同：
           五子棋用「玩家接管」+ 👥 + 显示剩余回合，
           其余棋类用「玩家控制」+ 🎮 + 不显示剩余。
           → 参数化为 PC_REASON_DEFAULT / PC_ICON / PC_SHOW_REMAINING。
        4. 五子棋的 is_player_controlled 语义完全不同于其他棋类
           （无「默认玩家方」概念，仅当 player_control 机制激活才为真，
            是 is_ai_controlled 的对称）。→ 由子类整体覆盖该方法。
        5. 围棋独有 decrement_player_control()（消耗一次玩家控制机制）。
           → 直接上移为基类方法（其余棋类继承但不调用，行为不变）。

    因此各棋类的 mechanism_engine.py 现在只需继承本基类并声明
    SIDE_LABELS / DEFAULT_PLAYER_SIDE 等类属性即可复用全部逻辑。

【子类契约】
    1. 声明 SIDE_LABELS（颜色 → 中文标签）与 DEFAULT_PLAYER_SIDE。
    2. 五子棋需覆盖 is_player_controlled() 与 player_control 摘要参数。
    3. 可选覆盖 side_label() 以做特殊兼容（如黑白棋把 red 视为黑方）。

【行为等价性约束】
    基类所有方法签名、默认值、返回结构与原 xiangqi 版本逐行等价；
    差异点全部抽为可覆盖的类属性 / 方法，任何棋类对外行为不得改变。
────────────────────────────────────────────────────────────────────
"""
from __future__ import annotations

import copy
import random
from typing import Any, Dict, List, Optional, Tuple


class MechanismEngineBase:
    """机制引擎共享基类，负责执行游戏机制原语。"""

    # ────────────────────────────────────────────────────────────────
    # 子类需声明的棋类参数
    # ────────────────────────────────────────────────────────────────

    # 阵营颜色 → 中文标签映射（用于前端摘要显示）。
    SIDE_LABELS: Dict[str, str] = {
        "red": "红方",
        "black": "黑方",
        "both": "双方",
    }

    # 无 player_control 机制时，默认由玩家控制的一方。
    DEFAULT_PLAYER_SIDE: str = "red"

    # player_control 机制的前端摘要参数（五子棋覆盖为不同文案）。
    PC_REASON_DEFAULT: str = "玩家控制"
    PC_ICON: str = "🎮"
    PC_SHOW_REMAINING: bool = False

    def __init__(self, rules_config: dict = None):
        self.rules_config = rules_config or {}

    def update_rules(self, rules_config: dict):
        """更新规则配置"""
        self.rules_config = rules_config

    def side_label(self, side: str) -> str:
        """返回普通机制（skip/ai/random/extra/move_limits）阵营的中文标签。

        各棋类可覆盖以精确复现原实现（如象棋「红方 if red else 黑方」）。
        """
        return self.SIDE_LABELS.get(side, side)

    def player_control_label(self, side: str) -> str:
        """返回 player_control 机制阵营的中文标签。

        独立于 side_label：部分棋类（如象棋）对 player_control 使用
        含「双方」的完整映射，而普通机制用二值映射。
        """
        return self.SIDE_LABELS.get(side, side)

    def _get_mechanisms(self, board_state: dict) -> dict:
        """获取 mechanisms 字段，确保存在且所有原语键均为列表。"""
        if "mechanisms" not in board_state or not isinstance(board_state["mechanisms"], dict):
            board_state["mechanisms"] = {
                "skip_turns": [],
                "ai_control": [],
                "random_moves": [],
                "extra_turns": [],
                "move_limits": [],
                "player_control": [],
            }
        mech = board_state["mechanisms"]
        for key in ["skip_turns", "ai_control", "random_moves", "extra_turns", "move_limits", "player_control"]:
            if key not in mech or not isinstance(mech[key], list):
                mech[key] = []
        return mech

    # ═══════════════════════════════════════════════════════════════
    # 回合开始前的机制处理
    # ═══════════════════════════════════════════════════════════════

    def apply_pre_turn_mechanisms(self, board_state: dict, side: str) -> Tuple[dict, Dict[str, Any]]:
        """
        回合开始前应用机制

        Returns:
            (board_state, info)
            info 包含：
            - skipped: bool - 本回合是否被跳过
            - skip_reason: str - 跳过原因
            - ai_controlled: bool - 本回合是否由AI控制
        """
        mech = self._get_mechanisms(board_state)
        info = {
            "skipped": False,
            "skip_reason": None,
            "ai_controlled": False,
            "ai_control_reason": None,
        }

        # 1. 检查跳过回合
        skip_idx = self._find_active_mechanism(mech["skip_turns"], side)
        if skip_idx is not None:
            info["skipped"] = True
            info["skip_reason"] = mech["skip_turns"][skip_idx].get("reason", "冻结效果")
            remaining = mech["skip_turns"][skip_idx]["remaining"]
            if remaining > 0:
                mech["skip_turns"][skip_idx]["remaining"] -= 1
                if mech["skip_turns"][skip_idx]["remaining"] <= 0:
                    mech["skip_turns"].pop(skip_idx)

        # 2. 检查AI接管（只有未被跳过时才检查）
        if not info["skipped"]:
            ai_idx = self._find_active_mechanism(mech["ai_control"], side)
            if ai_idx is not None:
                info["ai_controlled"] = True
                info["ai_control_reason"] = mech["ai_control"][ai_idx].get("reason", "AI接管中")

        # 3. 初始化move_limits的remaining_moves
        limit_idx = self._find_active_mechanism(mech["move_limits"], side)
        if limit_idx is not None:
            mech["move_limits"][limit_idx]["remaining_moves"] = mech["move_limits"][limit_idx]["limit"]

        return board_state, info

    # ═══════════════════════════════════════════════════════════════
    # 走棋后的机制处理
    # ═══════════════════════════════════════════════════════════════

    def apply_post_move_mechanisms(self, board_state: dict, side: str) -> Tuple[dict, Dict[str, Any]]:
        """
        走棋后应用机制

        Returns:
            (board_state, info)
            info 包含：
            - switch_turn: bool - 是否应该切换回合
            - consumed_random_move: bool - 是否消耗了一次随机走棋
            - extra_turn_granted: bool - 是否获得了额外回合
            - moves_remaining: int - 本回合剩余可走步数
        """
        mech = self._get_mechanisms(board_state)
        info = {
            "switch_turn": True,
            "consumed_random_move": False,
            "extra_turn_granted": False,
            "moves_remaining": 0,
        }

        # 1. 消耗随机走棋次数
        rand_idx = self._find_active_mechanism(mech["random_moves"], side)
        if rand_idx is not None:
            remaining = mech["random_moves"][rand_idx]["remaining"]
            if remaining > 0:
                mech["random_moves"][rand_idx]["remaining"] -= 1
                if mech["random_moves"][rand_idx]["remaining"] <= 0:
                    mech["random_moves"].pop(rand_idx)
            info["consumed_random_move"] = True

        # 2. 检查走棋步数限制
        limit_idx = self._find_active_mechanism(mech["move_limits"], side)
        if limit_idx is not None:
            if "remaining_moves" in mech["move_limits"][limit_idx]:
                mech["move_limits"][limit_idx]["remaining_moves"] -= 1
                remaining = mech["move_limits"][limit_idx]["remaining_moves"]
                info["moves_remaining"] = remaining
                if remaining > 0:
                    info["switch_turn"] = False
                else:
                    mech["move_limits"].pop(limit_idx)

        # 3. 检查额外回合（只有非move_limits模式下才检查）
        if info["switch_turn"]:
            extra_idx = self._find_active_mechanism(mech["extra_turns"], side)
            if extra_idx is not None:
                remaining = mech["extra_turns"][extra_idx]["remaining"]
                if remaining > 0:
                    mech["extra_turns"][extra_idx]["remaining"] -= 1
                    if mech["extra_turns"][extra_idx]["remaining"] <= 0:
                        mech["extra_turns"].pop(extra_idx)
                info["extra_turn_granted"] = True
                info["switch_turn"] = False

        # 4. 消耗AI接管次数（回合结束时消耗一次）
        if info["switch_turn"]:
            ai_idx = self._find_active_mechanism(mech["ai_control"], side)
            if ai_idx is not None:
                remaining = mech["ai_control"][ai_idx]["remaining"]
                if remaining > 0:
                    mech["ai_control"][ai_idx]["remaining"] -= 1
                    if mech["ai_control"][ai_idx]["remaining"] <= 0:
                        mech["ai_control"].pop(ai_idx)

        return board_state, info

    # ═══════════════════════════════════════════════════════════════
    # 状态查询方法
    # ═══════════════════════════════════════════════════════════════

    def is_ai_controlled(self, board_state: dict, side: str) -> bool:
        """判断某方当前回合是否由AI控制"""
        mech = self._get_mechanisms(board_state)
        return self._find_active_mechanism(mech["ai_control"], side) is not None

    def is_player_controlled(self, board_state: dict, side: str) -> bool:
        """判断某方是否由玩家控制。

        默认行为：DEFAULT_PLAYER_SIDE 一方由玩家控制，另一方由 AI 控制；
        通过 player_control 机制可以改变这个默认行为。
        （五子棋覆盖此方法，采用「仅机制激活才为玩家控制」的对称语义。）
        """
        mech = self._get_mechanisms(board_state)
        pc_list = mech.get("player_control", [])

        if not pc_list:
            return side == self.DEFAULT_PLAYER_SIDE

        for item in pc_list:
            controlled_side = item.get("side", self.DEFAULT_PLAYER_SIDE)
            if controlled_side == "both":
                return True
            if controlled_side == side:
                return True

        return False

    def is_random_move_required(self, board_state: dict, side: str) -> bool:
        """判断某方当前步是否需要随机走棋"""
        mech = self._get_mechanisms(board_state)
        return self._find_active_mechanism(mech["random_moves"], side) is not None

    def should_skip_turn(self, board_state: dict, side: str) -> bool:
        """判断某方是否应该跳过下一回合"""
        mech = self._get_mechanisms(board_state)
        return self._find_active_mechanism(mech["skip_turns"], side) is not None

    def get_active_mechanisms_summary(self, board_state: dict) -> List[str]:
        """获取当前激活的机制列表（用于前端显示）。"""
        mech = self._get_mechanisms(board_state)
        summary = []

        for item in mech["skip_turns"]:
            side_label = self.side_label(item["side"])
            reason = item.get("reason", "冻结")
            remaining = item.get("remaining", 0)
            remaining_str = "无限" if remaining < 0 else f"剩{remaining}回合"
            summary.append(f"⏸️ {side_label}{reason}（{remaining_str}）")

        for item in mech["ai_control"]:
            side_label = self.side_label(item["side"])
            reason = item.get("reason", "AI接管")
            remaining = item.get("remaining", 0)
            remaining_str = "无限" if remaining < 0 else f"剩{remaining}回合"
            summary.append(f"🤖 {side_label}{reason}（{remaining_str}）")

        for item in mech["player_control"]:
            side_label = self.player_control_label(item.get("side", self.DEFAULT_PLAYER_SIDE))
            reason = item.get("reason", self.PC_REASON_DEFAULT)
            if self.PC_SHOW_REMAINING:
                remaining = item.get("remaining", 0)
                remaining_str = "无限" if remaining < 0 else f"剩{remaining}回合"
                summary.append(f"{self.PC_ICON} {side_label}{reason}（{remaining_str}）")
            else:
                summary.append(f"{self.PC_ICON} {side_label}{reason}")

        for item in mech["random_moves"]:
            side_label = self.side_label(item["side"])
            reason = item.get("reason", "随机走棋")
            remaining = item.get("remaining", 0)
            remaining_str = "无限" if remaining < 0 else f"剩{remaining}步"
            summary.append(f"🎲 {side_label}{reason}（{remaining_str}）")

        for item in mech["extra_turns"]:
            side_label = self.side_label(item["side"])
            reason = item.get("reason", "额外回合")
            remaining = item.get("remaining", 0)
            remaining_str = "无限" if remaining < 0 else f"剩{remaining}回合"
            summary.append(f"⚡ {side_label}{reason}（{remaining_str}）")

        for item in mech["move_limits"]:
            side_label = self.side_label(item["side"])
            summary.append(f"🚶 {side_label}每回合{item['limit']}步")

        return summary

    # ═══════════════════════════════════════════════════════════════
    # AI性格系统
    # ═══════════════════════════════════════════════════════════════

    def get_personality_config(self) -> dict:
        """获取AI性格配置"""
        ai_diff = self.rules_config.get("ai_difficulty", {})
        personality = ai_diff.get("personality", {})
        if not personality:
            personality = {
                "type": "normal",
                "aggressiveness": 0.5,
                "conservatism": 0.5,
                "randomness_override": None,
                "depth_override": None,
                "value_biases": {},
                "custom_prompt": None,
            }
        return personality

    def apply_personality_to_ai(self, ai_instance) -> None:
        """将性格配置应用到 AI 实例（修改搜索深度、随机度、评估权重等）。"""
        personality = self.get_personality_config()
        p_type = personality.get("type", "normal")

        # 预设性格参数
        presets = {
            "normal": {
                "depth_mult": 1.0,
                "randomness_mult": 1.0,
                "aggressiveness": 0.5,
                "conservatism": 0.5,
            },
            "aggressive": {
                "depth_mult": 1.0,
                "randomness_mult": 0.8,
                "aggressiveness": 0.9,
                "conservatism": 0.2,
            },
            "defensive": {
                "depth_mult": 1.2,
                "randomness_mult": 0.5,
                "aggressiveness": 0.2,
                "conservatism": 0.9,
            },
            "random": {
                "depth_mult": 0.5,
                "randomness_mult": 3.0,
                "aggressiveness": 0.5,
                "conservatism": 0.5,
            },
        }

        preset = presets.get(p_type, presets["normal"])

        # 性格参数策略：
        # - custom类型：完全使用personality中的自定义值
        # - 预设类型：使用预设的aggressiveness/conservatism，但value_biases等扩展字段仍从personality读取
        if p_type == "custom":
            agg = personality.get("aggressiveness", 0.5)
            cons = personality.get("conservatism", 0.5)
        else:
            agg = preset["aggressiveness"]
            cons = preset["conservatism"]

        # 覆盖搜索深度
        depth_override = personality.get("depth_override")
        if depth_override is not None:
            ai_instance.depth = max(1, int(depth_override))
        else:
            original_depth = ai_instance.depth
            ai_instance.depth = max(1, int(original_depth * preset["depth_mult"]))

        # 覆盖随机度
        randomness_override = personality.get("randomness_override")
        if randomness_override is not None:
            ai_instance.randomness = max(0.0, min(1.0, float(randomness_override)))
        else:
            original_randomness = ai_instance.randomness
            ai_instance.randomness = max(0.0, min(1.0, original_randomness * preset["randomness_mult"]))

        # 存储性格参数到AI实例（供_evaluate使用）
        ai_instance.personality_aggressiveness = agg
        ai_instance.personality_conservatism = cons
        ai_instance.personality_value_biases = personality.get("value_biases", {})

    # ═══════════════════════════════════════════════════════════════
    # 辅助方法
    # ═══════════════════════════════════════════════════════════════

    def _find_active_mechanism(self, mechanism_list: List[dict], side: str) -> Optional[int]:
        """查找某方激活的机制，返回索引，找不到返回None
        remaining != 0 即视为激活（正数表示剩余次数，负数表示无限）
        """
        for i, item in enumerate(mechanism_list):
            if item.get("side") == side and item.get("remaining", 0) != 0:
                return i
        return None

    def add_mechanism(self, board_state: dict, mechanism_type: str, side: str,
                      remaining: int, reason: str = "") -> dict:
        """
        添加机制（便捷方法，也可以直接由AI通过JSON Patch操作）

        Args:
            mechanism_type: skip_turns | ai_control | random_moves | extra_turns
            side: 该棋类阵营颜色（red/black 或 black/white）
            remaining: 剩余回合/步数
            reason: 原因说明
        """
        mech = self._get_mechanisms(board_state)
        if mechanism_type not in mech:
            mech[mechanism_type] = []
        mech[mechanism_type].append({
            "side": side,
            "remaining": remaining,
            "reason": reason,
        })
        return board_state


__all__ = ["MechanismEngineBase"]
