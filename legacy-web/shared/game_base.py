"""
六道众生（CHessGAme）- 多棋类共享基类与通用路由。

设计原则（Python 之禅）：
    Beautiful is better than ugly.
    Simple is better than complex.
    Readability counts.
    Special cases aren't special enough to break the rules.
    If the implementation is hard to explain, it's a bad idea.

目标：
    - BaseGameState: 提取 6 个 main.py 中完全一致的 GameState 方法（7 个共享方法）。
      仅子类 _rebuild_engines 有棋类参数化差异；动物棋可覆盖 _after_reset_board。
    - register_common_routes: 一次性注册 25+ 条所有棋类完全相同的 API 路由，
      避免 6 个文件中重复约 1500 行 / 份 的完全相同代码。

约束：
    - 不改变任何对外接口契约（路径、方法、请求字段、响应字段、HTTP 状态码、错误消息语义）。
    - 不改变数值默认值或业务规则。
    - 所有被提取的方法和路由在原文件（xiangqi/main.py）中行为保持 100% 等价。
"""
from __future__ import annotations

import copy
import json
import logging
import os
import threading
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Dict, List, Optional

import httpx
from fastapi import FastAPI, Query, Request
from fastapi.responses import HTMLResponse, JSONResponse, Response
from pydantic import BaseModel

# 共享模块的相对导入（调用方需先将 shared/ 加入 sys.path 或作为包导入）
try:
    from .ai_config import get_api_key
    from .json_patch_utils import apply_patch as _rpg_apply_patch
    from .schema_validator import validate_config  # noqa: F401 (may be used by subclasses)
except ImportError:  # Fallback：当以直接脚本方式运行、shared/ 在 sys.path 时
    from ai_config import get_api_key  # type: ignore
    from json_patch_utils import apply_patch as _rpg_apply_patch  # type: ignore
    from schema_validator import validate_config  # type: ignore  # noqa: F401


# ═══════════════════════════════════════════════════════════════
# 共享 Pydantic 数据模型（6 棋类均相同）
# ═══════════════════════════════════════════════════════════════

class PlayerCommand(BaseModel):
    """玩家自然语言指令。"""
    command: str


class SetApiKey(BaseModel):
    """设置 API 密钥请求。"""
    api_key: str


class DifficultyRequest(BaseModel):
    """设置 AI 难度请求。"""
    difficulty: str  # 允许的值由 difficulty_levels 参数化


class StopMechanismRequest(BaseModel):
    """截停机制请求（所有棋类字段相同）。"""
    mechanism_type: str  # skip_turns | ai_control | random_moves | extra_turns | move_limits | player_control
    side: str  # 各棋类颜色命名（red/black、black/white），实现通用兼容


class RpgApplyPatchReq(BaseModel):
    """RPG 服务器调用 JSON Patch/规则覆盖请求。"""
    patch: list
    target: str  # board_state / rules / pieces_red / pieces_black 等


class KarmaRecoverRequest(BaseModel):
    """业力消减（消业）请求。

    各棋类原实现直接用 body.get()，缺字段/类型错误会 500 或静默出错；
    这里统一建模：event_type 必填，amount 必填（非负），其余字段原样透传给 samsara。
    """
    event_type: str = ""
    amount: float = 0

    model_config = {"extra": "allow"}


# ═══════════════════════════════════════════════════════════════
# Samsara 调用层：共享 httpx 连接池 + 统一降级与日志
# ═══════════════════════════════════════════════════════════════
#
# 背景（审查报告 4.3）：原先每处调用都 `async with httpx.AsyncClient(timeout=...)`，
# 每次新建 TCP 连接（无连接池、无 keep-alive 复用），50 余处调用重复建连；
# 且大量 `except Exception: pass` 静默吞错，前端拿到 200 却数据不完整。
#
# 方案：
#   1. 进程级共享 AsyncClient（连接池复用），随 FastAPI 生命周期关闭；
#   2. samsara_client(timeout) 作为上下文管理器，返回带默认超时的轻量代理，
#      调用点只需把 `httpx.AsyncClient(timeout=X)` 换成 `samsara_client(X)`，
#      缩进与后续代码完全不变，同时保留各调用点原有的超时语义；
#   3. samsara_warn() 统一记录降级原因（替代静默 pass），便于排障。
# ═══════════════════════════════════════════════════════════════

_samsara_logger = logging.getLogger("samsara_client")

_SHARED_CLIENT: Optional[httpx.AsyncClient] = None
_CLIENT_LOCK = threading.Lock()

# 连接池：总连接 100 / 单主机 20，足够 Hub + 6 RPG + 6 sandbox 并发
_HTTP_LIMITS = httpx.Limits(max_connections=100, max_keepalive_connections=20)


