"""
象棋规则引擎 v2.0 - 根据JSON配置计算合法移动
核心设计：jump + ray 双原子移动体系 + 条件表达式引擎
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

        self._king_types = {"general"}
        for side in ["red", "black"]:
            for cp in self._custom_pieces_by_side[side]:
                if cp.get("is_king"):
                    cp_type = cp.get("type")
                    if cp_type:
                        self._king_types.add(cp_type)

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

        # 依据 can_capture / eatable 修饰器过滤吃子着法
        all_moves = self._restrict_captures(piece, board_state, all_moves)

        return list({tuple(m): m for m in all_moves}.values())

    def _get_move_definitions(self, piece_type: str, side: str) -> List[dict]:
        """获取棋子的移动定义（直接从对应阵营的规则中查找）"""
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

    def _get_piece_config(self, piece_type: str, side: str) -> Optional[dict]:
        """获取某类型棋子的配置对象（阵营类型表或自定义棋子表中的条目）"""
        side_pieces = self._pieces_by_side.get(side, {})
        base_config = side_pieces.get(piece_type)
        if base_config is None:
            for cp in self._custom_pieces_by_side.get(side, []):
                if cp.get("type") == piece_type:
                    base_config = cp
                    break
        return base_config

    def _get_rules_type_modifiers(self) -> dict:
        """读取 rules.json 中的按类型修饰器映射（支持 type_modifiers 或 modifiers.type_modifiers）"""
        rules = self.rules or {}
        tm = rules.get("type_modifiers")
        if not isinstance(tm, dict):
            tm = (rules.get("modifiers") or {}).get("type_modifiers")
        return tm if isinstance(tm, dict) else {}

    def resolve_modifiers(self, piece: dict) -> Dict[str, Any]:
        """合并某棋子的有效修饰器

        优先级（低→高）：rules 全局类型映射 → rules 按阵营类型映射 →
        阵营类型配置(pieces_*.json / custom_pieces) → 棋子实例字段
        """
        out: Dict[str, Any] = {}
        ptype = piece.get("type")
        side = piece.get("side")

        # 1. rules 级 type_modifiers（先全局后按阵营细分）
        tm = self._get_rules_type_modifiers()
        type_map_global = tm.get(ptype)
        if isinstance(type_map_global, dict):
            out.update(type_map_global)
        side_map = tm.get(side)
        if isinstance(side_map, dict):
            type_map_side = side_map.get(ptype)
            if isinstance(type_map_side, dict):
                out.update(type_map_side)

        # 2. 阵营类型配置：类型条目（pieces）或自定义棋子条目
        base_config = self._get_piece_config(ptype, side)
        if base_config:
            cfg_mods = base_config.get("modifiers")
            if isinstance(cfg_mods, dict):
                out.update(cfg_mods)
            for attr in ("can_capture", "eatable", "invulnerable"):
                if attr in base_config:
                    out[attr] = base_config[attr]

        # 3. 棋子实例字段（对象自身或 custom_properties）优先级最高
        inst_mods = piece.get("custom_properties") or {}
        for attr in ("can_capture", "eatable", "invulnerable"):
            if attr in piece and piece[attr] is not None:
                out[attr] = piece[attr]
            elif attr in inst_mods:
                out[attr] = inst_mods[attr]

        return out

    def get_effective_modifier(self, piece: dict, attr: str, default: Any = None) -> Any:
        """获取某棋子在指定修饰字段上的有效值"""
        return self.resolve_modifiers(piece).get(attr, default)

    def is_target_eatable(self, target: dict) -> bool:
        """目标棋子是否可被吃掉（eatable=false 或 invulnerable=true 则该棋子无敌）"""
        if self.get_effective_modifier(target, "eatable", True) is False:
            return False
        if self.get_effective_modifier(target, "invulnerable", False) is True:
            return False
        return True

    def is_invulnerable(self, target: dict) -> bool:
        """目标棋子是否无敌 / 无法被吃"""
        return not self.is_target_eatable(target)

    def can_capture_piece(self, attacker: dict, target: dict) -> bool:
        """进攻方是否允许吃掉目标（同时受进攻方 can_capture 与目标可吃性约束）"""
        if self.get_effective_modifier(attacker, "can_capture", True) is False:
            return False
        return self.is_target_eatable(target)

    def _restrict_captures(
        self, piece: dict, board_state: dict, moves: List[List[int]]
    ) -> List[List[int]]:
        """按 can_capture / eatable 修饰器过滤吃子着法，非吃子（空格）着法不受影响"""
        attacker_can_capture = self.get_effective_modifier(piece, "can_capture", True)
        result = []
        for m in moves:
            target = self._get_piece_at(m, board_state)
            if target is not None and target["side"] != piece["side"]:
                # 落点为敌方棋子（吃子）：需同时满足进攻方可吃 与 目标可被吃
                if not attacker_can_capture:
                    continue
                if not self.is_target_eatable(target):
                    continue
            result.append(m)
        return result

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
                    # 镜像：对 y 分量取反
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
        """区域目标跳跃：可移动到指定区域内的任意格子（瞬移）"""
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
        """求值单个条件

        条件格式：{"operator": params}
        - params 为字符串时是简写形式
        - params 为字典时包含具名参数（如 pos, region, row, col）
        """
        if not isinstance(cond, dict) or len(cond) != 1:
            return True

        key = list(cond.keys())[0]
        value = cond[key]

        # 逻辑操作符
        if key == "not":
            return not self._eval_condition(value, piece, board_state, dest)
        if key == "and":
            return all(self._eval_condition(sub, piece, board_state, dest) for sub in value)
        if key == "or":
            return any(self._eval_condition(sub, piece, board_state, dest) for sub in value)

        # 解析位置参数：支持简写（字符串）和完整格式（dict 含 pos）
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
                region_name = value.get("region", "$palace")
            else:
                region_name = value
            pos = _resolve_pos(value, "$dest")
            region_name = self._resolve_var(region_name, piece)
            return self._is_in_region(pos, region_name, piece["side"])

        if key == "crossed_river":
            pos = _resolve_pos(value, "$dest")
            return self._crossed_river(pos[1], piece["side"])

        if key == "same_side":
            pos = _resolve_pos(value, "$dest")
            return self._is_own_side(pos[1], piece["side"])

        # 行列判断
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
        if var == "$palace":
            return "$palace"
        if var == "$side":
            return piece["side"]
        return var

    def _is_in_region(self, pos: List[int], region_ref: str, side: str) -> bool:
        """判断位置是否在指定区域"""
        if region_ref == "$full_board":
            return True
        if region_ref == "$palace":
            return self._in_palace(pos[0], pos[1], side)

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

    def _in_palace(self, x: int, y: int, side: str) -> bool:
        """判断是否在九宫格内"""
        geometry = self.board_config.get("geometry", {})
        palace = geometry.get("palace", {})
        side_palace = palace.get(side, {})

        if side_palace:
            tl = side_palace.get("top_left", [0, 0])
            br = side_palace.get("bottom_right", [0, 0])
            return tl[0] <= x <= br[0] and tl[1] <= y <= br[1]

        if x < 3 or x > 5:
            return False
        if side == "red":
            return 7 <= y <= 9
        else:
            return 0 <= y <= 2

    def _crossed_river(self, y: int, side: str) -> bool:
        """判断是否已过河"""
        geometry = self.board_config.get("geometry", {})
        river_line = geometry.get("river_line", 5)
        if side == "red":
            return y < river_line
        else:
            return y >= river_line

    def _is_own_side(self, y: int, side: str) -> bool:
        """判断是否在自己一侧"""
        geometry = self.board_config.get("geometry", {})
        river_line = geometry.get("river_line", 5)
        if side == "red":
            return y >= river_line
        else:
            return y < river_line

    def _in_bounds(self, x: int, y: int) -> bool:
        """判断是否在棋盘范围内"""
        geometry = self.board_config.get("geometry", {})
        width = geometry.get("width", 9)
        height = geometry.get("height", 10)
        return 0 <= x < width and 0 <= y < height

    def _get_width(self) -> int:
        """获取棋盘宽度"""
        return self.board_config.get("geometry", {}).get("width", 9)

    def _get_height(self) -> int:
        """获取棋盘高度"""
        return self.board_config.get("geometry", {}).get("height", 10)

    def _pos_index(self, board_state: dict) -> Dict[Tuple[int, int], dict]:
        """位置→棋子 O(1) 查找：将 board_state 中所有活子按坐标建索引"""
        # 位置索引 O(1) 查找
        pos_idx: Dict[Tuple[int, int], dict] = {}
        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True):
                continue
            pos = p.get("position")
            if not pos:
                continue
            key = (pos[0], pos[1])
            pos_idx[key] = p
        return pos_idx

    def _get_piece_at(
        self, pos: List[int], board_state: dict
    ) -> Optional[dict]:
        """获取指定位置的棋子（位置→棋子 O(1) 查找）"""
        # 位置索引 O(1) 查找
        pos_idx = self._pos_index(board_state)
        return pos_idx.get((pos[0], pos[1]))

    def is_in_check(self, side: str, board_state: dict) -> bool:
        """检查指定方是否被将军"""
        # 位置索引 O(1) 查找：预构建一次索引复用
        pos_idx = self._pos_index(board_state)

        general = None
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p["type"] in self._king_types and p["side"] == side:
                general = p
                break

        if not general:
            return False

        gx, gy = general["position"]

        other_general = None
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p["type"] in self._king_types and p["side"] != side:
                other_general = p
                break

        if other_general and other_general["position"][0] == gx:
            og_y = other_general["position"][1]
            y_min, y_max = min(gy, og_y), max(gy, og_y)
            # 飞将中间遮挡：用索引 O(1) 逐行查询，替代 O(n) 全量扫描
            blocked = False
            for y in range(y_min + 1, y_max):
                mid = pos_idx.get((gx, y))
                if mid and mid.get("id") != general["id"] and mid.get("id") != other_general.get("id"):
                    blocked = True
                    break
            if not blocked:
                return True

        # 敌子逐枚验证是否可攻击己方将/帅（保留原循环结构）
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p["side"] != side:
                valid = self.get_valid_moves(p, board_state)
                if [gx, gy] in valid:
                    return True

        return False

    def is_checkmate(self, side: str, board_state: dict) -> bool:
        """检查是否被将死"""
        if not self.is_in_check(side, board_state):
            return False

        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p["side"] == side:
                valid = self.get_valid_moves(p, board_state)
                for move in valid:
                    old_pos = p["position"]
                    p["position"] = move
                    captured_piece = None
                    for q in board_state.get("pieces", []):
                        if q is not p and q.get("is_alive", True) and q["position"][0] == move[0] and q["position"][1] == move[1]:
                            captured_piece = q
                            # 防御：无敌棋子永远不会被吃子模拟移除
                            if not self.is_invulnerable(captured_piece):
                                captured_piece["is_alive"] = False
                            break
                    still_check = self.is_in_check(side, board_state)
                    p["position"] = old_pos
                    if captured_piece:
                        captured_piece["is_alive"] = True
                    if not still_check:
                        return False

        return True

    def is_general_captured(self, board_state: dict) -> Optional[str]:
        """检查将帅是否被吃"""
        red_alive = False
        black_alive = False
        for p in board_state.get("pieces", []):
            if p["type"] in self._king_types and p.get("is_alive", True):
                if p["side"] == "red":
                    red_alive = True
                else:
                    black_alive = True

        if not red_alive:
            return "black"
        if not black_alive:
            return "red"
        return None
