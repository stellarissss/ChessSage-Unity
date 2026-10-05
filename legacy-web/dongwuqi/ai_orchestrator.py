"""
动物棋 AI编排器（顶层 RPG模式）

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
    """动物棋 AI编排器（协调意图解析/业力评估/代码生成/校验）"""

    # ── 棋类差异配置（默认值见 AIOrchestratorBase 类属性注释） ──
    GAME_TYPE = "dongwuqi"
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
    C_EXTRA_GUIDE = """\n\n## 移动原语说明（修改 moves 字段时必须使用以下 JSON 原语）

所有棋子的移动规则必须基于 jump 和 ray 两种原子原语组合生成。

### jump - 跳跃移动
格式:
```json
{
  "kind": "jump",
  "to": [dx, dy],
  "block": [[bx, by], ...],
  "land": "any|empty|enemy",
  "sym": "none|rotate4|rotate4_mirror|mirror_x",
  "where": [条件列表]
}
```
- `to`: 目标相对坐标 [dx, dy]。支持特殊格式 `{"mode": "region", "region": "$full_board"}` 表示区域跳跃（如全图瞬移）
- `block`: 阻挡坐标列表，相对坐标。如果任一 block 位置有棋子，则此移动不合法
- `land`: 落子规则
  - `"any"`: 可落在空格或敌方棋子（吃子）
  - `"empty"`: 只能落在空格
  - `"enemy"`: 只能落在敌方棋子（必须吃子）
- `sym`: 对称展开模式
  - `"none"`: 不展开（如 [1,0] 只表示右）
  - `"rotate4"`: 四方向旋转（右/上/左/下）
  - `"rotate4_mirror"`: 四方向旋转 + 镜像（共8方向）
  - `"mirror_x"`: 左右镜像
- `where`: 条件过滤数组（可选），支持以下条件：
  - `{"not": {"in_water": {"pos": "$dest"}}}`: 目标不能在水域
  - `{"not": {"in_own_den": {"pos": "$dest"}}}`: 目标不能是己方兽穴
  - `{"not": {"in_enemy_trap": {"pos": "$dest"}}}`: 目标不能在敌方陷阱
  - `{"in_region": {"region": "$full_board", "pos": "$dest"}}`: 目标必须在指定区域

### ray - 射线移动（沿方向连续移动）
格式:
```json
{
  "kind": "ray",
  "dir": [dx, dy],
  "max": 最大步数,
  "screens": 可跨越的棋子数,
  "land": "any|empty|enemy",
  "sym": "none|rotate4|rotate4_mirror|mirror_x",
  "path_constraint": {"must_be": "water", "no_blocker": true},
  "where": [条件列表]
}
```
- `dir`: 方向向量 [dx, dy]
- `max`: 最大移动步数，-1 表示无限
- `screens`: 可跨越的棋子数（如车的直线移动 screens=0，炮的隔子吃 screens=1）
- `path_constraint`: 路径约束（可选），用于跳河等特殊机制
  - `must_be`: 路径必须满足的地形（如 "water"）
  - `no_blocker`: 路径上是否允许有棋子阻挡

### 复合移动示例
- 普通四方向走一格（象/狮/虎等）:
  `{"kind": "jump", "to": [1,0], "block": [], "land": "any", "sym": "rotate4", "where": [...]}`
- 全图瞬移（如"人"棋子）:
  `{"kind": "jump", "to": {"mode": "region", "region": "$full_board"}, "land": "any", "sym": "none", "where": [{"not": {"in_own_den": {"pos": "$dest"}}}]}`
- 直线跳河（狮/虎）:
  `{"kind": "ray", "dir": [1,0], "max": 4, "screens": 0, "land": "any", "sym": "rotate4", "path_constraint": {"must_be": "water", "no_blocker": true}, "where": [...]}`
\n"""
    CP_EXTRA_GUIDE = """\n\n## 移动原语说明（修改 moves 字段时必须使用以下 JSON 原语）

所有棋子的移动规则必须基于 jump 和 ray 两种原子原语组合生成。

### jump - 跳跃移动
格式:
```json
{
  "kind": "jump",
  "to": [dx, dy],
  "block": [[bx, by], ...],
  "land": "any|empty|enemy",
  "sym": "none|rotate4|rotate4_mirror|mirror_x",
  "where": [条件列表]
}
```
- `to`: 目标相对坐标 [dx, dy]。支持特殊格式 `{"mode": "region", "region": "$full_board"}` 表示区域跳跃（如全图瞬移）
- `block`: 阻挡坐标列表，相对坐标。如果任一 block 位置有棋子，则此移动不合法
- `land`: 落子规则
  - `"any"`: 可落在空格或敌方棋子（吃子）
  - `"empty"`: 只能落在空格
  - `"enemy"`: 只能落在敌方棋子（必须吃子）
