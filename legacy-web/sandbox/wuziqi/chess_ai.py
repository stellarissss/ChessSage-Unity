"""
五子棋AI引擎 - 经典棋型评估 + Minimax + Alpha-Beta剪枝 + Zobrist置换表
对齐象棋的大模型驱动模式（自定义棋子价值评估 + value_biases）
"""
import json
import re
import random
import math
from typing import Dict, Any, List, Tuple, Optional
from rule_engine import RuleEngine

# ── 模型真源导入（复用逻辑说明）────────────────────────────────
# 本文件不写死 DeepSeek 模型名，统一从 shared/ai_config.py 的 get_model() 取。
# 顶层棋类与 sandbox 棋类因此共享同一模型配置（根 config.json / 环境变量
# DEEPSEEK_MODEL），换模型只需改配置真源一处，避免多处漏改。
# ────────────────────────────────────────────────────────────
try:
    from shared.ai_config import get_model, get_no_think_params
except ImportError:  # Fallback：shared/ 已在 sys.path 时（顶层 main.py 的注入方式）
    from ai_config import get_model, get_no_think_params  # type: ignore

# 棋型评分常量
SCORE_FIVE = 100000
SCORE_LIVE_FOUR = 10000
SCORE_RUSH_FOUR = 1000
SCORE_LIVE_THREE = 500
SCORE_SLEEP_THREE = 100
SCORE_LIVE_TWO = 50
SCORE_SLEEP_TWO = 10
SCORE_ONE = 1

# 默认搜索深度（与 rules.json 保持一致，作为兜底）
AI_SEARCH_DEPTH = {
    "easy": 1,
    "medium": 2,
    "hard": 4,
}

AI_CANDIDATE_RADIUS = 2
AI_MAX_CANDIDATES = 20

# 标准棋子价值（对齐象棋三层价值来源：标准表 → LLM缓存 → 启发式兜底）
PIECE_VALUES = {
    "stone": 100,
}

# Zobrist 置换表标志
TT_EXACT = 0  # 精确值（未触发剪枝）
TT_LOWER = 1  # 下界（maximizing 触发 beta 剪枝）
TT_UPPER = 2  # 上界（minimizing 触发 alpha 剪枝）


