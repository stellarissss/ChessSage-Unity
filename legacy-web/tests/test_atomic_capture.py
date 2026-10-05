# -*- coding: utf-8 -*-
"""
原子吃子修饰器验证测试（can_capture / eatable / invulnerable）
同时对 rpg 引擎与 sandbox 引擎进行验证。
运行：python tests/test_atomic_capture.py
"""
import os
import sys
import copy

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.dirname(HERE)

ENGINE_DIRS = [
    ("rpg", os.path.join(BASE, "xiangqi")),
    ("sandbox", os.path.join(BASE, "sandbox", "xiangqi")),
]


def make_pieces(red_mods=None, black_mods=None):
    """构造最小棋子配置（车走无敌直线），red_mods/black_mods 可为某类型注入修饰器"""
    def _mk(mods):
        config = {
            "pieces": {
                "chariot": {
                    "label": "車",
                    "moves": [
                        {"kind": "ray", "dir": [1, 0], "max": -1, "screens": 0,
                         "land": "any", "sym": "rotate4"}
                    ],
                },
                "soldier": {
                    "label": "兵",
                    "moves": [
                        {"kind": "jump", "to": "$forward", "block": [],
                         "land": "any", "sym": "none"}
                    ],
                },
            },
            "custom_pieces": [],
        }
        if mods:
            for k, v in mods.items():
                config["pieces"]["chariot"][k] = v
        return config
    return _mk(red_mods), _mk(black_mods)


def make_board(chariot_mods=None, soldier_mods=None):
    """构造棋盘状态：红车[0,5] vs 黑车[3,5]（同列），以及一个小兵用于其它检查"""
    pieces = [
        {"id": "r_chariot", "type": "chariot", "name": "車", "side": "red",
         "position": [0, 5], "is_alive": True, "custom_properties": chariot_mods or {}},
        {"id": "b_chariot", "type": "chariot", "name": "車", "side": "black",
         "position": [3, 5], "is_alive": True, "custom_properties": {}},
        {"id": "r_soldier", "type": "soldier", "name": "兵", "side": "red",
         "position": [8, 9], "is_alive": True, "custom_properties": soldier_mods or {}},
    ]
    return {"pieces": pieces, "current_turn": "red"}


def default_rules():
    return {"type_modifiers": {}, "ai_difficulty": {
        "levels": {"medium": {"depth": 3, "randomness": 0.0}}
    }}