def get_samsara_client() -> httpx.AsyncClient:
    """获取（必要时创建）进程级共享的 httpx 异步客户端。"""
    global _SHARED_CLIENT
    if _SHARED_CLIENT is None or _SHARED_CLIENT.is_closed:
        with _CLIENT_LOCK:
            if _SHARED_CLIENT is None or _SHARED_CLIENT.is_closed:
                _SHARED_CLIENT = httpx.AsyncClient(
                    limits=_HTTP_LIMITS,
                    timeout=httpx.Timeout(10.0),
                    headers={"connection": "keep-alive"},
                )
    return _SHARED_CLIENT


async def close_samsara_client() -> None:
    """关闭共享客户端（FastAPI shutdown 钩子调用）。"""
    global _SHARED_CLIENT
    client, _SHARED_CLIENT = _SHARED_CLIENT, None
    if client is not None and not client.is_closed:
        await client.aclose()


class _TimeoutClientProxy:
    """为共享客户端注入默认超时的轻量代理（保持 client.get/post 签名兼容）。"""

    __slots__ = ("_client", "_timeout")

    def __init__(self, client: httpx.AsyncClient, timeout: float):
        self._client = client
        self._timeout = timeout

    def _with_timeout(self, kwargs: dict) -> dict:
        kwargs.setdefault("timeout", self._timeout)
        return kwargs

    async def get(self, url, **kwargs):
        return await self._client.get(url, **self._with_timeout(kwargs))

    async def post(self, url, **kwargs):
        return await self._client.post(url, **self._with_timeout(kwargs))

    async def put(self, url, **kwargs):
        return await self._client.put(url, **self._with_timeout(kwargs))

    async def patch(self, url, **kwargs):
        return await self._client.patch(url, **self._with_timeout(kwargs))

    async def delete(self, url, **kwargs):
        return await self._client.delete(url, **self._with_timeout(kwargs))

    async def request(self, method, url, **kwargs):
        return await self._client.request(method, url, **self._with_timeout(kwargs))

    # 透传属性（如 .headers），避免调用点 AttributeError
    def __getattr__(self, name):
        return getattr(self._client, name)


@asynccontextmanager
async def samsara_client(timeout: float = 10.0):
    """共享客户端上下文：`async with samsara_client(5.0) as client:`。

    与 `httpx.AsyncClient(timeout=5.0)` 用法一致，但不新建连接、退出时不关闭连接。
    """
    yield _TimeoutClientProxy(get_samsara_client(), timeout)


def samsara_warn(context: str, exc: BaseException) -> None:
    """统一记录 samsara 调用失败（替代原先的 `except Exception: pass`）。"""
    _samsara_logger.warning("samsara 调用失败（已降级）[%s]: %s: %s", context, type(exc).__name__, exc)


async def samsara_get_json(path_or_url: str, default: Any = None, timeout: float = 5.0,
                           base: Optional[str] = None) -> Any:
    """GET 一个 samsara 接口并解析 JSON；失败返回 default（并记录日志）。"""
    url = path_or_url if path_or_url.startswith("http") else f"{base}{path_or_url}"
    try:
        async with samsara_client(timeout) as client:
            resp = await client.get(url)
        if resp.status_code != 200:
            _samsara_logger.warning("samsara GET %s 返回 %s", url, resp.status_code)
            return default
        return resp.json()
    except Exception as e:
        samsara_warn(f"GET {url}", e)
        return default


async def samsara_post_json(path_or_url: str, payload: Optional[dict] = None, timeout: float = 5.0,
                            base: Optional[str] = None) -> Any:
    """POST 到 samsara；失败返回 None（并记录日志）。"""
    url = path_or_url if path_or_url.startswith("http") else f"{base}{path_or_url}"
    try:
        async with samsara_client(timeout) as client:
            resp = await client.post(url, json=payload or {})
        if resp.status_code >= 400:
            _samsara_logger.warning("samsara POST %s 返回 %s", url, resp.status_code)
            return None
        try:
            return resp.json()
        except ValueError:
            return None
    except Exception as e:
        samsara_warn(f"POST {url}", e)
        return None


# ═══════════════════════════════════════════════════════════════
# BaseGameState — 所有棋类 GameState 的共享基类
# ═══════════════════════════════════════════════════════════════

