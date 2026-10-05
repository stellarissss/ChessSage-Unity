# -*- coding: utf-8 -*-
"""围棋机制原语（限气 liberty_cap / 不可吃 uncapturable）校验脚本

对 RPG 与 sandbox 两套规则引擎分别验证：
- 限气：报告/用于提子的气数为 min(实际气, cap)；被打吃（有效气=1）仍可被提。
- 不可吃：气尽亦不落子数变化、棋子保留。
- 默认配置（不配 mechanisms）行为与原规则完全一致。
"""
import importlib.util
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
RPG_PATH = str(ROOT / "weiqi" / "rule_engine.py")
SND_PATH = str(ROOT / "sandbox" / "weiqi" / "rule_engine.py")


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


RPG = _load(RPG_PATH, "rpg_rule_engine")
SND = _load(SND_PATH, "sandbox_rule_engine")

CAP3 = {"modifiers": {"go": {"black": {"liberty_cap": 3}}}}
UNCAP_BLACK = {"modifiers": {"go": {"black": {"uncapturable": True}}}}
GEOM = {"geometry": {"width": 9, "height": 9}}

passed = 0


def check(label, cond):
    global passed
    assert cond, f"FAIL: {label}"
    passed += 1
    print(f"  ✓ {label}")


def _piece(x, y, side, label):
    return {
        "id": f"{side}_{x}_{y}",
        "type": "stone",
        "name": label,
        "side": side,
        "position": [x, y],
        "is_alive": True,
        "custom_properties": {},
    }


def _state(pieces):
    return {"pieces": pieces, "captures": {"black": 0, "white": 0},
            "current_turn": "white", "ko_state": None}


def _new_engine(mod, rules):
    return mod.RuleEngine(GEOM, {"pieces": {}}, {"pieces": {}}, rules)


@pytest.mark.parametrize("mod, tag", [(RPG, "rpg"), (SND, "sandbox")])
def test_fastboard_liberty_cap(mod, tag):
    print(f"[FastBoard·{tag}] 限气 liberty_cap=3")
    fb = mod.FastBoard(9, 9).configure_modifiers(CAP3)
    fb.board[4][4] = mod.FastBoard.BLACK
    fb.board[4][3] = mod.FastBoard.BLACK          # 实际 6 气
    check(f"{tag} 报告有效气=3（实际6气被clamp）", fb._get_group_liberties(4, 4) == 3)

    fb0 = mod.FastBoard(9, 9)
    fb0.board[4][4] = mod.FastBoard.BLACK
    fb0.board[4][3] = mod.FastBoard.BLACK
    check(f"{tag} 默认（不配原语）报告实际6气", fb0._get_group_liberties(4, 4) == 6)

    # 有效气=3，无法被一手提掉（"仅当气<=1可提"时因有效=3而失败）
    cap_before = fb.captures[mod.FastBoard.WHITE]
    ok, captured = fb.play_move(3, 4, mod.FastBoard.WHITE)
    check(f"{tag} 有效气=3 时一口提不了黑（提子0）", ok and captured == 0)
    check(f"{tag} 黑棋子仍在", fb.board[4][4] == mod.FastBoard.BLACK)
    check(f"{tag} 白提子数未变", fb.captures[mod.FastBoard.WHITE] == cap_before)

    # 实际气降到1（被打吃，有效=1）时仍可被提 —— cap 不阻断真实提子
    fb1 = mod.FastBoard(9, 9).configure_modifiers(CAP3)
    fb1.board[0][0] = mod.FastBoard.BLACK
    fb1.board[0][1] = mod.FastBoard.WHITE          # 占黑一气
    check(f"{tag} 打吃时有效气=1（atari）", fb1._get_group_liberties(0, 0) == 1)
    c_before = fb1.captures[mod.FastBoard.WHITE]
    ok, captured = fb1.play_move(1, 0, mod.FastBoard.WHITE)
    check(f"{tag} 有效气=1 仍可被提（提1子）", ok and captured == 1)
    check(f"{tag} 黑被提走", fb1.board[0][0] == mod.FastBoard.EMPTY)
    check(f"{tag} 白提子数+1", fb1.captures[mod.FastBoard.WHITE] == c_before + 1)


