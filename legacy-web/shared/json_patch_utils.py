"""
JSON Patch 工具模块 - 实现RFC 6902标准的JSON Patch操作

支持 add/remove/replace/copy/move 五种操作
优先使用 jsonpatch 库，未安装时使用纯Python简化实现
"""
import copy
from typing import Tuple, List, Any, Dict

try:
    import jsonpatch
    HAS_JSONPATCH = True
except ImportError:
    HAS_JSONPATCH = False


def _parse_pointer(path: str) -> List[str]:
    """解析JSON Pointer路径为列表形式"""
    if not path or path == "/":
        return []
    if not path.startswith("/"):
        raise ValueError(f"Invalid JSON Pointer: {path}")
    
    parts = path[1:].split("/")
    result = []
    for part in parts:
        part = part.replace("~1", "/").replace("~0", "~")
        result.append(part)
    return result


def _get_value(obj: Any, parts: List[str]) -> Any:
    """根据路径parts获取对象中的值"""
    current = obj
    for part in parts:
        if isinstance(current, dict):
            if part not in current:
                raise KeyError(f"Path not found: {part}")
            current = current[part]
        elif isinstance(current, list):
            try:
                idx = int(part)
            except ValueError:
                raise KeyError(f"Invalid array index: {part}")
            if idx < 0 or idx >= len(current):
                raise IndexError(f"Array index out of bounds: {idx}")
            current = current[idx]
        else:
            raise KeyError(f"Cannot traverse into non-container type at: {part}")
    return current


def _set_value(obj: Any, parts: List[str], value: Any) -> None:
    """根据路径parts设置对象中的值"""
    if not parts:
        raise ValueError("Cannot set root value directly")
    
    current = obj
    for i, part in enumerate(parts[:-1]):
        if isinstance(current, dict):
            if part not in current:
                raise KeyError(f"Path not found: {part}")
            current = current[part]
        elif isinstance(current, list):
            try:
                idx = int(part)
            except ValueError:
                raise KeyError(f"Invalid array index: {part}")
            if idx < 0 or idx >= len(current):
                raise IndexError(f"Array index out of bounds: {idx}")
            current = current[idx]
        else:
            raise KeyError(f"Cannot traverse into non-container type at: {part}")
    
    last = parts[-1]
    if isinstance(current, dict):
        current[last] = value
    elif isinstance(current, list):
        if last == "-":
            current.append(value)
        else:
            try:
                idx = int(last)
            except ValueError:
                raise KeyError(f"Invalid array index: {last}")
            if idx < 0 or idx > len(current):
                raise IndexError(f"Array index out of bounds: {idx}")
            current.insert(idx, value)
    else:
        raise KeyError(f"Cannot set value on non-container type")


def _remove_value(obj: Any, parts: List[str]) -> Any:
    """根据路径parts删除对象中的值并返回被删除的值"""
    if not parts:
        raise ValueError("Cannot remove root")
    
    current = obj
    for i, part in enumerate(parts[:-1]):
        if isinstance(current, dict):
            if part not in current:
                raise KeyError(f"Path not found: {part}")
            current = current[part]
        elif isinstance(current, list):
            try:
                idx = int(part)
            except ValueError:
                raise KeyError(f"Invalid array index: {part}")
            if idx < 0 or idx >= len(current):
                raise IndexError(f"Array index out of bounds: {idx}")
            current = current[idx]
        else:
            raise KeyError(f"Cannot traverse into non-container type at: {part}")
    
    last = parts[-1]
    if isinstance(current, dict):
        if last not in current:
            raise KeyError(f"Path not found: {last}")
        return current.pop(last)
    elif isinstance(current, list):
        try:
            idx = int(last)
        except ValueError:
            raise KeyError(f"Invalid array index: {last}")
        if idx < 0 or idx >= len(current):
            raise IndexError(f"Array index out of bounds: {idx}")
        return current.pop(idx)
    else:
        raise KeyError(f"Cannot remove from non-container type")


def _apply_single_op(original: dict, op: dict) -> dict:
    """应用单个patch操作（纯Python实现）"""
    result = copy.deepcopy(original)
    
    op_type = op.get("op")
    path = op.get("path", "")
    parts = _parse_pointer(path)
    
    if op_type == "add":
        value = op.get("value")
        if value is None and "value" not in op:
            raise ValueError("'add' operation requires 'value' field")
        if not parts:
            return copy.deepcopy(value)
        _set_value(result, parts, copy.deepcopy(value))
        
    elif op_type == "remove":
        if not parts:
            raise ValueError("Cannot remove root")
        _remove_value(result, parts)
        
    elif op_type == "replace":
        value = op.get("value")
        if value is None and "value" not in op:
            raise ValueError("'replace' operation requires 'value' field")
        if not parts:
            return copy.deepcopy(value)
        _remove_value(result, parts)
        _set_value(result, parts, copy.deepcopy(value))
        
    elif op_type == "copy":
        from_path = op.get("from", "")
        from_parts = _parse_pointer(from_path)
        value = _get_value(original, from_parts)
        if not parts:
            return copy.deepcopy(value)
        _set_value(result, parts, copy.deepcopy(value))
        
    elif op_type == "move":
        from_path = op.get("from", "")
        from_parts = _parse_pointer(from_path)
        if not from_parts:
            raise ValueError("Cannot move from root")
        
        is_prefix = len(from_parts) < len(parts) and parts[:len(from_parts)] == from_parts
        if path.startswith(from_path) and path != from_path and is_prefix:
            raise ValueError("Cannot move into its own child")
        
        value = _remove_value(result, from_parts)
        if not parts:
            return copy.deepcopy(value)
        _set_value(result, parts, value)
        
    else:
        raise ValueError(f"Unknown operation: {op_type}")
    
    return result


