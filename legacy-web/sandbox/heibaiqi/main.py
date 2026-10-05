"""
无限制黑白棋 - 主服务器
黑白棋子项目的 FastAPI 入口。剧情编辑器与共享资产已分离至各自子项目。
"""
import os
import sys
import json
import copy
from pathlib import Path
from typing import Dict, Any, Optional

# 将 shared/ 加入 sys.path，以便复用 schema_validator / json_patch_utils
BASE_DIR = Path(__file__).resolve().parent
WORKSPACE_ROOT = BASE_DIR.parent.parent  # sandbox 比顶层深一层，需再上一级到仓库根
SHARED_DIR = WORKSPACE_ROOT / "shared"
if str(SHARED_DIR) not in sys.path:
    sys.path.insert(0, str(SHARED_DIR))

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, Request, Query
from fastapi.responses import JSONResponse, HTMLResponse, FileResponse
from fastapi.staticfiles import StaticFiles
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

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

CONFIG_FILES = ["board_state", "board", "pieces_black", "pieces_white", "rules", "ui_config"]

# ═══════════════════════════════════════════════════════════════
# 状态管理
# ═══════════════════════════════════════════════════════════════


class GameState:
    """全局游戏状态"""

    def __init__(self):
        self.configs: Dict[str, dict] = {}
        self._config_cache_valid: bool = False  # 配置缓存标志：True 时不再从磁盘读取
        self.ai_orchestrator = AIOrchestrator(api_key=get_api_key())
        self.rule_engine: Optional[RuleEngine] = None
        self.chess_ai: Optional[ChessAI] = None
        self.mechanism_engine: Optional[MechanismEngine] = None
        self.undo_stack: list = []  # 修改历史，用于撤回AI修改
        self.load_configs()

    def load_configs(self):
        """加载所有配置文件（仅缓存失效时才从磁盘读取）"""
        if not self._config_cache_valid:
            for name in CONFIG_FILES:
                path = CONFIGS_DIR / f"{name}.json"
                if path.exists():
                    with open(path, "r", encoding="utf-8") as f:
                        self.configs[name] = json.load(f)
            self._config_cache_valid = True
        self._rebuild_engines()

    def invalidate_config_cache(self):
        """使配置缓存失效：下一次 load_configs 从磁盘重新读取"""
        self._config_cache_valid = False

    def mark_config_cache_fresh(self):
        """标记内存配置为最新（缓存有效，复用内存）"""
        self._config_cache_valid = True

    def save_config(self, name: str):
        """保存配置到文件"""
        path = CONFIGS_DIR / f"{name}.json"
        with open(path, "w", encoding="utf-8") as f:
            json.dump(self.configs[name], f, ensure_ascii=False, indent=2)

    def save_all(self):
        """保存所有配置"""
        for name in CONFIG_FILES:
            self.save_config(name)

    def _rebuild_engines(self):
        """重建规则引擎和AI引擎"""
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

    def apply_config_update(self, updates: Dict[str, dict]):
        """应用配置更新"""
        # 保存当前状态到撤销栈
        snapshot = {}
        for name in updates:
            if name in self.configs:
                snapshot[name] = copy.deepcopy(self.configs[name])
        if snapshot:
            self.undo_stack.append(snapshot)
            if len(self.undo_stack) > 10:
                self.undo_stack.pop(0)

        for name, data in updates.items():
            self.configs[name] = data
            self.save_config(name)

        self.mark_config_cache_fresh()
        self._rebuild_engines()

    def undo_last_config_change(self) -> bool:
        """撤回上一次AI配置修改"""
        if not self.undo_stack:
            return False
        snapshot = self.undo_stack.pop()
        for name, data in snapshot.items():
            self.configs[name] = data
            self.save_config(name)
        self.mark_config_cache_fresh()
        self._rebuild_engines()
        return True

    def reset_board(self):
        """重置棋盘到初始状态 - 重置所有配置"""
        # 从初始备份目录加载所有配置
        initial_dir = CONFIGS_DIR / "initial"
        for name in CONFIG_FILES:
            initial_path = initial_dir / f"{name}.json.initial"
            if initial_path.exists():
                with open(initial_path, "r", encoding="utf-8") as f:
                    self.configs[name] = json.load(f)

        # 清空撤销栈
        self.undo_stack.clear()
        self.mark_config_cache_fresh()
        self._rebuild_engines()
        self.save_all()


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
# 数据模型
# ═══════════════════════════════════════════════════════════════


