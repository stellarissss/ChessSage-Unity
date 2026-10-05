#!/usr/bin/env python3
"""生成五子棋规则引擎的黄金等价性夹具。

以 legacy-web/wuziqi 的 Python 引擎（rule_engine.py）为真源，跑一段固定的落子序列，
逐步记录 current_turn / 合法落子集合 / 胜负判定，写入 wuziqi_golden.json，
供 C# 探针与 NUnit 测试比对。

用法：python3 tools/golden/gen_wuziqi_golden.py
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WUZIQI_DIR = ROOT / "legacy-web" / "wuziqi"
CONFIGS_DIR = WUZIQI_DIR / "configs"
OUT_PATH = ROOT / "Assets" / "ChessSage" / "Tests" / "Fixtures" / "wuziqi_golden.json"

sys.path.insert(0, str(WUZIQI_DIR))
from rule_engine import RuleEngine  # noqa: E402


def load(name):
    with open(CONFIGS_DIR / f"{name}.json", encoding="utf-8") as f:
        return json.load(f)


BOARD = load("board")
PIECES_RED = load("pieces_red")
PIECES_BLACK = load("pieces_black")
RULES = load("rules")
ENGINE = RuleEngine(BOARD, PIECES_RED, PIECES_BLACK, RULES)

WIDTH = BOARD["geometry"]["width"]
HEIGHT = BOARD["geometry"]["height"]
TOTAL_CELLS = WIDTH * HEIGHT


def make_piece(side, x, y, index):
    """按 main.py 的落子结构构造棋子条目。"""
    return {
        "id": f"{side}_stone_{x}_{y}_{index}",
        "type": "stone",
        "name": "●" if side == "black" else "○",
        "side": side,
        "position": [x, y],
        "is_alive": True,
        "custom_properties": {},
    }


def evaluate(board):
    """复刻 main.py 的胜负判定：先五连珠，再棋盘下满判和。"""
    winner = ENGINE.check_five_in_a_row(board)
    if winner:
        return {"ended": True, "winner": winner, "condition": "five_in_a_row", "draw": False}
    if ENGINE.is_game_over(board) == "draw":
        return {"ended": True, "winner": None, "condition": "stalemate", "draw": True}
    return {"ended": False, "winner": None, "condition": None, "draw": False}


def placements(board):
    """主程序 /api/valid_moves 无 piece_id 分支：全部空点，按 (x,y) 排序。"""
    occupied = {
        (p["position"][0], p["position"][1])
        for p in board.get("pieces", [])
        if p.get("is_alive", True)
    }
    return [[x, y] for x in range(WIDTH) for y in range(HEIGHT) if (x, y) not in occupied]


def snapshot(board, current_turn, label, last_piece=None):
    return {
        "label": label,
        "current_turn": current_turn,
        "placements": placements(board),
        "last_piece": last_piece,
        "outcome": evaluate(board),
    }


def replay(name, moves):
    """从空盘回放固定落子序列，逐步快照。moves 为 [[x,y], ...]（黑先）。"""
    board = {"pieces": []}
    current_turn = "black"
    steps = [snapshot(board, current_turn, "initial")]

    for i, (x, y) in enumerate(moves):
        board["pieces"].append(make_piece(current_turn, x, y, len(board["pieces"])))
        if not ENGINE.check_five_in_a_row(board):
            current_turn = "white" if current_turn == "black" else "black"
        steps.append(snapshot(board, current_turn, f"after_move_{i + 1}", board["pieces"][-1]))

    return {"name": name, "moves": [list(m) for m in moves], "steps": steps}


def outcome_case(name, placements_list, current_turn="black"):
    pieces = [make_piece(side, x, y, i) for i, (side, x, y) in enumerate(placements_list)]
    board = {"pieces": pieces}
    return {
        "name": name,
        "current_turn": current_turn,
        "pieces": pieces,
        "outcome": evaluate(board),
    }


def full_board_draw_case():
    """构造 15x15 满盘且四方向均无五连（模式 ((x + 2y) % 4) < 2，单方向最长连子数为 2）。"""
    cells = []
    for x in range(WIDTH):
        for y in range(HEIGHT):
            side = "black" if ((x + 2 * y) % 4) < 2 else "white"
            cells.append((side, x, y))
    case = outcome_case("full_board_draw", cells)
    assert case["outcome"]["draw"] and case["outcome"]["ended"], "满盘构造应判和"
    return case


def main():
    scenarios = [
        # 黑方横五连获胜
        replay("horizontal_black", [(7, 7), (0, 0), (8, 7), (0, 1), (9, 7), (0, 2), (10, 7), (0, 3), (11, 7)]),
        # 白方竖五连获胜
        replay("vertical_white", [(0, 0), (1, 6), (2, 0), (1, 7), (3, 0), (1, 8), (4, 0), (1, 9), (0, 1), (1, 10)]),
        # 黑方 "\" 斜五连获胜
        replay("diagonal_black", [(6, 6), (0, 0), (7, 7), (0, 1), (8, 8), (0, 2), (9, 9), (0, 3), (10, 10)]),
        # 白方 "/" 斜五连获胜
        replay("anti_diagonal_white", [(0, 0), (10, 6), (1, 0), (9, 7), (2, 0), (8, 8), (3, 0), (7, 9), (4, 1), (6, 10)]),
    ]

    cases = [
        outcome_case("empty_board", []),
        outcome_case("four_only",
                     [("black", 0, 0), ("black", 1, 0), ("black", 2, 0), ("black", 3, 0),
                      ("white", 0, 1), ("white", 1, 1), ("white", 2, 1), ("white", 3, 1)]),
        outcome_case("six_in_a_row",
                     [("black", x, 0) for x in range(6)]),
        outcome_case("edge_row_five",
                     [("black", x, HEIGHT - 1) for x in range(5)]),
        outcome_case("edge_col_five",
                     [("white", WIDTH - 1, y) for y in range(5)]),
        outcome_case("diagonal_five",
                     [("black", 2 + i, 2 + i) for i in range(5)]),
        outcome_case("anti_diagonal_five",
                     [("white", 6 - i, 2 + i) for i in range(5)]),
        outcome_case("both_five",
                     [("white", x, 0) for x in range(5)] + [("black", 5 + i, 5 + i) for i in range(5)]),
        full_board_draw_case(),
    ]

    golden = {
        "variant": "wuziqi",
        "engine": "rule_engine.py",
        "geometry": {"width": WIDTH, "height": HEIGHT},
        "scenarios": scenarios,
        "cases": cases,
    }

    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    with open(OUT_PATH, "w", encoding="utf-8") as f:
        json.dump(golden, f, ensure_ascii=False, indent=1)
        f.write("\n")

    print(f"已生成 {OUT_PATH}")
    print(f"  scenarios={len(scenarios)} cases={len(cases)}")


if __name__ == "__main__":
    main()