@pytest.mark.parametrize("mod, tag", [(RPG, "rpg"), (SND, "sandbox")])
def test_engine_liberty_cap(mod, tag):
    print(f"[RuleEngine·{tag}] 限气 liberty_cap=3")
    eng = _new_engine(mod, CAP3)
    st = _state([_piece(4, 4, "black", "●"), _piece(4, 3, "black", "●")])
    check(f"{tag} calculate_liberties 报告有效气=3（实际6）", eng.calculate_liberties(st, 4, 4) == 3)

    eng0 = _new_engine(mod, {})
    check(f"{tag} 默认报告实际气=6", eng0.calculate_liberties(st, 4, 4) == 6)

    # 打吃时（有效=1）白仍可提黑 —— "实际少气仍可吃"
    st1 = _state([_piece(0, 0, "black", "●"), _piece(0, 1, "white", "○")])
    check(f"{tag} 打吃有效气=1", eng.calculate_liberties(st1, 0, 0) == 1)
    new = eng.place_stone(st1, 1, 0, "white")
    black_alive = [p for p in new["pieces"] if p["side"] == "black" and p["is_alive"]]
    check(f"{tag} 白提黑成功（黑被提）", not black_alive)
    check(f"{tag} 白提子数=1", new["captures"]["white"] == 1)


@pytest.mark.parametrize("mod, tag", [(RPG, "rpg"), (SND, "sandbox")])
def test_fastboard_uncapturable(mod, tag):
    print(f"[FastBoard·{tag}] 不可吃 uncapturable=true")
    fb = mod.FastBoard(9, 9).configure_modifiers(UNCAP_BLACK)
    fb.board[4][4] = mod.FastBoard.BLACK
    fb.board[3][4] = mod.FastBoard.WHITE
    fb.board[5][4] = mod.FastBoard.WHITE
    fb.board[4][3] = mod.FastBoard.WHITE          # 仅剩一口 (4,5)
    c_before = fb.captures[mod.FastBoard.WHITE]
    ok, captured = fb.play_move(4, 5, mod.FastBoard.WHITE)
    check(f"{tag} 气尽落子成功（ok=True）", ok)
    check(f"{tag} 不可吃——提子数为0", captured == 0)
    check(f"{tag} 黑子仍在", fb.board[4][4] == mod.FastBoard.BLACK)
    check(f"{tag} 白提子数不变", fb.captures[mod.FastBoard.WHITE] == c_before)
    check(f"{tag} get_captured_stones 不含黑", fb.get_captured_stones(4, 5, mod.FastBoard.WHITE) == [])


@pytest.mark.parametrize("mod, tag", [(RPG, "rpg"), (SND, "sandbox")])
def test_engine_uncapturable(mod, tag):
    print(f"[RuleEngine·{tag}] 不可吃 uncapturable=true")
    eng = _new_engine(mod, UNCAP_BLACK)
    st = _state([_piece(4, 4, "black", "●")])
    for wx, wy in [(3, 4), (5, 4), (4, 3), (4, 5)]:
        st = eng.place_stone(st, wx, wy, "white")
    black_piece = [p for p in st["pieces"] if p["side"] == "black"][0]
    check(f"{tag} 黑气尽仍存留", black_piece["is_alive"])
    check(f"{tag} 白提子数不变", st["captures"]["white"] == 0)
    check(f"{tag} remove_group 不可移除（返回0）", eng.remove_group(st, 4, 4) == 0)


@pytest.mark.parametrize("mod, tag", [(RPG, "rpg"), (SND, "sandbox")])
def test_default_unchanged(mod, tag):
    print(f"[默认·{tag}] 不配置机制原语时行为不变")
    fb = mod.FastBoard(9, 9)
    fb.board[4][4] = mod.FastBoard.BLACK
    fb.board[3][4] = mod.FastBoard.WHITE
    fb.board[5][4] = mod.FastBoard.WHITE
    fb.board[4][3] = mod.FastBoard.WHITE
    ok, captured = fb.play_move(4, 5, mod.FastBoard.WHITE)
    check(f"{tag} 默认黑可被围吃（提1子）", ok and captured == 1)
    check(f"{tag} 默认黑被提走", fb.board[4][4] == mod.FastBoard.EMPTY)

    eng = _new_engine(mod, {})
    st = _state([_piece(4, 4, "black", "●")])
    for wx, wy in [(3, 4), (5, 4), (4, 3), (4, 5)]:
        st = eng.place_stone(st, wx, wy, "white")
    black_piece = [p for p in st["pieces"] if p["side"] == "black"][0]
    check(f"{tag} 默认黑正常被提", not black_piece["is_alive"])
    check(f"{tag} 默认白提子数=1", st["captures"]["white"] == 1)


def run_suite(tag):
    print(f"\n====== 引擎：{tag} ======")
    test_fastboard_liberty_cap(RPG if tag == "rpg" else SND, tag)
    test_engine_liberty_cap(RPG if tag == "rpg" else SND, tag)
    test_fastboard_uncapturable(RPG if tag == "rpg" else SND, tag)
    test_engine_uncapturable(RPG if tag == "rpg" else SND, tag)
    test_default_unchanged(RPG if tag == "rpg" else SND, tag)


if __name__ == "__main__":
    run_suite("rpg")
    run_suite("sandbox")
    print(f"\nALL PASSED ({passed} assertions)")