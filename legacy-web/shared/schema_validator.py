"""
JSON Schema 校验器 - 封装jsonschema校验功能
"""
import json
import os
from typing import Tuple, Dict, Any

try:
    import jsonschema
    from jsonschema import Draft7Validator
except ImportError:
    jsonschema = None
    Draft7Validator = None


SCHEMA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "schemas")

CONFIG_SCHEMA_MAP = {
    "board_state": "board_state.schema.json",
    "board": "board.schema.json",
    "pieces": "pieces.schema.json",
    "pieces_red": "pieces.schema.json",
    "pieces_black": "pieces.schema.json",
    "pieces_white": "pieces.schema.json",
    "rules": "rules.schema.json",
    "ui_config": "ui_config.schema.json",
}

_schemas_cache: Dict[str, dict] = {}


def _load_schema(schema_name: str) -> dict:
    """加载并缓存Schema文件"""
    if schema_name in _schemas_cache:
        return _schemas_cache[schema_name]

    schema_path = os.path.join(SCHEMA_DIR, schema_name)
    with open(schema_path, "r", encoding="utf-8") as f:
        schema = json.load(f)

    _schemas_cache[schema_name] = schema
    return schema


def validate_config(config_name: str, config_data: dict) -> Tuple[bool, str]:
    """
    校验配置文件

    Args:
        config_name: 配置名称 (board_state, piece_rules, game_rules, ui_config)
        config_data: 配置数据字典

    Returns:
        (是否通过, 错误信息)
    """
    if jsonschema is None:
        return True, "jsonschema未安装，跳过校验"

    if config_name not in CONFIG_SCHEMA_MAP:
        return False, f"未知的配置名称: {config_name}"

    schema_file = CONFIG_SCHEMA_MAP[config_name]
    try:
        schema = _load_schema(schema_file)
    except Exception as e:
        return False, f"加载Schema文件失败: {e}"

    try:
        Draft7Validator.check_schema(schema)
    except Exception as e:
        return False, f"Schema本身无效: {e}"

    validator = Draft7Validator(schema)
    errors = list(validator.iter_errors(config_data))

    if not errors:
        return True, ""

    error_messages = []
    for err in errors:
        path = ".".join(str(p) for p in err.absolute_path) if err.absolute_path else "root"
        error_messages.append(f"[{path}] {err.message}")

    return False, "; ".join(error_messages)


def validate_board_state(board_state: dict) -> Tuple[bool, str]:
    """校验棋盘状态"""
    valid, err = validate_config("board_state", board_state)
    if not valid:
        return False, err

    errors = []
    board = board_state.get("board", {})
    width = board.get("width", 9)
    height = board.get("height", 10)
    pieces = board_state.get("pieces", [])

    for i, piece in enumerate(pieces):
        position = piece.get("position", [])
        if len(position) == 2:
            x, y = position
            if x < 0 or x >= width:
                errors.append(f"[pieces.{i}.position.0] {x} 超出棋盘范围 (0-{width-1})")
            if y < 0 or y >= height:
                errors.append(f"[pieces.{i}.position.1] {y} 超出棋盘范围 (0-{height-1})")

    if errors:
        return False, "; ".join(errors)

    return True, ""


def validate_piece_rules(piece_rules: dict) -> Tuple[bool, str]:
    """校验棋子规则（兼容旧接口）"""
    return validate_config("pieces", piece_rules)


def validate_pieces(pieces: dict) -> Tuple[bool, str]:
    """校验棋子配置"""
    return validate_config("pieces", pieces)


def validate_rules(rules: dict) -> Tuple[bool, str]:
    """校验游戏规则配置"""
    return validate_config("rules", rules)


def validate_board(board: dict) -> Tuple[bool, str]:
    """校验棋盘配置"""
    return validate_config("board", board)


def validate_ui_config(ui_config: dict) -> Tuple[bool, str]:
    """校验界面配置"""
    return validate_config("ui_config", ui_config)
