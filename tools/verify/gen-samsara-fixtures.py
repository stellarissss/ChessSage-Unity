#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Samsara 黄金夹具生成器（M5-B）。

用途
====
把 legacy-web/samsara 的 Python 实现（与 ChessSage.Core.Samsara 逐行对齐的原版）
作为"权威引擎"，导出期望值到 Assets/ChessSage/Tests/Fixtures/samsara_*.json，
供 C# 侧 dotnet 测试逐字段比对（引擎外黄金等价性验证）。

设计约束
========
1. **无副作用 / 幂等**：Python 的 SamsaraState 默认会把状态写回
   legacy-web/configs/samsara_state.json。本脚本在每次构造前把
   samsara.state.STATE_FILE 重定向到 tempfile 目录，绝不触碰仓库内任何配置；
   重复运行结果完全一致（已对所有随机源固定种子，且仅走确定性分支）。
2. **可复跑**：不依赖 Unity、不依赖网络；仅需 Python 3.8+ 与 legacy-web 源码
   （httpx 用于 shared.karma_assessor_base 的 import，已安装）。
3. **单源真源**：配置来自 legacy-web/configs/，与
   Assets/StreamingAssets/Configs/global/ 逐字节一致（已核验）；C# 读取后者。
4. **推导依据**：凡标注 "derivation" 的字段，均在注释中写明来自哪条公式/规则，
   不通过调用"实现自身的同一函数"来生成期望值（公式用例另用 Python 原版函数交叉校验）。

运行
====
    python3 tools/verify/gen-samsara-fixtures.py
