# -*- coding: utf-8 -*-
"""技能系统回归测试（对应审查报告 2.1 / 2.2 修复）。

覆盖：
- 2.1 技能键规范：解锁以技能树权威 id 落盘；get_skill_modifiers / is_skill_unlocked /
  get_allowed_classifications 三处读取口径一致；旧格式 "{id}_t{tier}" 存档自动迁移。
- 2.2 一次性技能：首次透支免判（stealth_t2a）/ 金蝉脱壳（stealth_t3a）每关只消耗一次，
  且消耗持久化到 SamsaraState（关卡开始 reset_level_state 时重置）。

运行：pytest tests/test_skill_system.py -q
"""
import json

import pytest

import samsara.state as state_mod
from samsara.state import SamsaraState, normalize_skill_key
from samsara.skills import SkillSystem
from samsara.detection import DetectionSystem


@pytest.fixture
def state(tmp_path, monkeypatch):
    """隔离的 SamsaraState：STATE_FILE 重定向到临时文件，技能点充足。"""
    f = tmp_path / "samsara_state.json"
    f.write_text(json.dumps({"version": 4, "skill_points": 99}), encoding="utf-8")
    monkeypatch.setattr(state_mod, "STATE_FILE", f)
    s = SamsaraState()
    s.set("skill_points", 99)
    return s


# ── 2.1 键规范 ──────────────────────────────────────────────────

def test_normalize_skill_key():
    assert normalize_skill_key("karma_capacity_t1") == "karma_capacity_t1"
    assert normalize_skill_key("stealth_t2a") == "stealth_t2a"
    # 旧格式：权威 id + _t{tier} 冗余后缀
    assert normalize_skill_key("karma_capacity_t1_t1") == "karma_capacity_t1"
    assert normalize_skill_key("stealth_t2a_t2") == "stealth_t2a"
    # 未知键原样返回
    assert normalize_skill_key("whatever") == "whatever"


def test_unlock_stores_canonical_key(state):
    sk = SkillSystem(state)
    assert sk.unlock_skill("karma_capacity_t1", 1) is True
    assert "karma_capacity_t1" in state._data["skills"]
    assert state.is_skill_unlocked("karma_capacity_t1", 1) is True
    # 幂等：重复解锁不重复扣点
    points_before = state.get("skill_points")
    assert sk.unlock_skill("karma_capacity_t1", 1) is False
    assert state.get("skill_points") == points_before


def test_unlocked_skill_changes_modifiers(state):
    sk = SkillSystem(state)
    assert state.get_skill_modifiers()["karma_max_bonus"] == 0
    sk.unlock_skill("karma_capacity_t1", 1)
    assert state.get_skill_modifiers()["karma_max_bonus"] == 20


def test_tier2_requires_prev_tier(state):
    sk = SkillSystem(state)
    # stealth_t1 为默认技能，但 karma_capacity 分支 T1 未解锁 → T2 不可解锁
    assert sk.unlock_skill("karma_capacity_t2b", 2) is False
    assert state.is_skill_unlocked("karma_capacity_t2b") is False
    assert sk.unlock_skill("karma_capacity_t1", 1) is True
    assert sk.unlock_skill("karma_capacity_t2b", 2) is True
    assert state.get_skill_modifiers()["karma_recover_multiplier"] == 1.3


def test_allowed_classifications_after_unlock(state):
    sk = SkillSystem(state)
    assert "C+" not in state.get_allowed_classifications()
    assert "D" not in state.get_allowed_classifications()
    sk.unlock_skill("cheat_mastery_t1a", 1)
    sk.unlock_skill("cheat_mastery_t1b", 1)
    allowed = state.get_allowed_classifications()
    assert "C+" in allowed and "D" in allowed


def test_legacy_key_migrates_on_load(tmp_path, monkeypatch):
    f = tmp_path / "legacy.json"
    f.write_text(json.dumps({
        "version": 4,
        "skills": {
            "karma_capacity_t1_t1": {"unlocked_at": "2026-01-01T00:00:00", "tier": 1},
            "stealth_t1": {"unlocked_at": "2026-01-01T00:00:00", "tier": 1},
        },
    }), encoding="utf-8")
    monkeypatch.setattr(state_mod, "STATE_FILE", f)
    s = SamsaraState()
    assert "karma_capacity_t1" in s._data["skills"]
    assert "karma_capacity_t1_t1" not in s._data["skills"]
    assert s.get_skill_modifiers()["karma_max_bonus"] == 20


# ── 2.2 一次性技能 ──────────────────────────────────────────────

def _unlock_stealth_t3a(state):
    """解锁金蝉脱壳（T3a）：默认已有 stealth_t1，需先补 T2 前置。"""
    sk = SkillSystem(state)
    assert sk.unlock_skill("stealth_t2b", 2) is True
    assert sk.unlock_skill("stealth_t3a", 3) is True


def test_first_overdraft_skip_consumed_once_per_level(state):
    sk = SkillSystem(state)
    sk.unlock_skill("stealth_t2a", 2)
    ds = DetectionSystem(state)

    r1 = ds.handle_overdraft(50.0)
    assert r1.get("skip") is True
    assert state.is_one_time_skill_used("stealth_t2a") is True

    # 同一关第二次透支：不再豁免，识破概率正常累积
    r2 = ds.handle_overdraft(50.0)
    assert r2.get("skip") is None and r2.get("delta", 0) > 0
    assert state.get_detection() > 0

    # 下一关开始：reset_level_state 重置，豁免恢复可用
    state.reset_level_state()
    assert state.is_one_time_skill_used("stealth_t2a") is False
    r3 = ds.handle_overdraft(50.0)
    assert r3.get("skip") is True


def test_golden_escape_consumed_once_per_level(state, monkeypatch):
    _unlock_stealth_t3a(state)
    ds = DetectionSystem(state)
    # 稳定命中：roll 恒为 0（必 detected）
    monkeypatch.setattr(state_mod, "STATE_FILE", state_mod.STATE_FILE)
    import samsara.detection as det_mod
    monkeypatch.setattr(det_mod.random, "random", lambda: 0.0)

    r1 = ds.handle_overdraft(50.0)
    assert r1.get("escaped") is True
    assert state.is_one_time_skill_used("stealth_t3a") is True

    # 同一关第二次命中：无金蝉脱壳，直接判识破
    r2 = ds.handle_overdraft(50.0)
    assert r2.get("detected") is True and r2.get("escaped") is None

    # 下一关：恢复可用
    state.reset_level_state()
    assert state.is_one_time_skill_used("stealth_t3a") is False


def test_skill_flag_gone_after_use_in_modifiers(state):
    """get_skill_modifiers 对已消耗的一次性技能返回 False（前端/棋类侧同步口径）。"""
    sk = SkillSystem(state)
    sk.unlock_skill("stealth_t2a", 2)
    assert state.get_skill_modifiers()["first_overdraft_skip"] is True
    state.consume_one_time_skill("stealth_t2a")
    assert state.get_skill_modifiers()["first_overdraft_skip"] is False
