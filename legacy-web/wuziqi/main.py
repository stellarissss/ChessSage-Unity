"""
无限制五子棋 - 主服务器（重构版，使用 shared/game_base 共享基类）。
五子棋子项目的 FastAPI 入口。

本文件仅保留：
    1. 配置常量（CONFIGS_DIR / STATIC_DIR / SAMSARA_API_URL）
    2. GameState — 继承 BaseGameState，仅实现 _rebuild_engines
    3. FastAPI 应用创建 / 中间件 / 静态目录挂载
    4. 棋类专有路由：/api/command, /api/move, /api/ai_move, /api/undo
    5. 棋类专有辅助函数：_trigger_karma_recover, _count_consecutive
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
from chess_ai import GomokuAI
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
    """五子棋全局游戏状态 — 继承 BaseGameState，仅重写 _rebuild_engines。"""

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
        """重建规则引擎和AI引擎（五子棋参数化）。"""
        board_config = self.configs.get("board", {})
        pieces_red = self.configs.get("pieces_red", {})
        pieces_black = self.configs.get("pieces_black", {})
        rules_config = self.configs.get("rules", {})
        self.rule_engine = RuleEngine(board_config, pieces_red, pieces_black, rules_config)
        self.chess_ai = GomokuAI(
            board_config, pieces_red, pieces_black, rules_config,
            rules_config.get("ai_difficulty", {}).get("current", "medium"),
            api_key=getattr(self.ai_orchestrator, 'api_key', ''),
            token_stats_callback=getattr(self.ai_orchestrator, '_record_token_usage', None),
        )
        self.mechanism_engine = MechanismEngine(rules_config)
        self.mechanism_engine.apply_personality_to_ai(self.chess_ai)


state = GameState()

# ═══════════════════════════════════════════════════════════════
# FastAPI 应用
# ═══════════════════════════════════════════════════════════════

app = FastAPI(title="无限制五子棋", version="2.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# 挂载静态文件（五子棋前端）
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
    game_type="wuziqi",
    static_dir=STATIC_DIR,
    samsara_api_url=SAMSARA_API_URL,
    difficulty_levels=["easy", "medium", "hard"],
)


# ═══════════════════════════════════════════════════════════════
# 五子棋专有数据模型
# ═══════════════════════════════════════════════════════════════

class MoveRequest(BaseModel):
    piece_id: Optional[str] = None
    to: list  # [x, y]


# ═══════════════════════════════════════════════════════════════
# 五子棋专有 API 路由
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
    """玩家落子"""
    board = state.configs["board_state"]

    if board.get("game_status", {}).get("state") == "ended":
        return {"success": False, "message": "游戏已结束"}

    current_turn = board.get("current_turn", "black")
    player_side = board.get("player_side", "black")

    # AI 接管当前方 → 玩家不能走（ai_control 优先级最高）
    if state.mechanism_engine and state.mechanism_engine.is_ai_controlled(board, current_turn):
        return {"success": False, "message": "本回合由AI接管中，请等待AI走棋", "ai_controlled": True}
    # 玩家可走的条件：是玩家方 OR 被玩家接管（player_control）
    is_player_turn = (current_turn == player_side) or \
                     (state.mechanism_engine and state.mechanism_engine.is_player_controlled(board, current_turn))
    if not is_player_turn:
        return {"success": False, "message": "本回合由AI控制，请等待AI走棋", "ai_controlled": True}
    # 记录是否本次落子需消耗 player_control（在落子成功后递减）
    consume_player_control = (current_turn != player_side) and \
                             state.mechanism_engine and \
                             state.mechanism_engine.is_player_controlled(board, current_turn)

    to_x, to_y = req.to[0], req.to[1]

    geometry = state.configs.get("board", {}).get("geometry", {})
    width = geometry.get("width", 15)
    height = geometry.get("height", 15)

    if to_x < 0 or to_x >= width or to_y < 0 or to_y >= height:
        return {"success": False, "message": "位置超出棋盘范围"}

    for p in board.get("pieces", []):
        if p.get("is_alive", True) and p["position"][0] == to_x and p["position"][1] == to_y:
            return {"success": False, "message": "该位置已有棋子"}

    stone_type = "stone"
    piece_label = "●" if current_turn == "black" else "○"
    piece_id = f"{current_turn}_stone_{to_x}_{to_y}_{len(board['pieces'])}"

    new_piece = {
        "id": piece_id,
        "type": stone_type,
        "name": piece_label,
        "side": current_turn,
        "position": [to_x, to_y],
        "is_alive": True,
        "custom_properties": {},
    }
    board["pieces"].append(new_piece)

    board.setdefault("move_history", []).append({
        "piece_id": piece_id,
        "from": None,
        "to": [to_x, to_y],
        "captured": None,
    })

    winner = state.rule_engine.check_five_in_a_row(board)
    if winner:
        board["game_status"] = {
            "state": "ended",
            "winner": winner,
            "win_condition": "five_in_a_row",
            "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
        }
    else:
        # 玩家接管AI方落子成功后，消耗一次 player_control
        if consume_player_control:
            pc_list = board.get("mechanisms", {}).get("player_control", [])
            for i, item in enumerate(pc_list):
                if item.get("side") == current_turn and item.get("remaining", 0) > 0:
                    item["remaining"] -= 1
                    if item["remaining"] <= 0:
                        pc_list.pop(i)
                    break

        board["current_turn"] = "white" if current_turn == "black" else "black"

        geometry = state.configs.get("board", {}).get("geometry", {})
        width = geometry.get("width", 15)
        height = geometry.get("height", 15)
        total_cells = width * height
        alive_pieces = sum(1 for p in board["pieces"] if p.get("is_alive", True))
        if alive_pieces >= total_cells:
            board["game_status"] = {
                "state": "ended",
                "winner": "draw",
                "win_condition": "stalemate",
                "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
            }

    # 检测连子减少业力（消业）
    await _trigger_karma_recover(board, req.to, current_turn)

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
    """AI走棋"""
    board = state.configs["board_state"]

    current_turn = board.get("current_turn", "black")

    if board.get("game_status", {}).get("state") == "ended":
        return {"success": False, "message": "游戏已结束"}

    ai_should_move = False
    player_side = board.get("player_side", "black")
    # AI 接管当前方 → AI 走（ai_control 优先级最高，对称于 /api/move 的拦截顺序）
    if state.mechanism_engine and state.mechanism_engine.is_ai_controlled(board, current_turn):
        ai_should_move = True
    # 玩家接管当前方 → AI 不走（player_control 次优先）
    elif state.mechanism_engine and state.mechanism_engine.is_player_controlled(board, current_turn):
        ai_should_move = False
    # 当前方不是玩家方 → AI 走（默认 AI 方）
    elif current_turn != player_side:
        ai_should_move = True

    if not ai_should_move:
        return {"success": False, "message": "不是AI回合"}

    move = None
    is_random_move = False

    if state.mechanism_engine and state.mechanism_engine.is_random_move_required(board, current_turn):
        is_random_move = True
        import random
        moves = []
        geometry = state.configs.get("board", {}).get("geometry", {})
        width = geometry.get("width", 15)
        height = geometry.get("height", 15)
        existing_positions = set()
        for p in board.get("pieces", []):
            if p.get("is_alive", True):
                existing_positions.add((p["position"][0], p["position"][1]))
        for x in range(width):
            for y in range(height):
                if (x, y) not in existing_positions:
                    moves.append({"to": [x, y]})
        if moves:
            move = random.choice(moves)
    else:
        await state.chess_ai.precompute_custom_piece_values()
        ai_move_result = state.chess_ai.get_best_move(board, ai_side=current_turn)
        if ai_move_result:
            move = {"to": ai_move_result["to"]}

    if not move:
        board["game_status"] = {
            "state": "ended",
            "winner": "white" if current_turn == "black" else "black",
            "win_condition": "stalemate",
            "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
        }
        state.save_config("board_state")
        winner_side = "黑方" if current_turn == "black" else "白方"
        return {"success": True, "board_state": board, "message": f"AI无棋可走，{winner_side}获胜"}

    to_x, to_y = move["to"][0], move["to"][1]

    stone_type = "stone"
    piece_label = "●" if current_turn == "black" else "○"
    piece_id = f"{current_turn}_stone_{to_x}_{to_y}_{len(board['pieces'])}"

    new_piece = {
        "id": piece_id,
        "type": stone_type,
        "name": piece_label,
        "side": current_turn,
        "position": [to_x, to_y],
        "is_alive": True,
        "custom_properties": {},
    }
    board["pieces"].append(new_piece)

    board.setdefault("move_history", []).append({
        "piece_id": piece_id,
        "from": None,
        "to": [to_x, to_y],
        "captured": None,
    })

    winner = state.rule_engine.check_five_in_a_row(board)
    if winner:
        board["game_status"] = {
            "state": "ended",
            "winner": winner,
            "win_condition": "five_in_a_row",
            "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
        }
    else:
        board["current_turn"] = "white" if current_turn == "black" else "black"

        geometry = state.configs.get("board", {}).get("geometry", {})
        width = geometry.get("width", 15)
        height = geometry.get("height", 15)
        total_cells = width * height
        alive_pieces = sum(1 for p in board["pieces"] if p.get("is_alive", True))
        if alive_pieces >= total_cells:
            board["game_status"] = {
                "state": "ended",
                "winner": "draw",
                "win_condition": "stalemate",
                "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
            }

    # 检测连子减少业力（消业）
    await _trigger_karma_recover(board, move["to"], current_turn)

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


@app.post("/api/undo")
async def undo_move():
    """悔棋"""
    board = state.configs["board_state"]
    history = board.get("move_history", [])

    if not history:
        return {"success": False, "message": "没有可悔的棋"}

    steps_to_undo = min(2, len(history))

    for _ in range(steps_to_undo):
        if not history:
            break
        last = history.pop()
        for p in board["pieces"]:
            if p["id"] == last["piece_id"]:
                board["pieces"].remove(p)
                break

    board["current_turn"] = "black" if (len(board["move_history"]) % 2) == 0 else "white"
    board["game_status"] = {
        "state": "playing",
        "winner": None,
        "win_condition": None,
        "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
    }
    state.save_config("board_state")
    return {"success": True, "board_state": board, "message": f"已悔{steps_to_undo}步"}


@app.post("/api/valid_moves")
async def get_valid_moves(req: MoveRequest):
    """获取棋子的合法移动（五子棋前端点击选中棋子会调用此接口展示合法落点）

    注：五子棋原始前端逻辑复用象棋的"选中棋子→合法移动→executeMove"交互，
    棋子被选中后调用此 API 拿到合法位置；若 piece_id 缺省则退化为列出全棋盘空位。
    """
    board = state.configs["board_state"]
    if req.piece_id:
        for p in board["pieces"]:
            if p["id"] == req.piece_id and p.get("is_alive", True):
                moves = state.rule_engine.get_valid_moves(p, board)
                return {"success": True, "moves": moves}
        # piece_id 存在但无匹配棋子 → 返回空 moves
        return {"success": False, "moves": []}

    # piece_id 未给 → 返回全部空位置（前端点击坐标落子兼容模式）
    geometry = state.configs.get("board", {}).get("geometry", {})
    width = geometry.get("width", 15)
    height = geometry.get("height", 15)
    occupied = set()
    for p in board.get("pieces", []):
        if p.get("is_alive", True):
            pos = p.get("position", [])
            if len(pos) == 2:
                occupied.add((pos[0], pos[1]))
    moves = [[x, y] for x in range(width) for y in range(height) if (x, y) not in occupied]
    return {"success": True, "moves": moves}


# ═══════════════════════════════════════════════════════════════
# 五子棋专有辅助函数
# ═══════════════════════════════════════════════════════════════

async def _trigger_karma_recover(board: dict, position: list, side: str):
    """落子后检测连子并减少业力（消业，使用本地 KarmaAssessor）
    五子棋事件：three(10)/four(20)/block_three(8)/block_four(18)/double_three(15)/win(35)
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

        # 检测当前方连子数（三/四/五）
        counts = _count_consecutive(board, position, side)
        max_count = max(counts) if counts else 0

        # 检测对方被堵的连子
        opp_side = "white" if side == "black" else "black"
        opp_counts = _count_consecutive(board, position, opp_side)
        max_opp_blocked = max(opp_counts) if opp_counts else 0

        event_type = None
        amount = 0
        if max_count >= 5:
            event_type = "win"
            amount = 35
        elif max_count == 4:
            event_type = "four"
            amount = 20
        elif max_count == 3:
            # 检查是否双三
            three_count = sum(1 for c in counts if c == 3)
            if three_count >= 2:
                event_type = "double_three"
                amount = 15
            else:
                event_type = "three"
                amount = 10
        elif max_opp_blocked == 4:
            event_type = "block_four"
            amount = 18
        elif max_opp_blocked == 3:
            event_type = "block_three"
            amount = 8

        if event_type and amount > 0:
            karma_assessor.decrease_karma(amount, skill_modifiers)
            # 同步到 samsara
            try:
                async with samsara_client(3.0) as client:
                    await client.post(
                        f"{SAMSARA_API_URL}/api/karma/event",
                        json={
                            "game_type": "wuziqi",
                            "event_type": event_type,
                            "details": {"position": position, "side": side}
                        }
                    )
            except Exception as _e:
                samsara_warn("samsara 调用", _e)
    except Exception as _e:
        samsara_warn("samsara 调用", _e)


