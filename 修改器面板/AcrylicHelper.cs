using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PvzPanel
{
    /// <summary>
    /// 无边框窗口的「毛玻璃 + 圆角」。
    ///
    /// 做法与用户自己那套杂交版修改器一致：
    ///   · Win11 22H2+ → DwmSetWindowAttribute(SYSTEMBACKDROP_TYPE = TransientWindow) 就是高斯模糊
    ///   · Win10 回退 → SetWindowCompositionAttribute(ACCENT_ENABLE_BLURBEHIND)
    ///   · 圆角交给 DWM(WINDOW_CORNER_PREFERENCE)，比自己 Clip 更贴合系统
    ///
    /// 关键约束：窗口必须 WindowStyle=None，并且**不能** AllowsTransparency=true
    /// —— 一旦开了透明窗口，DWM 的这些效果全部失效，只剩半透明（看起来发灰发脏）。
    /// 窗口自身背景用带 alpha 的深色画刷叠在模糊层上，就是"深色毛玻璃"。
    /// </summary>
    public static class AcrylicHelper
    {
        // ---- Win11 22H2+ ----
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;
        private const int DWMSBT_TRANSIENTWINDOW = 2;   // 高斯模糊（无着色）

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        /// <summary>给窗口套上毛玻璃 + 圆角 + 深色标题栏。重复调用无副作用。</summary>
        public static void Apply(Window window)        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();

                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

                int backdrop = DWMSBT_TRANSIENTWINDOW;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
                    FallbackBlurBehind(hwnd);      // Win10 / 旧版 Win11 上 38 号属性不存在
            }
            catch
            {
                // 效果是锦上添花，失败也不能影响程序启动
            }
        }

        private static void FallbackBlurBehind(IntPtr hwnd)
        {
            var policy = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_BLURBEHIND,
                AccentFlags = 2,
                GradientColor = 0
            };

            int size = Marshal.SizeOf(policy);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        // ================================================================ 后台运行

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        /// <summary>
        /// 后台运行模式：给窗口打上 WS_EX_NOACTIVATE，**点面板不会抢走游戏的焦点**。
        ///
        /// 为什么需要它：本作没开 Run In Background，游戏一失去焦点就停主循环，
        /// 面板上的改动要等切回游戏才生效。开了这个模式后，游戏一直保持焦点、
        /// 持续跑，点面板开关就能立刻看到效果。
        ///
        /// 代价：窗口拿不到键盘焦点，所以文本框输不了字 —— 需要输入时关掉它。
        /// </summary>
        public static void SetNoActivate(Window window, bool enabled)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
                int style = GetWindowLong(hwnd, GWL_EXSTYLE);

                int next = enabled ? (style | WS_EX_NOACTIVATE) : (style & ~WS_EX_NOACTIVATE);
                if (next != style) SetWindowLong(hwnd, GWL_EXSTYLE, next);
            }
            catch
            {
                // 失败也不影响正常使用，只是失去后台运行这个便利
            }
        }
    }
}
