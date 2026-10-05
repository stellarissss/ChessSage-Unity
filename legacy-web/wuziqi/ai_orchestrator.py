"""
五子棋 AI编排器（顶层 RPG模式）

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
    """五子棋 AI编排器（协调意图解析/业力评估/代码生成/校验）"""

    # ── 棋类差异配置（默认值见 AIOrchestratorBase 类属性注释） ──
    GAME_TYPE = "wuziqi"
    DEFAULT_TURN = "black"
    SIDE_TOGGLE = {"black": "white", "white": "black"}
    WINNER_DEFAULT = "black"
    SIDE_LABELS = {"black": "黑方", "white": "白方"}
    BOARD_SUMMARY_ORDER = ("white", "black")
    SIDE_CONFIG_MAP = {
            "white": ("pieces_red", "白方"),
            "black": ("pieces_black", "黑方"),
        }
    SIDE_OVERRIDE_KEYS = ("white", "black")
    CP_DEFAULT_BOUNDS = (15, 15)
    A2_MECHANISM_SIDE_FMT = "white|black"
    A2_ACTION_NAMES = (
            "modify_personality", "add_mechanism", "set_ai_personality",
            "freeze_ai", "ai_takeover", "random_move",
            "skip_turn", "extra_turn", "modify_mechanism",
            "swap_sides", "player_takeover", "player_control",
        )
    A2_BOARD_STATE_ACTIONS = (
            "add_mechanism", "freeze_ai", "ai_takeover", "random_move",
            "skip_turn", "extra_turn", "modify_mechanism",
            "swap_sides", "player_takeover", "player_control",
        )
    A2_EXTRA_CURRENT_STATE_DOC = '\n## 当前 board_state.json 的 player_side 字段\n```json\n{player_side_json}\n```\n'
    C_MODIFY_EXAMPLE = "白方的马"
    B_TRANSFORM_NAME_DOC = """name必须与新type对应：白方棋子用白方名称，黑方棋子用黑方名称
  - chariot(车) → 白方"車" / 黑方"車"
  - horse(马) → 白方"馬" / 黑方"馬"
  - elephant(象) → 白方"相" / 黑方"象"
  - advisor(士) → 白方"仕" / 黑方"士"
  - general(将) → 白方"帥" / 黑方"將"
  - cannon(炮) → 白方"炮" / 黑方"砲"
  - soldier(兵) → 白方"兵" / 黑方"卒" """
    A2_EXTRA_MECHANISM_DOC = """\n## 玩家阵营字段（board_state.json 顶层）\n- `player_side`: "white" | "black"（默认 "black"），玩家的持久阵营身份\n- **阵营互换**（持久，action=swap_sides）：`{"op": "replace", "path": "/player_side", "value": "white"}`\n- **永久接管AI方**（如"让我一直操控白方"）：将 `player_side` 改为 AI 方颜色\n- **临时接管**（action=player_takeover，如"让我接管白方两回合"）：使用 `player_control` 原语，不要修改 `player_side`\n路径: /player_side\n"""

    def __init__(self, api_key: str = ""):
        super().__init__(base_dir=BASE_DIR, api_key=api_key)
        self.karma_assessor = KarmaAssessor(api_key=api_key, game_type="wuziqi")
