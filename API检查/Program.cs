using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

namespace PvzApiCheck
{
    /// <summary>
    /// 检查「某个 API 在这款被裁剪过的游戏里到底还在不在」。
    ///
    /// 背景：本游戏的 mscorlib/System.dll/System.Core.dll 被 Unity 链接器裁剪过。
    /// 出现过一个诡异现象：`File.AppendAllText(string,string,Encoding)` 编译能过、
    /// 运行期却 MissingMethodException —— 所以选 API 之前一律先查一遍，别猜。
    ///
    /// 用法：
    ///   API检查 Type System.IO.File                      → 列出该类型所有方法与签名
    ///   API检查 Member System.IO.File AppendAllText      → 只列同名方法的所有重载
    ///   API检查 Has System.IO.File AppendAllText         → 只回答 有/无
    /// </summary>
    internal static class Program
    {
        private static readonly Dictionary<string, ModuleDefinition> Modules =
            new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>编译期特性，运行期根本不会被解析，不算缺失。</summary>
        private static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Runtime.Versioning.TargetFrameworkAttribute",
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
            "System.Runtime.CompilerServices.NullableAttribute",
            "System.Runtime.CompilerServices.NullableContextAttribute",
            "System.Runtime.CompilerServices.RefSafetyRulesAttribute",
            "System.Runtime.CompilerServices.ExtensionAttribute",
        };

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length < 2)
            {
                Console.WriteLine("用法：");
                Console.WriteLine("  API检查 Type|Member|Has <类型全名> [成员名] [程序集目录]");
                Console.WriteLine("  API检查 Verify <我们的.dll> [游戏程序集目录]");
                return 2;
            }

            if (args[0].Equals("Verify", StringComparison.OrdinalIgnoreCase))
            {
                string target = args[1];
                string gameManaged = args.Length > 2 ? args[2] : DefaultDir();
                return Verify(target, gameManaged);
            }

            string mode = args[0];
            string typeName = args[1];
            string memberName = args.Length > 2 && !Directory.Exists(args[2]) ? args[2] : null;
            string dir = args.Length > 3 ? args[3] : (args.Length > 2 && Directory.Exists(args[2]) ? args[2] : DefaultDir());

            if (!Directory.Exists(dir))
            {
                Console.WriteLine("找不到程序集目录：" + dir);
                return 2;
            }

            LoadAll(dir);
            Console.WriteLine("程序集目录：" + dir + "（已载入 " + Modules.Count + " 个）");

            TypeDefinition type = FindType(typeName);
            if (type == null)
            {
                Console.WriteLine("✗ 类型不存在：" + typeName);
                return 1;
            }

            var methods = new List<MethodDefinition>();
            foreach (MethodDefinition m in type.Methods) methods.Add(m);

            if (memberName == null)
            {
                Console.WriteLine("✓ 类型存在：" + type.FullName + "（方法 " + methods.Count + " 个）");
                if (mode == "Type")
                {
                    foreach (MethodDefinition m in methods.OrderBy(m => m.Name))
                        Console.WriteLine("    " + Describe(m));
                }
                return 0;
            }

            List<MethodDefinition> hits = methods.Where(m => m.Name == memberName).ToList();

            if (mode == "Has")
            {
                Console.WriteLine((hits.Count > 0 ? "✓ 有" : "✗ 无") + "：" + typeName + "::" + memberName +
                                  "（重载 " + hits.Count + " 个）");
                return hits.Count > 0 ? 0 : 1;
            }

            if (hits.Count == 0)
            {
                Console.WriteLine("✗ 找不到成员：" + typeName + "::" + memberName);
                return 1;
            }

            Console.WriteLine("✓ " + typeName + "::" + memberName + " 共有 " + hits.Count + " 个重载：");
            foreach (MethodDefinition m in hits) Console.WriteLine("    " + Describe(m));
            return 0;
        }

        /// <summary>
        /// 核心防线：把「我们 DLL 里用到的每一个外部成员」拿去游戏程序集里核对。
        /// 编译期能过不代表运行期存在（游戏 mscorlib 被裁剪过，且本机编译用的框架程序集
        /// 未必就是游戏自带的那份），所以每次构建后都要跑一遍这个校验。
        /// </summary>
        private static int Verify(string dllPath, string gameManaged)
        {
            if (!File.Exists(dllPath)) { Console.WriteLine("找不到 " + dllPath); return 2; }
            if (!Directory.Exists(gameManaged)) { Console.WriteLine("找不到游戏程序集目录 " + gameManaged); return 2; }

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(gameManaged);
            string dllDir = Path.GetDirectoryName(Path.GetFullPath(dllPath));
            if (!string.IsNullOrEmpty(dllDir)) resolver.AddSearchDirectory(dllDir);

            AssemblyDefinition asm;
            try
            {
                var ms = new MemoryStream(File.ReadAllBytes(dllPath));
                asm = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Immediate,
                    ReadSymbols = false,
                    AssemblyResolver = resolver
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("读取失败：" + ex.Message);
                return 2;
            }

            Console.WriteLine("校验：" + Path.GetFileName(dllPath));
            Console.WriteLine("对抗：游戏程序集目录 " + gameManaged);
            Console.WriteLine();

            var missing = new List<string>();
            var okTypes = new HashSet<string>(StringComparer.Ordinal);
            var badTypes = new HashSet<string>(StringComparer.Ordinal);

            foreach (TypeReference tr in asm.MainModule.GetTypeReferences())
            {
                string name = tr.FullName;
                if (name.StartsWith("PvzGachaMod", StringComparison.Ordinal)) continue;
                if (Ignored.Contains(name)) continue;
                if (okTypes.Contains(name) || badTypes.Contains(name)) continue;

                try
                {
                    if (tr.Resolve() != null) okTypes.Add(name);
                    else badTypes.Add(name);
                }
                catch { badTypes.Add(name); }
            }

            foreach (MemberReference mr in asm.MainModule.GetMemberReferences())
            {
                string decl = mr.DeclaringType?.FullName ?? "?";
                if (decl.StartsWith("PvzGachaMod", StringComparison.Ordinal)) continue;
                if (Ignored.Contains(decl)) continue;        // 编译期特性的构造器同样忽略
                if (badTypes.Contains(decl)) continue;       // 类型都不在，成员必然不在，不重复报

                bool found;
                try { found = mr.Resolve() != null; }
                catch { found = false; }

                if (!found)
                {
                    string p = mr is MethodReference m
                        ? string.Join(",", m.Parameters.Select(x => x.ParameterType.Name))
                        : "";
                    missing.Add(decl + "::" + mr.Name + (p.Length > 0 ? "(" + p + ")" : ""));
                }
            }

            if (badTypes.Count > 0)
            {
                Console.WriteLine("✗ 缺失类型 " + badTypes.Count + " 个：");
                foreach (string t in badTypes.OrderBy(x => x)) Console.WriteLine("    " + t);
                Console.WriteLine();
            }

            if (missing.Count > 0)
            {
                Console.WriteLine("✗ 缺失成员 " + missing.Count + " 个：");
                foreach (string m in missing) Console.WriteLine("    " + m);
                return 1;
            }

            Console.WriteLine("✓ 全部成员引用都能在游戏程序集里解析（类型 " + okTypes.Count + " 个）");
            return 0;
        }

        private static string Describe(MethodDefinition m)        {
            var sb = new StringBuilder();
            sb.Append(m.ReturnType.Name).Append(' ').Append(m.Name).Append('(');
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(m.Parameters[i].ParameterType.FullName);
            }
            sb.Append(')');
            if (m.IsStatic) sb.Append("  [static]");
            return sb.ToString();
        }

        private static TypeDefinition FindType(string fullName)
        {
            foreach (ModuleDefinition mod in Modules.Values)
            {
                TypeDefinition t = mod.GetType(fullName);
                if (t != null) return t;

                foreach (TypeDefinition candidate in mod.Types)
                {
                    if (candidate.FullName == fullName) return candidate;
                }
            }
            return null;
        }

        private static void LoadAll(string dir)
        {
            string[] wanted =
            {
                "mscorlib.dll", "System.dll", "System.Core.dll", "netstandard.dll",
                // Unity 模块也要能查：这个游戏的引擎程序集同样被裁剪过
                "UnityEngine.CoreModule.dll", "UnityEngine.IMGUIModule.dll",
                "UnityEngine.JSONSerializeModule.dll", "UnityEngine.InputLegacyModule.dll",
            };

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(dir);

            foreach (string name in wanted)
            {
                string path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    var ms = new MemoryStream(File.ReadAllBytes(path));
                    Modules[name] = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters
                    {
                        ReadingMode = ReadingMode.Immediate,
                        ReadSymbols = false,
                        AssemblyResolver = resolver
                    }).MainModule;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("载入 " + name + " 失败：" + ex.Message);
                }
            }
        }

        private static string DefaultDir()
        {
            var env = Environment.GetEnvironmentVariable("GACHA_GAME_DIR");
            return string.IsNullOrEmpty(env) ? "" : System.IO.Path.Combine(env, "抽卡版PVZ_Data", "Managed");
        }
    }
}
