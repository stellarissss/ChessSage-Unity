"""
AI下棋引擎（黑白棋 / Othello） - 使用 Minimax + Alpha-Beta 剪枝
评估函数：角点/位置权重表 + 行动力 + 棋子数差 + 边缘控制 + 稳定性

注：文件名保留 chess_ai.py 以最小化 main.py 引用改动；类名为 OthelloAI。
"""
import copy
import random
from typing import Dict, Any, List, Optional
from rule_engine import RuleEngine

# 经典 Othello 8×8 角点/位置权重表（按 [x][y] 索引；该矩阵关于主对角线对称）
CORNER_WEIGHTS = [
    [120, -20,  20,   5,   5,  20, -20, 120],
    [-20, -40,  -5,  -5,  -5,  -5, -40, -20],
    [ 20,  -5,  15,   3,   3,  15,  -5,  20],
    [  5,  -5,   3,   3,   3,   3,  -5,   5],
    [  5,  -5,   3,   3,   3,   3,  -5,   5],
    [ 20,  -5,  15,   3,   3,  15,  -5,  20],
    [-20, -40,  -5,  -5,  -5,  -5, -40, -20],
    [120, -20,  20,   5,   5,  20, -20, 120],
]

# 4 个角点
CORNERS = [(0, 0), (7, 0), (0, 7), (7, 7)]


