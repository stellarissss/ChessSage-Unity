"""
中国跳棋规则引擎 v1.0 - 根据JSON配置计算合法移动
核心设计：step + hop 双原子移动体系 + hex6 对称（邻接表驱动）

坐标系统：[row, col] 双倍列坐标（偶数行 col 为偶数，奇数行 col 为奇数）
棋盘共 121 个位置，17 行；邻接偏移 6 方向，跳跃偏移为邻接的 2 倍。

跳棋不吃子：跳跃时被跳过的棋子保留在原位，连跳整条路径算 1 步。
"""
from typing import List, Optional


class RuleEngine:
    """规则引擎，根据 pieces_red.json 和 pieces_black.json 计算棋子的合法移动"""

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
        # 预处理邻接表和有效位置集合
        self._positions = set()
        for key in board.get("positions", {}):
            r, c = key.split(",")
            self._positions.add((int(r), int(c)))
        self._adjacency = {}
        for key, neighbors in board.get("adjacency", {}).items():
            r, c = key.split(",")
            self._adjacency[(int(r), int(c))] = [tuple(n) for n in neighbors]
        # 营区位置
        camps = board.get("geometry", {}).get("camps", {})
        self._camp_positions = {
            "red": set(tuple(p) for p in camps.get("red", {}).get("positions", [])),
            "black": set(tuple(p) for p in camps.get("black", {}).get("positions", [])),
        }
        # king类型（跳棋默认没有king，但支持自定义）
        self._king_types = set()

    # ─────────────────────────────────────────────
    # 对外接口（签名不变，供 main.py 和 chess_ai.py 调用）
    # ─────────────────────────────────────────────

    def get_valid_moves(self, piece: dict, board_state: dict) -> List[List[int]]:
        """
        计算棋子的所有合法移动位置

        Args:
            piece: 棋子对象 {id, type, name, side, position, is_alive}
            board_state: 棋盘状态 {pieces, current_turn, ...}

        Returns:
            合法位置列表 [[row, col], ...]
            连跳整条路径算 1 步，返回所有可达终点
        """
        piece_type = piece.get("type")
        if not piece_type:
            return []

        move_defs = self._get_move_definitions(piece_type, piece.get("side"))
        if not move_defs:
            return []

        all_moves = []
        for move_def in move_defs:
            moves = self._execute_move_def(move_def, piece, board_state)
            all_moves.extend(moves)

        # 去重（同一终点可能被 step/hop 多次覆盖）
        return list({tuple(m): m for m in all_moves}.values())

    def _get_piece_at(self, pos: List[int], board_state: dict) -> Optional[dict]:
        """获取指定位置的棋子"""
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p["position"][0] == pos[0] and p["position"][1] == pos[1]:
                return p
        return None

    def is_all_in_camp(self, board_state) -> Optional[str]:
        """检查是否有方全部棋子已进入对方营区

        红方目标：所有红方棋子进入黑方营区（rows 13-16）
        黑方目标：所有黑方棋子进入红方营区（rows 0-3）

        Returns:
            获胜方 "red" / "black"，或 None
        """
        # 红方目标：所有红方棋子在黑方营区(rows 13-16)
        red_all_in_black = all(
            self._is_in_camp(p["position"], "black")
            for p in board_state["pieces"]
            if p["side"] == "red" and p.get("is_alive", True)
        )
        if red_all_in_black:
            return "red"
        # 黑方目标：所有黑方棋子在红方营区(rows 0-3)
        black_all_in_red = all(
            self._is_in_camp(p["position"], "red")
            for p in board_state["pieces"]
            if p["side"] == "black" and p.get("is_alive", True)
        )
        if black_all_in_red:
            return "black"
        return None

    # ─────────────────────────────────────────────
    # 移动定义
    # ─────────────────────────────────────────────

    def _get_move_definitions(self, piece_type: str, side: str) -> List[dict]:
        """获取棋子的移动定义（直接从对应阵营的规则中查找，支持自定义棋子）"""
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

    def _execute_move_def(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """执行单个移动定义（step / hop 原语）"""
        kind = move_def.get("kind")
        if kind == "step":
            return self._step_moves(move_def, piece, board_state)
        if kind == "hop":
            return self._hop_moves(move_def, piece, board_state)
        return []

    # ─────────────────────────────────────────────
    # step 原语（单步移动）
    # ─────────────────────────────────────────────

    def _step_moves(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """step 原语：单步移动到相邻空位

        sym: "hex6" 表示直接从邻接表获取 6 方向邻居（不需要坐标旋转计算）
        land: "empty"（默认）要求目标位置为空
        """
        land = move_def.get("land", "empty")
        where = move_def.get("where", [])
        cur = piece["position"]
        moves = []
        for neighbor in self._get_neighbors(cur):
            target = self._get_piece_at(neighbor, board_state)
            if land == "empty" and target:
                continue
            if land == "any" and target and target.get("side") == piece.get("side"):
                continue
            if where and not self._eval_where(where, piece, board_state, neighbor):
                continue
            moves.append([neighbor[0], neighbor[1]])
        return moves

    # ─────────────────────────────────────────────
    # hop 原语（跳跃 + 连续跳跃）
    # ─────────────────────────────────────────────

    def _hop_moves(self, move_def: dict, piece: dict, board_state: dict) -> List[List[int]]:
        """hop 原语：跳跃移动，支持连跳

        - 相邻位置（邻接表）必须有棋子（任意方）才能跳
        - 落点 = 2*相邻位置 - 当前位置
        - 落点必须在棋盘有效位置内且为空
        - chain: true 时递归搜索连跳终点，用 visited 集合避免循环
        - 跳棋不吃子，被跳过的棋子保留在原位
        """
        land = move_def.get("land", "empty")
        chain = move_def.get("chain", False)
        where = move_def.get("where", [])
        cur = tuple(piece["position"])
        # 保存当前 hop 的 where/land 供 _hop_chain 使用
        self._hop_where = where
        self._hop_land = land
        results = []
        for neighbor in self._get_neighbors(cur):
            nbr = tuple(neighbor)
            # 相邻位置必须有棋子（任意方）才能跳
            adj_piece = self._get_piece_at(neighbor, board_state)
            if not adj_piece:
                continue
            # 计算落点 = 2*相邻位置 - 当前位置
            landing = (2 * nbr[0] - cur[0], 2 * nbr[1] - cur[1])
            # 落点必须在棋盘有效位置内
            if not self._is_valid_position(landing):
                continue
            # 落点必须为空（跳棋不能落子到已占位置）
            target = self._get_piece_at(list(landing), board_state)
            if land == "empty" and target:
                continue
            if land == "any" and target and target.get("side") == piece.get("side"):
                continue
            if where and not self._eval_where(where, piece, board_state, list(landing)):
                continue
            results.append([landing[0], landing[1]])
            # 连跳：递归搜索从落点出发的进一步跳跃
            # 为每个邻接分支创建独立的visited集合，避免不同路径互相阻塞
            if chain:
                branch_visited = {cur, landing}
                results.extend(self._hop_chain(landing, branch_visited, board_state, piece))
        return results

    def _hop_chain(self, current_pos, visited, board_state, piece) -> List[List[int]]:
        """递归搜索从 current_pos 出发的连跳终点

        Args:
            current_pos: 当前跳跃落点（tuple）
            visited: 已访问位置集合（含起点与所有中间落点，避免循环）
            board_state: 棋盘状态
            piece: 正在跳跃的棋子（用于排除其原始位置，视为已离开）

        Returns:
            从 current_pos 出发可达的连跳终点列表
        """
        where = getattr(self, "_hop_where", [])
        land = getattr(self, "_hop_land", "empty")
        results = []
        for neighbor in self._get_neighbors(current_pos):
            nbr = tuple(neighbor)
            # 相邻位置必须有棋子（任意方）才能跳
            adj_piece = self._get_piece_at(neighbor, board_state)
            # 排除正在跳跃的棋子本身（它已"离开"原位置，原位置视为空）
            if adj_piece and adj_piece.get("id") == piece.get("id"):
                adj_piece = None
            if not adj_piece:
                continue
            # 计算落点 = 2*相邻位置 - 当前位置
            landing = (2 * nbr[0] - current_pos[0], 2 * nbr[1] - current_pos[1])
            # 落点必须在棋盘有效位置内
            if not self._is_valid_position(landing):
                continue
            # 用 visited 集合避免循环
            if landing in visited:
                continue
            # 落点必须为空
            target = self._get_piece_at(list(landing), board_state)
            if land == "empty" and target:
                continue
            if land == "any" and target and target.get("side") == piece.get("side"):
                continue
            if where and not self._eval_where(where, piece, board_state, list(landing)):
                continue
            results.append([landing[0], landing[1]])
            visited.add(landing)
            # 继续递归
            results.extend(self._hop_chain(landing, visited, board_state, piece))
        return results

    # ─────────────────────────────────────────────
    # 棋盘辅助方法
    # ─────────────────────────────────────────────

    def _is_in_camp(self, pos, side) -> bool:
        """判断位置是否在指定方营区内

        红方营区：rows 0-3（geometry.camps.red.positions）
        黑方营区：rows 13-16（geometry.camps.black.positions）
        """
        return tuple(pos) in self._camp_positions.get(side, set())

    def _get_neighbors(self, pos) -> List[List[int]]:
        """从邻接表获取位置的所有邻居（hex6 六方向）"""
        key = (pos[0], pos[1])
        return [list(n) for n in self._adjacency.get(key, [])]

    def _is_valid_position(self, pos) -> bool:
        """检查位置是否在棋盘的 positions 字典中"""
        return (pos[0], pos[1]) in self._positions

    # ─────────────────────────────────────────────
    # where 条件表达式引擎（简化版，供未来扩展）
    # ─────────────────────────────────────────────

    def _eval_where(self, conditions: List[dict], piece: dict, board_state: dict, dest: List[int]) -> bool:
        """求值 where 条件表达式（所有条件需同时满足）"""
        for cond in conditions:
            if not self._eval_condition(cond, piece, board_state, dest):
                return False
        return True

    def _eval_condition(self, cond: dict, piece: dict, board_state: dict, dest: List[int]) -> bool:
        """求值单个条件（简化版）

        支持的操作符：
        - 逻辑组合：not / and / or
        - in_region：判断是否在营区内（支持 "red" / "black" / "$opponent_camp" / "$full_board"）
        - at_row / at_col：行列判断

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

        # in_region：判断是否在营区内
        if key == "in_region":
            if isinstance(value, dict):
                region_name = value.get("region", "$opponent_camp")
            else:
                region_name = value
            pos = _resolve_pos(value, "$dest")
            if region_name == "$full_board":
                return True
            if region_name == "$opponent_camp":
                opponent = "black" if piece["side"] == "red" else "red"
                return self._is_in_camp(pos, opponent)
            if region_name in ("red", "black"):
                return self._is_in_camp(pos, region_name)
            return True

        # 行列判断（pos 为 [row, col]）
        if key == "at_row":
            if isinstance(value, dict):
                pos = _resolve_pos(value, "$dest")
                row = value.get("row", -1)
            else:
                pos = dest
                row = value
            return pos[0] == row

        if key == "at_col":
            if isinstance(value, dict):
                pos = _resolve_pos(value, "$dest")
                col = value.get("col", -1)
            else:
                pos = dest
                col = value
            return pos[1] == col

        return True
