#!/usr/bin/env python3
"""生成中国跳棋黄金等价性夹具。

纯 stdlib：导入 legacy-web/tiaoqi/rule_engine.py 与 configs，跑固定序列（含连跳），
记录每步的 current_turn、每个棋子的合法移动集合（排序）与胜负，写入
Assets/ChessSage/Tests/Fixtures/tiaoqi_golden.json。
"""
import copy
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TIAOQI = ROOT / "legacy-web" / "tiaoqi"
sys.path.insert(0, str(TIAOQI))
from rule_engine import RuleEngine  # noqa: E402

CONFIGS = TIAOQI / "configs"


def load(name):
    return json.loads((CONFIGS / f"{name}.json").read_text(encoding="utf-8"))


engine = RuleEngine(load("board"), load("pieces_red"), load("pieces_black"), load("rules"))


def record(label, board_state):
    """记录一个局面的合法移动集合、胜负与每方是否有棋可走。"""
    moves = {}
    side_has_moves = {"red": False, "black": False}
    for p in board_state["pieces"]:
        if not p.get("is_alive", True):
            continue
        mv = engine.get_valid_moves(p, board_state)
        moves[p["id"]] = sorted([m[0], m[1]] for m in mv)
        if mv:
            side_has_moves[p["side"]] = True
    return {
        "label": label,
        "current_turn": board_state.get("current_turn", "red"),
        "pieces": [
            {
                "id": p["id"],
                "type": p["type"],
                "side": p["side"],
                "position": list(p["position"]),
                "is_alive": p.get("is_alive", True),
            }
            for p in board_state["pieces"]
        ],
        "moves": moves,
        "winner": engine.is_all_in_camp(board_state),
        "side_has_moves": side_has_moves,
    }


def scenario_initial():
    """从真实初始局面出发，按字典序最小合法动作走 8 步。"""
    bs = copy.deepcopy(load("board_state"))
    steps = [record("initial", bs)]
    for _ in range(8):
        side = bs["current_turn"]
        actions = []
        for p in bs["pieces"]:
            if not p.get("is_alive", True) or p["side"] != side:
                continue
            for m in engine.get_valid_moves(p, bs):
                actions.append((p["id"], m[0], m[1]))
        if not actions:
            break
        actions.sort()
        pid, x, y = actions[0]
        for p in bs["pieces"]:
            if p["id"] == pid:
                p["position"] = [x, y]
                break
        bs["current_turn"] = "black" if side == "red" else "red"
        steps.append(record(f"{pid}->[{x},{y}]", bs))
    return {"name": "initial_sequence", "steps": steps}


def scenario_hop_chain():
    """构造一条跳板链，验证 hop 与连跳终点。"""
    pieces = [
        {"id": "r_mover", "type": "piece", "name": "红", "side": "red", "position": [5, 1], "is_alive": True, "custom_properties": {}},
        {"id": "b_p1", "type": "piece", "name": "黑", "side": "black", "position": [5, 3], "is_alive": True, "custom_properties": {}},
        {"id": "b_p2", "type": "piece", "name": "黑", "side": "black", "position": [5, 7], "is_alive": True, "custom_properties": {}},
        {"id": "b_p3", "type": "piece", "name": "黑", "side": "black", "position": [5, 11], "is_alive": True, "custom_properties": {}},
    ]
    bs = {"pieces": pieces, "current_turn": "red"}
    steps = [record("chain_start", bs)]

    # 走一步：红子跳到最远连跳终点，再记录落点后的局面
    best = None
    for m in engine.get_valid_moves(pieces[0], bs):
        dist = abs(m[0] - pieces[0]["position"][0]) + abs(m[1] - pieces[0]["position"][1])
        if best is None or dist > best[0]:
            best = (dist, m)
    if best is not None:
        pieces[0]["position"] = [best[1][0], best[1][1]]
        bs["current_turn"] = "black"
        steps.append(record(f"r_mover->[{best[1][0]},{best[1][1]}]", bs))
    return {"name": "hop_chain", "steps": steps}


def main():
    fixture = {
        "variant": "tiaoqi",
        "engine": "rule_engine.py",
        "scenarios": [scenario_initial(), scenario_hop_chain()],
    }
    out = ROOT / "Assets" / "ChessSage" / "Tests" / "Fixtures" / "tiaoqi_golden.json"
    out.write_text(json.dumps(fixture, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()