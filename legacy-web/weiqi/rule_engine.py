"""
围棋规则引擎 v2.0 - 高性能优化版
核心功能：落子、气的计算、提子、打劫、禁手、五连检测
优化：二维数组棋盘表示 + 增量式更新 + 轻量级快速接口
"""
import copy
from typing import Dict, Any, List, Tuple, Optional


class FastBoard:
    """高性能二维数组棋盘表示，专为AI搜索优化"""

    EMPTY = 0
    BLACK = 1
    WHITE = 2

    def __init__(self, width: int, height: int):
        self.width = width
        self.height = height
        self.board = [[self.EMPTY] * height for _ in range(width)]
        self.captures = {self.BLACK: 0, self.WHITE: 0}
        self.ko_point = None
        self.move_count = 0
        self._directions = [(-1, 0), (1, 0), (0, -1), (0, 1)]
        # 机制原语：按颜色配置的限气（liberty_cap）与不可吃（uncapturable），默认不生效
        self._liberty_cap = {self.BLACK: None, self.WHITE: None}
        self._uncapturable = {self.BLACK: False, self.WHITE: False}

    @classmethod
    def from_state(cls, state: dict, width: int, height: int) -> "FastBoard":
        """从游戏状态快速构建FastBoard"""
        fb = cls(width, height)
        for p in state.get("pieces", []):
            if p.get("is_alive", True):
                x, y = p["position"]
                fb.board[x][y] = cls.BLACK if p["side"] == "black" else cls.WHITE
        caps = state.get("captures", {})
        fb.captures[cls.BLACK] = caps.get("black", 0)
        fb.captures[cls.WHITE] = caps.get("white", 0)
        return fb

    def configure_modifiers(self, rules: dict) -> "FastBoard":
        """从 rules.json 读取围棋机制原语（rules.modifiers.go），按颜色配置限气/不可吃

        默认（无配置）即保持原始规则：不设限气、非不可吃。
        """
        go = (rules or {}).get("modifiers", {}).get("go", {})
        if not isinstance(go, dict):
            go = {}
        for color, side in ((self.BLACK, "black"), (self.WHITE, "white")):
            entry = go.get(side)
            if not isinstance(entry, dict):
                self._liberty_cap[color] = None
                self._uncapturable[color] = False
                continue
            cap = entry.get("liberty_cap")
            self._liberty_cap[color] = cap if isinstance(cap, int) and cap > 0 else None
            self._uncapturable[color] = bool(entry.get("uncapturable", False))
        return self

    def _effective_liberties(self, color: int, actual: int) -> int:
        """按机制原语计算有效气数：设了 liberty_cap 时取 min(实际气, 上限)"""
        cap = self._liberty_cap.get(color)
        if cap is not None:
            return min(actual, cap)
        return actual

    def clone(self) -> "FastBoard":
        """克隆棋盘（比deepcopy快得多）"""
        fb = FastBoard.__new__(FastBoard)
        fb.width = self.width
        fb.height = self.height
        fb.board = [row[:] for row in self.board]
        fb.captures = {self.BLACK: self.captures[self.BLACK], self.WHITE: self.captures[self.WHITE]}
        fb.ko_point = self.ko_point
        fb.move_count = self.move_count
        fb._directions = self._directions
        fb._liberty_cap = dict(self._liberty_cap)
        fb._uncapturable = dict(self._uncapturable)
        return fb

    def in_bounds(self, x: int, y: int) -> bool:
        return 0 <= x < self.width and 0 <= y < self.height

    def get(self, x: int, y: int) -> int:
        return self.board[x][y]

    def _get_group_and_liberties(self, x: int, y: int) -> Tuple[List[Tuple[int, int]], int]:
        """BFS获取连通块和气数，一次遍历完成"""
        color = self.board[x][y]
        if color == self.EMPTY:
            return [], 0

        w, h = self.width, self.height
        group = []
        liberties = set()
        visited = set()
        stack = [(x, y)]
        dirs = self._directions

        while stack:
            cx, cy = stack.pop()
            key = cx * 1000 + cy
            if key in visited:
                continue
            visited.add(key)
            group.append((cx, cy))

            for dx, dy in dirs:
                nx, ny = cx + dx, cy + dy
                if nx < 0 or nx >= w or ny < 0 or ny >= h:
                    continue
                nkey = nx * 1000 + ny
                if nkey in visited:
                    continue
                val = self.board[nx][ny]
                if val == self.EMPTY:
                    liberties.add(nkey)
                elif val == color:
                    stack.append((nx, ny))

        return group, self._effective_liberties(color, len(liberties))

    def _get_group_liberties(self, x: int, y: int) -> int:
        """只计算气数，不收集全部棋子（更快）"""
        color = self.board[x][y]
        if color == self.EMPTY:
            return 0

        w, h = self.width, self.height
        liberties = set()
        visited = set()
        stack = [(x, y)]
        dirs = self._directions

        while stack:
            cx, cy = stack.pop()
            key = cx * 1000 + cy
            if key in visited:
                continue
            visited.add(key)

            for dx, dy in dirs:
                nx, ny = cx + dx, cy + dy
                if nx < 0 or nx >= w or ny < 0 or ny >= h:
                    continue
                val = self.board[nx][ny]
                if val == self.EMPTY:
                    liberties.add(nx * 1000 + ny)
                elif val == color and (nx * 1000 + ny) not in visited:
                    stack.append((nx, ny))

        return self._effective_liberties(color, len(liberties))

    def is_valid_move(self, x: int, y: int, color: int, ko_enabled: bool = True, suicide_enabled: bool = True) -> bool:
        """快速判断落子合法性"""
        if not self.in_bounds(x, y):
            return False
        if self.board[x][y] != self.EMPTY:
            return False

        if ko_enabled and self.ko_point == (x, y):
            return False

        if not suicide_enabled:
            return True

        opp = self.WHITE if color == self.BLACK else self.BLACK
        can_capture = False

        for dx, dy in self._directions:
            nx, ny = x + dx, y + dy
            if not self.in_bounds(nx, ny):
                continue
            if self.board[nx][ny] == opp:
                if self._uncapturable[opp]:
                    continue  # 敌人不可被吃，不能据此判定为合法提子
                if self._get_group_liberties(nx, ny) == 1:
                    can_capture = True
                    break

        if can_capture:
            return True

        self.board[x][y] = color
        my_libs = self._get_group_liberties(x, y)
        self.board[x][y] = self.EMPTY

        return my_libs > 0

    def play_move(self, x: int, y: int, color: int) -> Tuple[bool, int]:
        """执行落子，返回(是否成功, 提子数)"""
        if self.board[x][y] != self.EMPTY:
            return False, 0

        opp = self.WHITE if color == self.BLACK else self.BLACK
        self.board[x][y] = color
        captured_total = 0
        captured_single_point = None

        for dx, dy in self._directions:
            nx, ny = x + dx, y + dy
            if not self.in_bounds(nx, ny):
                continue
            if self.board[nx][ny] == opp:
                group, libs = self._get_group_and_liberties(nx, ny)
                if libs == 0:
                    if self._uncapturable[opp]:
                        continue  # 气尽但不可被吃，保留该组
                    if len(group) == 1:
                        captured_single_point = group[0]
                    captured_total += len(group)
                    for gx, gy in group:
                        self.board[gx][gy] = self.EMPTY

        self.captures[color] += captured_total

        my_libs = self._get_group_liberties(x, y)
        if my_libs == 0 and captured_total == 0:
            self.board[x][y] = self.EMPTY
            return False, 0

        if captured_total == 1 and captured_single_point:
            self.ko_point = captured_single_point
        else:
            self.ko_point = None

        self.move_count += 1
        return True, captured_total

    def undo_move(self, x: int, y: int, color: int, captured: List[Tuple[int, int]], prev_ko):
        """撤销落子（用于make/unmake模式）"""
        self.board[x][y] = self.EMPTY
        opp = self.WHITE if color == self.BLACK else self.BLACK
        for cx, cy in captured:
            self.board[cx][cy] = opp
        self.captures[color] -= len(captured)
        self.ko_point = prev_ko
        self.move_count -= 1

    def get_captured_stones(self, x: int, y: int, color: int) -> List[Tuple[int, int]]:
        """预演落子，获取会被提掉的棋子列表（用于undo）"""
        opp = self.WHITE if color == self.BLACK else self.BLACK
        captured = []
        for dx, dy in self._directions:
            nx, ny = x + dx, y + dy
            if not self.in_bounds(nx, ny):
                continue
            if self.board[nx][ny] == opp:
                group, libs = self._get_group_and_liberties(nx, ny)
                if libs == 0 and not self._uncapturable[opp]:
                    captured.extend(group)
        return captured

    def count_neighbors(self, x: int, y: int, color: int) -> int:
        """统计某颜色相邻棋子数"""
        count = 0
        for dx, dy in self._directions:
            nx, ny = x + dx, y + dy
            if self.in_bounds(nx, ny) and self.board[nx][ny] == color:
                count += 1
        return count

    def get_all_stones(self, color: int) -> List[Tuple[int, int]]:
        """获取某方所有棋子位置"""
        stones = []
        for x in range(self.width):
            for y in range(self.height):
                if self.board[x][y] == color:
                    stones.append((x, y))
        return stones


