"""
无限制五子棋 - 主服务器
五子棋子项目的 FastAPI 入口。
"""
import os
import sys
import json
import copy
from pathlib import Path
from typing import Dict, Any, Optional

# 将 shared/ 加入 sys.path，复用 schema_validator / json_patch_utils
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
from chess_ai import GomokuAI
from mechanism_engine import MechanismEngine
from ai_config import get_api_key

CONFIGS_DIR = BASE_DIR / "configs"
STATIC_DIR = BASE_DIR / "static"

CONFIG_FILES = ["board_state", "board", "pieces_red", "pieces_black", "rules", "ui_config"]


class GameState:
    """全局游戏状态"""

    def __init__(self):
        self.configs: Dict[str, dict] = {}
        self._config_cache_valid: bool = False  # 配置缓存标志：True 时不再从磁盘读取
        self.ai_orchestrator = AIOrchestrator(api_key=get_api_key())
        self.rule_engine: Optional[RuleEngine] = None
        self.chess_ai: Optional[GomokuAI] = None
        self.mechanism_engine: Optional[MechanismEngine] = None
        self.undo_stack: list = []
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

    def apply_config_update(self, updates: Dict[str, dict]):
        """应用配置更新"""
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
        """重置棋盘到初始状态"""
        initial_dir = CONFIGS_DIR / "initial"
        for name in CONFIG_FILES:
            initial_path = initial_dir / f"{name}.json.initial"
            if initial_path.exists():
                with open(initial_path, "r", encoding="utf-8") as f:
                    self.configs[name] = json.load(f)

        self.undo_stack.clear()
        self.mark_config_cache_fresh()
        self._rebuild_engines()
        self.save_all()


state = GameState()

app = FastAPI(title="无限制五子棋", version="2.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

if STATIC_DIR.exists():
    app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

# 挂载共享 assets 目录（角色立绘等，便于前端引用）
SHARED_ASSETS_DIR = SHARED_DIR / "assets"
if SHARED_ASSETS_DIR.exists():
    app.mount("/assets", StaticFiles(directory=str(SHARED_ASSETS_DIR)), name="assets")


class PlayerCommand(BaseModel):
    command: str


class SetApiKey(BaseModel):
    api_key: str


class MoveRequest(BaseModel):
    piece_id: Optional[str] = None
    to: list  # [x, y]


class DifficultyRequest(BaseModel):
    difficulty: str


@app.get("/")
async def index():
    """返回主页面"""
    html_path = STATIC_DIR / "index.html"
    if html_path.exists():
        return HTMLResponse(html_path.read_text(encoding="utf-8"))
    return HTMLResponse("<h1>前端文件未找到</h1>", status_code=404)


@app.get("/api/config/all")
async def get_all_configs():
    """获取所有配置"""
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


@app.get("/api/mechanisms")
async def get_mechanisms():
    """获取当前激活的机制列表"""
    board = state.configs["board_state"]
    summary = []
    if state.mechanism_engine:
        summary = state.mechanism_engine.get_active_mechanisms_summary(board)
    return {"success": True, "mechanisms": summary, "raw": board.get("mechanisms", {})}


class StopMechanismRequest(BaseModel):
    mechanism_type: str
    side: str


@app.post("/api/stop_mechanism")
async def stop_mechanism(req: StopMechanismRequest):
    """截停指定方的指定机制"""
    board = state.configs["board_state"]
    mech = board.get("mechanisms", {})
    if req.mechanism_type in mech and isinstance(mech[req.mechanism_type], list):
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
    if req.difficulty not in ["easy", "medium", "hard"]:
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
    target: str  # board_state / rules / pieces_red / pieces_black / board / ui_config


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


if __name__ == "__main__":
    import uvicorn

    port = int(os.environ.get("GAME_PORT", 8001))
    print("=" * 50)
    print("  无限制五子棋 - 启动中...")
    print(f"  访问地址: http://localhost:{port}")
    print("=" * 50)
    uvicorn.run(app, host="0.0.0.0", port=port)