"""剧情 API 路由（v1.4）

整合所有 RPG 系统：
- 剧情数据（序章/六道对话/结局）
- 选择系统
- 记忆碎片
- 结局判定
- 天道 Boss 战
- 真心祈求（AI 修改后的识破判定）
"""
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from .state import SamsaraState, REALMS, REALM_NAMES
from .choices import ChoiceSystem
from .memory_fragments import MemoryFragmentSystem
from .endings import EndingSystem
from .heaven_boss import HeavenBossSystem
from .detection import DetectionSystem

import json
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent.parent
STORY_FILE = BASE_DIR / "configs" / "story.json"

state = SamsaraState()
choices = ChoiceSystem(state)
memory = MemoryFragmentSystem(state)
endings = EndingSystem(state)
heaven_boss = HeavenBossSystem(state)
detection = DetectionSystem(state)

app = FastAPI(title="六道轮回 · 剧情API", version="1.4.0")


def _load_story() -> dict:
    if STORY_FILE.exists():
        try:
            return json.loads(STORY_FILE.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            pass
    return {}


def _frontend_state() -> dict:
    full = state.get_full_state()
    full["karma"] = state.get_karma()
    full["karma_max"] = state.get("karma_max", 120)
    full["detection"] = state.get_detection()
    full["skill_modifiers"] = state.get_skill_modifiers()
    full["alignment"] = state.get_alignment()
    full["detection_state"] = state.get_detection_state()
    full["prayer_count"] = state.get_prayer_count()
    full["endings_unlocked"] = state.get_endings_unlocked()
    full["memory_fragments"] = state.get_memory_fragments_unlocked()
    full["tiandao_boss_state"] = state.get_tiandao_boss_state()
    full["story_progress"] = state.get_story_progress()
    return full


# ════════════════════════════════════════
# 剧情数据
# ════════════════════════════════════════

@app.get("/api/story")
async def get_story():
    """获取完整剧情数据"""
    story = _load_story()
    return {
        "protagonist": story.get("protagonist", {}),
        "guide": story.get("guide", {}),
        "realms": story.get("realms", {}),
        "endings": story.get("endings", {}),
        "tiandao": story.get("tiandao", {}),
        "real_world_characters": story.get("real_world_characters", {}),
        "prayers": story.get("prayers", {}),
    }


@app.get("/api/story/prologue")
async def get_prologue():
    """获取序章数据"""
    story = _load_story()
    return story.get("prologue", {})


@app.get("/api/story/realm/{realm}")
async def get_realm_story(realm: str):
    """获取某道的剧情数据"""
    story = _load_story()
    realm_data = story.get("realms", {}).get(realm)
    if not realm_data:
        return {"success": False, "message": f"未知道: {realm}"}
    return {"success": True, "realm": realm, "data": realm_data}


@app.get("/api/story/realm/{realm}/level/{level}")
async def get_level_story(realm: str, level: str):
    """获取某道某关的剧情数据（对话/选择）"""
    story = _load_story()
    realm_data = story.get("realms", {}).get(realm, {})
    level_data = realm_data.get("levels", {}).get(level)
    if not level_data:
        return {"success": False, "message": f"未知关卡: {realm}/{level}"}
    # 附带选择面板的跳过状态
    choices_list = level_data.get("choices", [])
    for i, ch in enumerate(choices_list):
        ch["should_skip"] = choices.should_skip_choice(realm, level, i)
    return {
        "success": True,
        "realm": realm,
        "level": level,
        "data": level_data,
        "state": _frontend_state(),
    }


@app.get("/api/story/endings")
async def get_endings_data():
    """获取所有结局数据"""
    story = _load_story()
    return {
        "endings": story.get("endings", {}),
        "unlocked": state.get_endings_unlocked(),
        "preview": endings.get_ending_preview(),
    }


@app.get("/api/story/ending/{ending_id}")
async def get_ending_detail(ending_id: str):
    """获取某结局的完整剧情数据"""
    data = endings.get_ending_data(ending_id)
    if not data:
        return {"success": False, "message": f"未知结局: {ending_id}"}
    return {
        "success": True,
        "ending_id": ending_id,
        "data": data,
        "unlocked": state.get_endings_unlocked().get(ending_id, False),
    }


# ════════════════════════════════════════
# 选择系统
# ════════════════════════════════════════

@app.get("/api/choices/{realm}/{level}")
async def get_choices(realm: str, level: str):
    """获取某关卡的选择面板"""
    choices_list = choices.get_choices_for_level(realm, level)
    # 标注跳过状态
    result = []
    for i, ch in enumerate(choices_list):
        ch_copy = {**ch}
        ch_copy["should_skip"] = choices.should_skip_choice(realm, level, i)
        result.append(ch_copy)
    return {"choices": result, "state": _frontend_state()}


@app.post("/api/choices/apply")
async def apply_choice(request: Request):
    """应用玩家选择"""
    body = await request.json()
    realm = body.get("realm", "")
    level = body.get("level", "")
    choice_index = body.get("choice_index", 0)
    option_index = body.get("option_index", 0)

    result = choices.apply_choice(realm, level, choice_index, option_index)
    result["state"] = _frontend_state()

    # 如果选择直接触发结局
    if result.get("ending"):
        ending_result = endings.determine_ending(
            final_choice=result["ending"],
            no_cheat_final=True,
        )
        result["ending_result"] = ending_result

    return result


@app.get("/api/choices/alignment")
async def get_alignment():
    """获取当前 alignment"""
    return {
        "alignment": choices.get_alignment_summary(),
        "state": _frontend_state(),
    }


# ════════════════════════════════════════
# 记忆碎片
# ════════════════════════════════════════

@app.get("/api/memory")
async def get_memory_status():
    """获取记忆碎片状态"""
    return {
        "fragments": memory.get_all_fragments_status(),
        "unlocked": memory.get_unlocked_fragments(),
        "all_collected": memory.all_collected(),
        "unlock_count": memory.get_unlock_count(),
        "total": 6,
        "state": _frontend_state(),
    }


@app.get("/api/memory/{realm}")
async def get_memory_fragment(realm: str):
    """获取某道记忆碎片数据"""
    frag = memory.get_fragment_data(realm)
    frags = state.get_memory_fragments_unlocked()
    return {
        "realm": realm,
        "fragment": frag,
        "unlocked": frags.get(realm, False),
        "condition_met": memory.check_unlock_condition(realm),
    }


@app.post("/api/memory/{realm}/unlock")
async def try_unlock_memory(realm: str):
    """尝试解锁某道记忆碎片"""
    result = memory.try_unlock(realm)
    result["state"] = _frontend_state()
    return result


# ════════════════════════════════════════
# 结局系统
# ════════════════════════════════════════

@app.get("/api/endings")
async def get_endings_status():
    """获取所有结局解锁状态"""
    return {
        "endings": endings.get_all_endings_status(),
        "preview": endings.get_ending_preview(),
        "state": _frontend_state(),
    }


@app.post("/api/endings/determine")
async def determine_ending(request: Request):
    """判定结局"""
    body = await request.json()
    final_choice = body.get("final_choice")
    no_cheat_final = body.get("no_cheat_final", True)
    result = endings.determine_ending(final_choice, no_cheat_final)
    result["state"] = _frontend_state()
    return result


@app.get("/api/endings/preview")
async def get_ending_preview():
    """获取当前结局预览"""
    return {
        "preview": endings.get_ending_preview(),
        "should_trigger_boss": endings.should_trigger_tiandao_boss(),
        "state": _frontend_state(),
    }


# ════════════════════════════════════════
# 天道 Boss 战
# ════════════════════════════════════════

@app.get("/api/heaven-boss")
async def get_heaven_boss_info():
    """获取天道 Boss 战信息"""
    return {
        "boss_info": heaven_boss.get_boss_info(),
        "status": heaven_boss.get_status(),
        "config": heaven_boss.get_config(),
        "state": _frontend_state(),
    }


@app.post("/api/heaven-boss/enter")
async def enter_heaven_boss():
    """进入天道 Boss 战"""
    result = heaven_boss.enter_battle()
    result["state"] = _frontend_state()
    return result


@app.post("/api/heaven-boss/win")
async def win_heaven_boss():
    """天道 Boss 战胜利"""
    result = heaven_boss.on_win()
    result["state"] = _frontend_state()
    # 胜利后判定结局（识破结局）
    ending_result = endings.determine_ending(no_cheat_final=True)
    result["ending_result"] = ending_result
    return result


@app.post("/api/heaven-boss/lose")
async def lose_heaven_boss():
    """天道 Boss 战失败"""
    result = heaven_boss.on_lose()
    result["state"] = _frontend_state()
    return result


@app.get("/api/heaven-boss/board")
async def get_boss_board():
    """获取 Boss 战棋盘配置"""
    return {
        "initial_board": heaven_boss.get_initial_board(),
        "rules": heaven_boss.get_rules(),
        "pieces": heaven_boss.get_pieces_config(),
        "state": _frontend_state(),
    }


# ════════════════════════════════════════
# 真心祈求（AI 修改后的识破判定）
# ════════════════════════════════════════

@app.post("/api/prayer")
async def pray_to_tiandao(request: Request):
    """真心祈求（使用 AI 修改后调用）。
    增加祈求次数，并进行识破概率判定。
    """
    body = await request.json() if request.headers.get("content-type", "").startswith("application/json") else {}
    prayer_text = body.get("text", "")

    # 增加祈求次数
    count = state.increment_prayer()

    # 概率判定
    detection_result = detection.check_on_prayer()

    # 获取对应的低语
    story = _load_story()
    whispers = story.get("prayers", {}).get("whispers_by_count", {})
    guide_whispers = story.get("prayers", {}).get("guide_whispers_by_count", {})
    visual_hints = story.get("prayers", {}).get("visual_hints_by_count", {})

    # 找到最接近的低语
    whisper = None
    for k in sorted(whispers.keys(), key=int, reverse=True):
        if count >= int(k):
            whisper = whispers[k]
            break

    guide_whisper = None
    for k in sorted(guide_whispers.keys(), key=int, reverse=True):
        if count >= int(k):
            guide_whisper = guide_whispers[k]
            break

    visual_hint = None
    for k in sorted(visual_hints.keys(), key=int, reverse=True):
        if count >= int(k):
            visual_hint = visual_hints[k]
            break

    return {
        "success": True,
        "prayer_count": count,
        "whisper": whisper,
        "guide_whisper": guide_whisper,
        "visual_hint": visual_hint,
        "detection": detection_result,
        "exposure_path_triggered": state.is_exposure_path_triggered(),
        "state": _frontend_state(),
    }


@app.get("/api/prayer/status")
async def get_prayer_status():
    """获取祈求状态"""
    ds = state.get_detection_state()
    return {
        "prayer_count": state.get_prayer_count(),
        "detection": state.get_detection(),
        "is_detected": ds.get("is_detected", False),
        "detection_locked": ds.get("detection_locked", False),
        "exposure_path_triggered": ds.get("exposure_path_triggered", False),
        "trigger_boss_on_complete": ds.get("trigger_boss_on_complete", False),
        "state": _frontend_state(),
    }


# ════════════════════════════════════════
# 剧情进度
# ════════════════════════════════════════

@app.get("/api/story/progress")
async def get_story_progress():
    """获取剧情进度"""
    return {
        "progress": state.get_story_progress(),
        "alignment": state.get_alignment(),
        "prayer_count": state.get_prayer_count(),
        "endings_unlocked": state.get_endings_unlocked(),
        "memory_fragments": state.get_memory_fragments_unlocked(),
        "tiandao_boss_state": state.get_tiandao_boss_state(),
        "all_realms_completed": state.all_realms_completed(),
        "all_memory_collected": state.all_memory_fragments_collected(),
        "ending_preview": endings.get_ending_preview(),
        "should_trigger_boss": endings.should_trigger_tiandao_boss(),
        "state": _frontend_state(),
    }


@app.post("/api/story/progress")
async def update_story_progress(request: Request):
    """更新剧情进度"""
    body = await request.json()
    state.update_story_progress(**body)
    return {"success": True, "progress": state.get_story_progress(), "state": _frontend_state()}


@app.post("/api/story/mark-prologue-seen")
async def mark_prologue_seen():
    """标记序章已观看"""
    state.update_story_progress(prologue_seen=True)
    return {"success": True, "state": _frontend_state()}


# ════════════════════════════════════════
# RPG 总览（Hub 用）
# ════════════════════════════════════════

@app.get("/api/rpg/overview")
async def get_rpg_overview():
    """RPG 系统总览（Hub 入口展示用）"""
    align = state.get_alignment()
    ds = state.get_detection_state()
    frags = state.get_memory_fragments_unlocked()
    boss = state.get_tiandao_boss_state()

    return {
        "protagonist": _load_story().get("protagonist", {}),
        "alignment": {
            "enlightenment": align.get("enlightenment", 0),
            "corruption": align.get("corruption", 0),
            "rationality": align.get("rationality", 0),
            "emotion": align.get("emotion", 0),
        },
        "detection": {
            "value": state.get_detection(),
            "is_detected": ds.get("is_detected", False),
            "locked": ds.get("detection_locked", False),
            "exposure_path": ds.get("exposure_path_triggered", False),
        },
        "prayer_count": state.get_prayer_count(),
        "memory_fragments": {
            "unlocked_count": sum(1 for v in frags.values() if v),
            "total": 6,
            "all_collected": state.all_memory_fragments_collected(),
        },
        "tiandao_boss": {
            "defeated": boss.get("defeated", False),
            "attempts": boss.get("attempt_count", 0),
            "can_enter": endings.should_trigger_tiandao_boss(),
        },
        "endings": {
            "unlocked": state.get_endings_unlocked(),
            "preview": endings.get_ending_preview(),
        },
        "story_progress": state.get_story_progress(),
        "all_realms_completed": state.all_realms_completed(),
        "playthrough_count": state.get("playthrough_count", 1),
        "state": _frontend_state(),
    }
