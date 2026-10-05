"""
规则/配置缓存验证脚本：test_rules_cache

目标：证明游戏配置（rules.json / pieces_*.json / board.json / board_state.json）
仅首读一次进入内存缓存，走棋 / valid_moves / AI 计算过程中不会重复读盘；
仅在显式 invalidate（AI 修改成功应用、reset 等）后才会重新读盘，且能反映磁盘新内容；
reset_battle/restart 仍正常工作。

覆盖：rpg 版 xiangqi、weiqi，及 sandbox 版 xiangqi、weiqi。
运行时需使用包含依赖的 venv，例如：.venv-build/bin/python tests/test_rules_cache.py
"""
import builtins
import importlib.util
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]  # CHessGAme
SHARED_DIR = ROOT / "shared"

# 参与计数的“活配置”文件名（不含 initial 目录里的 *.json.initial）
CONFIG_NAMES = {
    "rules.json", "board.json", "board_state.json",
    "pieces_red.json", "pieces_black.json", "pieces_white.json",
    "ui_config.json",
}

READS = {name: 0 for name in CONFIG_NAMES}
READS["TOTAL"] = 0
_real_open = builtins.open


def _counting_open(*args, **kwargs):
    mode = kwargs.get("mode")
    if mode is None:
        mode = args[1] if len(args) > 1 and isinstance(args[1], str) else "r"
    try:
        base = os.path.basename(os.fspath(args[0]))
    except Exception:
        base = ""
    if "r" in mode and base in CONFIG_NAMES:
        READS[base] += 1
        READS["TOTAL"] += 1
    return _real_open(*args, **kwargs)


def install_counter():
    builtins.open = _counting_open


def reset_counts():
    for k in READS:
        READS[k] = 0


def load_game_main(game_dir: Path, module_name: str):
    """按独立模块名加载某个游戏的 main.py（避免不同游戏重名冲突）。"""
    for p in (str(game_dir), str(ROOT), str(SHARED_DIR)):
        if p not in sys.path:
            sys.path.insert(0, p)
    spec = importlib.util.spec_from_file_location(module_name, game_dir / "main.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def check_game(name: str, game_dir: Path, module_name: str) -> bool:
    """对单个游戏执行缓存行为验证，返回是否通过。"""
    ctx = game_dir.name
    ok = True

    def assert_that(cond, msg):
        nonlocal ok
        if not cond:
            ok = False
            print(f"  [FAIL] {ctx}: {msg}")
        else:
            print(f"  [PASS] {ctx}: {msg}")

    rules_path = game_dir / "configs" / "rules.json"
    original_rules = json.loads(rules_path.read_text(encoding="utf-8"))

    try:
        reset_counts()
        mod = load_game_main(game_dir, module_name)
        state = mod.state

        # —— 1) 首次 load_configs 读盘一次后，反复调用不再读盘 ——
        after_import = READS["rules.json"] + READS.get("pieces_red.json", 0) + READS.get("pieces_black.json", 0)
        for _ in range(5):
            state.load_configs()  # 模拟多次走棋 / valid_moves / AI 计算前重建引擎
        stable = READS["rules.json"] + READS.get("pieces_red.json", 0) + READS.get("pieces_black.json", 0)
        assert_that(
            stable == after_import and after_import > 0,
            f"首读后反复 load_configs 不再读盘（rules/pieces 读盘恒为 {stable}）",
        )

        # —— 2) AI 修改成功应用：写入磁盘 + 重建，内存即最新，无需再读盘即可反映新规则 ——
        modified = json.loads(json.dumps(original_rules))
        modified["_CACHE_TEST_MARKER"] = "ai_applied_rules"
        state.apply_config_update({"rules": modified})  # 模拟 AI command 应用（写盘+重建）
        dcache_rules = READS["rules.json"]
        for _ in range(3):
            state.load_configs()
        assert_that(
            state.configs["rules"].get("_CACHE_TEST_MARKER") == "ai_applied_rules",
            "AI 修改应用后内存即反映新规则（下一次落子即可感知）",
        )
        assert_that(
            READS["rules.json"] == dcache_rules,
            f"AI 应用后续调用 load_configs 不额外读盘（rules.json 读盘仍为 {dcache_rules}）",
        )

        # —— 3) 显式 invalidate 触发恰好一次重新读盘，并反映磁盘新内容 ——
        disk_modified = json.loads(json.dumps(original_rules))
        disk_modified["_CACHE_TEST_MARKER"] = "disk_changed_rules"
        rules_path.write_text(json.dumps(disk_modified), encoding="utf-8")
        before_invalid = READS["rules.json"]
        state.invalidate_config_cache()
        state.load_configs()
        delta = READS["rules.json"] - before_invalid
        assert_that(
            delta == 1 and state.configs["rules"].get("_CACHE_TEST_MARKER") == "disk_changed_rules",
            f"invalidate 后恰一次读盘（delta={delta}）且反映磁盘新规则",
        )

        # —— 4) reset_battle/restart 仍正常工作，且之后缓存保持有效 ——
        state.reset_board()  # = reset_battle / restart / reset_configs 的底层操作
        assert_that(
            isinstance(state.configs.get("board_state", {}), dict)
            and state.configs.get("board_state", {}).get("current_turn") is not None,
            "reset_board（reset_battle/restart）可用，board_state 已重建",
        )
        before_reset_load = READS["rules.json"]
        state.load_configs()
        assert_that(
            READS["rules.json"] == before_reset_load,
            "reset 后 load_configs 不重复读盘（缓存已标记为最新）",
        )
    finally:
        # 恢复原始 rules.json 磁盘内容，避免污染后续测试用到原值
        rules_path.write_text(json.dumps(original_rules), encoding="utf-8")

    return ok


def main() -> int:
    # 单游戏模式：由主进程通过子进程调用，保证各游戏模块命名空间互不干扰
    if len(sys.argv) >= 4 and sys.argv[1] == "--single":
        install_counter()
        game_dir = Path(sys.argv[2])
        label = game_dir.name
        print(f"[check] {label} ({game_dir})")
        ok = check_game(label, game_dir, sys.argv[3])
        print("-" * 60)
        print(f"规则缓存验证（{label}）：", "通过" if ok else "失败")
        return 0 if ok else 1

    install_counter()
    targets = [
        ("rpg xiangqi ", ROOT / "xiangqi", "rpg_xiangqi_main"),
        ("rpg weiqi   ", ROOT / "weiqi", "rpg_weiqi_main"),
        ("sandbox xiangqi", ROOT / "sandbox" / "xiangqi", "sandbox_xiangqi_main"),
        ("sandbox weiqi  ", ROOT / "sandbox" / "weiqi", "sandbox_weiqi_main"),
    ]
    import subprocess

    all_ok = True
    for label, game_dir, mod_name in targets:
        print(f">>> 运行 {label}")
        proc = subprocess.run(
            [sys.executable, str(Path(__file__).resolve()), "--single",
             str(game_dir), mod_name],
            capture_output=True, text=True,
        )
        print(proc.stdout)
        if proc.stderr:
            print(proc.stderr)
        if proc.returncode != 0:
            all_ok = False
        print("=" * 60)
    print("规则缓存验证：", "全部通过" if all_ok else "存在失败")
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())