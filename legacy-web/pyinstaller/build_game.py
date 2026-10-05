#!/usr/bin/env python3
"""
棋圣 ChessSage · 一键打包脚本（PyInstaller onedir，非单文件）

入口直接为 main.py —— 它已内置冻结态判定与「启动器 + 子进程」双模式。
游戏代码与资源（hub/ shared/ configs/ samsara/ sandbox/ 6 个棋类目录、config.json）
作为外部数据复制到 exe 旁，运行时由子进程 runpy 懒加载。

产物结构（dist/chesssage/）：
    dist/chesssage/
    ├── 棋圣[.exe]        # 冻结入口（自带 Python 运行时 + Web 依赖）
    ├── hub/ shared/ configs/ samsara/ sandbox/
    ├── xiangqi/ wuziqi/ weiqi/ dongwuqi/ tiaoqi/ heibaiqi/
    ├── config.json       # API 密钥（取自项目根，未配置时生成空占位）
    ├── 启动游戏.bat      # Windows 启动脚本
    └── start.sh          # Linux/macOS 启动脚本

用法：
    Windows:  python build_game.py   （或 一键打包.bat）
    Linux:    python3 build_game.py

说明：PyInstaller 不支持交叉编译 —— Windows 版 .exe 必须在 Windows 上构建；
本脚本跨平台，在 Windows 上运行即产出 棋圣.exe。
"""
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

WORKSPACE_ROOT = Path(__file__).resolve().parent.parent
PYI_DIR = Path(__file__).resolve().parent
DIST_ROOT = WORKSPACE_ROOT / "dist" / "chesssage"
WORK_PATH = WORKSPACE_ROOT / "build" / "pyi"
SPEC_FILE = PYI_DIR / "chesssage.spec"
EXE_NAME = "棋圣.exe" if os.name == "nt" else "棋圣"

# 复制为外部数据的目录（游戏代码 / 前端 / 配置 / 资源）
COPY_DIRS = [
    "hub", "shared", "configs", "samsara", "sandbox",
    "xiangqi", "wuziqi", "weiqi", "dongwuqi", "tiaoqi", "heibaiqi",
]

# 复制为外部数据的文件
# 注意：config.json 不在此列 —— 它由后续步骤生成「空模板」，
# 避免把本地含真实密钥的配置原样打进分发包（见下方 config.json 处理段）。
COPY_FILES = ["achievements.json"]

# 复制时排除的目录 / 文件（脚手架与中间产物）
EXCLUDE_DIRS = {"__pycache__", ".git", ".trae", "build", "dist", "tests", "node_modules"}
EXCLUDE_PATTERNS = (".pyc", ".pyo", ".log", ".bak", ".orig")
EXCLUDE_FILES = {"chesssage.spec", "build_game.py", "一键打包.bat", "使用说明.txt", "启动游戏.bat"}


def _should_exclude(rel: str) -> bool:
    parts = rel.split("/")
    if any(p in EXCLUDE_DIRS for p in parts):
        return True
    name = parts[-1]
    if name in EXCLUDE_FILES or name.endswith(EXCLUDE_PATTERNS):
        return True
    if name.startswith("."):
        return True
    return False


def copy_tree(src: Path, dst: Path, rel: str = "") -> tuple[int, int]:
    """复制目录树（排除脚手架），返回 (文件数, 目录数)。"""
    nf = nd = 0
    dst.mkdir(parents=True, exist_ok=True)
    for item in src.iterdir():
        rel_item = f"{rel}/{item.name}" if rel else item.name
        if _should_exclude(rel_item):
            continue
        if item.is_dir():
            nf2, nd2 = copy_tree(item, dst / item.name, rel_item)
            nf += nf2
            nd += nd2 + 1
        else:
            shutil.copy2(item, dst / item.name)
            nf += 1
    return nf, nd


def clean_old() -> None:
    for p in (DIST_ROOT, WORK_PATH):
        if p.exists():
            print(f"  清理 {p}")
            shutil.rmtree(p, ignore_errors=True)


def run_pyinstaller() -> None:
    print("\n[1/4] 运行 PyInstaller（onedir）...")
    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--clean", "--noconfirm",
        "--distpath", str(WORKSPACE_ROOT / "dist"),
        "--workpath", str(WORK_PATH),
        str(SPEC_FILE),
    ]
    result = subprocess.run(cmd, cwd=str(WORKSPACE_ROOT))
    if result.returncode != 0:
        print("✗ PyInstaller 构建失败")
        sys.exit(1)
    print(f"✓ PyInstaller 产物生成: {DIST_ROOT / EXE_NAME}")


