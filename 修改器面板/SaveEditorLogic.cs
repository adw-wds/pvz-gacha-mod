using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PvzShared;

namespace PvzPanel
{
    /// <summary>
    /// 存档编辑逻辑（不依赖 WPF，可单测）。
    ///
    /// 原则：只替换用户真正编辑过的字段，其余原样保留 → 最大限度避免破坏存档。
    /// 保存后必须按游戏算法重算 save.json.md5，否则游戏会判定「作弊者」。
    /// </summary>
    public static class SaveEditorLogic
    {
        /// <summary>这个字段被篡改会让游戏误判为首次运行，禁止编辑。</summary>
        public const string ProtectedField = "chuizhitongbu";

        public static string LoadText(out string error)
        {
            return LoadTextFrom(SavePaths.SaveFile, out error);
        }

        // ----------------------------------------------------------------
        // 路径无关的入口（支持手机版存档）
        //
        // ★ 安卓版是 0.60.0，与电脑版同版本。已验证它的 global-metadata.dat 里
        //   save.json / save.json.md5 / wujin.json / coin / chuizhitongbu … 等 14 个
        //   存档字符串全部存在 —— 即文件名、字段名、校验算法都和电脑版一致。
        //   所以同一套读写逻辑对手机存档直接成立，差别只有「路径」。
        //
        // ★ 手机版没法注入（libil2cpp.so，没有托管程序集），改存档是那边唯一可行的路。
        //   用户把手机上 save.json + save.json.md5 拷贝出来，改完再拷回去。
        // ----------------------------------------------------------------

        /// <summary>存档配套的校验文件：同目录、同名的 .md5。</summary>
        public static string HashPathOf(string saveFile) { return saveFile + ".md5"; }

        /// <summary>备份目录放在存档旁边 —— 跟着存档走，手机存档拷到哪备份就在哪。</summary>
        public static string BackupDirOf(string saveFile)
        {
            string dir = Path.GetDirectoryName(saveFile);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            return Path.Combine(dir, "backup");
        }

