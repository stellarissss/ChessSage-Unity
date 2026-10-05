#!/usr/bin/env python3
"""
棋圣 (ChessSage) — 六道众生统一启动器
一键拉起六种 AI 作弊棋类服务（剧情模式）+ 六种沙盒棋类（共 12 个子服务），
并在本地启动“六道众生”总坛界面。
"""
import os
import sys
import json
import subprocess
import time
import threading
import webbrowser
from datetime import datetime
from pathlib import Path

# ═══════════════════════════════════════════════════════════════
# 彩色日志
# ═══════════════════════════════════════════════════════════════

_ANSI = {
    "reset":  "\033[0m",
    "green":  "\033[32m",
    "yellow": "\033[33m",
    "red":    "\033[31m",
    "cyan":   "\033[36m",
    "dim":    "\033[2m",
}
_NO_COLOR = not sys.stdout.isatty()


def _c(color, text):
    """为文本着色，非 TTY 环境自动降级为纯文本"""
    if _NO_COLOR:
        return text
    return f"{_ANSI[color]}{text}{_ANSI['reset']}"


def _classify_line(line: str) -> str:
    """根据日志内容判定颜色等级：green / yellow / red"""
    upper = line.upper()
    # 红色：错误
    for kw in ("ERROR", "CRITICAL", "TRACEBACK", "EXCEPTION",
               "FATAL", "FAILED", "✗"):
        if kw in upper:
            return "red"
    # 黄色：警告
    for kw in ("WARNING", "WARN", "DEPRECATED", "405", "404",
               "SKIP", "跳过"):
        if kw in upper:
            return "yellow"
    # 绿色：正常运行
    return "green"


def log_info(msg):
    """输出绿色信息"""
    print(_c("green", msg))


def log_warn(msg):
    """输出黄色警告"""
    print(_c("yellow", msg))


def log_error(msg):
    """输出红色错误"""
    print(_c("red", msg))

# ═══════════════════════════════════════════════════════════════
# 冻结态识别与「可执行文件路径」
# ═══════════════════════════════════════════════════════════════
# 判定顺序（任一命中即为冻结态）：
#   1. CHESSSAGE_FROZEN 环境变量 —— 由打包脚本/子进程启动显式置位，最可靠；
#   2. sys.frozen            —— PyInstaller / cx_Freeze；
#   3. __compiled__          —— Nuitka（注意需用 globals() 运行时取值）。
_FROZEN_ROOT = (
    os.environ.get("CHESSSAGE_FROZEN") == "1"
    or bool(getattr(sys, "frozen", False))
    or ("__compiled__" in globals())
)


def _exe_path() -> str:
    """冻结产物自身的可执行文件路径（用于以「解释器 + 脚本」语义拉起子进程）。

    Nuitka standalone 下 sys.executable 指向本 exe；PyInstaller onedir 同样。
    但某些打包/启动方式（如被其他 exe 用 CreateProcess 拉起、或在 IDE 内调试）
    会让 sys.executable 回落到真实 Python 解释器，因此这里以 sys.argv[0] 兜底。
    """
    candidates = [sys.executable, sys.argv[0] if sys.argv else ""]
    for c in candidates:
        if c and os.path.isfile(c):
            return os.path.abspath(c)
    return os.path.abspath(sys.executable)


if _FROZEN_ROOT:
    # 冻结（Nuitka/PyInstaller）产物：以可执行文件所在目录为项目根
    WORKSPACE_ROOT = Path(_exe_path()).resolve().parent
else:
    WORKSPACE_ROOT = Path(__file__).resolve().parent
HUB_DIR = WORKSPACE_ROOT / "hub"
SHARED_DIR = WORKSPACE_ROOT / "shared"
ACHIEVEMENTS_FILE = WORKSPACE_ROOT / "achievements.json"
HUB_PORT = int(os.environ.get("HUB_PORT", 8080))


def _run_as_child_script(argv: list) -> bool:
    """冻结态子进程分支：argv[1] 是某个棋类服务的 main.py 路径时，就地执行它。

    冻结后 sys.executable 指向本程序，父进程以 `[exe, <棋类>/main.py]` 拉起子进程，
    期望得到与开发态 `python <棋类>/main.py` 完全一致的效果。Nuitka 把本文件编译成
    唯一入口，因此该分支必须内联在这里（外置 bootstrap.py 不会被编译进去）。
    返回 True 表示已按子进程语义执行完毕（调用方应直接退出）。
    """
    if len(argv) <= 1:
        return False
    target = argv[1]
    if not (target.endswith(".py") and os.path.isfile(target)):
        return False

    import runpy

    target = os.path.abspath(target)
    script_dir = os.path.dirname(target)
    # sys.path 顺序：脚本目录优先（保证同名模块 chess_ai/rule_engine 取本棋类版本），
    # 其次是项目根与进程 cwd，最后追加 exe 目录兜底。
    for p in (script_dir, str(WORKSPACE_ROOT), os.getcwd(), os.path.dirname(_exe_path())):
        if p and p not in sys.path:
            sys.path.insert(0, p)
    try:
        runpy.run_path(target, run_name="__main__")
    except SystemExit:
        raise
    except BaseException:
        # 子进程模式下错误必须可见（父进程会实时转发 stdout/stderr）
        import traceback
        print(f"[子进程] 加载失败: {target}", file=sys.stderr)
        traceback.print_exc()
        raise
    return True

# ═══════════════════════════════════════════════════════════════
# 成就定义
# ═══════════════════════════════════════════════════════════════