class GomokuAI:
    """五子棋AI引擎 - 启发式搜索 + 大模型驱动棋子价值评估"""

    def __init__(self, board: dict, pieces_red: dict, pieces_black: dict, rules: dict,
                 difficulty: str = "medium", api_key: str = "", base_url: str = "",
                 token_stats_callback=None):
        self.rule_engine = RuleEngine(board, pieces_red, pieces_black, rules)
        self.difficulty = difficulty
        self._pieces_by_side = {"white": pieces_red, "black": pieces_black}
        self._custom_pieces_by_side = {
            "white": pieces_red.get("custom_pieces", []),
            "black": pieces_black.get("custom_pieces", []),
        }
        self.custom_pieces = pieces_red.get("custom_pieces", []) + pieces_black.get("custom_pieces", [])
        self.api_key = api_key
        self.base_url = base_url or "https://api.deepseek.com/v1"
        self._custom_piece_values: Dict[str, int] = {}
        self._token_stats_callback = token_stats_callback

        self.board_width = board.get("geometry", {}).get("width", 15)
        self.board_height = board.get("geometry", {}).get("height", 15)

        levels = rules.get("ai_difficulty", {}).get("levels", {})
        level_cfg = levels.get(difficulty, levels.get("medium", {}))
        self.depth = level_cfg.get("depth", AI_SEARCH_DEPTH.get(difficulty, 2))
        self.randomness = level_cfg.get("randomness", 0.1)

        self.personality_aggressiveness = 0.5
        self.personality_conservatism = 0.5
        self.personality_value_biases: Dict[str, float] = {}

        # Zobrist 置换表初始化（固定 seed 保证可重现）
        self._zobrist_rng = random.Random(42)
        self._zobrist_table: Dict[Tuple[int, int, int], int] = {}
        for y in range(self.board_height):
            for x in range(self.board_width):
                # side 1 (red/白子) 和 -1 (black/黑子)
                self._zobrist_table[(x, y, 1)] = self._zobrist_rng.getrandbits(64)
                self._zobrist_table[(x, y, -1)] = self._zobrist_rng.getrandbits(64)
        self._transposition_table: Dict[int, Tuple[int, float, int]] = {}
        self._tt_max_size = 100000

    def set_difficulty(self, difficulty: str):
        self.difficulty = difficulty
        self.depth = AI_SEARCH_DEPTH.get(difficulty, 2)

    def set_personality(self, personality_type: str, aggressiveness: float = 0.5,
                        conservatism: float = 0.5, randomness_override: float = None,
                        depth_override: int = None, value_biases: Dict[str, float] = None,
                        custom_prompt: str = None):
        self.personality_aggressiveness = aggressiveness
        self.personality_conservatism = conservatism
        if randomness_override is not None:
            self.randomness = randomness_override
        if depth_override is not None:
            self.depth = depth_override
        if value_biases:
            self.personality_value_biases = value_biases

    def _board_to_matrix(self, board_state: dict) -> List[List[int]]:
        """将board_state转换为二维矩阵: 0=空, 1=AI方(red/白子), -1=玩家(black/黑子)"""
        w, h = self.board_width, self.board_height
        matrix = [[0] * w for _ in range(h)]
        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True):
                continue
            x, y = p["position"][0], p["position"][1]
            side = 1 if p["side"] == "white" else -1
            if 0 <= x < w and 0 <= y < h:
                matrix[y][x] = side
        return matrix

    def _get_candidate_moves(self, matrix: List[List[int]]) -> List[Tuple[int, int]]:
        """获取候选落子点：只考虑已有棋子周围一定半径内的空位"""
        w, h = self.board_width, self.board_height
        candidates = []
        visited = set()

        has_stones = False
        for y in range(h):
            for x in range(w):
                if matrix[y][x] != 0:
                    has_stones = True
                    for dy in range(-AI_CANDIDATE_RADIUS, AI_CANDIDATE_RADIUS + 1):
                        for dx in range(-AI_CANDIDATE_RADIUS, AI_CANDIDATE_RADIUS + 1):
                            nx, ny = x + dx, y + dy
                            if 0 <= nx < w and 0 <= ny < h and matrix[ny][nx] == 0:
                                if (nx, ny) not in visited:
                                    visited.add((nx, ny))
                                    score = self._evaluate_point_heuristic(matrix, nx, ny)
                                    candidates.append((nx, ny, score))

        if not has_stones:
            cx, cy = w // 2, h // 2
            return [(cx, cy)]

        candidates.sort(key=lambda t: t[2], reverse=True)
        return [(x, y) for x, y, _ in candidates[:AI_MAX_CANDIDATES]]

    def _evaluate_point_heuristic(self, matrix: List[List[int]], x: int, y: int) -> int:
        """启发式评估一个空位的价值（用于候选排序）"""
        score = 0
        for side in (1, -1):
            line_scores = []
            for dx, dy in [(1, 0), (0, 1), (1, 1), (1, -1)]:
                line = self._get_line_with_virtual(matrix, x, y, dx, dy, side)
                line_scores.append(self._score_line_pattern(line))
            side_score = max(line_scores)
            if side == 1:
                score += side_score
            else:
                score += side_score * 0.9
        return score

    def _get_line_with_virtual(self, matrix: List[List[int]], x: int, y: int,
                                dx: int, dy: int, side: int) -> List[int]:
        """获取包含虚拟落子(x,y)的一条线，side是虚拟棋子的颜色"""
        w, h = self.board_width, self.board_height
        line = []
        for i in range(-4, 5):
            nx, ny = x + dx * i, y + dy * i
            if i == 0:
                line.append(side)
            elif 0 <= nx < w and 0 <= ny < h:
                line.append(matrix[ny][nx])
            else:
                line.append(-2)
        return line

    def _score_line_pattern(self, line: List[int]) -> int:
        """对线型模式进行评分，返回分数"""
        n = len(line)
        center = n // 2
        side = line[center]
        if side == 0:
            return 0

        count = 1
        left_block = False
        right_block = False

        i = center - 1
        while i >= 0 and line[i] == side:
            count += 1
            i -= 1
        if i < 0 or line[i] != 0:
            left_block = True

        i = center + 1
        while i < n and line[i] == side:
            count += 1
            i += 1
        if i >= n or line[i] != 0:
            right_block = True

        if count >= 5:
            return SCORE_FIVE

        blocks = int(left_block) + int(right_block)

        if count == 4:
            if blocks == 0:
                return SCORE_LIVE_FOUR
            elif blocks == 1:
                return SCORE_RUSH_FOUR
            else:
                return 0

        if count == 3:
            if blocks == 0:
                return SCORE_LIVE_THREE
            elif blocks == 1:
                return SCORE_SLEEP_THREE
            else:
                return 0

        if count == 2:
            if blocks == 0:
                return SCORE_LIVE_TWO
            elif blocks == 1:
                return SCORE_SLEEP_TWO
            else:
                return 0

        if count == 1:
            if blocks < 2:
                return SCORE_ONE

        return 0

    def _evaluate_board(self, matrix: List[List[int]], ai_side: int) -> float:
        """全局评估函数：正分对AI有利，负分对玩家有利

        按 4 方向逐线扫描，避免按点扫描时同一线段被多次触发。
        应用 value_biases（性格系统）调整棋型评分权重。
        """
        ai_score = 0
        player_score = 0
        w, h = self.board_width, self.board_height

        directions = [(1, 0), (0, 1), (1, 1), (1, -1)]

        for dx, dy in directions:
            # 确定每方向的起始点集合（沿反方向到边界的点）
            if dx == 1 and dy == 0:
                # 横向：起始点为 x=0 的所有点
                starts = [(0, y) for y in range(h)]
            elif dx == 0 and dy == 1:
                # 竖向：起始点为 y=0 的所有点
                starts = [(x, 0) for x in range(w)]
            elif dx == 1 and dy == 1:
                # 正斜（右下）：起始点为 x=0 或 y=0 的点
                starts = [(0, y) for y in range(h)] + [(x, 0) for x in range(1, w)]
            else:  # dx == 1 and dy == -1
                # 反斜（右上）：起始点为 x=0 或 y=h-1 的点
                starts = [(0, y) for y in range(h)] + [(x, h - 1) for x in range(1, w)]

            for sx, sy in starts:
                line = []
                nx, ny = sx, sy
                while 0 <= nx < w and 0 <= ny < h:
                    line.append(matrix[ny][nx])
                    nx += dx
                    ny += dy
                if len(line) < 5:
                    continue
                # 分别对 AI 方(1) 和 玩家方(-1) 评分
                ai_score += self._score_long_line(line, 1)
                player_score += self._score_long_line(line, -1)

        agg = self.personality_aggressiveness
        cons = self.personality_conservatism
        value_biases = self.personality_value_biases

        # 应用 value_biases（对齐象棋：按棋子类型调整评分权重）
        # 五子棋默认只有 stone，value_biases["stone"] 影响整体棋型评分
        # 自定义棋子类型的 value_biases 在 _custom_piece_values 层面体现
        ai_bias = 1.0 + value_biases.get("stone", 0.0)
        player_bias = 1.0 + value_biases.get("stone", 0.0)

        total = (ai_score * (0.5 + agg * 0.5) * ai_bias) - \
                (player_score * (0.5 + cons * 0.5) * player_bias)
        return total

    def _score_long_line(self, line: List[int], side: int) -> int:
        """对一整条线进行棋型评分"""
        total = 0
        n = len(line)
        i = 0
        while i < n:
            if line[i] != side:
                i += 1
                continue

            count = 0
            start = i
            while i < n and line[i] == side:
                count += 1
                i += 1

            left_open = start > 0 and line[start - 1] == 0
            right_open = i < n and line[i] == 0

            if count >= 5:
                total += SCORE_FIVE * (count - 4)
            elif count == 4:
                if left_open and right_open:
                    total += SCORE_LIVE_FOUR
                elif left_open or right_open:
                    total += SCORE_RUSH_FOUR
            elif count == 3:
                if left_open and right_open:
                    total += SCORE_LIVE_THREE
                elif left_open or right_open:
                    total += SCORE_SLEEP_THREE
            elif count == 2:
                if left_open and right_open:
                    total += SCORE_LIVE_TWO
                elif left_open or right_open:
                    total += SCORE_SLEEP_TWO
            elif count == 1:
                if left_open or right_open:
                    total += SCORE_ONE

        return total

    def _check_five_at(self, matrix: List[List[int]], x: int, y: int, side: int) -> bool:
        """检查在(x,y)落side色子后是否五连"""
        for dx, dy in [(1, 0), (0, 1), (1, 1), (1, -1)]:
            count = 1
            nx, ny = x + dx, y + dy
            while 0 <= nx < self.board_width and 0 <= ny < self.board_height and matrix[ny][nx] == side:
                count += 1
                nx += dx
                ny += dy
            nx, ny = x - dx, y - dy
            while 0 <= nx < self.board_width and 0 <= ny < self.board_height and matrix[ny][nx] == side:
                count += 1
                nx -= dx
                ny -= dy
            if count >= 5:
                return True
        return False

    # ═══════════════════════════════════════════════════════════════
    # Zobrist 置换表（性能优化）
    # ═══════════════════════════════════════════════════════════════

    def _compute_zobrist(self, matrix: List[List[int]]) -> int:
        """计算当前棋盘的 Zobrist 哈希"""
        h = 0
        for y in range(self.board_height):
            for x in range(self.board_width):
                v = matrix[y][x]
                if v != 0:
                    h ^= self._zobrist_table[(x, y, v)]
        return h

    def _tt_lookup(self, zobrist_hash: int, depth: int, alpha: float, beta: float) -> Optional[float]:
        """查置换表，命中且可用则返回值，否则返回 None"""
        entry = self._transposition_table.get(zobrist_hash)
        if not entry:
            return None
        entry_depth, entry_value, entry_flag = entry
        if entry_depth < depth:
            return None
        # 根据 flag 判断是否可用
        if entry_flag == TT_EXACT:
            return entry_value
        if entry_flag == TT_LOWER and entry_value >= beta:
            return entry_value
        if entry_flag == TT_UPPER and entry_value <= alpha:
            return entry_value
        return None

    def _tt_store(self, zobrist_hash: int, depth: int, value: float, flag: int):
        """存入置换表（带大小限制）"""
        if len(self._transposition_table) >= self._tt_max_size:
            # 清空最旧的一半（简化策略：直接清空一半）
            keys = list(self._transposition_table.keys())
            for k in keys[:len(keys) // 2]:
                del self._transposition_table[k]
        self._transposition_table[zobrist_hash] = (depth, value, flag)

    def _minimax(self, matrix: List[List[int]], depth: int, alpha: float, beta: float,
                 maximizing: bool, ai_side: int, zobrist_hash: int) -> float:
        """Minimax + Alpha-Beta剪枝 + Zobrist置换表 + 合并重复计算"""
        # 查置换表
        cached = self._tt_lookup(zobrist_hash, depth, alpha, beta)
        if cached is not None:
            return cached

        if depth == 0:
            value = self._evaluate_board(matrix, ai_side)
            self._tt_store(zobrist_hash, depth, value, TT_EXACT)
            return value

        current_side = ai_side if maximizing else -ai_side
        candidates = self._get_candidate_moves(matrix)

        if not candidates:
            value = self._evaluate_board(matrix, ai_side)
            self._tt_store(zobrist_hash, depth, value, TT_EXACT)
            return value

        # 候选评估：合并五连检查和启发式评分（避免递归前重复调用）
        scored_candidates = []
        for x, y in candidates:
            matrix[y][x] = current_side
            is_five = self._check_five_at(matrix, x, y, current_side)
            if is_five:
                matrix[y][x] = 0
                # 五连，直接返回（不缓存，因为是终局）
                if maximizing:
                    return SCORE_FIVE + depth
                else:
                    return -SCORE_FIVE - depth
            score = self._evaluate_point_heuristic(matrix, x, y)
            matrix[y][x] = 0
            scored_candidates.append((x, y, score))

        scored_candidates.sort(key=lambda t: t[2], reverse=not maximizing)

        if maximizing:
            max_eval = -math.inf
            for x, y, _ in scored_candidates:
                matrix[y][x] = current_side
                # 增量更新 Zobrist 哈希
                new_hash = zobrist_hash ^ self._zobrist_table[(x, y, current_side)]
                eval_score = self._minimax(matrix, depth - 1, alpha, beta, False, ai_side, new_hash)
                matrix[y][x] = 0
                max_eval = max(max_eval, eval_score)
                alpha = max(alpha, eval_score)
                if beta <= alpha:
                    # 触发剪枝，存为下界
                    self._tt_store(zobrist_hash, depth, max_eval, TT_LOWER)
                    return max_eval
            self._tt_store(zobrist_hash, depth, max_eval, TT_EXACT)
            return max_eval
        else:
            min_eval = math.inf
            for x, y, _ in scored_candidates:
                matrix[y][x] = current_side
                new_hash = zobrist_hash ^ self._zobrist_table[(x, y, current_side)]
                eval_score = self._minimax(matrix, depth - 1, alpha, beta, True, ai_side, new_hash)
                matrix[y][x] = 0
                min_eval = min(min_eval, eval_score)
                beta = min(beta, eval_score)
                if beta <= alpha:
                    # 触发剪枝，存为上界
                    self._tt_store(zobrist_hash, depth, min_eval, TT_UPPER)
                    return min_eval
            self._tt_store(zobrist_hash, depth, min_eval, TT_EXACT)
            return min_eval

    def find_best_move(self, board_state: dict, ai_side: str = "white") -> Optional[Tuple[int, int]]:
        """寻找最佳落子位置"""
        matrix = self._board_to_matrix(board_state)
        ai_val = 1 if ai_side == "white" else -1
        initial_hash = self._compute_zobrist(matrix)

        candidates = self._get_candidate_moves(matrix)
        if not candidates:
            cx, cy = self.board_width // 2, self.board_height // 2
            return (cx, cy)

        if len(candidates) == 1:
            return candidates[0]

        best_score = -math.inf
        best_moves = []

        depth = self.depth

        # 候选评估（合并五连检查）
        scored = []
        for x, y in candidates:
            matrix[y][x] = ai_val
            if self._check_five_at(matrix, x, y, ai_val):
                matrix[y][x] = 0
                return (x, y)
            s = self._evaluate_point_heuristic(matrix, x, y)
            matrix[y][x] = 0
            scored.append((x, y, s))
        scored.sort(key=lambda t: t[2], reverse=True)
        top_candidates = [(x, y) for x, y, _ in scored[:min(len(scored), AI_MAX_CANDIDATES)]]

        for x, y in top_candidates:
            matrix[y][x] = ai_val
            if self._check_five_at(matrix, x, y, ai_val):
                matrix[y][x] = 0
                return (x, y)
            new_hash = initial_hash ^ self._zobrist_table[(x, y, ai_val)]
            score = self._minimax(matrix, depth - 1, -math.inf, math.inf, False, ai_val, new_hash)
            matrix[y][x] = 0

            if score > best_score:
                best_score = score
                best_moves = [(x, y)]
            elif score == best_score:
                best_moves.append((x, y))

        if not best_moves:
            return top_candidates[0]

        if self.randomness > 0 and len(best_moves) > 1:
            if random.random() < self.randomness:
                return random.choice(best_moves)

        return best_moves[0]

    # ═══════════════════════════════════════════════════════════════
    # 大模型驱动：自定义棋子价值评估（对齐象棋 chess_ai.py）
    # 三层价值来源：标准表(PIECE_VALUES) → LLM缓存(_custom_piece_values) → 启发式兜底
    # ═══════════════════════════════════════════════════════════════

    async def _evaluate_custom_piece_value(self, piece_rule: dict) -> int:
        """通过 DeepSeek 大模型动态评估自定义棋子的价值（对齐象棋 chess_ai.py:326-424）

        Args:
            piece_rule: 棋子规则定义（包含 movement、attack、custom_modifiers）

        Returns:
            整数价值（50-1000 范围，相对于标准 stone=100）
        """
        if not self.api_key:
            return self._heuristic_custom_piece_value(piece_rule)

        piece_type = piece_rule.get("type", "unknown")
        # 检查缓存
        if piece_type in self._custom_piece_values:
            return self._custom_piece_values[piece_type]

        movement = piece_rule.get("movement", {})
        attack = piece_rule.get("attack", {})
        custom_modifiers = piece_rule.get("custom_modifiers", [])

        system_prompt = """你是五子棋棋子价值评估专家。根据棋子的移动能力和攻击能力评估其相对价值。

五子棋标准棋子（stone）价值基准：100
- 标准落子：只能在空位落子，无移动和吃子能力

评估依据：
1. 移动范围（能移动的格子数越多价值越高）
2. 移动灵活性（自由移动 > 直线/斜线 > 日字 > 条件移动）
3. 攻击能力（能吃子的棋子价值更高）
4. custom_modifiers 中的 extra_movement 额外增加价值
5. 是否能影响连珠判断（能改变落子规则的棋子价值更高）

输出要求：
- 只输出一个整数（50-1000 范围），表示该棋子相对于标准 stone 的价值
- 标准 stone 价值为 100，更强大的棋子应大于 100
- 不要输出任何其他内容"""

        user_prompt = f"""请评估以下自定义棋子的价值：

棋子类型: {piece_type}
棋子名称: {piece_rule.get('name', '未知')}

移动能力:
{json.dumps(movement, ensure_ascii=False, indent=2)}

攻击能力:
{json.dumps(attack, ensure_ascii=False, indent=2)}

自定义修饰器:
{json.dumps(custom_modifiers, ensure_ascii=False, indent=2)}

请评估这个棋子的价值（50-1000 的整数），只输出数字。"""

        try:
            import httpx
            headers = {
                "Authorization": f"Bearer {self.api_key}",
                "Content-Type": "application/json",
            }
            payload = {
                # 模型名统一取自 ai_config（配置真源）；禁止在此写死模型字符串
                "model": get_model(),
                "messages": [
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt},
                ],
                "temperature": 0,
                # 关闭推理：棋子估值是简单数值映射，无需思考链（详见 shared/ai_config.py）
                **get_no_think_params(),
                "max_tokens": 512,
            }
            async with httpx.AsyncClient(timeout=30.0) as client:
                resp = await client.post(
                    f"{self.base_url}/chat/completions", headers=headers, json=payload
                )
                resp.raise_for_status()
                data = resp.json()
                # 通过回调记录 token 消耗到共享的 token_stats
                if self._token_stats_callback and 'usage' in data:
                    try:
                        self._token_stats_callback(data['usage'])
                    except Exception:
                        pass  # 统计失败不影响主流程
                content = data["choices"][0]["message"]["content"].strip()
                # 提取数字
                match = re.search(r'\d+', content)
                if match:
                    value = int(match.group())
                    value = max(50, min(1000, value))  # 限制范围
                    self._custom_piece_values[piece_type] = value
                    return value
        except Exception:
            pass  # 失败时回退到启发式

        # 回退到启发式
        return self._heuristic_custom_piece_value(piece_rule)

    def _heuristic_custom_piece_value(self, piece_rule: dict) -> int:
        """启发式评估自定义棋子价值（当 LLM 不可用时的回退方案，对齐象棋 chess_ai.py:426-468）"""
        piece_type = piece_rule.get("type", "unknown")
        if piece_type in self._custom_piece_values:
            return self._custom_piece_values[piece_type]

        movement = piece_rule.get("movement", {})
        custom_modifiers = piece_rule.get("custom_modifiers", [])

        # 基础价值按 movement 类型
        move_type = movement.get("type", "")
        base_value = {
            "free": 800,           # 自由移动，高价值
            "orthogonal": 500,     # 直线移动
            "diagonal": 300,       # 斜线移动
            "L_shape": 400,        # 日字移动
            "conditional": 150,    # 条件移动
        }.get(move_type, 200)

        # max_distance 加成
        max_dist = movement.get("max_distance", 1)
        if max_dist > 1:
            base_value += min(max_dist * 30, 200)

        # custom_modifiers 中的 extra_movement 加成
        for mod in custom_modifiers:
            if mod.get("type") == "extra_movement":
                extra_movement = mod.get("movement", {})
                extra_type = extra_movement.get("type", "")
                base_value += {
                    "free": 200,
                    "orthogonal": 100,
                    "diagonal": 80,
                    "L_shape": 100,
                }.get(extra_type, 50)

        value = max(50, min(1000, base_value))
        self._custom_piece_values[piece_type] = value
        return value

    async def precompute_custom_piece_values(self):
        """预计算所有自定义棋子的价值（在 get_best_move 前调用，对齐象棋 chess_ai.py:470-481）

        遍历 custom_pieces，对每个棋子类型调用 LLM 评估价值并缓存。
        这样评估函数可以直接查缓存，避免同步方法中调用异步 API。
        """
        if not self.custom_pieces:
            return  # 无自定义棋子，跳过避免无谓的 async 调用开销
        for cp in self.custom_pieces:
            cp_type = cp.get("type")
            if cp_type and cp_type not in self._custom_piece_values:
                await self._evaluate_custom_piece_value(cp)

    def set_api_key(self, api_key: str):
        """更新 API 密钥（无需重建引擎）"""
        self.api_key = api_key

    def get_best_move(self, board_state: dict, ai_side: str = "white") -> Optional[Dict[str, Any]]:
        """兼容接口：获取最佳走法（返回与象棋AI相同格式）"""
        result = self.get_ai_move(board_state, ai_side)
        if result.get("success"):
            return result
        return None

    def get_ai_move(self, board_state: dict, ai_side: str = "white") -> Dict[str, Any]:
        """生成AI走棋响应"""
        move = self.find_best_move(board_state, ai_side)
        if not move:
            return {"success": False, "message": "没有可用的落子位置"}

        x, y = move
        stone_type = "stone"
        piece_label = "○" if ai_side == "white" else "●"
        piece_id = f"{ai_side}_stone_{x}_{y}_{len(board_state.get('pieces', []))}"

        return {
            "success": True,
            "piece_id": piece_id,
            "piece_type": stone_type,
            "from": None,
            "to": [x, y],
            "piece_label": piece_label,
            "side": ai_side,
        }
