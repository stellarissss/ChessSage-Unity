using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 玩家角色程序化立绘（64×80，锚点在脚下）。
    ///
    /// 上一版大地图<b>根本没有主角形象</b>（<see cref="OverworldPlayer"/> 只移动 transform），
    /// 玩家在画面上"看不见自己"。补上角色是"可玩性"最直接的修复：
    /// 有形象 → 有归属感 → 有探索意愿。
    /// </summary>
    internal static class PlayerArt
    {
        const int W = 64;
        const int H = 80;

        static Sprite _sprite;

        public static Sprite Character()
        {
            if (_sprite != null) return _sprite;

            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                name = "PlayerCharacter",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[W * H];

            var robe = new Color32(238, 236, 230, 255);
            var robeShade = new Color32(196, 194, 188, 255);
            var sash = new Color32(64, 116, 176, 255);
            var skin = new Color32(232, 196, 158, 255);
            var hair = new Color32(46, 40, 44, 255);
            var outline = new Color32(28, 26, 34, 255);
            var boot = new Color32(78, 62, 48, 255);

            void Put(int x, int y, Color32 c) => ProceduralPaint.Put(px, W, 0, 0, W, H, x, y, c);

            // 躯干（袍）
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 22, 26, 42, 58, robe);
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 22, 26, 26, 58, robeShade);   // 左侧阴影
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 22, 44, 42, 50, sash);        // 腰带
            // 双侧描边，保证在任何地形色上都"跳"出来
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 20, 26, 22, 60, outline);
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 42, 26, 44, 60, outline);

            // 下摆（略宽）
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 20, 20, 44, 28, robe);
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 20, 18, 44, 20, outline);

            // 靴子
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 24, 14, 32, 20, boot);
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 34, 14, 42, 20, boot);

            // 头 + 发
            ProceduralPaint.FillCircle(px, W, 0, 0, W, H, 32, 64, 11, skin);
            ProceduralPaint.FillCircle(px, W, 0, 0, W, H, 32, 68, 11, hair);
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 21, 60, 43, 68, hair);
            ProceduralPaint.FillCircle(px, W, 0, 0, W, H, 32, 66, 12, outline);        // 描边底
            ProceduralPaint.FillCircle(px, W, 0, 0, W, H, 32, 65, 11, skin);
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 22, 68, 42, 76, hair);         // 发顶

            // 眼睛
            Put(28, 63, outline);
            Put(36, 63, outline);

            // 肩部/手臂
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 16, 40, 22, 54, robe);
            ProceduralPaint.FillRect(px, W, 0, 0, W, H, 42, 40, 48, 54, robe);
            ProceduralPaint.FillCircle(px, W, 0, 0, W, H, 19, 40, 4, skin);
            ProceduralPaint.FillCircle(px, W, 0, 0, W, H, 45, 40, 4, skin);

            tex.SetPixels32(px);
            tex.Apply(false, false);

            _sprite = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.06f), 100f, 0, SpriteMeshType.FullRect);
            _sprite.name = "player";
            return _sprite;
        }
    }
}
