"""
象棋 AI编排器（顶层 RPG模式）

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

from shared.ai_orchestrator_base import RPGOrchestratorBase
from karma_assessor import KarmaAssessor

BASE_DIR = Path(__file__).parent


class AIOrchestrator(RPGOrchestratorBase):
    """象棋 AI编排器（协调意图解析/业力评估/代码生成/校验）"""

    # ── 棋类差异配置（默认值见 AIOrchestratorBase 类属性注释） ──
    GAME_TYPE = "xiangqi"

    def __init__(self, api_key: str = ""):
        super().__init__(base_dir=BASE_DIR, api_key=api_key)
        self.karma_assessor = KarmaAssessor(api_key=api_key, game_type="xiangqi")
