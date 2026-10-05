"""
围棋 AI编排器（sandbox模式）

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
    """围棋 AI编排器（协调意图解析/业力评估/代码生成/校验）"""

    # ── 棋类差异配置（默认值见 AIOrchestratorBase 类属性注释） ──
    GAME_TYPE = "weiqi"
    # 模型名：继承基类 DEFAULT_MODEL（来自 ai_config，与顶层棋类统一）。
    # 历史遗留的 "deepseek-chat" 覆盖已移除 —— 顶层与 sandbox 共用同一模型。
    DEFAULT_TURN = "black"
    SIDE_TOGGLE = {"black": "white", "white": "black"}
    WINNER_DEFAULT = "black"
    SIDE_LABELS = {"black": "黑方", "white": "白方"}
    BOARD_SUMMARY_ORDER = ("black", "white")
    SIDE_CONFIG_MAP = {
            "white": ("pieces_red", "白方"),
            "black": ("pieces_black", "黑方"),
        }
    SIDE_OVERRIDE_KEYS = ("white", "black")
    CP_DEFAULT_BOUNDS = (19, 19)
    C_MODIFY_EXAMPLE = "白方的马"
    B_TRANSFORM_NAME_DOC = """name必须与新type对应：白方棋子用白方名称，黑方棋子用黑方名称
  - chariot(车) → 白方"車" / 黑方"車"
  - horse(马) → 白方"馬" / 黑方"馬"
  - elephant(象) → 白方"相" / 黑方"象"
  - advisor(士) → 白方"仕" / 黑方"士"
  - general(将) → 白方"帥" / 黑方"將"
  - cannon(炮) → 白方"炮" / 黑方"砲"
  - soldier(兵) → 白方"兵" / 黑方"卒" """

    def __init__(self, api_key: str = ""):
        # 公共基础设施（logger/token_stats/base_url 等）由基类初始化
        super().__init__(base_dir=BASE_DIR, api_key=api_key)

    def _a1_undo_step(self, board: dict, last: dict) -> None:
        """围棋落子制悔棋：from 为 None 时标记落子死亡；captured 兼容 ID 列表。"""
        piece = self._find_piece(board, last["piece_id"])
        if piece:
            if last.get("from") is not None:
                piece["position"] = last["from"]
            else:
                piece["is_alive"] = False
        if last.get("captured"):
            captured_ids = last["captured"]
            if isinstance(captured_ids, list):
                for cap_id in captured_ids:
                    cap = self._find_piece(board, cap_id)
                    if cap:
                        cap["is_alive"] = True
            else:
                cap = self._find_piece(board, captured_ids)
                if cap:
                    cap["is_alive"] = True
        board["current_turn"] = self.SIDE_TOGGLE.get(board["current_turn"], self.WINNER_DEFAULT)

    def _a1_undo_post(self, board: dict) -> None:
        """围棋悔棋后清除劫争（ko）状态。"""
        board["ko_state"] = None
