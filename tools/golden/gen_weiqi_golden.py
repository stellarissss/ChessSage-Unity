#!/usr/bin/env python3
"""生成「无限制围棋」规则引擎黄金夹具。

以 legacy-web/weiqi/rule_engine.py 为真源，回放若干确定性场景（提子、多子提、
打劫、禁自杀、黑棋禁手、机制原语限气/不可吃），逐步记录：
    - current_turn / 双方合法落点
    - 本步落子：side / to / placed_id / captured / 落子后棋盘快照 / 提子数
    - 胜负判定：game_over / winner / reason
    - next_turn
另附构造局面的胜负用例与气的计算用例，写入
Assets/ChessSage/Tests/Fixtures/weiqi_golden.json，供 C# 探针与 NUnit 测试比对。

命令：
    cd /workspace/ChessSage-Unity && python3 tools/golden/gen_weiqi_golden.py
"""
import copy
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEGACY = ROOT / "legacy-web" / "weiqi"
CONFIGS = LEGACY / "configs"
OUT = ROOT / "Assets" / "ChessSage" / "Tests" / "Fixtures" / "weiqi_golden.json"


def load_engine_class():
    """从 legacy-web/weiqi/rule_engine.py 加载 RuleEngine（纯 stdlib）。"""
    spec = importlib.util.spec_from_file_location("weiqi_rule_engine", LEGACY / "rule_engine.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.RuleEngine


def load_json(name):
    return json.loads((CONFIGS / f"{name}.json").read_text(encoding="utf-8"))


def deep_merge(base, override):
    """递归合并 rules_override，用于构造启用禁手 / 机制原语的规则变体。"""
    result = copy.deepcopy(base)
    for key, value in (override or {}).items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = deep_merge(result[key], value)
        else:
            result[key] = copy.deepcopy(value)
    return result


def make_piece(pid, side, x, y, alive=True):
    return {
        "id": pid,
        "type": "stone",
        "name": "●" if side == "black" else "○",
        "side": side,
        "position": [x, y],
        "is_alive": alive,
        "custom_properties": {},
    }


ENGINE_CLS = load_engine_class()
BOARD = load_json("board")
PIECES_RED = load_json("pieces_red")
PIECES_BLACK = load_json("pieces_black")
RULES = load_json("rules")


def build_engine(rules_override):
    return ENGINE_CLS(BOARD, PIECES_RED, PIECES_BLACK, deep_merge(RULES, rules_override))


def empty_board(captures=None):
    return {
        "pieces": [],
        "current_turn": "black",
        "move_history": [],
        "game_status": {"state": "playing", "winner": None, "win_condition": None},
        "ko_state": None,
        "captures": copy.deepcopy(captures or {"black": 0, "white": 0}),
    }


def alive_ids(board):
    return {p["id"] for p in board.get("pieces", []) if p.get("is_alive", True)}


def board_snapshot(board):
    """落子后棋盘快照：{"x,y": side}，仅活子，按键排序。"""
    snap = {}
    for p in board.get("pieces", []):
        if p.get("is_alive", True):
            snap[f"{p['position'][0]},{p['position'][1]}"] = p["side"]
    return dict(sorted(snap.items()))


def sort_placements(moves):
    return [[int(p[0]), int(p[1])] for p in sorted(moves, key=lambda p: (p[0], p[1]))]


def outcome(engine, board):
    """胜负判定链：先吃十子；再判全歼——某方曾落子但活子归零即被全歼。

    对应 rule_engine.check_capture_10 / check_capture_all，并以"曾存在"约束
    避免开局一方尚未落子就被误判全歼。
    """
    winner = engine.check_capture_10(board)
    if winner:
        return {"ended": True, "winner": winner, "condition": "capture_10"}

    pieces = board.get("pieces", [])
    ever = {s: any(p["side"] == s for p in pieces) for s in ("black", "white")}
    alive = {
        s: any(p["side"] == s and p.get("is_alive", True) for p in pieces)
        for s in ("black", "white")
    }
    if (ever["black"] and not alive["black"] and alive["white"]) or \
       (ever["white"] and not alive["white"] and alive["black"]):
        winner = engine.check_capture_all(board)
        if winner:
            return {"ended": True, "winner": winner, "condition": "capture_all"}
    return {"ended": False, "winner": None, "condition": None}


def snapshot_step(engine, board, label):
    """当前局面的快照（不含落子）。"""
    oc = outcome(engine, board)
    return {
        "label": label,
        "current_turn": board["current_turn"],
        "placements": {
            "black": sort_placements(engine.get_valid_moves(board, "black")),
            "white": sort_placements(engine.get_valid_moves(board, "white")),
        },
        "game_over": oc["ended"],
        "winner": oc["winner"],
        "reason": oc["condition"],
        "move": None,
        "next_turn": None,
    }


def build_scenario(name, moves, rules_override=None):
    """从空盘交替落子回放固定序列，逐步记录。moves 为 [[x,y], ...]（黑先）。"""
    engine = build_engine(rules_override)
    board = empty_board()
    steps = []

    for i, (x, y) in enumerate(moves):
        side = board["current_turn"]
        step = snapshot_step(engine, board, f"step{i}")

        prev_alive = alive_ids(board)
        new_board = engine.place_stone(board, x, y, side)
        captured = sorted(
            p["id"] for p in new_board["pieces"]
            if p["side"] != side and p["id"] in prev_alive and not p.get("is_alive", True)
        )
        step["move"] = {
            "side": side,
            "to": [int(x), int(y)],
            "placed_id": new_board["pieces"][-1]["id"],
            "captured": captured,
            "board_after": board_snapshot(new_board),
            "captures": {k: int(v) for k, v in new_board["captures"].items()},
            "ko_state": new_board.get("ko_state"),
        }

        oc = outcome(engine, new_board)
        board = new_board
        if oc["ended"]:
            step["next_turn"] = None
            steps.append(step)
            board["current_turn"] = side
            break

        board["current_turn"] = "white" if side == "black" else "black"
        step["next_turn"] = board["current_turn"]
        steps.append(step)

    steps.append(snapshot_step(engine, board, f"step{len(moves)}"))
    return {
        "name": name,
        "rules_override": rules_override or {},
        "moves": [[int(x), int(y)] for x, y in moves],
        "steps": steps,
    }


def build_outcome_case(name, pieces, captures=None, current_turn="black"):
    engine = build_engine(None)
    board = empty_board(captures)
    board["pieces"] = copy.deepcopy(pieces)
    board["current_turn"] = current_turn
    return {
        "name": name,
        "pieces": copy.deepcopy(pieces),
        "captures": copy.deepcopy(captures or {"black": 0, "white": 0}),
        "current_turn": current_turn,
        "outcome": outcome(engine, board),
    }


def build_liberty_case(name, pieces, at, rules_override=None):
    engine = build_engine(rules_override)
    board = empty_board()
    board["pieces"] = copy.deepcopy(pieces)
    return {
        "name": name,
        "rules_override": rules_override or {},
        "pieces": copy.deepcopy(pieces),
        "at": [int(at[0]), int(at[1])],
        "liberties": engine.calculate_liberties(board, at[0], at[1]),
    }


def build():
    scenarios = [
        # 空盘：全部空点合法
        build_scenario("empty_board", []),

        # 提子即胜：黑三面围住 (1,0)，白在 (0,0) 落子提掉该子（此点表面无气，但可提子故合法），
        # 提子后写下打劫点，(1,0) 立即提回被禁止。
        build_scenario("capture_and_ko", [
            (1, 0), (2, 0), (0, 1), (1, 1), (10, 10), (0, 0),
        ]),

        # 多子提：黑围住白两子连通块，最后一气填满整块被提。
        build_scenario("multi_capture", [
            (1, 1), (2, 1), (1, 2), (2, 2), (2, 0), (10, 10),
            (2, 3), (11, 10), (3, 1), (12, 10), (3, 2),
        ]),

        # 禁自杀：角点 (0,0) 两邻皆白且提不到子，黑落子自杀被禁。
        build_scenario("suicide_forbidden", [
            (5, 5), (1, 0), (6, 5), (0, 1),
        ]),

        # 全歼获胜：白仅一子于角上，黑落 (0,1) 提净白子，白被全歼黑胜。
        build_scenario("capture_all_replay", [
            (1, 0), (0, 0), (0, 1),
        ]),

        # 黑棋禁手 - 长连：黑五连后落在 (5,0) 成六连，被禁。
        build_scenario("forbidden_overline", [
            (0, 0), (10, 10), (1, 0), (11, 10), (2, 0), (12, 10),
            (3, 0), (13, 10), (4, 0), (14, 10),
        ], rules_override={"special_rules": {"forbidden_black": {"enabled": True}}}),

        # 黑棋禁手 - 双活三：黑落 (2,2) 同时形成横竖两个活三，被禁。
        build_scenario("forbidden_double_three", [
            (1, 2), (10, 10), (3, 2), (11, 10), (2, 1), (12, 10),
            (2, 3), (13, 10),
        ], rules_override={"special_rules": {"forbidden_black": {"enabled": True}}}),

        # 机制原语 - 不可吃：白 (1,1) 设为不可吃，黑填满其最后一气也不提子。
        build_scenario("uncapturable_blocks_capture", [
            (0, 1), (1, 1), (1, 0), (10, 10), (2, 1), (11, 10), (1, 2),
        ], rules_override={"modifiers": {"go": {"white": {"uncapturable": True}}}}),
    ]

    cases = [
        build_outcome_case("ongoing_empty", []),
        build_outcome_case("ongoing_both_alive",
                           [make_piece("b0", "black", 1, 1), make_piece("w0", "white", 2, 2)]),
        # 仅一方落子、对方从未存在 → 不算全歼
        build_outcome_case("ongoing_single_side",
                           [make_piece("b0", "black", 1, 1)]),
        build_outcome_case("capture_10_black", [], captures={"black": 10, "white": 3}),
        build_outcome_case("capture_10_white", [], captures={"black": 9, "white": 10}),
        # 吃十子优先于全歼
        build_outcome_case("capture_10_over_capture_all",
                           [make_piece("b0", "black", 1, 1), make_piece("w0", "white", 2, 2, alive=False)],
                           captures={"black": 10, "white": 0}),
        # 白曾落子且活子归零、黑存活 → 黑全歼胜
        build_outcome_case("capture_all_black",
                           [make_piece("b0", "black", 1, 1), make_piece("w0", "white", 2, 2, alive=False)]),
        build_outcome_case("capture_all_white",
                           [make_piece("w0", "white", 2, 2), make_piece("b0", "black", 1, 1, alive=False)]),
    ]

    liberty_cases = [
        build_liberty_case("no_cap_corner", [make_piece("b0", "black", 0, 0)], (0, 0)),
        build_liberty_case("no_cap_center", [make_piece("b0", "black", 5, 5)], (5, 5)),
        build_liberty_case("liberty_cap_color",
                           [make_piece("b0", "black", 5, 5)], (5, 5),
                           rules_override={"modifiers": {"go": {"black": {"liberty_cap": 2}}}}),
        build_liberty_case("liberty_cap_symbol",
                           [make_piece("b0", "black", 5, 5)], (5, 5),
                           rules_override={"modifiers": {"go": {"●": {"liberty_cap": 1}}}}),
        build_liberty_case("liberty_cap_not_applied_to_white",
                           [make_piece("w0", "white", 5, 5)], (5, 5),
                           rules_override={"modifiers": {"go": {"black": {"liberty_cap": 1}}}}),
    ]

    return {
        "variant": "weiqi",
        "engine": "rule_engine.py",
        "geometry": {
            "width": BOARD["geometry"]["width"],
            "height": BOARD["geometry"]["height"],
        },
        "scenarios": scenarios,
        "cases": cases,
        "liberty_cases": liberty_cases,
    }


def main():
    data = build()
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(data, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    total = sum(len(s["steps"]) for s in data["scenarios"])
    print(f"已生成 {OUT}")
    print(f"  scenarios={len(data['scenarios'])}/{total} 步 "
          f"cases={len(data['cases'])} liberty_cases={len(data['liberty_cases'])}")


if __name__ == "__main__":
    main()