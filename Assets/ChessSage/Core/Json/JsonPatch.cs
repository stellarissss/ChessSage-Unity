using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Json
{
    /// <summary>
    /// RFC 6902 JSON Patch 实现。
    /// 与 legacy-web/shared/json_patch_utils.py 的纯 Python 分支逐语义对齐：
    /// 支持 add / remove / replace / copy / move，长度与越界检查、JSON Pointer 转义
    /// （~1→"/"、~0→"~"）、move 禁止移入自身子路径等约束完全一致。
    /// </summary>
    public static class JsonPatch
    {
        static readonly HashSet<string> ValidOps = new HashSet<string> { "add", "remove", "replace", "copy", "move" };

        /// <summary>解析 JSON Pointer 为路径段列表。</summary>
        public static List<string> ParsePointer(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return new List<string>();
            if (!path.StartsWith("/", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid JSON Pointer: {path}");

            var raw = path.Substring(1).Split('/');
            var result = new List<string>(raw.Length);
            foreach (var part in raw)
                result.Add(part.Replace("~1", "/").Replace("~0", "~"));
            return result;
        }

        static JToken GetValue(JToken obj, List<string> parts)
        {
            var current = obj;
            foreach (var part in parts)
            {
                if (current is JObject jo)
                {
                    if (!jo.TryGetValue(part, out var next))
                        throw new KeyNotFoundException($"Path not found: {part}");
                    current = next;
                }
                else if (current is JArray ja)
                {
                    if (!int.TryParse(part, out var idx))
                        throw new KeyNotFoundException($"Invalid array index: {part}");
                    if (idx < 0 || idx >= ja.Count)
                        throw new IndexOutOfRangeException($"Array index out of bounds: {idx}");
                    current = ja[idx];
                }
                else
                {
                    throw new KeyNotFoundException($"Cannot traverse into non-container type at: {part}");
                }
            }
            return current;
        }

        static JToken ResolveParent(JToken root, List<string> parts)
        {
            var current = root;
            for (int i = 0; i < parts.Count - 1; i++)
            {
                var part = parts[i];
                if (current is JObject jo)
                {
                    if (!jo.TryGetValue(part, out var next))
                        throw new KeyNotFoundException($"Path not found: {part}");
                    current = next;
                }
                else if (current is JArray ja)
                {
                    if (!int.TryParse(part, out var idx))
                        throw new KeyNotFoundException($"Invalid array index: {part}");
                    if (idx < 0 || idx >= ja.Count)
                        throw new IndexOutOfRangeException($"Array index out of bounds: {idx}");
                    current = ja[idx];
                }
                else
                {
                    throw new KeyNotFoundException($"Cannot traverse into non-container type at: {part}");
                }
            }
            return current;
        }

        static void SetValue(JToken obj, List<string> parts, JToken value)
        {
            if (parts.Count == 0) throw new ArgumentException("Cannot set root value directly");

            var parent = ResolveParent(obj, parts);
            var last = parts[parts.Count - 1];

            if (parent is JObject po)
            {
                po[last] = value;
            }
            else if (parent is JArray pa)
            {
                if (last == "-")
                {
                    pa.Add(value);
                }
                else
                {
                    if (!int.TryParse(last, out var idx))
                        throw new KeyNotFoundException($"Invalid array index: {last}");
                    if (idx < 0 || idx > pa.Count)
                        throw new IndexOutOfRangeException($"Array index out of bounds: {idx}");
                    pa.Insert(idx, value);
                }
            }
            else
            {
                throw new KeyNotFoundException("Cannot set value on non-container type");
            }
        }

        static JToken RemoveValue(JToken obj, List<string> parts)
        {
            if (parts.Count == 0) throw new ArgumentException("Cannot remove root");

            var parent = ResolveParent(obj, parts);
            var last = parts[parts.Count - 1];

            if (parent is JObject po)
            {
                if (!po.TryGetValue(last, out var removed))
                    throw new KeyNotFoundException($"Path not found: {last}");
                po.Remove(last);
                return removed;
            }
            if (parent is JArray pa)
            {
                if (!int.TryParse(last, out var idx))
                    throw new KeyNotFoundException($"Invalid array index: {last}");
                if (idx < 0 || idx >= pa.Count)
                    throw new IndexOutOfRangeException($"Array index out of bounds: {idx}");
                var removed = pa[idx];
                pa.RemoveAt(idx);
                return removed;
            }
            throw new KeyNotFoundException("Cannot remove from non-container type");
        }

        static JToken ApplySingleOp(JToken original, JObject op)
        {
            var result = (JToken)original.DeepClone();
            var opType = (string)op["op"];
            var path = op["path"]?.Value<string>() ?? string.Empty;
            var parts = ParsePointer(path);

            switch (opType)
            {
                case "add":
                {
                    if (!op.ContainsKey("value"))
                        throw new ArgumentException("'add' operation requires 'value' field");
                    if (parts.Count == 0) return (JToken)op["value"].DeepClone();
                    SetValue(result, parts, (JToken)op["value"].DeepClone());
                    break;
                }
                case "remove":
                {
                    if (parts.Count == 0) throw new ArgumentException("Cannot remove root");
                    RemoveValue(result, parts);
                    break;
                }
                case "replace":
                {
                    if (!op.ContainsKey("value"))
                        throw new ArgumentException("'replace' operation requires 'value' field");
                    if (parts.Count == 0) return (JToken)op["value"].DeepClone();
                    RemoveValue(result, parts);
                    SetValue(result, parts, (JToken)op["value"].DeepClone());
                    break;
                }
                case "copy":
                {
                    var fromPath = op["from"]?.Value<string>() ?? string.Empty;
                    var fromParts = ParsePointer(fromPath);
                    var value = GetValue(original, fromParts);
                    if (parts.Count == 0) return (JToken)value.DeepClone();
                    SetValue(result, parts, (JToken)value.DeepClone());
                    break;
                }
                case "move":
                {
                    var fromPath = op["from"]?.Value<string>() ?? string.Empty;
                    var fromParts = ParsePointer(fromPath);
                    if (fromParts.Count == 0) throw new ArgumentException("Cannot move from root");

                    bool isPrefix = fromParts.Count < parts.Count && IsPrefix(parts, fromParts);
                    if (path.StartsWith(fromPath, StringComparison.Ordinal) && path != fromPath && isPrefix)
                        throw new ArgumentException("Cannot move into its own child");

                    var value = RemoveValue(result, fromParts);
                    if (parts.Count == 0) return (JToken)value.DeepClone();
                    SetValue(result, parts, value);
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown operation: {opType}");
            }

            return result;
        }

        static bool IsPrefix(List<string> full, List<string> prefix)
        {
            for (int i = 0; i < prefix.Count; i++)
                if (full[i] != prefix[i]) return false;
            return true;
        }

        /// <summary>应用 JSON Patch，返回新文档（不修改入参）。</summary>
        public static JToken Apply(JToken original, JArray patch)
        {
            if (patch == null) throw new ArgumentException("Patch must be a list");

            var result = (JToken)original.DeepClone();
            foreach (var token in patch)
            {
                if (!(token is JObject op))
                    throw new ArgumentException($"Invalid patch operation: {token}");
                if (!op.ContainsKey("op"))
                    throw new ArgumentException("Patch operation missing 'op' field");
                if (!op.ContainsKey("path"))
                    throw new ArgumentException("Patch operation missing 'path' field");
                result = ApplySingleOp(result, op);
            }
            return result;
        }

        /// <summary>校验 patch 的格式合法性，返回 (是否合法, 错误信息)。</summary>
        public static (bool ok, string error) IsValid(JArray patch)
        {
            if (patch == null) return (false, "Patch must be a list");

            for (int i = 0; i < patch.Count; i++)
            {
                if (!(patch[i] is JObject op))
                    return (false, $"Operation {i}: must be a dictionary");

                if (!op.ContainsKey("op")) return (false, $"Operation {i}: missing 'op' field");

                var opType = op["op"]?.Value<string>();
                if (opType == null || !ValidOps.Contains(opType))
                    return (false, $"Operation {i}: unknown operation '{opType}'");

                if (!op.ContainsKey("path")) return (false, $"Operation {i}: missing 'path' field");

                var path = op["path"];
                if (path.Type != JTokenType.String)
                    return (false, $"Operation {i}: 'path' must be a string");
                var pathStr = path.Value<string>();
                if (!string.IsNullOrEmpty(pathStr) && !pathStr.StartsWith("/", StringComparison.Ordinal))
                    return (false, $"Operation {i}: invalid path format '{pathStr}'");

                if ((opType == "add" || opType == "replace") && !op.ContainsKey("value"))
                    return (false, $"Operation {i}: '{opType}' requires 'value' field");

                if ((opType == "copy" || opType == "move") && !op.ContainsKey("from"))
                    return (false, $"Operation {i}: '{opType}' requires 'from' field");

                if (opType == "copy" || opType == "move")
                {
                    var from = op["from"];
                    if (from.Type != JTokenType.String)
                        return (false, $"Operation {i}: 'from' must be a string");
                    var fromStr = from.Value<string>();
                    if (!string.IsNullOrEmpty(fromStr) && !fromStr.StartsWith("/", StringComparison.Ordinal))
                        return (false, $"Operation {i}: invalid from path format '{fromStr}'");
                }
            }
            return (true, "");
        }

        static string EscapePathSegment(string key) => key.Replace("~", "~0").Replace("/", "~1");

        static void DiffValues(JToken original, JToken modified, string path, JArray patch)
        {
            if (original == null || modified == null || original.Type != modified.Type)
            {
                patch.Add(new JObject { ["op"] = "replace", ["path"] = path, ["value"] = modified?.DeepClone() });
                return;
            }

            if (original is JObject oo && modified is JObject mo)
            {
                foreach (var prop in oo.Properties())
                {
                    if (!mo.ContainsKey(prop.Name))
                    {
                        var p = string.IsNullOrEmpty(path) ? "/" + EscapePathSegment(prop.Name) : path + "/" + EscapePathSegment(prop.Name);
                        patch.Add(new JObject { ["op"] = "remove", ["path"] = p });
                    }
                }
                foreach (var prop in mo.Properties())
                {
                    var p = string.IsNullOrEmpty(path) ? "/" + EscapePathSegment(prop.Name) : path + "/" + EscapePathSegment(prop.Name);
                    if (!oo.ContainsKey(prop.Name))
                        patch.Add(new JObject { ["op"] = "add", ["path"] = p, ["value"] = prop.Value.DeepClone() });
                    else
                        DiffValues(oo[prop.Name], prop.Value, p, patch);
                }
            }
            else if (original is JArray oa && modified is JArray ma)
            {
                if (oa.Count != ma.Count)
                {
                    patch.Add(new JObject { ["op"] = "replace", ["path"] = path, ["value"] = ma.DeepClone() });
                    return;
                }
                for (int i = 0; i < oa.Count; i++)
                {
                    var p = string.IsNullOrEmpty(path) ? "/" + i : path + "/" + i;
                    DiffValues(oa[i], ma[i], p, patch);
                }
            }
            else
            {
                if (!JToken.DeepEquals(original, modified))
                    patch.Add(new JObject { ["op"] = "replace", ["path"] = path, ["value"] = modified.DeepClone() });
            }
        }

        /// <summary>对比两份文档，生成 JSON Patch 列表。</summary>
        public static JArray GenerateDiff(JToken original, JToken modified)
        {
            var patch = new JArray();
            DiffValues(original, modified, "", patch);
            return patch;
        }
    }
}