def apply_patch(original: dict, patch: list) -> dict:
    """
    应用JSON Patch (RFC 6902) 到原始配置，返回新配置
    
    Args:
        original: 原始配置字典
        patch: JSON Patch操作列表
        
    Returns:
        应用patch后的新配置字典
        
    Raises:
        ValueError: patch格式错误或操作失败
    """
    if not isinstance(patch, list):
        raise ValueError("Patch must be a list")
    
    if HAS_JSONPATCH:
        try:
            result = copy.deepcopy(original)
            p = jsonpatch.JsonPatch(patch)
            return p.apply(result)
        except jsonpatch.JsonPatchException as e:
            raise ValueError(f"Patch application failed: {e}")
    
    result = copy.deepcopy(original)
    for op in patch:
        if not isinstance(op, dict):
            raise ValueError(f"Invalid patch operation: {op}")
        if "op" not in op:
            raise ValueError("Patch operation missing 'op' field")
        if "path" not in op:
            raise ValueError("Patch operation missing 'path' field")
        result = _apply_single_op(result, op)
    
    return result


def is_valid_patch(patch: list) -> Tuple[bool, str]:
    """
    验证patch格式是否合法
    
    Args:
        patch: JSON Patch操作列表
        
    Returns:
        (是否合法, 错误信息)
    """
    if not isinstance(patch, list):
        return False, "Patch must be a list"
    
    valid_ops = {"add", "remove", "replace", "copy", "move"}
    
    for i, op in enumerate(patch):
        if not isinstance(op, dict):
            return False, f"Operation {i}: must be a dictionary"
        
        if "op" not in op:
            return False, f"Operation {i}: missing 'op' field"
        
        op_type = op["op"]
        if op_type not in valid_ops:
            return False, f"Operation {i}: unknown operation '{op_type}'"
        
        if "path" not in op:
            return False, f"Operation {i}: missing 'path' field"
        
        path = op["path"]
        if not isinstance(path, str):
            return False, f"Operation {i}: 'path' must be a string"
        if not path.startswith("/") and path != "":
            return False, f"Operation {i}: invalid path format '{path}'"
        
        if op_type in ("add", "replace") and "value" not in op:
            return False, f"Operation {i}: '{op_type}' requires 'value' field"
        
        if op_type in ("copy", "move") and "from" not in op:
            return False, f"Operation {i}: '{op_type}' requires 'from' field"
        
        if op_type in ("copy", "move"):
            from_path = op.get("from", "")
            if not isinstance(from_path, str):
                return False, f"Operation {i}: 'from' must be a string"
            if not from_path.startswith("/") and from_path != "":
                return False, f"Operation {i}: invalid from path format '{from_path}'"
    
    return True, ""


def _diff_values(original: Any, modified: Any, path: str, patch: List[dict]) -> None:
    """递归比较两个值，生成patch操作"""
    if type(original) != type(modified):
        patch.append({"op": "replace", "path": path, "value": copy.deepcopy(modified)})
        return
    
    if isinstance(original, dict):
        orig_keys = set(original.keys())
        mod_keys = set(modified.keys())
        
        for key in orig_keys - mod_keys:
            new_path = f"{path}/{key.replace('~', '~0').replace('/', '~1')}" if path else f"/{key.replace('~', '~0').replace('/', '~1')}"
            patch.append({"op": "remove", "path": new_path})
        
        for key in mod_keys - orig_keys:
            new_path = f"{path}/{key.replace('~', '~0').replace('/', '~1')}" if path else f"/{key.replace('~', '~0').replace('/', '~1')}"
            patch.append({"op": "add", "path": new_path, "value": copy.deepcopy(modified[key])})
        
        for key in orig_keys & mod_keys:
            new_path = f"{path}/{key.replace('~', '~0').replace('/', '~1')}" if path else f"/{key.replace('~', '~0').replace('/', '~1')}"
            _diff_values(original[key], modified[key], new_path, patch)
            
    elif isinstance(original, list):
        if len(original) != len(modified):
            patch.append({"op": "replace", "path": path, "value": copy.deepcopy(modified)})
            return
        
        for i in range(len(original)):
            new_path = f"{path}/{i}" if path else f"/{i}"
            _diff_values(original[i], modified[i], new_path, patch)
            
    else:
        if original != modified:
            patch.append({"op": "replace", "path": path, "value": copy.deepcopy(modified)})


def generate_diff(original: dict, modified: dict) -> list:
    """
    对比两个配置，生成diff patch列表
    
    Args:
        original: 原始配置字典
        modified: 修改后的配置字典
        
    Returns:
        JSON Patch操作列表
    """
    if HAS_JSONPATCH:
        try:
            return jsonpatch.make_patch(original, modified).patch
        except Exception:
            pass
    
    patch: List[dict] = []
    _diff_values(original, modified, "", patch)
    return patch