class RuleEngine:
    """围棋规则引擎"""

    def __init__(self, board: dict, pieces_red: dict, pieces_black: dict, rules: dict):
        self.board_config = board
        self.rules = rules
        self._pieces_by_side = {
            "black": pieces_black.get("pieces", {}),
            "white": pieces_red.get("pieces", {}),
        }
        self._directions = [(-1, 0), (1, 0), (0, -1), (0, 1)]

    # ---------- 机制原语解析（关键词: 限气 liberty_cap / 不可吃 uncapturable） ----------
    def _go_modifiers(self) -> Dict[str, Any]:
        """读取围棋机制原语配置 rules.modifiers.go"""
        mods = self.rules.get("modifiers", {}).get("go", {})
        return mods if isinstance(mods, dict) else {}

    def _resolve_mods(self, side: str, symbol: Optional[str] = None) -> Dict[str, Any]:
        """解析颜色级 +（可选的）棋子符号级机制原语，符号级覆盖颜色级"""
        go = self._go_modifiers()
        result: Dict[str, Any] = {}
        color_entry = go.get(side)
        if isinstance(color_entry, dict):
            result.update(color_entry)
        if symbol:
            sym_entry = go.get(symbol)
            if isinstance(sym_entry, dict):
                result.update(sym_entry)
        return result

    def _liberty_cap(self, side: str, symbol: Optional[str] = None) -> Optional[int]:
        """返回某颜色/符号的限气上限，未配置则返回 None"""
        cap = self._resolve_mods(side, symbol).get("liberty_cap")
        return cap if isinstance(cap, int) and cap > 0 else None

    def _uncapturable(self, side: str, symbol: Optional[str] = None) -> bool:
        """返回某颜色/符号是否不可被吃（不可断气）"""
        return bool(self._resolve_mods(side, symbol).get("uncapturable", False))

    def _effective_liberties(self, board_state: dict, group: List[dict]) -> int:
        """按机制原语计算有效气数：无 liberty_cap 时等于实际气数"""
        actual = self._count_liberties(board_state, group)
        if not group:
            return actual
        cap = self._liberty_cap(group[0]["side"], symbol=group[0].get("name"))
        if cap is not None:
            return min(actual, cap)
        return actual

    def get_valid_moves(self, board_state: dict, side: str) -> List[List[int]]:
        """获取所有合法落子位置"""
        valid_moves = []
        width = self._get_width()
        height = self._get_height()

        for x in range(width):
            for y in range(height):
                if self.is_valid_move(board_state, x, y, side):
                    valid_moves.append([x, y])

        return valid_moves

    def is_valid_move(self, board_state: dict, x: int, y: int, side: str) -> bool:
        """判断落子是否合法"""
        if not self._in_bounds(x, y):
            return False

        if self._get_piece_at([x, y], board_state):
            return False

        if self._is_ko(board_state, x, y, side):
            return False

        if self._is_suicide(board_state, x, y, side):
            return False

        if side == "black" and self.rules.get("special_rules", {}).get("forbidden_black", {}).get("enabled", False):
            if self._is_forbidden_black(board_state, x, y):
                return False

        return True

    def place_stone(self, board_state: dict, x: int, y: int, side: str) -> dict:
        """落子并处理提子，返回新的棋盘状态"""
        new_state = copy.deepcopy(board_state)

        stone_label = "●" if side == "black" else "○"
        new_piece = {
            "id": f"{side}_stone_{x}_{y}_{len(new_state['pieces'])}",
            "type": "stone",
            "name": stone_label,
            "side": side,
            "position": [x, y],
            "is_alive": True,
            "custom_properties": {},
        }
        new_state["pieces"].append(new_piece)

        captured_count = 0
        captured_ids = []

        other_side = "white" if side == "black" else "black"
        for dx, dy in self._directions:
            nx, ny = x + dx, y + dy
            if self._in_bounds(nx, ny):
                piece = self._get_piece_at([nx, ny], new_state)
                if piece and piece["side"] == other_side:
                    group = self._get_group(new_state, nx, ny)
                    if self._effective_liberties(new_state, group) == 0:
                        if self._uncapturable(other_side, symbol=group[0].get("name") if group else None):
                            continue  # 气尽但不可被吃，保留该组
                        captured_count += len(group)
                        captured_ids.extend(p["id"] for p in group)
                        for p in group:
                            p["is_alive"] = False

        new_state["captures"] = new_state.get("captures", {"black": 0, "white": 0})
        new_state["captures"][side] = new_state["captures"].get(side, 0) + captured_count

        prev_ko = new_state.get("ko_state")
        new_state["ko_state"] = None

        if captured_count == 1:
            board_key = self._get_board_key(new_state)
            if prev_ko and board_key == prev_ko:
                pass
            else:
                new_state["ko_state"] = board_key

        return new_state

    def calculate_liberties(self, board_state: dict, x: int, y: int) -> int:
        """计算指定位置棋子所属连通块的气"""
        piece = self._get_piece_at([x, y], board_state)
        if not piece:
            return 0
        group = self._get_group(board_state, x, y)
        return self._effective_liberties(board_state, group)

    def remove_group(self, board_state: dict, x: int, y: int) -> int:
        """移除指定位置棋子所属的连通块，返回提子数"""
        piece = self._get_piece_at([x, y], board_state)
        if not piece:
            return 0

        group = self._get_group(board_state, x, y)
        if self._uncapturable(group[0]["side"], symbol=group[0].get("name")):
            return 0  # 不可被吃，禁止移除该组
        for p in group:
            p["is_alive"] = False

        return len(group)

    def check_capture_10(self, board_state: dict) -> Optional[str]:
        """检查是否有一方吃子达到10个，返回获胜方"""
        target = self.rules.get("win_conditions", {}).get("capture_10", {}).get("target", 10)
        captures = board_state.get("captures", {"black": 0, "white": 0})
        if captures.get("black", 0) >= target:
            return "black"
        if captures.get("white", 0) >= target:
            return "white"
        return None

    def check_capture_all(self, board_state: dict) -> Optional[str]:
        """检查是否一方棋子被全歼"""
        black_alive = False
        white_alive = False

        for p in board_state.get("pieces", []):
            if p.get("is_alive", True):
                if p["side"] == "black":
                    black_alive = True
                else:
                    white_alive = True

        if not black_alive:
            return "white"
        if not white_alive:
            return "black"
        return None

    def _get_group(self, board_state: dict, x: int, y: int) -> List[dict]:
        """获取连通块（同色相连的棋子）"""
        # 位置索引 O(1) 查找：一次建索引，BFS 全程复用
        pos_idx = self._pos_index(board_state)
        start = pos_idx.get((x, y))
        if not start:
            return []

        group = []
        visited = set()
        stack = [(x, y)]
        target_side = start["side"]

        while stack:
            cx, cy = stack.pop()
            if (cx, cy) in visited:
                continue
            visited.add((cx, cy))

            # 位置索引 O(1) 查找当前格
            current = pos_idx.get((cx, cy))
            if current and current["side"] == target_side:
                group.append(current)
                for dx, dy in self._directions:
                    nx, ny = cx + dx, cy + dy
                    if self._in_bounds(nx, ny) and (nx, ny) not in visited:
                        stack.append((nx, ny))

        return group

    def _count_liberties(self, board_state: dict, group: List[dict]) -> int:
        """计算连通块的气数（通过位置索引 O(1) 判定邻格是否为空）"""
        # 位置索引 O(1) 查找：一次构建即可判定所有邻格占位情况
        pos_idx = self._pos_index(board_state)
        liberties = set()

        for piece in group:
            px, py = piece["position"]
            for dx, dy in self._directions:
                nx, ny = px + dx, py + dy
                if self._in_bounds(nx, ny) and (nx, ny) not in pos_idx:
                    liberties.add((nx, ny))

        actual = len(liberties)
        if not group:
            return actual
        cap = self._liberty_cap(group[0]["side"], symbol=group[0].get("name"))
        if cap is not None:
            return min(actual, cap)
        return actual

    def _is_ko(self, board_state: dict, x: int, y: int, side: str) -> bool:
        """检查是否为打劫"""
        if not self.rules.get("special_rules", {}).get("ko_rule", {}).get("enabled", False):
            return False

        current_key = board_state.get("ko_state")
        if not current_key:
            return False

        test_state = copy.deepcopy(board_state)
        test_state["pieces"].append({
            "id": "test",
            "type": "stone",
            "name": "●",
            "side": side,
            "position": [x, y],
            "is_alive": True,
            "custom_properties": {},
        })

        other_side = "white" if side == "black" else "black"
        for dx, dy in self._directions:
            nx, ny = x + dx, y + dy
            if self._in_bounds(nx, ny):
                piece = self._get_piece_at([nx, ny], test_state)
                if piece and piece["side"] == other_side:
                    group = self._get_group(test_state, nx, ny)
                    if self._count_liberties(test_state, group) == 0:
                        if self._uncapturable(other_side, symbol=group[0].get("name") if group else None):
                            continue
                        for p in group:
                            p["is_alive"] = False

        new_key = self._get_board_key(test_state)
        return new_key == current_key

    def _is_suicide(self, board_state: dict, x: int, y: int, side: str) -> bool:
        """检查是否为自杀（落子后自身无气且不能提子）"""
        if not self.rules.get("special_rules", {}).get("suicide_rule", {}).get("enabled", False):
            return False

        test_state = copy.deepcopy(board_state)
        test_state["pieces"].append({
            "id": "test",
            "type": "stone",
            "name": "●",
            "side": side,
            "position": [x, y],
            "is_alive": True,
            "custom_properties": {},
        })

        other_side = "white" if side == "black" else "black"
        can_capture = False
        for dx, dy in self._directions:
            nx, ny = x + dx, y + dy
            if self._in_bounds(nx, ny):
                piece = self._get_piece_at([nx, ny], test_state)
                if piece and piece["side"] == other_side:
                    group = self._get_group(test_state, nx, ny)
                    if self._count_liberties(test_state, group) == 0:
                        if self._uncapturable(other_side, symbol=group[0].get("name") if group else None):
                            continue  # 敌人不可被吃，不能据此判定可提子
                        can_capture = True
                        break

        if can_capture:
            return False

        group = self._get_group(test_state, x, y)
        return self._count_liberties(test_state, group) == 0

    def _is_forbidden_black(self, board_state: dict, x: int, y: int) -> bool:
        """检查黑棋禁手（三三、四四、长连）"""
        test_state = copy.deepcopy(board_state)
        test_state["pieces"].append({
            "id": "test",
            "type": "stone",
            "name": "●",
            "side": "black",
            "position": [x, y],
            "is_alive": True,
            "custom_properties": {},
        })

        if self._check_overline(test_state, x, y, "black"):
            return True

        if self._count_open_threes(test_state, x, y, "black") >= 2:
            return True

        if self._count_open_fours(test_state, x, y, "black") >= 2:
            return True

        return False

    def _check_line(self, board_state: dict, x: int, y: int, dx: int, dy: int, side: str) -> bool:
        """检查指定方向是否有五连"""
        count = 1

        for step in range(1, 5):
            nx, ny = x + dx * step, y + dy * step
            if not self._in_bounds(nx, ny):
                break
            piece = self._get_piece_at([nx, ny], board_state)
            if not piece or piece["side"] != side:
                break
            count += 1

        for step in range(1, 5):
            nx, ny = x - dx * step, y - dy * step
            if not self._in_bounds(nx, ny):
                break
            piece = self._get_piece_at([nx, ny], board_state)
            if not piece or piece["side"] != side:
                break
            count += 1

        return count >= 5

    def _check_overline(self, board_state: dict, x: int, y: int, side: str) -> bool:
        """检查是否长连（超过五连）"""
        for dx, dy in [(1, 0), (0, 1), (1, 1), (1, -1)]:
            count = 1

            for step in range(1, 7):
                nx, ny = x + dx * step, y + dy * step
                if not self._in_bounds(nx, ny):
                    break
                piece = self._get_piece_at([nx, ny], board_state)
                if not piece or piece["side"] != side:
                    break
                count += 1

            for step in range(1, 7):
                nx, ny = x - dx * step, y - dy * step
                if not self._in_bounds(nx, ny):
                    break
                piece = self._get_piece_at([nx, ny], board_state)
                if not piece or piece["side"] != side:
                    break
                count += 1

            if count > 5:
                return True

        return False

    def _count_open_threes(self, board_state: dict, x: int, y: int, side: str) -> int:
        """计算活三数量"""
        count = 0

        for dx, dy in [(1, 0), (0, 1), (1, 1), (1, -1)]:
            if self._is_open_three(board_state, x, y, dx, dy, side):
                count += 1

        return count

    def _is_open_three(self, board_state: dict, x: int, y: int, dx: int, dy: int, side: str) -> bool:
        """检查指定方向是否为活三"""
        line = []

        for step in range(-4, 5):
            nx, ny = x + dx * step, y + dy * step
            if self._in_bounds(nx, ny):
                piece = self._get_piece_at([nx, ny], board_state)
                line.append(piece["side"] if piece else None)
            else:
                line.append("border")

        center_pos = 4
        pattern = line[center_pos - 2:center_pos + 3]

        open_three_patterns = [
            [None, side, side, side, None],
            [None, side, None, side, side],
            [side, None, side, side, None],
            [None, side, side, None, side],
            [side, side, None, side, None],
        ]

        return pattern in open_three_patterns

    def _count_open_fours(self, board_state: dict, x: int, y: int, side: str) -> int:
        """计算冲四数量"""
        count = 0

        for dx, dy in [(1, 0), (0, 1), (1, 1), (1, -1)]:
            if self._is_open_four(board_state, x, y, dx, dy, side):
                count += 1

        return count

    def _is_open_four(self, board_state: dict, x: int, y: int, dx: int, dy: int, side: str) -> bool:
        """检查指定方向是否为冲四"""
        line = []

        for step in range(-4, 5):
            nx, ny = x + dx * step, y + dy * step
            if self._in_bounds(nx, ny):
                piece = self._get_piece_at([nx, ny], board_state)
                line.append(piece["side"] if piece else None)
            else:
                line.append("border")

        center_pos = 4
        pattern = line[center_pos - 2:center_pos + 3]

        open_four_patterns = [
            [None, side, side, side, side],
            [side, None, side, side, side],
            [side, side, None, side, side],
            [side, side, side, None, side],
            [side, side, side, side, None],
        ]

        return pattern in open_four_patterns

    def _get_board_key(self, board_state: dict) -> str:
        """生成棋盘状态的唯一标识（用于打劫检测）"""
        pieces = []
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True):
                pieces.append((p["position"][0], p["position"][1], p["side"]))

        pieces.sort()
        return str(pieces)

    def _in_bounds(self, x: int, y: int) -> bool:
        """判断是否在棋盘范围内"""
        geometry = self.board_config.get("geometry", {})
        width = geometry.get("width", 19)
        height = geometry.get("height", 19)
        return 0 <= x < width and 0 <= y < height

    def _get_width(self) -> int:
        """获取棋盘宽度"""
        return self.board_config.get("geometry", {}).get("width", 19)

    def _get_height(self) -> int:
        """获取棋盘高度"""
        return self.board_config.get("geometry", {}).get("height", 19)

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

    def _get_piece_at(self, pos: List[int], board_state: dict) -> Optional[dict]:
        """获取指定位置的棋子（位置→棋子 O(1) 查找）"""
        # 位置索引 O(1) 查找
        pos_idx = self._pos_index(board_state)
        return pos_idx.get((pos[0], pos[1]))