ACHIEVEMENT_DEFINITIONS = [
    # 棋盘操控类
    {"id": "ambush", "name": "十面埋伏", "desc": "象棋中你的棋子数量≥20", "icon": "♟", "category": "board", "rarity": "rare", "games": ["xiangqi"]},
    {"id": "sea_of_pieces", "name": "人海战术", "desc": "棋盘上总棋子数≥40", "icon": "👥", "category": "board", "rarity": "rare", "games": ["all"]},
    {"id": "last_man_standing", "name": "孤勇者", "desc": "你只剩1个棋子且游戏未结束", "icon": "🦸", "category": "board", "rarity": "common", "games": ["all"]},
    {"id": "palette", "name": "调色板", "desc": "让棋盘变色", "icon": "🎨", "category": "board", "rarity": "common", "games": ["all"]},
    {"id": "reality_stone", "name": "现实宝石", "desc": "改变棋盘线条/框架", "icon": "💎", "category": "board", "rarity": "common", "games": ["all"]},
    {"id": "bigger_picture", "name": "格局打开", "desc": "修改棋盘尺寸", "icon": "📐", "category": "board", "rarity": "rare", "games": ["all"]},
    {"id": "lawn_party", "name": "草坪派对", "desc": "棋盘变成绿色系", "icon": "🌱", "category": "board", "rarity": "common", "games": ["all"]},
    {"id": "genshin", "name": "我超，原", "desc": "棋盘变成紫色系", "icon": "✨", "category": "board", "rarity": "common", "games": ["all"]},
    # AI 创造类
    {"id": "clone_wars", "name": "克隆战争", "desc": "创建了自定义棋子", "icon": "🧬", "category": "ai_create", "rarity": "rare", "games": ["all"]},
    {"id": "zoo", "name": "动物园", "desc": "在斗兽棋中创建新的动物棋子", "icon": "🦁", "category": "ai_create", "rarity": "rare", "games": ["dongwuqi"]},
    {"id": "alchemist", "name": "炼金术士", "desc": "AI成功创建了自定义棋子", "icon": "⚗️", "category": "ai_create", "rarity": "rare", "games": ["all"]},
    {"id": "architect", "name": "建筑师", "desc": "AI成功修改了棋盘外观", "icon": "🏗️", "category": "ai_create", "rarity": "common", "games": ["all"]},
    {"id": "lawmaker", "name": "立法者", "desc": "AI成功修改了规则", "icon": "📜", "category": "ai_create", "rarity": "common", "games": ["all"]},
    {"id": "what_a_guy", "name": "好家伙", "desc": "创建了名字超过10字的棋子", "icon": "😱", "category": "ai_create", "rarity": "rare", "games": ["all"]},
    # 规则破坏类
    {"id": "outlaw", "name": "无法无天", "desc": "激活10条以上自定义规则", "icon": "🏴", "category": "rule_break", "rarity": "legendary", "games": ["all"]},
    {"id": "rewrite_fate", "name": "改写命运", "desc": "成功修改了棋子走法", "icon": "✏️", "category": "rule_break", "rarity": "common", "games": ["all"]},
    {"id": "god_hand", "name": "上帝之手", "desc": "直接设置游戏胜负", "icon": "👁️", "category": "rule_break", "rarity": "legendary", "games": ["all"]},
    # AI 对话类
    {"id": "what_are_you_doing", "name": "你在干嘛", "desc": "和ChatAI谈论无关的事情", "icon": "🤔", "category": "chat", "rarity": "common", "games": ["all"]},
    {"id": "id_revealed", "name": "报身份证号", "desc": "AI拒绝了你的指令", "icon": "🪪", "category": "chat", "rarity": "common", "games": ["all"]},
    {"id": "chatterbox", "name": "话痨", "desc": "累计发送50条AI指令", "icon": "💬", "category": "chat", "rarity": "rare", "games": ["all"]},
    {"id": "all_in_one", "name": "这波是肉身开团", "desc": "一步触发3种以上修改类型", "icon": "🌊", "category": "chat", "rarity": "rare", "games": ["all"]},
    # 机制类
    {"id": "ceasefire", "name": "停战协议", "desc": "使用了跳过回合机制", "icon": "⏸️", "category": "mechanism", "rarity": "common", "games": ["all"]},
    {"id": "body_snatch", "name": "夺舍", "desc": "使用了AI接管机制", "icon": "🤖", "category": "mechanism", "rarity": "common", "games": ["all"]},
    {"id": "giving_up", "name": "开摆", "desc": "使用了随机走棋机制", "icon": "🎲", "category": "mechanism", "rarity": "common", "games": ["all"]},
    {"id": "clone_jutsu", "name": "分身术", "desc": "使用了额外回合机制", "icon": "⚡", "category": "mechanism", "rarity": "common", "games": ["all"]},
    {"id": "no_longer_human", "name": "我不做人了", "desc": "使用了玩家控制切换机制", "icon": "🎭", "category": "mechanism", "rarity": "rare", "games": ["all"]},
    # 游戏事件类
    {"id": "speedrun", "name": "就这？", "desc": "5步内获胜", "icon": "⚡", "category": "event", "rarity": "legendary", "games": ["all"]},
    {"id": "suffering", "name": "受苦", "desc": "连续被AI吃5子", "icon": "💀", "category": "event", "rarity": "common", "games": ["all"]},
    {"id": "got_cketched", "name": "我大意了啊", "desc": "被AI获胜", "icon": "😅", "category": "event", "rarity": "common", "games": ["all"]},
    {"id": "winner", "name": "胜利者", "desc": "赢得一场比赛", "icon": "🏆", "category": "event", "rarity": "common", "games": ["all"]},
    # 围棋特别
    {"id": "go_five", "name": "五子棋？", "desc": "在围棋中把自己方棋子连成五子", "icon": "🔗", "category": "event", "rarity": "legendary", "games": ["weiqi"]},
    # 消耗类
    {"id": "broke", "name": "你币没了", "desc": "Token消耗超过10000", "icon": "🪙", "category": "usage", "rarity": "common", "games": ["all"]},
    {"id": "money_power", "name": "钞能力", "desc": "Token消耗超过50000", "icon": "💸", "category": "usage", "rarity": "legendary", "games": ["all"]},
    # 元成就
    {"id": "samsara", "name": "六道轮回", "desc": "在所有6种棋类中各触发至少1个成就", "icon": "♻️", "category": "meta", "rarity": "legendary", "games": ["all"]},
    {"id": "collector", "name": "收藏家", "desc": "解锁10个成就", "icon": "📦", "category": "meta", "rarity": "rare", "games": ["all"]},
    {"id": "completionist", "name": "成就党", "desc": "解锁25个成就", "icon": "🎖️", "category": "meta", "rarity": "legendary", "games": ["all"]},
    {"id": "pokedex", "name": "全图鉴", "desc": "解锁所有成就", "icon": "📖", "category": "meta", "rarity": "legendary", "games": ["all"]},
]

