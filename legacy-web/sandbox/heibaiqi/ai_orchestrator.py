"""
黑白棋 AI编排器（sandbox模式）

单基类重构产物：编排流程全部在 shared/ai_orchestrator_base.py，
本文件仅声明本棋类的差异配置（数据驱动）与少量结构性 override。
"""
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

# 自动注入 shared/ 到 sys.path（兼容直接执行与被 main.py 导入两种场景）
_SHARED = Path(__file__).resolve().parent.parent / "shared"
if _SHARED.exists() and str(_SHARED) not in sys.path:
    sys.path.insert(0, str(_SHARED))

from shared.ai_orchestrator_base import AIOrchestratorBase
from schema_validator import validate_board_state, validate_pieces

BASE_DIR = Path(__file__).parent


class AIOrchestrator(AIOrchestratorBase):
    """黑白棋 AI编排器（协调意图解析/业力评估/代码生成/校验）"""

    # ── 棋类差异配置（默认值见 AIOrchestratorBase 类属性注释） ──
    GAME_TYPE = "heibaiqi"
    # 模型名：继承基类 DEFAULT_MODEL（来自 ai_config，与顶层棋类统一）。
    # 历史遗留的 "deepseek-chat" 覆盖已移除 —— 顶层与 sandbox 共用同一模型。
    DEFAULT_TURN = "black"
    SIDE_TOGGLE = {"black": "white", "white": "black"}
    WINNER_DEFAULT = "black"
    SIDE_LABELS = {"black": "黑方", "white": "白方"}
    SIDE_CONFIG_MAP = {
            "white": ("pieces_white", "白方"),
            "black": ("pieces_black", "黑方"),
        }
    SIDE_CONFIG_DEFAULT = "black"
    SIDE_OVERRIDE_KEYS = ("red", "black")
    PIECE_TYPE_MAPPING = {
            "pawn": "disc", "rook": "disc", "knight": "disc",
            "bishop": "disc", "queen": "disc", "king": "disc",
        }
    B_ROTATE_HINT_DOC = "board配置包括：width/height互换、regions 坐标旋转"
    B_TRANSFORM_NAME_DOC = """name 必须与新 type 对应：黑白棋标准棋子 disc 的 name 按阵营区分
  - 黑方 disc → name "黑棋"，label "●"
  - 白方 disc → name "白棋"，label "○"
- 自定义棋子的 name 应语义清晰，体现其能力"""
    B_TYPE_HINT_LEAD = "黑白棋标准棋子类型只有一种："
    B_TYPE_HINT_DOC = """- 棋子 → "disc"（●黑棋 / ○白棋）

自定义棋子可使用任意英文标识符命名 type（如 jumper、slider、flipper），但不得与 disc 冲突。
绝对禁止使用 pawn、rook、bishop、knight、king、queen 等国际象棋术语！"""
    A2_MECHANISM_SIDE_FMT = "black|white"
    A2_MECHANISM_MOVE_NOUN = "落子"
    A2_RANDOM_EFFECT_DOC = "指定方接下来N步随机选择合法落子点"
    A2_MOVE_LIMITS_TITLE = "每回合落子数限制"
    A2_MOVE_LIMITS_EFFECT = "指定方每回合可以落N子"
    A2_AGGRESSIVE_DESC = "激进进攻型（重视行动力，激进夹吃）"
    A2_DEFENSIVE_DESC = "保守防守型（重视角点控制，棋子数领先）"
    C_MODIFY_EXAMPLE = "黑方的disc"
    C_PROMPT_PATCH_EXAMPLE = '[{"op": "replace", "path": "/pieces/disc/moves/0/max", "value": 8}]'
    CP_BOARD_DESC = "- 棋盘尺寸: {w} x {h}"
    CP_PREDEFINED_TYPES = {"disc"}
    CP_PRIMITIVES = ("jump", "ray", "flip")
    CP_PRIMITIVES_DESC = "jump/ray/flip"
    CP_PRIMITIVES_PHRASE = "jump/ray/flip 三原语"
    CP_DEFAULT_BOUNDS = (8, 8)
    RULE_CHANGE_CONFIG_NAMES = '("pieces", "pieces_black", "pieces_white", "pieces_red")'

    def __init__(self, api_key: str = ""):
        # 公共基础设施（logger/token_stats/base_url 等）由基类初始化
        super().__init__(base_dir=BASE_DIR, api_key=api_key)

    def _get_board_summary(self, board: dict) -> str:
        """生成黑白棋棋盘摘要（8×8 矩阵 + 棋子统计 + 合法落子点 + 游戏状态 + 机制）

        通过 self.rule_engine.get_valid_placements(side, board_state) 获取合法落子点；
        如果 rule_engine 不可用，使用内置 fallback（遍历空格沿 8 方向检查能否夹吃）。
        """
        pieces = board.get("pieces", [])
        alive = [p for p in pieces if p.get("is_alive", True)]

        board_geo = board.get("board", {})
        width = int(board_geo.get("width", 8))
        height = int(board_geo.get("height", 8))

        # 构建棋盘矩阵：●=黑棋 ○=白棋 .=空格
        grid = [["." for _ in range(width)] for _ in range(height)]
        black_count = 0
        white_count = 0
        for p in alive:
            pos = p.get("position")
            if not pos or len(pos) != 2:
                continue
            x, y = pos
            if not (0 <= x < width and 0 <= y < height):
                continue
            side = p.get("side")
            if side == "black":
                grid[y][x] = "●"
                black_count += 1
            elif side == "white":
                grid[y][x] = "○"
                white_count += 1
            elif side == "red":
                # 兼容 red：视为黑方
                grid[y][x] = "●"
                black_count += 1

        current_turn = board.get("current_turn", "black")
        turn_label = {"black": "黑方", "white": "白方", "red": "黑方"}.get(
            current_turn, current_turn
        )

        # 合法落子点
        valid_placements = self._compute_valid_placements(
            current_turn, board, width, height
        )

        # 游戏状态
        game_status = board.get("game_status", {})
        state = game_status.get("state", "playing")

        # 激活机制
        mechanisms = board.get("mechanisms", {})
        active_mechs = []
        if isinstance(mechanisms, dict):
            for mtype, mlist in mechanisms.items():
                if isinstance(mlist, list) and mlist:
                    active_mechs.append(f"{mtype}:{len(mlist)}")
        mech_str = "、".join(active_mechs) if active_mechs else "无"

        # 拼装
        header = f"当前回合：{turn_label}"
        board_title = f"棋盘状态（{width}×{height}，●=黑棋 ○=白棋 .=空格）："
        col_header = "  " + " ".join(str(i) for i in range(width))
        rows = []
        for y in range(height):
            rows.append(f"{y} " + " ".join(grid[y][x] for x in range(width)))
        board_str = "\n".join([col_header] + rows)
        counts_str = f"棋子统计：黑方 {black_count} 子，白方 {white_count} 子"
        if valid_placements:
            placements_str = "合法落子点（当前方{}）：".format(turn_label) + " ".join(
                "[{},{}]".format(p[0], p[1]) for p in valid_placements
            )
        else:
            placements_str = "合法落子点（当前方{}）：无".format(turn_label)
        state_str = f"游戏状态：{state}"
        mech_line = f"激活机制：{mech_str}"

        return "\n".join(
            [header, board_title, board_str, "", counts_str, placements_str, state_str, mech_line]
        )

    def _compute_valid_placements(
        self, side: str, board_state: dict, width: int, height: int
    ) -> List[List[int]]:
        """获取当前方的合法落子点（优先用 rule_engine，不可用则内置 fallback）"""
        re_ = getattr(self, "rule_engine", None)
        if re_ is not None:
            try:
                placements = re_.get_valid_placements(side, board_state)
                if placements is not None:
                    return placements
            except Exception:
                pass
        return self._fallback_valid_placements(side, board_state, width, height)

    def _fallback_valid_placements(
        self, side: str, board_state: dict, width: int, height: int
    ) -> List[List[int]]:
        """内置 fallback：遍历所有空格，对每个空格沿 8 方向检查能否夹吃至少 1 颗敌方棋子"""
        me = "black" if side in ("black", "red") else "white"
        enemy = "white" if me == "black" else "black"

        # 构建位置→阵营映射（red 归一化为 black）
        pos_map = {}
        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True):
                continue
            pos = p.get("position")
            if not pos or len(pos) != 2:
                continue
            s = p.get("side")
            s = "black" if s in ("black", "red") else ("white" if s == "white" else s)
            pos_map[(pos[0], pos[1])] = s

        directions = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)]
        placements: List[List[int]] = []
        seen = set()

        for y in range(height):
            for x in range(width):
                if (x, y) in pos_map:
                    continue  # 非空格，不可落子
                if (x, y) in seen:
                    continue
                # 沿 8 方向检查能否夹吃
                for dx, dy in directions:
                    nx, ny = x + dx, y + dy
                    has_enemy = False
                    while 0 <= nx < width and 0 <= ny < height:
                        cell = pos_map.get((nx, ny))
                        if cell is None:
                            break  # 遇到空格，此方向无法夹吃
                        if cell == me:
                            if has_enemy:
                                seen.add((x, y))
                                placements.append([x, y])
                            break
                        # cell == enemy
                        has_enemy = True
                        nx += dx
                        ny += dy
                    if (x, y) in seen:
                        break

        return placements

    async def _parse_intent_with_log(
        self, command: str, context: Dict[str, Any]
    ) -> Tuple[Optional[dict], float, str]:
        """意图解析（带日志）"""
        configs = context.get("configs", {})
        board = configs.get("board_state", {})
        pieces_summary = self._get_board_summary(board)
        active_rules = board.get("game_status", {}).get("custom_rules_active", [])

        extra = ""
        if active_rules:
            extra = f"\n- 已激活的自定义规则：{', '.join(active_rules)}"

        user_prompt = f"""当前游戏状态：
{pieces_summary}{extra}

玩家输入："{command}"

请分析并输出JSON。"""

        resp, elapsed = await self._call_deepseek(
            self._get_prompt("INTENT_PARSER_SYSTEM"), user_prompt, temperature=0.3
        )

        return self._extract_json(resp), elapsed, resp

    def _validate_board(
        self, new_board: dict, old_board: dict, action_type: str = "unknown"
    ) -> Tuple[bool, str]:
        """棋盘校验（棋类无关：仅校验棋子核心属性完整性，几何由 schema 依据 board_state.board 适配）

        黑白棋为 8×8（width=8, height=8，无九宫格/河界），几何校验由 validate_board_state
        依据 board_state.board.width/height 完成，此处不包含任何九宫格/河界相关校验。
        """
        valid, err = validate_board_state(new_board)
        if not valid:
            return False, err

        old_pieces = old_board.get("pieces", [])
        new_pieces = new_board.get("pieces", [])

        old_piece_map = {p["id"]: p for p in old_pieces}
        new_piece_map = {p["id"]: p for p in new_pieces}

        core_attrs = ["id", "type", "side", "name"]

        if action_type == "add":
            old_alive_ids = [p["id"] for p in old_pieces if p.get("is_alive", True)]
            missing = [pid for pid in old_alive_ids if pid not in new_piece_map]
            if missing:
                return False, f"棋子丢失: {', '.join(missing)}"

            for pid in old_alive_ids:
                old_p = old_piece_map[pid]
                new_p = new_piece_map[pid]
                for attr in core_attrs:
                    if old_p.get(attr) != new_p.get(attr):
                        return False, f"棋子属性被篡改: {pid}的{attr}从{old_p.get(attr)}变为{new_p.get(attr)}"

            old_count = len(old_alive_ids)
            new_count = len([p for p in new_pieces if p.get("is_alive", True)])
            if new_count < old_count:
                return False, f"棋子数量减少: 原{old_count}个，现{new_count}个"

        elif action_type == "remove":
            old_ids = [p["id"] for p in old_pieces]
            missing = [pid for pid in old_ids if pid not in new_piece_map]
            if missing:
                return False, f"棋子丢失: {', '.join(missing)}"

            for pid in old_ids:
                old_p = old_piece_map[pid]
                new_p = new_piece_map[pid]
                for attr in core_attrs:
                    if old_p.get(attr) != new_p.get(attr):
                        return False, f"棋子属性被篡改: {pid}的{attr}从{old_p.get(attr)}变为{new_p.get(attr)}"

        elif action_type == "move":
            old_ids = [p["id"] for p in old_pieces]
            missing = [pid for pid in old_ids if pid not in new_piece_map]
            if missing:
                return False, f"棋子丢失: {', '.join(missing)}"

            for pid in old_ids:
                old_p = old_piece_map[pid]
                new_p = new_piece_map[pid]
                for attr in core_attrs:
                    if old_p.get(attr) != new_p.get(attr):
                        return False, f"棋子属性被篡改: {pid}的{attr}从{old_p.get(attr)}变为{new_p.get(attr)}"

        elif action_type == "rotate":
            old_ids = [p["id"] for p in old_pieces]
            missing = [pid for pid in old_ids if pid not in new_piece_map]
            if missing:
                return False, f"棋子丢失: {', '.join(missing)}"

            for pid in old_ids:
                old_p = old_piece_map[pid]
                new_p = new_piece_map[pid]
                for attr in core_attrs:
                    if old_p.get(attr) != new_p.get(attr):
                        return False, f"棋子属性被篡改: {pid}的{attr}从{old_p.get(attr)}变为{new_p.get(attr)}"

        elif action_type == "modify":
            old_ids = [p["id"] for p in old_pieces]
            missing = [pid for pid in old_ids if pid not in new_piece_map]
            if missing:
                return False, f"棋子丢失: {', '.join(missing)}"

        elif action_type == "transform":
            old_ids = [p["id"] for p in old_pieces]
            missing = [pid for pid in old_ids if pid not in new_piece_map]
            if missing:
                return False, f"棋子丢失: {', '.join(missing)}"

            old_count = len([p for p in old_pieces if p.get("is_alive", True)])
            new_count = len([p for p in new_pieces if p.get("is_alive", True)])
            if old_count != new_count:
                return False, f"棋子数量变化: 原{old_count}个，现{new_count}个"

            for pid in old_ids:
                old_p = old_piece_map[pid]
                new_p = new_piece_map[pid]
                if old_p.get("id") != new_p.get("id"):
                    return False, f"棋子ID被篡改: {pid}的id从{old_p.get('id')}变为{new_p.get('id')}"
                if old_p.get("side") != new_p.get("side"):
                    return False, f"棋子阵营被篡改: {pid}的side从{old_p.get('side')}变为{new_p.get('side')}"
                if old_p.get("position") != new_p.get("position"):
                    return False, f"棋子位置被改变（transform只改变类型，不改变位置）: {pid}"
                if old_p.get("is_alive") != new_p.get("is_alive"):
                    return False, f"棋子存活状态被改变: {pid}"

        elif action_type == "unknown":
            old_ids = [p["id"] for p in old_pieces]
            missing = [pid for pid in old_ids if pid not in new_piece_map]
            if missing:
                return False, f"棋子丢失: {', '.join(missing)}"

            for pid in old_ids:
                old_p = old_piece_map[pid]
                new_p = new_piece_map[pid]
                if old_p.get("id") != new_p.get("id"):
                    return False, f"棋子ID被篡改: {pid}的id从{old_p.get('id')}变为{new_p.get('id')}"
                if old_p.get("side") != new_p.get("side"):
                    return False, f"棋子阵营被篡改: {pid}的side从{old_p.get('side')}变为{new_p.get('side')}"

        return True, ""

    def _validate_rules(self, rules: dict) -> Tuple[bool, str]:
        """验证棋子配置（胜利条件由 schema 依据 rules.win_conditions 校验，
        适配黑白棋 most_discs/no_valid_moves_both/board_full；无 checkmate/general_captured 相关校验）"""
        return validate_pieces(rules)

    def _validate_rule_change(self, old_rules: dict, new_rules: dict, target_type: str = None) -> Tuple[bool, str]:
        """
        检测黑白棋规则是否发生了实质性变化，并验证结构完整性。

        黑白棋的 pieces 配置结构（pieces_black.json / pieces_white.json）：
            { "_metadata": {...}, "side": "black|white", "pieces": { "disc": {...} }, "custom_pieces": [] }
        不含象棋的 side_overrides 字段，故此处不再校验 side_overrides。

        move.kind 合法值：jump / ray / flip（黑白棋核心原语）。

        Args:
            old_rules: 修改前的规则
            new_rules: 修改后的规则
            target_type: 目标棋子类型（可选，如果指定则只检查该类型）

        Returns:
            (valid, message) - valid=True表示规则发生了变化且结构完整，valid=False表示规则未变化或结构不完整
        """
        # 结构完整性：必须有 pieces 字段且为对象
        if not isinstance(new_rules, dict):
            return False, "规则必须是对象类型"
        new_pieces = new_rules.get("pieces")
        if not isinstance(new_pieces, dict):
            return False, "缺少 pieces 字段或类型错误，该字段必须保留"

        # 校验 move.kind 合法（jump/ray/flip）
        valid_kinds = {"jump", "ray", "flip"}

        def _check_moves(owner_label, moves):
            for mv in moves or []:
                kind = mv.get("kind", "")
                if kind not in valid_kinds:
                    return f"{owner_label} 的 move.kind '{kind}' 不是合法值（合法值: jump/ray/flip）"
            return None

        for ptype, pconfig in new_pieces.items():
            err = _check_moves(f"棋子 {ptype}", pconfig.get("moves", []))
            if err:
                return False, err
        for cp in new_rules.get("custom_pieces", []) or []:
            err = _check_moves(f"自定义棋子 {cp.get('type')}", cp.get("moves", []))
            if err:
                return False, err

        # 实质性变化检查
        old_pieces_dict = old_rules.get("pieces", {}) if isinstance(old_rules, dict) else {}
        new_pieces_dict = new_rules.get("pieces", {})
        old_custom = old_rules.get("custom_pieces", []) if isinstance(old_rules, dict) else []
        new_custom = new_rules.get("custom_pieces", []) or []

        if target_type:
            old_rule = old_pieces_dict.get(target_type, {})
            new_rule = new_pieces_dict.get(target_type, {})

            old_movement = old_rule.get("moves", [])
            new_movement = new_rule.get("moves", [])

            old_cp = [c for c in old_custom if c.get("type") == target_type]
            new_cp = [c for c in new_custom if c.get("type") == target_type]

            if old_movement == new_movement and old_cp == new_cp:
                return False, f"规则 {target_type} 未发生实质性变化"

            return True, f"规则 {target_type} 发生变化"
        else:
            if old_pieces_dict != new_pieces_dict:
                return True, "规则发生变化"

            if old_custom != new_custom:
                return True, "自定义棋子规则发生变化"

            return False, "规则未发生实质性变化"

    def _a1_undo_step(self, board: dict, last: dict) -> None:
        """黑白棋翻转撤销：恢复被翻转 side、删除新增 disc、按历史恢复回合。"""
        # 1) 恢复被翻转棋子的 side
        for flip in last.get("flipped", []) or []:
            fp_id = flip.get("id") or flip.get("piece_id")
            if not fp_id:
                continue
            fp = self._find_piece(board, fp_id)
            if fp:
                orig = flip.get("from_side") or flip.get("original_side") or flip.get("prev_side")
                if orig:
                    fp["side"] = orig

        # 2) 删除本次落子新增的 disc（若历史记录了被吃棋子，也一并恢复）
        placed_id = last.get("piece_id") or last.get("placed_id") or last.get("moved_piece_id")
        if placed_id:
            # 若记录了 from（来源位置），说明是移动而非新增 → 复位位置
            from_pos = last.get("from") or last.get("from_position")
            placed_piece = self._find_piece(board, placed_id)
            if from_pos and placed_piece:
                placed_piece["position"] = from_pos
            else:
                # 黑白棋标准落子是新增 disc → 从 pieces 中移除
                board["pieces"] = [
                    p for p in board.get("pieces", []) if p.get("id") != placed_id
                ]

        # 3) 恢复被吃棋子（兼容一般 move_history 的 captured 字段）
        captured_id = last.get("captured")
        if captured_id:
            cap = self._find_piece(board, captured_id)
            if cap:
                cap["is_alive"] = True

        # 4) 切换回合（黑白棋 black/white 交替；red 视为 black）
        prev_turn = last.get("previous_turn") or last.get("from_turn")
        if prev_turn:
            board["current_turn"] = prev_turn
        else:
            cur = board.get("current_turn", "black")
            if cur == "black":
                board["current_turn"] = "white"
            elif cur == "white":
                board["current_turn"] = "black"
            elif cur == "red":
                board["current_turn"] = "black"
            else:
                board["current_turn"] = "black"

    def _a1_undo_post(self, board: dict) -> None:
        """悔棋后游戏回到进行中。"""
        gs = board.setdefault("game_status", {})
        if gs.get("state") == "ended":
            gs["state"] = "playing"
            gs["winner"] = None
            gs["reason"] = None

    def _a1_winner_message(self, winner: str, intent: dict) -> str:
        """黑白棋 set_winner 消息：支持 draw 与阵营中文标签。"""
        if winner == "draw":
            return intent.get("response_to_player", "已设置为平局")
        label = {"black": "黑方", "white": "白方", "red": "黑方"}.get(winner, winner)
        return intent.get("response_to_player", f"已设置{label}获胜")
