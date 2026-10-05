"""
无限制黑白棋 - 主服务器（重构版，使用 shared/game_base 共享基类）。
黑白棋子项目的 FastAPI 入口。剧情编辑器与共享资产已分离至各自子项目。

本文件仅保留：
    1. 配置常量（CONFIGS_DIR / STATIC_DIR / SAMSARA_API_URL）
    2. GameState — 继承 BaseGameState，仅实现 _rebuild_engines
    3. FastAPI 应用创建 / 中间件 / 静态目录挂载
    4. 棋类专有路由：/api/command, /api/move, /api/ai_move, /api/undo, /api/valid_moves
    5. 棋类专有辅助函数：_place_disc_and_flip、_switch_turn_after_move、_trigger_karma_recover 等
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
    DifficultyRequest,  # noqa: F401
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
    """黑白棋全局游戏状态 — 继承 BaseGameState，仅重写 _rebuild_engines。"""

    CONFIG_FILES = ["board_state", "board", "pieces_black", "pieces_white", "rules", "ui_config"]
    DEFAULT_DIFFICULTY = "normal"
    PIECE_CONFIG_KEYS = ("pieces_black", "pieces_white")

    def __init__(self):
        super().__init__(configs_dir=CONFIGS_DIR)
        api_key = get_api_key()
        self.ai_orchestrator = AIOrchestrator(api_key=api_key)
        # super().__init__ 不调用 load_configs（遵守子类契约），这里在设置完 ai_orchestrator 后调用
        self.load_configs()

    def _rebuild_engines(self):
        """重建规则引擎和AI引擎（黑白棋参数化：pieces_black + pieces_white）。"""
        board_config = self.configs.get("board", {})
        pieces_black = self.configs.get("pieces_black", {})
        pieces_white = self.configs.get("pieces_white", {})
        rules_config = self.configs.get("rules", {})
        self.rule_engine = RuleEngine(board_config, pieces_black, pieces_white, rules_config)
        self.chess_ai = ChessAI(
            board_config, pieces_black, pieces_white, rules_config,
            rules_config.get("ai_difficulty", {}).get("current", "normal"),
            api_key=getattr(self.ai_orchestrator, 'api_key', ''),
            token_stats_callback=getattr(self.ai_orchestrator, '_record_token_usage', None),
        )
        self.mechanism_engine = MechanismEngine(rules_config)
        # 应用AI性格
        self.mechanism_engine.apply_personality_to_ai(self.chess_ai)


state = GameState()


# ═══════════════════════════════════════════════════════════════
# 黑白棋落子辅助函数
# ═══════════════════════════════════════════════════════════════


def _place_disc_and_flip(board: dict, to_pos: list, side: str, rule_engine: RuleEngine):
    """落子 + 翻转，返回 (new_disc_id, flipped_ids)"""
    # 找到当前方最大编号
    max_num = 0
    for p in board.get("pieces", []):
        if p.get("side") == side and p.get("id", "").startswith(f"{side}_disc_"):
            try:
                num = int(p["id"].split("_")[-1])
                if num > max_num:
                    max_num = num
            except (ValueError, IndexError):
                pass
    new_disc_id = f"{side}_disc_{max_num + 1}"
    new_disc = {
        "id": new_disc_id,
        "type": "disc",
        "name": "黑棋" if side == "black" else "白棋",
        "side": side,
        "position": list(to_pos),
        "is_alive": True,
        "custom_properties": {},
    }
    board["pieces"].append(new_disc)
    flipped_ids = rule_engine.apply_flip_captures(list(to_pos), side, board)
    return new_disc_id, flipped_ids


def _switch_turn_after_move(board: dict, current_turn: str, rule_engine: RuleEngine,
                            mechanism_engine, rules_config: dict):
    """走棋后切换回合（含 pass_when_no_move 逻辑）"""
    opponent = "white" if current_turn == "black" else "black"
    board["current_turn"] = opponent

    # 检查对方是否有合法落子
    opponent_moves = rule_engine.get_valid_placements(opponent, board)

    if not opponent_moves:
        # 对方无合法落子
        pass_enabled = rules_config.get("special_rules", {}).get(
            "pass_when_no_move", {}).get("enabled", True)
        if pass_enabled:
            # 跳过对方回合，current_turn 改回当前方
            board["current_turn"] = current_turn
            # 检查当前方是否也无合法落子
            current_moves = rule_engine.get_valid_placements(current_turn, board)
            if not current_moves:
                # 双方均无合法落子 - 游戏结束
                winner = rule_engine.get_winner(board)
                board["game_status"] = {
                    "state": "ended",
                    "winner": winner,
                    "win_condition": "no_valid_moves_both",
                    "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
                }
        else:
            # pass 未启用，检查当前方是否还能走
            current_moves = rule_engine.get_valid_placements(current_turn, board)
            if not current_moves:
                # 双方均无合法落子 - 游戏结束
                winner = rule_engine.get_winner(board)
                board["game_status"] = {
                    "state": "ended",
                    "winner": winner,
                    "win_condition": "no_valid_moves_both",
                    "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
                }
            else:
                # 当前方仍可走，回合改回当前方
                board["current_turn"] = current_turn
    else:
        # 对方有合法落子 - 检查 skip_turn 机制
        skip_count = 0
        while mechanism_engine and mechanism_engine.should_skip_turn(board, board["current_turn"]):
            board, _skip_info = mechanism_engine.apply_pre_turn_mechanisms(
                board, board["current_turn"])
            skip_count += 1
            if skip_count > 4:
                break
            board["current_turn"] = "white" if board["current_turn"] == "black" else "black"


def _check_board_full(board: dict, rule_engine: RuleEngine):
    """检查棋盘是否已满，若满则设置游戏结束"""
    board_width = board.get("board", {}).get("width", 8)
    board_height = board.get("board", {}).get("height", 8)
    total_cells = board_width * board_height
    alive_count = sum(1 for p in board.get("pieces", []) if p.get("is_alive", True))
    if alive_count >= total_cells and board.get("game_status", {}).get("state") != "ended":
        winner = rule_engine.get_winner(board)
        board["game_status"] = {
            "state": "ended",
            "winner": winner,
            "win_condition": "board_full",
            "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
        }


# ═══════════════════════════════════════════════════════════════
# FastAPI 应用
# ═══════════════════════════════════════════════════════════════

app = FastAPI(title="无限制黑白棋", version="2.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# 挂载静态文件（黑白棋前端）
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
    game_type="heibaiqi",
    static_dir=STATIC_DIR,
    samsara_api_url=SAMSARA_API_URL,
    difficulty_levels=["easy", "normal", "hard"],
)


# ═══════════════════════════════════════════════════════════════
# 黑白棋专有数据模型
# ═══════════════════════════════════════════════════════════════


class MoveRequest(BaseModel):
    piece_id: Optional[str] = None
    to: list  # [x, y] - 黑白棋无 piece_id，仅需落子坐标


class ValidMovesRequest(BaseModel):
    side: str  # black | white


# ═══════════════════════════════════════════════════════════════
# 黑白棋专有 API 路由
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
    """玩家落子（集成机制引擎 + 翻转逻辑）"""
    board = state.configs["board_state"]

    # 1. 校验游戏未结束
    if board.get("game_status", {}).get("state") == "ended":
        return {"success": False, "message": "游戏已结束"}

    # 2. 获取当前回合方（默认 black）
    current_turn = board.get("current_turn", "black")

    # 3. 机制引擎检查
    if state.mechanism_engine and state.mechanism_engine.is_ai_controlled(board, current_turn):
        return {"success": False, "message": "本回合由AI接管中，请等待AI走棋", "ai_controlled": True}

    if state.mechanism_engine and not state.mechanism_engine.is_player_controlled(board, current_turn):
        return {"success": False, "message": "当前方不由玩家控制", "not_player_controlled": True}

    # 4. 获取合法落子点
    valid_placements = state.rule_engine.get_valid_placements(current_turn, board)

    # 5. 校验 req.to 是否在合法点列表
    to_pos = list(req.to)
    is_valid = any(list(vp) == to_pos for vp in valid_placements)
    if not is_valid:
        return {"success": False, "message": "非法落子点"}

    # 6. 落子：新增 disc 到 board["pieces"]
    # 7. 翻转：调用 apply_flip_captures 翻转夹吃的对方棋子
    new_disc_id, flipped_ids = _place_disc_and_flip(board, to_pos, current_turn, state.rule_engine)

    # 翻转后减少业力（消业）
    await _trigger_karma_recover(flipped_ids, to_pos, current_turn)

    # 8. 记录 move_history
    board.setdefault("move_history", []).append({
        "side": current_turn,
        "to": to_pos,
        "flipped": flipped_ids,
        "placed_disc_id": new_disc_id,
    })

    # 9. 走棋后机制处理
    switch_turn = True
    if state.mechanism_engine:
        board, post_info = state.mechanism_engine.apply_post_move_mechanisms(board, current_turn)
        switch_turn = post_info.get("switch_turn", True)

    # 10. 切换回合（含 pass_when_no_move 逻辑）
    if switch_turn:
        _switch_turn_after_move(
            board, current_turn, state.rule_engine,
            state.mechanism_engine, state.configs.get("rules", {}))

    # 11. 检查棋盘是否已满
    _check_board_full(board, state.rule_engine)

    # 12. 获取机制摘要
    mechanisms_summary = []
    if state.mechanism_engine:
        mechanisms_summary = state.mechanism_engine.get_active_mechanisms_summary(board)

    # 13. 保存 board_state
    state.save_config("board_state")

    # 14. 返回结果
    return {
        "success": True,
        "board_state": board,
        "flipped": flipped_ids,
        "mechanisms": mechanisms_summary,
    }


@app.post("/api/ai_move")
async def ai_move():
    """AI落子（集成机制引擎 + 翻转逻辑）"""
    board = state.configs["board_state"]

    current_turn = board.get("current_turn", "black")

    if board.get("game_status", {}).get("state") == "ended":
        return {"success": False, "message": "游戏已结束"}

    # 检查是否应该由AI走棋
    # 默认行为：黑方由玩家控制，白方由AI控制（通过 player_control 机制可改）
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
        import random
        placements = state.rule_engine.get_valid_placements(current_turn, board)
        if placements:
            chosen = random.choice(placements)
            move = {"to": list(chosen), "side": current_turn}
    else:
        # 获取AI最佳落子
        move = state.chess_ai.get_best_move(board)

    if not move:
        # AI无合法落子 - 检查对方是否也无合法落子
        opponent = "white" if current_turn == "black" else "black"
        opponent_moves = state.rule_engine.get_valid_placements(opponent, board)
        if not opponent_moves:
            # 双方均无合法落子 - 游戏结束
            winner = state.rule_engine.get_winner(board)
            board["game_status"] = {
                "state": "ended",
                "winner": winner,
                "win_condition": "no_valid_moves_both",
                "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
            }
            state.save_config("board_state")
            return {"success": True, "board_state": board, "message": "双方均无棋可走，游戏结束"}
        else:
            # 跳过AI回合
            board["current_turn"] = opponent
            state.save_config("board_state")
            winner_side = "黑方" if current_turn == "white" else "白方"
            return {"success": True, "board_state": board, "message": f"AI无棋可走，跳过回合，{winner_side}行动"}

    # 执行落子 + 翻转（同 /api/move 逻辑）
    to_pos = list(move["to"])
    new_disc_id, flipped_ids = _place_disc_and_flip(board, to_pos, current_turn, state.rule_engine)

    # 翻转后减少业力（消业）
    await _trigger_karma_recover(flipped_ids, to_pos, current_turn)

    # 记录历史
    board.setdefault("move_history", []).append({
        "side": current_turn,
        "to": to_pos,
        "flipped": flipped_ids,
        "placed_disc_id": new_disc_id,
    })

    # 走棋后机制处理
    switch_turn = True
    if state.mechanism_engine:
        board, post_info = state.mechanism_engine.apply_post_move_mechanisms(board, current_turn)
        switch_turn = post_info.get("switch_turn", True)

    # 切换回合（含 pass_when_no_move 逻辑）
    if switch_turn:
        _switch_turn_after_move(
            board, current_turn, state.rule_engine,
            state.mechanism_engine, state.configs.get("rules", {}))

    # 检查棋盘是否已满
    _check_board_full(board, state.rule_engine)

    # 获取机制摘要
    mechanisms_summary = []
    if state.mechanism_engine:
        mechanisms_summary = state.mechanism_engine.get_active_mechanisms_summary(board)

    state.save_config("board_state")
    return {
        "success": True,
        "board_state": board,
        "ai_move": {"to": to_pos, "flipped": flipped_ids, "side": current_turn},
        "is_random": is_random_move,
        "mechanisms": mechanisms_summary,
    }


@app.post("/api/valid_moves")
async def get_valid_moves(req: ValidMovesRequest):
    """获取指定方的合法落子点"""
    board = state.configs["board_state"]
    side = req.side or board.get("current_turn", "black")
    moves = state.rule_engine.get_valid_placements(side, board)
    return {"success": True, "moves": moves, "side": side}


@app.post("/api/undo")
async def undo_move():
    """悔棋（回退一步）"""
    board = state.configs["board_state"]
    history = board.get("move_history", [])

    if not history:
        return {"success": False, "message": "没有可悔的棋"}

    # 从 move_history 弹出最后一条
    last = history.pop()
    side = last.get("side")
    opponent = "white" if side == "black" else "black"

    # 删除该步新增的 disc（按 id 查找并从 pieces 列表移除）
    placed_id = last.get("placed_disc_id")
    if placed_id:
        board["pieces"] = [p for p in board.get("pieces", []) if p["id"] != placed_id]

    # 恢复被翻转的棋子的 side（用 flipped 列表反向恢复）
    flipped_ids = last.get("flipped", [])
    for p in board.get("pieces", []):
        if p["id"] in flipped_ids:
            p["side"] = opponent

    # 切换 current_turn 回该方
    board["current_turn"] = side
    board["move_history"] = history

    # 重置游戏状态为进行中
    board["game_status"] = {
        "state": "playing",
        "winner": None,
        "win_condition": None,
        "custom_rules_active": board.get("game_status", {}).get("custom_rules_active", []),
    }

    state.save_config("board_state")
    return {"success": True, "board_state": board, "message": "已悔一步"}


# ═══════════════════════════════════════════════════════════════
# 黑白棋专有辅助函数
# ═══════════════════════════════════════════════════════════════

async def _trigger_karma_recover(flipped_ids: list, to_pos: list, current_turn: str):
    """翻转棋子后触发业力减少（消业，使用本地 KarmaAssessor）
    黑白棋事件：flip_small(8)/flip_medium(15)/flip_large(25)/corner(20)/flipped(5)/win(35)
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

        flip_count = len(flipped_ids) if flipped_ids else 0
        if flip_count == 0:
            return

        # 根据翻转数量确定事件
        if flip_count >= 7:
            amount = 25
            event_type = "flip_large"
        elif flip_count >= 4:
            amount = 15
            event_type = "flip_medium"
        else:
            amount = 8
            event_type = "flip_small"

        # 翻转方减少业力（消业）
        karma_assessor.decrease_karma(amount, skill_modifiers)
        # 被翻转方也减少少量业力（5点）
        karma_assessor.decrease_karma(5, skill_modifiers)

        # 检查是否占角（4个角的位置）
        board_config = state.configs.get("board", {})
        width = board_config.get("geometry", {}).get("width", 8)
        height = board_config.get("geometry", {}).get("height", 8)
        corners = [(0, 0), (0, height - 1), (width - 1, 0), (width - 1, height - 1)]
        if tuple(to_pos) in corners:
            karma_assessor.decrease_karma(20, skill_modifiers)
            try:
                async with samsara_client(3.0) as client:
                    await client.post(
                        f"{SAMSARA_API_URL}/api/karma/event",
                        json={
                            "game_type": "heibaiqi",
                            "event_type": "corner",
                            "details": {"position": to_pos, "side": current_turn}
                        }
                    )
            except Exception as _e:
                samsara_warn("samsara 调用", _e)

        # 同步翻转事件到 samsara
        try:
            async with samsara_client(3.0) as client:
                await client.post(
                    f"{SAMSARA_API_URL}/api/karma/event",
                    json={
                        "game_type": "heibaiqi",
                        "event_type": event_type,
                        "details": {"flip_count": flip_count, "side": current_turn}
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

    port = int(os.environ.get("GAME_PORT", 8005))
    print("=" * 50)
    print("  无限制黑白棋 - 启动中...")
    print(f"  访问地址: http://localhost:{port}")
    print("=" * 50)
    uvicorn.run(app, host="0.0.0.0", port=port)
