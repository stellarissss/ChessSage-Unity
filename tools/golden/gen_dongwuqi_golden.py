#!/usr/bin/env python3
"""动物棋黄金等价性数据生成器（纯 stdlib）。

import legacy-web/dongwuqi/rule_engine.py 与 configs，跑覆盖 水域/陷阱/等级吃子/
狮虎跳河/兽穴/胜负 的固定场景序列，输出 Assets/ChessSage/Tests/Fixtures/dongwuqi_golden.json。

命令：cd /workspace/ChessSage-Unity && python3 tools/golden/gen_dongwuqi_golden.py
"""
import copy
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEGACY = ROOT / "legacy-web" / "dongwuqi"
CONFIGS = LEGACY / "configs"
OUT = ROOT / "Assets" / "ChessSage" / "Tests" / "Fixtures" / "dongwuqi_golden.json"

sys.path.insert(0, str(LEGACY))
from rule_engine import RuleEngine  # noqa: E402


def load(name):
    with open(CONFIGS / f"{name}.json", encoding="utf-8") as f:
        return json.load(f)


BOARD = load("board")
PIECES_RED = load("pieces_red")
PIECES_BLACK = load("pieces_black")
RULES = load("rules")

ENGINE = RuleEngine(BOARD, PIECES_RED, PIECES_BLACK, RULES)


def piece(pid, ptype, side, x, y, alive=True):
    return {
        "id": pid, "type": ptype, "name": ptype, "side": side,
        "position": [x, y], "is_alive": alive, "custom_properties": {},
    }


STANDARD_TRAPS = [
    piece("b_trap_a", "trap", "black", 2, 0),
    piece("b_trap_b", "trap", "black", 4, 0),
    piece("b_trap_c", "trap", "black", 3, 1),
    piece("r_trap_a", "trap", "red", 2, 8),
    piece("r_trap_b", "trap", "red", 4, 8),
    piece("r_trap_c", "trap", "red", 3, 7),
]


def state_of(pieces, turn="red"):
    return {
        "pieces": pieces, "current_turn": turn, "move_history": [],
        "game_status": {"state": "playing", "winner": None, "win_condition": None},
        "consumed_traps": [],
    }


def snapshot(st, label, move=None):
    moves = {}
    for p in st["pieces"]:
        if not p.get("is_alive", True) or ENGINE._is_terrain_piece(p):
            continue
        moves[p["id"]] = sorted(ENGINE.get_valid_moves(p, st))
    alive = sorted(p["id"] for p in st["pieces"]
                   if p.get("is_alive", True) and not ENGINE._is_terrain_piece(p))
    return {
        "label": label,
        "current_turn": st["current_turn"],
        "moves": moves,
        "alive": alive,
        "consumed_traps": sorted(st.get("consumed_traps", [])),
        "winner": ENGINE.check_win(st),
        "move": move,
    }


def apply_move(st, pid, to):
    p = next(x for x in st["pieces"] if x["id"] == pid)
    target = ENGINE._get_piece_at(list(to), st)
    p["position"] = list(to)
    if target:
        target["is_alive"] = False
    ENGINE.is_killed_by_trap(p, st)
    winner = ENGINE.check_win(st)
    if winner is None:
        st["current_turn"] = "black" if st["current_turn"] == "red" else "red"
        winner = ENGINE.check_win(st)
    return winner


def scenario(label, pieces, sequence, turn="red"):
    """sequence: [(piece_id, (x, y)), ...]，逐步应用并记录。"""
    init = state_of(copy.deepcopy(pieces), turn)
    st = copy.deepcopy(init)
    steps = [snapshot(st, label)]
    for i, (pid, to) in enumerate(sequence):
        apply_move(st, pid, to)
        steps.append(snapshot(st, f"{label}#{i + 1}", move={"piece_id": pid, "to": list(to)}))
    return {"label": label, "board_state": init, "steps": steps}


def build():
    scenarios = []

    # 1. 初始局面：真实 board_state 全棋子移动集合
    initial = load("board_state")
    initial["consumed_traps"] = []
    scenarios.append({
        "label": "initial",
        "board_state": initial,
        "steps": [snapshot(copy.deepcopy(initial), "initial")],
    })

    # 2. 水域：鼠可入水并在水中移动，水中鼠不能吃岸上象，象不能入水
    scenarios.append(scenario(
        "rat_water",
        [piece("r_rat", "rat", "red", 1, 5), piece("b_elephant", "elephant", "black", 1, 6),
         piece("b_cat", "cat", "black", 6, 0)],
        [("r_rat", (2, 5))],
    ))

    # 3. 等级吃子：陆地鼠吃象（鼠克象），象不能吃鼠
    scenarios.append(scenario(
        "rat_eats_elephant",
        [piece("r_rat", "rat", "red", 1, 1), piece("b_elephant", "elephant", "black", 1, 2),
         piece("b_cat", "cat", "black", 6, 0)],
        [("r_rat", (1, 2))],
    ))

    # 4. 陷阱：象吃陷阱中的鼠（降级为 0），红犬踩黑陷阱被吞噬
    scenarios.append(scenario(
        "trap",
        STANDARD_TRAPS + [
            piece("r_rat", "rat", "red", 2, 0), piece("b_elephant", "elephant", "black", 1, 0),
            piece("r_dog", "dog", "red", 3, 2), piece("r_wolf", "wolf", "red", 0, 6),
            piece("b_cat", "cat", "black", 6, 0),
        ],
        [("b_elephant", (2, 0)), ("r_dog", (3, 1))],
        turn="black",
    ))

    # 5. 狮虎跳河：正面跳河 / 水中有鼠阻挡
    scenarios.append(scenario(
        "lion_jump",
        [piece("b_lion", "lion", "black", 0, 3), piece("b_tiger", "tiger", "black", 6, 4),
         piece("r_cat", "cat", "red", 6, 6)],
        [("b_lion", (3, 3))],
        turn="black",
    ))
    scenarios.append(scenario(
        "lion_blocked",
        [piece("b_lion", "lion", "black", 0, 3), piece("r_rat", "rat", "red", 2, 3),
         piece("r_cat", "cat", "red", 6, 6)],
        [],
        turn="black",
    ))

    # 6. 兽穴：红狮从陷阱格进入黑兽穴获胜
    scenarios.append(scenario(
        "enter_den",
        [piece("r_lion", "lion", "red", 3, 1), piece("b_cat", "cat", "black", 6, 0),
         piece("r_cat", "cat", "red", 0, 6)],
        [("r_lion", (3, 0))],
    ))

    # 7. 全歼：黑方无动物
    scenarios.append(scenario(
        "annihilation",
        [piece("b_lion", "lion", "black", 3, 3)],
        [],
        turn="red",
    ))

    # 8. 困毙：黑猫被红象堵死
    scenarios.append(scenario(
        "stalemate",
        [piece("b_cat", "cat", "black", 0, 0), piece("r_elephant", "elephant", "red", 1, 0),
         piece("r_elephant2", "elephant", "red", 0, 1), piece("r_wolf", "wolf", "red", 6, 5)],
        [],
        turn="black",
    ))

    return {"variant": "dongwuqi", "engine": "rule_engine.py", "scenarios": scenarios}


def main():
    data = build()
    OUT.parent.mkdir(parents=True, exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
        f.write("\n")
    total = sum(len(s["steps"]) for s in data["scenarios"])
    print(f"写入 {OUT}：{len(data['scenarios'])} 场景 / {total} 步")


if __name__ == "__main__":
    main()