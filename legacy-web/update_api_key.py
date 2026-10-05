#!/usr/bin/env python3
"""
批量更新各棋类的 main.py，使其从主配置文件读取 API 密钥
"""
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent
GAMES = ["wuziqi", "weiqi", "dongwuqi", "tiaoqi", "heibaiqi"]


def update_main_py(game: str):
    file_path = WORKSPACE / game / "main.py"
    if not file_path.exists():
        print(f"  跳过 {game} (文件不存在)")
        return

    content = file_path.read_text(encoding="utf-8")

    changes = []

    # 1. 添加导入
    import_line = "from ai_config import get_api_key"
    if import_line not in content:
        # 在 mechanism_engine 导入之后添加
        content = content.replace(
            "from mechanism_engine import MechanismEngine",
            "from mechanism_engine import MechanismEngine\nfrom ai_config import get_api_key"
        )
        changes.append("添加导入")

    # 2. 修改 AIOrchestrator 初始化
    if "AIOrchestrator(api_key=api_key)" not in content:
        # 替换 AIOrchestrator()
        content = content.replace(
            "self.ai_orchestrator = AIOrchestrator()",
            '        api_key = get_api_key()\n        self.ai_orchestrator = AIOrchestrator(api_key=api_key)'
        )
        changes.append("修改初始化")

    if changes:
        file_path.write_text(content, encoding="utf-8")
        print(f"  ✓ {game}: {', '.join(changes)}")
    else:
        print(f"  - {game}: 无需更新")


def main():
    print("开始批量更新 API 密钥读取逻辑...")
    print()
    for game in GAMES:
        print(f"{game}:")
        update_main_py(game)
        print()
    print("完成！")


if __name__ == "__main__":
    main()
