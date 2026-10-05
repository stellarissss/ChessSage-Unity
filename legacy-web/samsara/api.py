from typing import Any, Dict

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel
from .state import SamsaraState, REALM_NAMES
from .karma import KarmaSystem
from .karma_assessor import KarmaAssessor
from .detection import DetectionSystem
from .bosses import BossSystem
from .skills import SkillSystem
from .progression import ProgressionSystem
from .levels import LevelSystem
from .objectives import ObjectiveSystem
from .turn_limit import TurnLimitSystem
from .story_api import app as story_app

state = SamsaraState()
karma = KarmaSystem(state)
karma_assessor = KarmaAssessor(state)
detection = DetectionSystem(state)
bosses = BossSystem(state)
skills = SkillSystem(state)
progression = ProgressionSystem(state)
levels = LevelSystem(state)
objectives = ObjectiveSystem(state)
turn_limit = TurnLimitSystem(state)

app = FastAPI(title="六道轮回 API", version="1.4.1")
# 挂载剧情 API（RPG 系统）
app.mount("/story", story_app)

NO_CACHE_HEADERS = {
    "Cache-Control": "no-store, no-cache, must-revalidate, max-age=0",
    "Pragma": "no-cache",
    "Expires": "0",
}


# ─────────────────────────────────────────────────────────────
# 请求模型（审查报告 4.3）：原先直接 `body.get()`，字段类型错误会在
# 业务运算中抛异常 → 500。改为 Pydantic 校验后，类型错误统一 422。
# 所有字段均带默认值且允许额外字段，保证对现有调用方完全兼容。
# ─────────────────────────────────────────────────────────────

class _LooseModel(BaseModel):
    model_config = {"extra": "allow"}


class KarmaRecoverReq(_LooseModel):
    game_type: str = ""
    event_type: str = ""
    event_data: Dict[str, Any] = {}


class KarmaAssessReq(_LooseModel):
    game_type: str = ""
    instruction: str = ""
    intent_class: str = "C"
    board_summary: str = ""


class KarmaConsumeReq(_LooseModel):
    amount: float = 0
    allow_overdraft: bool = True


class KarmaEventReq(_LooseModel):
    game_type: str = ""
    event_type: str = ""
    details: Dict[str, Any] = {}


class KarmaRefundReq(_LooseModel):
    amount: float = 0


def _frontend_state() -> dict:
    """统一使用 SamsaraState.get_frontend_state() 避免双副本错位。"""
    return state.get_frontend_state()


def _nc(payload) -> JSONResponse:
    """带 Cache-Control: no-store 的响应。"""
    return JSONResponse(content=payload, headers=NO_CACHE_HEADERS)


@app.get("/api/state")
async def get_samsara_state():
    return _nc(_frontend_state())


@app.get("/api/karma")
async def get_karma():
    return _nc(karma.get_state())


@app.post("/api/karma/recover")
async def recover_karma(req: KarmaRecoverReq):
    game_type = req.game_type
    event_type = req.event_type
    event_data = req.event_data
    amount = karma.recover(game_type, event_type, event_data)
    return _nc({"success": True, "amount": amount, "karma": karma.get_state(), "state": _frontend_state()})


@app.post("/api/karma/assess")
async def assess_karma(req: KarmaAssessReq):
    game_type = req.game_type
    instruction = req.instruction
    intent_class = req.intent_class
    board_summary = req.board_summary
    amount = await karma_assessor.assess(game_type, instruction, intent_class, board_summary)
    return _nc({"success": True, "estimated_cost": amount})


@app.post("/api/karma/consume")
async def consume_karma(req: KarmaConsumeReq):
    """作弊增加业力。返回 overshoot 信息用于识破判定。"""
    amount = req.amount
    allow_overdraft = req.allow_overdraft
    actual, is_overdraft, overdraft_amount = karma.consume(amount, allow_overdraft)
    if actual == 0:
        return _nc({"success": False, "message": "超出单次上限，拦截", "state": _frontend_state()})
    result = {
        "success": True,
        "actual_consumed": actual,
        "is_overdraft": is_overdraft,
        "overdraft_amount": overdraft_amount,
        "karma": karma.get_state(),
        "state": _frontend_state(),
    }
    if is_overdraft:
        detection_result = detection.handle_overdraft(overdraft_amount)
        result["detection"] = detection_result
    return _nc(result)


@app.post("/api/karma/event")
async def karma_event(req: KarmaEventReq):
    game_type = req.game_type
    event_type = req.event_type
    details = req.details
    amount = karma.recover(game_type, event_type, details)
    return _nc({"success": True, "amount": amount, "state": _frontend_state()})


@app.post("/api/karma/refund")
async def refund_karma(req: KarmaRefundReq):
    amount = req.amount
    karma.refund(amount)
    return _nc({"success": True, "karma": karma.get_state(), "state": _frontend_state()})