def _count_consecutive(board: dict, position: list, side: str) -> list:
    """统计在position位置各方向上的连子数（包括position本身）"""
    directions = [(1, 0), (0, 1), (1, 1), (1, -1)]
    counts = []
    pieces = board.get("pieces", [])
    # 构建位置->side的映射
    pos_map = {}
    for p in pieces:
        if p.get("is_alive", True):
            pos_map[tuple(p.get("position", []))] = p.get("side")

    x, y = position
    for dx, dy in directions:
        count = 1
        # 正方向
        nx, ny = x + dx, y + dy
        while pos_map.get((nx, ny)) == side:
            count += 1
            nx += dx
            ny += dy
        # 反方向
        nx, ny = x - dx, y - dy
        while pos_map.get((nx, ny)) == side:
            count += 1
            nx -= dx
            ny -= dy
        counts.append(count)
    return counts


# ═══════════════════════════════════════════════════════════════
# 启动
# ═══════════════════════════════════════════════════════════════

if __name__ == "__main__":
    import uvicorn

    port = int(os.environ.get("GAME_PORT", 8001))
    print("=" * 50)
    print("  无限制五子棋 - 启动中...")
    print(f"  访问地址: http://localhost:{port}")
    print("=" * 50)
    uvicorn.run(app, host="0.0.0.0", port=port)
