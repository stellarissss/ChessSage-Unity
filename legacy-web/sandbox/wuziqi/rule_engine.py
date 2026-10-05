"""
五子棋规则引擎 - 根据JSON配置计算合法移动
核心设计：支持五连珠胜利检测 + 棋子落子逻辑
"""
import copy
from typing import Dict, Any, List, Tuple, Optional


class RuleEngine:
    """规则引擎，根据pieces_red.json和pieces_black.json计算棋子的合法移动"""

    def __init__(self, board: dict, pieces_red: dict, pieces_black: dict, rules: dict):
        self.board_config = board
        self.rules = rules

        self._pieces_by_side = {
            "red": pieces_red.get("pieces", {}),
            "black": pieces_black.get("pieces", {}),
        }
        self._custom_pieces_by_side = {
            "red": pieces_red.get("custom_pieces", []),
            "black": pieces_black.get("custom_pieces", []),
        }

    def get_valid_moves(
        self, piece: dict, board_state: dict
    ) -> List[List[int]]:
        """
        计算棋子的所有合法移动位置

        Args:
            piece: 棋子对象 {id, type, name, side, position, is_alive}
            board_state: 棋盘状态 {pieces, current_turn, ...}

        Returns:
            合法位置列表 [[x, y], ...]
        """
        piece_type = piece.get("type")
        if not piece_type:
            return []

        move_defs = self._get_move_definitions(piece_type, piece.get("side"))
        if not move_defs:
            return []

        px, py = piece["position"]
        all_moves = []

        for move_def in move_defs:
            expanded = self._expand_symmetry(move_def)
            for exp_def in expanded:
                moves = self._execute_move_def(exp_def, piece, board_state)
                all_moves.extend(moves)

        return list({tuple(m): m for m in all_moves}.values())

    def _get_move_definitions(self, piece_type: str, side: str) -> List[dict]:
        """获取棋子的移动定义"""
        side_pieces = self._pieces_by_side.get(side, {})
        base_config = side_pieces.get(piece_type)
        if not base_config:
            for cp in self._custom_pieces_by_side.get(side, []):
                if cp.get("type") == piece_type:
                    base_config = cp
                    break
        if not base_config:
            return []

        return base_config.get("moves", [])

    def _expand_symmetry(self, move_def: dict) -> List[dict]:
        """展开对称定义"""
        sym = move_def.get("sym", "none")
        kind = move_def.get("kind")
        result = []

        if kind == "jump":
            to = move_def.get("to")
            if isinstance(to, dict) and to.get("mode") == "region":
                return [{**move_def}]
            block = move_def.get("block", [])

            if sym == "rotate4":
                dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)]
                for dx, dy in dirs:
                    new_to = [to[0] * dx - to[1] * dy, to[0] * dy + to[1] * dx]
                    new_block = [[b[0] * dx - b[1] * dy, b[0] * dy + b[1] * dx] for b in block]
                    result.append({**move_def, "to": new_to, "block": new_block})

            elif sym == "rotate4_mirror":
                dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)]
                for dx, dy in dirs:
                    new_to = [to[0] * dx - to[1] * dy, to[0] * dy + to[1] * dx]
                    new_block = [[b[0] * dx - b[1] * dy, b[0] * dy + b[1] * dx] for b in block]
                    result.append({**move_def, "to": new_to, "block": new_block})
                    new_to_mirror = [new_to[0], -new_to[1]]
                    new_block_mirror = [[b[0], -b[1]] for b in new_block]
                    result.append({**move_def, "to": new_to_mirror, "block": new_block_mirror})

            elif sym == "mirror_x":
                result.append({**move_def})
                result.append({**move_def, "to": [-to[0], to[1]], "block": [[-b[0], b[1]] for b in block]})

            else:
                result.append({**move_def})

        elif kind == "ray":
            direction = move_def.get("dir")

            if sym == "rotate4":
                dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)]
                for dx, dy in dirs:
                    new_dir = [direction[0] * dx - direction[1] * dy, direction[0] * dy + direction[1] * dx]
                    result.append({**move_def, "dir": new_dir})

            elif sym == "rotate4_mirror":
                dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)]
                for dx, dy in dirs:
                    new_dir = [direction[0] * dx - direction[1] * dy, direction[0] * dy + direction[1] * dx]
                    result.append({**move_def, "dir": new_dir})
                    result.append({**move_def, "dir": [new_dir[0], -new_dir[1]]})

            elif sym == "mirror_x":
                result.append({**move_def})
                result.append({**move_def, "dir": [-direction[0], direction[1]]})

            else:
                result.append({**move_def})

        return result

    def _execute_move_def(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """执行单个移动定义"""
        kind = move_def.get("kind")

        if kind == "jump":
            return self._jump_moves(move_def, piece, board_state)
        elif kind == "ray":
            return self._ray_moves(move_def, piece, board_state)

        return []

    def _jump_moves(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """执行跳跃移动"""
        px, py = piece["position"]
        to = move_def.get("to")
        block = move_def.get("block", [])
        land = move_def.get("land", "any")
        where = move_def.get("where", [])

        if isinstance(to, dict) and to.get("mode") == "region":
            return self._jump_region_moves(move_def, piece, board_state)

        if to == "$forward":
            direction = -1 if piece["side"] == "red" else 1
            to = [0, direction]

        nx, ny = px + to[0], py + to[1]

        if not self._in_bounds(nx, ny):
            return []

        for bx, by in block:
            if self._get_piece_at([px + bx, py + by], board_state):
                return []

        target = self._get_piece_at([nx, ny], board_state)

        if land == "empty" and target:
            return []
        if land == "enemy" and (not target or target["side"] == piece["side"]):
            return []
        if land == "any" and target and target["side"] == piece["side"]:
            return []

        if where and not self._eval_where(where, piece, board_state, [nx, ny]):
            return []

        return [[nx, ny]]

    def _jump_region_moves(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """区域目标跳跃"""
        px, py = piece["position"]
        region_ref = move_def["to"]["region"]
        land = move_def.get("land", "any")
        where = move_def.get("where", [])

        moves = []
        width = self._get_width()
        height = self._get_height()

        for nx in range(width):
            for ny in range(height):
                if nx == px and ny == py:
                    continue
                if not self._is_in_region([nx, ny], region_ref, piece["side"]):
                    continue
                target = self._get_piece_at([nx, ny], board_state)
                if land == "empty" and target:
                    continue
                if land == "enemy" and (not target or target["side"] == piece["side"]):
                    continue
                if land == "any" and target and target["side"] == piece["side"]:
                    continue
                if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                    continue
                moves.append([nx, ny])

        return moves

    def _ray_moves(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """执行射线移动"""
        px, py = piece["position"]
        direction = move_def.get("dir")
        max_dist = move_def.get("max", -1)
        screens = move_def.get("screens", 0)
        land = move_def.get("land", "any")
        where = move_def.get("where", [])

        if max_dist == -1:
            max_dist = max(self._get_width(), self._get_height())

        moves = []
        platforms_found = 0

        for step in range(1, max_dist + 1):
            nx, ny = px + direction[0] * step, py + direction[1] * step

            if not self._in_bounds(nx, ny):
                break

            target = self._get_piece_at([nx, ny], board_state)

            if platforms_found < screens:
                if target:
                    platforms_found += 1
                elif land == "empty":
                    if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                        continue
                    moves.append([nx, ny])
            else:
                if target:
                    if land in ("any", "enemy") and target["side"] != piece["side"]:
                        if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                            continue
                        moves.append([nx, ny])
                    break
                elif land in ("any", "empty"):
                    if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                        continue
                    moves.append([nx, ny])

        return moves

    def _eval_where(self, conditions: List[dict], piece: dict, board_state: dict, dest: List[int]) -> bool:
        """求值 where 条件表达式"""
        for cond in conditions:
            if not self._eval_condition(cond, piece, board_state, dest):
                return False
        return True

    def _eval_condition(self, cond: dict, piece: dict, board_state: dict, dest: List[int]) -> bool:
        """求值单个条件"""
        if not isinstance(cond, dict) or len(cond) != 1:
            return True

        key = list(cond.keys())[0]
        value = cond[key]

        if key == "not":
            return not self._eval_condition(value, piece, board_state, dest)
        if key == "and":
            return all(self._eval_condition(sub, piece, board_state, dest) for sub in value)
        if key == "or":
            return any(self._eval_condition(sub, piece, board_state, dest) for sub in value)

        def _resolve_pos(val, default="$dest"):
            if isinstance(val, dict):
                pos_var = val.get("pos", default)
            elif isinstance(val, str):
                pos_var = val
            else:
                pos_var = default
            return piece["position"] if pos_var == "$self" else dest

        if key == "in_region":
            if isinstance(value, dict):
                region_name = value.get("region", "$full_board")
            else:
                region_name = value
            pos = _resolve_pos(value, "$dest")
            region_name = self._resolve_var(region_name, piece)
            return self._is_in_region(pos, region_name, piece["side"])

        if key == "at_row":
            if isinstance(value, dict):
                pos = _resolve_pos(value, "$dest")
                row = value.get("row", -1)
            else:
                pos = dest
                row = value
            return pos[1] == row

        if key == "at_col":
            if isinstance(value, dict):
                pos = _resolve_pos(value, "$dest")
                col = value.get("col", -1)
            else:
                pos = dest
                col = value
            return pos[0] == col

        return True

    def _resolve_var(self, var: str, piece: dict) -> str:
        """解析变量引用"""
        if var == "$side":
            return piece["side"]
        return var

    def _is_in_region(self, pos: List[int], region_ref: str, side: str) -> bool:
        """判断位置是否在指定区域"""
        if region_ref == "$full_board":
            return True

        geometry = self.board_config.get("geometry", {})
        regions = geometry.get("regions", {})
        region = regions.get(region_ref)

        if not region:
            return True

        x, y = pos
        if "x_range" in region:
            rx_start, rx_end = region["x_range"]
            if not (rx_start <= x <= rx_end):
                return False
        if "y_range" in region:
            ry_start, ry_end = region["y_range"]
            if not (ry_start <= y <= ry_end):
                return False

        return True

    def _in_bounds(self, x: int, y: int) -> bool:
        """判断是否在棋盘范围内"""
        geometry = self.board_config.get("geometry", {})
        width = geometry.get("width", 15)
        height = geometry.get("height", 15)
        return 0 <= x < width and 0 <= y < height

    def _get_width(self) -> int:
        """获取棋盘宽度"""
        return self.board_config.get("geometry", {}).get("width", 15)

    def _get_height(self) -> int:
        """获取棋盘高度"""
        return self.board_config.get("geometry", {}).get("height", 15)

    def _get_piece_at(
        self, pos: List[int], board_state: dict
    ) -> Optional[dict]:
        """获取指定位置的棋子"""
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p["position"][0] == pos[0] and p["position"][1] == pos[1]:
                return p
        return None

    def check_five_in_a_row(self, board_state: dict) -> Optional[str]:
        """检查是否有五连珠"""
        geometry = self.board_config.get("geometry", {})
        width = geometry.get("width", 15)
        height = geometry.get("height", 15)

        pieces = board_state.get("pieces", [])
        board = {}
        for p in pieces:
            if p.get("is_alive", True):
                board[(p["position"][0], p["position"][1])] = p["side"]

        directions = [
            (1, 0),   # 水平
            (0, 1),   # 垂直
            (1, 1),   # 对角线 \
            (1, -1),  # 对角线 /
        ]

        for x in range(width):
            for y in range(height):
                side = board.get((x, y))
                if not side:
                    continue

                for dx, dy in directions:
                    count = 1
                    nx, ny = x + dx, y + dy
                    while 0 <= nx < width and 0 <= ny < height and board.get((nx, ny)) == side:
                        count += 1
                        nx += dx
                        ny += dy
                    nx, ny = x - dx, y - dy
                    while 0 <= nx < width and 0 <= ny < height and board.get((nx, ny)) == side:
                        count += 1
                        nx -= dx
                        ny -= dy

                    if count >= 5:
                        return side

        return None

    def is_game_over(self, board_state: dict) -> Optional[str]:
        """检查游戏是否结束"""
        winner = self.check_five_in_a_row(board_state)
        if winner:
            return winner

        geometry = self.board_config.get("geometry", {})
        width = geometry.get("width", 15)
        height = geometry.get("height", 15)
        total_cells = width * height

        pieces = board_state.get("pieces", [])
        alive_pieces = sum(1 for p in pieces if p.get("is_alive", True))

        if alive_pieces >= total_cells:
            return "draw"

        return None

    def is_in_check(self, side: str, board_state: dict) -> bool:
        """五子棋无将军概念，返回False"""
        return False

    def is_checkmate(self, side: str, board_state: dict) -> bool:
        """五子棋无将死概念，返回False"""
        return False

    def is_general_captured(self, board_state: dict) -> Optional[str]:
        """五子棋无将帅概念，使用五连珠检测"""
        return self.check_five_in_a_row(board_state)