"""
动物棋规则引擎 v2.0 - 根据JSON配置计算合法移动
核心设计：jump + ray 双原子移动体系 + 条件表达式引擎
动物棋特有：等级吃子(rank) + 水域(water) + 陷阱降级(trap) + 兽穴(den) + 狮虎跳河(path_constraint)
"""
import copy
from typing import Dict, Any, List, Tuple, Optional, Set


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

        # 预计算各区域单元格集合，用于快速查表
        self._region_cells = self._build_region_cells()

    def _build_region_cells(self) -> Dict[str, Set[Tuple[int, int]]]:
        """预计算各区域的单元格集合"""
        regions = self.board_config.get("geometry", {}).get("regions", {})
        result: Dict[str, Set[Tuple[int, int]]] = {}
        for name, region in regions.items():
            cells = region.get("cells", [])
            if cells:
                result[name] = set((c[0], c[1]) for c in cells)
        return result

    # ═══════════════════════════════════════════════════════════════
    # 区域查询方法（动物棋特有）
    # ═══════════════════════════════════════════════════════════════

    def _is_in_water(self, pos: List[int]) -> bool:
        return (pos[0], pos[1]) in self._region_cells.get("water", set())

    def _is_in_trap(self, pos: List[int]) -> bool:
        """是否在任意一方的陷阱中"""
        return ((pos[0], pos[1]) in self._region_cells.get("trap_red", set())
                or (pos[0], pos[1]) in self._region_cells.get("trap_black", set()))

    def _is_in_enemy_trap(self, pos: List[int], side: str) -> bool:
        """是否在 side 的敌方陷阱中（即对方设置的陷阱）"""
        enemy_trap = "trap_black" if side == "red" else "trap_red"
        return (pos[0], pos[1]) in self._region_cells.get(enemy_trap, set())

    def _is_in_own_den(self, pos: List[int], side: str) -> bool:
        """是否在 side 自己的兽穴中"""
        own_den = "den_red" if side == "red" else "den_black"
        return (pos[0], pos[1]) in self._region_cells.get(own_den, set())

    def _is_in_enemy_den(self, pos: List[int], side: str) -> bool:
        """是否在 side 的敌方兽穴中"""
        enemy_den = "den_black" if side == "red" else "den_red"
        return (pos[0], pos[1]) in self._region_cells.get(enemy_den, set())

    def is_killed_by_trap(self, piece: dict, board_state: dict) -> bool:
        """敌方正陷于己方陷阱的动物会被吞噬：踩中敌方陷阱的动物立即死亡。

        陷阱本身也随吞噬一并消耗（一次性陷阱，记入 board_state.consumed_traps，前端随之消失）。
        返回是否被吞噬（原地修改 piece.is_alive=False）。
        """
        if not (self.rules.get("special_rules", {}).get("trap_neutralizes_rank", {}) or {}).get("enabled", True):
            return False
        pos = piece.get("position")
        side = piece.get("side")
        if not pos or not side:
            return False
        if tuple(pos) in {tuple(c) for c in board_state.get("consumed_traps", [])}:
            return False
        if self._is_in_enemy_trap(pos, side):
            piece["is_alive"] = False
            board_state.setdefault("consumed_traps", []).append(list(pos))
            return True
        return False

    # ═══════════════════════════════════════════════════════════════
    # 主入口
    # ═══════════════════════════════════════════════════════════════

    def get_valid_moves(
        self, piece: dict, board_state: dict
    ) -> List[List[int]]:
        """计算棋子的所有合法移动位置"""
        piece_type = piece.get("type")
        if not piece_type:
            return []

        move_defs = self._get_move_definitions(piece_type, piece.get("side"))
        if not move_defs:
            return []

        all_moves = []
        for move_def in move_defs:
            expanded = self._expand_symmetry(move_def)
            for exp_def in expanded:
                moves = self._execute_move_def(exp_def, piece, board_state)
                all_moves.extend(moves)

        return list({tuple(m): m for m in all_moves}.values())

    def _get_move_definitions(self, piece_type: str, side: str) -> List[dict]:
        """获取棋子的移动定义"""
        config = self._get_piece_config(piece_type, side)
        if not config:
            return []
        return config.get("moves", [])

    def _get_piece_config(self, piece_type: str, side: str) -> Optional[dict]:
        """获取棋子的完整配置（含 rank/capture/moves）"""
        side_pieces = self._pieces_by_side.get(side, {})
        if piece_type in side_pieces:
            return side_pieces[piece_type]
        for cp in self._custom_pieces_by_side.get(side, []):
            if cp.get("type") == piece_type:
                return cp
        return None

    # ═══════════════════════════════════════════════════════════════
    # 对称展开
    # ═══════════════════════════════════════════════════════════════

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

    # ═══════════════════════════════════════════════════════════════
    # 移动执行
    # ═══════════════════════════════════════════════════════════════

    def _jump_moves(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """执行跳跃移动"""
        px, py = piece["position"]
        to = move_def.get("to")
        block = move_def.get("block", [])
        land = move_def.get("land", "any")
        where = move_def.get("where", [])

        if isinstance(to, dict) and to.get("mode") == "region":
            return self._jump_region_moves(move_def, piece, board_state)

        nx, ny = px + to[0], py + to[1]

        if not self._in_bounds(nx, ny):
            return []

        for bx, by in block:
            if self._get_piece_at([px + bx, py + by], board_state):
                return []

        target = self._get_piece_at([nx, ny], board_state)

        # 落子判定（含等级吃子）
        if land == "empty" and target:
            return []
        if land == "enemy":
            if not target:
                return []
            if target["side"] == piece["side"]:
                return []
            if not self._can_capture(piece, target, board_state):
                return []
        if land == "any" and target:
            if target["side"] == piece["side"]:
                return []
            if not self._can_capture(piece, target, board_state):
                return []

        if where and not self._eval_where(where, piece, board_state, [nx, ny]):
            return []

        return [[nx, ny]]

    def _jump_region_moves(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """区域目标跳跃：可移动到指定区域内的任意格子"""
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
                if land == "enemy":
                    if not target or target["side"] == piece["side"]:
                        continue
                    if not self._can_capture(piece, target, board_state):
                        continue
                if land == "any" and target:
                    if target["side"] == piece["side"]:
                        continue
                    if not self._can_capture(piece, target, board_state):
                        continue
                if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                    continue
                moves.append([nx, ny])

        return moves

    def _ray_moves(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """执行射线移动（支持 path_constraint 跳河模式）"""
        px, py = piece["position"]
        direction = move_def.get("dir")
        max_dist = move_def.get("max", -1)
        screens = move_def.get("screens", 0)
        land = move_def.get("land", "any")
        where = move_def.get("where", [])
        path_constraint = move_def.get("path_constraint")

        if max_dist == -1:
            max_dist = max(self._get_width(), self._get_height())

        moves = []

        # 跳河模式：中间格必须满足 path_constraint，终点为第一个不满足的格子
        if path_constraint:
            must_be = path_constraint.get("must_be")
            no_blocker = path_constraint.get("no_blocker", False)
            path_cells_passed = 0

            for step in range(1, max_dist + 1):
                nx, ny = px + direction[0] * step, py + direction[1] * step
                if not self._in_bounds(nx, ny):
                    break

                target = self._get_piece_at([nx, ny], board_state)

                # 判断当前格是否为"路径格"（满足 path_constraint）
                is_path_cell = True
                if must_be == "water" and not self._is_in_water([nx, ny]):
                    is_path_cell = False
                if no_blocker and target:
                    is_path_cell = False  # 有棋子阻挡，不能作为路径

                if is_path_cell:
                    # 中间水格，继续延伸（不作为落点）
                    if no_blocker and target:
                        break  # 水中有棋子阻挡跳河
                    path_cells_passed += 1
                    continue
                else:
                    # 终点候选（非水格/陆地）
                    if path_cells_passed == 0:
                        break  # 没跳过任何水格，不是合法跳河
                    if target:
                        if (target["side"] != piece["side"]
                                and self._can_capture(piece, target, board_state)):
                            if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                                break
                            moves.append([nx, ny])
                        break  # 终点有棋子，无论是否吃都结束
                    else:
                        if land in ("any", "empty"):
                            if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                                break
                            moves.append([nx, ny])
                        break  # 落到陆地后结束（跳河只跳一次）
            return moves

        # 普通射线模式
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
                    if (land in ("any", "enemy") and target["side"] != piece["side"]
                            and self._can_capture(piece, target, board_state)):
                        if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                            continue
                        moves.append([nx, ny])
                    break
                elif land in ("any", "empty"):
                    if where and not self._eval_where(where, piece, board_state, [nx, ny]):
                        continue
                    moves.append([nx, ny])

        return moves

    # ═══════════════════════════════════════════════════════════════
    # 吃子判定（动物棋等级吃子核心）
    # ═══════════════════════════════════════════════════════════════

    def _can_capture(self, attacker: dict, defender: dict, board_state: dict) -> bool:
        """判断攻击方是否能吃掉防守方（等级吃子 + 鼠克象 + 陷阱降级 + 水域规则）"""
        if attacker.get("side") == defender.get("side"):
            return False

        attacker_type = attacker.get("type")
        defender_type = defender.get("type")
        attacker_config = self._get_piece_config(attacker_type, attacker.get("side"))
        defender_config = self._get_piece_config(defender_type, defender.get("side"))

        if not attacker_config or not defender_config:
            return True  # 无配置默认可吃

        attacker_rank = attacker_config.get("rank", 0)
        defender_rank = defender_config.get("rank", 0)
        attacker_pos = attacker.get("position")
        defender_pos = defender.get("position")
        attacker_in_water = self._is_in_water(attacker_pos) if attacker_pos else False
        defender_in_water = self._is_in_water(defender_pos) if defender_pos else False

        # 1. 水域规则 - defender 在水中（水中鼠免疫岸上攻击）
        if defender_in_water:
            defender_water_rules = defender_config.get("capture", {}).get("water_rules", {})
            if defender_water_rules.get("invulnerable_in_water"):
                # 水中鼠只能被同样在水中的鼠吃
                if not attacker_in_water:
                    return False

        # 2. 水域规则 - attacker 在水中（水中鼠不能吃岸上的象）
        if attacker_in_water:
            attacker_water_rules = attacker_config.get("capture", {}).get("water_rules", {})
            cannot_attack_from_water = attacker_water_rules.get("cannot_attack_from_water", [])
            if defender_type in cannot_attack_from_water:
                return False

        # 3. 陷阱降级 - defender 在 attacker 方的陷阱中（即 defender 的敌方陷阱）
        if self._is_in_enemy_trap(defender_pos, defender["side"]):
            defender_rank = 0

        # 4. 例外规则（鼠克象 / 象不能吃鼠）
        attacker_capture = attacker_config.get("capture", {})
        exceptions = attacker_capture.get("exceptions", [])
        for exc in exceptions:
            if "can_eat" in exc and exc["can_eat"] == defender_type:
                return True  # 显式可吃（鼠克象）
            if "cannot_eat" in exc and exc["cannot_eat"] == defender_type:
                return False  # 显式不可吃（象不能吃鼠）

        # 5. 默认等级规则
        return attacker_rank >= defender_rank

    # ═══════════════════════════════════════════════════════════════
    # 条件表达式引擎
    # ═══════════════════════════════════════════════════════════════

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

        # 区域判断
        if key == "in_region":
            if isinstance(value, dict):
                region_name = value.get("region", "$full_board")
            else:
                region_name = value
            pos = _resolve_pos(value, "$dest")
            region_name = self._resolve_var(region_name, piece)
            return self._is_in_region(pos, region_name, piece["side"])

        # 动物棋区域原语
        if key == "in_water":
            pos = _resolve_pos(value, "$dest")
            return self._is_in_water(pos)

        if key == "in_trap":
            pos = _resolve_pos(value, "$dest")
            return self._is_in_trap(pos)

        if key == "in_enemy_trap":
            pos = _resolve_pos(value, "$dest")
            return self._is_in_enemy_trap(pos, piece["side"])

        if key == "in_own_den":
            pos = _resolve_pos(value, "$dest")
            return self._is_in_own_den(pos, piece["side"])

        if key == "in_enemy_den":
            pos = _resolve_pos(value, "$dest")
            return self._is_in_enemy_den(pos, piece["side"])

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
        if var == "$side":
            return piece["side"]
        return var

    def _is_in_region(self, pos: List[int], region_ref: str, side: str) -> bool:
        """判断位置是否在指定区域（支持 cells 数组和 x_range/y_range 两种形式）"""
        if region_ref == "$full_board":
            return True

        # 优先查预计算的 cells 集合
        cells = self._region_cells.get(region_ref)
        if cells is not None:
            return (pos[0], pos[1]) in cells

        # 回退到 x_range/y_range 形式
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

    # ═══════════════════════════════════════════════════════════════
    # 棋盘工具方法
    # ═══════════════════════════════════════════════════════════════

    def _in_bounds(self, x: int, y: int) -> bool:
        """判断是否在棋盘范围内"""
        geometry = self.board_config.get("geometry", {})
        width = geometry.get("width", 7)
        height = geometry.get("height", 9)
        return 0 <= x < width and 0 <= y < height

    def _get_width(self) -> int:
        """获取棋盘宽度"""
        return self.board_config.get("geometry", {}).get("width", 7)

    def _get_height(self) -> int:
        """获取棋盘高度"""
        return self.board_config.get("geometry", {}).get("height", 9)

    def _get_piece_at(
        self, pos: List[int], board_state: dict
    ) -> Optional[dict]:
        """获取指定位置的棋子"""
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p["position"][0] == pos[0] and p["position"][1] == pos[1]:
                return p
        return None

    # ═══════════════════════════════════════════════════════════════
    # 胜负判定（动物棋）
    # ═══════════════════════════════════════════════════════════════

    def check_win(self, board_state: dict) -> Optional[str]:
        """判定胜负，返回胜方 'red'/'black' 或 None

        优先级：enter_den（进兽穴）> annihilation（全歼）> stalemate（困毙）
        """
        pieces = board_state.get("pieces", [])
        enter_den_enabled = (self.rules.get("win_conditions", {}).get("enter_den", {}) or {}).get("enabled", True)

        # 1. enter_den：己方动物进入对方兽穴（受 rules.json 的 win_conditions.enter_den.enabled 控制）
        if enter_den_enabled:
            for p in pieces:
                if not p.get("is_alive", True):
                    continue
                pos = p.get("position")
                side = p.get("side")
                if not pos or not side:
                    continue
                if self._is_in_enemy_den(pos, side):
                    return side

        # 2. annihilation：一方无存活棋子
        red_alive = any(p.get("is_alive", True) and p.get("side") == "red" for p in pieces)
        black_alive = any(p.get("is_alive", True) and p.get("side") == "black" for p in pieces)
        if not red_alive:
            return "black"
        if not black_alive:
            return "red"

        # 3. stalemate：当前方无任何合法移动
        current_turn = board_state.get("current_turn")
        if current_turn:
            has_move = False
            for p in pieces:
                if p.get("is_alive", True) and p.get("side") == current_turn:
                    if self.get_valid_moves(p, board_state):
                        has_move = True
                        break
            if not has_move:
                return "black" if current_turn == "red" else "red"

        return None
