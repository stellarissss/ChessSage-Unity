# -*- coding: utf-8 -*-
"""动物棋陷阱捕食回归：踩中敌方陷阱的动物立即被吞噬（死亡），无法进而犯兽穴。"""
import os
import sys
import json
import copy
import importlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def _load(base):
    md = os.path.join(ROOT, base)
    sys.path.insert(0, md)
    mod = importlib.import_module("rule_engine")
    sys.path.pop(0)
    sys.modules.pop("rule_engine", None)
    d = os.path.join(md, "configs")
    board = json.load(open(os.path.join(d, "board.json"), encoding="utf-8"))
    pr = json.load(open(os.path.join(d, "pieces_red.json"), encoding="utf-8"))
    pb = json.load(open(os.path.join(d, "pieces_black.json"), encoding="utf-8"))
    rules = json.load(open(os.path.join(d, "rules.json"), encoding="utf-8"))
    bs = json.load(open(os.path.join(d, "board_state.json"), encoding="utf-8"))
    return mod.RuleEngine(board, pr, pb, rules), bs


def run(base):
    re, bs = _load(base)
    print(f"== {base}")

    def mk(pid, typ, side, pos, alive=True):
        return {"id": pid, "type": typ, "side": side, "position": list(pos),
                "is_alive": alive, "custom_properties": {}}

    # 1) 敌方正踩在敌方陷阱上：立即被吞噬
    bs2 = copy.deepcopy(bs)
    for p in bs2.get("pieces", []):
        p["is_alive"] = False
    red_ele = mk("RE", "elephant", "red", [2, 0])       # 红象踩在黑方陷阱 (2,0)
    bs2["pieces"] = [red_ele]
    killed = re.is_killed_by_trap(red_ele, bs2)
    assert killed is True, f"{base}: 红象踩黑陷阱应被吞噬"
    assert red_ele["is_alive"] is False, f"{base}: 被吞噬后 is_alive 应为 False"
    # 一次性陷阱：吞噬后陷阱自身也消失——记入 consumed_traps；若该格存在陷阱棋子则一并置亡
    assert list(bs2.get("consumed_traps", [[]])[0]) == [2, 0], f"{base}: 应记录消耗陷阱格"
    for tp in bs2.get("pieces", []):
        if tp.get("type") == "trap" and list(tp.get("position", [])) == [2, 0]:
            assert tp.get("is_alive") is False, f"{base}: 陷阱棋子应随吞噬消失"

    # 2) 己方陷阱不作用
    bs3 = copy.deepcopy(bs)
    for p in bs3.get("pieces", []):
        p["is_alive"] = False
    red_ele2 = mk("RE2", "elephant", "red", [2, 8])     # 红象在红方陷阱 (2,8), 非敌方陷阱
    bs3["pieces"] = [red_ele2]
    assert re.is_killed_by_trap(red_ele2, bs3) is False, f"{base}: 己方陷阱不应吞噬"

    # 3) 非陷阱格不吞噬
    red_ele3 = mk("RE3", "elephant", "red", [0, 0])
    bs4 = copy.deepcopy(bs)
    for p in bs4.get("pieces", []):
        p["is_alive"] = False
    bs4["pieces"] = [red_ele3]
    assert re.is_killed_by_trap(red_ele3, bs4) is False, f"{base}: 普通格不吞噬"

    print("  PASS")


for g in ["dongwuqi", "sandbox/dongwuqi"]:
    run(g)
print("ALL PASS")