"""
无限制跳棋 - 主服务器（重构版，使用 shared/game_base 共享基类）。
跳棋子项目的 FastAPI 入口。剧情编辑器与共享资产已分离至各自子项目。

本文件仅保留：
    1. 配置常量（CONFIGS_DIR / STATIC_DIR / SAMSARA_API_URL）
    2. GameState — 继承 BaseGameState，仅实现 _rebuild_engines
    3. FastAPI 应用创建 / 中间件 / 静态目录挂载
    4. 棋类专有路由：/api/command, /api/move, /api/ai_move, /api/undo, /api/valid_moves
    5. 棋类专有辅助函数：_trigger_karma_recover
    6. if __name__ == "__main__" 启动块

公共路由（25+ 条）和共享 GameState 方法均由 shared.game_base 提供。
"""
import os
import sys
import json  # noqa: F401（保留以备子模块内部间接使用，原文件有）
import copy  # noqa: F401
from pathlib import Path
from typing import Dict, Any, Optional  # noqa: F401

# 将 shared/ 加入 sys.path，以便复用 schema_validator / json_patch_utils / game_base
BASE_DIR = Path(__file__).resolve().parent
WORKSPACE_ROOT = BASE_DIR.parent
SHARED_DIR = WORKSPACE_ROOT / "shared"
if str(SHARED_DIR) not in sys.path:
    sys.path.insert(0, str(SHARED_DIR))

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, Request, Query  # WebSocket 等保留占位
from fastapi.staticfiles import StaticFiles
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import httpx

# ── 共享基类导入 ────────────────────────────────────────────────
from shared.game_base import (
    BaseGameState,
    register_common_routes,
    SetApiKey,      # noqa: F401（re-export，保持原 import 语义对外可见）
    DifficultyRequest,
    PlayerCommand,
    close_samsara_client,
    samsara_client,
    samsara_warn,
)

from ai_orchestrator import AIOrchestrator
from rule_engine import RuleEngine
from chess_ai import ChessAI
from mechanism_engine import MechanismEngine
from ai_config import get_api_key

# ═══════════════════════════════════════════════════════════════
# 配置
# ═══════════════════════════════════════════════════════════════

CONFIGS_DIR = BASE_DIR / "configs"
STATIC_DIR = BASE_DIR / "static"

SAMSARA_API_URL = os.environ.get("SAMSARA_API_URL", "http://localhost:8080/samsara")


# ═══════════════════════════════════════════════════════════════
# 状态管理（继承 BaseGameState，仅实现棋类参数化差异）
# ═══════════════════════════════════════════════════════════════

class GameState(BaseGameState):
    """跳棋全局游戏状态 — 继承 BaseGameState，仅重写 _rebuild_engines。"""

    CONFIG_FILES = ["board_state", "board", "pieces_red", "pieces_black", "rules", "ui_config"]
    DEFAULT_DIFFICULTY = "medium"
    PIECE_CONFIG_KEYS = ("pieces_red", "pieces_black")

    def __init__(self):
        super().__init__(configs_dir=CONFIGS_DIR)
        api_key = get_api_key()
        self.ai_orchestrator = AIOrchestrator(api_key=api_key)
        # super().__init__ 不调用 load_configs（遵守子类契约），这里在设置完 ai_orchestrator 后调用
        self.load_configs()

    def _rebuild_engines(self):
        """重建规则引擎和AI引擎（跳棋参数化）。"""
        board_config = self.configs.get("board", {})
        pieces_red = self.configs.get("pieces_red", {})
        pieces_black = self.configs.get("pieces_black", {})
        rules_config = self.configs.get("rules", {})
        self.rule_engine = RuleEngine(board_config, pieces_red, pieces_black, rules_config)
        self.chess_ai = ChessAI(
            board_config, pieces_red, pieces_black, rules_config,
            rules_config.get("ai_difficulty", {}).get("current", "medium"),
            api_key=getattr(self.ai_orchestrator, 'api_key', ''),
            token_stats_callback=getattr(self.ai_orchestrator, '_record_token_usage', None),
        )
        self.mechanism_engine = MechanismEngine(rules_config)
        # 应用AI性格
        self.mechanism_engine.apply_personality_to_ai(self.chess_ai)


state = GameState()

# ═══════════════════════════════════════════════════════════════
# FastAPI 应用
# ═══════════════════════════════════════════════════════════════

