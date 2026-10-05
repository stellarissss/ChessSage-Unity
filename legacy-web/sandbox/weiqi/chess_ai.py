"""
围棋AI引擎 v3.0 - 极致性能优化版
核心算法：MCTS + UCT + RAVE/AMAF + 先验概率启发式
性能优化：
- FastBoard二维数组棋盘，O(1)位置访问
- make/unmake模式，零deepcopy开销
- RAVE (Rapid Action Value Estimation) 加速收敛
- 候选落子剪枝（周围2格 + 星位）
- 启发式先验概率指导搜索
- 时间限制搜索，响应稳定
"""
import random
import math
import time
from typing import Dict, Any, List, Tuple, Optional
from rule_engine import RuleEngine, FastBoard


class MCTSNode:
    """MCTS搜索树节点 - 轻量版"""

    __slots__ = [
        "parent", "move_x", "move_y", "children", "visits",
        "value", "prior", "rave_visits", "rave_value",
        "_untried_mask", "_children_list",
    ]

    def __init__(self, parent=None, move_x: int = -1, move_y: int = -1, prior: float = 0.0):
        self.parent = parent
        self.move_x = move_x
        self.move_y = move_y
        self.children: Dict[int, "MCTSNode"] = {}
        self.visits = 0
        self.value = 0.0
        self.prior = prior
        self.rave_visits = 0
        self.rave_value = 0.0
        self._untried_mask = None
        self._children_list = None

    def uct_rave(self, parent_visits: int, exploration: float = 1.414,
                 rave_weight: float = 300.0) -> float:
        """UCT+RAVE混合公式"""
        if self.visits == 0:
            return float("inf") + random.random() * 0.01

        beta = self.rave_visits / (self.visits + self.rave_visits + 4 * self.visits * self.rave_visits / (rave_weight * rave_weight) + 1e-6)
        beta = min(1.0, max(0.0, beta))

        avg_value = self.value / self.visits
        rave_avg = self.rave_value / self.rave_visits if self.rave_visits > 0 else avg_value
        mixed = (1 - beta) * avg_value + beta * rave_avg

        ucb = exploration * math.sqrt(math.log(parent_visits) / self.visits)
        prior_bonus = 0.1 * self.prior / (1 + self.visits)

        return mixed + ucb + prior_bonus

    def best_child(self, parent_visits: int, exploration: float = 1.414) -> Optional["MCTSNode"]:
        if not self.children:
            return None
        return max(self.children.values(), key=lambda c: c.uct_rave(parent_visits, exploration))

    def most_visited_child(self) -> Optional["MCTSNode"]:
        if not self.children:
            return None
        return max(self.children.values(), key=lambda c: c.visits)