@app.get("/api/detection")
async def get_detection():
    return _nc({"detection": state.get_detection(), "state": _frontend_state()})


@app.get("/api/skills")
async def get_skills():
    return _nc({
        "skill_points": state.get("skill_points", 0),
        "skill_tree": skills.get_skill_tree(),
        "available": skills.get_available_skills(),
        "modifiers": state.get_skill_modifiers(),
        "state": _frontend_state(),
    })


@app.get("/api/skills/tree")
async def get_skill_tree():
    return _nc(skills.get_skill_tree())


@app.post("/api/skills/unlock")
async def unlock_skill(request: Request):
    body = await request.json()
    skill_id = body.get("skill_id", "")
    tier = body.get("tier", 1)
    success = skills.unlock_skill(skill_id, tier)
    return _nc({"success": success, "skill_points": state.get("skill_points", 0), "state": _frontend_state()})


@app.get("/api/levels")
async def get_levels():
    return _nc({
        "current_level": levels.get_current_level(),
        "total_levels": levels.get_total_levels(),
        "realms": levels.get_all_realms_progress(),
        "state": _frontend_state(),
    })


@app.post("/api/levels/start")
async def start_level(request: Request):
    body = await request.json()
    realm = body.get("realm", None)
    level_index = body.get("level_index", None)
    mode = body.get("mode", "level")
    if realm:
        state.set_realm(realm)
    if level_index is not None:
        state.set("current_level", level_index)
    state.reset_level_state()
    state.set_sandbox_mode(mode == "sandbox")
    if mode == "sandbox":
        level = levels.load_sandbox()
    else:
        level = levels.load_level()
    turn_limit.reset(level.get("turn_limit", 40 if level.get("game_type") == "weiqi" else 20))
    bosses.reset_boss_skills()
    return _nc({"success": True, "level": level, "state": _frontend_state()})


@app.post("/api/levels/advance")
async def advance_level():
    result = levels.advance_to_next_level()
    next_level = None
    if result["success"]:
        state.reset_level_state()
        turn_limit.reset(40 if result["new_level"].get("game_type") == "weiqi" else 20)
        bosses.reset_boss_skills()
        # Phase 2 修复：返回 next_level 对象，方便前端「下一关」按钮直接同步 UI
        next_level = {
            "level_index": result["new_level"].get("level_index", state.get("current_level", 0)),
            "realm": state.get("current_realm", "hell"),
            "realm_name": REALM_NAMES.get(state.get("current_realm", "hell"), ""),
            "level": levels.load_level(),
        }
    result["state"] = _frontend_state()
    if next_level is not None:
        result["next_level"] = next_level
    return _nc(result)


@app.post("/api/levels/reset_level")
async def reset_level_vars_only():
    """（棋页「重置所有配置」使用）只重置当前关内部变量：业力 / turn / cheat 计数 / 识破。

    不切换道、不切 level_index、不删技能/成就/对齐字段；等价于软档对当前关变量的一次清零。
    调用后会一并重置 turn_limit 与 Boss 技能，返回完整前端 state。
    """
    nl = levels.load_level()
    game_type = (nl or {}).get("game_type")
    state.reset_level_state()
    turn_limit.reset(40 if game_type == "weiqi" else 20)
    bosses.reset_boss_skills()
    return _nc({"success": True, "state": _frontend_state()})


@app.get("/api/objectives")
async def get_objectives():
    level = levels.get_current_level()
    objective = level.get("objective") if level else None
    if not objective:
        objective = {"type": "checkmate"}
    return _nc({"objective": objective, "state": _frontend_state()})


@app.post("/api/objectives/check")
async def check_objective(request: Request):
    body = await request.json()
    game_state = body.get("game_state", {})
    level = levels.get_current_level()
    objective = level.get("objective") if level else {"type": "checkmate"}
    result = objectives.check(objective, game_state)
    return _nc({"success": True, "objective": objective, "result": result, "state": _frontend_state()})


@app.post("/api/turn/tick")
async def tick_turn():
    is_over = turn_limit.tick()
    return _nc({
        "success": True,
        "current_turn": state.get("current_turn", 0),
        "remaining_turns": turn_limit.get_remaining(),
        "is_over": is_over,
        "state": _frontend_state(),
    })


@app.get("/api/turn/status")
async def get_turn_status():
    return _nc({
        "current_turn": state.get("current_turn", 0),
        "turn_limit": state.get("turn_limit", 20),
        "remaining_turns": turn_limit.get_remaining(),
        "state": _frontend_state(),
    })


@app.post("/api/turn/increment")
async def increment_turn(request: Request):
    body = await request.json()
    game_type = body.get("game_type", "")
    is_over = turn_limit.tick()
    return _nc({
        "success": True,
        "current_turn": state.get("current_turn", 0),
        "remaining_turns": turn_limit.get_remaining(),
        "is_over": is_over,
        "state": _frontend_state(),
    })


