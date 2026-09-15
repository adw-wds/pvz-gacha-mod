using System;
using System.IO;

namespace PvzShared
{
    /// <summary>
    /// 文件命令通道（游戏内没有 TcpListener，所以用文件代替 socket）。
    ///
    /// 面板 → mod：**追加**一行一条 JSON 到「修改器命令.json」，mod 一次性全部消费掉。
    /// mod → 面板：写「修改器状态.json」，面板轮询读取。
    ///
    /// 为什么是「追加多行」而不是「覆盖单条」：
    /// 游戏失焦时主循环停住，命令会滞留很久。若面板只覆盖写一个文件，
    /// 用户连点 5 个开关就只会活下最后一条，其余 4 条形同丢包
    /// （实测症状：界面上一堆「待生效」永远不消，功能“根本没用”）。
    ///
    /// 只用 File.Exists / ReadAllText / WriteAllText / Delete 的两参或无参形式 ——
    /// 这些是游戏自己在用的，必然存在（AppendAllText 两个重载都被裁掉了，
    /// 所以“追加”由面板侧（.NET 8）负责，mod 只负责读+清空）。
    /// </summary>
    public static class ChannelFiles
    {
        /// <summary>读命令文件里的所有命令行；没有则返回空数组。</summary>
        public static string[] ReadCommands()
        {
            try
            {
                string path = SavePaths.CommandFile;
                if (!File.Exists(path)) return new string[0];
                string text = File.ReadAllText(path);
                if (string.IsNullOrEmpty(text)) return new string[0];

                string[] raw = text.Split('\n');
                var lines = new System.Collections.Generic.List<string>(raw.Length);
                for (int i = 0; i < raw.Length; i++)
                {
                    string line = raw[i].Trim();
                    if (line.Length > 0) lines.Add(line);
                }
                return lines.ToArray();
            }
            catch { return new string[0]; }
        }

        /// <summary>读单条命令（兼容旧版单条格式；没用到就返回 null）。</summary>
        public static string ReadCommand()
        {
            string[] lines = ReadCommands();
            return lines.Length > 0 ? lines[lines.Length - 1] : null;
        }

        /// <summary>消费掉命令文件（处理完必须清，避免重复执行）。</summary>
        public static void ClearCommand()
        {
            try
            {
                string path = SavePaths.CommandFile;
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public static void WriteState(string json)
        {
            try
            {
                SavePaths.EnsureDirExists();
                File.WriteAllText(SavePaths.StateFile, json ?? "");
            }
            catch { }
        }

        public static string ReadState()
        {
            try
            {
                string path = SavePaths.StateFile;
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch { return null; }
        }

        /// <summary>面板侧：投递一条命令。</summary>
        public static bool WriteCommand(string json)
        {
            try
            {
                SavePaths.EnsureDirExists();
                File.WriteAllText(SavePaths.CommandFile, json ?? "");
                return true;
            }
            catch { return false; }
        }

        public static bool StateExists()
        {
            try { return File.Exists(SavePaths.StateFile); }
            catch { return false; }
        }
    }
}
