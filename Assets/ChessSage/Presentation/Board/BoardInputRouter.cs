using System;
using UnityEngine;

namespace ChessSage.Presentation.Board
{
    /// <summary>
    /// 棋盘输入路由：把鼠标/触摸的世界坐标翻译成格坐标，并以"事件"形式抛给上层。
    ///
    /// 采用<b>依赖倒置</b>：表现层不认识 Core 的 <c>GameSession</c>，
    /// 只对外发出 <see cref="CellClicked"/> / <see cref="CellHovered"/>。
    /// 这样同一份输入层可服务于对局、大地图、编辑器预览等多种场景。
    /// </summary>
    public sealed class BoardInputRouter : MonoBehaviour
    {
        /// <summary>格被点击：(x, y)。</summary>
        public event Action<int, int> CellClicked;

        /// <summary>格被悬停（仅鼠标，触摸设备不触发）。</summary>
        public event Action<int, int> CellHovered;

        /// <summary>右键/双击取消当前选择。</summary>
        public event Action SelectionCancelled;

        public Camera Cam;
        public BoardView View;
        public float DragThresholdPixels = 8f;

        Vector3 _pressScreen;
        bool _pressed;
        bool _isTouch;

        void Reset()
        {
            Cam = Camera.main;
            View = GetComponent<BoardView>();
        }

        void Update()
        {
            if (Cam == null) Cam = Camera.main;
            if (Cam == null || View == null) return;

            HandleTouch();
            HandleMouse();
        }

        void HandleTouch()
        {
            if (Input.touchCount == 0) return;
            var t = Input.GetTouch(0);
            switch (t.phase)
            {
                case TouchPhase.Began:
                    _pressed = true;
                    _isTouch = true;
                    _pressScreen = t.position;
                    break;
                case TouchPhase.Ended:
                    if (_pressed && ((Vector3)t.position - _pressScreen).magnitude <= DragThresholdPixels)
                        EmitClick(t.position);
                    _pressed = false;
                    _isTouch = false;
                    break;
                case TouchPhase.Canceled:
                    _pressed = false;
                    _isTouch = false;
                    break;
            }
        }

        void HandleMouse()
        {
            // 触摸时忽略鼠标模拟事件，避免双触发。
            if (_isTouch && Input.touchCount > 0) return;

            if (Input.GetMouseButtonDown(1))
            {
                SelectionCancelled?.Invoke();
                return;
            }

            if (Input.GetMouseButtonDown(0))
            {
                _pressed = true;
                _pressScreen = Input.mousePosition;
            }
            else if (Input.GetMouseButtonUp(0) && _pressed)
            {
                _pressed = false;
                var delta = Input.mousePosition - _pressScreen;
                if (delta.magnitude <= DragThresholdPixels) EmitClick(Input.mousePosition);
            }

            var mp = Input.mousePosition;
            if (TryScreenToCell(mp, out int hx, out int hy)) CellHovered?.Invoke(hx, hy);
        }

        /// <summary>
        /// 模拟一次玩家点击（测试与试玩录制专用）。
        ///
        /// 之所以是公开 API 而不是让调用方反射触发 <see cref="CellClicked"/> 事件：
        /// C# 事件的 backing field 是 private，运行时反射在 AOT/IL2CPP 下不可靠。
        /// 显式提供入口让录制脚本走与真实输入完全相同的下游路径。
        /// </summary>
        public void SimulateClick(int x, int y) => CellClicked?.Invoke(x, y);

        void EmitClick(Vector3 screenPos)
        {
            if (TryScreenToCell(screenPos, out int x, out int y)) CellClicked?.Invoke(x, y);
        }

        bool TryScreenToCell(Vector3 screenPos, out int x, out int y)
        {
            var ray = Cam.ScreenPointToRay(screenPos);
            var plane = new Plane(Vector3.forward, Vector3.zero);
            if (!plane.Raycast(ray, out float dist))
            {
                x = y = 0;
                return false;
            }
            var world = ray.GetPoint(dist);
            return View.LocalToCell(transform.InverseTransformPoint(world), out x, out y);
        }
    }
}