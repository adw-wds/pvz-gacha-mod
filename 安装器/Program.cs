using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PvzInstall
{
    /// <summary>
    /// 安装 / 卸载 / 校验。
    ///
    /// 本修改器**不改游戏原始程序集**（Assembly-CSharp.dll 保持原样），只做两件事：
    ///   1. 把 PvzGachaMod.dll 放进 抽卡版PVZ_Data\Managed\
    ///   2. 在 Unity 的两个启动清单里登记它（让 Unity 启动时预加载并调用 ModBootstrap.Init）
    ///
    /// 两个清单首次改动前会备份成 .orig，卸载时从 .orig 还原 → 完全可逆。
    /// </summary>
    internal static class Program
    {
        private const string ModDllName = "PvzGachaMod.dll";
        private const string ModAsmName = "PvzGachaMod";
        private static readonly string DefaultGameDir =
            Environment.GetEnvironmentVariable("GACHA_GAME_DIR") ?? "";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            string gameDir = args.Length > 1 ? args[1] : DefaultGameDir;
            string repoRoot = FindRepoRoot();

            if (repoRoot == null)
            {
                Console.WriteLine("找不到 MOD 目录。请把安装器放在修改器目录的 源码\\安装器\\bin\\... 下运行。");
                return 2;
            }

            string dataDir = Path.Combine(gameDir, "抽卡版PVZ_Data");
            if (!File.Exists(Path.Combine(gameDir, "抽卡版PVZ.exe")))
            {
                Console.WriteLine("目标目录里找不到 抽卡版PVZ.exe：" + gameDir);
                return 2;
            }

            switch (mode)
            {
                case "install":   return Install(repoRoot, gameDir, dataDir);
                case "uninstall": return Uninstall(gameDir, dataDir);
                case "verify":    return Verify(gameDir, dataDir);
                default:
                    Console.WriteLine("用法：安装器 install|uninstall|verify [游戏目录]");
                    Console.WriteLine("默认游戏目录：" + DefaultGameDir);
                    return 0;
            }
        }

        // ---------------------------------------------------------------- 安装

        private static int Install(string repoRoot, string gameDir, string dataDir)
        {
            string srcDll = Path.Combine(repoRoot, "MOD", ModDllName);
            if (!File.Exists(srcDll))
            {
                Console.WriteLine("找不到 " + srcDll + "，请先运行 源码\\构建.ps1 构建 mod。");
                return 3;
            }

            // 1) 复制 mod DLL
            string managedDir = Path.Combine(dataDir, "Managed");
            Directory.CreateDirectory(managedDir);
            string dstDll = Path.Combine(managedDir, ModDllName);
            File.Copy(srcDll, dstDll, overwrite: true);
            Console.WriteLine("已放置 " + Rel(gameDir, dstDll) + "（" + new FileInfo(dstDll).Length + " 字节）");

            // 2) 登记到 Unity 启动清单
            if (!RegisterStartup(dataDir)) return 4;

            // 3) 注入抽卡拦截（抽卡免费 / 必出稀有度 / 货架自定义 必须改游戏程序集）
            string acs = Path.Combine(managedDir, "Assembly-CSharp.dll");
            if (File.Exists(acs))
            {
                Console.WriteLine();
                int rc = PvzInjector.Program.Run("patch", acs);
                if (rc != 0)
                {
                    Console.WriteLine("警告：注入未全部成功（以上失败项对应的功能不可用）。");
                    Console.WriteLine("      其余功能（战斗 / 货币 / 存档编辑）不受影响。");
                }
            }
            else
            {
                Console.WriteLine("警告：没找到 Assembly-CSharp.dll，跳过注入（抽卡类功能将不可用）");
            }

            Console.WriteLine();
            Console.WriteLine("安装完成。");
            Console.WriteLine("  · 本修改器不占用任何键盘按键，也不在游戏画面里画任何东西");
            Console.WriteLine("  · 改功能、调数值都在面板里点选：工具\\面板\\抽卡版修改器.exe");
            Console.WriteLine("  · 功能没反应时看面板的「诊断」页，它会告诉你断在哪一环");
            return 0;
        }

        private static bool RegisterStartup(string dataDir)
        {
            string saPath = Path.Combine(dataDir, "ScriptingAssemblies.json");
            string riPath = Path.Combine(dataDir, "RuntimeInitializeOnLoads.json");

            if (!File.Exists(saPath) || !File.Exists(riPath))
            {
                Console.WriteLine("找不到 Unity 启动清单，游戏版本可能不符：");
                Console.WriteLine("  " + saPath);
                Console.WriteLine("  " + riPath);
                return false;
            }

            Backup(saPath);
            Backup(riPath);

            // ---- ScriptingAssemblies.json：names 里加一项 ----
            JsonNode sa = JsonNode.Parse(File.ReadAllText(saPath));
            var names = new List<string>();
            if (sa["names"] is JsonArray arr)
            {
                foreach (JsonNode n in arr) names.Add(n.GetValue<string>());
            }
            if (!names.Contains(ModDllName))
            {
                names.Add(ModDllName);
                var newArr = new JsonArray();
                foreach (string n in names) newArr.Add(n);
                sa["names"] = newArr;
                WriteJson(saPath, sa);
                Console.WriteLine("ScriptingAssemblies.json：已登记 " + ModDllName + "（共 " + names.Count + " 项）");
            }
            else
            {
                Console.WriteLine("ScriptingAssemblies.json：已存在 " + ModDllName + "，跳过");
            }

            // ---- RuntimeInitializeOnLoads.json：root 里加我们的入口 ----
            JsonNode ri = JsonNode.Parse(File.ReadAllText(riPath));
            bool found = false;
            if (ri["root"] is JsonArray root)
            {
                foreach (JsonNode e in root)
                {
                    if (e?["assemblyName"]?.GetValue<string>() == ModAsmName) { found = true; break; }
                }

                if (!found)
                {
                    root.Add(new JsonObject
                    {
                        ["assemblyName"] = ModAsmName,
                        ["nameSpace"] = "PvzGachaMod",
                        ["className"] = "ModBootstrap",
                        ["methodName"] = "Init",
                        ["loadTypes"] = 2,             // 2 = AfterSceneLoad
                        ["isUnityClass"] = false
                    });
                    WriteJson(riPath, ri);
                    Console.WriteLine("RuntimeInitializeOnLoads.json：已登记启动入口（共 " + root.Count + " 项）");
                }
                else
                {
                    Console.WriteLine("RuntimeInitializeOnLoads.json：已存在启动入口，跳过");
                }
            }

            return true;
        }

        // ---------------------------------------------------------------- 卸载

        private static int Uninstall(string gameDir, string dataDir)
        {
            int restored = 0, deleted = 0;

            foreach (string name in new[] { "ScriptingAssemblies.json", "RuntimeInitializeOnLoads.json" })
            {
                string path = Path.Combine(dataDir, name);
                string backup = path + ".orig";
                if (File.Exists(backup))
                {
                    File.Copy(backup, path, overwrite: true);
                    File.Delete(backup);
                    Console.WriteLine("已还原 " + name + "（并删除备份）");
                    restored++;
                }
            }

            // 万一 .orig 丢了，至少把登记项删掉
            if (restored == 0)
            {
                RemoveStartupEntries(dataDir);
                Console.WriteLine("没有 .orig 备份，改为直接删除登记项");
            }

            string dll = Path.Combine(dataDir, "Managed", ModDllName);
            if (File.Exists(dll)) { File.Delete(dll); deleted++; Console.WriteLine("已删除 Managed\\" + ModDllName); }

            // Assembly-CSharp 若被注入过（抽卡拦截会用到），一并还原
            string acs = Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll");
            string acsOrig = acs + ".orig";
            if (File.Exists(acsOrig))
            {
                File.Copy(acsOrig, acs, overwrite: true);
                File.Delete(acsOrig);
                Console.WriteLine("已还原 Assembly-CSharp.dll（并删除备份）");
                restored++;
            }

            Console.WriteLine();
            Console.WriteLine("卸载完成：还原 " + restored + " 个文件，删除 " + deleted + " 个文件。");
            Console.WriteLine("游戏原始文件已恢复；建议启动一次游戏确认正常。");
            return 0;
        }

        private static void RemoveStartupEntries(string dataDir)
        {
            try
            {
                string saPath = Path.Combine(dataDir, "ScriptingAssemblies.json");
                if (File.Exists(saPath))
                {
                    JsonNode sa = JsonNode.Parse(File.ReadAllText(saPath));
                    if (sa["names"] is JsonArray arr)
                    {
                        var kept = new JsonArray();
                        foreach (JsonNode n in arr)
                        {
                            string v = n.GetValue<string>();
                            if (v != ModDllName) kept.Add(v);
                        }
                        sa["names"] = kept;
                        WriteJson(saPath, sa);
                    }
                }

                string riPath = Path.Combine(dataDir, "RuntimeInitializeOnLoads.json");
                if (File.Exists(riPath))
                {
                    JsonNode ri = JsonNode.Parse(File.ReadAllText(riPath));
                    if (ri["root"] is JsonArray root)
                    {
                        var kept = new JsonArray();
                        foreach (JsonNode e in root)
                        {
                            if (e?["assemblyName"]?.GetValue<string>() != ModAsmName) kept.Add(e.DeepClone());
                        }
                        ri["root"] = kept;
                        WriteJson(riPath, ri);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("清理登记项失败（可手动删除）：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 校验

        private static int Verify(string gameDir, string dataDir)
        {
            int bad = 0;

            string dll = Path.Combine(dataDir, "Managed", ModDllName);
            bool dllOk = File.Exists(dll);
            Console.WriteLine((dllOk ? "✓" : "✗") + " Managed\\" + ModDllName);
            if (!dllOk) bad++;

            string saPath = Path.Combine(dataDir, "ScriptingAssemblies.json");
            bool saOk = false;
            try
            {
                JsonNode sa = JsonNode.Parse(File.ReadAllText(saPath));
                if (sa["names"] is JsonArray arr)
                {
                    foreach (JsonNode n in arr)
                        if (n.GetValue<string>() == ModDllName) { saOk = true; break; }
                }
            }
            catch { }
            Console.WriteLine((saOk ? "✓" : "✗") + " ScriptingAssemblies.json 已登记");
            if (!saOk) bad++;

            string riPath = Path.Combine(dataDir, "RuntimeInitializeOnLoads.json");
            bool riOk = false;
            try
            {
                JsonNode ri = JsonNode.Parse(File.ReadAllText(riPath));
                if (ri["root"] is JsonArray root)
                {
                    foreach (JsonNode e in root)
                        if (e?["assemblyName"]?.GetValue<string>() == ModAsmName) { riOk = true; break; }
                }
            }
            catch { }
            Console.WriteLine((riOk ? "✓" : "✗") + " RuntimeInitializeOnLoads.json 已登记");
            if (!riOk) bad++;

            // 抽卡拦截（改的是游戏自己的程序集，单独校验）
            string acs = Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll");
            int inj = PvzInjector.Program.Run("verify", acs);
            if (inj != 0)
            {
                bad++;
                Console.WriteLine("  → 抽卡类功能（抽卡免费 / 必出稀有度 / 货架自定义）不可用，重新运行 install 可补齐");
            }

            Console.WriteLine();
            Console.WriteLine(bad == 0 ? "校验通过：修改器已正确安装。" : "校验未通过：" + bad + " 项缺失，请重新运行 install。");
            return bad == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 小工具

        private static void Backup(string path)
        {
            string backup = path + ".orig";
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
                Console.WriteLine("已备份 " + Path.GetFileName(path) + " → .orig");
            }
        }

        private static void WriteJson(string path, JsonNode node)
        {
            // 必须写无 BOM 的 UTF-8：Unity 的 JSON 解析器对 BOM 不可靠。
            // 注意：不要传自定义的 JsonSerializerOptions（.NET 8 下 JsonNode 会因缺 TypeInfoResolver 抛异常），
            // 无参的 ToJsonString() 本来就是紧凑格式。
            File.WriteAllText(path, node.ToJsonString());
        }

        private static string Rel(string root, string path)
        {
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart('\\', '/')
                : path;
        }

        /// <summary>从 exe 所在目录向上找到含 MOD 目录的修改器根。</summary>
        private static string FindRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "MOD"))) return dir;
                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }
    }
}