ALL_GAME_IDS = ["xiangqi", "wuziqi", "weiqi", "dongwuqi", "tiaoqi", "heibaiqi"]
META_ACHIEVEMENT_IDS = {"samsara", "collector", "completionist", "pokedex"}


def _load_achievements():
    """读取成就存档，不存在则创建空存档"""
    if not ACHIEVEMENTS_FILE.exists():
        _save_achievements({"version": 1, "unlocked": {}, "stats": {"total_commands": 0, "total_moves": 0, "total_captures": 0, "games_played": {}}})
    try:
        return json.loads(ACHIEVEMENTS_FILE.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return {"version": 1, "unlocked": {}, "stats": {"total_commands": 0, "total_moves": 0, "total_captures": 0, "games_played": {}}}


def _save_achievements(data):
    """写入成就存档，失败前先写入 .bak 回滚备份"""
    # 先备份旧文件（如果存在）
    try:
        if ACHIEVEMENTS_FILE.exists():
            bak = ACHIEVEMENTS_FILE.with_suffix(ACHIEVEMENTS_FILE.suffix + ".bak")
            bak.write_bytes(ACHIEVEMENTS_FILE.read_bytes())
    except OSError:
        pass
    ACHIEVEMENTS_FILE.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def _check_meta_achievements(data):
    """检查并解锁元成就，返回新解锁的元成就列表"""
    newly_unlocked = []
    unlocked = data["unlocked"]
    count = len(unlocked)

    # collector: 10个
    if count >= 10 and "collector" not in unlocked:
        unlocked["collector"] = {"unlocked_at": datetime.now().isoformat(), "game": "meta", "context": f"已解锁{count}个成就"}
        newly_unlocked.append("collector")

    # completionist: 25个
    if count >= 25 and "completionist" not in unlocked:
        unlocked["completionist"] = {"unlocked_at": datetime.now().isoformat(), "game": "meta", "context": f"已解锁{count}个成就"}
        newly_unlocked.append("completionist")

    # samsara: 6种棋各至少1个
    games_with_achievements = set()
    for info in unlocked.values():
        g = info.get("game", "")
        if g in ALL_GAME_IDS:
            games_with_achievements.add(g)
    if len(games_with_achievements) >= 6 and "samsara" not in unlocked:
        unlocked["samsara"] = {"unlocked_at": datetime.now().isoformat(), "game": "meta", "context": "六道棋境均已解锁成就"}
        newly_unlocked.append("samsara")

    # pokedex: 全部成就
    non_meta_count = sum(1 for aid in unlocked if aid not in META_ACHIEVEMENT_IDS)
    total_non_meta = sum(1 for a in ACHIEVEMENT_DEFINITIONS if a["id"] not in META_ACHIEVEMENT_IDS)
    if non_meta_count >= total_non_meta and "pokedex" not in unlocked and total_non_meta > 0:
        unlocked["pokedex"] = {"unlocked_at": datetime.now().isoformat(), "game": "meta", "context": "全图鉴达成"}
        newly_unlocked.append("pokedex")

    return newly_unlocked

GAMES = [
    {"id": "xiangqi", "name": "无限制象棋", "realm": "human", "icon": "♜", "sub": "人界 · 楚河汉界",
     "description": "传统象棋骨架，AI 实时改写走法、规则与胜负。", "port": 8000},
    {"id": "wuziqi", "name": "无限制五子棋", "realm": "heaven", "icon": "⚫", "sub": "天界 · 五连登仙",
     "description": "连珠成线即可登天，规则只在你一句话之间。", "port": 8001},
    {"id": "weiqi", "name": "无限制围棋", "realm": "asura", "icon": "⚪", "sub": "阿修罗 · 混沌气局",
     "description": "十九路战场，气、劫、提子皆可被自然语言重写。", "port": 8002},
    {"id": "dongwuqi", "name": "无限制动物棋", "realm": "animal", "icon": "🐘", "sub": "畜生界 · 斗兽丛林",
     "description": "鼠可吃象，狮可跳河，让动物们突破等级与水域。", "port": 8003},
    {"id": "tiaoqi", "name": "无限制跳棋", "realm": "hungry", "icon": "⬢", "sub": "饿鬼界 · 六角星途",
     "description": "六角星盘上连跳奔袭，AI 让 hunger 无止境。", "port": 8004},
    {"id": "heibaiqi", "name": "无限制黑白棋", "realm": "hell", "icon": "☯", "sub": "地狱界 · 阴阳翻转",
     "description": "夹吃翻转的 Othello，大模型赋予地狱般的自定义规则。", "port": 8005},
]

# 沙盒模式棋类（纯净版，无业力/识破/成就/RPG 集成，与 RPG 完全隔离）
SANDBOX_GAMES = [
    {"id": "xiangqi",  "name": "纯净象棋",   "icon": "♜", "sub": "楚河汉界 · 自由对弈",
     "description": "纯净象棋，AI 改规无业力束缚。", "port": 8010},
    {"id": "wuziqi",   "name": "纯净五子棋", "icon": "⚫", "sub": "五连登仙 · 自由对弈",
     "description": "纯净五子棋，连珠成线无拘束。", "port": 8011},
    {"id": "weiqi",    "name": "纯净围棋",   "icon": "⚪", "sub": "混沌气局 · 自由对弈",
     "description": "纯净围棋，十九路自由改写。", "port": 8012},
    {"id": "dongwuqi", "name": "纯净动物棋", "icon": "🐘", "sub": "斗兽丛林 · 自由对弈",
     "description": "纯净动物棋，鼠象狮各显神通。", "port": 8013},
    {"id": "tiaoqi",   "name": "纯净跳棋",   "icon": "⬢", "sub": "六角星途 · 自由对弈",
     "description": "纯净跳棋，连跳奔袭无止境。", "port": 8014},
    {"id": "heibaiqi", "name": "纯净黑白棋", "icon": "☯", "sub": "阴阳翻转 · 自由对弈",
     "description": "纯净黑白棋，夹吃翻转自定义。", "port": 8015},
]

REALMS = {
    "hell": {"name": "地狱道", "icon": "☯", "game": "heibaiqi", "description": "黑白棋 · 阴阳翻转"},
    "hungry": {"name": "饿鬼道", "icon": "👹", "game": "tiaoqi", "description": "跳棋 · 六角星途"},
    "animal": {"name": "畜生道", "icon": "🐅", "game": "dongwuqi", "description": "动物棋 · 斗兽丛林"},
    "human": {"name": "人道", "icon": "🧠", "game": "xiangqi", "description": "象棋 · 楚河汉界"},
    "asura": {"name": "阿修罗道", "icon": "⚔️", "game": "weiqi", "description": "围棋 · 混沌气局"},
    "heaven": {"name": "天道", "icon": "☸️", "game": "wuziqi", "description": "五子棋 · 五连登仙"},
}


def print_banner():
    print(_c("cyan", "=" * 58))
    print(_c("green", "  棋圣 ChessSage · 六道众生"))
    print(_c("cyan", "  统一启动器"))
    print(_c("cyan", "=" * 58))


def check_dependencies():
    print(_c("dim", "\n[检查依赖]"))
    try:
        import fastapi  # noqa: F401
        import uvicorn  # noqa: F401
        import httpx    # noqa: F401
        import pydantic # noqa: F401
        log_info("✓ 所有依赖已安装")
        return True
    except ImportError as e:
        log_error(f"✗ 缺少依赖: {e}")
        log_warn("  请运行: pip install -r requirements.txt")
        return False


def _pipe_logger(process, name):
    """后台线程：实时读取子进程 stdout 并按内容着色打印"""
    try:
        for line in process.stdout:
            line = line.rstrip()
            if not line:
                continue
            level = _classify_line(line)
            tag = _c("dim", f"[{name}]")
            print(f"{tag} {_c(level, line)}")
    except Exception as e:
        # 子进程输出读取异常必须可见（曾被静默吞掉，导致「子进程失败却无任何日志」）
        import traceback
        print(f"{_c('dim', '[' + str(name) + ']')} {_c('red', f'日志管道异常: {e}')}", file=sys.stderr)
        traceback.print_exc()


def start_process(name, script_path, port, cwd=None):
    log_info(f"  启动 {name} (端口 {port})...")
    if cwd is None:
        cwd = script_path.parent

    env = os.environ.copy()
    env["PYTHONPATH"] = str(WORKSPACE_ROOT) + os.pathsep + env.get("PYTHONPATH", "")
    env["GAME_PORT"] = str(port)
    if _FROZEN_ROOT:
        # 显式告知子进程「我是被冻结产物拉起的」，绕开各打包器标记差异
        env["CHESSSAGE_FROZEN"] = "1"

    try:
        # 冻结态：以 [本exe, 脚本路径] 拉起，由入口的 _run_as_child_script 还原
        # “解释器 + 脚本”语义（各棋类获得独立进程与模块命名空间，避免同名模块冲突）。
        launcher = [
            _exe_path() if _FROZEN_ROOT else sys.executable,
            str(script_path),
        ]
        process = subprocess.Popen(
            launcher,
            cwd=str(cwd),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
        )
        t = threading.Thread(target=_pipe_logger, args=(process, name), daemon=True)
        t.start()
        return process
    except Exception as e:
        log_error(f"  ✗ 启动失败: {e}")
        return None


# ═══════════════════════════════════════════════════════════════
# 按需启停进程管理：不一次性拉起所有棋类服务，仅在使用时启动、空闲时回收
# ═══════════════════════════════════════════════════════════════
_LAZY = {}            # key -> {proc, name, port, cwd, last}
_LAZY_LOCK = threading.Lock()
# 兜底空闲阈值（秒）：仅用于回收异常遗留（如浏览器崩溃、pagehide 未触发）的进程。
# 主回收路径为前端「主动关闭界面/关闭网页」时调用 /api/lazy/stop（pagehide + sendBeacon）。
# 阈值取较大值，避免网页休眠/焦点移开/切标签页（心跳被节流）时误杀进行中的对局。
LAZY_IDLE_SECONDS = int(os.environ.get("LAZY_IDLE_SECONDS", 1800))
_LAZY_KEY_BY_REALM = {g["realm"]: g["id"] for g in GAMES}
_LAZY_KEY_BY_SANDBOX_PORT = {g["port"]: g["id"] for g in SANDBOX_GAMES}
_LAZY_KEY_BY_RPG_PORT = {g["port"]: g["id"] for g in GAMES}

# 冻结（PyInstaller/Nuitka）产物中以子进程方式拉起棋类：以本 exe 为解释器，
# 传 [exe, 脚本路径] 触发 main.py 入口处的子进程分支（_run_as_child_script），
# 与开发态 `python <棋类>/main.py` 等价，为各棋类提供独立进程/模块命名空间，
# 避免同目录同名模块（chess_ai / rule_engine 等）跨棋类冲突。
def _layout_key(mode: str, gid: str):
    return f"{mode}:{gid}"


# 兼容别名：懒加载端点统一用 _lazy_key(mode, gid) 生成服务布局 key
_lazy_key = _layout_key


def _port_to_key(port: str) -> str | None:
    """按服务端口解析懒加载 key（rpg/sandbox 各自端口表）。"""
    try:
        p = int(port)
    except (TypeError, ValueError):
        return None
    for mode, mapping in (("sandbox", _LAZY_KEY_BY_SANDBOX_PORT),
                          ("rpg", _LAZY_KEY_BY_RPG_PORT)):
        gid = mapping.get(p)
        if gid:
            return _layout_key(mode, gid)
    return None


def _all_game_keys():
    keys = [_lazy_key("rpg", g["id"]) for g in GAMES]
    keys += [_lazy_key("sandbox", g["id"]) for g in SANDBOX_GAMES]
    return keys


def _ensure_game(key):
    """确保某个棋类服务已启动（幂等），并刷新最近使用时间。"""
    mode, gid = key.partition(":")[0], key.split(":", 1)[1]
    if mode == "rpg":
        game = next((g for g in GAMES if g["id"] == gid), None)
        argv = WORKSPACE_ROOT / gid / "main.py"
    else:
        game = next((g for g in SANDBOX_GAMES if g["id"] == gid), None)
        argv = WORKSPACE_ROOT / "sandbox" / gid / "main.py"
    if not game:
        return {"ok": False, "error": f"unknown game {key}"}
    now = time.time()
    with _LAZY_LOCK:
        entry = _LAZY.get(key)
        if entry and not _is_dead(entry):
            entry["last"] = now
            return {"ok": True, "port": entry["port"], "url": f"http://localhost:{entry['port']}/"}
        proc = start_process(game["name"], argv, game["port"])
        _LAZY[key] = {"proc": proc, "name": game["name"], "port": game["port"],
                      "cwd": argv.parent, "last": now}
        return {"ok": proc is not None, "port": game["port"], "url": f"http://localhost:{game['port']}/"}


def _is_dead(entry):
    proc = entry.get("proc")
    return proc is None or proc.poll() is not None


def _dispose_game(key):
    """停止某个棋类服务。"""
    with _LAZY_LOCK:
        entry = _LAZY.pop(key, None)
        if not entry:
            return
        proc = entry.get("proc")
        if proc and proc.poll() is None:
            try:
                proc.terminate()
                proc.wait(timeout=5)
            except Exception:
                try: proc.kill()
                except Exception: pass
        log_info(f"  已回收空闲进程：{entry['name']}")


def _lazy_reaper():
    """后台线程：定期回收超过空闲阈值的进程。"""
    while True:
        time.sleep(15)
        now = time.time()
        stale = [k for k, e in list(_LAZY.items()) if now - e.get("last", 0) > LAZY_IDLE_SECONDS]
        for k in stale:
            _dispose_game(k)


def _dispose_all():
    """退出时回收所有按需启动的棋类进程。"""
    for key in list(_LAZY.keys()):
        _dispose_game(key)


def build_hub_app():
    """构建总界面的 FastAPI 应用"""
    from fastapi import FastAPI, Request
    from fastapi.responses import HTMLResponse, JSONResponse, Response
    from fastapi.staticfiles import StaticFiles
    from fastapi.middleware.cors import CORSMiddleware

    NO_STORE = {
        "Cache-Control": "no-store, no-cache, must-revalidate, max-age=0",
        "Pragma": "no-cache",
        "Expires": "0",
    }

    def _nc(payload):
        return JSONResponse(content=payload, headers=NO_STORE)

    app = FastAPI(title="棋圣 · 六道众生", version="1.4.1")
    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_credentials=True,
        allow_methods=["*"],
        allow_headers=["*"],
    )

    if HUB_DIR.exists():
        app.mount("/static", StaticFiles(directory=str(HUB_DIR)), name="hub_static")
    if SHARED_DIR.exists():
        app.mount("/shared", StaticFiles(directory=str(SHARED_DIR)), name="shared_static")

    @app.get("/")
    async def title_page():
        # 标题页（双模式入口：剧情模式 / 沙盒模式）
        html_path = HUB_DIR / "title.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>标题页未找到</h1>", status_code=404)

    @app.get("/hub")
    async def hub_index():
        # 总坛已下线：剧情模式唯一入口改为大地图 /overworld
        return Response(status_code=302, headers={"Location": "/overworld"})

    @app.get("/overworld")
    async def overworld_page():
        # 2.5D 六道大陆大地图（Phaser 3 · 剧情模式唯一入口）
        html_path = HUB_DIR / "overworld.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>大陆大地图文件未找到</h1>", status_code=404)

    @app.get("/api/overworld/config")
    async def overworld_config():
        # 返回坐标级大陆大地图配置（只读，禁止浏览器缓存）
        cfg_path = WORKSPACE_ROOT / "configs" / "overworld.json"
        if cfg_path.exists():
            try:
                return _nc(json.loads(cfg_path.read_text(encoding="utf-8")))
            except (json.JSONDecodeError, OSError):
                pass
        return JSONResponse({"error": "overworld.json 缺失或损坏"}, headers=NO_STORE, status_code=404)

    @app.get("/tutorial")
    async def tutorial_page():
        # 独立玩法教程页：由大地图/标题页以新标签页打开。
        # 内容与大地图完全解耦（纯静态文档），不存在浮窗覆盖 canvas 导致的卡死。
        html_path = HUB_DIR / "tutorial.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>教程页面未找到</h1>", status_code=404)

    @app.get("/sandbox")
    async def sandbox_page():
        # 沙盒总坛（纯净棋类入口，与 RPG 隔离）
        html_path = HUB_DIR / "sandbox.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>沙盒总坛未找到</h1>", status_code=404)

    @app.get("/achievements")
    async def achievements_page():
        # 成就已并入大地图 /overworld 浮窗，历史链接重定向
        return Response(status_code=302, headers={"Location": "/overworld"})

    # ── RPG 页面路由 ──
    @app.get("/dialogue")
    async def dialogue_page():
        html_path = HUB_DIR / "dialogue.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>剧情对话页未找到</h1>", status_code=404)

    @app.get("/memory-album")
    async def memory_album_page():
        html_path = HUB_DIR / "memory_album.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>记忆相册页未找到</h1>", status_code=404)

    @app.get("/ending")
    async def ending_page():
        html_path = HUB_DIR / "ending.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>结局页未找到</h1>", status_code=404)

    @app.get("/heaven-boss")
    async def heaven_boss_page():
        html_path = HUB_DIR / "heaven_boss.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>天道Boss战页未找到</h1>", status_code=404)

    @app.get("/api/games")
    async def list_games():
        return _nc([
            {
                **game,
                "url": f"http://localhost:{game['port']}/",
            }
            for game in GAMES
        ])

    @app.get("/api/sandbox/games")
    async def list_sandbox_games():
        # 沙盒模式棋类列表（纯净版，与 RPG 隔离）
        return _nc([
            {
                **game,
                "url": f"http://localhost:{game['port']}/",
            }
            for game in SANDBOX_GAMES
        ])

    # ── 按需启停棋类进程（懒加载） ──
    @app.get("/api/lazy/start")
    async def lazy_start(request: Request):
        q = request.query_params
        mode, gid = q.get("mode", "rpg"), q.get("game", "")
        if not gid:
            return _nc({"ok": False, "error": "missing game"})
        return _nc(_ensure_game(_lazy_key(mode, gid)))

    @app.post("/api/lazy/stop")
    async def lazy_stop(request: Request):
        q = request.query_params
        mode, gid = q.get("mode", "rpg"), q.get("game", "")
        port = q.get("port", "")
        if port:
            key = _port_to_key(port)
            if key:
                _dispose_game(key)
                return _nc({"ok": True, "stopped": key})
            return _nc({"ok": False, "error": "unknown port"})
        if not gid:
            return _nc({"ok": False, "error": "missing game"})
        _dispose_game(_lazy_key(mode, gid))
        return _nc({"ok": True})

    @app.get("/api/lazy/ping")
    async def lazy_ping(request: Request):
        """对局进程心跳：按端口刷新最近使用时间，避免空闲回收器在对局进行中误杀。
        只要对局界面仍打开（例如玩家切走焦点但未关闭），前端持续心跳，进程保活；
        只有玩家主动关闭界面（返回地图/回标题）才由前端调用 /api/lazy/stop 立即回收。"""
        q = request.query_params
        port = q.get("port", "")
        key = _port_to_key(port)
        if not key:
            return _nc({"ok": False, "error": "unknown port"})
        _ensure_game(key)   # 刷新 last 时间戳（幂等：未启动会拉起）
        return _nc({"ok": True, "port": int(port)})

    @app.get("/api/lazy/running")
    async def lazy_running():
        running = []
        with _LAZY_LOCK:
            for key, e in _LAZY.items():
                if not _is_dead(e):
                    mode, gid = key.split(":", 1)
                    running.append({"mode": mode, "game": gid, "port": e["port"]})
        return _nc({"running": running, "count": len(running)})

    # 进入对局容器时：确保对应棋类服务已就绪后才返回页面
    @app.get("/play")
    async def play_page(request: Request):
        q = request.query_params
        realm = q.get("realm")
        if realm and realm in _LAZY_KEY_BY_REALM:
            _ensure_game(_lazy_key("rpg", _LAZY_KEY_BY_REALM[realm]))
        html_path = HUB_DIR / "play.html"
        if html_path.exists():
            return HTMLResponse(html_path.read_text(encoding="utf-8"))
        return HTMLResponse("<h1>对局容器文件未找到</h1>", status_code=404)

    @app.get("/api/health")
    async def health():
        return _nc({"status": "ok", "hub_port": HUB_PORT})

    # ── 六道轮回 API ──
    sys.path.insert(0, str(WORKSPACE_ROOT))
    from samsara.api import app as samsara_app
    app.mount("/samsara", samsara_app)

    @app.get("/api/samsara/realms")
    async def get_realms_list():
        return _nc(REALMS)

    # ── 成就 API ──

    @app.get("/api/achievements")
    async def get_achievements():
        data = _load_achievements()
        unlocked = data.get("unlocked", {})
        achievements = []
        for adef in ACHIEVEMENT_DEFINITIONS:
            info = unlocked.get(adef["id"])
            achievements.append({
                "id": adef["id"],
                "name": adef["name"],
                "desc": adef["desc"],
                "icon": adef["icon"],
                "category": adef["category"],
                "rarity": adef["rarity"],
                "games": adef["games"],
                "unlocked": info is not None,
                "unlocked_at": info.get("unlocked_at") if info else None,
                "game": info.get("game") if info else None,
                "context": info.get("context") if info else None,
            })
        return _nc({
            "total": len(ACHIEVEMENT_DEFINITIONS),
            "unlocked_count": len(unlocked),
            "achievements": achievements,
        })

    @app.post("/api/achievements/unlock")
    async def unlock_achievement(request: Request):
        body = await request.json()
        achievement_id = body.get("achievement_id", "")
        game = body.get("game", "unknown")
        context = body.get("context", "")

        valid_ids = {a["id"] for a in ACHIEVEMENT_DEFINITIONS}
        if achievement_id not in valid_ids:
            return JSONResponse({"error": "unknown achievement"}, headers=NO_STORE, status_code=400)

        data = _load_achievements()
        unlocked = data["unlocked"]

        if achievement_id in unlocked:
            return _nc({"newly_unlocked": False, "achievement_id": achievement_id, "meta_unlocked": []})

        unlocked[achievement_id] = {
            "unlocked_at": datetime.now().isoformat(),
            "game": game,
            "context": context,
        }

        meta_unlocked = _check_meta_achievements(data)
        _save_achievements(data)
        return _nc({"newly_unlocked": True, "achievement_id": achievement_id, "meta_unlocked": meta_unlocked})

    @app.get("/api/achievements/stats")
    async def get_stats():
        data = _load_achievements()
        return _nc(data.get("stats", {}))

    @app.post("/api/achievements/stats")
    async def update_stats(request: Request):
        body = await request.json()
        data = _load_achievements()
        stats = data.setdefault("stats", {"total_commands": 0, "total_moves": 0, "total_captures": 0, "games_played": {}})

        if "field" in body and "increment" in body:
            field = body["field"]
            inc = body["increment"]
            if field in ("total_commands", "total_moves", "total_captures"):
                stats[field] = stats.get(field, 0) + inc
            elif field == "games_played":
                game_id = body.get("game_id", "unknown")
                gp = stats.setdefault("games_played", {})
                gp[game_id] = gp.get(game_id, 0) + inc
        elif "fields" in body:
            for k, v in body["fields"].items():
                stats[k] = v

        _save_achievements(data)
        return _nc({"ok": True, "stats": stats})

    @app.post("/api/achievements/reset")
    async def reset_achievements(request: Request):
        """成就重置。默认重置 stats 与 unlocked，保留成就定义不变。"""
        try:
            body = await request.json()
        except Exception:
            body = {}
        # 先备份
        if ACHIEVEMENTS_FILE.exists():
            try:
                bak = ACHIEVEMENTS_FILE.with_suffix(ACHIEVEMENTS_FILE.suffix + ".bak")
                bak.write_bytes(ACHIEVEMENTS_FILE.read_bytes())
            except OSError:
                pass
        mode = body.get("mode", "all")
        if mode == "stats_only":
            data = _load_achievements()
            data["stats"] = {"total_commands": 0, "total_moves": 0, "total_captures": 0, "games_played": {}}
            _save_achievements(data)
            return _nc({"ok": True, "mode": mode, "data": data})
        # all: 整个成就结构恢复空
        empty = {
            "version": 1,
            "unlocked": {},
            "stats": {"total_commands": 0, "total_moves": 0, "total_captures": 0, "games_played": {}},
        }
        _save_achievements(empty)
        return _nc({"ok": True, "mode": "all", "data": empty})


    return app