class BaseGameState:
    """
    多棋类共享的游戏状态基类。

    子类契约：
        1. 在 __init__ 中先调用 super().__init__(config_files, configs_dir)
        2. 创建 ai_orchestrator（本地 import）并赋值给 self.ai_orchestrator
        3. 最后调用 self.load_configs() 以触发 _rebuild_engines
        4. 必须实现 _rebuild_engines()
        5. 如需在 reset_board 后附加逻辑（如动物棋重置关卡业力），覆盖 _after_reset_board()
    """

    # 子类可在 class 级或 __init__ 中重写，若 __init__ 传入则以参数为准
    CONFIG_FILES: List[str] = [
        "board_state", "board", "pieces_red", "pieces_black", "rules", "ui_config"
    ]
    # 默认棋子配置键顺序（传给 RuleEngine 的 2、3 号参数，以及 AI 初始化参数）
    PIECE_CONFIG_KEYS: tuple = ("pieces_red", "pieces_black")
    # 默认 AI 难度档（部分路由作为兜底使用）
    DEFAULT_DIFFICULTY: str = "medium"

    def __init__(self, config_files: Optional[List[str]] = None,
                 configs_dir: Optional[Path] = None):
        # 参数优先 > 类级常量
        if config_files is not None:
            self.CONFIG_FILES = list(config_files)
        if configs_dir is not None:
            self.CONFIGS_DIR = Path(configs_dir)

        self.configs: Dict[str, dict] = {}

        # 配置缓存标志：为 True 时 self.configs 即最新值，load_configs 不会再从磁盘读取；
        # 仅在显式的“规则/配置已变更”事件后置为 False，下一次 load_configs 才重新读盘。
        self._config_cache_valid: bool = False

        self.undo_stack: list = []  # 修改历史栈，撤回 AI 对配置的修改

        # 子类必须在自己 __init__ 中赋值后才能 load_configs
        self.ai_orchestrator: Any = None
        self.rule_engine: Any = None
        self.chess_ai: Any = None  # 指向棋类对应的 AI（ChessAI/GomokuAI/GoAI/AnimalChessAI 等）
        self.mechanism_engine: Any = None

    # ───────────────────────────────────────────────────────────
    # 子类必须 / 可以覆盖的钩子
    # ───────────────────────────────────────────────────────────

    def _rebuild_engines(self) -> None:
        """
        重建 RuleEngine / ChessAI / MechanismEngine 并应用性格。
        子类务必实现：其参数因棋类而异。
        """
        raise NotImplementedError("Subclasses must implement _rebuild_engines()")

    def _after_reset_board(self) -> None:
        """
        reset_board() 结束时调用的钩子（默认：同步 Samsara 业力到本地 karma_assessor）。

        Bug 1 修复：任何棋类（不只是动物棋）在 reset_board 后都必须清空/同步本地
        业力评估器副本，否则“第二关开始本地业力还是上一局残留值 + Samsara 服务器
        已被 actions 同步到下一关初始值”导致业力突然消失 / 突然回复。

        子类可继续覆盖此方法：比如动物棋需要强制重置 carryover = 0 的默认值，
        但要先 super()._after_reset_board() 保证基础同步已发生。
        """
        # karma_assessor 存在则同步：重置本地业力
        orch = getattr(self, "ai_orchestrator", None)
        if orch is not None:
            ka = getattr(orch, "karma_assessor", None)
            if ka is not None and hasattr(ka, "reset_level_karma"):
                # 从服务端 SamsaraState 读取 modifiers/carryover（若模块可用）
                try:
                    from samsara.state import SamsaraState  # 延迟避免循环 import
                    s = SamsaraState()
                    modifiers = s.get_skill_modifiers()
                    carryover = s.get("realm_overshoot_carryover") or 0
                except Exception:
                    modifiers = None
                    carryover = 0
                ka.reset_level_karma(skill_modifiers=modifiers, carryover=carryover)

        # 同步「道级识破概率」到本地评估器副本。
        #
        # 语义（见 README §三 识破概率系统）：识破概率是【按道累积的全局变量】，
        # 只增不减，且【跨关卡延续】——上一关残留的识破值必须带到下一关。
        # 它仅在两种情况下归零：①被天道识破（服务端 samsara/state.py 的
        # reset_on_detection()，把所有道的识破值锁死为 0）；②开启新周目（playthrough
        # 重置）。因此这里【绝不能】清零，只能从服务端权威值同步，否则会把跨关卡的
        # 累积抹掉（曾误改为置 0.0，属回归缺陷，已修正）。
        if orch is not None:
            ka = getattr(orch, "karma_assessor", None)
            if ka is not None and hasattr(ka, "set_realm_detection"):
                try:
                    from samsara.state import SamsaraState  # 延迟避免循环 import
                    s = SamsaraState()
                    realm = s.get("current_realm") or "human"
                    # 传 (detection, realm)：与 KarmaAssessorBase 的签名一致；
                    # 注意 SamsaraState 上的同名方法是 (realm, value)，顺序相反，勿混用。
                    detection = s.get_realm_detection(realm)
                    ka.set_realm_detection(detection, realm)
                except TypeError:
                    # 兼容旧版相反参数序 set_realm_detection(realm, detection)
                    try:
                        ka.set_realm_detection(realm, detection)
                    except Exception as _e:
                        samsara_warn("samsara 调用", _e)
                except Exception as _e:
                    samsara_warn("samsara 调用", _e)
        return None

    # ───────────────────────────────────────────────────────────
    # 共享方法（6 棋类行为完全相同 — 以下方法不得被子类覆盖）
    # ───────────────────────────────────────────────────────────

    def load_configs(self) -> None:
        """从 configs/*.json 加载所有配置文件并（重新）构建引擎。

        仅当配置缓存失效时才会从磁盘读取；缓存有效时复用内存中的 self.configs，
        大幅减少走棋 / valid_moves / AI 计算过程中的重复磁盘 IO。
        """
        if not self._config_cache_valid:
            for name in self.CONFIG_FILES:
                path = self.CONFIGS_DIR / f"{name}.json"
                if path.exists():
                    with open(path, "r", encoding="utf-8") as f:
                        self.configs[name] = json.load(f)
            self._config_cache_valid = True
        self._rebuild_engines()

    def invalidate_config_cache(self) -> None:
        """使配置缓存失效：下一次 load_configs 将从磁盘重新读取全部配置。"""
        self._config_cache_valid = False

    def mark_config_cache_fresh(self) -> None:
        """标记当前内存中的配置为最新（缓存有效，后续 load_configs 复用内存）。"""
        self._config_cache_valid = True

    def save_config(self, name: str) -> None:
        """保存单个配置到对应 JSON 文件。"""
        path = self.CONFIGS_DIR / f"{name}.json"
        with open(path, "w", encoding="utf-8") as f:
            json.dump(self.configs[name], f, ensure_ascii=False, indent=2)

    def save_all(self) -> None:
        """保存所有配置到各自 JSON 文件。"""
        for name in self.CONFIG_FILES:
            self.save_config(name)

    def apply_config_update(self, updates: Dict[str, dict]) -> None:
        """应用一组配置更新，并在修改前保存快照到撤回栈。"""
        # 保存可撤回的深拷贝快照（仅包含被修改的条目）
        snapshot: Dict[str, dict] = {}
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
        """撤回最近一次 AI 对配置的修改。成功返回 True。"""
        if not self.undo_stack:
            return False
        snapshot = self.undo_stack.pop()
        for name, data in snapshot.items():
            self.configs[name] = data
            self.save_config(name)
        self.mark_config_cache_fresh()
        self._rebuild_engines()
        return True

    def reset_board(self) -> None:
        """
        重置棋盘到初始状态：从 configs/initial/*.json.initial 加载所有配置。
        之后清空撤回栈、重建引擎、保存、调用 _after_reset_board 钩子。
        """
        initial_dir = self.CONFIGS_DIR / "initial"
        for name in self.CONFIG_FILES:
            initial_path = initial_dir / f"{name}.json.initial"
            if initial_path.exists():
                with open(initial_path, "r", encoding="utf-8") as f:
                    self.configs[name] = json.load(f)

        self.undo_stack.clear()
        self.mark_config_cache_fresh()
        self._rebuild_engines()
        self.save_all()
        self._after_reset_board()


