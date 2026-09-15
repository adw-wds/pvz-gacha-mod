using System;
using PvzShared;

namespace PvzTests
{
    /// <summary>指令集脚本解析器：脚本引擎的安全性全靠它，所以边界要逐个钉死。</summary>
    internal static class ScriptTests
    {
        public static void Run()
        {
            Console.WriteLine("ScriptParser");

            T.Case("基本指令都能解析出来", () =>
            {
                var p = ScriptParser.Parse("set gacha.free on\ntoggle battle.oneHitKill\ndo battle.killAll\nwait 500\nlog 你好\nstop");
                T.True(p.Ok, p.Error);
                T.Eq(6, p.Root.Count);
                T.Eq(InstKind.Set, p.Root[0].Kind);
                T.Eq("gacha.free", p.Root[0].A);
                T.Eq("on", p.Root[0].B);
                T.Eq(InstKind.Toggle, p.Root[1].Kind);
                T.Eq(InstKind.Do, p.Root[2].Kind);
                T.Eq(500, p.Root[3].Count);
                T.Eq("你好", p.Root[4].A);
                T.Eq(InstKind.Stop, p.Root[5].Kind);
            });

            T.Case("注释与空行被忽略，行号仍然按原文算", () =>
            {
                var p = ScriptParser.Parse("# 开头注释\n\nset gacha.free on   # 行尾注释\n\n");
                T.True(p.Ok, p.Error);
                T.Eq(1, p.Root.Count);
                T.Eq(3, p.Root[0].Line);      // 报错时要指回原文第 3 行
            });

            T.Case("if 块与嵌套", () =>
            {
                var p = ScriptParser.Parse(
                    "if scene == SampleScene\n" +
                    "    set battle.oneHitKill on\n" +
                    "    repeat 2\n" +
                    "        do battle.killAll\n" +
                    "    end\n" +
                    "end");
                T.True(p.Ok, p.Error);
                T.Eq(1, p.Root.Count);

                ScriptNode ifNode = p.Root[0];
                T.Eq(InstKind.IfScene, ifNode.Kind);
                T.Eq("SampleScene", ifNode.A);
                T.Eq(2, ifNode.Body.Count);
                T.Eq(InstKind.Repeat, ifNode.Body[1].Kind);
                T.Eq(2, ifNode.Body[1].Count);
                T.Eq(InstKind.Do, ifNode.Body[1].Body[0].Kind);
            });

            T.Case("if <特性键> == <值> 与 scene 区分开", () =>
            {
                var p = ScriptParser.Parse("if battle.oneHitKill == true\nend");
                T.True(p.Ok, p.Error);
                T.Eq(InstKind.IfKey, p.Root[0].Kind);
                T.Eq("battle.oneHitKill", p.Root[0].A);
                T.Eq("true", p.Root[0].B);
            });

            T.Case("缺 end / 多余 end 都被拒绝并指出行号", () =>
            {
                var p1 = ScriptParser.Parse("if scene == SampleScene\nset gacha.free on");
                T.True(!p1.Ok);
                T.Contains(p1.Error, "没有 end");
                T.Eq(1, p1.ErrorLine);

                var p2 = ScriptParser.Parse("set gacha.free on\nend");
                T.True(!p2.Ok);
                T.Contains(p2.Error, "多余的 end");
                T.Eq(2, p2.ErrorLine);
            });

            T.Case("不认识的指令 / 参数不全 / 次数非法 都给出中文原因和行号", () =>
            {
                var p1 = ScriptParser.Parse("set gacha.free on\nfrobnicate x");
                T.True(!p1.Ok);
                T.Contains(p1.Error, "不认识的指令");
                T.Contains(p1.Error, "frobnicate");
                T.Eq(2, p1.ErrorLine);

                var p2 = ScriptParser.Parse("set gacha.free");
                T.True(!p2.Ok);
                T.Contains(p2.Error, "两个参数");

                var p3 = ScriptParser.Parse("wait abc");
                T.True(!p3.Ok);
                T.Contains(p3.Error, "毫秒数");

                var p4 = ScriptParser.Parse("repeat 0\nend");
                T.True(!p4.Ok);
                T.Contains(p4.Error, "正整数");

                var p5 = ScriptParser.Parse("if battle.speed 5\nend");
                T.True(!p5.Ok);
                T.Contains(p5.Error, "==");
            });

            T.Case("空脚本与 null 不报错（只是没内容可跑）", () =>
            {
                T.True(ScriptParser.Parse("").Ok);
                T.Eq(0, ScriptParser.Parse("").InstructionCount);
                T.True(ScriptParser.Parse(null).Ok);
                T.True(ScriptParser.Parse("# 只有注释").Ok);
            });

            T.Case("深嵌套不会栈溢出，且计数正确", () =>
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 30; i++) sb.Append("repeat 2\n");
                sb.Append("log 深处\n");
                for (int i = 0; i < 30; i++) sb.Append("end\n");

                var p = ScriptParser.Parse(sb.ToString());
                T.True(p.Ok, p.Error);
                T.Eq(31, p.InstructionCount);   // 30 个块头 + 1 条 log
            });

            Console.WriteLine("InstructionSet");

            T.Case("指令集非空且每条都有语法/说明/示例", () =>
            {
                T.True(InstructionSet.All.Length >= 8, "指令集太少");
                foreach (InstructionDef d in InstructionSet.All)
                {
                    T.True(!string.IsNullOrEmpty(d.Syntax), "有指令缺语法");
                    T.True(!string.IsNullOrEmpty(d.Summary), d.Syntax + " 缺说明");
                    T.True(!string.IsNullOrEmpty(d.Example), d.Syntax + " 缺示例");
                }
            });

            T.Case("文档里的每条指令，解析器都必须认识（防止文档与实现走偏）", () =>
            {
                foreach (InstructionDef d in InstructionSet.All)
                {
                    // 示例是给人看的一行片段（if / repeat 故意不带 end），
                    // 所以这里只要求「不是“不认识的指令”」—— 那就证明解析器认得这个关键字。
                    var p = ScriptParser.Parse(d.Example);
                    T.True(p.Error.IndexOf("不认识的指令", StringComparison.Ordinal) < 0,
                        "指令「" + d.Syntax + "」解析器不认识，示例报错：" + p.Error);
                }
            });

            T.Case("示例脚本本身必须能解析通过", () =>
            {
                var p = ScriptParser.Parse(InstructionSet.SampleScript);
                T.True(p.Ok, p.Error);
                T.True(p.InstructionCount >= 5);
            });

            T.Case("指令集与值的提示能序列化成 JSON", () =>
            {
                string json = InstructionSet.ToJson();
                T.True(json.StartsWith("[{", StringComparison.Ordinal));
                T.Contains(json, "\"syntax\":");
                T.Contains(json, "\"example\":");
                T.True(InstructionSet.ValueHintsJson().StartsWith("[", StringComparison.Ordinal));
            });
        }
    }
}