- `sym`: 对称展开模式
  - `"none"`: 不展开（如 [1,0] 只表示右）
  - `"rotate4"`: 四方向旋转（右/上/左/下）
  - `"rotate4_mirror"`: 四方向旋转 + 镜像（共8方向）
  - `"mirror_x"`: 左右镜像
- `where`: 条件过滤数组（可选），支持以下条件：
  - `{"not": {"in_water": {"pos": "$dest"}}}`: 目标不能在水域
  - `{"not": {"in_own_den": {"pos": "$dest"}}}`: 目标不能是己方兽穴
  - `{"not": {"in_enemy_trap": {"pos": "$dest"}}}`: 目标不能在敌方陷阱
  - `{"in_region": {"region": "$full_board", "pos": "$dest"}}`: 目标必须在指定区域

### ray - 射线移动（沿方向连续移动）
格式:
```json
{
  "kind": "ray",
  "dir": [dx, dy],
  "max": 最大步数,
  "screens": 可跨越的棋子数,
  "land": "any|empty|enemy",
  "sym": "none|rotate4|rotate4_mirror|mirror_x",
  "path_constraint": {"must_be": "water", "no_blocker": true},
  "where": [条件列表]
}
```
- `dir`: 方向向量 [dx, dy]
- `max`: 最大移动步数，-1 表示无限
- `screens`: 可跨越的棋子数（如车的直线移动 screens=0，炮的隔子吃 screens=1）
- `path_constraint`: 路径约束（可选），用于跳河等特殊机制
  - `must_be`: 路径必须满足的地形（如 "water"）
  - `no_blocker`: 路径上是否允许有棋子阻挡

### 复合移动示例
- 普通四方向走一格（象/狮/虎等）:
  `{"kind": "jump", "to": [1,0], "block": [], "land": "any", "sym": "rotate4", "where": [...]}`
- 全图瞬移（如"人"棋子）:
  `{"kind": "jump", "to": {"mode": "region", "region": "$full_board"}, "land": "any", "sym": "none", "where": [{"not": {"in_own_den": {"pos": "$dest"}}}]}`
- 直线跳河（狮/虎）:
  `{"kind": "ray", "dir": [1,0], "max": 4, "screens": 0, "land": "any", "sym": "rotate4", "path_constraint": {"must_be": "water", "no_blocker": true}, "where": [...]}`
\n"""
    C_ACTION_KEYWORDS = (
            "modify_rule", "change_move", "add_ability", "create_custom_piece",
            "alter_movement", "modify_piece", "change_rule", "update_rule",
            "add_move", "remove_move", "change_capture", "modify_screens",
            "modify_existing_piece",
        )

    def __init__(self, api_key: str = ""):
        super().__init__(base_dir=BASE_DIR, api_key=api_key)
        self.karma_assessor = KarmaAssessor(api_key=api_key, game_type="dongwuqi")

    async def _route_modify_existing_piece(
        self, instruction: dict, action_intent: dict, configs: dict, log_entry: dict
    ) -> Optional[Dict[str, Any]]:
        """C+ 路由：dongwuqi 的"修改现有棋子"（如"把我的狼变成人"）改走 C 类规则修改流程。"""
        if instruction.get("action") == "modify_existing_piece":
            return await self._handle_action_c_with_log(action_intent, configs, log_entry)
        return None

    def _c_sync_board_pieces(
        self, action_str: str, instruction: dict,
        target_config_name: str, board: dict, log_entry: dict,
    ) -> None:
        """modify_existing_piece 时同步修改 board_state 中的棋子实例。"""
        if action_str != "modify_existing_piece":
            return
        params = instruction.get("parameters", {})
        old_type = params.get("piece_type_old") or params.get("old_type")
        new_type = params.get("new_type")
        new_name = params.get("new_name")
        target_side = "red" if target_config_name == "pieces_red" else "black"
        if old_type and new_type:
            for p in board.get("pieces", []):
                if p.get("side") == target_side and p.get("type") == old_type and p.get("is_alive", True):
                    p["type"] = new_type
                    if new_name:
                        p["name"] = new_name
                    log_entry["board_state_sync"] = f"已将 {target_side} 的 {old_type} 棋子实例同步为 {new_type}"
                    break  # 只修改第一个匹配的棋子