class GoAI:
    """高性能围棋AI引擎 v3.0"""

    def __init__(self, board: dict, pieces_red: dict, pieces_black: dict, rules: dict,
                 difficulty: str = "medium", api_key: str = "", base_url: str = "",
                 token_stats_callback=None):
        self.rule_engine = RuleEngine(board, pieces_red, pieces_black, rules)
        self.difficulty = difficulty
        self.api_key = api_key
        self.base_url = base_url
        self._token_stats_callback = token_stats_callback
        self.rules = rules
        self.board_config = board

        levels = rules.get("ai_difficulty", {}).get("levels", {})
        level_cfg = levels.get(difficulty, levels.get("medium", {}))
        self.depth = level_cfg.get("depth", 3)

        self.time_limit = {
            "easy": 0.8,
            "medium": 2.5,
            "hard": 6.0,
        }.get(difficulty, 2.5)

        self.simulations_per_second = 0
        self.randomness = level_cfg.get("randomness", 0.1)

        self.personality_aggressiveness = 0.5
        self.personality_conservatism = 0.5

        self._width = board.get("geometry", {}).get("width", 19)
        self._height = board.get("geometry", {}).get("height", 19)
        self._star_points = board.get("geometry", {}).get("star_points", [])
        self._star_set = set(tuple(p) for p in self._star_points)

        self._ko_enabled = rules.get("special_rules", {}).get("ko_rule", {}).get("enabled", False)
        self._suicide_enabled = rules.get("special_rules", {}).get("suicide_rule", {}).get("enabled", True)
        self._capture_target = rules.get("win_conditions", {}).get("capture_10", {}).get("target", 10)

    def set_difficulty(self, difficulty: str):
        self.difficulty = difficulty
        self.time_limit = {
            "easy": 0.8,
            "medium": 2.5,
            "hard": 6.0,
        }.get(difficulty, 2.5)
        self.randomness = {
            "easy": 0.3,
            "medium": 0.1,
            "hard": 0.0,
        }.get(difficulty, 0.1)

    def set_api_key(self, api_key: str):
        self.api_key = api_key

    def get_best_move(self, board_state: dict) -> Optional[Dict[str, Any]]:
        side = board_state.get("current_turn", "black")
        color = FastBoard.BLACK if side == "black" else FastBoard.WHITE

        if self.randomness > 0 and random.random() < self.randomness:
            move = self._get_random_move(board_state, side)
            if move:
                return move

        fast_board = FastBoard.from_state(board_state, self._width, self._height).configure_modifiers(self.rules)
        candidates = self._get_candidate_moves_fast(fast_board, color)

        if not candidates:
            return None

        if len(candidates) == 1:
            return self._move_from_pos_fast(candidates[0], fast_board, board_state)

        best_pos = self._mcts_search(fast_board, candidates, color)

        if best_pos is None:
            best_pos = self._greedy_best_fast(fast_board, candidates, color)

        return self._move_from_pos_fast(best_pos, fast_board, board_state)

    def _mcts_search(self, board: FastBoard, candidates: List[Tuple[int, int]],
                     root_color: int) -> Optional[Tuple[int, int]]:
        deadline = time.time() + self.time_limit
        root = MCTSNode()

        scored_candidates = []
        for (cx, cy) in candidates:
            score = self._heuristic_prior(board, cx, cy, root_color)
            scored_candidates.append((cx, cy, score))

        max_score = max(s for _, _, s in scored_candidates) if scored_candidates else 1.0
        if max_score > 0:
            priors = {
                (cx, cy): 0.1 + 0.9 * (score / max_score)
                for cx, cy, score in scored_candidates
            }
        else:
            priors = {(cx, cy): 1.0 / len(scored_candidates) for cx, cy, _ in scored_candidates}

        simulations = 0
        start_time = time.time()

        while time.time() < deadline:
            board_copy = board.clone()
            node = root
            current_color = root_color
            path_moves = []
            path_colors = []

            while True:
                if not node.children:
                    break

                valid_children = {}
                for key, child in node.children.items():
                    if board_copy.is_valid_move(child.move_x, child.move_y, current_color,
                                                self._ko_enabled, self._suicide_enabled):
                        valid_children[key] = child

                if not valid_children:
                    break

                node.children = valid_children
                best = max(valid_children.values(),
                           key=lambda c: c.uct_rave(node.visits, 1.414))
                node = best
                captured = board_copy.get_captured_stones(best.move_x, best.move_y, current_color)
                prev_ko = board_copy.ko_point
                board_copy.play_move(best.move_x, best.move_y, current_color)
                path_moves.append((best.move_x, best.move_y, captured, prev_ko))
                path_colors.append(current_color)
                current_color = FastBoard.WHITE if current_color == FastBoard.BLACK else FastBoard.BLACK

            if node.visits > 0 and simulations > 50:
                new_candidates = self._get_candidate_moves_fast(board_copy, current_color)
                if new_candidates:
                    for (cx, cy) in new_candidates[:15]:
                        key = cx * 1000 + cy
                        if key not in node.children:
                            prior = priors.get((cx, cy), 0.5)
                            child = MCTSNode(parent=node, move_x=cx, move_y=cy, prior=prior)
                            node.children[key] = child
                    if node.children:
                        best_child = max(node.children.values(),
                                         key=lambda c: c.prior + random.random() * 0.1)
                        captured = board_copy.get_captured_stones(best_child.move_x, best_child.move_y, current_color)
                        prev_ko = board_copy.ko_point
                        board_copy.play_move(best_child.move_x, best_child.move_y, current_color)
                        path_moves.append((best_child.move_x, best_child.move_y, captured, prev_ko))
                        path_colors.append(current_color)
                        current_color = FastBoard.WHITE if current_color == FastBoard.BLACK else FastBoard.BLACK
                        node = best_child

            result, sim_moves = self._fast_rollout(board_copy, current_color)

            self._backpropagate(node, path_colors, result, sim_moves, root_color)

            simulations += 1

        elapsed = time.time() - start_time
        self.simulations_per_second = simulations / max(elapsed, 0.001)

        best_child = root.most_visited_child()
        if best_child and best_child.move_x >= 0:
            return (best_child.move_x, best_child.move_y)

        return None

    def _fast_rollout(self, board: FastBoard, start_color: int) -> Tuple[float, List[Tuple[int, int, int]]]:
        """快速随机对局（极轻量模拟），返回(胜负值, 落子序列[(x,y,color),...])"""
        current_color = start_color
        moves = []
        max_moves = 16
        consecutive_passes = 0
        w, h = self._width, self._height

        for _ in range(max_moves):
            if self._check_win_fast(board):
                winner = self._get_winner_fast(board)
                val = 1.0 if winner == start_color else -1.0
                return val, moves

            move = self._rollout_pick_move(board, current_color, w, h)
            if move is None:
                consecutive_passes += 1
                if consecutive_passes >= 2:
                    break
                current_color = FastBoard.WHITE if current_color == FastBoard.BLACK else FastBoard.BLACK
                continue
            consecutive_passes = 0

            mx, my = move
            board.play_move(mx, my, current_color)
            moves.append((mx, my, current_color))
            current_color = FastBoard.WHITE if current_color == FastBoard.BLACK else FastBoard.BLACK

        val = self._evaluate_position_fast(board, start_color)
        return val, moves

    def _rollout_pick_move(self, board: FastBoard, color: int, w: int, h: int) -> Optional[Tuple[int, int]]:
        """rollout选棋：从周围空位中快速挑选，不做完整合法性验证"""
        candidates = []
        found_any = False
        opp = FastBoard.WHITE if color == FastBoard.BLACK else FastBoard.BLACK

        for x in range(w):
            row = board.board[x]
            for y in range(h):
                if row[y] == FastBoard.EMPTY:
                    has_neighbor = False
                    opp_adj = 0
                    my_adj = 0
                    for dx, dy in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
                        nx, ny = x + dx, y + dy
                        if 0 <= nx < w and 0 <= ny < h:
                            v = board.board[nx][ny]
                            if v != FastBoard.EMPTY:
                                has_neighbor = True
                                if v == color:
                                    my_adj += 1
                                else:
                                    opp_adj += 1
                    if has_neighbor:
                        found_any = True
                        score = my_adj * 2 + opp_adj * 3
                        if opp_adj > 0:
                            score += 10
                        candidates.append((score + random.random(), x, y))

        if not found_any:
            cx, cy = w // 2, h // 2
            if board.board[cx][cy] == FastBoard.EMPTY:
                return (cx, cy)
            return None

        candidates.sort(reverse=True, key=lambda t: t[0])
        top_k = max(3, min(8, len(candidates) // 4))

        for i in range(top_k):
            _, x, y = candidates[i]
            if board.is_valid_move(x, y, color, self._ko_enabled, self._suicide_enabled):
                return (x, y)

        for _, x, y in candidates:
            if board.is_valid_move(x, y, color, self._ko_enabled, self._suicide_enabled):
                return (x, y)

        return None

    def _quick_score_fast(self, board: FastBoard, x: int, y: int, color: int) -> float:
        """快速落子评估（纯启发式，不做完整落子）"""
        score = 0.0
        opp = FastBoard.WHITE if color == FastBoard.BLACK else FastBoard.BLACK

        my_neighbors = board.count_neighbors(x, y, color)
        opp_neighbors = board.count_neighbors(x, y, opp)

        score += my_neighbors * 3.0
        score += opp_neighbors * 5.0

        if opp_neighbors > 0:
            captured_test = board.get_captured_stones(x, y, color)
            score += len(captured_test) * 50.0

        center_x = self._width / 2.0
        center_y = self._height / 2.0
        dist_center = abs(x - center_x) + abs(y - center_y)
        score += max(0.0, 8.0 - dist_center * 0.4)

        if (x, y) in self._star_set:
            score += 3.0

        return score

    def _heuristic_prior(self, board: FastBoard, x: int, y: int, color: int) -> float:
        """先验概率评估（用于MCTS节点先验）"""
        return self._quick_score_fast(board, x, y, color) + 1.0

    def _backpropagate(self, leaf: MCTSNode, path_colors: List[int],
                       result: float, sim_moves: List[Tuple[int, int, int]],
                       root_color: int):
        """反向传播 + RAVE更新"""
        node = leaf
        result_norm = (result + 1.0) / 2.0

        current = leaf
        color_idx = len(path_colors) - 1
        while current is not None and color_idx >= -1:
            if color_idx >= 0:
                c = path_colors[color_idx]
            else:
                c = root_color

            current.visits += 1
            if c == root_color:
                current.value += result_norm
            else:
                current.value += 1.0 - result_norm

            current = current.parent
            color_idx -= 1

        for (mx, my, move_color) in sim_moves:
            node = leaf
            color_idx = len(path_colors) - 1
            found = False

            while node is not None:
                if node.move_x == mx and node.move_y == my and node.parent is not None:
                    node.rave_visits += 1
                    if move_color == root_color:
                        node.rave_value += result_norm
                    else:
                        node.rave_value += 1.0 - result_norm
                    found = True
                    break

                key = mx * 1000 + my
                if key in node.children:
                    child = node.children[key]
                    child.rave_visits += 1
                    if move_color == root_color:
                        child.rave_value += result_norm
                    else:
                        child.rave_value += 1.0 - result_norm

                node = node.parent
                color_idx -= 1

    def _evaluate_position_fast(self, board: FastBoard, color: int) -> float:
        """快速局面评估（-1到1）"""
        if self._check_win_fast(board):
            winner = self._get_winner_fast(board)
            return 1.0 if winner == color else -1.0

        opp = FastBoard.WHITE if color == FastBoard.BLACK else FastBoard.BLACK

        my_caps = board.captures[color]
        opp_caps = board.captures[opp]

        cap_diff = my_caps - opp_caps
        cap_score = cap_diff / self._capture_target
        cap_score = max(-1.0, min(1.0, cap_score))

        my_stones = len(board.get_all_stones(color))
        opp_stones = len(board.get_all_stones(opp))
        stone_score = (my_stones - opp_stones) / max(my_stones + opp_stones, 1) * 0.3

        return max(-1.0, min(1.0, cap_score * 0.8 + stone_score * 0.2))

    def _check_win_fast(self, board: FastBoard) -> bool:
        return (board.captures[FastBoard.BLACK] >= self._capture_target or
                board.captures[FastBoard.WHITE] >= self._capture_target)

    def _get_winner_fast(self, board: FastBoard) -> int:
        if board.captures[FastBoard.BLACK] >= self._capture_target:
            return FastBoard.BLACK
        return FastBoard.WHITE

    def _greedy_best_fast(self, board: FastBoard, candidates: List[Tuple[int, int]],
                          color: int) -> Tuple[int, int]:
        best_score = float("-inf")
        best_move = candidates[0]

        for pos in candidates[:30]:
            test_board = board.clone()
            ok, captured = test_board.play_move(pos[0], pos[1], color)
            if ok:
                score = self._quick_score_fast(board, pos[0], pos[1], color)
                score += captured * 100
                if score > best_score:
                    best_score = score
                    best_move = pos

        return best_move

    def _get_empty_neighbors(self, board: FastBoard, radius: int = 2) -> List[Tuple[int, int]]:
        """获取所有已有棋子周围radius格内的空位（不验证合法性）"""
        candidates = set()
        w, h = self._width, self._height

        for x in range(w):
            row = board.board[x]
            for y in range(h):
                if row[y] != FastBoard.EMPTY:
                    for dx in range(-radius, radius + 1):
                        nx = x + dx
                        if nx < 0 or nx >= w:
                            continue
                        for dy in range(-radius, radius + 1):
                            ny = y + dy
                            if 0 <= ny < h and board.board[nx][ny] == FastBoard.EMPTY:
                                candidates.add((nx, ny))

        return list(candidates)

    def _filter_valid_moves(self, board: FastBoard, candidates: List[Tuple[int, int]],
                            color: int) -> List[Tuple[int, int]]:
        """从候选中过滤出合法落子"""
        valid = []
        for (x, y) in candidates:
            if board.is_valid_move(x, y, color, self._ko_enabled, self._suicide_enabled):
                valid.append((x, y))
        return valid

    def _get_candidate_moves_fast(self, board: FastBoard, color: int,
                                  max_candidates: int = 40) -> List[Tuple[int, int]]:
        """快速获取候选落子（已有棋子周围2格 + 星位）"""
        empties = self._get_empty_neighbors(board, 2)

        if not empties:
            result = []
            for sp in self._star_points:
                sx, sy = sp[0], sp[1]
                if board.board[sx][sy] == FastBoard.EMPTY:
                    if board.is_valid_move(sx, sy, color, self._ko_enabled, self._suicide_enabled):
                        result.append((sx, sy))
            if not result:
                cx, cy = self._width // 2, self._height // 2
                if board.board[cx][cy] == FastBoard.EMPTY:
                    if board.is_valid_move(cx, cy, color, self._ko_enabled, self._suicide_enabled):
                        result.append((cx, cy))
            return result

        if len(empties) <= max_candidates:
            return self._filter_valid_moves(board, empties, color)

        scored = []
        opp = FastBoard.WHITE if color == FastBoard.BLACK else FastBoard.BLACK
        for (x, y) in empties:
            s = 0.0
            my_n = 0
            opp_n = 0
            for dx, dy in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
                nx, ny = x + dx, y + dy
                if 0 <= nx < self._width and 0 <= ny < self._height:
                    v = board.board[nx][ny]
                    if v == color:
                        my_n += 1
                    elif v == opp:
                        opp_n += 1
            s += my_n * 3 + opp_n * 5
            if (x, y) in self._star_set:
                s += 2
            scored.append((s, (x, y)))

        scored.sort(reverse=True, key=lambda x: x[0])
        top = [m for _, m in scored[:max_candidates * 2]]
        return self._filter_valid_moves(board, top, color)[:max_candidates]

    def _get_random_move(self, board_state: dict, side: str) -> Optional[dict]:
        fast_board = FastBoard.from_state(board_state, self._width, self._height).configure_modifiers(self.rules)
        color = FastBoard.BLACK if side == "black" else FastBoard.WHITE
        candidates = self._get_candidate_moves_fast(fast_board, color)
        if not candidates:
            return None
        pos = random.choice(candidates)
        return self._move_from_pos_fast(pos, fast_board, board_state)

    def _move_from_pos_fast(self, pos: Tuple[int, int], fast_board: FastBoard,
                            board_state: dict) -> dict:
        test = fast_board.clone()
        color = FastBoard.BLACK if board_state.get("current_turn", "black") == "black" else FastBoard.WHITE
        ok, captured_count = test.play_move(pos[0], pos[1], color)

        captured_ids = []
        if captured_count > 0:
            opp = "white" if color == FastBoard.BLACK else "black"
            for p in board_state.get("pieces", []):
                if p.get("is_alive", True) and p["side"] == opp:
                    px, py = p["position"]
                    if test.board[px][py] == FastBoard.EMPTY:
                        captured_ids.append(p["id"])

        return {
            "piece_id": None,
            "from": None,
            "to": [pos[0], pos[1]],
            "captured": captured_ids,
        }

    async def precompute_custom_piece_values(self):
        pass
