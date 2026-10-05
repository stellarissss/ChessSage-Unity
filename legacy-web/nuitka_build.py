#!/usr/bin/env python3
"""
棋圣 ChessSage · Nuitka 打包脚本（多文件 standalone onedir / 生产模式 / 无控制台）

产物（dist/棋圣/）：
    dist/棋圣/
        ├── 棋圣.exe              # 编译入口（Windows 无控制台）
        ├── python3.dll / *.pyd   # 运行时依赖（standalone 自带）
        ├── hub/ shared/ configs/ samsara/ sandbox/
        ├── xiangqi/ wuziqi/ weiqi/ dongwuqi/ tiaoqi/ heibaiqi/
        ├── config.json           # API 密钥
        ├── 启动游戏.bat / start.sh
        └── 使用说明.txt

用法：
    Windows:  build_windows.bat            （推荐，一键）
              或  .venv-build\\Scripts\\python.exe nuitka_build.py --windows --zig
    Linux:    python3 nuitka_build.py

命令行参数：
    --windows          Windows 目标（在 Windows 上默认即可，可省略）
    --zig              使用 Zig 作为 C 编译器（Nuitka 自动下载；含 MSVC 时可不加）
    --enable-cache     启用 ccache 加速二次编译（默认开启）
    --no-cache         关闭 ccache
    --clean            编译前清理旧产物与缓存
    --lto              启用 LTO 链接期优化（生产最高性能；默认开启）
    --no-lto           关闭 LTO（加快编译速度，性能略降）
    --tmp <路径>       指定编译临时目录（C 盘空间不足时用，如 D:\\nktmp）
    --output <路径>    自定义最终产物目录（默认 dist/棋圣）

设计要点（为什么这样打包）：
    1. 入口是 main.py —— 同一份 main.py 既是开发态启动器，也是冻结产物的唯一入口；
       bootstrap.py 不会被打包器编入，其「子进程分支」逻辑已内联进 main.py。
    2. 子进程模型 —— 冻结后父进程以 [本exe, <棋类>/main.py] 拉起子服务，main.py 入口
       通过 _run_as_child_script() 还原「解释器 + 脚本」语义，使每个棋类获得独立的
       进程与模块命名空间（各棋类目录下存在同名模块 chess_ai.py / rule_engine.py 等，
       若在同一进程内加载会互相污染）。
    3. 代码以「数据目录」形式随包携带（--include-data-dir）—— 棋类服务由子进程按需
       runpy 加载，Nuitka 静态分析无法追踪，故必须显式携带源码目录。
    4. webview 显式 --include-package —— pywebview 在 main.py 中是延迟导入（函数内
       import），静态分析检测不到，漏了会在 --window 模式下崩溃。
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
OUT_DIR = ROOT / "build" / "nuitka"
DEFAULT_OUTPUT = ROOT / "dist" / "棋圣"
EXE = "棋圣.exe" if os.name == "nt" else "棋圣"
ENTRY = ROOT / "main.py"

# 作为数据一并携带的游戏代码 / 前端 / 配置
DATA_DIRS = [
    "hub", "shared", "configs", "samsara", "sandbox",
    "xiangqi", "wuziqi", "weiqi", "dongwuqi", "tiaoqi", "heibaiqi",
]

# 运行期必需、但可能被静态分析漏掉的包。
#
# 【为什么必须显式列出】
# 本项目的结构特殊：main.py（唯一被 Nuitka 编译的入口）自己只用标准库，
# 真正的 Web 服务代码位于各棋类目录（xiangqi/main.py 等），它们是作为
# 「数据文件」被携带的 .py，运行期由 runpy.run_path() 就地执行。
# Nuitka 的静态分析只看得到 main.py 的 import，**看不到这些数据文件的
# import**，因此 fastapi / uvicorn / pydantic / httpx 等都不会被自动打包，
# 产物一启动就会 ModuleNotFoundError。必须在此显式声明。
#
# 注意：pywebview 无需在此列出——Nuitka 自带 pywebview 插件会自动识别并打包
# （手动再加 --include-package=webview 会与之冲突报 FATAL）。
INCLUDE_PACKAGES = [
    "fastapi",
    "uvicorn",
    "pydantic",
    "httpx",
    "jsonschema",
    "jsonpatch",
    "anyio",
    "starlette",
]

# 明确排除的无用大包（减小产物体积 / 避免误报缺失）
EXCLUDE_PACKAGES = [
    "tkinter", "matplotlib", "numpy", "pandas", "scipy",
    "PIL", "rembg", "onnxruntime", "cv2", "notebook", "IPython",
    # ziglang 是构建期依赖（wheel 内含整个 Zig 工具链，近 2 万个文件，
    # 数百 MB）。产物运行时完全不需要它，必须显式排除，否则会撑爆产物。
    "ziglang",
]


def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser(
        description="棋圣 ChessSage · Nuitka 多文件打包",
        formatter_class=argparse.RawTextHelpFormatter,
    )
    ap.add_argument("--windows", action="store_true", help="Windows 目标标记（兼容 .bat 传参）")
    ap.add_argument("--zig", action="store_true", help="使用 Zig 作为 C 编译器")
    ap.add_argument("--msvc", action="store_true", help="使用已安装的 MSVC 编译")
    ap.add_argument("--enable-cache", dest="cache", action="store_true", default=True,
                    help="启用 ccache（默认开启）")
    ap.add_argument("--no-cache", dest="cache", action="store_false", help="关闭 ccache")
    ap.add_argument("--clean", action="store_true", help="编译前清理旧产物与缓存")
    ap.add_argument("--tmp", metavar="PATH", default=None, help="编译临时目录")
    ap.add_argument("--output", metavar="PATH", default=None, help="最终产物目录")
    ap.add_argument("--console", action="store_true", help="保留控制台（调试用）")
    ap.add_argument("--lto", dest="lto", action="store_true", default=True,
                    help="启用 LTO 链接期优化（生产最高性能；默认开启）")
    ap.add_argument("--no-lto", dest="lto", action="store_false",
                    help="关闭 LTO（加快编译速度，性能略降）")
    ap.add_argument(
        "--emit-heartbeat", metavar="PATH", default=None,
        help="仅生成一个编译期心跳 .bat（显示已用时）后退出，不执行打包",
    )
    args, _unknown = ap.parse_known_args()
    return args


# 心跳脚本内容：由 Python 写出，避免在批处理里同时对抗延迟展开、
# 重定向与括号转义（那里最容易藏解析 bug）。
# 关键点：
#   · EnableDelayedExpansion 下用 !var! 而非 %var%，否则循环里取到旧值；
#   · `<nul set /p` 实现不换行的原地刷新，靠 ping 做秒级延时；
#   · 用 ping 而不是 timeout：timeout 在 stdin 被重定向时会直接报错退出。
HEARTBEAT_BAT = r"""@echo off
chcp 65001 >nul 2>nul
title ChessSage build heartbeat
setlocal EnableDelayedExpansion
echo.
echo   ==========================================
echo    Nuitka build is running. Elapsed time:
echo.
echo    Closing the MAIN window aborts the build.
echo   ==========================================
echo.
set /a S=0
:loop
set /a M=!S!/60
set /a R=!S!%%60
if !R! lss 10 ( set "RS=0!R!" ) else ( set "RS=!R!" )
<nul set /p "=[Elapsed] !M! min !RS! sec   "
ping -n 2 127.0.0.1 >nul
set /a S+=1
goto loop
"""


def emit_heartbeat(path: str) -> int:
    """写出心跳脚本（CRLF、无 BOM）。供 build_windows.bat 调用。"""
    p = Path(path)
    try:
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(HEARTBEAT_BAT.replace("\n", "\r\n").encode("utf-8"))
    except OSError as exc:
        print(f"[心跳] 生成失败：{exc}")
        return 1
    print(f"[心跳] 已生成：{p}")
    return 0


# 打包容器的排除规则：这些内容绝不能进产物（发给玩家属于垃圾/泄露）
EXCLUDE_DIRS = {
    "__pycache__", ".git", ".github", ".pytest_cache", ".mypy_cache",
    ".ruff_cache", ".venv", "venv", ".idea", ".vscode", "node_modules",
    ".DS_Store", "build", "dist", ".tox", ".eggs",
}
EXCLUDE_FILE_SUFFIXES = {
    ".pyc", ".pyo", ".pyd.orig", ".log", ".tmp", ".bak", ".orig", ".rej",
    ".swp", ".swo", ".crdownload", ".part",
}
EXCLUDE_FILE_NAMES = {
    ".DS_Store", "Thumbs.db", "desktop.ini", ".gitignore", ".gitattributes",
    "nuitka-crash-report.xml",
}
# shared/assets 下的离线美术工具脚本：仅开发者本地生成素材时使用（抠图、抽帧、
# 生成 CG 视频等），前端与游戏运行零引用，且会连带 rembg/onnxruntime 等重依赖。
# 作为独立规则排除（这些文件在 shared/assets 内，但它不在包语义里，按文件排除）。
EXCLUDE_REL_PATHS = {
    "shared/assets/cutout_all.py",
    "shared/assets/cutout_rembg.py",
    "shared/assets/generate_all.py",
    "shared/assets/generate_anim_frames.py",
    "shared/assets/regenerate_white_bg.py",
    "shared/assets/cg/generate_cg_videos.py",
}
# 素材包自带的开发/许可文件：.url 是编辑器快捷方式、.tsx 是 Tiled 工程源文件，
# 均不参与运行。（.tmx 地图与 .md 文档保守保留——可能被后续地图功能用到。）
EXCLUDE_FILE_SUFFIXES_ASSETS = {".url", ".tsx"}


def _is_excluded(p: Path) -> bool:
    """判断某路径是否属于打包时需要排除的缓存/垃圾文件。"""
    if any(part in EXCLUDE_DIRS for part in p.parts):
        return True
    if p.name in EXCLUDE_FILE_NAMES:
        return True
    if p.suffix in EXCLUDE_FILE_SUFFIXES:
        return True
    # 隐藏文件（.env / .eslintrc 之类）一般不参与运行，且可能含敏感信息
    if p.name.startswith(".") and p.is_file():
        return True
    rel = p.as_posix()
    if rel in EXCLUDE_REL_PATHS:
        return True
    # 素材包开发文件（三方素材的 License.txt 会保留）
    if p.suffix in EXCLUDE_FILE_SUFFIXES_ASSETS:
        return True
    return False


def _list_data_files(root: Path) -> list[Path]:
    """列出目录下所有应进产物的文件（已过滤缓存/垃圾），按路径排序保证命令稳定。

    仅供「产物预览 / 统计」使用；实际传给 Nuitka 的是通配参数（见 build()），
    因为逐文件列举会产生上千条参数，撑爆 Windows 命令行长度上限。
    """
    return sorted(
        p for p in root.rglob("*")
        if p.is_file() and not _is_excluded(p.relative_to(root.parent))
    )


def _count_excluded(root: Path) -> int:
    """统计被排除的文件数（用于向用户展示过滤效果）。"""
    return sum(
        1 for p in root.rglob("*")
        if p.is_file() and _is_excluded(p.relative_to(root.parent))
    )


def _collect_extensions(root: Path) -> set[str]:
    """收集目录下所有『应进产物』文件的扩展名（不含被排除者）。

    用于生成 `--include-data-files=<dir>=./=<ext>` 通配参数：
    每种扩展名一条，替代「每个文件一条」，把命令行从 20 万字符压到几百字符。
    """
    exts = set()
    for p in root.rglob("*"):
        if not p.is_file():
            continue
        if _is_excluded(p.relative_to(root.parent)):
            continue
        if p.suffix:
            exts.add(p.suffix)
    return exts


def _pkg_available(name: str) -> bool:
    """检测当前解释器是否可导入指定顶层包（不真正导入，避免副作用）。"""
    import importlib.util
    try:
        return importlib.util.find_spec(name) is not None
    except (ImportError, ValueError):
        return False


def _inject_pip_mirror() -> None:
    """把镜像源注入环境变量，供 Nuitka 内部的 pip 子进程继承。

    Nuitka 缺 Zig 时会执行 `sys.executable -m pip install ziglang`。
    该命令没有 -i 参数，默认走 PyPI —— 国内直连会长时间静默卡死
    （CPU / 磁盘 / 网络全部 0，看起来像死机）。
    pip 原生支持 PIP_INDEX_URL / PIP_TRUSTED_HOST 环境变量，
    子进程会继承，这是最干净的注入方式（无需改 Nuitka 源码或私有目录）。
    用户若已自行设置，则不覆盖。
    """
    os.environ.setdefault(
        "PIP_INDEX_URL", "https://pypi.tuna.tsinghua.edu.cn/simple"
    )
    os.environ.setdefault("PIP_TRUSTED_HOST", "pypi.tuna.tsinghua.edu.cn")
    os.environ.setdefault("PIP_DISABLE_PIP_VERSION_CHECK", "1")
    # 避免 pip 在超时后无限重试卡住整个编译
    os.environ.setdefault("PIP_TIMEOUT", "30")
    os.environ.setdefault("PIP_RETRIES", "5")


def _find_nuitka_zig() -> str | None:
    """查找 Nuitka 私有 pip space 中已就绪的 zig 可执行文件。

    仅用于给用户一个「已缓存 / 需下载」的提示，不参与命令行构造
    （Nuitka 自己会找到它，我们无法也不必覆盖）。
    """
    if os.name != "nt":
        return None
    base = Path.home() / ".cache" / "Nuitka" / "downloads" / "pip"
    if not base.is_dir():
        return None
    for pkg_dir in sorted(base.glob("private-*")):
        for cand in pkg_dir.glob("**/ziglang/zig.exe"):
            if cand.is_file():
                return str(cand)
    return None


def clean(extra_tmp: str | None = None) -> None:
    for p in (OUT_DIR, DEFAULT_OUTPUT):
        if p.exists():
            print(f"  · 清理 {p}")
            shutil.rmtree(p, ignore_errors=True)
    if extra_tmp:
        t = Path(extra_tmp)
        if t.exists():
            print(f"  · 清理临时目录 {t}")
            shutil.rmtree(t, ignore_errors=True)


def build() -> int:
    args = parse_args()

    # 旁路模式：只生成心跳脚本，不做任何打包工作。
    if args.emit_heartbeat:
        return emit_heartbeat(args.emit_heartbeat)

    print("=" * 60)
    print("  棋圣 ChessSage · Nuitka 打包（standalone onedir）")
    print("=" * 60)
    print(f"  Python : {sys.version.split()[0]}  ({sys.executable})")
    print(f"  平台   : {sys.platform}")

    if args.clean:
        print("\n[清理]")
        clean(args.tmp)

    if not ENTRY.exists():
        print(f"✗ 找不到入口文件 {ENTRY}")
        return 1

    output = Path(args.output).resolve() if args.output else DEFAULT_OUTPUT

    cmd = [
        sys.executable, "-m", "nuitka",
        "--standalone",
        f"--output-dir={OUT_DIR}",
        "--assume-yes-for-downloads",
        "--lto=" + ("yes" if args.lto else "no"),   # 默认开启 LTO：链接期优化，性能最佳；--no-lto 可关闭以加快编译
        "--remove-output",
        "--company-name=ChessSage",
        "--product-name=棋圣",
        "--file-version=3.1.0.0",
        "--product-version=3.1.0.0",
        "--file-description=棋圣·六道轮回",
    ]

    # ── 平台/编译器相关 ────────────────────────────────────────
    if os.name == "nt":
        if args.msvc:
            print("  [编译器] MSVC")
        elif args.zig or os.environ.get("CHESSSAGE_COMPILER", "zig") == "zig":
            cmd.append("--zig")
            # Zig 的获取方式（已核实 Nuitka 4.2.1 源码，勿凭印象改）：
            #   Nuitka 用 utils/PrivatePipSpace.getZigBinaryPath() 找 Zig，
            #   它【只】查自己的私有 pip space：
            #       ~/.cache/Nuitka/downloads/pip/private-<hash>/.../site-packages/ziglang/zig.exe
            #   找不到就调 `python -m pip install ziglang` 装进去。
            #   → 所以往项目 venv 里装 ziglang 是【无效】的，Nuitka 看不见。
            #   → Nuitka 也没有 --zig-binary-path 这类选项（曾误传，已删）。
            # 正确做法：让 Nuitka 自己的 pip 走镜像即可 —— pip 会读取
            # PIP_INDEX_URL 环境变量，而该子进程继承自我们。详见下方 _inject_pip_mirror()。
            _inject_pip_mirror()
            cached = _find_nuitka_zig()
            if cached:
                print(f"  [编译器] Zig（已缓存：{cached}）")
            else:
                print("  [编译器] Zig（首次将自动下载 ziglang 包，走清华镜像）")
                print("            该包含完整 Zig 工具链，约 120MB，下载时无进度属正常。")
        if not args.console:
            cmd.append("--windows-console-mode=disable")
        # 图标：仅当 assets 下存在 .ico 时才传，否则留空参数会被 Nuitka 判为非法
        ico = ROOT / "assets" / "app.ico"
        if ico.is_file():
            cmd.append(f"--windows-icon-from-ico={ico}")
    else:
        print("  [编译器] 系统 gcc/clang")

    # ── 缓存 ───────────────────────────────────────────────────
    # Nuitka 的编译缓存默认即为开启，没有 --enable-cache 这类选项，
    # 只能通过 --disable-cache=<类型> 关闭。故「开启」时什么都不加，仅关闭时传参。
    # 关全部（ccache + bytecode）：--no-cache 的语义是「本次不用任何缓存」，
    # 只关 ccache 会留下 bytecode 缓存，与用户预期不符。
    if not args.cache:
        cmd.append("--disable-cache=all")
        print("  [缓存] 已关闭编译缓存")

    # ── 临时目录 ───────────────────────────────────────────────
    # 注意：Nuitka 没有 --temp-dir 选项。控制编译临时目录的正确做法是设置
    # TMP/TEMP 环境变量，Nuitka 及其调用的 C 编译器（Zig/MSVC/gcc）都会继承。
    tmp = args.tmp or os.environ.get("CHESSSAGE_TMP")
    if tmp:
        tmp_abs = os.path.abspath(tmp)
        os.makedirs(tmp_abs, exist_ok=True)
        os.environ["TMP"] = tmp_abs
        os.environ["TEMP"] = tmp_abs
        print(f"  [临时目录] {tmp_abs}（通过 TMP/TEMP 环境变量生效）")

    # ── 数据目录 ───────────────────────────────────────────────
    # 三个必须绕开的 Nuitka 陷阱：
    #   ① --include-data-dir 会把 .py 视为「代码」而自动过滤，导致棋类服务源码
    #      一个都进不了产物（这正是旧包「完全无法使用」的根因）；
    #   ② --include-data-dir 是「整目录拷贝」，会把 __pycache__ / *.pyc 等
    #      编译缓存一并带进产物（不该分发给玩家的垃圾文件）；
    #   ③ 若改为「逐文件列举」--include-data-files，参数会多达上千条，
    #      命令行膨胀到 20 万字符，远超 Windows 上限（cmd.exe 8191 /
    #      CreateProcess 32767），Nuitka 会直接启动失败。
    #
    # 正确做法：三值通配语法（官方文档明确支持保留目录层级）
    #   --include-data-files=<扫描目录>=./=<模式>
    # 每个数据目录「每种扩展名一条」参数，命令行压到几百字符；
    # 再用 --noinclude-data-files 剔除缓存/垃圾，产物依然干净。
    #     · 目录层级完整保留（sandbox/xiangqi/main.py 不会压平覆盖）
    #     · .py 源码可正常进入产物（走数据文件通道，不走代码通道）
    excluded_hits = 0
    total_files = 0
    total_bytes = 0
    for d in DATA_DIRS:
        base = ROOT / d
        if not base.is_dir():
            print(f"  [!] 数据目录缺失，跳过：{d}")
            continue

        files = _list_data_files(base)
        excluded_hits += _count_excluded(base)
        total_files += len(files)
        for p in files:
            try:
                total_bytes += p.stat().st_size
            except OSError:
                pass

        exts = sorted(_collect_extensions(base))
        for ext in exts:
            # 三值语法 <源目录>=<目标目录>=<模式>：
            # 目标目录必须写成「同名子目录」（如 shared=shared/=...），
            # 若写成 ./=（产物根），所有文件会被压平到根目录并互相覆盖
            # （xiangqi/main.py 与 wuziqi/main.py 会挤成同一个 main.py）。
            # 源目录内文件的相对路径会保留并拼接到目标目录之后。
            cmd.append(f"--include-data-files={d}={d}/=**/*{ext}")

        py_n = sum(1 for p in files if p.suffix == ".py")
        other_n = len(files) - py_n
        print(f"  · {d}: {py_n} 个 .py 源码 + {other_n} 个资源文件"
              f"（{len(exts)} 种扩展名）")

    # 排除规则 -> 传给 Nuitka 的 --noinclude-data-files
    # 注意：该选项匹配的是「目标路径」（产物内相对路径）。使用全局通配而非
    # 逐目录列举，把 11 目录 × 14 后缀 = 154 条参数压到 14 条，避免命令行超限。
    cmd.append("--noinclude-data-files=**/__pycache__")
    cmd.append("--noinclude-data-files=**/__pycache__/*")
    for ext in sorted(EXCLUDE_FILE_SUFFIXES):
        cmd.append(f"--noinclude-data-files=**/*{ext}")
    for name in sorted(EXCLUDE_FILE_NAMES):
        cmd.append(f"--noinclude-data-files=**/{name}")
    for ext in sorted(EXCLUDE_FILE_SUFFIXES_ASSETS):
        cmd.append(f"--noinclude-data-files=**/*{ext}")
    for rel in sorted(EXCLUDE_REL_PATHS):
        cmd.append(f"--noinclude-data-files={rel}")

    if excluded_hits:
        print(f"  [i] 已排除 {excluded_hits} 个缓存/垃圾文件"
              f"（__pycache__ / *.pyc / .git / *.log 等）")
    print(f"  [i] 数据文件合计 {total_files} 个，约 {total_bytes / 1024 / 1024:.1f} MB")

    cmd.append("--include-data-files=achievements.json=achievements.json")
    if (ROOT / "config.json").exists():
        cmd.append("--include-data-files=config.json=config.json")

    # ── 显式包含 / 排除 ────────────────────────────────────────
    # INCLUDE_PACKAGES 里的包是棋类服务的运行期必需依赖（见列表处注释）。
    # 若缺失，产物能编译成功但一启动就 ModuleNotFoundError，属于「静默产出坏包」，
    # 因此这里缺失时直接报错中止，绝不能降级为告警放行。
    missing = [pkg for pkg in INCLUDE_PACKAGES if not _pkg_available(pkg)]
    if missing:
        print("")
        print("  [X] 以下运行期必需依赖未安装，无法打包：")
        for pkg in missing:
            print(f"        · {pkg}")
        print("")
        print("  请先安装后再打包：")
        print("      python -m pip install -r requirements-build.txt")
        print("")
        return 1
    for pkg in INCLUDE_PACKAGES:
        cmd.append(f"--include-package={pkg}")
    for pkg in EXCLUDE_PACKAGES:
        cmd.append(f"--nofollow-import-to={pkg}")

    # Zig 下载提示：Nuitka 用私有 pip space，装到项目 venv 里它看不见，
    # 所以无法「预装」；能做的是把镜像注入环境变量（已在上方完成），
    # 首次编译时它会自动下载并且速度正常。这里只做提醒。
    if os.name == "nt" and not args.msvc and _find_nuitka_zig() is None:
        print("")
        print("  [提示] 首次编译需要 Nuitka 下载 ziglang 包（约 120MB，含完整 Zig 工具链）。")
        print(f"         已自动指向镜像：{os.environ.get('PIP_INDEX_URL', '(默认 PyPI)')}")
        print("         下载期间只有这一行提示、无进度条，属正常现象，请耐心等待。")
        print("         若超过 10 分钟仍无反应，按 Ctrl+C 改用 MSVC：build_windows.bat --msvc")
        print("")

    cmd.append(str(ENTRY))

    print("\n[Nuitka 命令]")
    print("  " + " ".join(cmd))
    print("\n[编译中] 首次约 10-25 分钟，大量 C 编译输出属正常，请勿中断...\n")
    t0 = time.time()
    rc = subprocess.run(cmd, cwd=str(ROOT)).returncode
    if rc != 0:
        print(f"\n✗ Nuitka 构建失败（退出码 {rc}）")
        return rc

    print(f"\n[编译完成] 耗时 {time.time() - t0:.0f}s，开始整理产物...")

    # ── 定位 .dist 输出 ────────────────────────────────────────
    dist = OUT_DIR / "main.dist"
    if not dist.is_dir():
        cands = [p for p in OUT_DIR.glob("*.dist") if p.is_dir()]
        if not cands:
            print("✗ 未找到 Nuitka 输出目录（*.dist）")
            return 1
        dist = cands[0]

    # ── 重命名入口 ─────────────────────────────────────────────
    bin_path = next((p for p in dist.iterdir() if p.name.startswith("main.")), None)
    if bin_path is None:
        print("✗ 未找到编译入口可执行文件")
        return 1
    target_bin = dist / EXE
    if bin_path.resolve() != target_bin.resolve():
        try:
            bin_path.rename(target_bin)
        except OSError:
            shutil.move(str(bin_path), str(target_bin))
    print(f"  · 入口已重命名为 {EXE}")

    # ── 补齐 config.json ──────────────────────────────────────
    # 注意：绝不把本地 config.json（含真实密钥）原样打进分发包。
    # 优先用仓库内的 config.example.json 模板；再兜底生成空配置。
    cfg = dist / "config.json"
    if not cfg.exists():
        example = ROOT / "config.example.json"
        if example.exists():
            try:
                data = json.loads(example.read_text(encoding="utf-8"))
                # 剔除模板里的说明字段，并强制清空凭证，防止误带密钥。
                data.pop("_说明", None)
                data["api_key"] = ""
                cfg.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
            except Exception:
                cfg.write_text('{\n  "api_key": ""\n}\n', encoding="utf-8")
        else:
            cfg.write_text('{\n  "api_key": ""\n}\n', encoding="utf-8")

    generate_scripts(dist)

    # ── 移动到最终产物目录 ────────────────────────────────────
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        shutil.rmtree(output, ignore_errors=True)
    try:
        shutil.move(str(dist), str(output))
    except OSError:
        shutil.copytree(dist, output)
        shutil.rmtree(dist, ignore_errors=True)

    files = sum(1 for _ in output.rglob("*") if _.is_file())
    size_mb = sum(_.stat().st_size for _ in output.rglob("*") if _.is_file()) / 1024 / 1024
    print("\n" + "~" * 60)
    print("  打包完成")
    print(f"  产物目录 : {output}")
    print(f"  文件总数 : {files}")
    print(f"  占用空间 : {size_mb:.0f} MB")
    if os.name == "nt":
        print(f"  启动方式 : 双击 {output}\\启动游戏.bat")
    else:
        print(f"  启动方式 : cd {output} && ./start.sh")
    print("~" * 60)
    return 0


def generate_scripts(dist: Path) -> None:
    """在产物目录内生成一键启动脚本与说明。"""
    if os.name == "nt":
        bat = (
            "@echo off\r\n"
            "chcp 65001 >nul\r\n"
            "cd /d \"%~dp0\"\r\n"
            "set CHESSSAGE_PRODUCTION=1\r\n"
            "set CHESSSAGE_FROZEN=1\r\n"
            f"start \"\" \"%~dp0{EXE}\"\r\n"
            "exit /b 0\r\n"
        )
        (dist / "启动游戏.bat").write_text(bat, encoding="utf-8")
    else:
        sh = dist / "start.sh"
        sh.write_text(
            "#!/bin/bash\n"
            "cd \"$(dirname \"$0\")\"\n"
            "export CHESSSAGE_PRODUCTION=1\n"
            "export CHESSSAGE_FROZEN=1\n"
            f"exec ./{EXE} \"$@\"\n",
            encoding="utf-8",
        )
        os.chmod(sh, 0o755)

    (dist / "使用说明.txt").write_text(
        "棋圣 ChessSage · 六道轮回（Nuitka 打包版）\n"
        "============================================\n"
        "\n"
        "【运行】\n"
        "  Windows：双击「启动游戏.bat」（或直接双击 棋圣.exe）\n"
        "  Linux  ：在终端执行 ./start.sh\n"
        "\n"
        "【配置】\n"
        "  在 config.json 的 api_key 字段填入大模型密钥；\n"
        "  也可在游戏界面内填写。\n"
        "\n"
        "【说明】\n"
        "  · 首次启动会稍慢（需初始化内置运行环境）。\n"
        "  · 棋类服务按需启动：进入某棋类时才拉起对应服务，退出后自动回收。\n"
        "  · 浏览器打开 http://localhost:8080 即为总坛界面。\n"
        "  · 本目录需整体分发，请勿单独拷贝 棋圣.exe。\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    sys.exit(build())
