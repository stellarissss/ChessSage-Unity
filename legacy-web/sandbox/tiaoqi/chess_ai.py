"""
AI下棋引擎 - 使用Minimax + Alpha-Beta剪枝
"""
import json
import re
import random
import copy
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

# 棋子价值表（跳棋只有一种棋子）
PIECE_VALUES = {
    "piece": 100,
}


class ChessAI:
    """AI下棋引擎"""

    def __init__(self, board: dict, pieces_red: dict, pieces_black: dict, rules: dict, difficulty: str = "medium", api_key: str = "", base_url: str = "https://api.deepseek.com/v1", token_stats_callback=None):
        self.rule_engine = RuleEngine(board, pieces_red, pieces_black, rules)
        self.difficulty = difficulty
        self._pieces_by_side = {
            "red": pieces_red,
            "black": pieces_black,
        }
        self._custom_pieces_by_side = {
            "red": pieces_red.get("custom_pieces", []),
            "black": pieces_black.get("custom_pieces", []),
        }
        self.custom_pieces = pieces_red.get("custom_pieces", []) + pieces_black.get("custom_pieces", [])
        self.api_key = api_key
        self.base_url = base_url
        self._custom_piece_values: Dict[str, int] = {}
        self._token_stats_callback = token_stats_callback

        levels = rules.get("ai_difficulty", {}).get("levels", {})
        level_cfg = levels.get(difficulty, levels.get("medium", {}))
        self.depth = level_cfg.get("depth", 3)
        self.randomness = level_cfg.get("randomness", 0.1)

        self.personality_aggressiveness = 0.5
        self.personality_conservatism = 0.5
        self.personality_value_biases: Dict[str, float] = {}

    def set_difficulty(self, difficulty: str):
        """设置难度"""
        self.difficulty = difficulty
        if difficulty == "easy":
            self.depth = 2
            self.randomness = 0.3
        elif difficulty == "medium":
            self.depth = 3
            self.randomness = 0.1
        else:
            self.depth = 4
            self.randomness = 0.0

    def set_api_key(self, api_key: str):
        """更新API密钥（无需重建引擎）"""
        self.api_key = api_key

    def get_best_move(self, board_state: dict) -> Optional[Dict[str, Any]]:
        """
        获取AI的最佳移动

        Returns:
            {"piece_id": str, "from": [x,y], "to": [x,y], "captured": str|None}
        """
        side = board_state.get("current_turn", "black")

        # 预计算自定义棋子价值（如果尚未计算）
        # 注意：这里是同步方法，预计算应在调用get_best_move前通过precompute_custom_piece_values完成
        # 此处仅作为兜底，用启发式评估未缓存的自定义棋子
        for cp in self.custom_pieces:
            cp_type = cp.get("type")
            if cp_type and cp_type not in self._custom_piece_values:
                self._heuristic_custom_piece_value(cp)

        # 简单难度可能随机走
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

        # 按吃子优先排序，提高剪枝效率
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
        """随机选择一个合法移动"""
        moves = self._generate_all_moves(board_state, side)
        if not moves:
            return None
        return random.choice(moves)

    def _generate_all_moves(
        self, board_state: dict, side: str
    ) -> List[Dict[str, Any]]:
        """生成某方所有合法移动"""
        moves = []
        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True):
                continue
            if p["side"] != side:
                continue
            valid = self.rule_engine.get_valid_moves(p, board_state)
            for pos in valid:
                target = self.rule_engine._get_piece_at(pos, board_state)
                moves.append(
                    {
                        "piece_id": p["id"],
                        "from": list(p["position"]),
                        "to": pos,
                        "captured": target["id"] if target else None,
                    }
                )
        return moves

    def _simulate_move(self, board_state: dict, move: dict) -> dict:
        """模拟执行移动，返回新的棋盘状态"""
        new_state = copy.deepcopy(board_state)

        # 找到并移动棋子
        for p in new_state["pieces"]:
            if p["id"] == move["piece_id"]:
                p["position"] = list(move["to"])
                break

        # 处理吃子
        if move.get("captured"):
            for p in new_state["pieces"]:
                if p["id"] == move["captured"]:
                    p["is_alive"] = False
                    break

        new_state["current_turn"] = "red" if new_state["current_turn"] == "black" else "black"
        return new_state

    def _move_score(self, move: dict, board_state: dict) -> int:
        """评估移动的即时价值（用于排序）- 跳棋基于推进度"""
        score = 0
        # 跳棋不吃子，按移动后推进度排序
        from_pos = move.get("from", [0, 0])
        to_pos = move.get("to", [0, 0])
        # 找到移动方
        piece = None
        for p in board_state.get("pieces", []):
            if p["id"] == move.get("piece_id"):
                piece = p
                break
        if piece:
            side = piece.get("side", "red")
            from_progress = self._calc_progress(from_pos, side)
            to_progress = self._calc_progress(to_pos, side)
            score = int((to_progress - from_progress) * 100)
        return score

    def _minimax(
        self,
        board_state: dict,
        depth: int,
        alpha: float,
        beta: float,
        is_max: bool,
        ai_side: str,
    ) -> float:
        """Minimax + Alpha-Beta剪枝"""
        # 终止条件（全部入营胜利）
        winner = self.rule_engine.is_all_in_camp(board_state)
        if winner:
            return 10000 if winner == ai_side else -10000

        if depth == 0:
            return self._evaluate(board_state, ai_side)

        current_side = board_state.get("current_turn", "red")
        moves = self._generate_all_moves(board_state, current_side)

        if not moves:
            # 困毙
            return -10000 if current_side == ai_side else 10000

        if is_max:
            max_eval = float("-inf")
            for move in moves:
                new_state = self._simulate_move(board_state, move)
                eval_score = self._minimax(
                    new_state, depth - 1, alpha, beta, False, ai_side
                )
                max_eval = max(max_eval, eval_score)
                alpha = max(alpha, eval_score)
                if beta <= alpha:
                    break
            return max_eval
        else:
            min_eval = float("inf")
            for move in moves:
                new_state = self._simulate_move(board_state, move)
                eval_score = self._minimax(
                    new_state, depth - 1, alpha, beta, True, ai_side
                )
                min_eval = min(min_eval, eval_score)
                beta = min(beta, eval_score)
                if beta <= alpha:
                    break
            return min_eval

    def _evaluate(self, board_state: dict, ai_side: str) -> float:
        """评估棋盘局面（跳棋推进度评估）"""
        score = 0.0
        agg = getattr(self, "personality_aggressiveness", 0.5)
        cons = getattr(self, "personality_conservatism", 0.5)

        opponent_side = "black" if ai_side == "red" else "red"

        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True):
                continue

            pos = p["position"]
            side = p["side"]

            if side == ai_side:
                # 推进度：距离对方营区越近越好
                progress = self._calc_progress(pos, side)
                score += progress * 100

                # 在对方营区内加分
                if self._is_in_camp(pos, opponent_side):
                    score += 50 * (0.5 + agg)

                # 仍在己方营区扣分（落后棋子惩罚）
                if self._is_in_camp(pos, side):
                    score -= 30 * (0.5 + cons)

                # 自定义棋子额外价值
                piece_type = p.get("type", "piece")
                if piece_type in self._custom_piece_values:
                    score += self._custom_piece_values[piece_type] * 0.1
            else:
                # 对方棋子的推进度（负分）
                progress = self._calc_progress(pos, side)
                score -= progress * 100

                # 对方在己方营区内的棋子（对己方不利）
                if self._is_in_camp(pos, ai_side):
                    score -= 30

        return score

    def _calc_progress(self, pos: List[int], side: str) -> float:
        """计算棋子推进度（0.0=己方营区, 1.0=对方营区）

        红方从row 0向row 16推进
        黑方从row 16向row 0推进
        """
        row = pos[0] if isinstance(pos, list) else pos[0]
        if side == "red":
            # 红方从上方(rows 0-3)向下方(rows 13-16)推进
            return min(max((row - 3) / 10.0, 0.0), 1.0)
        else:
            # 黑方从下方(rows 13-16)向上方(rows 0-3)推进
            return min(max((13 - row) / 10.0, 0.0), 1.0)

    def _is_in_camp(self, pos: List[int], side: str) -> bool:
        """判断位置是否在指定方营区内"""
        row = pos[0] if isinstance(pos, list) else pos[0]
        if side == "red":
            return 0 <= row <= 3
        else:
            return 13 <= row <= 16

    async def _evaluate_custom_piece_value(self, piece_rule: dict) -> int:
        """通过AI动态评估自定义跳棋棋子的价值

        Args:
            piece_rule: 棋子规则定义（包含moves数组）

        Returns:
            整数价值（50-1000范围）
        """
        if not self.api_key:
            return self._heuristic_custom_piece_value(piece_rule)

        piece_type = piece_rule.get("type", "unknown")
        # 检查缓存
        if piece_type in self._custom_piece_values:
            return self._custom_piece_values[piece_type]

        moves = piece_rule.get("moves", [])

        system_prompt = """你是中国跳棋棋子价值评估专家。根据棋子的移动能力评估其价值。

参考价值表（标准棋子）：
- 棋子(piece): 100（标准跳棋子，可单步移动+连续跳跃）

跳棋移动原语：
- step: 向相邻空位移动一格（基础能力）
- hop: 隔子跳跃，chain=true可连续跳跃（核心能力）

评估依据：
1. 是否有step（单步移动能力）
2. 是否有hop（跳跃能力）
3. hop的chain是否为true（连续跳跃能力，价值最高）
4. land类型（empty=正常, any=可落到任何位置，更强）
5. sym类型（hex6=六向展开, none=不展开）

输出要求：
- 只输出一个整数（50-1000范围），表示该棋子的价值
- 不要输出任何其他内容"""

        user_prompt = f"""请评估以下自定义跳棋棋子的价值：

棋子类型: {piece_type}

移动规则:
{json.dumps(moves, ensure_ascii=False, indent=2)}

请评估这个棋子的价值（50-1000的整数），只输出数字。"""

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
                if self._token_stats_callback and 'usage' in data:
                    try:
                        self._token_stats_callback(data['usage'])
                    except Exception:
                        pass
                content = data["choices"][0]["message"]["content"].strip()
                match = re.search(r'\d+', content)
                if match:
                    value = int(match.group())
                    value = max(50, min(1000, value))
                    self._custom_piece_values[piece_type] = value
                    return value
        except Exception:
            pass

        return self._heuristic_custom_piece_value(piece_rule)

    def _heuristic_custom_piece_value(self, piece_rule: dict) -> int:
        """启发式评估自定义跳棋棋子价值（当AI不可用时的回退方案）"""
        piece_type = piece_rule.get("type", "unknown")
        if piece_type in self._custom_piece_values:
            return self._custom_piece_values[piece_type]

        moves = piece_rule.get("moves", [])
        base_value = 100  # 基础价值

        for move_def in moves:
            kind = move_def.get("kind", "")
            chain = move_def.get("chain", False)
            land = move_def.get("land", "empty")

            if kind == "step":
                base_value += 50  # 单步移动
            elif kind == "hop":
                base_value += 100  # 跳跃能力
                if chain:
                    base_value += 100  # 连续跳跃加成
            if land == "any":
                base_value += 50  # 可落到任何位置

        value = max(50, min(1000, base_value))
        self._custom_piece_values[piece_type] = value
        return value

    async def precompute_custom_piece_values(self):
        """预计算所有自定义棋子的价值（在get_best_move前调用）

        遍历custom_pieces，对每个棋子类型调用AI评估价值并缓存。
        这样_evaluate中可以直接查缓存，避免同步方法中调用异步API。
        """
        if not self.custom_pieces:
            return  # 无自定义棋子，跳过避免无谓的 async 调用开销
        for cp in self.custom_pieces:
            cp_type = cp.get("type")
            if cp_type and cp_type not in self._custom_piece_values:
                await self._evaluate_custom_piece_value(cp)