def start_hub_server(port):
    import uvicorn

    # 生产模式配置：绑定外部地址、稳定日志级别，服务仅此一份（桌面应用单进程）
    host = os.environ.get("HUB_HOST", "0.0.0.0")
    log_level = os.environ.get("HUB_LOG_LEVEL", "warning")
    hub_app = build_hub_app()
    log_info(f"  启动 六道众生生产服务器 (端口 {port})...")
    uvicorn.run(hub_app, host=host, port=port, log_level=log_level)


def open_browser(url, no_browser=False):
    if no_browser:
        return
    # 给总坛服务一点启动时间
    time.sleep(1.2)
    try:
        webbrowser.open(url, new=2)
        log_info(f"\n  已尝试唤起浏览器: {url}")
    except Exception as e:
        log_warn(f"\n  未能自动打开浏览器: {e}")


def _hub_ready(port: int, timeout: float = 25.0) -> bool:
    """等待总坛服务可访问（桌面窗口/浏览器打开前就绪检查）。"""
    import urllib.request

    url = f"http://127.0.0.1:{port}/api/health"
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=1) as resp:
                if resp.status == 200:
                    return True
        except Exception:
            pass
        time.sleep(0.3)
    return False


# ── 独立桌面窗口 · WebGPU 内核选择 ──────────────────────────────
# WebGPU 是页面渲染的前提（大地图走 WebGPU）。是否可用完全取决于嵌入式 Web 引擎：
#   - Chromium 系（Windows WebView2/EdgeChromium、QtWebEngine、CEF）：默认可开 WebGPU；
#   - Apple WKWebView：较新系统支持 WebGPU，缺失时由页面升级到系统浏览器；
#   - Linux WebKitGTK：*根本不支持* WebGPU，只能让位给系统浏览器。
# Chromium 系通过注入命令行标志显式开启 WebGPU，确保任何机器默认可用。

