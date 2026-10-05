using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Model
{
    /// <summary>
    /// 棋子模型。
    /// 与 legacy-web 中各棋类 board_state.json 的 pieces[] 条目一一对应：
    /// {id, type, name, side, position:[x,y], is_alive, custom_properties}。
    /// </summary>
    public sealed class Piece
    {
        public string Id;
        public string Type;
        public string Name;
        public string Side;
        public int X;
        public int Y;
        public bool IsAlive = true;
        public JObject CustomProperties = new JObject();
        /// <summary>模板中可能出现的其它字段（如 can_capture / eatable / invulnerable）。</summary>
        public JObject Extra = new JObject();

        public Piece() { }

        public Piece(string id, string type, string name, string side, int x, int y)
        {
            Id = id; Type = type; Name = name; Side = side; X = x; Y = y;
        }

        public Piece Clone() => new Piece
        {
            Id = Id,
            Type = Type,
            Name = Name,
            Side = Side,
            X = X,
            Y = Y,
            IsAlive = IsAlive,
            CustomProperties = (JObject)CustomProperties.DeepClone(),
            Extra = (JObject)Extra.DeepClone(),
        };

        public static Piece FromJson(JObject o)
        {
            var p = new Piece
            {
                Id = o["id"]?.Value<string>(),
                Type = o["type"]?.Value<string>(),
                Name = o["name"]?.Value<string>(),
                Side = o["side"]?.Value<string>(),
                IsAlive = o["is_alive"]?.Value<bool>() ?? true,
            };
            var pos = o["position"] as JArray;
            if (pos != null && pos.Count >= 2) { p.X = pos[0].Value<int>(); p.Y = pos[1].Value<int>(); }
            if (o["custom_properties"] is JObject cp) p.CustomProperties = (JObject)cp.DeepClone();

            // 保留 can_capture / eatable / invulnerable 等实例级修饰字段
            foreach (var name in new[] { "can_capture", "eatable", "invulnerable" })
                if (o.TryGetValue(name, out var v)) p.Extra[name] = v.DeepClone();
            return p;
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["id"] = Id,
                ["type"] = Type,
                ["name"] = Name,
                ["side"] = Side,
                ["position"] = new JArray(X, Y),
                ["is_alive"] = IsAlive,
                ["custom_properties"] = CustomProperties.DeepClone(),
            };
            foreach (var prop in Extra.Properties()) o[prop.Name] = prop.Value.DeepClone();
            return o;
        }
    }
}