app = FastAPI(title="无限制跳棋", version="2.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# 挂载静态文件（跳棋前端）
if STATIC_DIR.exists():
    app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

# 挂载共享 assets 目录（角色立绘等，便于前端引用）
SHARED_ASSETS_DIR = SHARED_DIR / "assets"
if SHARED_ASSETS_DIR.exists():
    app.mount("/assets", StaticFiles(directory=str(SHARED_ASSETS_DIR)), name="assets")


# ═══════════════════════════════════════════════════════════════
# 注册共享公共路由（25+ 条：根路由 / samsara 代理 / 配置读写 / 关卡系统等）
# ═══════════════════════════════════════════════════════════════

register_common_routes(
    app,
    state,
    game_type="tiaoqi",
    static_dir=STATIC_DIR,
    samsara_api_url=SAMSARA_API_URL,
    difficulty_levels=["easy", "medium", "hard"],
)


# ═══════════════════════════════════════════════════════════════
# 跳棋专有数据模型
# ═══════════════════════════════════════════════════════════════

class MoveRequest(BaseModel):
    piece_id: str
    to: list  # [x, y]


class ValidMovesRequest(BaseModel):
    piece_id: str


# ═══════════════════════════════════════════════════════════════
# 跳棋专有 API 路由
# ═══════════════════════════════════════════════════════════════

@app.post("/api/command")
async def process_command(req: PlayerCommand, dry_run: str = Query(None)):
    """处理玩家自然语言指令

    dry_run=1 时仅解析意图与评估 cost_energy，不写入配置（供 RPG cheat/assess 使用）。
    """
    try:
        # 从 samsara 获取技能修饰符和状态
        skill_modifiers = {}
        allowed_classifications = None
        samsara_data = {}
        try:
            async with samsara_client(5.0) as client:
                state_resp = await client.get(f"{SAMSARA_API_URL}/api/state")
                if state_resp.status_code == 200:
                    samsara_data = state_resp.json()
                    # 同步本地业力和识破状态（业障模型：初始50，阈值120）
                    state.ai_orchestrator.karma_assessor.set_local_karma_state(
                        karma=samsara_data.get("karma", 50),
                        karma_max=samsara_data.get("karma_max", 120),
                        single_max=samsara_data.get("karma_single_max", 120),
                    )
                    state.ai_orchestrator.karma_assessor.set_realm_detection(
                        detection=samsara_data.get("detection", 0.0),
                        realm=samsara_data.get("current_realm", "human"),
                    )
                    allowed_classifications = samsara_data.get("allowed_classifications")
                    # 获取技能修饰符（同一 /api/state 响应已含该字段，无需二次请求）
                    skill_modifiers = samsara_data.get("skill_modifiers", {})
        except Exception as _e:
            samsara_warn("samsara 调用", _e)

        result = await state.ai_orchestrator.process_command(
            req.command, {
                "configs": state.configs,
                "skill_modifiers": skill_modifiers,
                "allowed_classifications": allowed_classifications,
            }
        )

        # dry_run 模式下不应用配置变更，仅返回解析结果（含 cost_energy/classification）
        is_dry_run = dry_run == "1"
        classification = result.get("classification", "")
        estimated_karma_cost = result.get("estimated_karma_cost", 0)
        karma_assessor = state.ai_orchestrator.karma_assessor

        # 业障模型：作弊增加业力
        # - type=applied: 成功执行修改，增加业力
        # - type=fun (E类): 闲聊也增加1点业力
        # - type=rejected/error: 修改失败/被拒绝，不增加业力（等效于全额退还）
        # - karma_blocked: 超出单次上限被拦截，不增加业力
        should_increase_karma = (
            (not is_dry_run)
            and result.get("success")
            and result.get("type") in ("applied", "fun")
            and estimated_karma_cost > 0
            and not result.get("karma_blocked", False)
        )

        if should_increase_karma:
            # 成功执行修改或闲聊，增加业力
            if result.get("type") == "applied":
                modified = result.get("modified_configs", {})
                if modified:
                    state.apply_config_update(modified)

            # 增加业力（作弊产生业障）
            increase_result = karma_assessor.increase_karma(
                amount=estimated_karma_cost,
                skill_modifiers=skill_modifiers,
            )

            detection_result = None
            if increase_result.get("is_overdraft"):
                detection_result = karma_assessor.handle_overdraft(
                    overshoot_amount=increase_result.get("overdraft_amount", 0),
                    skill_modifiers=skill_modifiers,
                )

            # 同步到 samsara（业障模型：consume 接口现在表示增加业力）
            try:
                async with samsara_client(10.0) as client:
                    await client.post(
                        f"{SAMSARA_API_URL}/api/karma/consume",
                        json={"amount": estimated_karma_cost, "allow_overdraft": True}
                    )
                    # 记录作弊（仅非E类）
                    if classification != "E":
                        await client.post(f"{SAMSARA_API_URL}/api/cheat/record")
            except Exception as e:
                result["karma_sync_error"] = str(e)

            # 将增加结果添加到返回值
            result["karma_increased"] = increase_result.get("actual_increased", 0)
            result["is_overdraft"] = increase_result.get("is_overdraft", False)
            samsara_karma_max = samsara_data.get("karma_max", 120) + skill_modifiers.get("karma_max_bonus", 0)
            result["karma_state"] = {
                "current": karma_assessor.get_local_karma(),
                "max": samsara_karma_max,
            }
            if detection_result:
                result["detection"] = detection_result

            # 添加完整的业力和识破状态
            result["karma_detection_state"] = karma_assessor.get_state(skill_modifiers)

        return result
    except Exception as e:
        import traceback
        traceback.print_exc()
        return {"success": False, "type": "error", "message": f"服务器内部错误: {str(e)}"}


@app.post("/api/move")
async def make_move(req: MoveRequest):
    """玩家走棋（集成机制引擎）"""
    board = state.configs["board_state"]

    if board.get("game_status", {}).get("state") == "ended":
        return {"success": False, "message": "游戏已结束"}

    current_turn = board.get("current_turn", "red")

    # 回合前机制检查（skip_turn等 - 这里只做状态提示，实际跳过在前端/AI走棋时触发）
    pre_info = {}

    # 检查是否被AI接管（如果当前方被AI接管，则不允许玩家手动走棋）
    if state.mechanism_engine and state.mechanism_engine.is_ai_controlled(board, current_turn):
        return {"success": False, "message": "本回合由AI接管中，请等待AI走棋", "ai_controlled": True}

    # 检查当前方是否由玩家控制（通过player_control机制判断）
    if state.mechanism_engine and not state.mechanism_engine.is_player_controlled(board, current_turn):
        return {"success": False, "message": "当前方不由玩家控制", "not_player_controlled": True}

    # 找到棋子
    piece = None
    for p in board["pieces"]:
        if p["id"] == req.piece_id and p.get("is_alive", True):
            piece = p
            break

    if not piece:
        return {"success": False, "message": "棋子不存在"}

    if piece["side"] != current_turn:
        return {"success": False, "message": "不是该方回合"}

    # 验证移动合法性
    valid_moves = state.rule_engine.get_valid_moves(piece, board)
    if req.to not in valid_moves:
        return {"success": False, "message": "非法移动"}

    # 执行移动（跳棋不吃子）
    from_pos = list(piece["position"])
    piece["position"] = req.to
    # 移动后减少业力（消业）
    await _trigger_karma_recover(piece, from_pos, req.to, board)

    # 记录历史
    board.setdefault("move_history", []).append(
        {
            "piece_id": piece["id"],
            "from": from_pos,
            "to": req.to,
            "captured": None,
        }
    )

    # 检查游戏结束（全部入营胜利）
    winner = state.rule_engine.is_all_in_camp(board)
    if winner:
        board["game_status"] = {
            "state": "ended",
            "winner": winner,
            "win_condition": "all_in_camp",
            "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
        }
    else:
        # 走棋后机制处理
        switch_turn = True
        post_info = {}
        if state.mechanism_engine:
            board, post_info = state.mechanism_engine.apply_post_move_mechanisms(board, current_turn)
            switch_turn = post_info.get("switch_turn", True)

        if switch_turn:
            board["current_turn"] = "black" if current_turn == "red" else "red"

            # 检查下一回合是否被跳过，如果被跳过，继续切换直到找到不被跳过的一方
            skip_count = 0
            while state.mechanism_engine and state.mechanism_engine.should_skip_turn(board, board["current_turn"]):
                board, skip_info = state.mechanism_engine.apply_pre_turn_mechanisms(board, board["current_turn"])
                skip_count += 1
                if skip_count > 4:
                    break
                board["current_turn"] = "black" if board["current_turn"] == "red" else "red"

    # 获取当前激活的机制摘要
    mechanisms_summary = []
    if state.mechanism_engine:
        mechanisms_summary = state.mechanism_engine.get_active_mechanisms_summary(board)

    state.save_config("board_state")
    return {
        "success": True,
        "board_state": board,
        "mechanisms": mechanisms_summary,
    }


@app.post("/api/ai_move")
async def ai_move():
    """AI走棋（集成机制引擎）"""
    board = state.configs["board_state"]

    current_turn = board.get("current_turn", "black")

    if board.get("game_status", {}).get("state") == "ended":
        return {"success": False, "message": "游戏已结束"}

    # 检查是否应该由AI走棋
    # 条件：当前方被AI接管，或者当前方不由玩家控制（默认行为：黑方不由玩家控制）
    ai_should_move = False
    if state.mechanism_engine:
        if state.mechanism_engine.is_ai_controlled(board, current_turn):
            ai_should_move = True
        elif not state.mechanism_engine.is_player_controlled(board, current_turn):
            ai_should_move = True

    if not ai_should_move:
        return {"success": False, "message": "不是AI回合"}

    move = None
    is_random_move = False

    # 检查是否需要随机走棋
    if state.mechanism_engine and state.mechanism_engine.is_random_move_required(board, current_turn):
        is_random_move = True
        # 随机走一步
        import random
        moves = []
        for p in board.get("pieces", []):
            if not p.get("is_alive", True) or p["side"] != current_turn:
                continue
            valid = state.rule_engine.get_valid_moves(p, board)
            for pos in valid:
                moves.append({
                    "piece_id": p["id"],
                    "from": list(p["position"]),
                    "to": pos,
                    "captured": None,
                })
        if moves:
            move = random.choice(moves)
    else:
        # 获取AI最佳移动
        await state.chess_ai.precompute_custom_piece_values()
        move = state.chess_ai.get_best_move(board)

    if not move:
        board["game_status"] = {
            "state": "ended",
            "winner": "red" if current_turn == "black" else "black",
            "win_condition": "stalemate",
            "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
        }
        state.save_config("board_state")
        winner_side = "红方" if current_turn == "black" else "黑方"
        return {"success": True, "board_state": board, "message": f"AI无棋可走，{winner_side}获胜"}

    # 执行AI移动
    piece = None
    for p in board["pieces"]:
        if p["id"] == move["piece_id"]:
            piece = p
            break

    if not piece:
        return {"success": False, "message": "AI移动异常"}

    # 执行AI移动（跳棋不吃子）
    from_pos = list(piece["position"])
    piece["position"] = move["to"]
    # 移动后减少业力（消业）
    await _trigger_karma_recover(piece, from_pos, move["to"], board)

    board.setdefault("move_history", []).append(move)

    # 检查游戏结束（全部入营胜利）
    winner = state.rule_engine.is_all_in_camp(board)
    if winner:
        board["game_status"] = {
            "state": "ended",
            "winner": winner,
            "win_condition": "all_in_camp",
            "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
        }
    else:
        # 走棋后机制处理
        switch_turn = True
        post_info = {}
        if state.mechanism_engine:
            board, post_info = state.mechanism_engine.apply_post_move_mechanisms(board, current_turn)
            switch_turn = post_info.get("switch_turn", True)

        if switch_turn:
            board["current_turn"] = "red" if current_turn == "black" else "black"

            # 检查下一回合是否被跳过，如果被跳过，继续切换
            skip_count = 0
            while state.mechanism_engine and state.mechanism_engine.should_skip_turn(board, board["current_turn"]):
                board, skip_info = state.mechanism_engine.apply_pre_turn_mechanisms(board, board["current_turn"])
                skip_count += 1
                if skip_count > 4:
                    break
                board["current_turn"] = "red" if board["current_turn"] == "black" else "black"

    # 获取当前激活的机制摘要
    mechanisms_summary = []
    if state.mechanism_engine:
        mechanisms_summary = state.mechanism_engine.get_active_mechanisms_summary(board)

    state.save_config("board_state")
    return {
        "success": True,
        "board_state": board,
        "ai_move": move,
        "is_random": is_random_move,
        "mechanisms": mechanisms_summary,
    }


@app.post("/api/valid_moves")
async def get_valid_moves(req: ValidMovesRequest):
    """获取棋子的合法移动"""
    board = state.configs["board_state"]
    for p in board["pieces"]:
        if p["id"] == req.piece_id and p.get("is_alive", True):
            moves = state.rule_engine.get_valid_moves(p, board)
            return {"success": True, "moves": moves}
    return {"success": False, "moves": []}


@app.post("/api/undo")
async def undo_move():
    """悔棋（回退一步）"""
    board = state.configs["board_state"]
    history = board.get("move_history", [])

    if not history:
        return {"success": False, "message": "没有可悔的棋"}

    # 悔两步（玩家一步+AI一步），如果只有一步就悔一步
    steps_to_undo = min(2, len(history))

    for _ in range(steps_to_undo):
        if not history:
            break
        last = history.pop()
        piece = None
        for p in board["pieces"]:
            if p["id"] == last["piece_id"]:
                piece = p
                break
        if piece:
            piece["position"] = last["from"]

    # 根据剩余历史记录数判断当前回合
    # 初始状态(空历史)：红方回合
    # 每走一步切换一次，所以 history长度为偶数时是红方回合，奇数时是黑方回合
    board["current_turn"] = "red" if len(history) % 2 == 0 else "black"
    board["move_history"] = history
    board["game_status"] = {
        "state": "playing",
        "winner": None,
        "win_condition": None,
        "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
    }
    state.save_config("board_state")
    return {"success": True, "board_state": board, "message": f"已悔{steps_to_undo}步"}


# ═══════════════════════════════════════════════════════════════
# 跳棋专有辅助函数
# ═══════════════════════════════════════════════════════════════

async def _trigger_karma_recover(piece: dict, from_pos: list, to_pos: list, board: dict):
    """移动后触发业力减少（消业，使用本地 KarmaAssessor）
    跳棋事件：jump_3(10)/jump_5(20)/home(15)/single_move(3)/all_home(35)
    """
    try:
        karma_assessor = state.ai_orchestrator.karma_assessor

        # 获取技能修饰符
        skill_modifiers = {}
        try:
            async with samsara_client(3.0) as client:
                skill_resp = await client.get(f"{SAMSARA_API_URL}/api/skills")
                if skill_resp.status_code == 200:
                    skill_modifiers = skill_resp.json().get("modifiers", {})
        except Exception as _e:
            samsara_warn("samsara 调用", _e)

        # 计算跳跃距离（曼哈顿距离）
        dx = abs(to_pos[0] - from_pos[0])
        dy = abs(to_pos[1] - from_pos[1])
        jump_distance = max(dx, dy)

        event_type = None
        amount = 0
        if jump_distance >= 5:
            event_type = "jump_5"
            amount = 20
        elif jump_distance >= 3:
            event_type = "jump_3"
            amount = 10
        else:
            event_type = "single_move"
            amount = 3

        karma_assessor.decrease_karma(amount, skill_modifiers)

        # 检查是否入营（需要根据棋盘配置判断终点区域）
        # 简化判断：如果棋子到达对方起始区域，视为入营
        try:
            board_config = state.configs.get("board", {})
            regions = board_config.get("regions", {})
            piece_side = piece.get("side", "")
            target_region = "red_home" if piece_side == "black" else "black_home"
            target_cells = regions.get(target_region, {}).get("cells", [])
            if list(to_pos) in target_cells:
                karma_assessor.decrease_karma(15, skill_modifiers)
                # 同步入营事件
                try:
                    async with samsara_client(3.0) as client:
                        await client.post(
                            f"{SAMSARA_API_URL}/api/karma/event",
                            json={
                                "game_type": "tiaoqi",
                                "event_type": "home",
                                "details": {"piece_id": piece.get("id"), "position": to_pos}
                            }
                        )
                except Exception as _e:
                    samsara_warn("samsara 调用", _e)
        except Exception as _e:
            samsara_warn("samsara 调用", _e)

        # 同步跳跃事件到 samsara
        try:
            async with samsara_client(3.0) as client:
                await client.post(
                    f"{SAMSARA_API_URL}/api/karma/event",
                    json={
                        "game_type": "tiaoqi",
                        "event_type": event_type,
                        "details": {"piece_id": piece.get("id"), "from": from_pos, "to": to_pos}
                    }
                )
        except Exception as _e:
            samsara_warn("samsara 调用", _e)
    except Exception as _e:
        samsara_warn("samsara 调用", _e)


# ═══════════════════════════════════════════════════════════════
# 启动
# ═══════════════════════════════════════════════════════════════

if __name__ == "__main__":
    import uvicorn

    port = int(os.environ.get("GAME_PORT", 8004))
    print("=" * 50)
    print("  无限制跳棋 - 启动中...")
    print(f"  访问地址: http://localhost:{port}")
    print("=" * 50)
    uvicorn.run(app, host="0.0.0.0", port=port)
