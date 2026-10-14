using System.Collections.Generic;
using UnityEngine;

namespace ChessSage.Presentation.Samsara
{
    /// <summary>
    /// 轻量界面栈/路由：同一时刻只显示栈顶一屏，退栈时自动恢复下一屏。
    ///
    /// 【为什么需要】RPG 有四到五个全屏界面（选关 / 技能树 / 剧情 / 结局 / 记忆），
    /// 若各自 <c>SetActive</c> 容易互相覆盖、留下僵尸界面。用一个极小的栈统一管理
    /// 「显示谁、按返回回到谁」，比引入完整 UI 框架（导航图 / 生命周期 / 转场）
    /// 更贴合当前体量——够用即可，不做过度设计。
    ///
    /// 只做三件事：Push（入栈并独占显示）、Pop（出栈并恢复下层）、Replace（换顶）。
    /// 不持有业务状态，因此可被 <see cref="SamsaraRpg"/> 单点调用。
    /// </summary>
    public sealed class UiScreenStack
    {
        readonly List<GameObject> _stack = new List<GameObject>();

        public int Count => _stack.Count;
        public bool IsEmpty => _stack.Count == 0;
        public GameObject Top => _stack.Count == 0 ? null : _stack[_stack.Count - 1];

        /// <summary>入栈并显示；若已有栈顶则先隐藏。传 null 为空操作。</summary>
        public void Push(GameObject screen)
        {
            if (screen == null) return;
            if (Top != null) Top.SetActive(false);
            _stack.Add(screen);
            screen.SetActive(true);
        }

        /// <summary>出栈并隐藏，恢复下一屏；空栈返回 null。</summary>
        public GameObject Pop()
        {
            if (_stack.Count == 0) return null;
            var top = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            if (top != null) top.SetActive(false);
            if (Top != null) Top.SetActive(true);
            return top;
        }

        /// <summary>换顶：出栈当前顶并压入新屏（不中转显示下层，避免一帧闪烁）。</summary>
        public void Replace(GameObject screen)
        {
            if (_stack.Count > 0)
            {
                var top = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
                if (top != null) top.SetActive(false);
            }
            Push(screen);
        }

        /// <summary>清空并隐藏全部。</summary>
        public void Clear()
        {
            foreach (var go in _stack)
                if (go != null) go.SetActive(false);
            _stack.Clear();
        }
    }
}