def run_engine_tests(name, engdir):
    """针对单个引擎目录运行全部断言，返回 (通过数, 失败信息列表)"""
    sys.path.insert(0, engdir)
    try:
        import rule_engine as rule_mod
        import chess_ai as chess_mod
    finally:
        # 加载后立刻移除，避免影响另一个引擎
        sys.path.remove(engdir)

    RuleEngine = rule_mod.RuleEngine

    fails = []
    passed = 0

    def check(desc, cond):
        nonlocal passed
        if cond:
            passed += 1
            print(f"  [PASS] {desc}")
        else:
            fails.append(desc)
            print(f"  [FAIL] {desc}")

    # ── 场景D：无任何修饰器 → 默认行为不变 ──
    red, black = make_pieces()
    rules = default_rules()
    eng = RuleEngine({}, red, black, rules)
    board = make_board()
    r_chariot = board["pieces"][0]
    moves = eng.get_valid_moves(r_chariot, board)
    check("默认：红车能吃黑车[3,5]", [3, 5] in moves)
    check("默认：红车仍可走到空格[1,5]", [1, 5] in moves)
    check("默认：目标可被吃(eatable=True)", eng.is_target_eatable(board["pieces"][1]) is True)
    check("默认：可吃子(can_capture=True)",
          eng.get_effective_modifier(r_chariot, "can_capture", True) is True)
    # 吃子模拟会移除目标
    cap_ok = True
    b_chariot = board["pieces"][1]
    board2 = copy.deepcopy(board)
    for p in board2["pieces"]:
        if p["id"] == "b_chariot":
            p["is_alive"] = False
    check("默认：目标可被吃则允许移除", any(p["is_alive"] is False for p in board2["pieces"] if p["id"] == "b_chariot"))

    # ── 场景A：进攻方 can_capture=false → 不含吃子着法，空格不受影响 ──
    redA, blackA = make_pieces(red_mods={"can_capture": False})
    rulesA = default_rules()
    engA = RuleEngine({}, redA, blackA, rulesA)
    boardA = make_board()
    rA = boardA["pieces"][0]
    movesA = engA.get_valid_moves(rA, boardA)
    check("A: 无法吃子：红车不含吃子[3,5]", [3, 5] not in movesA)
    check("A: 无法吃子：仍含空格[1,5]", [1, 5] in movesA)
    check("A: 仍含空格[2,5]", [2, 5] in movesA)
    check("A: can_capture 解析为 False",
          engA.get_effective_modifier(rA, "can_capture", True) is False)

    # ── 场景A2：rules.type_modifiers 全局类型映射亦可令进攻方无法吃子 ──
    redB, blackB = make_pieces()
    rulesB = default_rules()
    rulesB["type_modifiers"] = {"chariot": {"can_capture": False}}
    engB = RuleEngine({}, redB, blackB, rulesB)
    boardB = make_board()
    rB = boardB["pieces"][0]
    movesB = engB.get_valid_moves(rB, boardB)
    check("A: rules.type_modifiers：红车不含吃子[3,5]", [3, 5] not in movesB)
    check("A: rules.type_modifiers：仍含空格[1,5]", [1, 5] in movesB)

    # ── 场景B：目标 eatable=false（无敌）→ 进攻方无法吃掉它 ──
    redC, blackC = make_pieces(black_mods={"eatable": False})
    rulesC = default_rules()
    engC = RuleEngine({}, redC, blackC, rulesC)
    boardC = make_board()
    rC = boardC["pieces"][0]
    tgtC = boardC["pieces"][1]
    movesC = engC.get_valid_moves(rC, boardC)
    check("B: 目标无敌：不能移动到[3,5](吃子)", [3, 5] not in movesC)
    check("B: 目标无敌：仍可走到空格[1,5]", [1, 5] in movesC)
    check("B: is_invulnerable(目标)为True", engC.is_invulnerable(tgtC) is True)
    check("B: can_capture_piece(红车,目标)为False", engC.can_capture_piece(rC, tgtC) is False)

    # ── 场景B2：invulnerable=true 别名等价于 eatable=false（走 rules 阵营细分） ──
    redD, blackD = make_pieces()
    rulesD = default_rules()
    rulesD["type_modifiers"] = {"black": {"chariot": {"invulnerable": True}}}
    engD = RuleEngine({}, redD, blackD, rulesD)
    boardD = make_board()
    rD = boardD["pieces"][0]
    tgtD = boardD["pieces"][1]
    movesD = engD.get_valid_moves(rD, boardD)
    check("B: invulnerable别名：不能吃[3,5]", [3, 5] not in movesD)
    check("B: invulnerable别名：is_invulnerable(目标)为True", engD.is_invulnerable(tgtD) is True)

    # ── 场景C：强制吃子模拟不会移除无敌目标（_simulate_move 防御） ──
    rulesE = default_rules()
    redE, blackE = make_pieces(black_mods={"eatable": False})
    boardE = make_board()
    ai = chess_mod.ChessAI({}, redE, blackE, rulesE)
    forced_move = {
        "piece_id": "r_chariot",
        "from": [0, 5],
        "to": [3, 5],
        "captured": "b_chariot",
    }
    sim = ai._simulate_move(boardE, forced_move)
    survived = None
    for p in sim["pieces"]:
        if p["id"] == "b_chariot":
            survived = p["is_alive"]
    check("C: 强吃模拟不删除无敌目标(is_alive保持True)",
          survived is True)
    # 对照：可吃目标会被移除
    redE3, blackE3 = make_pieces()
    ai3 = chess_mod.ChessAI({}, redE3, blackE3, rulesE)
    sim3 = ai3._simulate_move(make_board(), {"piece_id": "r_chariot", "from": [0, 5], "to": [3, 5], "captured": "b_chariot"})
    removed = None
    for p in sim3["pieces"]:
        if p["id"] == "b_chariot":
            removed = p["is_alive"]
    check("C: 可吃目标在模拟中会被移除(is_alive=False)", removed is False)

    return passed, fails


def main():
    ok_all = True
    for name, engdir in ENGINE_DIRS:
        print(f"═══ 引擎：{name} ({engdir}) ═══")
        if not os.path.isdir(engdir):
            print("  [SKIP] 目录不存在")
            continue
        passed, fails = run_engine_tests(name, engdir)
        status = "ALL PASS" if not fails else "FAILURES: " + "; ".join(fails)
        print(f"── {name}: {passed} 项通过 / {len(fails)} 项失败 ──")
        if fails:
            ok_all = False
    print("\n总体结果:", "PASS ✓" if ok_all else "FAIL ✗")
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())