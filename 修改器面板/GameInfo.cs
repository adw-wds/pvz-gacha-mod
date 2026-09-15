using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PvzPanel
{
    /// <summary>游戏相关固定路径、启动与窗口焦点操作。</summary>
    public static class GameInfo
    {
        public static readonly string DefaultGameDir =
            Environment.GetEnvironmentVariable("GACHA_GAME_DIR") ?? "";

        /// <summary>游戏进程名（不含 .exe）。</summary>
        public const string ProcessName = "抽卡版PVZ";

        public static string GameDir = DefaultGameDir;
        public static string ExePath { get { return Path.Combine(GameDir, "抽卡版PVZ.exe"); } }

        /// <summary>Assembly-CSharp.dll 路径（安装器会改它，这里用于显示与校验）。</summary>
        public static string AssemblyCSharp { get { return Path.Combine(GameDir, "抽卡版PVZ_Data", "Managed", "Assembly-CSharp.dll"); } }

        public static bool Exists { get { return File.Exists(ExePath); } }

        public static bool Launch(out string error)
        {
            error = null;
            try
            {
                if (!Exists) { error = "找不到游戏：" + ExePath; return false; }
                Process.Start(new ProcessStartInfo
                {
                    FileName = ExePath,
                    WorkingDirectory = GameDir,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                error = "启动失败：" + ex.Message;
                return false;
            }
        }

        public static void OpenFolder()
        {
            try { Process.Start(new ProcessStartInfo { FileName = GameDir, UseShellExecute = true }); }
            catch { }
        }

        // ---------------------------------------------------------------- 进程 / 窗口

        private static bool _running;
        private static DateTime _runningAt = DateTime.MinValue;

        /// <summary>
        /// 游戏进程是否在。缓存 1 秒，因为面板每秒都会问一次。
        /// 注意：本作构建时未勾选 Run In Background（且 Application.runInBackground 被裁剪，无法从托管侧打开），
        /// 所以**游戏一旦失去焦点，它的主循环就停住、Update 不再被调用**，状态文件也不更新。
        /// 进程存活与“主循环在响应”必须分开判断，否则用户切到面板就会看到假的“未连接”。
        /// </summary>
        public static bool IsRunning()
        {
            if ((DateTime.Now - _runningAt).TotalSeconds < 1.0) return _running;
            _runningAt = DateTime.Now;
            try { _running = Process.GetProcessesByName(ProcessName).Length > 0; }
            catch { _running = false; }
            return _running;
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// 把游戏窗口切到前台。游戏失焦时会暂停主循环，用这个可以让它立刻醒来、
        /// 把面板刚发的命令在本帧就处理掉。
        /// </summary>
        public static bool FocusGame()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(ProcessName);
                if (ps.Length == 0) return false;

                IntPtr h = ps[0].MainWindowHandle;
                if (h == IntPtr.Zero) return false;

                ShowWindow(h, 9);          // SW_RESTORE：被最小化时先还原
                return SetForegroundWindow(h);
            }
            catch { return false; }
        }
    }
}
