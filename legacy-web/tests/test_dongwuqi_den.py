# -*- coding: utf-8 -*-
"""动物棋兽穴回归测试：进敌方兽穴判胜、兽穴不被当作陷阱、enter_den 配置开关生效。"""
import sys
import os
import json
import copy
import importlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def _load_engine(base: str):
    """按模式独立加载 rule_engine（避免同名字模块缓存串扰）。"""
    module_dir = os.path.join(ROOT, base)
    sys.path.insert(0, module_dir)
    mod = importlib.import_module("rule_engine")
    sys.path.pop(0)
    sys.modules.pop("rule_engine", None)

    d = os.path.join(module_dir, "configs")
    board = json.load(open(os.path.join(d, "board.json"), encoding="utf-8"))
    pr = json.load(open(os.path.join(d, "pieces_red.json"), encoding="utf-8"))
    pb = json.load(open(os.path.join(d, "pieces_black.json"), encoding="utf-8"))
    rules = json.load(open(os.path.join(d, "rules.json"), encoding="utf-8"))
    bs = json.load(open(os.path.join(d, "board_state.json"), encoding="utf-8"))
    return mod.RuleEngine(board, pr, pb, rules), bs


def _den_only_board(bs, at=(3, 0), side="red"):
    bs2 = copy.deepcopy(bs)
    for p in bs2.get("pieces", []):
        p["is_alive"] = False
    bs2["pieces"] = [{
        "id": "RAT", "type": "rat", "name": "🐭", "side": side,
        "position": list(at), "is_alive": True, "custom_properties": {},
    }]
    bs2["current_turn"] = side
    return bs2


def run(base):
    re, bs = _load_engine(base)
    print(f"== {base}")

    # 1) 进入敌方兽穴判胜（红子进 black 兽穴）
    w = re.check_win(_den_only_board(bs, (3, 0), "red"))
    assert w == "red", f"{base}: 红子进黑穴应胜 red, got {w}"

    # 2) 黑子进 red 兽穴判胜
    w = re.check_win(_den_only_board(bs, (3, 8), "black"))
    assert w == "black", f"{base}: 黑子进红穴应胜 black, got {w}"

    # 3) 兽穴不被当作陷阱（参数随各模式签名适配）
    try:
        den_as_trap = re._is_in_trap([3, 0], "red", bs)
    except TypeError:
        den_as_trap = re._is_in_trap([3, 0])
    assert den_as_trap is False, f"{base}: 兽穴(3,0)不应被判为陷阱"

    # 4) 真陷阱格仍判为陷阱
    try:
        trap_real = re._is_in_trap([2, 0], "red", bs)
    except TypeError:
        trap_real = re._is_in_trap([2, 0])
    assert trap_real is True, f"{base}: 真陷阱(2,0)应判为陷阱"

    # 5) enter_den 配置开关：关闭后进兽穴不再判胜（但全歼/困毙仍生效）
    re2, bs2 = _load_engine(base)
    wc = re2.rules.setdefault("win_conditions", {}).setdefault("enter_den", {})
    wc["enabled"] = False
    assert re2.check_win(_den_only_board(bs2, (3, 0), "red")) is None, \
        f"{base}: enter_den.enabled=False 时进兽穴不应判胜"
    wc["enabled"] = True
    assert re2.check_win(_den_only_board(bs2, (3, 0), "red")) == "red", \
        f"{base}: enter_den.enabled=True 时进兽穴应判胜 red"

    print("  PASS")
    return True


ok = True
for g in ["dongwuqi", "sandbox/dongwuqi"]:
    ok = run(g) and ok
print("ALL PASS" if ok else "FAILED")
assert ok