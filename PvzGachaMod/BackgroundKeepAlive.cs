using System;
using System.Runtime.InteropServices;

namespace PvzGachaMod
{
    /// <summary>
    /// 让游戏在失去焦点后继续跑。
    ///
    /// 背景：本作构建时没勾 Run In Background，且 Application.runInBackground 被 Unity 链接器裁掉了
    /// （Application 只剩 24 个成员）。所以一失去焦点，原生主循环就停住 —— Update 不再被调用，
    /// 状态文件也不更新，面板看起来像"连不上"。实测：失焦后状态文件 44 秒不更新，点回游戏立刻恢复。
    ///
    /// 做法：Unity 是**根据窗口消息**判定焦点状态的（收到 WM_ACTIVATEAPP(FALSE) 就认为进了后台）。
    /// 这里把游戏窗口的 WndProc 换成一个自己的，把"失去激活"类消息直接吞掉，
    /// 只把"获得激活"的消息转发给原处理 —— 于是 Unity 永远认为自己在前台，主循环照常跑。
    ///
    /// 安全：任何一步失败都只是放弃这个功能并记日志，绝不影响游戏本体。
    /// 必须用游戏自己的窗口句柄，且回调委托要静态保活（否则 GC 回收后窗口消息会崩）。
    /// </summary>
    internal static class BackgroundKeepAlive
    {
        private const int GWL_WNDPROC = -4;
        private const int WM_ACTIVATEAPP = 0x001C;
        private const int WM_ACTIVATE = 0x0006;
        private const int WM_NCACTIVATE = 0x0086;
        private const int WM_KILLFOCUS = 0x0008;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        // 必须静态保活：委托被 GC 掉之后窗口还会回调这个地址，直接崩游戏
        private static WndProcDelegate _hook;
        private static IntPtr _oldProc = IntPtr.Zero;
        private static IntPtr _hwnd = IntPtr.Zero;

        public static bool Installed { get { return _oldProc != IntPtr.Zero; } }
        public static int Swallowed;      // 吞掉了多少条"失焦"消息（用来看是否真的在起作用）
        public static string LastError = "";

        /// <summary>装挂钩。重复调用无副作用。</summary>
        public static bool Install()
        {
            if (Installed) return true;

            try
            {
                _hwnd = GetActiveWindow();
                if (_hwnd == IntPtr.Zero)   // 没有活动窗口就等下一帧再试
                {
                    LastError = "拿不到游戏窗口句柄";
                    return false;
                }

                _hook = HookProc;
                IntPtr newProc = Marshal.GetFunctionPointerForDelegate(_hook);
                IntPtr old = SetWindowLongPtr(_hwnd, GWL_WNDPROC, newProc);

                if (old == IntPtr.Zero)
                {
                    LastError = "SetWindowLongPtr 失败，错误码 " + Marshal.GetLastWin32Error();
                    _hook = null;
                    return false;
                }

                _oldProc = old;
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                _hook = null;
                _oldProc = IntPtr.Zero;
                return false;
            }
        }

        /// <summary>卸载挂钩（关功能时调，避免游戏退出时留下悬空指针）。</summary>
        public static void Uninstall()
        {
            if (!Installed) return;
            try
            {
                SetWindowLongPtr(_hwnd, GWL_WNDPROC, _oldProc);
            }
            catch { }
            _oldProc = IntPtr.Zero;
            _hwnd = IntPtr.Zero;
            _hook = null;
        }

        private static IntPtr HookProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    // Unity 收到这两个才知道自己"进后台了"。吞掉它，原生主循环就不会暂停。
                    case WM_ACTIVATEAPP:
                    case WM_ACTIVATE:
                        // wParam 非 0 = 重新获得激活，这种要放行（否则切回来时状态不同步）
                        if (wParam == IntPtr.Zero)
                        {
                            Swallowed++;
                            return IntPtr.Zero;
                        }
                        break;

                    // 同理：别让窗口变成"非活动标题栏"
                    case WM_NCACTIVATE:
                        if (wParam == IntPtr.Zero)
                        {
                            Swallowed++;
                            return (IntPtr)1;
                        }
                        break;

                    case WM_KILLFOCUS:
                        Swallowed++;
                        return IntPtr.Zero;
                }
            }
            catch
            {
                // 回调里绝不能抛：一路穿回原生栈会直接崩游戏
            }

            if (_oldProc == IntPtr.Zero) return IntPtr.Zero;
            return CallWindowProc(_oldProc, hWnd, msg, wParam, lParam);
        }
    }
}
