using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Json
{
    /// <summary>
    /// JSON Schema Draft-07 子集校验器。
    /// 覆盖 legacy-web/shared/schemas/*.schema.json 实际使用的关键字：
    /// type（含类型数组与 null）、enum、properties、required、additionalProperties、
    /// items、minItems、maxItems、minimum、maximum、minLength、maxLength。
    /// 错误信息形如 "[路径] 描述"，与 schema_validator.py 的呈现方式一致。
    /// </summary>
    public static class JsonSchemaLite
    {
        public static List<string> Validate(JToken schema, JToken instance)
        {
            var errors = new List<(string path, string msg)>();
            ValidateNode(schema, instance, new List<string>(), errors);
            return errors.Select(e => $"[{(e.path.Length == 0 ? "root" : e.path)}] {e.msg}").ToList();
        }

        static void AddError(List<(string, string)> errors, List<string> path, string msg)
        {
            errors.Add((string.Join(".", path), msg));
        }

        static bool MatchesType(JToken instance, string type)
        {
            switch (type)
            {
                case "object": return instance.Type == JTokenType.Object;
                case "array": return instance.Type == JTokenType.Array;
                case "string": return instance.Type == JTokenType.String;
                case "boolean": return instance.Type == JTokenType.Boolean;
                case "null": return instance.Type == JTokenType.Null;
                case "integer": return (instance.Type == JTokenType.Integer) ||
                                        (instance.Type == JTokenType.Float && instance.Value<double>() % 1 == 0);
                case "number": return instance.Type == JTokenType.Integer || instance.Type == JTokenType.Float;
                default: return true;
            }
        }

        static void ValidateNode(JToken schema, JToken instance, List<string> path, List<(string, string)> errors)
        {
            if (schema == null) return;
            if (schema.Type == JTokenType.Boolean)
            {
                if (!schema.Value<bool>())
                    AddError(errors, path, "schema is false; value is not allowed");
                return;
            }
            if (!(schema is JObject s)) return;

            // type
            if (s["type"] != null)
            {
                var expected = new List<string>();
                if (s["type"].Type == JTokenType.String) expected.Add(s["type"].Value<string>());
                else if (s["type"] is JArray arr) expected.AddRange(arr.Select(t => t.Value<string>()));

                if (expected.Count > 0 && !expected.Any(t => MatchesType(instance, t)))
                {
                    var actual = instance.Type == JTokenType.Integer ? "integer"
                        : instance.Type == JTokenType.Float ? "number"
                        : instance.Type == JTokenType.Object ? "object"
                        : instance.Type == JTokenType.Array ? "array"
                        : instance.Type == JTokenType.String ? "string"
                        : instance.Type == JTokenType.Boolean ? "boolean"
                        : instance.Type == JTokenType.Null ? "null"
                        : instance.Type.ToString().ToLowerInvariant();
                    AddError(errors, path, $"{Describe(instance)} is not of type {string.Join(", ", expected.Select(t => $"'{t}'"))} (actual: {actual})");
                    return;
                }
            }

            // enum
            if (s["enum"] is JArray enumValues)
            {
                if (!enumValues.Any(v => JToken.DeepEquals(v, instance)))
                    AddError(errors, path, $"{Describe(instance)} is not one of [{string.Join(", ", enumValues.Select(Describe))}]");
            }

            // 数值范围
            if (instance.Type == JTokenType.Integer || instance.Type == JTokenType.Float)
            {
                double value = instance.Value<double>();
                if (s["minimum"] != null && value < s["minimum"].Value<double>())
                    AddError(errors, path, $"{value} is less than the minimum of {s["minimum"].Value<double>()}");
                if (s["maximum"] != null && value > s["maximum"].Value<double>())
                    AddError(errors, path, $"{value} is greater than the maximum of {s["maximum"].Value<double>()}");
            }

            // 字符串长度
            if (instance.Type == JTokenType.String)
            {
                var text = instance.Value<string>() ?? string.Empty;
                if (s["minLength"] != null && text.Length < s["minLength"].Value<int>())
                    AddError(errors, path, $"'{text}' is too short");
                if (s["maxLength"] != null && text.Length > s["maxLength"].Value<int>())
                    AddError(errors, path, $"'{text}' is too long");
            }

            // 数组
            if (instance is JArray array)
            {
                if (s["minItems"] != null && array.Count < s["minItems"].Value<int>())
                    AddError(errors, path, $"[{string.Join(", ", array.Select(Describe))}] is too short (minItems: {s["minItems"].Value<int>()})");
                if (s["maxItems"] != null && array.Count > s["maxItems"].Value<int>())
                    AddError(errors, path, $"[{string.Join(", ", array.Select(Describe))}] is too long (maxItems: {s["maxItems"].Value<int>()})");
                if (s["items"] is JToken itemSchema)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        path.Add(i.ToString());
                        ValidateNode(itemSchema, array[i], path, errors);
                        path.RemoveAt(path.Count - 1);
                    }
                }
            }

            // 对象
            if (instance is JObject obj)
            {
                if (s["required"] is JArray required)
                {
                    foreach (var name in required.Select(t => t.Value<string>()))
                        if (!obj.ContainsKey(name))
                        {
                            path.Add(name);
                            AddError(errors, path, $"'{name}' is a required property");
                            path.RemoveAt(path.Count - 1);
                        }
                }

                if (s["properties"] is JObject properties)
                {
                    foreach (var prop in obj.Properties())
                    {
                        if (properties.TryGetValue(prop.Name, out var propSchema))
                        {
                            path.Add(prop.Name);
                            ValidateNode(propSchema, prop.Value, path, errors);
                            path.RemoveAt(path.Count - 1);
                        }
                        else if (s["additionalProperties"] != null)
                        {
                            if (s["additionalProperties"].Type == JTokenType.Boolean)
                            {
                                if (!s["additionalProperties"].Value<bool>())
                                {
                                    path.Add(prop.Name);
                                    AddError(errors, path, "Additional properties are not allowed");
                                    path.RemoveAt(path.Count - 1);
                                }
                            }
                            else
                            {
                                path.Add(prop.Name);
                                ValidateNode(s["additionalProperties"], prop.Value, path, errors);
                                path.RemoveAt(path.Count - 1);
                            }
                        }
                    }
                }
                else if (s["additionalProperties"] != null)
                {
                    if (s["additionalProperties"].Type == JTokenType.Boolean && !s["additionalProperties"].Value<bool>())
                    {
                        foreach (var prop in obj.Properties())
                        {
                            path.Add(prop.Name);
                            AddError(errors, path, "Additional properties are not allowed");
                            path.RemoveAt(path.Count - 1);
                        }
                    }
                    else if (s["additionalProperties"].Type != JTokenType.Boolean)
                    {
                        foreach (var prop in obj.Properties())
                        {
                            path.Add(prop.Name);
                            ValidateNode(s["additionalProperties"], prop.Value, path, errors);
                            path.RemoveAt(path.Count - 1);
                        }
                    }
                }
            }
        }

        static string Describe(JToken token)
        {
            if (token == null) return "null";
            switch (token.Type)
            {
                case JTokenType.Null: return "null";
                case JTokenType.String: return "'" + token.Value<string>() + "'";
                case JTokenType.Object: return "{...}";
                case JTokenType.Array: return "[...]";
                default: return token.ToString();
            }
        }
    }
}