using System.Collections.Generic;
using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 地标渲染器：把六道入口与各 POI 画成<b>体量明显的建筑</b>（静态，只建一次）。
    ///
    /// 地标数量固定（= POI 数 + 桥数），不随视野变化，故不做分块与卸载——
    /// 常驻 20 来个精灵可忽略。排序按 (x+y) 参与画家算法，保证玩家能走到建筑"后面"。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldLandmarkRenderer : MonoBehaviour
    {
        [Header("数据")]
        public OverworldMap Map;
        public float TileScale = 0.96f;

        [Tooltip("建筑整体尺寸系数（乘在 LandmarkPlanner.ScaleFor 之上）。")]
        public float SizeCoefficient = 0.62f;

        [Tooltip("是否为地标绘制落地投影。")]
        public bool EnableShadows = true;

        readonly List<SpriteRenderer> _sprites = new List<SpriteRenderer>();

        public int LandmarkCount { get; private set; }

        public void Initialize(OverworldMap map)
        {
            Map = map;
            foreach (var lm in LandmarkPlanner.Plan(map))
                Spawn(lm.Style, lm.X, lm.Y, LandmarkPlanner.ScaleFor(lm.Style), lm.Label);

            // 桥梁也是地图上的"实体"，一并作为小型地标绘制（石桥/木桥用色区分）。
            foreach (var b in map.Bridges)
                SpawnBridge(b);
        }

        void Spawn(LandmarkStyle style, int x, int y, float scale, string label)
        {
            var kind = Map.GetTerrain(x, y);
            bool raised = TerrainPalette.IsRaised(kind);
            var centre = IsoGrid.CellToWorld(x, y, Map.GetHeight(x, y), raised) * TileScale;
            float s = scale * SizeCoefficient;

            if (EnableShadows)
            {
                var sh = NewSprite("lm_shadow", DecorAtlas.Shadow(),
                    centre + new Vector3(0f, -0.02f, 0f),
                    new Vector3(s * 1.5f, s * 0.6f, 1f),
                    new Color(1f, 1f, 1f, 0.5f), OverworldSort.Base(x, y) + 3);
                _sprites.Add(sh);
            }

            var sr = NewSprite("landmark_" + style, LandmarkArt.Get(style), centre,
                new Vector3(s, s, 1f), Color.white,
                OverworldSort.Base(x, y) + 4);
            _sprites.Add(sr);
            LandmarkCount++;
        }

        void SpawnBridge(BridgeData b)
        {
            var kind = Map.GetTerrain(b.X, b.Y);
            bool raised = TerrainPalette.IsRaised(kind);
            var centre = IsoGrid.CellToWorld(b.X, b.Y, Map.GetHeight(b.X, b.Y), raised) * TileScale;
            var tint = b.Type == "wood" ? new Color(0.72f, 0.56f, 0.36f) : new Color(0.78f, 0.76f, 0.72f);
            var sr = NewSprite("bridge_" + b.Id, LandmarkArt.Bridge(),
                centre, new Vector3(0.95f, 0.95f, 1f), tint,
                OverworldSort.Base(b.X, b.Y) + 3);
            _sprites.Add(sr);
        }

        SpriteRenderer NewSprite(string name, Sprite sprite, Vector3 pos, Vector3 scale, Color color, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            return sr;
        }
    }
}
