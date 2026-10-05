"""
跳棋 AI编排器（sandbox模式）

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
    """跳棋 AI编排器（协调意图解析/业力评估/代码生成/校验）"""

    # ── 棋类差异配置（默认值见 AIOrchestratorBase 类属性注释） ──
    GAME_TYPE = "tiaoqi"
    # 模型名：继承基类 DEFAULT_MODEL（来自 ai_config，与顶层棋类统一）。
    # 历史遗留的 "deepseek-chat" 覆盖已移除 —— 顶层与 sandbox 共用同一模型。
    PIECE_TYPE_MAPPING = {
            "pawn": "piece", "rook": "piece", "knight": "piece",
            "bishop": "piece", "queen": "piece", "king": "piece",
            "soldier": "piece", "chariot": "piece", "horse": "piece",
            "elephant": "piece", "advisor": "piece", "general": "piece",
            "cannon": "piece",
        }
    B_TYPE_HINT_LEAD = "跳棋只有一种棋子类型，type字段必须使用 \"piece\"。"
    B_TYPE_HINT_DOC = """- 红方棋子name字段为 "红"
- 黑方棋子name字段为 "黑"

跳棋棋盘为六角星形（hexagonal_star），坐标使用双倍列系统：
- row范围: 0-16
- col范围: 0-24（偶数行col为偶数，奇数行col为奇数）
- 红方营区: rows 0-3（顶部三角）
- 黑方营区: rows 13-16（底部三角）

绝对禁止使用 pawn、rook、bishop、knight、king、queen、soldier、chariot 等象棋术语！"""
    CP_PREDEFINED_TYPES = {"piece"}
    CP_PRIMITIVES = ("step", "hop")
    CP_PRIMITIVES_DESC = "step/hop"
    CP_PRIMITIVES_PHRASE = "step/hop 原语"
    CP_TYPE_CONFLICT_SUFFIX = '（不能是 "piece"）'
    CP_DYNAMIC_BOUNDS = False
    CP_DEFAULT_BOUNDS = (17, 25)
    CP_RANGE_ERROR_FMT = "(row 0-{wm1}, col 0-{hm1})"
    D_AUTO_SYNC_BOARD_STATE = False
    B_ROTATE_EMPHASIS = """
## ⚠️ 操作类型特别提醒（旋转棋盘）
- 跳棋棋盘为六角星形，旋转操作需谨慎处理六向对称性
- 必须同时修改board配置和棋子坐标，不能只改棋子
- 所有棋子的[row, col]坐标都需要按相同旋转规则进行转换
- 棋子的其他属性（id、type、side、name、is_alive等）保持不变
"""
    B_TRANSFORM_EMPHASIS = """
## ⚠️ 操作类型特别提醒（棋子类型变换）
- 跳棋只有一种棋子类型 "piece"，类型变换通常无意义
- 如必须变换，type字段只能使用 "piece"，name字段保持红方"红"/黑方"黑"
- id、side、position、is_alive 等核心属性绝对不能修改
- 棋子数量不能变化（不能新增也不能删除棋子）
- 使用 replace 操作修改 /pieces/{index}/type 和 /pieces/{index}/name
- 注意：数组索引从 0 开始
"""
    D_SIZE_CHANGE_NOTE = """
## ⚠️ 重要提醒：跳棋棋盘尺寸修改
跳棋使用六角星形棋盘，坐标系统和邻接表是程序生成的。
- board.json 的 appearance 字段包含视觉配置（layout.viewbox_* 等）
- 修改尺寸主要影响视觉表现（如缩放、padding）
- 不能简单增减 geometry.rows 或 max_col（会破坏邻接关系）
- **不要输出空的 patch 数组！至少要输出一个操作（即使只是保持原值的 replace）**
"""
    CP_BOARD_DESC = """- 棋盘类型: 六角星形（hexagonal_star）
- 棋盘坐标系统: 双倍列（doubled_column）
- row范围: 0-16, col范围: 0-24
- 偶数行col为偶数, 奇数行col为奇数"""
    CP_PRIMITIVES_GUIDE = """
## 跳棋移动原语（创建新棋子时必须基于以下原语组合）
- step: 向相邻空位移动一格
  - 字段: {"kind": "step", "land": "empty", "sym": "hex6"}
  - land: empty（仅落到空位）或 any（可落到任何位置）
  - sym: hex6（六向展开）或 none（不展开）
- hop: 隔子跳跃，chain=true可连续跳跃
  - 字段: {"kind": "hop", "land": "empty", "chain": true, "sym": "hex6"}
  - chain: true（可连续跳跃）或 false（单次跳跃）
  - 跳过的棋子不被吃掉，仍保留在原位
"""
    CP_TAIL_REQUIREMENTS_DOC = """5. 棋子位置必须在棋盘范围内（row 0-16, col 0-24）且不与现有棋子重叠
6. 新棋子位置必须是六角星棋盘上的合法位置（参考board.json的positions字段）
7. 只输出JSON对象，不要输出其他内容"""

    def __init__(self, api_key: str = ""):
        # 公共基础设施（logger/token_stats/base_url 等）由基类初始化
        super().__init__(base_dir=BASE_DIR, api_key=api_key)
