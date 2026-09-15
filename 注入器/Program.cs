using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PvzInjector
{
    /// <summary>
    /// 编译期注入器：把对 <c>PvzGachaMod.Hooks.*</c> 的调用直接插进游戏的 Assembly-CSharp.dll。
    ///
    /// 为什么需要它：本游戏的 mscorlib 被 Unity 裁剪过（连 Assembly.LoadFile / Module.GetPEKind 都没有），
    /// 任何依赖运行时反射的 hook 框架（BepInEx/Harmony）都装不起来 —— 只能改 IL。
    ///
    /// 只给「必须拦截」的三项功能用（抽卡免费 / 必出稀有度 / 货架自定义）；
    /// 其余每帧类作弊都走 mod 自己的 Update，不需要改游戏文件。
    ///
    /// 安全：首次 patch 前备份 Assembly-CSharp.dll.orig（已存在则不覆盖），幂等，restore 可还原。
    /// </summary>
    public static class Program
    {
        private const string HookNamespace = "PvzGachaMod";
        private const string HookTypeName = "Hooks";
        private const string ModAssemblyName = "PvzGachaMod";

        /// <summary>一条注入规则：在 目标类型.方法 里插入对 Hooks.钩子(参数...) 的调用。</summary>
        private sealed class Rule
        {
            public string TypeName;
            public string MethodName;
            public int ParamCount;
            public string HookMethod;
            public bool AtStart;              // true=方法开头；false=每个 ret 之前
            public string[] Args;             // 实参说明：this / refint:N / intarray:N
            public string Note;

            public Rule(string type, string method, int paramCount, string hook,
                        bool atStart, string[] args, string note)
            {
                TypeName = type; MethodName = method; ParamCount = paramCount;
                HookMethod = hook; AtStart = atStart; Args = args ?? new string[0]; Note = note;
            }
        }

        private static readonly Rule[] Rules =
        {
            // 抽卡免费：买之前把当前货架商品（仅卡包）的价格临时置 0，买完还原。
            // 一举通过 `cost > coin` 门槛、并让 `coin -= cost` 实际扣 0。
            new Rule("shopBuyBottom", "OnMouseDown", 0, "BuyPrefix",  true,  null, "抽卡免费-前置"),
            new Rule("shopBuyBottom", "OnMouseDown", 0, "BuyPostfix", false, null, "抽卡免费-后置"),

            // 必出稀有度：就地改档位参数，原方法自然返回目标档的池（不必碰返回值）
            new Rule("Wins", "getXiyouGroup", 1, "OverrideTier", true, new[] { "refint:0" }, "必出稀有度"),

            // 货架自定义：就地改 6 格商品（数组同长度，可原地改元素）
            new Rule("shangdian", "buhuo", 2, "OverrideShelfInPlace", true, new[] { "intarray:1" }, "货架自定义"),

            // 商店刷新免费：刷新按钮里写死了 300，注入点在方法开头（游戏读盘之前），
            // 先把存档金币 +300 让游戏随后扣掉，净变化 0。
            new Rule("shangdianshuaxin", "OnMouseDown", 0, "FreeRefreshPrefix", true, null, "商店刷新免费"),
        };

        private static int Main(string[] args)
        {
            return Run(args.Length > 0 ? args[0] : "patch", args.Length > 1 ? args[1] : null);
        }

        /// <summary>供命令行与安装器共同调用的入口。dllPath 为 null 时自动查找游戏目录。</summary>
        public static int Run(string mode, string dllPath = null)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            mode = (mode ?? "patch").ToLowerInvariant();
            string dll = string.IsNullOrEmpty(dllPath) ? FindAssemblyCSharp() : dllPath;

            if (dll == null || !File.Exists(dll))
            {
                Console.WriteLine("找不到 Assembly-CSharp.dll，请显式传入路径");
                return 2;
            }

            Console.WriteLine("目标：" + dll);
            string backup = dll + ".orig";

            switch (mode)
            {
                case "patch":   return Patch(dll, backup);
                case "restore": return Restore(dll, backup);
                case "verify":  return Verify(dll);
                default:
                    Console.WriteLine("用法：注入器 patch|restore|verify [Assembly-CSharp.dll]");
                    return 0;
            }
        }

        // ---------------------------------------------------------------- patch

        private static int Patch(string dll, string backup)
        {
            if (!File.Exists(backup))
            {
                // 危险情形：没有原始备份，但文件已经被注入过了。
                // 此时再备份＝把「已改过」的文件当原件，以后 uninstall 会「还原」成改过的版本。必须拒绝。
                if (HasAnyInjection(dll))
                {
                    Console.WriteLine("✗ 没有原始备份，但 Assembly-CSharp.dll 已经是注入过的版本。");
                    Console.WriteLine("  继续会把你手上这份当成原件备份，以后卸载就还原不回原版了。");
                    Console.WriteLine("  请先用游戏平台的「校验文件完整性」或者重装游戏，再运行安装。");
                    return 5;
                }

                File.Copy(dll, backup);
                Console.WriteLine("已备份原文件 → " + Path.GetFileName(backup));
            }
            else
            {
                // 每次都从原始备份重新开始：否则规则改了以后，旧的不正确注入会因为
                // “已注入则跳过”而残留下来（真实踩过：buhuo 传错实参却一直存在）。
                File.Copy(backup, dll, overwrite: true);
                Console.WriteLine("已从原始备份重置 " + Path.GetFileName(dll) + "，重新注入");
            }

            AssemblyDefinition asm = Read(dll, out string readError);
            if (asm == null) { Console.WriteLine("读取失败：" + readError); return 3; }

            ModuleDefinition module = asm.MainModule;
            AssemblyNameReference modRef = GetOrAddAssemblyRef(module, ModAssemblyName);

            int applied = 0, skipped = 0, failed = 0;
            foreach (Rule rule in Rules)
            {
                TypeDefinition type = FindType(module, rule.TypeName);
                if (type == null)
                {
                    Console.WriteLine("  [失败] 找不到类型 " + rule.TypeName + "（" + rule.Note + "）");
                    failed++;
                    continue;
                }

                MethodDefinition method = type.Methods.FirstOrDefault(m =>
                    m.Name == rule.MethodName && m.Parameters.Count == rule.ParamCount);

                if (method == null || !method.HasBody)
                {
                    Console.WriteLine("  [失败] 找不到方法 " + rule.TypeName + "." + rule.MethodName + "/" +
                                      rule.ParamCount + "（" + rule.Note + "）");
                    failed++;
                    continue;
                }

                if (AlreadyInjected(method, rule.HookMethod))
                {
                    Console.WriteLine("  [跳过] " + rule.TypeName + "." + rule.MethodName + " 已注入 " + rule.HookMethod);
                    skipped++;
                    continue;
                }

                try
                {
                    MethodReference hook = MakeHookReference(module, modRef, rule, method);
                    int count = Inject(method, hook, rule);
                    Console.WriteLine("  [完成] " + rule.TypeName + "." + rule.MethodName + " ← " + rule.HookMethod +
                                      "(" + string.Join(",", rule.Args) + ")  插入点 " + count + " 处 ／ " + rule.Note);
                    applied++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  [失败] " + rule.TypeName + "." + rule.MethodName + " 注入异常：" + ex.Message);
                    failed++;
                }
            }

            if (applied == 0)
            {
                Console.WriteLine();
                Console.WriteLine(skipped > 0 ? "全部规则此前已注入，未写回文件。" : "没有任何规则成功注入，未写回文件。");
                return failed > 0 ? 4 : 0;
            }

            try
            {
                asm.Write(dll);
            }
            catch (Exception ex)
            {
                Console.WriteLine("写回失败：" + ex.Message);
                Console.WriteLine("原文件已备份为 " + Path.GetFileName(backup) + "，可运行 restore 还原。");
                return 3;
            }

            Console.WriteLine();
            Console.WriteLine("注入完成：成功 " + applied + "，跳过 " + skipped + "，失败 " + failed);
            Verify(dll);
            return failed == 0 ? 0 : 4;
        }

        private static int Inject(MethodDefinition method, MethodReference hook, Rule rule)
        {
            MethodBody body = method.Body;
            ILProcessor il = body.GetILProcessor();

            if (rule.AtStart)
            {
                InjectAt(method, hook, rule, body.Instructions[0]);
                return 1;
            }

            // 后缀式：只支持 void 方法（返回值在栈上时没法简单插入）
            if (method.ReturnType.FullName != "System.Void")
                throw new InvalidOperationException("后缀注入只支持 void 方法");

            Instruction[] rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            if (rets.Length == 0) throw new InvalidOperationException("方法里没有 ret");

            foreach (Instruction ret in rets) InjectAt(method, hook, rule, ret);
            return rets.Length;
        }

        private static void InjectAt(MethodDefinition method, MethodReference hook, Rule rule, Instruction anchor)
        {
            ILProcessor il = method.Body.GetILProcessor();

            for (int i = 0; i < rule.Args.Length; i++)
                il.InsertBefore(anchor, LoadArgument(method, rule.Args[i]));

            il.InsertBefore(anchor, Instruction.Create(OpCodes.Call, hook));
        }

        private static Instruction LoadArgument(MethodDefinition method, string spec)
        {
            if (spec == "this") return Instruction.Create(OpCodes.Ldarg_0);

            int colon = spec.IndexOf(':');
            string kind = colon < 0 ? spec : spec.Substring(0, colon);
            int ilIndex = colon < 0 ? 0 : int.Parse(spec.Substring(colon + 1));

            // 注意：IL 的参数编号里，实例方法的 arg0 是 this，静态方法没有 this。
            // 所以规范里的下标一律按 IL 编号写（静态方法第一个参数就是 0）。
            int pIndex = method.IsStatic ? ilIndex : ilIndex - 1;
            if (pIndex < 0 || pIndex >= method.Parameters.Count)
                throw new InvalidOperationException("参数下标越界：" + spec +
                    "（" + (method.IsStatic ? "静态" : "实例") + "方法" + method.Name +
                    " 只有 " + method.Parameters.Count + " 个参数）");

            ParameterDefinition p = method.Parameters[pIndex];
            return kind == "refint" ? Instruction.Create(OpCodes.Ldarga, p) : Instruction.Create(OpCodes.Ldarg, p);
        }

        private static bool AlreadyInjected(MethodDefinition method, string hookMethod)
        {
            return method.Body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference mr &&
                mr.Name == hookMethod &&
                mr.DeclaringType != null &&
                mr.DeclaringType.Name == HookTypeName);
        }

        /// <summary>文件里是否已经有我们的注入（用于识别「没有备份但已被改过」的危险情形）。</summary>
        private static bool HasAnyInjection(string dll)
        {
            try
            {
                AssemblyDefinition asm = Read(dll, out _);
                if (asm == null) return false;
                ModuleDefinition module = asm.MainModule;

                foreach (Rule rule in Rules)
                {
                    TypeDefinition type = FindType(module, rule.TypeName);
                    MethodDefinition method = type?.Methods.FirstOrDefault(m =>
                        m.Name == rule.MethodName && m.Parameters.Count == rule.ParamCount);
                    if (method != null && method.HasBody && AlreadyInjected(method, rule.HookMethod)) return true;
                }
            }
            catch { }
            return false;
        }

        private static MethodReference MakeHookReference(ModuleDefinition module, AssemblyNameReference modRef,
            Rule rule, MethodDefinition target)
        {
            var hookType = new TypeReference(HookNamespace, HookTypeName, module, modRef);
            var hook = new MethodReference(rule.HookMethod, module.TypeSystem.Void, hookType) { HasThis = false };

            foreach (string spec in rule.Args)
            {
                string kind = spec == "this" ? "this" :
                              (spec.IndexOf(':') < 0 ? spec : spec.Substring(0, spec.IndexOf(':')));

                TypeReference pt;
                switch (kind)
                {
                    case "this":     pt = target.DeclaringType; break;
                    case "refint":   pt = new ByReferenceType(module.TypeSystem.Int32); break;
                    case "intarray": pt = new ArrayType(module.TypeSystem.Int32); break;
                    default:         pt = module.TypeSystem.Object; break;
                }

                hook.Parameters.Add(new ParameterDefinition(pt));
            }

            return hook;
        }

        // ---------------------------------------------------------------- restore / verify

        private static int Restore(string dll, string backup)
        {
            if (!File.Exists(backup))
            {
                Console.WriteLine("找不到备份文件：" + backup);
                return 2;
            }

            File.Copy(backup, dll, overwrite: true);
            Console.WriteLine("已从备份还原 " + Path.GetFileName(dll));
            return 0;
        }

        private static int Verify(string dll)
        {
            AssemblyDefinition asm = Read(dll, out string readError);
            if (asm == null) { Console.WriteLine("读取失败：" + readError); return 3; }

            ModuleDefinition module = asm.MainModule;
            int present = 0, missing = 0;

            foreach (Rule rule in Rules)
            {
                TypeDefinition type = FindType(module, rule.TypeName);
                MethodDefinition method = type?.Methods.FirstOrDefault(m =>
                    m.Name == rule.MethodName && m.Parameters.Count == rule.ParamCount);

                bool ok = method != null && method.HasBody && AlreadyInjected(method, rule.HookMethod);
                Console.WriteLine("  [" + (ok ? "已注入" : "未注入") + "] " + rule.TypeName + "." +
                                  rule.MethodName + " ← " + rule.HookMethod);
                if (ok) present++; else missing++;
            }

            Console.WriteLine("验证结果：已注入 " + present + "，未注入 " + missing);
            return missing == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 小工具

        private static AssemblyDefinition Read(string path, out string error)
        {
            error = null;
            try
            {
                // 必须走内存流：ReadAssembly(path) 会锁住文件导致写回失败；
                // Deferred 模式下 Cecil 会一直持有该流，所以此处不能 Dispose。
                var ms = new MemoryStream(File.ReadAllBytes(path));
                return AssemblyDefinition.ReadAssembly(ms, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Deferred,
                    ReadSymbols = false,
                    AssemblyResolver = BuildResolver(path)
                });
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static DefaultAssemblyResolver BuildResolver(string dllPath)
        {
            var resolver = new DefaultAssemblyResolver();
            string dir = Path.GetDirectoryName(Path.GetFullPath(dllPath));
            if (!string.IsNullOrEmpty(dir)) resolver.AddSearchDirectory(dir);
            return resolver;
        }

        /// <summary>游戏类型都在全局命名空间，先按全名找，找不到再扫一遍。</summary>
        private static TypeDefinition FindType(ModuleDefinition module, string typeName)
        {
            TypeDefinition t = module.GetType(typeName);
            if (t != null) return t;

            foreach (TypeDefinition candidate in module.Types)
            {
                if (candidate.Name == typeName) return candidate;
                TypeDefinition nested = candidate.NestedTypes.FirstOrDefault(n => n.Name == typeName);
                if (nested != null) return nested;
            }
            return null;
        }

        private static AssemblyNameReference GetOrAddAssemblyRef(ModuleDefinition module, string name)
        {
            AssemblyNameReference existing = module.AssemblyReferences.FirstOrDefault(r => r.Name == name);
            if (existing != null) return existing;

            var reference = new AssemblyNameReference(name, new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(reference);
            return reference;
        }

        private static string FindAssemblyCSharp()
        {
            string env = Environment.GetEnvironmentVariable("GACHA_GAME_DIR");
            string[] candidates = string.IsNullOrEmpty(env)
                ? new string[0]
                : new[] { System.IO.Path.Combine(env, "抽卡版PVZ_Data", "Managed", "Assembly-CSharp.dll") };
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
