using System;
using System.Collections.Generic;
using PvzShared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PvzGachaMod
{
    /// <summary>
    /// 指令集脚本的运行时。逐帧推进，`wait` 只是把「继续执行」推迟到之后某一帧，
    /// 绝不用 Thread.Sleep 之类阻塞游戏主循环。
    ///
    /// 执行模型很小：一个栈，每层是「一段节点表 + 当前下标」（repeat 还多一个剩余次数）。
    /// 每条指令最终都翻译成一次 CommandApi.HandleCommand，所以参数校验、清洗、
    /// 日志、状态回写全都复用已经测过的那一套，脚本这边不重复实现。
    /// </summary>
    internal sealed class ScriptEngine
    {
        private sealed class Frame
        {
            public List<ScriptNode> Nodes;
            public int Index;
            public int RepeatLeft;      // repeat 块用；普通块是 1
        }

        /// <summary>一帧最多执行多少条，避免长脚本把某一帧卡住。</summary>
        private const int MaxStepsPerFrame = 24;

        private readonly List<Frame> _stack = new List<Frame>();
        private readonly CommandApi _api;

        private int _waitFrames;            // 还要等几帧才继续
        private int _executed;
        private int _lastLine;

        public bool Running { get; private set; }
        public string Status { get; private set; } = "空闲";
        public int ErrorLine { get; private set; }

        public ScriptEngine(CommandApi api)
        {
            _api = api;
        }

        public int Executed { get { return _executed; } }
        public int CurrentLine { get { return _lastLine; } }

        /// <summary>启动一份脚本；语法错误会直接拒绝并给出错在第几行。</summary>
        public bool Start(string source, out string error)
        {
            error = null;

            ScriptProgram prog = ScriptParser.Parse(source);
            if (!prog.Ok)
            {
                ErrorLine = prog.ErrorLine;
                Status = "语法错误（第 " + prog.ErrorLine + " 行）：" + prog.Error;
                return false;
            }

            if (prog.InstructionCount == 0)
            {
                Status = "脚本是空的";
                return false;
            }

            _stack.Clear();
            _stack.Add(new Frame { Nodes = prog.Root, Index = 0, RepeatLeft = 1 });
            _waitFrames = 0;
            _executed = 0;
            _lastLine = 0;
            ErrorLine = 0;
            Running = true;
            Status = "运行中（共 " + prog.InstructionCount + " 条指令）";
            return true;
        }

        public void Stop(string reason)
        {
            if (!Running && _stack.Count == 0) return;
            _stack.Clear();
            _waitFrames = 0;
            Running = false;
            Status = string.IsNullOrEmpty(reason) ? "已停止" : reason;
        }

        /// <summary>每个游戏帧调一次。</summary>
        public void Tick()
        {
            if (!Running) return;

            try
            {
                if (_waitFrames > 0) { _waitFrames--; return; }

                int steps = 0;
                while (Running && steps < MaxStepsPerFrame)
                {
                    if (_waitFrames > 0) return;      // 刚遇到 wait，这一帧不再往下
                    if (!StepOnce()) break;
                    steps++;
                }
            }
            catch (Exception ex)
            {
                ModRuntime.LogOnce("脚本执行", ex);
                Stop("执行出错：" + ex.Message);
            }
        }

        /// <summary>执行一条指令；返回 false 表示整个脚本已经跑完。</summary>
        private bool StepOnce()
        {
            if (_stack.Count == 0) { Finish("已跑完"); return false; }

            Frame frame = _stack[_stack.Count - 1];
            if (frame.Index >= frame.Nodes.Count)
            {
                _stack.RemoveAt(_stack.Count - 1);

                // repeat 块结束：还有剩余次数就从头再来
                if (frame.RepeatLeft > 1)
                {
                    frame.RepeatLeft--;
                    frame.Index = 0;
                    _stack.Add(frame);
                    return true;
                }

                if (_stack.Count == 0) { Finish("已跑完"); return false; }
                return true;
            }

            ScriptNode node = frame.Nodes[frame.Index];
            _lastLine = node.Line;
            _executed++;          // 计数的是「走过多少条指令」，含 log / if / wait / repeat 这些",

            switch (node.Kind)
            {
                case InstKind.Set:
                    Run("{\"key\":" + MiniJson.Quote(node.A) + ",\"value\":" + EncodeValue(node.B) + "}");
                    frame.Index++;
                    break;

                case InstKind.Toggle:
                {
                    FeatureDef def = FeatureCatalog.Find(node.A);
                    if (def == null) { Stop("第 " + node.Line + " 行：不认识的特性键「" + node.A + "」"); return false; }

                    string cur = FeatureCatalog.RawValue(def, ModRuntime.Settings);
                    string next = cur == "true" ? "false" : "true";
                    Run("{\"key\":" + MiniJson.Quote(node.A) + ",\"value\":" + next + "}");
                    frame.Index++;
                    break;
                }

                case InstKind.Do:
                    Run("{\"action\":" + MiniJson.Quote(node.A) + ",\"value\":" + EncodeValue(node.B) + "}");
                    frame.Index++;
                    break;

                case InstKind.Wait:
                    // 60fps 下换算成帧数；不追求精确，只要求不卡住主循环
                    _waitFrames = Math.Max(1, node.Count * 60 / 1000);
                    frame.Index++;
                    return true;

                case InstKind.Log:
                    ModRuntime.Log("[脚本] " + node.A);
                    frame.Index++;
                    break;

                case InstKind.Stop:
                    Finish("被 stop 结束");
                    return false;

                case InstKind.IfScene:
                {
                    string scene = "";
                    try { scene = SceneManager.GetActiveScene().name; } catch { }
                    bool hit = string.Equals(scene, node.A, StringComparison.OrdinalIgnoreCase);
                    frame.Index++;
                    if (hit) _stack.Add(new Frame { Nodes = node.Body, Index = 0, RepeatLeft = 1 });
                    break;
                }

                case InstKind.IfKey:
                {
                    FeatureDef def = FeatureCatalog.Find(node.A);
                    if (def == null) { Stop("第 " + node.Line + " 行：不认识的特性键「" + node.A + "」"); return false; }

                    string cur = FeatureCatalog.RawValue(def, ModRuntime.Settings);
                    bool hit = SameValue(cur, Normalize(node.B));
                    frame.Index++;
                    if (hit) _stack.Add(new Frame { Nodes = node.Body, Index = 0, RepeatLeft = 1 });
                    break;
                }

                case InstKind.Repeat:
                    frame.Index++;
                    _stack.Add(new Frame { Nodes = node.Body, Index = 0, RepeatLeft = node.Count });
                    break;

                default:
                    frame.Index++;
                    break;
            }

            return true;
        }

        private void Finish(string reason)
        {
            bool wasRunning = Running;
            Running = false;
            _stack.Clear();
            _waitFrames = 0;
            Status = reason + "，共执行 " + _executed + " 条";
            if (wasRunning) ModRuntime.Log("[脚本] " + Status);
        }

        /// <summary>把指令交给命令通道处理：参数校验/清洗/日志全部复用已有实现。</summary>
        private void Run(string json)
        {
            string response = _api.HandleCommand(json);
            if (response.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0)
            {
                Stop("第 " + _lastLine + " 行执行失败：" + response);
            }
        }

        /// <summary>on/off 是脚本里更好写的别名，这里统一成通道认的字面量。</summary>
        private static string Normalize(string v)
        {
            if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return "true";
            if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return "false";
            return v ?? "";
        }

        private static string EncodeValue(string raw)
        {
            raw = Normalize(raw);
            if (raw == "true" || raw == "false") return raw;
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return raw;
            return MiniJson.Quote(raw);
        }

        private static bool SameValue(string a, string b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (double.TryParse(a, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double da) &&
                double.TryParse(b, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double db))
                return Math.Abs(da - db) < 0.0001;
            return false;
        }

        /// <summary>写进状态文件给面板看。</summary>
        public string StatusJson()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"running\":").Append(MiniJson.Bool(Running));
            sb.Append(",\"line\":").Append(MiniJson.Num(_lastLine));
            sb.Append(",\"executed\":").Append(MiniJson.Num(_executed));
            sb.Append(",\"status\":").Append(MiniJson.Quote(Status));
            sb.Append(",\"errorLine\":").Append(MiniJson.Num(ErrorLine));
            sb.Append('}');
            return sb.ToString();
        }
    }
}
