using System;
using System.IO;

namespace PvzShared
{
    /// <summary>存档与命令通道的文件位置。全部基于 Application.persistentDataPath 的同级目录。</summary>
    public static class SavePaths
    {
        public const string CompanyName = "MiaoDouzi";
        public const string ProductName = "抽卡版PVZ";

        /// <summary>
        /// %USERPROFILE%\AppData\LocalLow\MiaoDouzi\抽卡版PVZ
        /// 注意：LocalLow 是 Local 的【兄弟目录】（同在 AppData 下），不是 Local 的子目录。
        /// 游戏内更稳的做法是直接设 OverrideDir = Application.persistentDataPath（推荐）。
        /// </summary>
        public static string Dir
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appData = Path.GetDirectoryName(local);
                if (string.IsNullOrEmpty(appData)) appData = local;      // 极端情况下的兵底
                return Path.Combine(appData, "LocalLow", CompanyName, ProductName);
            }
        }

        /// <summary>由 mod 注入的真实目录（游戏内用 Application.persistentDataPath 更可靠）。</summary>
        public static string OverrideDir;

        public static string Root { get { return string.IsNullOrEmpty(OverrideDir) ? Dir : OverrideDir; } }

        public static string SaveFile { get { return Path.Combine(Root, "save.json"); } }
        public static string HashFile { get { return Path.Combine(Root, "save.json.md5"); } }
        public static string WujinFile { get { return Path.Combine(Root, "wujin.json"); } }
        public static string Wujin2File { get { return Path.Combine(Root, "wujin2.json"); } }
        public static string GaonanFile { get { return Path.Combine(Root, "wjdata.json"); } }

        /// <summary>面板写、mod 读并消费的命令文件。</summary>
        public static string CommandFile { get { return Path.Combine(Root, "修改器命令.json"); } }

        /// <summary>mod 写、面板读的状态文件。</summary>
        public static string StateFile { get { return Path.Combine(Root, "修改器状态.json"); } }

        public static string ModLogFile { get { return Path.Combine(Root, "PvzGachaMod.log"); } }

        /// <summary>
        /// mod 自己的设置（开关/数值）。
        /// 必须持久化：否则关一次游戏所有开关都回默认，用户会以为“改了不生效”。
        /// </summary>
        public static string SettingsFile { get { return Path.Combine(Root, "修改器设置.json"); } }

        public static string EnsureDirExists()
        {
            if (!Directory.Exists(Root)) Directory.CreateDirectory(Root);
            return Root;
        }
    }
}
