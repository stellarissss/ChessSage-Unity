"""
黑白棋规则引擎 v1.0 - 根据JSON配置计算合法落子与翻转
核心设计：jump + ray + flip 三原子移动体系 + 条件表达式引擎

三原语：
- jump：离散跳跃（自定义棋子可用）
- ray：射线滑行（自定义棋子可用）
- flip：夹吃翻转（黑白棋核心原语，从落子点沿方向先遇≥1敌棋再遇己棋，中间敌棋翻转阵营）

条件表达式引擎（where）：in_region / crossed_river / same_side / at_row / at_col / not / and / or
- crossed_river 在 8×8 上语义为"上下半场"（y < height/2 为上半场，y >= height/2 为下半场）
- same_side 语义为"己方半场"（黑方/red 下半场 y >= height/2，白方上半场 y < height/2）
"""
import copy
from typing import Dict, Any, List, Tuple, Optional


class RuleEngine:
    """规则引擎，根据pieces_black.json和pieces_white.json计算棋子的合法落子与翻转"""

    def __init__(self, board: dict, pieces_black: dict, pieces_white: dict, rules: dict):
        self.board_config = board
        self.rules = rules

        self._pieces_by_side = {
            "black": pieces_black.get("pieces", {}),
            "white": pieces_white.get("pieces", {}),
            # red 兼容：复用黑方定义（red 视为下半场阵营）
            "red": pieces_black.get("pieces", {}),
        }
        self._custom_pieces_by_side = {
            "black": pieces_black.get("custom_pieces", []),
            "white": pieces_white.get("custom_pieces", []),
            "red": pieces_black.get("custom_pieces", []),
        }

    # ------------------------------------------------------------------
    # 对外主接口
    # ------------------------------------------------------------------
    def get_valid_moves(
        self, piece: dict, board_state: dict
    ) -> List[List[int]]:
        """
        计算棋子的所有合法移动/落子位置

        - 标准 disc（moves 中含 flip 原语）：调用 _flip_moves 返回合法落子点（空格）
        - 自定义棋子（含 jump/ray）：保留原有 jump/ray 逻辑

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

        # 含 flip 原语 → 黑白棋落子语义
        has_flip = any(md.get("kind") == "flip" for md in move_defs)
        if has_flip:
            return self._flip_moves(piece, board_state)

        # 自定义棋子 jump/ray 逻辑
        px, py = piece["position"]
        all_moves = []

        for move_def in move_defs:
            expanded = self._expand_symmetry(move_def)
            for exp_def in expanded:
                moves = self._execute_move_def(exp_def, piece, board_state)
                all_moves.extend(moves)

        return list({tuple(m): m for m in all_moves}.values())

    def get_valid_placements(
        self, side: str, board_state: dict
    ) -> List[List[int]]:
        """
        聚合该方所有 disc 的 _flip_moves，去重得到全局合法落子点
        （黑白棋落子点是空格而非棋子）
        """
        placements: List[List[int]] = []
        seen = set()
        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True):
                continue
            if p["side"] != side:
                continue
            for placement in self._flip_moves(p, board_state):
                key = (placement[0], placement[1])
                if key not in seen:
                    seen.add(key)
                    placements.append(placement)
        return placements

    def apply_flip_captures(
        self, pos: List[int], side: str, board_state: dict
    ) -> List[str]:
        """
        实际执行翻转：从落子点 pos 出发，沿所有 flip 方向，
        找到"≥1 敌棋 + 1 己棋"的夹击线，将中间敌棋的 side 字段改为 side。
        返回被翻转的棋子 id 列表。
        """
        px, py = pos
        flipped_ids: List[str] = []

        for direction, max_dist in self._get_flip_directions(side):
            enemies: List[dict] = []
            for step in range(1, max_dist + 1):
                nx, ny = px + direction[0] * step, py + direction[1] * step
                if not self._in_bounds(nx, ny):
                    break
                target = self._get_piece_at([nx, ny], board_state)
                if target is None:
                    # 遇到空格 → 此方向无夹击
                    break
                if target["side"] == side:
                    # 遇到己方棋子 → 翻转中间敌棋
                    if enemies:
                        for e in enemies:
                            e["side"] = side
                            flipped_ids.append(e["id"])
                    break
                else:
                    enemies.append(target)
        return flipped_ids

    def is_game_over(self, board_state: dict) -> bool:
        """游戏结束：双方均无合法落子 或 棋盘满"""
        total = sum(1 for p in board_state.get("pieces", []) if p.get("is_alive", True))
        if total >= self._get_width() * self._get_height():
            return True

        sides = set(
            p["side"]
            for p in board_state.get("pieces", [])
            if p.get("is_alive", True)
        )
        if not sides:
            return True

        for side in sides:
            if self.get_valid_placements(side, board_state):
                return False
        return True

    def get_winner(self, board_state: dict) -> Optional[str]:
        """棋子数多者胜，相等为平局（返回 None）"""
        counts: Dict[str, int] = {}
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True):
                counts[p["side"]] = counts.get(p["side"], 0) + 1
        if not counts:
            return None
        max_count = max(counts.values())
        winners = [s for s, c in counts.items() if c == max_count]
        if len(winners) == 1:
            return winners[0]
        return None

    # ------------------------------------------------------------------
    # flip 原语核心
    # ------------------------------------------------------------------
    def _flip_moves(
        self, piece: dict, board_state: dict
    ) -> List[List[int]]:
        """
        返回该棋子（作为锚点己棋）能支撑的合法落子点。
        落子点必须能沿某个 flip 方向翻转至少 1 颗敌方棋子。
        """
        move_defs = self._get_move_definitions(piece["type"], piece.get("side"))
        if not move_defs:
            return []

        placements: List[List[int]] = []
        seen = set()
        for move_def in move_defs:
            if move_def.get("kind") != "flip":
                continue
            for exp_def in self._expand_symmetry(move_def):
                for placement in self._flip_moves_single(exp_def, piece, board_state):
                    key = (placement[0], placement[1])
                    if key not in seen:
                        seen.add(key)
                        placements.append(placement)
        return placements

    def _flip_moves_single(
        self, exp_def: dict, piece: dict, board_state: dict
    ) -> List[List[int]]:
        """单个已展开方向的 flip 落子点计算"""
        px, py = piece["position"]
        side = piece["side"]
        direction = exp_def.get("dir", [1, 0])
        max_dist = exp_def.get("max", 6)
        where = exp_def.get("where", [])

        placements: List[List[int]] = []
        enemy_count = 0

        for step in range(1, max_dist + 1):
            nx, ny = px + direction[0] * step, py + direction[1] * step
            if not self._in_bounds(nx, ny):
                break
            target = self._get_piece_at([nx, ny], board_state)
            if target is None:
                # 空格：若此前已遇≥1敌棋，则为合法落子点
                if enemy_count >= 1:
                    # where 条件以"落子后的新棋子"视角求值：$self/$dest 均指向落子点
                    placing_piece = {
                        "id": "__placing__",
                        "type": piece["type"],
                        "side": side,
                        "position": [nx, ny],
                    }
                    if not where or self._eval_where(where, placing_piece, board_state, [nx, ny]):
                        placements.append([nx, ny])
                break
            elif target["side"] == side:
                # 己方棋子：此方向无法形成落子
                break
            else:
                enemy_count += 1

        return placements

    def _get_flip_directions(self, side: str) -> List[Tuple[Tuple[int, int], int]]:
        """聚合该方所有棋子类型的 flip 方向（已对称展开，去重）"""
        directions: List[Tuple[Tuple[int, int], int]] = []
        seen = set()

        def _collect(config: dict):
            for md in config.get("moves", []):
                if md.get("kind") != "flip":
                    continue
                for exp in self._expand_symmetry(md):
                    d = tuple(exp.get("dir", [1, 0]))
                    if d not in seen:
                        seen.add(d)
                        directions.append((d, exp.get("max", 6)))

        for ptype, pconfig in self._pieces_by_side.get(side, {}).items():
            _collect(pconfig)
        for cp in self._custom_pieces_by_side.get(side, []):
            _collect(cp)

        return directions

    # ------------------------------------------------------------------
    # 移动定义 / 对称展开 / 执行（保留以兼容自定义棋子的 jump/ray）
    # ------------------------------------------------------------------
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

        elif kind == "flip":
            direction = move_def.get("dir", [1, 0])

            if sym == "rotate4_mirror":
                # 黑白棋标准：八方向对称展开（4 正方向 + 4 对角方向）
                all_dirs = [
                    (1, 0), (-1, 0), (0, 1), (0, -1),
                    (1, 1), (1, -1), (-1, 1), (-1, -1),
                ]
                for d in all_dirs:
                    result.append({**move_def, "dir": [d[0], d[1]]})

            elif sym == "rotate4":
                for dx, dy in [(1, 0), (0, 1), (-1, 0), (0, -1)]:
                    new_dir = [direction[0] * dx - direction[1] * dy, direction[0] * dy + direction[1] * dx]
                    result.append({**move_def, "dir": new_dir})

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
        elif kind == "flip":
            return self._flip_moves(piece, board_state)

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
            side = piece["side"]
            # 下半场阵营（red/black）向上 (-1)；上半场阵营（white）向下 (+1)
            direction = -1 if side in ("red", "black") else 1
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

    # ------------------------------------------------------------------
    # where 条件表达式引擎（完整保留，AI 可自由用于自定义棋子）
    # ------------------------------------------------------------------
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

        支持的操作符：
        - in_region / crossed_river / same_side / at_row / at_col
        - 逻辑：not / and / or
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
            # 8×8 语义：上下半场。己方半场的反向即"过河"
            pos = _resolve_pos(value, "$dest")
            return self._crossed_half(pos[1], piece["side"])

        if key == "same_side":
            # 8×8 语义：己方半场（黑/red 下半场 y >= height/2，白方上半场 y < height/2）
            pos = _resolve_pos(value, "$dest")
            return self._own_half(pos[1], piece["side"])

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
            # 黑白棋无九宫
            return False

        geometry = self.board_config.get("geometry", {})
        regions = geometry.get("regions", {})
        region = regions.get(region_ref)

        if region is None:
            return True

        return self._match_region(pos[0], pos[1], region)

    def _match_region(self, x: int, y: int, region: Any) -> bool:
        """
        匹配多种区域格式：
        - dict 含 x_range/y_range（象棋遗留格式）
        - dict 为命名子区域集合（如 edges:{top,bottom,left,right}）→ 命中任一即可
        - list 为 4 元素矩形 [x1,y1,x2,y2]
        - list 为点列表 [[x,y], ...]
        """
        if isinstance(region, dict):
            if "x_range" in region or "y_range" in region:
                if "x_range" in region:
                    rx_start, rx_end = region["x_range"]
                    if not (rx_start <= x <= rx_end):
                        return False
                if "y_range" in region:
                    ry_start, ry_end = region["y_range"]
                    if not (ry_start <= y <= ry_end):
                        return False
                return True
            # 命名子区域集合：命中任一即可
            for sub in region.values():
                if self._match_region(x, y, sub):
                    return True
            return False

        if isinstance(region, list):
            if len(region) == 4 and all(isinstance(v, (int, float)) for v in region):
                # 矩形 [x1, y1, x2, y2]
                x1, y1, x2, y2 = region
                return x1 <= x <= x2 and y1 <= y <= y2
            # 点列表 [[x, y], ...]
            for pt in region:
                if isinstance(pt, (list, tuple)) and len(pt) == 2:
                    if pt[0] == x and pt[1] == y:
                        return True
            return False

        return True

    def _own_half(self, y: int, side: str) -> bool:
        """是否在己方半场（黑/red 下半场，白方上半场）"""
        mid = self._get_height() / 2
        if side == "white":
            return y < mid
        # black / red / 默认 → 下半场
        return y >= mid

    def _crossed_half(self, y: int, side: str) -> bool:
        """是否过河（进入对方半场）"""
        return not self._own_half(y, side)

    def _in_bounds(self, x: int, y: int) -> bool:
        """判断是否在棋盘范围内"""
        geometry = self.board_config.get("geometry", {})
        width = geometry.get("width", 8)
        height = geometry.get("height", 8)
        return 0 <= x < width and 0 <= y < height

    def _get_width(self) -> int:
        """获取棋盘宽度"""
        return self.board_config.get("geometry", {}).get("width", 8)

    def _get_height(self) -> int:
        """获取棋盘高度"""
        return self.board_config.get("geometry", {}).get("height", 8)

    def _get_piece_at(
        self, pos: List[int], board_state: dict
    ) -> Optional[dict]:
        """获取指定位置的棋子"""
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p["position"][0] == pos[0] and p["position"][1] == pos[1]:
                return p
        return None