class OthelloAI:
    """黑白棋 AI 下棋引擎"""

    # 评估权重基数
    MOBILITY_WEIGHT = 5.0       # 行动力（高权重，符合 Othello 经典理论）
    STABLE_DISC_VALUE = 10      # 单颗稳定棋子价值
    DISC_DIFF_WEIGHT = 1.0      # 棋子数差
    EDGE_WEIGHT = 2.0           # 边缘控制（非角点边格）

    def __init__(self, board: dict, pieces_black: dict, pieces_white: dict, rules: dict,
                 difficulty: str = "medium", api_key: str = "",
                 base_url: str = "https://api.deepseek.com/v1",
                 token_stats_callback=None):
        """
        黑白棋双阵营（black / white），由 rule_engine 统一调度。
        """
        self.rule_engine = RuleEngine(board, pieces_black, pieces_white, rules)
        self.board_config = board
        self.rules = rules
        self.api_key = api_key
        self.base_url = base_url
        self._token_stats_callback = token_stats_callback

        levels = rules.get("ai_difficulty", {}).get("levels", {})
        level_cfg = levels.get(difficulty, levels.get("medium", {}))
        self.depth = level_cfg.get("depth", 3)
        self.randomness = level_cfg.get("randomness", 0.1)

        # 性格参数（由 mechanism_engine.apply_personality_to_ai 覆盖）
        self.personality_aggressiveness = 0.5
        self.personality_conservatism = 0.5
        self.personality_value_biases: Dict[str, float] = {}

    # ════════════════════════════════════════════════════════════
    # 通用方法
    # ════════════════════════════════════════════════════════════
    def set_difficulty(self, difficulty: str):
        """设置难度（从 rules.ai_difficulty.levels 读取深度/随机度）"""
        self.difficulty = difficulty
        levels = self.rules.get("ai_difficulty", {}).get("levels", {})
        level_cfg = levels.get(difficulty, levels.get("medium", {}))
        self.depth = level_cfg.get("depth", 3)
        self.randomness = level_cfg.get("randomness", 0.1)

    def set_api_key(self, api_key: str):
        """更新 API 密钥（无需重建引擎）"""
        self.api_key = api_key

    def get_token_stats(self) -> Dict[str, Any]:
        """获取 token 统计

        黑白棋 AI 不直接调用大模型（标准 disc 无价值差异），
        token 统计由 ai_orchestrator 统一维护，此处返回空字典占位。
        """
        return {}

    # ════════════════════════════════════════════════════════════
    # 入口：获取最佳落子
    # ════════════════════════════════════════════════════════════
    def get_best_move(self, board_state: dict) -> Optional[Dict[str, Any]]:
        """获取 AI 的最佳落子

        Returns:
            {"to": [x, y], "side": side} 或 None（无合法落子）
        """
        side = board_state.get("current_turn", "black")

        # 随机性控制：高随机度（如 random 性格）可能直接随机落子
        if self.randomness > 0 and random.random() < self.randomness:
            move = self._get_random_move(board_state, side)
            if move:
                return move

        best_move = None
        best_score = float("-inf")
        alpha = float("-inf")
        beta = float("inf")

        moves = self._generate_all_moves(board_state, side)
        if not moves:
            return None

        # 按落子即时收益排序，提高剪枝效率
        moves.sort(key=lambda m: self._move_score(m, board_state), reverse=True)

        for move in moves:
            new_state = self._simulate_move(board_state, move)
            score = self._minimax(new_state, self.depth - 1, alpha, beta, False, side)
            if score > best_score:
                best_score = score
                best_move = move
            alpha = max(alpha, score)

        return best_move

    def _get_random_move(self, board_state: dict, side: str) -> Optional[dict]:
        """随机选择一个合法落子"""
        moves = self._generate_all_moves(board_state, side)
        if not moves:
            return None
        return random.choice(moves)

    # ════════════════════════════════════════════════════════════
    # 落子生成 / 模拟
    # ════════════════════════════════════════════════════════════
    def _generate_all_moves(self, board_state: dict, side: str) -> List[Dict[str, Any]]:
        """生成某方所有合法落子点

        Returns:
            [{"to": [x, y], "side": side}, ...]
        """
        placements = self.rule_engine.get_valid_placements(side, board_state)
        moves = []
        for pos in placements:
            moves.append({"to": list(pos), "side": side})
        return moves

    def _simulate_move(self, board_state: dict, move: dict) -> dict:
        """模拟执行落子（含翻转），返回新的棋盘状态

        步骤：
        1. 深拷贝 board_state
        2. 新增一颗 disc（id 用 temp_disc_N，保证在该 state 内唯一）
        3. 调用 rule_engine.apply_flip_captures 修改被夹吃棋子的 side
        4. 切换回合
        """
        new_state = copy.deepcopy(board_state)
        side = move["side"]
        pos = list(move["to"])

        # 新增一颗 disc，id 在当前 state 内唯一
        existing_ids = {p.get("id") for p in new_state.get("pieces", [])}
        n = 0
        while f"temp_disc_{n}" in existing_ids:
            n += 1
        new_disc = {
            "id": f"temp_disc_{n}",
            "type": "disc",
            "name": "黑棋" if side == "black" else "白棋",
            "side": side,
            "position": pos,
            "is_alive": True,
            "custom_properties": {},
        }
        new_state.setdefault("pieces", []).append(new_disc)

        # 执行翻转：修改被夹吃棋子的 side（黑白棋翻转后棋子仍在原位，仅阵营改变）
        try:
            self.rule_engine.apply_flip_captures(pos, side, new_state)
        except Exception:
            # rule_engine 不可用或签名不一致时退化为仅落子（不中断搜索）
            pass

        # 切换回合
        new_state["current_turn"] = "white" if side == "black" else "black"
        return new_state

    def _move_score(self, move: dict, board_state: dict) -> int:
        """评估落子的即时分数（用于排序），基于位置权重表"""
        x, y = move["to"]
        return self._get_corner_weight((x, y))

    # ════════════════════════════════════════════════════════════
    # Minimax + Alpha-Beta 剪枝
    # ════════════════════════════════════════════════════════════
    def _minimax(self, board_state: dict, depth: int, alpha: float, beta: float,
                 is_max: bool, ai_side: str) -> float:
        """Minimax + Alpha-Beta 剪枝（黑白棋版本）

        终止条件：
        - 深度为 0 → 调用 _evaluate
        - 当前方无合法落子 → 返回负分（简化处理：不模拟跳过回合）
        """
        if depth == 0:
            return self._evaluate(board_state, ai_side)

        current_side = board_state.get("current_turn", "black")
        moves = self._generate_all_moves(board_state, current_side)

        if not moves:
            # 无合法落子：对当前方不利
            return -10000 if current_side == ai_side else 10000

        if is_max:
            max_eval = float("-inf")
            for move in moves:
                new_state = self._simulate_move(board_state, move)
                eval_score = self._minimax(new_state, depth - 1, alpha, beta, False, ai_side)
                max_eval = max(max_eval, eval_score)
                alpha = max(alpha, eval_score)
                if beta <= alpha:
                    break
            return max_eval
        else:
            min_eval = float("inf")
            for move in moves:
                new_state = self._simulate_move(board_state, move)
                eval_score = self._minimax(new_state, depth - 1, alpha, beta, True, ai_side)
                min_eval = min(min_eval, eval_score)
                beta = min(beta, eval_score)
                if beta <= alpha:
                    break
            return min_eval

    # ════════════════════════════════════════════════════════════
    # 评估函数（核心）
    # ════════════════════════════════════════════════════════════
    def _evaluate(self, board_state: dict, ai_side: str) -> float:
        """评估棋盘局面（含性格影响）

        组成：
        1. 棋子数差（当前方 - 对方）
        2. 角点 / 位置权重表（CORNER_WEIGHTS 逐子累加）
        3. 行动力（mobility）：当前方合法落子数 - 对方合法落子数
        4. 边缘控制：四条边非角点格子占有差
        5. 稳定性（简化版）：稳定棋子数差
        """
        agg = getattr(self, "personality_aggressiveness", 0.5)
        cons = getattr(self, "personality_conservatism", 0.5)
        opp_side = "white" if ai_side == "black" else "black"

        # 性格驱动的连续权重缩放：
        #   aggressive（agg↑）→ 行动力权重提升（接近 ×2）
        #   defensive（cons↑）→ 稳定性权重提升（接近 ×2）
        mobility_w = self.MOBILITY_WEIGHT * (0.5 + agg)
        stability_w = self.STABLE_DISC_VALUE * (0.5 + cons)
        disc_w = self.DISC_DIFF_WEIGHT
        edge_w = self.EDGE_WEIGHT

        # 1. 棋子数差
        my_discs = self._count_discs(board_state, ai_side)
        opp_discs = self._count_discs(board_state, opp_side)
        score = (my_discs - opp_discs) * disc_w

        # 2. 角点 / 位置权重表
        positional = 0
        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True):
                continue
            w = self._get_corner_weight(tuple(p["position"]))
            if p["side"] == ai_side:
                positional += w
            else:
                positional -= w
        score += positional

        # 3. 行动力
        my_moves = len(self._generate_all_moves(board_state, ai_side))
        opp_moves = len(self._generate_all_moves(board_state, opp_side))
        score += (my_moves - opp_moves) * mobility_w

        # 4. 边缘控制
        my_edges = self._count_edge_discs(board_state, ai_side)
        opp_edges = self._count_edge_discs(board_state, opp_side)
        score += (my_edges - opp_edges) * edge_w

        # 5. 稳定性
        my_stable = self._get_stable_discs(board_state, ai_side)
        opp_stable = self._get_stable_discs(board_state, opp_side)
        score += (my_stable - opp_stable) * stability_w

        # random 性格：高随机度时对最终评估施加 ±30% 抖动
        if self.randomness >= 0.3:
            score *= (1.0 + random.uniform(-0.3, 0.3))

        return score

    # ════════════════════════════════════════════════════════════
    # 辅助方法
    # ════════════════════════════════════════════════════════════
    def _get_corner_weight(self, pos) -> int:
        """返回某位置的角点 / 位置权重；越界返回 0"""
        x, y = pos
        if 0 <= x < len(CORNER_WEIGHTS) and 0 <= y < len(CORNER_WEIGHTS[0]):
            return CORNER_WEIGHTS[x][y]
        return 0

    def _count_discs(self, board_state: dict, side: str) -> int:
        """统计某方棋子数"""
        cnt = 0
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True) and p.get("side") == side:
                cnt += 1
        return cnt

    def _count_edge_discs(self, board_state: dict, side: str) -> int:
        """统计某方在四条边（非角点）上的棋子数"""
        cnt = 0
        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True) or p.get("side") != side:
                continue
            x, y = p["position"]
            is_edge = (x == 0 or x == 7 or y == 0 or y == 7)
            is_corner = (x, y) in CORNERS
            if is_edge and not is_corner:
                cnt += 1
        return cnt

    def _get_stable_discs(self, board_state: dict, side: str) -> int:
        """简化版稳定棋子数：角点 + 沿角点方向连续的同色棋子

        对每个角点：若角点为 side，则从角点出发沿行、列、对角线三个方向
        数连续同色棋子（角点本身只计一次，使用 set 去重）。
        """
        occupied = {}
        for p in board_state.get("pieces", []):
            if p.get("is_alive", True):
                occupied[tuple(p["position"])] = p.get("side")

        stable = set()

        # 每个角点对应 3 个延伸方向（行、列、对角线，均指向棋盘内部）
        corner_dirs = {
            (0, 0): [(1, 0), (0, 1), (1, 1)],
            (7, 0): [(-1, 0), (0, 1), (-1, 1)],
            (0, 7): [(1, 0), (0, -1), (1, -1)],
            (7, 7): [(-1, 0), (0, -1), (-1, -1)],
        }

        for corner, dirs in corner_dirs.items():
            if occupied.get(corner) != side:
                continue
            stable.add(corner)
            for dx, dy in dirs:
                cx, cy = corner
                while True:
                    cx += dx
                    cy += dy
                    if not (0 <= cx < 8 and 0 <= cy < 8):
                        break
                    if occupied.get((cx, cy)) == side:
                        stable.add((cx, cy))
                    else:
                        break

        return len(stable)


# 向后兼容别名：main.py 等模块仍按 ChessAI 引用
ChessAI = OthelloAI
