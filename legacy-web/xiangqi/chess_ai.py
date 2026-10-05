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

# 棋子价值表
PIECE_VALUES = {
    "general": 10000,
    "chariot": 900,
    "cannon": 450,
    "horse": 400,
    "elephant": 200,
    "advisor": 200,
    "soldier": 100,
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
                    # 防御：无敌棋子永远不会被吃子模拟移除
                    if not self.rule_engine.is_invulnerable(p):
                        p["is_alive"] = False
                    break

        new_state["current_turn"] = "red" if new_state["current_turn"] == "black" else "black"
        return new_state

    def _move_score(self, move: dict, board_state: dict) -> int:
        """评估移动的即时价值（用于排序）"""
        score = 0
        if move.get("captured"):
            for p in board_state.get("pieces", []):
                if p["id"] == move["captured"]:
                    piece_type = p["type"]
                    if piece_type in PIECE_VALUES:
                        score += PIECE_VALUES[piece_type]
                    elif piece_type in self._custom_piece_values:
                        score += self._custom_piece_values[piece_type]
                    else:
                        # 兜底用启发式
                        cp_rule = None
                        for cp in self.custom_pieces:
                            if cp.get("type") == piece_type:
                                cp_rule = cp
                                break
                        if cp_rule:
                            score += self._heuristic_custom_piece_value(cp_rule)
                    break
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
        # 终止条件
        winner = self.rule_engine.is_general_captured(board_state)
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
        """评估棋盘局面（考虑性格影响）"""
        score = 0.0
        agg = getattr(self, "personality_aggressiveness", 0.5)
        cons = getattr(self, "personality_conservatism", 0.5)
        value_biases = getattr(self, "personality_value_biases", {})

        for p in board_state.get("pieces", []):
            if not p.get("is_alive", True):
                continue
            # 预定义棋子用固定价值表，自定义棋子用缓存的AI评估值
            piece_type = p["type"]
            if piece_type in PIECE_VALUES:
                val = PIECE_VALUES[piece_type]
            elif piece_type in self._custom_piece_values:
                val = self._custom_piece_values[piece_type]
            else:
                # 兜底：查找custom_pieces并用启发式评估
                cp_rule = None
                for cp in self.custom_pieces:
                    if cp.get("type") == piece_type:
                        cp_rule = cp
                        break
                if cp_rule:
                    val = self._heuristic_custom_piece_value(cp_rule)
                else:
                    val = 0

            # 应用棋子价值偏差（性格系统）
            if piece_type in value_biases:
                val = val * (1.0 + value_biases[piece_type])

            if p["side"] == ai_side:
                score += val
            else:
                score -= val

            # 位置奖励
            px, py = p["position"]
            # 兵过河奖励（激进性格对过河兵奖励更高）
            if p["type"] == "soldier":
                base_bonus = 30
                agg_bonus = base_bonus * (0.5 + agg)
                if p["side"] == "red" and py <= 4:
                    score += agg_bonus if p["side"] == ai_side else -agg_bonus
                elif p["side"] == "black" and py >= 5:
                    score += agg_bonus if p["side"] == ai_side else -agg_bonus

            # 车马炮在中线附近有奖励（激进性格更重视中心控制）
            if p["type"] in ("chariot", "horse", "cannon"):
                base_center = (3.5 - abs(px - 4)) * 2
                center_bonus = base_center * (0.5 + agg)
                if p["side"] == ai_side:
                    score += center_bonus
                else:
                    score -= center_bonus

            # 自定义棋子的中心控制奖励（保守权重，避免过度影响评估）
            if piece_type not in PIECE_VALUES and piece_type in self._custom_piece_values:
                base_center = (3.5 - abs(px - 4)) * 1.0
                center_bonus = base_center * (0.5 + agg)
                if p["side"] == ai_side:
                    score += center_bonus
                else:
                    score -= center_bonus

            # 保守性格：己方将帅附近的棋子有额外防守加分
            if cons > 0.5:
                general_pos = self._find_general_position(board_state, p["side"])
                if general_pos:
                    gx, gy = general_pos
                    dist = abs(px - gx) + abs(py - gy)
                    if dist <= 2:
                        defense_bonus = (3 - dist) * 15 * (cons - 0.5) * 2
                        if p["side"] == ai_side:
                            score += defense_bonus
                        else:
                            score -= defense_bonus

        return score

    def _find_general_position(self, board_state: dict, side: str) -> Optional[List[int]]:
        """查找某方将帅的位置"""
        for p in board_state.get("pieces", []):
            if p.get("side") == side and p.get("type") == "general" and p.get("is_alive", True):
                return list(p["position"])
        return None

    async def _evaluate_custom_piece_value(self, piece_rule: dict) -> int:
        """通过AI动态评估自定义棋子的价值

        Args:
            piece_rule: 棋子规则定义（包含movement、attack、custom_modifiers）

        Returns:
            整数价值（50-1000范围）
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

        system_prompt = """你是中国象棋棋子价值评估专家。根据棋子的移动能力和攻击能力评估其价值。

参考价值表（标准棋子）：
- 將/帥(general): 10000（核心棋子，被吃即输）
- 車(chariot): 900（直线远距离移动，威力最大）
- 砲/炮(cannon): 450（隔子打，中等威力）
- 馬(horse): 400（日字移动，中等威力）
- 象/相(elephant): 200（田字移动，防守型）
- 士/仕(advisor): 200（九宫斜线，防守型）
- 兵/卒(soldier): 100（前进一格，低价值）

评估依据：
1. 移动范围（max_distance越大价值越高）
2. 移动灵活性（free > orthogonal/diagonal > L_shape > conditional）
3. 攻击能力（same_as_movement vs 特殊攻击如cannon_shot）
4. custom_modifiers中的extra_movement额外增加价值
5. 是否能过河（can_cross_river）

输出要求：
- 只输出一个整数（50-1000范围），表示该棋子的价值
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
                # 记录 token 消耗到共享的 token_stats（通过回调注入）
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
        """启发式评估自定义棋子价值（当AI不可用时的回退方案）"""
        piece_type = piece_rule.get("type", "unknown")
        if piece_type in self._custom_piece_values:
            return self._custom_piece_values[piece_type]

        movement = piece_rule.get("movement", {})
        custom_modifiers = piece_rule.get("custom_modifiers", [])

        # 基础价值按movement类型
        move_type = movement.get("type", "")
        base_value = {
            "free": 800,           # 自由移动，高价值
            "orthogonal": 500,     # 直线移动
            "diagonal": 300,       # 斜线移动
            "L_shape": 400,        # 日字移动
            "conditional": 150,    # 条件移动
        }.get(move_type, 200)

        # max_distance加成
        max_dist = movement.get("max_distance", 1)
        if max_dist > 1:
            base_value += min(max_dist * 30, 200)

        # custom_modifiers中的extra_movement加成
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

        # 能过河加成
        if movement.get("can_cross_river", False):
            base_value += 50

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