_CHROMIUM_WGPU_FLAGS = (
    "--enable-unsafe-webgpu "
    "--ignore-gpu-blocklist "
    "--enable-features=Vulkan,WebGPU,WebGPUDeveloperFeatures"
)


def _has_module(name):
    try:
        __import__(name)
        return True
    except Exception:
        return False


def _enable_webview2_webgpu():
    """Windows WebView2(EdgeChromium)：SDK 会读取环境变量 WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS，
    向其追加开启 WebGPU 的 Chromium 命令行参数。"""
    prev = os.environ.get("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "").strip()
    os.environ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = " ".join(
        [p for p in (prev, _CHROMIUM_WGPU_FLAGS) if p]
    )


def _enable_qtwebengine_webgpu():
    """QtWebEngine(Chromium)：QWebEngine 在启动前读取 QTWEBENGINE_CHROMIUM_FLAGS，
    必须在 QApplication 构造（webview.start）之前设置。"""
    prev = os.environ.get("QTWEBENGINE_CHROMIUM_FLAGS", "").strip()
    os.environ["QTWEBENGINE_CHROMIUM_FLAGS"] = " ".join(
        [p for p in (prev, _CHROMIUM_WGPU_FLAGS) if p]
    )


def _pick_webgpu_gui():
    """按平台挑选支持 WebGPU 的 pywebview GUI 后端。
    返回 (gui, 说明标签)；gui 为 None 表示本机只有不支持 WebGPU 的内核（Linux WebKitGTK），
    调用方应回退到系统浏览器以保证玩家仍能用 WebGPU。"""
    if os.name == "nt":
        _enable_webview2_webgpu()
        return "edgechromium", "WebView2(EdgeChromium/Chromium·WebGPU)"
    # Chromium 系优先：QtWebEngine > CEF
    if _has_module("PyQt6.QtWebEngineWidgets") or _has_module("PySide6.QtWebEngineWidgets"):
        _enable_qtwebengine_webgpu()
        return "qt", "QtWebEngine(Chromium·WebGPU)"
    if _has_module("cefpython3"):
        return "cef", "CEF(Chromium)"
    if sys.platform == "darwin":
        # macOS WKWebView：较新系统支持 WebGPU，缺失时由页面升级到系统浏览器
        return "webkit", "WKWebView(WebKit)"
    # Linux 仅剩 WebKitGTK：不支持 WebGPU，让位给系统浏览器
    return None, None


class _DesktopWindowBridge:
    """通过 pywebview js_api 暴露给页面：当嵌入引擎不支持 WebGPU 时，
    用系统浏览器（Chromium，必支持 WebGPU）打开同一地址以继续游玩。"""

    def __init__(self, url):
        self._url = url

    def webgpu_unavailable(self):
        """页面探测到当前窗口内核没有 WebGPU。交由系统浏览器打开同一页面。"""
        try:
            webbrowser.open(self._url, new=2)
            return True
        except Exception as e:
            print(f"[桌面窗口] 升级到系统浏览器失败: {e}")
            return False


def open_desktop_window(url: str) -> bool:
    """以支持 WebGPU 的 Chromium WebView 打开总坛。
    返回 True 表示已打开窗口（阻塞直至关闭）；返回 False 表示本机内核不支持 WebGPU，
    调用方应回退到系统浏览器模式，保证玩家仍能使用 WebGPU 渲染。

    内核选择：Windows→WebView2(EdgeChromium)、Linux/macOS→QtWebEngine(优先)/CEF。
    主线程运行 GUI 事件循环，阻塞直至窗口关闭。
    """
    import webview  # 延迟导入：仅桌面窗口模式需要

    gui, gui_label = _pick_webgpu_gui()
    if gui is None:
        log_warn("  当前环境桌面内核（WebKitGTK）不支持 WebGPU，改用系统浏览器以启用 WebGPU。")
        return False

    log_info(f"  使用 {gui_label} 内核独立桌面窗口（已开启 WebGPU）。")
    webview.create_window(
        "棋圣 ChessSage · 六道众生",
        url=url,
        width=1440,
        height=900,
        min_size=(1024, 640),
        background_color="#0a0a16",
        text_select=False,
        zoomable=True,
        easy_drag=True,
        js_api=_DesktopWindowBridge(url),
    )
    log_info(f"\n  [独立桌面窗口] 已打开: {url}  ({gui_label})")
    log_info("  " + "=" * 54)
    log_info("  关闭窗口即停止全部服务。")
    log_info("  Chromium 内核，默认已开启 WebGPU（次世代渲染）。")
    log_info("  " + "=" * 54)
    # GUI 事件循环在主线程运行，阻塞直至所有窗口关闭
    webview.start(gui=gui, private_mode=False, debug=False)
    return True


def _hold_and_monitor(processes):
    """浏览器 / 纯服务模式：保持主流程存活并按需健康告警（Ctrl+C 后返回）。"""
    _alerted_exits = {i: False for i in range(len(processes))}
    _health_tick = 0
    try:
        while True:
            time.sleep(1)
            _health_tick += 1
            # 每 30 秒轮询一次子进程存活状态（仅告警不重启，避免覆盖副作用重置棋盘状态）
            if _health_tick % 30 == 0:
                for idx, (name, proc) in enumerate(processes):
                    rc = proc.poll()
                    if rc is not None and not _alerted_exits[idx]:
                        log_error(f"  ⚠ 进程异常退出: {name} (退出码 {rc})，请检查日志或手动重启")
                        _alerted_exits[idx] = True
    except KeyboardInterrupt:
        print()


def _stop_all(processes):
    """终止全部棋类子服务。"""
    log_info("\n  正在停止所有服务...")
    for name, proc in processes:
        log_info(f"    停止 {name}...")
        try:
            proc.terminate()
            proc.wait(timeout=5)
        except Exception:
            proc.kill()
    log_info("  所有服务已停止")


def main():
    print_banner()

    no_browser = "--no-browser" in sys.argv
    production_mode = "--production" in sys.argv or os.environ.get("CHESSSAGE_PRODUCTION") == "1"
    # 默认唤起系统浏览器（localhost 安全上下文，可用 WebGPU）：
    #   --browser / --no-window 显式声明浏览器模式；无参数时同样走浏览器。
    #   仅当代码判定成功打开独立桌面窗口（--window）时才改为不唤系统浏览器。
    browser_mode = not ("--window" in sys.argv)
    if "--browser" in sys.argv or "--no-window" in sys.argv:
        browser_mode = True
    # 显式 --window 才启用独立桌面窗口（pywebview，可选）；默认一律走系统浏览器。
    # 浏览器访问 http://localhost:HUB_PORT（安全上下文）即可用 WebGPU（Chrome/Edge 桌面版），
    # 不受嵌入窗口内核（如 Linux WebKitGTK 无 WebGPU）限制，是最稳妥的 WebGPU 渲染通道。
    window_mode = "--window" in sys.argv
    # 生产模式：仅启动服务，不自动打开任何界面（供服务器/打包后后台运行）
    if production_mode:
        no_browser = True

    if not check_dependencies():
        sys.exit(1)

    # 棋类服务改为按需懒启动（进入对局时才拉起，空闲自动回收），不再一次性全开。

    # 启动按需启停进程的回收线程
    threading.Thread(target=_lazy_reaper, daemon=True).start()

    # 2. 在后台线程启动总坛服务
    hub_thread = threading.Thread(
        target=start_hub_server,
        args=(HUB_PORT,),
        daemon=True,
    )
    hub_thread.start()

    hub_url = f"http://localhost:{HUB_PORT}/"

    print(_c("cyan", "\n" + "=" * 58))
    log_info("  服务启动完成！")
    print(_c("cyan", "  " + "=" * 56))
    log_info(f"  六道众生标题页: {hub_url}")
    log_info("  棋类服务已启用【按需懒加载】：进入对局时才启动对应进程，空闲自动回收。")
    print(_c("yellow", "\n  关闭窗口 / 按 Ctrl+C 停止所有服务"))
    print(_c("cyan", "  " + "=" * 56))

    # 打开方式：
    #    默认：唤起系统浏览器（localhost 安全上下文，可用 WebGPU，栈次世代渲染）
    #    --browser / --no-window：同默认，明确以浏览器模式启动
    #    --window：改用独立桌面窗口（pywebview，可选，Chromium 内核已注入 WebGPU 标志）
    #    --no-browser：仅启动服务，不打开任何界面
    if window_mode:
        if _hub_ready(HUB_PORT):
            try:
                # 返回 False：本机桌面内核不支持 WebGPU（如 Linux WebKitGTK），
                # 自动回退到系统浏览器模式，保证玩家仍能用 WebGPU 渲染。
                opened = open_desktop_window(hub_url)  # 阻塞直至窗口关闭
            except Exception as e:
                log_warn(f"\n  pywebview 启动失败: {e}")
                log_warn("  若需独立窗口请安装: pip install pywebview")
                log_warn("  本次退回浏览器模式")
                browser_mode = True
            else:
                if not opened:
                    browser_mode = True
                else:
                    _dispose_all()
                    return
        else:
            log_warn("  总坛服务启动超时，退回浏览器模式")
            browser_mode = True

    if browser_mode:
        browser_thread = threading.Thread(
            target=open_browser,
            args=(hub_url, no_browser),
            daemon=True,
        )
        browser_thread.start()

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        _dispose_all()


if __name__ == "__main__":
    # 冻结态子进程分支：被父进程以 [exe, <棋类>/main.py] 拉起时，就地执行该脚本并退出。
    if _FROZEN_ROOT and _run_as_child_script(sys.argv):
        sys.exit(0)
    main()
