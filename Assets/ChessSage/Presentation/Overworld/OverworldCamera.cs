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
            transform.position = new Vector3(transform.position.x, transform.position.y, CameraZ);
        }

        void LateUpdate()
        {
            if (Target == null) return;

            var p = Target.WorldPosition;
            var desired = new Vector3(p.x, p.y, CameraZ);
            if (ClampToMap && Map != null) desired = Clamp(desired);

            transform.position = Vector3.SmoothDamp(transform.position, desired, ref _vel, SmoothTime);
        }

        /// <summary>
        /// 钳制到地图包围盒。等距下 x=(cx-cy)、y=(cx+cy)/2，
        /// 故可玩区在屏幕空间的 Y 范围远小于 X —— 这是等距地图容易忽略的细节。
        /// </summary>
        Vector3 Clamp(Vector3 desired)
        {
            float halfW = Map.Width * Map.Height > 0 ? OrthoSize * (Screen.width / (float)Screen.height) : OrthoSize;
            float maxX = (Map.Width + Map.Height) * IsoGrid.TileW * 0.5f;
            float maxY = (Map.Width + Map.Height) * IsoGrid.TileH * 0.5f;
            desired.x = Mathf.Clamp(desired.x, -maxX + halfW, maxX - halfW);
            desired.y = Mathf.Clamp(desired.y, -maxY + OrthoSize, maxY - OrthoSize);
            return desired;
        }

        /// <summary>一键聚焦某个格（六道入口快速旅行 / 小地图跳转）。</summary>
        public void FocusOn(int cellX, int cellY)
        {
            if (Map == null) return;
            var kind = Map.GetTerrain(cellX, cellY);
            var p = IsoGrid.CellToWorld(cellX, cellY, Map.GetHeight(cellX, cellY),
                TerrainPalette.IsRaised(kind));
            transform.position = new Vector3(p.x, p.y, CameraZ);
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