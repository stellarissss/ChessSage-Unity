# -*- mode: python ; coding: utf-8 -*-
"""
棋圣 ChessSage · PyInstaller 打包规格（onedir，非单文件）

用途：Win/Linux 一键产出可执行入口。入口直接使用 main.py —— 它已内置冻结态
判定（sys.frozen/__compiled__）与「启动器 + 子进程」双模式，无需额外 bootstrap。

产物结构（dist/chesssage/，EXE 名：棋圣 / 棋圣.exe）：
    dist/chesssage/
        ├── 棋圣[.exe]          # PyInstaller 编译入口（自带 Python 运行时）
        ├── _internal/          # 依赖（fastapi/uvicorn/pydantic/httpx 等）
        ├── hub/ shared/ configs/ samsara/ sandbox/
        │   └── 6 个棋类目录（xiangqi/…/heibaiqi）   <- 外部数据，运行时懒加载
        └── config.json

要点：
- 游戏代码（main.py 之外的 .py）与资源作为【外部数据】随包携带，运行时由子进程
  runpy 加载，PyInstaller 静态分析看不到它们的 import，因此第三方 Web 依赖须
  在此显式收集为 hiddenimports。
- rembg/torch/numpy/PIL 等仅用于 shared/assets 离线美术脚本，不打包以控体积。
- PyInstaller 不支持交叉编译：Windows 版 .exe 必须在 Windows 上构建。
"""
from PyInstaller.utils.hooks import collect_submodules
import os

# SPECPATH 由 PyInstaller 注入（spec 所在目录）；项目根即其上 1 级
_ROOT = os.path.dirname(os.path.normpath(SPECPATH))
_ENTRY = os.path.join(_ROOT, "main.py")

# 运行时第三方依赖
_RUNTIME_PKGS = [
    "uvicorn", "starlette", "fastapi", "httpx", "httpcore",
    "pydantic", "pydantic_core", "jsonschema", "jsonpatch",
    "websockets", "anyio", "h11", "certifi", "idna",
    "httptools", "watchfiles", "uvloop", "yaml",
]
hiddenimports = []
for _pkg in _RUNTIME_PKGS:
    try:
        hiddenimports += collect_submodules(_pkg)
    except Exception:
        pass

_EXCLUDES = [
    "rembg", "torch", "numpy", "PIL", "Pillow", "scipy",
    "pandas", "matplotlib", "tensorflow", "onnxruntime", "skimage",
    "pytest", "_pytest", "IPython", "notebook", "jupyter", "tkinter",
]

a = Analysis(
    [_ENTRY],
    pathex=[_ROOT],
    binaries=[],
    datas=[],
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=_EXCLUDES,
    noarchive=False,
    optimize=0,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="棋圣",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="chesssage",
)