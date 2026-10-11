using System.Collections.Generic;
using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>地形调色板：13 种地形的基础色 + 山体顶面/侧面色。</summary>
    public static class TerrainPalette
    {
        //13 种地形的基色（取自 legacy-web ground_palette 的同源色系，略作提亮以适配 URP 的线性空间）
        static readonly Color[] Base =
        {
            new Color(0.13f, 0.24f, 0.42f, 1f), // 0 深水
            new Color(0.29f, 0.51f, 0.68f, 1f), // 1 浅滩
            new Color(0.85f, 0.79f, 0.58f, 1f), // 2 沙滩
            new Color(0.44f, 0.68f, 0.35f, 1f), // 3 草地
            new Color(0.55f, 0.58f, 0.34f, 1f), // 4 丘陵
            new Color(0.47f, 0.45f, 0.41f, 1f), // 5 山地
            new Color(0.88f, 0.89f, 0.92f, 1f), // 6 高岭（雪）
            new Color(0.72f, 0.26f, 0.12f, 1f), // 7 岩浆
            new Color(0.20f, 0.18f, 0.22f, 1f), // 8 外圈群山
            new Color(0.78f, 0.87f, 0.90f, 1f), // 9 冰面
            new Color(0.78f, 0.80f, 0.74f, 1f), // 10 盐湖
            new Color(0.66f, 0.60f, 0.48f, 1f), // 11 干河床
            new Color(0.42f, 0.40f, 0.39f, 1f), // 12 裸岩
        };

        public static Color Get(TerrainKind t)
        {
            int i = (int)t;
            return i >= 0 && i < Base.Length ? Base[i] : Color.magenta;
        }

        /// <summary>山体顶面（受光更强、更亮）。</summary>
        public static Color TopFace(TerrainKind t) => Color.Lerp(Get(t), Color.white, 0.22f);

        /// <summary>山体侧面（背光、更暗，制造高度错觉）。</summary>
        public static Color SideFace(TerrainKind t) => Color.Lerp(Get(t), Color.black, 0.35f);

        /// <summary>该地形是否绘制为"有厚度的山体"（顶面 + 两个侧面），而非平面。</summary>
        public static bool IsRaised(TerrainKind t)
            => t == TerrainKind.Mountain || t == TerrainKind.Ridge || t == TerrainKind.BareRock;

        /// <summary>该地形是否绘制顶面高光（雪峰）。</summary>
        public static bool IsSnowCap(TerrainKind t) => t == TerrainKind.Ridge;
    }

    /// <summary>
    /// 等距瓦片的网格坐标换算（Isometric Grid）。
    ///
    /// 采用 2:1 经典等距投影（菱形瓦片）。相比 web 版的「非等距四向正交」，
    /// 等距带来三处实质提升：
    ///   · 视觉上真正有"立体大陆"，而非俯视贴图；
    ///   · 格子 ↔ 像素变换仍是<b>纯仿射变换</b>，拾取与移动依然 O(1) 且精确；
    ///   · 高度（<c>baked.height</c>）可直接映射为 Z 偏移，无需重建碰撞网格。
    ///
    /// 变换公式（TileSize 为菱形半宽）：
    ///   screenX = (x - y) * TileW
    ///   screenY = (x + y) * TileH - elevation * ElevZ
    /// </summary>
    public static class IsoGrid
    {
        /// <summary>菱形半宽（像素/世界单位）。</summary>
        public const float TileW = 1f;

        /// <summary>菱形半高。2:1 等距即 TileH = TileW / 2。</summary>
        public const float TileH = 0.5f;

        /// <summary>每单位高程抬升的世界 Z。</summary>
        public const float ElevZ = 0.22f;

        /// <summary>山体额外厚度（让高地有实体感）。</summary>
        public const float RaisedZ = 0.35f;

        public static Vector3 CellToWorld(int x, int y, int elevation, bool raised)
        {
            float z = elevation / 255f * ElevZ + (raised ? RaisedZ : 0f);
            return new Vector3((x - y) * TileW, (x + y) * TileH, z);
        }

        /// <summary>世界坐标 → 格坐标（浮点，用于光标预览）。</summary>
        public static Vector2Int WorldToCell(Vector3 world)
        {
            float a = world.x / TileW;
            float b = world.y / TileH;
            // 逆变换：x = (a + b) / 2, y = (b - a) / 2
            return new Vector2Int(
                Mathf.RoundToInt((a + b) * 0.5f),
                Mathf.RoundToInt((b - a) * 0.5f));
        }
    }
}