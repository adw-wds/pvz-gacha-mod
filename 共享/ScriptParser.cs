using System;
using System.Collections.Generic;

namespace PvzShared
{
    public enum InstKind
    {
        Set,        // set <特性键> <值>
        Toggle,     // toggle <特性键>
        Do,         // do <动作> [值]
        Wait,       // wait <毫秒>
        Log,        // log <文本>
        IfScene,    // if scene == <场景>
        IfKey,      // if <特性键> == <值>
        Repeat,     // repeat <次数>
        Stop,       // stop
    }

    public sealed class ScriptNode
    {
        public InstKind Kind;
        public string A = "";        // 键 / 动作 / 场景 / 值
        public string B = "";        // 比较值 / 动作参数
        public int Count = 1;        // repeat 次数
        public int Line;             // 原文件行号（报错定位用）
        public List<ScriptNode> Body;   // if / repeat 的子块
    }

    public sealed class ScriptProgram
    {
        public bool Ok;
        public string Error = "";
        public int ErrorLine;
        public List<ScriptNode> Root = new List<ScriptNode>();
        public int InstructionCount;

        public static ScriptProgram Failed(string error, int line)
        {
            return new ScriptProgram { Ok = false, Error = error, ErrorLine = line };
        }
    }

    /// <summary>
    /// 指令集脚本的解析器：纯逻辑、不碰 Unity 也不碰文件，所以能整套离线单测。
    ///
    /// 为什么不用真正的 C# 脚本：mod 跑在游戏被裁剪过的 Mono 运行时里，
    /// 连 Assembly.LoadFile 都没有，Roslyn 那套动态编译根本装不起来。
    /// 所以脚本定义成**一套指令集**，由 mod 逐行解释执行 —— 面板负责编辑与高亮，mod 只认这些指令。
    /// </summary>
    public static class ScriptParser
    {
        public static ScriptProgram Parse(string text)
        {
            var prog = new ScriptProgram();
            if (text == null) { prog.Ok = true; return prog; }

            string[] rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var stack = new List<List<ScriptNode>>();
            var pending = new List<ScriptNode>();      // 等待 end 的块头

            stack.Add(prog.Root);

            for (int i = 0; i < rawLines.Length; i++)
            {
                int lineNo = i + 1;
                string line = rawLines[i];

                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;

                string[] tok = Split(line);
                string op = tok[0].ToLowerInvariant();

                ScriptNode node;

                switch (op)
                {
                    case "set":
                        if (tok.Length < 3) return ScriptProgram.Failed("set 需要两个参数：set <特性键> <值>", lineNo);
                        node = new ScriptNode { Kind = InstKind.Set, A = tok[1], B = tok[2], Line = lineNo };
                        break;

                    case "toggle":
                        if (tok.Length < 2) return ScriptProgram.Failed("toggle 需要参数：toggle <特性键>", lineNo);
                        node = new ScriptNode { Kind = InstKind.Toggle, A = tok[1], Line = lineNo };
                        break;

                    case "do":
                        if (tok.Length < 2) return ScriptProgram.Failed("do 需要参数：do <动作> [值]", lineNo);
                        node = new ScriptNode { Kind = InstKind.Do, A = tok[1], B = tok.Length > 2 ? tok[2] : "", Line = lineNo };
                        break;

                    case "wait":
                    {
                        if (tok.Length < 2) return ScriptProgram.Failed("wait 需要毫秒数：wait 500", lineNo);
                        if (!int.TryParse(tok[1], out int ms) || ms < 0)
                            return ScriptProgram.Failed("wait 的毫秒数不是合法非负整数：" + tok[1], lineNo);
                        node = new ScriptNode { Kind = InstKind.Wait, Count = ms, Line = lineNo };
                        break;
                    }

                    case "log":
                        node = new ScriptNode { Kind = InstKind.Log, A = line.Substring(3).Trim(), Line = lineNo };
                        break;

                    case "stop":
                        node = new ScriptNode { Kind = InstKind.Stop, Line = lineNo };
                        break;

                    case "if":
                    {
                        // if scene == Zhucaidan    /    if gacha.free == true
                        if (tok.Length < 4 || tok[2] != "==")
                            return ScriptProgram.Failed("if 需要写成：if <特性键|scene> == <值>", lineNo);

                        bool isScene = string.Equals(tok[1], "scene", StringComparison.OrdinalIgnoreCase);
                        node = new ScriptNode
                        {
                            Kind = isScene ? InstKind.IfScene : InstKind.IfKey,
                            A = isScene ? tok[3] : tok[1],
                            B = isScene ? "" : tok[3],
                            Line = lineNo,
                            Body = new List<ScriptNode>()
                        };
                        break;
                    }

                    case "repeat":
                    {
                        if (tok.Length < 2) return ScriptProgram.Failed("repeat 需要次数：repeat 3", lineNo);
                        if (!int.TryParse(tok[1], out int n) || n <= 0)
                            return ScriptProgram.Failed("repeat 的次数必须是正整数：" + tok[1], lineNo);
                        node = new ScriptNode { Kind = InstKind.Repeat, Count = n, Line = lineNo, Body = new List<ScriptNode>() };
                        break;
                    }

                    case "end":
                    {
                        if (pending.Count == 0) return ScriptProgram.Failed("多余的 end：没有对应的 if / repeat", lineNo);
                        pending.RemoveAt(pending.Count - 1);
                        stack.RemoveAt(stack.Count - 1);
                        continue;   // end 自己不产生节点
                    }

                    default:
                        return ScriptProgram.Failed("不认识的指令「" + tok[0] + "」", lineNo);
                }

                stack[stack.Count - 1].Add(node);
                prog.InstructionCount++;

                if (node.Kind == InstKind.IfScene || node.Kind == InstKind.IfKey || node.Kind == InstKind.Repeat)
                {
                    pending.Add(node);
                    stack.Add(node.Body);
                }
            }

            if (pending.Count > 0)
            {
                ScriptNode open = pending[pending.Count - 1];
                return ScriptProgram.Failed("有 " + pending.Count + " 个块没有 end（最早在第 " + open.Line + " 行）", open.Line);
            }

            prog.Ok = true;
            return prog;
        }

        /// <summary>按空白切词（脚本里不需要引号，值本身不含空格）。</summary>
        private static string[] Split(string line)
        {
            var list = new List<string>(4);
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                if (i >= line.Length) break;
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                list.Add(line.Substring(start, i - start));
            }
            return list.ToArray();
        }
    }
}
