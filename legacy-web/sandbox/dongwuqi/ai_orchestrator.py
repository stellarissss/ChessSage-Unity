"""
动物棋 AI编排器（sandbox模式）

单基类重构产物：编排流程全部在 shared/ai_orchestrator_base.py，
本文件仅声明本棋类的差异配置（数据驱动）与少量结构性 override。
"""
import sys
from pathlib import Path
from typing import Any, Dict, Optional

# 自动注入 shared/ 到 sys.path（兼容直接执行与被 main.py 导入两种场景）
_SHARED = Path(__file__).resolve().parent.parent / "shared"
if _SHARED.exists() and str(_SHARED) not in sys.path:
    sys.path.insert(0, str(_SHARED))

from shared.ai_orchestrator_base import AIOrchestratorBase

BASE_DIR = Path(__file__).parent


class AIOrchestrator(AIOrchestratorBase):
    """动物棋 AI编排器（协调意图解析/业力评估/代码生成/校验）"""

    # ── 棋类差异配置（默认值见 AIOrchestratorBase 类属性注释） ──
    GAME_TYPE = "dongwuqi"
    # 模型名：继承基类 DEFAULT_MODEL（来自 ai_config，与顶层棋类统一）。
    # 历史遗留的 "deepseek-chat" 覆盖已移除 —— 顶层与 sandbox 共用同一模型。
    PIECE_TYPE_MAPPING = {
            "小鼠": "rat", "老鼠": "rat", "mouse": "rat",
            "猫": "cat", "小猫": "cat",
            "狗": "dog", "小狗": "dog",
            "狼": "wolf", "灰狼": "wolf",
            "豹": "leopard", "猎豹": "leopard",
            "虎": "tiger", "老虎": "tiger",
            "狮": "lion", "狮子": "lion",
            "象": "elephant", "大象": "elephant",
        }
    B_ROTATE_HINT_DOC = "board配置包括：width/height互换、regions区域坐标旋转（water/trap/den的cells数组）"
    B_TRANSFORM_NAME_DOC = """name必须与新type对应的emoji（红黑双方使用相同emoji）
  - elephant(象) → 🐘
  - lion(狮) → 🦁
  - tiger(虎) → 🐯
  - leopard(豹) → 🐆
  - wolf(狼) → 🐺
  - dog(狗) → 🐶
  - cat(猫) → 🐱
  - rat(鼠) → 🐭"""
    A2_DEFENSIVE_DESC = "保守防守型（重视防守，兽穴安全）"
    B_TYPE_HINT_DOC = """- 鼠 → "rat"
- 猫 → "cat"
- 狗 → "dog"
- 狼 → "wolf"
- 豹 → "leopard"
- 虎 → "tiger"
- 狮 → "lion"
- 象 → "elephant"

绝对禁止使用 pawn、rook、bishop、knight、king、queen 等国际象棋术语，也禁止使用 general、chariot、horse、cannon、soldier、advisor 等中国象棋术语！"""
    CP_PREDEFINED_TYPES = {"rat", "cat", "dog", "wolf", "leopard", "tiger", "lion", "elephant"}

    def __init__(self, api_key: str = ""):
        # 公共基础设施（logger/token_stats/base_url 等）由基类初始化
        super().__init__(base_dir=BASE_DIR, api_key=api_key)
