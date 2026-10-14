using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 大地图相机：平滑跟随 + 边界钳制 + 等距固定角度。
    ///
    /// 等距投影下"正确的视角"是固定的 30°俯角，因此这里不提供自由旋转 ——
    /// 允许玩家旋转会立刻破坏格子 ↔ 屏幕的映射直觉（Web 版就因为可旋转导致
    /// 方向键手感与地图错位）。需要表现纵深时用<b>平移</b>而非旋转。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldCamera : MonoBehaviour
    {
        [Header("目标")]
        public OverworldPlayer Target;

        [Header("参数")]
        [Tooltip("跟随平滑度（越大越快）。")]
        public float SmoothTime = 0.14f;

        [Tooltip("视野高度（正交尺寸的一半，决定能看到多少格）。")]
        public float OrthoSize = 8f;

        [Tooltip("世界Z（等距 2D：负值表示相机往后退，配合正交投影得到伪 3D 视角）。")]
        public float CameraZ = -30f;

        [Header("边界")]
        public bool ClampToMap = true;
        public OverworldMap Map;

        [Tooltip("与分块渲染器一致的每格世界缩放（钳制范围需要它）。")]
        public float TileScale = 0.96f;

        Vector3 _vel;

        void Start()
        {
            if (Target == null) Target = FindObjectOfType<OverworldPlayer>();
            if (Cam2D == null) Cam2D = Camera.main;
            ApplyOrtho();
        }

        /// <summary>实际驱动的相机。由组合根注入。</summary>
        public Camera Cam2D;

        void ApplyOrtho()
        {
            if (Cam2D == null) Cam2D = Camera.main;
            if (Cam2D == null) return;
            Cam2D.orthographic = true;
            Cam2D.orthographicSize = OrthoSize;
            var p = Cam2D.transform.position;
            Cam2D.transform.position = new Vector3(p.x, p.y, CameraZ);
        }

        void LateUpdate()
        {
            if (Target == null) return;
            if (Cam2D == null) Cam2D = Camera.main;
            if (Cam2D == null) return;

            var p = Target.WorldPosition;
            var desired = new Vector3(p.x, p.y, CameraZ);
            if (ClampToMap && Map != null) desired = Clamp(desired);

            // 关键修复：相机跟随必须作用在 <b>真正的相机构体</b>（Cam2D）上。
            // 历史实现写的是本组件所在物体的 transform，而那正是与玩家/世界根共用的物体，
            // 等于"自己追自己"——相机实际从未跟随，玩家走几步就飘出画面。
            Cam2D.transform.position = Vector3.SmoothDamp(Cam2D.transform.position, desired, ref _vel, SmoothTime);
        }

        /// <summary>
        /// 钳制到地图<b>真实世界包围盒</b>。
        ///
        /// 历史实现用的是 <c>(W+H)*TileW*0.5</c> 这类"以原点为中心"的假设，
        /// 而等距地图在世界里其实位于 x∈[-(H-1), W-1]、y∈[0, (W+H-2)/2]（未居中）。
        /// 于是 y 方向被钳到不足实际范围的一半，相机长期把玩家顶到画面边缘。
        /// 现在直接由 <see cref="IsoGrid"/> 推出四至，再按视野半宽/半高内缩。
        /// </summary>
        Vector3 Clamp(Vector3 desired)
        {
            float s = TileScale;
            float minX = -(Map.Height - 1) * IsoGrid.TileW * s;
            float maxX = (Map.Width - 1) * IsoGrid.TileW * s;
            float minY = 0f;
            float maxY = (Map.Width + Map.Height - 2) * IsoGrid.TileH * s;

            float halfW = OrthoSize * (Screen.width / (float)Mathf.Max(1, Screen.height));
            float halfH = OrthoSize;

            // 地图某方向比视野还小时，直接居中而不是产生反转区间。
            desired.x = (maxX - minX) > 2f * halfW
                ? Mathf.Clamp(desired.x, minX + halfW, maxX - halfW)
                : (minX + maxX) * 0.5f;
            desired.y = (maxY - minY) > 2f * halfH
                ? Mathf.Clamp(desired.y, minY + halfH, maxY - halfH)
                : (minY + maxY) * 0.5f;
            return desired;
        }

        /// <summary>一键聚焦某个格（六道入口快速旅行 / 小地图跳转）。</summary>
        public void FocusOn(int cellX, int cellY)
        {
            if (Map == null) return;
            if (Cam2D == null) Cam2D = Camera.main;
            var kind = Map.GetTerrain(cellX, cellY);
            var p = IsoGrid.CellToWorld(cellX, cellY, Map.GetHeight(cellX, cellY),
                TerrainPalette.IsRaised(kind));
            if (Cam2D != null) Cam2D.transform.position = new Vector3(p.x, p.y, CameraZ);
            _vel = Vector3.zero;
        }

        /// <summary>缩放（滚轮 / 双指），带上下限。</summary>
        public void Zoom(float delta)
        {
            OrthoSize = Mathf.Clamp(OrthoSize - delta * 2f, 4f, 24f);
            ApplyOrtho();
        }
    }
}