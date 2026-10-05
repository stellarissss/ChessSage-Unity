#!/usr/bin/env python3
"""生成黑白棋黄金夹具。

以 legacy-web/heibaiqi/rule_engine.py 为真源，从标准开局出发走一条确定性的
固定落子序列（每步取字典序最小的合法落子），逐步骤记录：
    - label / current_turn
    - 双方合法落子集合（排序）
    - 本步落子：side / to / 新 disc id / 翻转子集 / 落子后棋盘快照
    - 胜负判定：game_over / winner / reason
    - next_turn：落子并处理"无路可走则跳过"后的回合方

命令：
    cd /workspace/ChessSage-Unity && python3 tools/golden/gen_heibaiqi_golden.py
"""
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEGACY = ROOT / "legacy-web" / "heibaiqi"
CONFIGS = LEGACY / "configs"
OUT = ROOT / "Assets" / "ChessSage" / "Tests" / "Fixtures" / "heibaiqi_golden.json"


def load_engine_class():
    """从 legacy-web/heibaiqi/rule_engine.py 加载 RuleEngine（纯 stdlib）。"""
    spec = importlib.util.spec_from_file_location("heibaiqi_rule_engine", LEGACY / "rule_engine.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.RuleEngine


def load_json(name):
    return json.loads((CONFIGS / f"{name}.json").read_text(encoding="utf-8"))


def other_side(side):
    return "white" if side == "black" else "black"


def sort_placements(placements):
    return [[int(p[0]), int(p[1])] for p in sorted(placements, key=lambda p: (p[0], p[1]))]


def board_snapshot(board):
    """落子后棋盘快照：{"x,y": side}，仅活子，按键排序。"""
    snap = {}
    for p in board.get("pieces", []):
        if p.get("is_alive", True):
            snap[f"{p['position'][0]},{p['position'][1]}"] = p["side"]
    return dict(sorted(snap.items()))


def outcome_reason(board, engine, total_cells):
    """对应 is_game_over：棋盘满 / 双方均无合法落子 → 结束原因，否则 None。"""
    alive = [p for p in board.get("pieces", []) if p.get("is_alive", True)]
    if len(alive) >= total_cells:
        return "board_full"
    sides = []
    for p in alive:
        if p["side"] not in sides:
            sides.append(p["side"])
    if not sides:
        return "no_valid_moves_both"
    for s in sides:
        if engine.get_valid_placements(s, board):
            return None
    return "no_valid_moves_both"


def place_disc_and_flip(board, to_pos, side, engine):
    """对应 main.py/_place_disc_and_flip：新增 disc 后翻转，返回 (new_id, flipped_ids)。"""
    max_num = 0
    prefix = f"{side}_disc_"
    for p in board.get("pieces", []):
        if p.get("side") == side and p.get("id", "").startswith(prefix):
            try:
                num = int(p["id"].split("_")[-1])
                if num > max_num:
                    max_num = num
            except ValueError:
                pass
    new_id = f"{prefix}{max_num + 1}"
    board["pieces"].append({
        "id": new_id,
        "type": "disc",
        "name": "黑棋" if side == "black" else "白棋",
        "side": side,
        "position": [int(to_pos[0]), int(to_pos[1])],
        "is_alive": True,
        "custom_properties": {},
    })
    flipped = engine.apply_flip_captures([int(to_pos[0]), int(to_pos[1])], side, board)
    return new_id, flipped


def next_turn_after(board, engine, current):
    """pass_when_no_move：对方无棋则跳过，改回当前方；双方无棋返回 None。"""
    opponent = other_side(current)
    if engine.get_valid_placements(opponent, board):
        return opponent
    if engine.get_valid_placements(current, board):
        return current
    return None


def generate():
    rule_engine_cls = load_engine_class()
    board_config = load_json("board")
    pieces_black = load_json("pieces_black")
    pieces_white = load_json("pieces_white")
    rules = load_json("rules")
    board = load_json("board_state")

    engine = rule_engine_cls(board_config, pieces_black, pieces_white, rules)
    total_cells = board_config["geometry"]["width"] * board_config["geometry"]["height"]

    steps = []
    for i in range(256):
        current = board.get("current_turn", "black")
        reason = outcome_reason(board, engine, total_cells)

        step = {
            "label": f"step{i}",
            "current_turn": current,
            "placements": {
                "black": sort_placements(engine.get_valid_placements("black", board)),
                "white": sort_placements(engine.get_valid_placements("white", board)),
            },
            "game_over": reason is not None,
            "winner": engine.get_winner(board) if reason is not None else None,
            "reason": reason,
            "move": None,
            "next_turn": None,
        }

        if reason is not None:
            steps.append(step)
            break

        placements = engine.get_valid_placements(current, board)
        move = min(placements, key=lambda p: (p[0], p[1]))

        new_id, flipped = place_disc_and_flip(board, move, current, engine)
        step["move"] = {
            "side": current,
            "to": [int(move[0]), int(move[1])],
            "placed_id": new_id,
            "flipped": sorted(flipped),
            "board_after": board_snapshot(board),
        }

        nxt = next_turn_after(board, engine, current)
        board["current_turn"] = nxt if nxt else current
        step["next_turn"] = nxt
        steps.append(step)

    fixture = {
        "variant": "heibaiqi",
        "engine": "rule_engine.py",
        "steps": steps,
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(fixture, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"已生成 {OUT}（{len(steps)} 步）")


if __name__ == "__main__":
    generate()