"""
from __future__ import annotations

import copy
import json
import random
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEGACY = ROOT / "legacy-web"
OUT_DIR = ROOT / "Assets" / "ChessSage" / "Tests" / "Fixtures"
sys.path.insert(0, str(LEGACY))

import samsara.state as state_mod  # noqa: E402
from samsara.state import SamsaraState, REALMS, normalize_skill_key  # noqa: E402
from samsara.karma import KarmaSystem  # noqa: E402
from samsara.skills import SkillSystem  # noqa: E402
from samsara.levels import LevelSystem  # noqa: E402
from samsara.progression import ProgressionSystem  # noqa: E402
from samsara.endings import EndingSystem  # noqa: E402
from samsara.bosses import BossSystem  # noqa: E402
from samsara.heaven_boss import HeavenBossSystem  # noqa: E402
from samsara.detection import DetectionSystem  # noqa: E402
from samsara.objectives import ObjectiveSystem  # noqa: E402
from samsara.choices import ChoiceSystem  # noqa: E402
from samsara.memory_fragments import MemoryFragmentSystem  # noqa: E402
from samsara.turn_limit import TurnLimitSystem  # noqa: E402
import shared.karma_assessor_base as kab  # noqa: E402

random.seed(20260101)

_TS = "2026-01-01T00:00:00"

_TMPDIRS = []


def _write_temp_state(data: dict) -> Path:
    d = Path(tempfile.mkdtemp(prefix="samsara_gen_"))
    _TMPDIRS.append(d)
    f = d / "state.json"
    f.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    return f


def _meta() -> dict:
    return {"unlocked_at": _TS, "tier": 1}


def new_state(seed: dict = None, skills=None, skill_points=None,
              used_one_time=None, level_karma=None, realm=None) -> SamsaraState:
    """构造隔离的 SamsaraState（写入 temp 文件，绝不污染仓库配置）。"""
    data = {"version": 4} if seed is None else copy.deepcopy(seed)
    state_mod.STATE_FILE = _write_temp_state(data)
    st = SamsaraState()
    if skills is not None:
        st._data["skills"] = {sid: _meta() for sid in skills}
    if skill_points is not None:
        st._data["skill_points"] = skill_points
    if used_one_time is not None:
        st._data["one_time_skill_usage"] = {
            sid: {"used_at": _TS, "context": "test"} for sid in used_one_time
        }
    if level_karma is not None:
        st._data["level_karma"] = level_karma
    if realm is not None:
        st._data["current_realm"] = realm
    return st


def _dump(name: str, obj: dict) -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    path = OUT_DIR / f"{name}.json"
    text = json.dumps(obj, ensure_ascii=False, indent=2)
    path.write_text(text + "\n", encoding="utf-8")
    return len(text.encode("utf-8"))


# ════════════════════════════════════════════════════════════════════════════
# 1) 业力：KarmaSystem（状态机）+ KarmaAssessorBase（本地镜像）
# ════════════════════════════════════════════════════════════════════════════

def gen_karma() -> dict:
    recover_cases = []

    def recover(name, game_type, event_type, skills=None, karma_before=50):
        st = new_state(skills=skills if skills is not None else [], level_karma=karma_before)
        ks = KarmaSystem(st)
        base = ks.events.get(game_type, {}).get(event_type, 0)
        amt = ks.recover(game_type, event_type)
        recover_cases.append({
            "name": name, "game_type": game_type, "event_type": event_type,
            "skills": skills or [], "karma_before": karma_before,
            "base_amount": base, "expected_amount": amt,
            "expected_karma_after": st.get_karma(),
        })

    recover("xiangqi_capture_rook", "xiangqi", "capture_rook")
    recover("heibaiqi_flip_small", "heibaiqi", "flip_small")
    recover("multiplier_1_3", "xiangqi", "capture_pawn",
            skills=["stealth_t1", "karma_capacity_t1", "karma_capacity_t2b"])
    recover("clamp_at_zero", "xiangqi", "capture_rook", karma_before=5)
    recover("unknown_event_zero", "xiangqi", "does_not_exist")
    recover("unknown_game_zero", "nope", "capture_rook")

    consume_cases = []

    def consume(name, amount, skills=None, karma_before=50):
        st = new_state(skills=skills if skills is not None else [], level_karma=karma_before)
        ks = KarmaSystem(st)
        actual, over, over_amt = ks.consume(amount)
        consume_cases.append({
            "name": name, "amount": amount, "skills": skills or [],
            "karma_before": karma_before,
            "expected": {
                "actual_increased": actual,
                "is_overdraft": over,
                "overdraft_amount": over_amt,
                "new_karma": st.get_karma(),
            },
        })

    consume("within_threshold", 60)
    consume("cross_threshold", 40, karma_before=100)
    consume("exact_threshold_no_overdraft", 40, karma_before=80)
    consume("over_single_max_rejected", 130)
    consume("deep_cheat_t3b_extends_single_max", 150,
            skills=["cheat_mastery_t3b"])
    consume("capacity_t2a_extends_single_max", 150,
            skills=["karma_capacity_t2a"])

    # 公式用例：Δ = C × O^α（独立写入，Python 原版函数交叉校验）
    formula = []
    for O, C, alpha in [(100, 0.1, 1.5), (64, 0.1, 1.5), (4, 0.1, 1.5),
                        (1, 0.1, 1.5), (25, 0.1, 1.5), (0, 0.1, 1.5),
                        (-5, 0.1, 1.5), (100, 0.07, 1.5), (100, 0.1, 1.3)]:
        a = kab.KarmaAssessorBase(api_key="")
        a.set_local_karma_state(50, 120, 120)
        mods = {"detection_coefficient": C, "detection_alpha": alpha}
        # 独立推导（derivation）：O<=0 → 0；否则 C*O**alpha
        expected = 0.0 if O <= 0 else C * (O ** alpha)
        got = a.calculate_detection_delta(O, mods)
        assert abs(expected - got) < 1e-12, (O, C, alpha, expected, got)
        formula.append({"overshoot": O, "coefficient": C, "alpha": alpha,
                        "expected": expected})

    increase_cases = []

    def inc(name, amount, karma=50, karma_max=120, single_max=120, mods=None):
        a = kab.KarmaAssessorBase(api_key="")
        a.set_local_karma_state(karma, karma_max, single_max)
        r = a.increase_karma(amount, mods)
        increase_cases.append({
            "name": name, "amount": amount, "karma_before": karma,
            "karma_max": karma_max, "single_max": single_max, "modifiers": mods or {},
            "expected": {
                "actual_increased": r["actual_increased"],
                "is_overdraft": r["is_overdraft"],
                "overdraft_amount": r["overdraft_amount"],
                "new_karma": r["new_karma"],
                "success": r["success"],
            },
        })

    inc("safe", 60)
    inc("cross", 40, karma=100)
    inc("exact", 40, karma=80)
    inc("reject_over_single", 130)
    inc("bonus_single_max", 150, mods={"karma_single_max_bonus": 40})
    inc("bonus_karma_max", 60, karma_max=120, mods={"karma_max_bonus": 20})

    decrease_cases = []

    def dec(name, amount, karma=50, mods=None):
        a = kab.KarmaAssessorBase(api_key="")
        a.set_local_karma_state(karma, 120, 120)
        actual = a.decrease_karma(amount, mods)
        decrease_cases.append({
            "name": name, "amount": amount, "karma_before": karma,
            "modifiers": mods or {}, "expected_decreased": actual,
            "expected_karma_after": a.get_local_karma(),
        })

    dec("plain", 20)
    dec("with_multiplier", 20, mods={"karma_recover_multiplier": 1.3})
    dec("clamp_zero", 200, karma=10)

    assessor_state = kab.KarmaAssessorBase(api_key="")
    assessor_state.set_local_karma_state(70, 120, 140)
    assessor_state.set_realm_detection(12.5, "animal")
    assessor_get_state = assessor_state.get_state(
        {"karma_max_bonus": 20, "karma_single_max_bonus": 30, "initial_karma_reduction": 25}
    )

    fallback = {k: kab.KarmaAssessorBase(api_key="")._fallback_assess(k)
                for k in ["E", "D", "A", "B", "C", "C+", "ZZ"]}

    return {
        "_meta": {
            "generator": "tools/verify/gen-samsara-fixtures.py",
            "source": "legacy-web/samsara/karma.py + legacy-web/shared/karma_assessor_base.py",
            "note": "KarmaSystem 走 SamsaraState 状态机；KarmaAssessorBase 为棋类侧进程内镜像（对应 ChessSage.Core.Samsara.KarmaAssessor）。",
        },
        "constants": {
            "karma_max": 120, "karma_single_max": 120, "initial_karma": 50,
            "initial_karma_reduction_neti": 25,
        },
        "recover_cases": recover_cases,
        "consume_cases": consume_cases,
        "assessor_delta_formula_cases": formula,
        "assessor_increase_cases": increase_cases,
        "assessor_decrease_cases": decrease_cases,
        "assessor_get_state": assessor_get_state,
        "assessor_fallback": fallback,
    }


# ════════════════════════════════════════════════════════════════════════════
# 2) 识破：DetectionSystem（状态机）— 公式 + 确定性分支
# ════════════════════════════════════════════════════════════════════════════

def gen_detection() -> dict:
    delta_cases = []

    def delta(name, overshoot, skills=None):
        st = new_state(skills=skills if skills is not None else [])
        ds = DetectionSystem(st)
        val = ds.calculate_delta(overshoot)
        delta_cases.append({
            "name": name, "overshoot": overshoot, "skills": skills or [],
            "expected": val,
        })

    # 无 stealth_t1 → C=0.1, α=1.5（清空技能以取默认系数）
    delta("coefficient_0_1_alpha_1_5_O100", 100, skills=[])
    delta("O64", 64, skills=[])
    delta("O4", 4, skills=[])
    delta("O1", 1, skills=[])
    delta("O25", 25, skills=[])
    delta("nonpositive_O0", 0, skills=[])
    delta("nonpositive_neg", -5, skills=[])
    # stealth_t1 → C=0.07
    delta("stealth_t1_C_0_07_O100", 100, skills=["stealth_t1"])
    delta("stealth_t1_O64", 64, skills=["stealth_t1"])
    # karma_capacity_t3a → α=1.3（仍需 stealth_t1 默认？此处显式两技能）
    delta("alpha_1_3_O100", 100, skills=["stealth_t1", "karma_capacity_t1", "karma_capacity_t3a"])

    # 单调性采样点（同一 C/α 下 O 越大 Δ 越大）
    st = new_state(skills=[])
    ds = DetectionSystem(st)
    mono = [{"overshoot": O, "delta": ds.calculate_delta(O)} for O in [1, 2, 4, 8, 16, 32, 64, 100]]

    # HandleOverdraft 确定性分支
    # (a) 非正溢出
    st = new_state(skills=[])
    r_nonpos = DetectionSystem(st).handle_overdraft(0.0)
    # (b) 锁死状态
    st = new_state(skills=[])
    st._data["detection_state"]["detection_locked"] = True
    st._data["detection_state"]["is_detected"] = True
    r_locked = DetectionSystem(st).handle_overdraft(50.0)
    # (c) 首次透支豁免（stealth_t2a 未消耗）
    st = new_state(skills=["stealth_t1", "stealth_t2a"])
    r_skip = DetectionSystem(st).handle_overdraft(50.0)
    skip_state = {
        "skip": r_skip.get("skip"),
        "consumed": st.is_one_time_skill_used("stealth_t2a"),
        "current": r_skip.get("current"),
        "delta": r_skip.get("delta"),
    }

    handle_overdraft_fixed = {
        "nonpositive": {"detected": r_nonpos["detected"], "delta": r_nonpos["delta"]},
        "locked": {"detected": r_locked["detected"], "locked": r_locked.get("locked"),
                   "current": r_locked["current"]},
        "first_skip": skip_state,
    }

    # 判词（从 story.json.detection 读取）
    story_detection = json.loads(
        (LEGACY / "configs" / "story.json").read_text(encoding="utf-8")
    ).get("detection", {})

    return {
        "_meta": {
            "generator": "tools/verify/gen-samsara-fixtures.py",
            "source": "legacy-web/samsara/detection.py",
            "note": "Δ = C × O^α；C 默认 0.1（stealth_t1 解锁后 0.07），α 默认 1.5（karma_capacity_t3a 解锁后 1.3）。",
        },
        "delta_cases": delta_cases,
        "monotonic_points": mono,
        "handle_overdraft_fixed": handle_overdraft_fixed,
        "judgment_text": story_detection.get("judgment_text"),
        "reset_message": story_detection.get("reset_message"),
    }


# ════════════════════════════════════════════════════════════════════════════
# 3) 技能树
# ════════════════════════════════════════════════════════════════════════════

def gen_skills() -> dict:
    tree = json.loads((LEGACY / "configs" / "skill_tree.json").read_text(encoding="utf-8"))
    branches = list(tree["branches"].keys())

    tree_shape = {}
    costs = {}
    for b, bd in tree["branches"].items():
        tree_shape[b] = {}
        for tier, td in bd["tiers"].items():
            if "options" in td:
                ids = [o["id"] for o in td["options"]]
                for o in td["options"]:
                    costs[o["id"]] = o["cost"]
            else:
                ids = [td["id"]]
                costs[td["id"]] = td["cost"]
            tree_shape[b][str(tier)] = ids

    unlock_cases = []

    def unlock(name, skill_id, tier, pre=None, points=5):
        st = new_state(skills=pre if pre is not None else None, skill_points=points)
        pre_keys = sorted((st.get("skills") or {}).keys())
        sk = SkillSystem(st)
        ok = sk.unlock_skill(skill_id, tier)
        unlock_cases.append({
            "name": name, "skill_id": skill_id, "tier": tier,
            "pre_skills": pre_keys,
            "skill_points": points,
            "expected_success": ok,
            "expected_points_after": st.get("skill_points"),
            "expected_unlocked": st.is_skill_unlocked(skill_id),
        })

    unlock("tier1_fresh", "karma_capacity_t1", 1)
    unlock("tier2_without_tier1", "karma_capacity_t2b", 2)
    unlock("tier2_after_tier1", "karma_capacity_t2b", 2,
           pre=["stealth_t1", "karma_capacity_t1"], points=5)
    unlock("already_unlocked_idempotent", "stealth_t1", 1, pre=["stealth_t1"], points=5)
    unlock("unknown_skill", "does_not_exist_t1", 1, pre=["stealth_t1"], points=5)
    unlock("tier3_without_tier2", "karma_capacity_t3a", 3,
           pre=["stealth_t1", "karma_capacity_t1"], points=5)
    unlock("insufficient_points", "karma_capacity_t1", 1, pre=["stealth_t1"], points=0)
    unlock("cheat_mastery_t1a", "cheat_mastery_t1a", 1, pre=["stealth_t1"], points=5)

    avail = sorted(x["id"] for x in SkillSystem(new_state()).get_available_skills())

    # 修饰符用例
    modifier_cases = []

    def modifiers(name, skills, used_one_time=None):
        st = new_state(skills=skills, used_one_time=used_one_time)
        modifier_cases.append({
            "name": name, "skills": skills, "used_one_time": used_one_time or [],
            "expected": st.get_skill_modifiers(),
        })

    modifier_cases.append({
        "name": "empty", "skills": [], "used_one_time": [],
        "expected": new_state(skills=[]).get_skill_modifiers(),
    })
    modifiers("stealth_t1", ["stealth_t1"])
    modifiers("capacity_t1", ["stealth_t1", "karma_capacity_t1"])
    modifiers("capacity_t1_t2a", ["stealth_t1", "karma_capacity_t1", "karma_capacity_t2a"])
    modifiers("capacity_t1_t2b", ["stealth_t1", "karma_capacity_t1", "karma_capacity_t2b"])
    modifiers("capacity_t1_t3a", ["stealth_t1", "karma_capacity_t1", "karma_capacity_t3a"])
    modifiers("capacity_t3b", ["stealth_t1", "karma_capacity_t3b"])
    modifiers("stealth_t3a_active", ["stealth_t1", "stealth_t3a"])
    modifiers("stealth_t3a_used", ["stealth_t1", "stealth_t3a"], used_one_time=["stealth_t3a"])
    modifiers("stealth_t2a_active", ["stealth_t1", "stealth_t2a"])
    modifiers("cheat_t3b", ["stealth_t1", "cheat_mastery_t3b"])
    modifiers("cheat_t3a", ["stealth_t1", "cheat_mastery_t3a"])
    modifiers("realm_t2a", ["stealth_t1", "realm_insight_t2a"])
    modifiers("realm_t3a", ["stealth_t1", "realm_insight_t3a"])
    modifiers("realm_t3b", ["stealth_t1", "realm_insight_t3b"])

    all_ids = sorted(costs.keys())
    modifier_cases.append({
        "name": "all_skills", "skills": all_ids, "used_one_time": [],
        "expected": new_state(skills=all_ids).get_skill_modifiers(),
    })

    # allowed classifications
    def allowed(skills):
        return sorted(new_state(skills=skills).get_allowed_classifications())

    return {
        "_meta": {"generator": "tools/verify/gen-samsara-fixtures.py",
                  "source": "legacy-web/samsara/skills.py + state.get_skill_modifiers"},
        "branches": branches,
        "tree_shape": tree_shape,
        "costs": costs,
        "unlock_cases": unlock_cases,
        "available_from_fresh": avail,
        "modifier_cases": modifier_cases,
        "allowed_classifications": {
            "fresh": allowed(["stealth_t1"]),
            "with_t1a": allowed(["stealth_t1", "cheat_mastery_t1a"]),
            "with_t1a_t1b": allowed(["stealth_t1", "cheat_mastery_t1a", "cheat_mastery_t1b"]),
        },
    }


# ════════════════════════════════════════════════════════════════════════════
# 4) 关卡 / 进度 / 回合
# ════════════════════════════════════════════════════════════════════════════

def gen_levels() -> dict:
    st = new_state()
    ls = LevelSystem(st)

    realm_meta = {}
    all_levels = []
    for r in REALMS:
        prog = ls.get_realm_progress(r)
        realm_meta[r] = {
            "name": prog["name"], "game_type": prog["game_type"],
            "icon": prog["icon"], "total_levels": prog["total_levels"],
        }
        for i in range(prog["total_levels"]):
            lv = ls.load_level(r, i)
            all_levels.append({
                "realm": r, "index": i, "id": lv.get("id"),
                "name": lv.get("name"), "type": lv.get("type"),
                "objective_type": (lv.get("objective") or {}).get("type"),
                "has_puzzle_data": "puzzle_data" in lv,
            })

    advance_cases = []

    def advance(name, realm, level):
        st = new_state(realm=realm)
        st._data["current_level"] = level
        ls2 = LevelSystem(st)
        res = ls2.advance_to_next_level()
        entry = {"name": name, "realm": realm, "current_level": level,
                 "expected_success": res["success"]}
        if res["success"]:
            entry["expected_new_index"] = res["new_level"]["level_index"]
        advance_cases.append(entry)

    advance("hell_0_to_1", "hell", 0)
    advance("hell_last", "hell", 4)

    sandbox_cases = []
    for r in REALMS:
        sb = LevelSystem(new_state()).load_sandbox(r)
        sandbox_cases.append({
            "realm": r, "expected_turn_limit": sb["turn_limit"],
            "expected_ai_depth": sb["ai_depth"], "expected_name": sb["name"],
            "expected_objective": sb["objective"]["type"],
        })

    # 状态标签
    st = new_state(realm="hell")
    st._data["realm_progress"]["hell"]["levels_passed"] = 2
    rl = LevelSystem(st).get_realm_levels("hell")
    statuses = [lv["status"] for lv in rl["levels"]]

    # 回合上限
    tl_cases = []

    def turn(name, limit, cur, reset_to=None):
        st = new_state(realm="hell")
        st._data["turn_limit"] = limit
        st._data["current_turn"] = cur
        tl = TurnLimitSystem(st)
        entry = {"name": name, "limit": limit, "current": cur,
                 "expected_remaining": tl.get_remaining(),
                 "expected_over": tl.is_over(),
                 "expected_progress": tl.get_progress()}
        if reset_to is not None:
            tl.reset(reset_to)
            entry["reset_to"] = reset_to
            entry["expected_after_reset_remaining"] = tl.get_remaining()
            entry["expected_after_reset_over"] = tl.is_over()
        tl_cases.append(entry)

    turn("fresh", 20, 0)
    turn("half", 20, 10)
    turn("at_limit", 20, 20)
    turn("over_limit_progress_capped", 20, 25)
    turn("reset", 20, 20, reset_to=30)

    # tick
    st = new_state()
    st._data["turn_limit"] = 20
    st._data["current_turn"] = 19
    tl = TurnLimitSystem(st)
    tick_over = tl.tick()  # 19 -> 20 -> over
    tick_case = {"limit": 20, "current": 19, "expected_after_tick_over": tick_over,
                 "expected_after_tick_current": st.get("current_turn"),
                 "expected_after_tick_remaining": tl.get_remaining()}

    return {
        "_meta": {"generator": "tools/verify/gen-samsara-fixtures.py",
                  "source": "legacy-web/samsara/levels.py + turn_limit.py"},
        "realm_meta": realm_meta,
        "levels": all_levels,
        "advance_cases": advance_cases,
        "sandbox_cases": sandbox_cases,
        "realm_levels_status": {"realm": "hell", "levels_passed": 2,
                                "expected": statuses},
        "turn_limit_cases": tl_cases,
        "turn_tick_case": tick_case,
    }


def gen_progression() -> dict:
    realm_counts = {"hell": 5, "hungry": 5, "animal": 5, "human": 6, "asura": 6, "heaven": 6}

    resolve_cases = []

    def resolve(name, realm="hell", won=True, no_cheat=True, boss_defeated=False,
                overdraft_count=0, level_karma=50, levels_passed=0, sandbox_mode=False):
        st = new_state(realm=realm, level_karma=level_karma)
        st._data["overdraft_count"] = overdraft_count
        st._data["realm_progress"][realm]["levels_passed"] = levels_passed
        st._data["sandbox_mode"] = sandbox_mode
        ps = ProgressionSystem(st)
        res = ps.resolve_level(won, no_cheat, boss_defeated)
        resolve_cases.append({
            "name": name, "realm": realm, "won": won, "no_cheat": no_cheat,
            "boss_defeated": boss_defeated, "overdraft_count": overdraft_count,
            "level_karma": level_karma, "levels_passed_before": levels_passed,
            "sandbox_mode": sandbox_mode,
            "expected": {
                "skill_points": res["skill_points"],
                "bonus_reasons": res["bonus_reasons"],
                "overshoot_carryover": res["overshoot_carryover"],
                "sandbox_unlocked": res.get("sandbox_unlocked", False),
            },
            "expected_skill_points_total": st.get("skill_points"),
            "expected_levels_passed": st.get("realm_progress")[realm]["levels_passed"],
        })

    resolve("perfect_win")                                # 1 + 2(no_cheat) + 1(no overdraft) = 4
    resolve("win_overdrafted", overdraft_count=1)         # 3
    resolve("win_with_boss", boss_defeated=True)          # 5
    resolve("lose", won=False)                            # 0
    resolve("sandbox_win", sandbox_mode=True)             # 1
    resolve("overshoot_carryover", level_karma=140)       # overshoot 20
    resolve("realm_completed", realm="hell", levels_passed=4)  # -> 5 >= 5 completed

    advance_cases = []

    def advance(name, realm, levels_passed, overdraft=0):
        st = new_state(realm=realm)
        st._data["realm_progress"][realm]["levels_passed"] = levels_passed
        st._data["overdraft_count"] = overdraft
        res = ProgressionSystem(st).advance_realm()
        advance_cases.append({
            "name": name, "realm": realm, "levels_passed": levels_passed,
            "overdraft": overdraft,
            "expected": {"advanced": res["advanced"], "new_realm": res["new_realm"],
                         "direction": res["direction"]},
            "expected_realm_after": st.get("current_realm"),
        })

    advance("hell_incomplete", "hell", 4)          # not advanced
    advance("hell_complete_clean", "hell", 5, 0)   # up_2 -> animal
    advance("hell_complete_overdraft", "hell", 5, 1)  # up_1 -> hungry
    advance("asura_complete_clean", "asura", 6, 0)  # 4 -> min(6,5)=5 heaven
    advance("heaven_complete", "heaven", 6, 0)      # transcend

    retreat_cases = []

    def retreat(name, realm):
        st = new_state(realm=realm)
        res = ProgressionSystem(st).retreat_realm()
        retreat_cases.append({"name": name, "realm": realm, "expected": res,
                              "expected_realm_after": st.get("current_realm")})

    retreat("from_human", "human")
    retreat("from_hell", "hell")

    return {
        "_meta": {"generator": "tools/verify/gen-samsara-fixtures.py",
                  "source": "legacy-web/samsara/progression.py"},
        "realm_level_counts": realm_counts,
        "resolve_cases": resolve_cases,
        "advance_cases": advance_cases,
        "retreat_cases": retreat_cases,
    }


# ════════════════════════════════════════════════════════════════════════════
# 5) 结局
# ════════════════════════════════════════════════════════════════════════════

def gen_endings() -> dict:
    cases = []

    def ending(name, prayer=0, align=None, karma=50, frags_all=False,
               realms_all=False, exposure=False, boss_defeated=False,
               final_choice=None, no_cheat_final=True):
        st = new_state(level_karma=karma)
        st._data["prayer_count"] = prayer
        if align:
            st._data["alignment"].update(align)
        if frags_all:
            for r in REALMS:
                st._data["memory_fragments_unlocked"][r] = True
        if realms_all:
            for r in REALMS:
                st._data["realm_progress"][r]["completed"] = True
        if exposure:
            st._data["detection_state"]["exposure_path_triggered"] = True
        if boss_defeated:
            st._data["tiandao_boss_state"]["defeated"] = True
        es = EndingSystem(st)
        flags = {
            "exposed": es.check_exposed_ending(),
            "true_me": es.check_true_me_ending(final_choice),
            "enlightenment": es.check_enlightenment_ending(final_choice),
            "corruption": es.check_corruption_ending(final_choice),
            "samsara": es.check_samsara_ending(final_choice),
        }
        det = es.determine_ending(final_choice, no_cheat_final)
        cases.append({
            "name": name,
            "setup": {"prayer": prayer, "align": align or {}, "karma": karma,
                      "frags_all": frags_all, "realms_all": realms_all,
                      "exposure": exposure, "boss_defeated": boss_defeated,
                      "final_choice": final_choice, "no_cheat_final": no_cheat_final},
            "flags": flags,
            "expected_ending_id": det["ending_id"],
            "expected_reason": det["reason"],
        })

    ending("exposed_priority", prayer=1, exposure=True, boss_defeated=True, realms_all=True)
    ending("true_me", frags_all=True, align={"enlightenment": 9}, final_choice="enlightenment")
    ending("enlightenment", align={"enlightenment": 5}, final_choice="enlightenment")
    ending("corruption", align={"corruption": 5}, karma=250, final_choice="corruption")
    ending("samsara", align={"enlightenment": 1, "corruption": 1}, karma=150,
           final_choice="samsara")
    # 边界
    ending("enlight_boundary_e_minus_c_3", align={"enlightenment": 3}, final_choice="enlightenment")
    ending("enlight_boundary_e_minus_c_2", align={"enlightenment": 2}, final_choice="enlightenment")
    ending("enlight_karma_99", align={"enlightenment": 5}, karma=99, final_choice="enlightenment")
    ending("enlight_karma_100_blocked", align={"enlightenment": 5}, karma=100, final_choice="enlightenment")
    ending("corrupt_karma_201", align={"corruption": 5}, karma=201, final_choice="corruption")
    ending("corrupt_karma_200_blocked", align={"corruption": 5}, karma=200, final_choice="corruption")
    ending("samsara_karma_100", align={"enlightenment": 0, "corruption": 0}, karma=100,
           final_choice="samsara")
    ending("samsara_karma_99_blocked", align={"enlightenment": 0, "corruption": 0}, karma=99,
           final_choice="samsara")
    ending("samsara_karma_200", align={"enlightenment": 1, "corruption": 1}, karma=200,
           final_choice="samsara")
    ending("samsara_karma_201_blocked", align={"enlightenment": 1, "corruption": 1}, karma=201,
           final_choice="samsara")
    ending("samsara_abs2", align={"enlightenment": 2, "corruption": 0}, karma=150,
           final_choice="samsara")
    ending("samsara_abs3_blocked", align={"enlightenment": 3, "corruption": 0}, karma=150,
           final_choice="samsara")
    # 优先级 / no_cheat_final
    ending("true_me_beats_enlight", frags_all=True, align={"enlightenment": 9}, karma=50,
           final_choice="enlightenment")
    ending("no_cheat_false_skips_true_me", frags_all=True, align={"enlightenment": 9}, karma=50,
           final_choice="enlightenment", no_cheat_final=False)
    ending("exposed_beats_true_me", prayer=1, exposure=True, boss_defeated=True,
           frags_all=True, align={"enlightenment": 9}, final_choice="enlightenment")
    # 祈求但未打 Boss
    ending("prayer_no_boss_undetermined", prayer=1, align={"enlightenment": 5})
    # 无最终选择的兜底
    ending("fallback_enlight", align={"enlightenment": 5}, karma=150)
    ending("fallback_corrupt", align={"corruption": 5}, karma=150)
    ending("fallback_samsara", align={"enlightenment": 3, "corruption": 3})

    # 结局名称/类型
    story = json.loads((LEGACY / "configs" / "story.json").read_text(encoding="utf-8"))
    endings_meta = {eid: {"name": e.get("name"), "type": e.get("type")}
                    for eid, e in story["endings"].items()}

    # preview
    st = new_state(level_karma=50)
    st._data["alignment"].update({"enlightenment": 5})
    preview = EndingSystem(st).get_ending_preview()

    return {
        "_meta": {"generator": "tools/verify/gen-samsara-fixtures.py",
                  "source": "legacy-web/samsara/endings.py"},
        "endings_meta": endings_meta,
        "cases": cases,
        "preview_enlight_case": {"align": {"enlightenment": 5}, "karma": 50,
                                 "expected": preview},
    }


# ════════════════════════════════════════════════════════════════════════════
# 6) Boss / 天道 Boss
# ════════════════════════════════════════════════════════════════════════════

def gen_boss() -> dict:
    definitions = {}
    for r in REALMS:
        st = new_state(realm=r)
        bs = BossSystem(st)
        b = bs.get_current_boss()
        definitions[r] = {
            "id": b.get("id"), "name": b.get("name"), "title": b.get("title"),
            "skill": b.get("skill"), "ai_personality": b.get("ai_personality"),
            "icon": b.get("icon"), "skill_uses": b.get("skill_uses"),
        }

    trigger_cases = []

    def trigger(name, realm, cheat_count, skills=None):
        st = new_state(realm=realm, skills=skills if skills is not None else None)
        bs = BossSystem(st)
        res = bs.trigger_boss_skill(cheat_count)
        trigger_cases.append({
            "name": name, "realm": realm, "cheat_count": cheat_count,
            "skills": skills or [],
            "expected_triggered": res["triggered"],
            "expected_skill": res.get("skill"),
            "expected_effect": res.get("effect"),
            "deterministic": True,
        })

    trigger("human_no_skill", "human", 5)
    trigger("animal_rank_lock", "animal", 1)
    trigger("hungry_take_more_2", "hungry", 2)
    trigger("hungry_take_more_1", "hungry", 1)
    trigger("hell_flip_reduced", "hell", 1, skills=["realm_insight_t2a"])
    trigger("asura_chaos_reduced", "asura", 1, skills=["realm_insight_t2a"])

    # 非确定性技能：只校验 skill 字段（triggered 在 {true,false}）
    nondeterministic = []
    for realm in ["hell", "asura"]:
        st = new_state(realm=realm)
        res = BossSystem(st).trigger_boss_skill(1)
        nondeterministic.append({
            "realm": realm, "expected_skill": res.get("skill"),
            "triggered_is_bool": isinstance(res["triggered"], bool),
        })

    # purify 序列：3 次可用，第 4 次 false
    st = new_state(realm="heaven")
    bs = BossSystem(st)
    seq = [bs.trigger_boss_skill(1)["triggered"] for _ in range(4)]
    # reset_boss_skills 恢复
    bs.reset_boss_skills()
    after_reset = bs.trigger_boss_skill(1)["triggered"]

    # 天道 Boss
    st = new_state()
    hb = HeavenBossSystem(st)
    boss_info = hb.get_boss_info()
    rules = hb.get_rules()
    pieces = hb.get_pieces_config()
    initial_board = hb.get_initial_board()

    can_enter_cases = []

    def can_enter(name, realms_all, prayer, defeated):
        st = new_state()
        st._data["prayer_count"] = prayer
        if realms_all:
            for r in REALMS:
                st._data["realm_progress"][r]["completed"] = True
        if defeated:
            st._data["tiandao_boss_state"]["defeated"] = True
        res = HeavenBossSystem(st).can_enter()
        can_enter_cases.append({
            "name": name, "realms_all": realms_all, "prayer": prayer, "defeated": defeated,
            "expected_can_enter": res["can_enter"],
            "expected_reason": res["reason"],
            "expected_already_defeated": res.get("already_defeated", False),
        })

    can_enter("not_all_realms", False, 0, False)
    can_enter("no_prayer", True, 0, False)
    can_enter("can_enter", True, 1, False)
    can_enter("already_defeated", True, 1, True)

    # enter_battle 计数
    st = new_state()
    for r in REALMS:
        st._data["realm_progress"][r]["completed"] = True
    st._data["prayer_count"] = 1
    hb2 = HeavenBossSystem(st)
    e1 = hb2.enter_battle()
    e2 = hb2.enter_battle()
    on_win = hb2.on_win()
    enter_case = {
        "first_attempt_count": e1["attempt_count"],
        "second_attempt_count": e2["attempt_count"],
        "success": e1["success"],
        "on_win_defeated": on_win["defeated"],
        "on_win_next": on_win["next"],
    }

    return {
        "_meta": {"generator": "tools/verify/gen-samsara-fixtures.py",
                  "source": "legacy-web/samsara/bosses.py + heaven_boss.py"},
        "definitions": definitions,
        "trigger_cases": trigger_cases,
        "nondeterministic_skills": nondeterministic,
        "purify_sequence": {"realm": "heaven", "expected": seq, "after_reset": after_reset},
        "heaven_boss": {
            "info": boss_info, "rules": rules, "pieces": pieces,
            "initial_board": initial_board, "can_enter_cases": can_enter_cases,
            "enter_case": enter_case,
        },
    }


# ════════════════════════════════════════════════════════════════════════════
# 7) 状态 / 默认值 / JSON
# ════════════════════════════════════════════════════════════════════════════

def gen_state() -> dict:
    st = new_state()  # seed {"version":4}
    defaults = {}
    for k in ["version", "current_realm", "current_level", "skill_points",
              "karma_max", "karma_single_max", "initial_karma",
              "realm_overshoot_carryover", "current_turn", "turn_limit",
              "cheat_count", "overdraft_count", "no_cheat_this_level",
              "total_levels_completed", "sandbox_mode", "playthrough_count",
              "level_karma", "prayer_count"]:
        defaults[k] = st.get(k)
    defaults["realm_detections_all_zero"] = all(
        st.get("realm_detections")[r] == 0.0 for r in REALMS)
    defaults["skills_keys"] = sorted((st.get("skills") or {}).keys())
    defaults["realm_progress_keys"] = sorted(st.get("realm_progress").keys())
    defaults["memory_fragments_keys"] = sorted(st.get("memory_fragments_unlocked").keys())
    defaults["endings_keys"] = sorted(st.get("endings_unlocked").keys())
    defaults["alignment"] = st.get("alignment")
    defaults["realm_progress_template"] = st.get("realm_progress")["hell"]

    normalize_cases = [
        {"input": "karma_capacity_t1", "expected": normalize_skill_key("karma_capacity_t1")},
        {"input": "karma_capacity_t1_t1", "expected": normalize_skill_key("karma_capacity_t1_t1")},
        {"input": "stealth_t2a_t2", "expected": normalize_skill_key("stealth_t2a_t2")},
        {"input": "whatever", "expected": normalize_skill_key("whatever")},
        {"input": "  stealth_t1  ", "expected": normalize_skill_key("  stealth_t1  ")},
    ]

    global_configs = ["boss_definitions", "formations", "karma_events", "level_pools",
                      "objective_types", "overworld", "puzzles", "samsara_state",
                      "skill_tree", "story", "tiandao_boss"]

    return {
        "_meta": {"generator": "tools/verify/gen-samsara-fixtures.py",
                  "source": "legacy-web/samsara/state.py"},
        "seed": {"version": 4},
        "expected_defaults": defaults,
        "normalize_cases": normalize_cases,
        "global_configs": global_configs,
        "key_paths": {
            "story_endings": ["enlightenment", "corruption", "samsara", "true_me", "exposed"],
            "story_detection_keys": ["judgment_text", "reset_message"],
            "tiandao_keys": ["id", "name", "boss_battle", "boss_dialogues"],
            "level_pools_realms": REALMS,
            "objective_types_keys": sorted(json.loads(
                (LEGACY / "configs" / "objective_types.json").read_text(encoding="utf-8")).keys()),
        },
    }


# ════════════════════════════════════════════════════════════════════════════
# 8) 目标 / 记忆碎片 / 选择（补充覆盖）
# ════════════════════════════════════════════════════════════════════════════

def gen_misc() -> dict:
    # 目标
    objective_cases = []

    def obj(name, objective, game_state, current_turn=0):
        st = new_state()
        st._data["current_turn"] = current_turn
        r = ObjectiveSystem(st).check(objective, game_state)
        objective_cases.append({"name": name, "objective": objective,
                                "game_state": game_state, "current_turn": current_turn,
                                "expected": r})

    obj("checkmate_win", {"type": "checkmate"},
        {"game_status": {"state": "ended", "winner": "red"}})
    obj("checkmate_no", {"type": "checkmate"},
        {"game_status": {"state": "playing", "winner": None}})
    obj("capture_count_partial", {"type": "capture_count", "target": 2},
        {"pieces": [{"is_alive": False, "side": "black"}, {"is_alive": True, "side": "black"}]})
    obj("assassination", {"type": "assassination", "target_piece": "tiger"},
        {"pieces": [{"type": "tiger", "side": "black", "is_alive": False}]})
    obj("turn_limit_win", {"type": "turn_limit", "max_turns": 20},
        {"game_status": {"state": "ended", "winner": "red"}}, current_turn=10)
    obj("turn_limit_used_up", {"type": "turn_limit", "max_turns": 20},
        {"game_status": {"state": "playing"}}, current_turn=20)
    obj("unknown_type", {"type": "no_such"}, {})

    # 记忆碎片
    st = new_state(realm="hell")
    mf = MemoryFragmentSystem(st)
    mem_cases = []
    r1 = mf.try_unlock("hell")
    mem_cases.append({"name": "locked_before_clear", "expected": {
        "success": r1["success"], "already_unlocked": r1["already_unlocked"],
        "reason": r1["reason"]}})
    st._data["realm_progress"]["hell"]["no_cheat_full_clear"] = True
    r2 = mf.try_unlock("hell")
    mem_cases.append({"name": "unlock_after_clear", "expected": {
        "success": r2["success"], "already_unlocked": r2["already_unlocked"],
        "reason": r2["reason"]}})
    r3 = mf.try_unlock("hell")
    mem_cases.append({"name": "already_unlocked", "expected": {
        "success": r3["success"], "already_unlocked": r3["already_unlocked"],
        "reason": r3["reason"]}})
    mem_status_keys = ["realm", "unlocked", "title", "id", "cg", "condition_met"]

    # 选择
    st = new_state()
    cs = ChoiceSystem(st)
    choice_cases = []

    def apply(name, realm, level, ci, oi):
        st = new_state()
        cs2 = ChoiceSystem(st)
        r = cs2.apply_choice(realm, level, ci, oi)
        choice_cases.append({
            "name": name, "realm": realm, "level": level,
            "choice_index": ci, "option_index": oi,
            "expected": {
                "success": r["success"],
                "option_text": r.get("option_text"),
                "hint": r.get("hint"),
                "is_final": r.get("is_final"),
            },
            "expected_alignment": st.get_alignment(),
            "expected_karma_after": st.get_karma(),
            "expected_choices_made": len(st.get_choices_made()),
        })

    apply("hell3_opt0", "hell", "3", 0, 0)
    apply("hell3_opt1", "hell", "3", 0, 1)
    apply("human3_karma_delta", "human", "3", 0, 1)
    apply("heaven6_final0", "heaven", "6", 0, 0)
    apply("bad_panel", "hell", "3", 5, 0)
    apply("bad_option", "hell", "3", 0, 9)

    # no-choice level
    no_choice = len(ChoiceSystem(new_state()).get_choices_for_level("hell", "1"))

    return {
        "_meta": {"generator": "tools/verify/gen-samsara-fixtures.py",
                  "source": "legacy-web/samsara/objectives.py + memory_fragments.py + choices.py"},
        "objective_cases": objective_cases,
        "memory_cases": mem_cases,
        "memory_status_keys": mem_status_keys,
        "choice_cases": choice_cases,
        "no_choice_level_count": no_choice,
    }


# ════════════════════════════════════════════════════════════════════════════

def main() -> None:
    fixtures = {
        "samsara_karma": gen_karma(),
        "samsara_detection": gen_detection(),
        "samsara_skills": gen_skills(),
        "samsara_levels": gen_levels(),
        "samsara_progression": gen_progression(),
        "samsara_endings": gen_endings(),
        "samsara_boss": gen_boss(),
        "samsara_state": gen_state(),
        "samsara_misc": gen_misc(),
    }
    for name, obj in fixtures.items():
        size = _dump(name, obj)
        print(f"  wrote {name}.json ({size} bytes)")

    print(f"OK: {len(fixtures)} fixtures written to {OUT_DIR.relative_to(ROOT)}")


if __name__ == "__main__":
    try:
        main()
    finally:
        for d in _TMPDIRS:
            shutil.rmtree(d, ignore_errors=True)