def copy_external_data() -> None:
    print("\n[2/4] 复制外部数据（游戏源码 / 资源 / 配置）...")
    nf, nd = 0, 0
    for dir_name in COPY_DIRS:
        src = WORKSPACE_ROOT / dir_name
        if not src.is_dir():
            print(f"  [!] 缺失目录，跳过: {dir_name}")
            continue
        nf2, nd2 = copy_tree(src, DIST_ROOT / dir_name, dir_name)
        nf += nf2
        nd += nd2
        print(f"  · {dir_name}: {nf2} 文件 / {nd2} 目录")
    for file_name in COPY_FILES:
        src = WORKSPACE_ROOT / file_name
        if src.is_file():
            shutil.copy2(src, DIST_ROOT / file_name)
            nf += 1
    # config.json：未提供则生成空占位（避免把真实 Key 打进分发包）
    cfg = DIST_ROOT / "config.json"
    if not cfg.exists():
        cfg.write_text(json.dumps({"api_key": "", "base_url": "https://api.deepseek.com/v1",
                                   "model": "deepseek-flash"},
                                  indent=2, ensure_ascii=False), encoding="utf-8")
    else:
        # 存在则清空凭证（防敏感信息外泄）
        try:
            data = json.loads(cfg.read_text(encoding="utf-8"))
            data["api_key"] = ""
            cfg.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
        except Exception:
            pass
    print(f"  [i] 外部数据合计 {nf} 文件 / {nd} 目录")


def generate_start_scripts() -> None:
    print("\n[3/4] 生成启动脚本...")
    if os.name == "nt":
        bat = (
            "@echo off\r\n"
            "chcp 65001 >nul\r\n"
            "cd /d \"%~dp0\"\r\n"
            f"start \"\" \"%~dp0{EXE_NAME}\"\r\n"
            "exit /b 0\r\n"
        )
        (DIST_ROOT / "启动游戏.bat").write_text(bat, encoding="utf-8")
    else:
        sh = DIST_ROOT / "start.sh"
        sh.write_text(
            "#!/bin/bash\n"
            "cd \"$(dirname \"$0\")\"\n"
            "export CHESSSAGE_FROZEN=1\n"
            f"exec ./{EXE_NAME} \"$@\"\n",
            encoding="utf-8",
        )
        os.chmod(sh, 0o755)

    (DIST_ROOT / "使用说明.txt").write_text(
        "棋圣 ChessSage · 六道轮回（PyInstaller onedir 版）\n"
        "============================================\n"
        "【运行】\n"
        "  Windows：双击「启动游戏.bat」或「棋圣.exe」\n"
        "  Linux  ：在终端执行 ./start.sh\n"
        "  浏览器将打开 http://localhost:8080/\n"
        "\n"
        "【配置】\n"
        "  在 config.json 的 api_key 字段填入大模型密钥；或在游戏界面内填写。\n"
        "\n"
        "【说明】\n"
        "  · 本目录需整体分发，请勿单独拷贝 棋圣[.exe]。\n"
        "  · 棋类服务按需启动，进入某棋类时才拉起对应服务。\n",
        encoding="utf-8",
    )
    print("  · 已生成 启动脚本 / 使用说明")


def main() -> None:
    print("=" * 60)
    print("  棋圣 ChessSage · 一键打包（PyInstaller onedir）")
    print("=" * 60)
    pyinstaller_ok = subprocess.run(
        [sys.executable, "-c", "import PyInstaller"], capture_output=True
    ).returncode == 0
    if not pyinstaller_ok:
        print("✗ 未安装 PyInstaller，请先：pip install pyinstaller")
        sys.exit(1)

    print("\n[清理]")
    clean_old()
    run_pyinstaller()
    copy_external_data()
    generate_start_scripts()

    print("\n[4/4] 完成")
    print(f"  产物目录 : {DIST_ROOT}")
    files = sum(1 for _ in DIST_ROOT.rglob("*") if _.is_file())
    size_mb = sum(_.stat().st_size for _ in DIST_ROOT.rglob("*") if _.is_file()) / 1024 / 1024
    print(f"  文件总数 : {files}   占用空间 : {size_mb:.0f} MB")
    print(f"  启动方式 : {'双击 ' + str(DIST_ROOT / '启动游戏.bat') if os.name == 'nt' else 'cd ' + str(DIST_ROOT) + ' && ./start.sh'}")
    print("=" * 60)


if __name__ == "__main__":
    main()