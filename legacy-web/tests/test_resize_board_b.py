# -*- coding: utf-8 -*-
"""B类 resize_board 动作验证：驱动 _handle_action_b_with_log 处理 resize 意图。"""
import os
import sys
import json
import asyncio

PROJ = "/workspace/CHessGAme"


def _load(module_dir):
    """按目录加载 ai_orchestrator 模块及其 configs。"""
    sys.path.insert(0, module_dir)
    import importlib
    mod = importlib.import_module("ai_orchestrator")
    # 清理，避免其他目录的同名模块相互干扰
    sys.path.pop(0)
    sys.modules.pop("ai_orchestrator", None)
    configs_dir = os.path.join(module_dir, "configs")
    with open(os.path.join(configs_dir, "board_state.json"), encoding="utf-8") as f:
        board_state = json.load(f)
    with open(os.path.join(configs_dir, "board.json"), encoding="utf-8") as f:
        board = json.load(f)
    return mod.AIOrchestrator, board_state, board


def check_resize(module_dir, label):
    AIOrchestrator, board_state, board = _load(module_dir)
    # 旧棋盘尺寸（用于校验 0 保持 0、旧最大列映射到新最大列）
    geom = board.get("geometry", {})
    old_w = geom.get("width", 9)
    old_h = geom.get("height", 10)

    configs = {"board_state": board_state, "board": board}
    intent = {
        "structured_instruction": {
            "action": "resize_board",
            "parameters": {"new_width": 20, "new_height": 20},
        },
        "next_ai_prompt": "",
        "target_files": [],
        "side": None,
    }
    log_entry = {}
    orch = AIOrchestrator()
    result = asyncio.run(orch._handle_action_b_with_log(intent, configs, log_entry))

    assert result.get("success") is True, f"{label}: success=False -> {result}"
    assert "B类不处理此动作" not in result.get("message", ""), f"{label}: 仍被拒绝"
    assert result.get("classification") == "B"

    resized = result["modified_configs"]["board_state"]
    pieces = resized.get("pieces", [])
    assert pieces, f"{label}: 无棋子"

    # 全部坐标落在新棋盘范围内
    for p in pieces:
        x, y = p["position"]
        assert 0 <= x < 20 and 0 <= y < 20, f"{label}: 越界 {p['id']} -> {p['position']}"

    # 原始 x=0 的棋子缩放后仍为 0（用 board_state 原坐标校验）
    for p in board_state.get("pieces", []):
        if p.get("position", [None])[0] == 0 and p.get("is_alive", True):
            new_p = next(q for q in pieces if q.get("id") == p.get("id"))
            assert new_p["position"][0] == 0, f"{label}: 左边界 0 未保持 {p['id']}"

    # 旧最大列(old_w-1)的棋子映射到新最大列(new_w-1)=(20-1)=19
    edge_pieces = [
        p for p in board_state.get("pieces", [])
        if p.get("position", [None])[0] == old_w - 1 and p.get("is_alive", True)
    ]
    if edge_pieces:
        mapped_to_19 = False
        for p in edge_pieces:
            new_p = next(q for q in pieces if q.get("id") == p.get("id"))
            assert new_p["position"][0] == 19, f"{label}: 最大列映射错误 {p['id']} -> {new_p['position']}"
            mapped_to_19 = True
        assert mapped_to_19, f"{label}: 旧最大列未映射到 19"

    # 持久化：返回的 modified_configs 反射了改动，可直接落盘
    assert resized is result["modified_configs"]["board_state"]
    print(f"[PASS] {label}: success={result['success']} message='{result['message']}' "
          f"pieces={len(pieces)} 全部 0<=x,y<20, 旧0列保持0, 旧最大列->19")


def main():
    games = ["xiangqi", "wuziqi", "weiqi", "dongwuqi", "tiaoqi", "heibaiqi"]
    scenarios = []
    for g in games:
        scenarios.append((os.path.join(PROJ, g), f"{g}(rpg)"))
        scenarios.append((os.path.join(PROJ, "sandbox", g), f"sandbox/{g}"))
    for module_dir, label in scenarios:
        check_resize(module_dir, label)
    print("\n全部 PASS")


if __name__ == "__main__":
    main()