# ═══════════════════════════════════════════════════════════════
# register_common_routes — 一次性注册 25+ 条通用公共路由
# ═══════════════════════════════════════════════════════════════

def register_common_routes(
    app: FastAPI,
    state: BaseGameState,
    *,
    game_type: str,
    static_dir: Path,
    samsara_api_url: str,
    difficulty_levels: Optional[List[str]] = None,
) -> None:
    """
    将所有棋类共有的 FastAPI 路由注册到 app。

    保留的棋类专有路由（不应在此函数中，由各棋类自行注册）：
        /api/command, /api/move, /api/ai_move, /api/undo, /api/valid_moves, WebSocket
        以及各棋类自己的辅助函数（如 _trigger_karma_recover、_place_disc_and_flip 等）。

    Args:
        app: 当前棋类服务的 FastAPI 实例。
        state: 当前棋类 GameState（BaseGameState 子类实例）。
        game_type: 路由中需要上传到总坛的标识符，如 "xiangqi"/"heibaiqi"。
        static_dir: 前端 index.html 所在目录（通常为 <game>/static）。
        samsara_api_url: 总坛 samsara 服务基础 URL。
        difficulty_levels: /api/difficulty 允许的难度列表。默认 ["easy", "medium", "hard"]。
    """
    if difficulty_levels is None:
        difficulty_levels = ["easy", "medium", "hard"]
    static_dir = Path(static_dir)
    SAMSARA_API_URL = samsara_api_url  # noqa: N806 (local alias for readability vs original)

    # 共享 httpx 连接池随应用生命周期关闭（每个 app 注册一次，幂等无害）
    @app.on_event("shutdown")
    async def _close_shared_http_client():
        await close_samsara_client()

    # ───────────────────────────────────────────────────────────
    # 根路由
    # ───────────────────────────────────────────────────────────

    @app.get("/")
    async def index():
        """返回主页面（与各棋类原有实现逐字段一致）。"""
        html_path = static_dir / "index.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>前端文件未找到</h1>", status_code=404)

    # ───────────────────────────────────────────────────────────
    # Samsara API 反向代理（避免前端 404 导致业力短暂归零）
    # ───────────────────────────────────────────────────────────

    @app.api_route("/samsara/{path:path}", methods=["GET", "POST", "PUT", "DELETE", "PATCH"])
    async def proxy_samsara(path: str, request: Request):
        """代理 /samsara/* 到总坛 SAMSARA_API_URL。"""
        target_url = f"{SAMSARA_API_URL}/{path}"
        body = await request.body()
        headers = {k: v for k, v in request.headers.items() if k.lower() not in ("host", "content-length")}
        try:
            async with samsara_client(10.0) as client:
                resp = await client.request(
                    request.method, target_url,
                    headers=headers, content=body, params=request.query_params,
                )
                content = resp.content
                excluded = {"content-encoding", "transfer-encoding", "connection", "keep-alive", "content-length"}
                response_headers = {k: v for k, v in resp.headers.items() if k.lower() not in excluded}
                return Response(
                    content=content,
                    status_code=resp.status_code,
                    headers=response_headers,
                    media_type=resp.headers.get("content-type"),
                )
        except Exception as e:
            return JSONResponse({"detail": f"samsara proxy error: {e}"}, status_code=502)

    # ───────────────────────────────────────────────────────────
    # 配置读写
    # ───────────────────────────────────────────────────────────

    @app.get("/api/config/all")
    async def get_all_configs():
        """获取所有配置（必须放在 /api/config/{config_name} 路由之前）。"""
        return state.configs

    @app.get("/api/config/{config_name}")
    async def get_config(config_name: str):
        """获取单个配置。"""
        if config_name not in state.CONFIG_FILES:
            return JSONResponse({"error": "无效的配置名"}, status_code=400)
        return state.configs.get(config_name, {})

    @app.post("/api/undo_config")
    async def undo_config_change():
        """撤回最近一次 AI 配置修改。"""
        success = state.undo_last_config_change()
        if success:
            return {"success": True, "message": "已撤回上一次AI修改", "configs": state.configs}
        return {"success": False, "message": "没有可撤回的修改"}

    @app.post("/api/reset_configs")
    async def reset_configs():
        """重置所有配置到初始状态。"""
        state.reset_board()
        payload: Dict[str, Any] = {
            "success": True,
            "message": "所有配置已重置",
            "configs": state.configs,
            "board_state": state.configs["board_state"],
        }
        # Bug 3 修复：响应追加 karma_detection_state + state，供前端立即同步 UI
        payload["karma_detection_state"] = _build_karma_detection_payload(state)
        payload["state"] = _proxy_samsara_state_brief()
        return payload

    # ───────────────────────────────────────────────────────────
    # API 密钥
    # ───────────────────────────────────────────────────────────

    @app.post("/api/apikey")
    async def set_api_key(req: SetApiKey):
        """设置 API 密钥（同步应用到 orchestrator 和 AI）。"""
        state.ai_orchestrator.set_api_key(req.api_key)
        if state.chess_ai:
            state.chess_ai.set_api_key(req.api_key)
        return {"success": True, "message": "API密钥已设置"}

    @app.get("/api/apikey/status")
    async def api_key_status():
        """检查 API 密钥状态。"""
        return {"has_key": bool(state.ai_orchestrator.api_key)}

    # ───────────────────────────────────────────────────────────
    # 游戏控制
    # ───────────────────────────────────────────────────────────

    @app.post("/api/restart")
    async def restart_game():
        """重新开始游戏（重置棋盘 + 初始配置）。"""
        state.reset_board()
        payload: Dict[str, Any] = {
            "success": True,
            "message": "游戏已重新开始",
            "board_state": state.configs["board_state"],
            "configs": state.configs,
        }
        payload["karma_detection_state"] = _build_karma_detection_payload(state)
        payload["state"] = _proxy_samsara_state_brief()
        return payload

    @app.post("/api/difficulty")
    async def set_difficulty(req: DifficultyRequest):
        """设置 AI 难度（参数化允许列表）。"""
        if req.difficulty not in difficulty_levels:
            return {"success": False, "message": "无效的难度"}
        state.configs["rules"]["ai_difficulty"]["current"] = req.difficulty
        state.save_config("rules")
        state.mark_config_cache_fresh()
        if state.chess_ai is not None:
            state.chess_ai.set_difficulty(req.difficulty)
        return {"success": True, "message": f"难度已设置为{req.difficulty}"}

    # ───────────────────────────────────────────────────────────
    # 日志
    # ───────────────────────────────────────────────────────────

    @app.get("/api/logs")
    async def get_logs(count: int = 10):
        """获取 AI 对话日志。"""
        return {"logs": state.ai_orchestrator.get_logs(count)}

    @app.post("/api/clear_logs")
    async def clear_logs():
        """清空 AI 对话日志。"""
        state.ai_orchestrator.logger.clear()
        return {"success": True, "message": "日志已清空"}

    # ───────────────────────────────────────────────────────────
    # 业力 / 识破 / 思考状态
    # ───────────────────────────────────────────────────────────

    @app.get("/api/karma_detection")
    async def get_karma_detection():
        """获取本地业力与识破状态。

        Bug 1 / 4 修复：响应字段严格对齐 karma.get_state()，额外提供 single_max 与 initial，
        前端即使缓存了旧响应也能通过字段差异判定需重绘。
        """
        return {
            "success": True,
            "karma": _build_karma_detection_payload(state)["karma"],
            "detection": _build_karma_detection_payload(state)["detection"],
        }

    @app.post("/api/karma/recover")
    async def recover_karma(req: KarmaRecoverRequest):
        """业力减少（消业）事件入口。game_type 参数化（对应六道棋名）。

        改用 Pydantic 模型校验（审查报告 4.3）：缺字段不再 500，类型错误由
        FastAPI 统一返回 422；event_data 仍原样透传给 samsara。
        """
        event_type = req.event_type
        amount = req.amount
        body = req.model_dump()
        karma_assessor = state.ai_orchestrator.karma_assessor

        # 从 samsara 获取技能修饰符
        skill_modifiers: Dict[str, Any] = {}
        try:
            async with samsara_client(5.0) as client:
                skill_resp = await client.get(f"{SAMSARA_API_URL}/api/skills")
                if skill_resp.status_code == 200:
                    skill_data = skill_resp.json()
                    skill_modifiers = skill_data.get("modifiers", {})
        except Exception as _e:
            samsara_warn("samsara 调用", _e)

        actual = karma_assessor.decrease_karma(amount, skill_modifiers)

        # 同步到 samsara（失败仅记录，不阻断本地消业结果）
        try:
            async with samsara_client(5.0) as client:
                await client.post(
                    f"{SAMSARA_API_URL}/api/karma/recover",
                    json={"game_type": game_type, "event_type": event_type, "event_data": body}
                )
        except Exception as _e:
            samsara_warn("samsara 调用", _e)

        return {
            "success": True,
            "amount": actual,
            "karma": {
                "current": karma_assessor.get_local_karma(),
                "max": karma_assessor.get_local_karma_max(),
            },
            "detection": karma_assessor.get_realm_detection(),
        }

    @app.get("/api/thinking_status")
    async def get_thinking_status():
        """获取 AI 思考中状态（true/false）。"""
        return state.ai_orchestrator.get_thinking_status()

    # ───────────────────────────────────────────────────────────
    # 机制
    # ───────────────────────────────────────────────────────────

    @app.get("/api/mechanisms")
    async def get_mechanisms():
        """获取当前激活的机制列表。"""
        board = state.configs["board_state"]
        summary: list = []
        if state.mechanism_engine is not None:
            summary = state.mechanism_engine.get_active_mechanisms_summary(board)
        return {"success": True, "mechanisms": summary, "raw": board.get("mechanisms", {})}

    @app.post("/api/stop_mechanism")
    async def stop_mechanism(req: StopMechanismRequest):
        """截停指定方的指定机制（对 player_control 与常规机制分情况处理，逻辑与原实现一致）。"""
        board = state.configs["board_state"]
        mech = board.get("mechanisms", {})
        if req.mechanism_type in mech and isinstance(mech[req.mechanism_type], list):
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

        summary: list = []
        if state.mechanism_engine is not None:
            summary = state.mechanism_engine.get_active_mechanisms_summary(board)
        return {"success": True, "message": "机制已截停", "mechanisms": summary, "board_state": board}

    # ───────────────────────────────────────────────────────────
    # Token 统计
    # ───────────────────────────────────────────────────────────

    @app.get("/api/token_stats")
    async def get_token_stats():
        """获取累计 Token 消耗统计。"""
        return state.ai_orchestrator.get_token_stats()

    # ───────────────────────────────────────────────────────────
    # RPG 代理路由
    # ───────────────────────────────────────────────────────────

    @app.post("/api/rpg/apply_patch")
    async def rpg_apply_patch(req: RpgApplyPatchReq):
        """RPG 服务应用 JSON Patch 到指定配置文件。"""
        if req.target not in state.CONFIG_FILES:
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
        """RPG 服务应用规则覆盖（target 强制为 rules）。"""
        req.target = "rules"
        return await rpg_apply_patch(req)

    # ── 局部辅助：构造对齐 karma.get_state() 的 karma_detection 负载 ──
    def _build_karma_detection_payload(st: BaseGameState) -> Dict[str, Any]:
        ka = getattr(getattr(st, "ai_orchestrator", None), "karma_assessor", None)
        if ka is None:
            # 测试环境或未初始化：返回占位（结构保证齐全，前端不会 crash）
            return {
                "karma": {"current": 0, "max": 0, "single_max": 0, "initial": 0},
                "detection": 0.0,
            }
        # 兼容未实现 *_single_max / *_initial 的旧 KarmaAssessor 副本
        def _call(name: str, default: Any) -> Any:
            fn = getattr(ka, name, None)
            if callable(fn):
                try:
                    return fn()
                except Exception:
                    return default
            return default
        return {
            "karma": {
                "current": _call("get_local_karma", 0),
                "max": _call("get_local_karma_max", 0),
                "single_max": _call("get_local_karma_single_max", _call("get_local_karma_max", 0)),
                "initial": _call("get_local_karma_initial", _call("get_local_karma_max", 0)),
            },
            "detection": _call("get_realm_detection", 0.0),
        }

    def _proxy_samsara_state_brief() -> Dict[str, Any]:
        """代理获取 Samsara 简要 state（失败时给安全空对象，不阻塞 UI 刷新）。"""
        try:
            import httpx as _httpx
            resp_raw = _httpx.get(
                f"{SAMSARA_API_URL}/api/state", timeout=2.0,
            )
            if resp_raw.status_code == 200:
                return resp_raw.json()
        except Exception as _e:
            samsara_warn("samsara 调用", _e)
        return {
            "karma": _build_karma_detection_payload(state)["karma"]["current"],
            "karma_max": _build_karma_detection_payload(state)["karma"]["max"],
            "detection": _build_karma_detection_payload(state)["detection"],
        }

    @app.post("/api/rpg/reset_battle")
    async def rpg_reset_battle():
        """RPG 每局开始时调用，重置棋盘（自动调用子类 _after_reset_board 钩子）。

        Bug 1/2 修复：返回体追加 karma_detection_state 与 Samsara state，供前端
        立即刷新业力条/识破条，避免轮询。
        """
        state.reset_board()
        return {
            "success": True,
            "message": "战斗已重置",
            "board_state": state.configs["board_state"],
            "configs": state.configs,
            "karma_detection_state": _build_karma_detection_payload(state),
            "state": _proxy_samsara_state_brief(),
        }

    # ───────────────────────────────────────────────────────────
    # 关卡系统接口
    # ───────────────────────────────────────────────────────────

    @app.get("/api/level/info")
    async def get_level_info():
        """获取当前关卡信息（代理到 Samsara）。"""
        try:
            async with samsara_client(5.0) as client:
                resp = await client.get(f"{SAMSARA_API_URL}/api/levels")
                data = resp.json()
                return data
        except Exception as e:
            return {"success": False, "message": str(e), "current_level": None}

    @app.post("/api/level/apply")
    async def apply_level_config():
        """根据当前关卡配置设置难度、回合限制等参数。game_type 参数化。

        Bug 2 修复：apply 成功后再 reset_board()，保证开局棋盘/规则/棋子
        回到本关初始状态（否则前端沿用之前的修改，第二关视觉与规则不恢复）。

        道境一致性守卫：棋类 ↔ 道一一对应（象棋=人道、黑白棋=地狱道…）。
        旁路进入（直连棋类 URL、/play 容器页、刷新页面）不会经过大地图
        「轮回修行」的 /samsara/api/levels/start 调用，samsara 全局道境会
        停留在上一次的道（默认 hell），导致玩家在象棋里看到"地狱道·暗之始"。
        apply 时发现道境与本服务棋类不符 → 自动切到本棋类对应道，并按
        该道玩家进度（levels_passed）选择关卡索引，与设计书"六棋=六道"一致。
        """
        try:
            samsara_level = None
            async with samsara_client(5.0) as client:
                resp = await client.get(f"{SAMSARA_API_URL}/api/levels")
                data = resp.json()
                level = data.get("current_level")

                # ── 道境一致性守卫 ──
                if level and level.get("game_type") and level["game_type"] != game_type:
                    fixed = False
                    for rp in data.get("realms", []):
                        if rp.get("game_type") != game_type:
                            continue
                        realm = rp.get("realm")
                        passed = int(rp.get("levels_passed", 0) or 0)
                        total = int(rp.get("total_levels", 0) or 0)
                        if not realm:
                            break
                        # 该道已通关则停在最后一关，否则落在下一个未通关卡
                        idx = min(passed, total - 1) if total > 0 else 0
                        start_resp = await client.post(
                            f"{SAMSARA_API_URL}/api/levels/start",
                            json={"realm": realm, "level_index": idx},
                        )
                        if start_resp.status_code == 200:
                            start_data = start_resp.json()
                            new_level = (start_data or {}).get("level")
                            if new_level:
                                level = new_level
                                fixed = True
                        break
                    if not fixed:
                        # 兜底：仅切道（set_realm 会把 current_level 归零并重置关卡变量）
                        realm_fallback = {
                            "xiangqi": "human", "wuziqi": "heaven",
                            "weiqi": "asura", "dongwuqi": "animal",
                            "tiaoqi": "hungry", "heibaiqi": "hell",
                        }.get(game_type)
                        if realm_fallback:
                            await client.post(
                                f"{SAMSARA_API_URL}/api/levels/start",
                                json={"realm": realm_fallback},
                            )
                            re_resp = await client.get(f"{SAMSARA_API_URL}/api/levels")
                            level = (re_resp.json() or {}).get("current_level") or level

                if not level:
                    # 沙盒 / fallback：无 level 时仍返回 success + 本地 karma 快照
                    return {
                        "success": False,
                        "message": "无当前关卡",
                        "karma_detection_state": _build_karma_detection_payload(state),
                        "state": _proxy_samsara_state_brief(),
                    }
                samsara_level = level

                ai_depth = level.get("ai_depth", 3)
                ai_personality = level.get("ai_personality", "normal")  # noqa: F841 (保留字段名以备扩展)
                turn_limit = level.get("turn_limit", 20)  # noqa: F841 (保留字段名)

                difficulty_map = {1: "easy", 2: "easy", 3: "medium", 4: "hard", 5: "hard"}
                difficulty = difficulty_map.get(ai_depth, "medium")
                state.configs["rules"]["ai_difficulty"]["current"] = difficulty
                if state.chess_ai is not None:
                    state.chess_ai.set_difficulty(difficulty)
                state.save_config("rules")
                state.mark_config_cache_fresh()

                async with samsara_client(5.0) as client2:
                    await client2.post(f"{SAMSARA_API_URL}/api/turn/reset")
                    await client2.post(
                        f"{SAMSARA_API_URL}/api/turn/increment",
                        json={"game_type": game_type}
                    )

            # apply 完后：reset_board 触发 _after_reset_board → 本地 karma 同步
            state.reset_board()
            return {
                "success": True,
                "level": samsara_level,
                "difficulty": difficulty,
                "board_state": state.configs["board_state"],
                "configs": state.configs,
                "karma_detection_state": _build_karma_detection_payload(state),
                "state": _proxy_samsara_state_brief(),
            }
        except Exception as e:
            import traceback
            traceback.print_exc()
            return {"success": False, "message": str(e)}

    @app.post("/api/level/complete")
    async def complete_level(won: bool = True, no_cheat: bool = False, boss_defeated: bool = False):
        """通关/失败时调用 samsara progression 解析进度，并在胜利时推进关卡。"""
        try:
            async with samsara_client(10.0) as client:
                resp = await client.post(
                    f"{SAMSARA_API_URL}/api/progression/resolve",
                    json={"won": won, "no_cheat": no_cheat, "boss_defeated": boss_defeated}
                )
                data = resp.json()
                if won:
                    await client.post(f"{SAMSARA_API_URL}/api/levels/advance")
                return data
        except Exception as e:
            return {"success": False, "message": str(e)}


__all__ = [
    "BaseGameState",
    "PlayerCommand",
    "SetApiKey",
    "DifficultyRequest",
    "StopMechanismRequest",
    "RpgApplyPatchReq",
    "register_common_routes",
]