        /// <summary>读任意位置的存档。电脑版存档传 SavePaths.SaveFile。</summary>
        public static string LoadTextFrom(string saveFile, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(saveFile)) { error = "没有指定存档文件"; return null; }
                if (!File.Exists(saveFile)) { error = "找不到存档：" + saveFile; return null; }
                return File.ReadAllText(saveFile);
            }
            catch (Exception ex) { error = "读取存档失败：" + ex.Message; return null; }
        }

        /// <summary>
        /// 粗判这份文件是不是抽卡版存档。
        /// 手机存档是用户自己拷出来的，很容易拷错（拿成别的游戏的、或拿成 wujin.json）。
        /// 拷错了直接往里写会让游戏读存档失败 —— 所以选文件时就先挡一道。
        /// </summary>
        public static bool LooksLikeSaveFile(string path, out string reason)
        {
            reason = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { reason = "文件不存在"; return false; }

                JsonNode root = JsonNode.Parse(File.ReadAllText(path));
                if (!(root is JsonObject obj)) { reason = "根节点不是 JSON 对象"; return false; }

                // 这几个字段是存档特有的；wujin.json / wjdata.json 里没有
                foreach (string key in new[] { "scores", "scores2", "playerNames", ProtectedField })
                    if (obj.ContainsKey(key)) return true;

                reason = "不像抽卡版存档（没有 scores / scores2 / playerNames / " + ProtectedField + " 这些字段）";
                return false;
            }
            catch (Exception ex) { reason = "不是合法的 JSON：" + ex.Message; return false; }
        }

        /// <summary>把顶层字段摊平成「字段名 → 原始 JSON 文本」。数组/对象整体作为一个字符串。</summary>
        public static Dictionary<string, string> FlattenFields(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonNode root = JsonNode.Parse(json);
            if (!(root is JsonObject obj)) throw new FormatException("存档根节点不是 JSON 对象");

            foreach (KeyValuePair<string, JsonNode> kv in obj)
                result[kv.Key] = kv.Value == null ? "null" : kv.Value.ToJsonString();

            return result;
        }

        /// <summary>只替换 edits 里出现的字段（值必须是合法 JSON 片段），其余原样保留。</summary>
        public static string ApplyEdits(string originalJson, IReadOnlyDictionary<string, string> edits)
        {
            JsonNode root = JsonNode.Parse(originalJson);
            if (!(root is JsonObject obj)) throw new FormatException("存档根节点不是 JSON 对象");

            if (edits != null)
            {
                foreach (KeyValuePair<string, string> kv in edits)
                {
                    if (kv.Key == ProtectedField) continue;
                    obj[kv.Key] = JsonNode.Parse(kv.Value);
                }
            }

            return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }

        public static bool SaveWithBackup(string json, out string backupPath, out string error)
        {
            return SaveWithBackupTo(SavePaths.SaveFile, SavePaths.HashFile,
                Path.Combine(SavePaths.EnsureDirExists(), "backup"), json, out backupPath, out error);
        }

        /// <summary>保存到任意位置的存档，校验文件与备份目录都按存档位置推导。</summary>
        public static bool SaveWithBackupToPath(string saveFile, string json,
            out string backupPath, out string error)
        {
            return SaveWithBackupTo(saveFile, HashPathOf(saveFile), BackupDirOf(saveFile),
                json, out backupPath, out error);
        }

        public static bool SaveWithBackupTo(string saveFile, string hashFile, string backupDir, string json,
            out string backupPath, out string error)
        {
            backupPath = null;
            error = null;

            try
            {
                Directory.CreateDirectory(backupDir);
                if (File.Exists(saveFile))
                {
                    backupPath = Path.Combine(backupDir,
                        "save.json.bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
                    File.Copy(saveFile, backupPath, overwrite: true);
                }

                File.WriteAllText(saveFile, json);
                SaveHash.WriteHash(saveFile, hashFile);
                return true;
            }
            catch (Exception ex)
            {
                error = "保存失败：" + ex.Message;

                // 回滚：从刚做的备份恢复，避免留下半截存档
                if (backupPath != null && File.Exists(backupPath))
                {
                    try
                    {
                        File.Copy(backupPath, saveFile, overwrite: true);
                        SaveHash.WriteHash(saveFile, hashFile);
                        error += "（已回滚到备份）";
                    }
                    catch (Exception rollbackEx) { error += "（回滚也失败：" + rollbackEx.Message + "）"; }
                }
                return false;
            }
        }

        public static string[] RestoreBackups()
        {
            return RestoreBackupsFrom(Path.Combine(SavePaths.Root, "backup"));
        }

        /// <summary>列出某个备份目录里的备份，最新的在前。</summary>
        public static string[] RestoreBackupsFrom(string backupDir)
        {
            try
            {
                if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir)) return new string[0];
                string[] files = Directory.GetFiles(backupDir, "save.json.bak_*");
                Array.Sort(files, StringComparer.Ordinal);
                Array.Reverse(files);
                return files;
            }
            catch { return new string[0]; }
        }

        /// <summary>从备份还原到任意位置的存档。</summary>
        public static bool RestoreToPath(string saveFile, string backupPath, out string error)
        {
            return RestoreTo(saveFile, HashPathOf(saveFile), backupPath, out error);
        }

        public static bool Restore(string backupPath, out string error)
        {
            return RestoreTo(SavePaths.SaveFile, SavePaths.HashFile, backupPath, out error);
        }

        public static bool RestoreTo(string saveFile, string hashFile, string backupPath, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(backupPath)) { error = "备份不存在：" + backupPath; return false; }
                File.Copy(backupPath, saveFile, overwrite: true);
                SaveHash.WriteHash(saveFile, hashFile);
                return true;
            }
            catch (Exception ex) { error = "还原失败：" + ex.Message; return false; }
        }

        /// <summary>无存档时按游戏 GameStart.LoadArrayData 的默认值生成一份（数组字段全部建全，避免读成 null）。</summary>
        public static string SynthesizeDefaultSaveJson()
        {
            var obj = new JsonObject
            {
                ["scores"] = new JsonArray(0, 1, 5),
                ["shangdian"] = new JsonArray(),
                ["shangdianYishou"] = new JsonArray(),
                ["shangdianyigong"] = new JsonArray(),
                ["scores2"] = new JsonArray(),
                ["pospos"] = new JsonArray(),
                ["dates"] = new JsonArray(),
                ["playerNames"] = new JsonArray("MiaoDouziHaToTeMoIIDesu"),
                ["music"] = 0.3,
                ["sf"] = 0.3,
                ["CustomZombieList"] = new JsonArray(0, 1),
                ["CustomSpawnNumber"] = new JsonArray(1, 2, 3, 4, 8, 10),
                ["Difficulty"] = 0,
                ["Maoxian"] = 0,
                ["MaoxianIFA"] = 0,
                ["MaoxianSnow"] = 0,
                ["heng"] = 1280,
                ["shu"] = 720,
                ["quanping"] = false,
                ["chuizhitongbu"] = true,
                ["pingban"] = false,
                ["wujinceng"] = 0,
                ["wujinceng2"] = 0,
                ["wujinceng3"] = 0,
                ["wujinceng2Last"] = 0,
                ["kcZheDang"] = false,
                ["hpShow"] = false,
                ["treeVanishOff"] = false,
                ["coin"] = 0,
                ["chushisun"] = 0,
                ["maoliang"] = 0,
                ["coinYingtao"] = 0,
                ["sunPokeCishu"] = 0,
                ["zmPokeCishu"] = 0,
                ["tianjianglihe"] = 0,
                ["touzi"] = 0,
                ["touzi2"] = 0,
                ["liekabao"] = false,
                ["ptkabao"] = false,
                ["xykabao"] = false,
                ["sskabao"] = false,
                ["canbaohusan"] = false
            };
            return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
    }
}
