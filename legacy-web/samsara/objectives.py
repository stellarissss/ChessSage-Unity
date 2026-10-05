import json
from pathlib import Path
from .state import SamsaraState

BASE_DIR = Path(__file__).resolve().parent.parent
CONFIGS_DIR = BASE_DIR / "configs"
OBJECTIVE_TYPES_FILE = CONFIGS_DIR / "objective_types.json"


class ObjectiveSystem:
    def __init__(self, state: SamsaraState):
        self.state = state
        self.objective_types = self._load_objective_types()

    def _load_objective_types(self):
        if OBJECTIVE_TYPES_FILE.exists():
            try:
                return json.loads(OBJECTIVE_TYPES_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return self._get_default_objective_types()

    def _get_default_objective_types(self):
        return {
            "checkmate": {
                "name": "将死对方",
                "description": "使对方无路可走",
                "turn_limit": 20,
            },
            "capture_count": {
                "name": "吃子数目标",
                "description": "吃掉对方指定数量的棋子",
                "turn_limit": 20,
            },
            "turn_limit": {
                "name": "竞速",
                "description": "在限定回合内获胜",
                "turn_limit": 20,
            },
            "evacuation": {
                "name": "撤离",
                "description": "将指定棋子撤离到目标区域",
                "turn_limit": 20,
            },
            "board_coverage": {
                "name": "棋盘覆盖率",
                "description": "己方棋子覆盖指定百分比的棋盘",
                "turn_limit": 20,
            },
            "formation": {
                "name": "特定阵型",
                "description": "组成指定阵型",
                "turn_limit": 20,
            },
            "color_coverage": {
                "name": "颜色覆盖率",
                "description": "点亮指定百分比的暗色区域",
                "turn_limit": 20,
            },
            "survival": {
                "name": "生存",
                "description": "坚持指定回合数并保留一定棋子",
                "turn_limit": 20,
            },
            "assassination": {
                "name": "刺杀",
                "description": "在限定回合内吃掉对方指定棋子",
                "turn_limit": 20,
            },
            "escort": {
                "name": "护送",
                "description": "护送指定棋子沿路径前进",
                "turn_limit": 20,
            },
            "compound": {
                "name": "复合条件",
                "description": "满足多个条件",
                "turn_limit": 20,
            },
        }

    def check(self, objective: dict, game_state: dict) -> dict:
        obj_type = objective.get("type", "checkmate")
        if obj_type == "checkmate":
            return self._check_checkmate(objective, game_state)
        elif obj_type == "capture_count":
            return self._check_capture_count(objective, game_state)
        elif obj_type == "turn_limit":
            return self._check_turn_limit(objective, game_state)
        elif obj_type == "evacuation":
            return self._check_evacuation(objective, game_state)
        elif obj_type == "board_coverage":
            return self._check_board_coverage(objective, game_state)
        elif obj_type == "color_coverage":
            return self._check_color_coverage(objective, game_state)
        elif obj_type == "survival":
            return self._check_survival(objective, game_state)
        elif obj_type == "assassination":
            return self._check_assassination(objective, game_state)
        elif obj_type == "compound":
            return self._check_compound(objective, game_state)
        else:
            return {"completed": False, "progress": 0, "message": "未知目标类型"}

    def _check_checkmate(self, objective, game_state):
        game_status = game_state.get("game_status", {})
        if game_status.get("state") == "ended" and game_status.get("winner") == "red":
            return {"completed": True, "progress": 100, "message": "已将死对方"}
        return {"completed": False, "progress": 0, "message": "继续将死对方"}

    def _check_capture_count(self, objective, game_state):
        target = objective.get("target", 1)
        captured = len([p for p in game_state.get("pieces", []) if not p.get("is_alive", True) and p.get("side") == "black"])
        progress = min(100, int(captured / target * 100))
        completed = captured >= target
        return {
            "completed": completed,
            "progress": progress,
            "message": f"已吃掉{captured}/{target}个棋子",
        }

    def _check_turn_limit(self, objective, game_state):
        max_turns = objective.get("max_turns", 20)
        current_turn = self.state.get("current_turn", 0)
        game_status = game_state.get("game_status", {})
        if game_status.get("state") == "ended" and game_status.get("winner") == "red" and current_turn <= max_turns:
            return {"completed": True, "progress": 100, "message": "在限定回合内获胜"}
        if current_turn >= max_turns:
            return {"completed": False, "progress": 0, "message": "回合已用完"}
        progress = min(100, int((max_turns - current_turn) / max_turns * 100))
        return {"completed": False, "progress": progress, "message": f"还剩{max_turns - current_turn}回合"}

    def _check_evacuation(self, objective, game_state):
        target_region = objective.get("target_region", {})
        pieces = objective.get("pieces", [])
        tl = target_region.get("top_left", [0, 0])
        br = target_region.get("bottom_right", [10, 10])
        evacuated = 0
        for p in game_state.get("pieces", []):
            if p.get("type") in pieces and p.get("is_alive", True):
                pos = p.get("position", [0, 0])
                if tl[0] <= pos[0] <= br[0] and tl[1] <= pos[1] <= br[1]:
                    evacuated += 1
        progress = min(100, int(evacuated / len(pieces) * 100)) if pieces else 0
        return {
            "completed": evacuated >= len(pieces),
            "progress": progress,
            "message": f"{evacuated}/{len(pieces)}个棋子已撤离",
        }

    def _check_board_coverage(self, objective, game_state):
        target_pct = objective.get("target_percentage", 60)
        board_width = 9
        board_height = 10
        total_cells = board_width * board_height
        occupied = len([p for p in game_state.get("pieces", []) if p.get("is_alive", True) and p.get("side") == "red"])
        coverage = min(100, int(occupied / total_cells * 100))
        return {
            "completed": coverage >= target_pct,
            "progress": coverage,
            "message": f"覆盖率{coverage}%",
        }

    def _check_color_coverage(self, objective, game_state):
        target_pct = objective.get("target_percentage", 75)
        board_width = 9
        board_height = 10
        dark_cells = 0
        lit_cells = 0
        for y in range(board_height):
            for x in range(board_width):
                if (x + y) % 2 == 1:
                    dark_cells += 1
                    for p in game_state.get("pieces", []):
                        if p.get("is_alive", True) and p.get("position") == [x, y]:
                            lit_cells += 1
                            break
        coverage = min(100, int(lit_cells / dark_cells * 100)) if dark_cells > 0 else 0
        return {
            "completed": coverage >= target_pct,
            "progress": coverage,
            "message": f"暗色区域覆盖率{coverage}%",
        }

    def _check_survival(self, objective, game_state):
        min_pieces = objective.get("min_pieces", 3)
        max_turns = objective.get("max_turns", 20)
        current_turn = self.state.get("current_turn", 0)
        alive_count = len([p for p in game_state.get("pieces", []) if p.get("is_alive", True) and p.get("side") == "red"])
        if current_turn >= max_turns and alive_count >= min_pieces:
            return {"completed": True, "progress": 100, "message": "生存成功"}
        if alive_count < min_pieces:
            return {"completed": False, "progress": 0, "message": "棋子不足"}
        progress = min(100, int(current_turn / max_turns * 100))
        return {"completed": False, "progress": progress, "message": f"还剩{max_turns - current_turn}回合"}

    def _check_assassination(self, objective, game_state):
        target_piece = objective.get("target_piece", "")
        for p in game_state.get("pieces", []):
            if p.get("type") == target_piece and p.get("side") == "black" and not p.get("is_alive", True):
                return {"completed": True, "progress": 100, "message": "刺杀成功"}
        return {"completed": False, "progress": 0, "message": "目标仍存活"}

    def _check_compound(self, objective, game_state):
        operator = objective.get("operator", "AND")
        conditions = objective.get("conditions", [])
        results = []
        for cond in conditions:
            results.append(self.check(cond, game_state))
        if operator == "AND":
            completed = all(r["completed"] for r in results)
        elif operator == "OR":
            completed = any(r["completed"] for r in results)
        else:
            completed = False
        avg_progress = sum(r["progress"] for r in results) / len(results) if results else 0
        messages = [r["message"] for r in results]
        return {
            "completed": completed,
            "progress": avg_progress,
            "message": "; ".join(messages),
            "sub_results": results,
        }