class PlayerCommand(BaseModel):
    command: str


class SetApiKey(BaseModel):
    api_key: str


class MoveRequest(BaseModel):
    to: list  # [x, y] - 黑白棋无 piece_id，仅需落子坐标


class ValidMovesRequest(BaseModel):
    side: str  # black | white


class DifficultyRequest(BaseModel):
    difficulty: str  # easy | normal | hard | master


# ═══════════════════════════════════════════════════════════════
# API 路由
# ═══════════════════════════════════════════════════════════════


@app.get("/")
async def index():
    """返回主页面"""
    html_path = STATIC_DIR / "index.html"
    if html_path.exists():
        return HTMLResponse(html_path.read_text(encoding="utf-8"))
    return HTMLResponse("<h1>前端文件未找到</h1>", status_code=404)


@app.get("/api/config/all")
async def get_all_configs():
    """获取所有配置（必须放在 /{config_name} 路由之前）"""
    return state.configs


@app.get("/api/config/{config_name}")
async def get_config(config_name: str):
    """获取配置"""
    if config_name not in CONFIG_FILES:
        return JSONResponse({"error": "无效的配置名"}, status_code=400)
    return state.configs.get(config_name, {})


@app.post("/api/apikey")
async def set_api_key(req: SetApiKey):
    """设置API密钥"""
    state.ai_orchestrator.set_api_key(req.api_key)
    if state.chess_ai:
        state.chess_ai.set_api_key(req.api_key)
    return {"success": True, "message": "API密钥已设置"}


@app.get("/api/apikey/status")
async def api_key_status():
    """检查API密钥状态"""
    return {"has_key": bool(state.ai_orchestrator.api_key)}


@app.post("/api/command")
async def process_command(req: PlayerCommand, dry_run: str = Query(None)):
    """处理玩家自然语言指令

    dry_run=1 时仅解析意图与评估 cost_energy，不写入配置（供 RPG cheat/assess 使用）。
    """
    try:
        result = await state.ai_orchestrator.process_command(
            req.command, {"configs": state.configs}
        )

        # dry_run 模式下不应用配置变更，仅返回解析结果（含 cost_energy/classification）
        is_dry_run = dry_run == "1"
        if (not is_dry_run) and result.get("success") and result.get("type") == "applied":
            modified = result.get("modified_configs", {})
            if modified:
                state.apply_config_update(modified)

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


@app.get("/api/mechanisms")
async def get_mechanisms():
    """获取当前激活的机制列表"""
    board = state.configs["board_state"]
    summary = []
    if state.mechanism_engine:
        summary = state.mechanism_engine.get_active_mechanisms_summary(board)
    return {"success": True, "mechanisms": summary, "raw": board.get("mechanisms", {})}


class StopMechanismRequest(BaseModel):
    mechanism_type: str  # skip_turns | ai_control | random_moves | extra_turns | move_limits | player_control
    side: str  # black | white | both


@app.post("/api/stop_mechanism")
async def stop_mechanism(req: StopMechanismRequest):
    """截停指定方的指定机制"""
    board = state.configs["board_state"]
    mech = board.get("mechanisms", {})
    if req.mechanism_type in mech and isinstance(mech[req.mechanism_type], list):
        # 移除指定方的所有激活机制
        # player_control机制没有remaining字段，始终有效
        if req.mechanism_type == "player_control":
            mech[req.mechanism_type] = [
                item for item in mech[req.mechanism_type]
                if not item.get("side") == req.side
            ]
        else:
            mech[req.mechanism_type] = [
                item for item in mech[req.mechanism_type]
                if not (item.get("side") == req.side and item.get("remaining", 0) != 0)
            ]
        board["mechanisms"] = mech
        state.save_config("board_state")

    summary = []
    if state.mechanism_engine:
        summary = state.mechanism_engine.get_active_mechanisms_summary(board)
    return {"success": True, "message": "机制已截停", "mechanisms": summary, "board_state": board}


