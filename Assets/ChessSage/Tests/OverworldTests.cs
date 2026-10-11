using System.Collections.Generic;
using ChessSage.Core.Overworld;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace ChessSage.Tests
{
    /// <summary>
    /// 大地图（��道大陆）数据与导航的回归测试。
    ///
    /// 这些测试直接校验 <c>configs/global/overworld.json</c> 的<b>真实数据</b>，
    /// 相当于把legacy-web 设计文档里的验收口径固化为可执行断言：
    ///   · 尺寸 112×84、外圈6 格 wall 占比 23.5%
    ///   · 全部 11 个 POI 存在且落在可行走格上（web 版曾因障碍围死入口而全图不可达）
    ///   · 六道入口全部可达（BFS 连通性）
    /// </summary>
    public sealed class OverworldTests
    {
        const int WallThickness = 6;

        static OverworldMap Load()
        {
            var path = System.IO.Path.Combine(TestEnv.ConfigsRoot, "global", "overworld.json");
            Assert.IsTrue(System.IO.File.Exists(path), "缺少大地图配置：" + path);
            return OverworldMap.FromJson(JObject.Parse(System.IO.File.ReadAllText(path)));
        }

        [Test]
        public void Map_HasExpectedDimensions()
        {
            var m = Load();
            Assert.AreEqual(112, m.Width);
            Assert.AreEqual(84, m.Height);
            Assert.AreEqual(9408, m.Terrain.Length, "terrain 字节数应等于 width×height");
            Assert.AreEqual(9408, m.RegionIndex.Length);
            Assert.AreEqual(9408, m.Elevation.Length);
        }

        [Test]
        public void Map_BorderIsMountainsAndFullyWalkableInside()
        {
            var m = Load();

            // 四条外圈必须全是 Wall
            for (int x = 0; x < m.Width; x++)
            {
                Assert.AreEqual(TerrainKind.Wall, m.GetTerrain(x, 0));
                Assert.AreEqual(TerrainKind.Wall, m.GetTerrain(x, m.Height - 1));
            }
            for (int y = 0; y < m.Height; y++)
            {
                Assert.AreEqual(TerrainKind.Wall, m.GetTerrain(0, y));
                Assert.AreEqual(TerrainKind.Wall, m.GetTerrain(m.Width - 1, y));
            }

            // 外圈厚度为 6
            Assert.AreEqual(TerrainKind.Wall, m.GetTerrain(WallThickness - 1, 40));
            Assert.AreNotEqual(TerrainKind.Wall, m.GetTerrain(WallThickness, 40));

            // wall 占比应约 23.5%
            int walls = 0;
            for (int y = 0; y < m.Height; y++)
                for (int x = 0; x < m.Width; x++)
                    if (m.GetTerrain(x, y) == TerrainKind.Wall) walls++;
            float ratio = (float)walls / (m.Width * m.Height);
            Assert.That(ratio, Is.InRange(0.23f, 0.24f), "外圈群山占比应约 23.5%，实际 " + ratio);
        }

        [Test]
        public void Map_TerrainCodeRangeIsLegal()
        {
            var m = Load();
            for (int y = 0; y < m.Height; y++)
            {
                for (int x = 0; x < m.Width; x++)
                {
                    Assert.IsTrue(m.GetTerrain(x, y) <= TerrainKind.BareRock,
                        $"({x},{y}) 地形码越界：{(int)m.GetTerrain(x, y)}");
                    int ri = m.GetRegionIndex(x, y);
                    Assert.That(ri, Is.InRange(-1, m.Regions.Count - 1), $"({x},{y}) 区域索引越界：{ri}");
                }
            }
        }

        [Test]
        public void Map_HasNineRegions()
        {
            var m = Load();
            Assert.AreEqual(9, m.Regions.Count, "应为九大区域");
            foreach (var r in m.Regions)
            {
                Assert.IsNotEmpty(r.Id);
                Assert.IsNotEmpty(r.Name);
                Assert.AreEqual(4, r.Rect.Length);
                Assert.IsTrue(r.Radius > 0);
            }
        }

        [Test]
        public void Map_AllPoisSitOnWalkableGround()
        {
            var m = Load();
            Assert.AreEqual(11, m.Pois.Count, "应为 11 个 POI（spawn + 6 道 + 沙盒 + NPC + 告示牌 + 成就殿）");
            foreach (var p in m.Pois)
            {
                Assert.IsTrue(m.InBounds(p.X, p.Y), $"POI {p.Id} 越界：({p.X},{p.Y})");
                Assert.IsTrue(m.IsWalkable(p.X, p.Y),
                    $"POI {p.Id} 落在不可通行地形 {m.GetTerrain(p.X, p.Y)} 上——web 版曾因此导致入口不可达");
            }
        }

        [Test]
        public void Map_SixRealmsPresentAndCorrect()
        {
            var m = Load();
            var expected = new[] { "hell", "hungry", "animal", "human", "asura", "heaven" };
            var found = new HashSet<string>();
            foreach (var p in m.RealmPois()) found.Add(p.Realm);
            foreach (var e in expected) Assert.IsTrue(found.Contains(e), $"缺少六道入口：{e}");
        }

        [Test]
        public void Map_PlayerSpawnIsWalkableAndNearCenter()
        {
            var m = Load();
            Assert.IsTrue(m.IsWalkable(m.PlayerSpawn[0], m.PlayerSpawn[1]));
            // 出生点应在中央平原一带
            var region = m.GetRegion(m.PlayerSpawn[0], m.PlayerSpawn[1]);
            Assert.IsNotNull(region);
            Assert.AreEqual(Realm.CPlain, region.Id);
        }

        [Test]
        public void Nav_EveryRealmIsReachableFromSpawn()
        {
            // 这是 web 版最严重的缺陷（入口被障碍围死）对应的回归防线。
            var m = Load();
            var flow = new FlowField(m);
            flow.Build(m.PlayerSpawn[0], m.PlayerSpawn[1]);

            foreach (var p in m.RealmPois())
            {
                var d = flow.DistanceAt(p.X, p.Y);
                Assert.AreNotEqual(int.MaxValue, d, $"六道入口 {p.Id}({p.Realm}) 从出生点不可达");
                Assert.Greater(d, 0, "入口不应与出生点重合");
            }

            foreach (var p in m.Pois)
                Assert.AreNotEqual(int.MaxValue, flow.DistanceAt(p.X, p.Y), $"POI {p.Id} 从出生点不可达");
        }

        [Test]
        public void Nav_StepsReduceDistanceMonotonically()
        {
            var m = Load();
            var flow = new FlowField(m);
            var target = m.FindPoi("realm_hell");
            flow.Build(target.X, target.Y);

            int x = m.PlayerSpawn[0], y = m.PlayerSpawn[1];
            int startDist = flow.DistanceAt(x, y);
            for (int i = 0; i < 5000 && startDist > 0; i++)
            {
                var step = flow.StepToward(x, y);
                if (step == null) break;
                int nd = flow.DistanceAt(step[0], step[1]);
                Assert.Less(nd, flow.DistanceAt(x, y), $"第 {i} 步距离未下降（梯度下降失效）");
                x = step[0]; y = step[1];
            }
            Assert.AreEqual(0, flow.DistanceAt(x, y), "沿流场应能精确抵达目标");
Assert.Greater(startDist, 20, "出生点到地狱道应有实质路程");
        }

        [Test]
        public void Nav_BlockedByOuterWallOnly()
        {
            var m = Load();
            // 深水不阻挡（web 版既定口径）
            int water = 0;
            for (int y = 6; y < m.Height - 6; y++)
                for (int x = 6; x < m.Width - 6; x++)
                    if (OverworldMap.IsWater(m.GetTerrain(x, y))) { water++; Assert.IsTrue(m.IsWalkable(x, y)); }
            Assert.Greater(water, 500, "内陆应存在水面（且可通行）");

            // 山体也不阻挡
            for (int y = 6; y < m.Height - 6; y++)
                for (int x = 6; x < m.Width - 6; x++)
                    if (OverworldMap.IsMountain(m.GetTerrain(x, y)))
                        Assert.IsTrue(m.IsWalkable(x, y), "山体不阻挡（仅外圈群山阻挡）");
        }

        [Test]
        public void Fog_BitmapRevealIsIdempotentAndBounded()
        {
            var m = Load();
            var fog = new FogOfWar(m);
            Assert.AreEqual(0f, fog.ExploredRatio(), "初始应全未探索");

            fog.RevealAround(58, 45, 3);
            float after = fog.ExploredRatio();
            Assert.Greater(after, 0f);
            Assert.Less(after, 0.1f, "单次小范围揭示不应覆盖全图");

            fog.RevealAround(58, 45, 3);
            Assert.AreEqual(after, fog.ExploredRatio(), "重复揭示同一区域应为幂等");

            // 揭示到边界外不应越界写入
            fog.RevealAround(0, 0, 5);
            Assert.IsFalse(fog.IsExplored(-1, -1));
            Assert.IsFalse(fog.IsExplored(m.Width, m.Height));

            fog.RevealAround(58, 45, 1000);
            Assert.AreEqual(1f, fog.ExploredRatio(), 0.001f, "全图揭示后覆盖率应为 100%");
        }

        [Test]
        public void Chunk_WindowLoadsOnlyNearbyChunks()
        {
            var m = Load();
            var cm = new ChunkManager(m);
            Assert.AreEqual(7, cm.ChunksX, "112/16 = 7 块");
            Assert.AreEqual(6, cm.ChunksY, "ceil(84/16) = 6 块");

            cm.UpdateWindow(58, 45, 1);
            int cx = ChunkManager.ChunkOf(58), cy = ChunkManager.ChunkOf(45);
            Assert.IsTrue(cm.ShouldLoad(cx, cy), "玩家所在块必须待加载");

            cm.ComputeWindow(58, 45, 1, out int minCx, out int minCy, out int maxCx, out int maxCy);
            Assert.AreEqual(2, minCx);
            Assert.AreEqual(4, maxCx, "半径 1 应覆盖 3 个块宽");
            Assert.IsFalse(minCx <= 0 && maxCx >= 0, "远处块 (0,0) 不应落在加载窗口内");

            cm.MarkLoaded(cx, cy);
            Assert.IsFalse(cm.ShouldLoad(cx, cy), "已加载块不应重复加载");
            Assert.IsTrue(cm.ShouldLoad(0, 0), "尚未加载的远处块仍需加载");

            // 走到远处后，原块应进入卸载队列
            cm.UpdateWindow(5, 5, 1);
            Assert.IsTrue(cm.PendingUnload.Contains(ChunkManager.Key(cx, cy)), "离开视野的块应被标记卸载");
        }

        [Test]
        public void Map_ParsingRejectsMismatchedSize()
        {
            var root = new JObject
            {
                ["world"] = new JObject { ["width"] = 4, ["height"] = 4 },
                ["baked"] = new JObject
                {
                    ["size"] = new JArray(4, 4),
                    ["terrain"] = "abc",   // 长度 3≠ 16
                },
            };
            Assert.Throws<System.InvalidOperationException>(() => OverworldMap.FromJson(root));
        }
    }
}