@app.post("/api/turn/reset")
async def reset_turn():
    turn_limit.reset()
    return _nc({"success": True, "state": _frontend_state()})


@app.get("/api/boss")
async def get_boss():
    return _nc({"boss": bosses.get_current_boss(), "state": _frontend_state()})


@app.post("/api/boss/trigger")
async def trigger_boss_skill(request: Request):
    body = await request.json()
    cheat_count = body.get("cheat_count", 0)
    result = bosses.trigger_boss_skill(cheat_count)
    result["state"] = _frontend_state()
    return _nc(result)


@app.post("/api/progression/resolve")
async def resolve_level(request: Request):
    body = await request.json()
    won = body.get("won", False)
    no_cheat = body.get("no_cheat", False)
    boss_defeated = body.get("boss_defeated", False)
    rewards = progression.resolve_level(won, no_cheat, boss_defeated)

    if won:
        advance_result = progression.advance_realm()
        rewards["realm_advance"] = advance_result
        # ── Bug 2 修复：把下一关信息注入响应给前端“下一关”按钮使用 ──
        next_level = None
        if advance_result.get("realm_switched"):
            # 道切换：load_level 默认取第 0 关
            nl = levels.load_level()
            next_level = {
                "level_index": 0,
                "realm": state.get("current_realm", "hell"),
                "realm_name": REALM_NAMES.get(state.get("current_realm", "hell"), ""),
                "level": nl,
            }
        else:
            # 道内：advance_to_next_level 返回 new_level
            adv = levels.advance_to_next_level()
            if adv.get("success"):
                new_idx = adv["new_level"]["level_index"]
                nl = levels.load_level()
                next_level = {
                    "level_index": new_idx,
                    "realm": state.get("current_realm", "hell"),
                    "realm_name": REALM_NAMES.get(state.get("current_realm", "hell"), ""),
                    "level": nl,
                }
                # 推进关卡号后同步：重置业力、回合限制、Boss 技能
                state.reset_level_state()
                game_type = (nl or {}).get("game_type")
                turn_limit.reset(40 if game_type == "weiqi" else 20)
                bosses.reset_boss_skills()
        rewards["next_level"] = next_level
    return _nc({"success": True, "rewards": rewards, "state": _frontend_state()})


@app.post("/api/progression/retreat")
async def retreat_realm():
    result = progression.retreat_realm()
    result["state"] = _frontend_state()
    return _nc(result)


@app.post("/api/cheat/record")
async def record_cheat():
    state.record_cheat()
    cheat_count = state.get("cheat_count", 0)
    boss_result = bosses.trigger_boss_skill(cheat_count)
    return _nc({"success": True, "cheat_count": cheat_count, "boss_trigger": boss_result, "state": _frontend_state()})


@app.post("/api/reset")
async def reset_samsara(request: Request):
    """存档重置。

    body.mode 取值：
      - "soft" (默认): 保留技能树 / 技能点 / 结局等 RPG 长期进度，只重置当前关卡进度。
      - "hard":         全部默认化（仅 version 保留，备份为 .bak）。
    端点会返回完整前端状态，供 hub 及棋类前端直接同步刷新 UI。
    """
    body = {}
    try:
        body = await request.json()
    except Exception:
        body = {}
    mode = body.get("mode", "soft")
    full = (mode == "hard")
    state.reset_all(full=full)
    turn_limit.reset()
    bosses.reset_boss_skills()
    return _nc({"success": True, "mode": mode, "state": _frontend_state()})


@app.get("/api/realms")
async def get_realms():
    return _nc([
        {"id": k, "name": v} for k, v in REALM_NAMES.items()
    ])


@app.get("/api/levels/realm/{realm}")
async def get_realm_levels(realm: str):
    result = levels.get_realm_levels(realm)
    if not result:
        return _nc({"success": False, "message": f"未知道: {realm}"})
    result["state"] = _frontend_state()
    return _nc({"success": True, **result})


@app.post("/api/levels/sandbox")
async def start_sandbox(request: Request):
    body = await request.json()
    realm = body.get("realm", None)
    if realm:
        state.set_realm(realm)
    if not state.is_sandbox_unlocked(state.get("current_realm")):
        return _nc({"success": False, "message": "沙盒模式未解锁，请先通关该道所有关卡"})
    state.reset_level_state()
    state.set_sandbox_mode(True)
    level = levels.load_sandbox()
    turn_limit.reset(level.get("turn_limit", 20))
    bosses.reset_boss_skills()
    return _nc({"success": True, "level": level, "state": _frontend_state()})


@app.post("/api/detection/reset")
async def reset_on_detection():
    state.reset_on_detection()
    return _nc({"success": True, "message": detection.get_reset_message(), "state": _frontend_state()})