@app.get("/api/token_stats")
async def get_token_stats():
    """获取Token消耗统计"""
    return state.ai_orchestrator.get_token_stats()


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


@app.post("/api/undo_config")
async def undo_config_change():
    """撤回AI配置修改"""
    success = state.undo_last_config_change()
    if success:
        return {"success": True, "message": "已撤回上一次AI修改", "configs": state.configs}
    return {"success": False, "message": "没有可撤回的修改"}


@app.post("/api/restart")
async def restart_game():
    """重新开始游戏"""
    state.reset_board()
    return {"success": True, "message": "游戏已重新开始", "board_state": state.configs["board_state"]}


@app.post("/api/difficulty")
async def set_difficulty(req: DifficultyRequest):
    """设置AI难度"""
    if req.difficulty not in ["easy", "normal", "hard", "master"]:
        return {"success": False, "message": "无效的难度"}

    state.configs["rules"]["ai_difficulty"]["current"] = req.difficulty
    state.save_config("rules")
    state.mark_config_cache_fresh()
    state.chess_ai.set_difficulty(req.difficulty)
    return {"success": True, "message": f"难度已设置为{req.difficulty}"}


@app.post("/api/reset_configs")
async def reset_configs():
    """重置所有配置到初始状态"""
    state.reset_board()
    return {"success": True, "message": "所有配置已重置"}


@app.get("/api/logs")
async def get_logs(count: int = 10):
    """获取AI对话日志"""
    return {"logs": state.ai_orchestrator.get_logs(count)}


@app.get("/api/thinking_status")
async def get_thinking_status():
    """获取AI思考状态"""
    return state.ai_orchestrator.get_thinking_status()


@app.post("/api/clear_logs")
async def clear_logs():
    """清空日志"""
    state.ai_orchestrator.logger.clear()
    return {"success": True, "message": "日志已清空"}


# ═══════════════════════════════════════════════════════════════
# RPG 代理路由（供 rpg_server:80 调用）
# ═══════════════════════════════════════════════════════════════

from json_patch_utils import apply_patch as _rpg_apply_patch  # noqa: E402


class RpgApplyPatchReq(BaseModel):
    patch: list
    target: str  # board_state / rules / pieces_black / pieces_white / board / ui_config


@app.post("/api/rpg/apply_patch")
async def rpg_apply_patch(req: RpgApplyPatchReq):
    """应用 JSON Patch 到指定配置文件"""
    if req.target not in CONFIG_FILES:
        return JSONResponse({"success": False, "message": f"无效 target: {req.target}"}, status_code=400)
    try:
        current = copy.deepcopy(state.configs.get(req.target, {}))
        patched = _rpg_apply_patch(current, req.patch)
        state.configs[req.target] = patched
        state.save_config(req.target)
        state.mark_config_cache_fresh()
        state._rebuild_engines()
        return {"success": True, "target": req.target, "configs": patched}
    except Exception as e:
        import traceback
        traceback.print_exc()
        return {"success": False, "message": f"应用 patch 失败: {e}"}


@app.post("/api/rpg/apply_rules")
async def rpg_apply_rules(req: RpgApplyPatchReq):
    """应用规则覆盖（target 强制为 rules）"""
    req.target = "rules"
    return await rpg_apply_patch(req)


@app.post("/api/rpg/reset_battle")
async def rpg_reset_battle():
    """RPG 每局开始时调用，重置棋盘到初始状态"""
    state.reset_board()
    return {"success": True, "message": "战斗已重置", "board_state": state.configs["board_state"]}


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
