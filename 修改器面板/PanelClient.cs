using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using PvzShared;

namespace PvzPanel
{
    public sealed class ModFeature
    {
        public string Key = "";
        public string Label = "";
        public string Group = "";
        public string Kind = "text";
        public string Raw = "";
        public string Hint = "";

        /// <summary>生效场景："" 随时；"level" 需在关卡内；"shop" 靠进商店触发。</summary>
        public string Scene = "";
        public double Min;
        public double Max;

        /// <summary>非空 → 面板渲染成分段按钮（用户点一下就行，不用输数字）。</summary>
        public readonly List<KeyValuePair<string, string>> Options = new List<KeyValuePair<string, string>>();

        public bool IsBool { get { return Kind == "bool"; } }
        public bool IsNumber { get { return Kind == "int" || Kind == "float"; } }
        public bool HasOptions { get { return Options.Count > 0; } }
        public bool BoolValue { get { return Raw == "true"; } }

        public double NumValue
        {
            get
            {
                return double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            }
        }
    }

    /// <summary>mod 侧埋点的只读快照（面板「诊断」页用）。</summary>
    /// <summary>脚本运行状态。</summary>
    public sealed class ModScriptState
    {
        public bool Running;
        public int Line;
        public int Executed;
        public int ErrorLine;
        public string Status = "";
    }

    /// <summary>一条指令的说明（由 mod 下发，面板首页与脚本页共用）。</summary>
    public sealed class InstructionInfo
    {
        public string Syntax = "";
        public string Summary = "";
        public string Example = "";
    }

    public sealed class ModDiagInfo    {
        public readonly Dictionary<string, int> Numbers = new Dictionary<string, int>();
        public string Scene = "";
        public string Notes = "";

        public int Get(string key)
        {
            return Numbers.TryGetValue(key, out int v) ? v : 0;
        }
    }

    public sealed class ModState
    {
        public bool Ok;
        public string Product = "";
        public string Version = "";
        public string ModVersion = "";
        public string Error = "";
        public int CommandsHandled;
        public string LastAction = "";
        public string LastResult = "";
        public bool LastActionOk = true;

        public readonly List<ModFeature> Features = new List<ModFeature>();
        public readonly List<string> FailedCapabilities = new List<string>();
        public ModDiagInfo Diag = new ModDiagInfo();
        public ModScriptState Script = new ModScriptState();
        public readonly List<InstructionInfo> Instructions = new List<InstructionInfo>();
        public readonly List<string> ValueHints = new List<string>();

        public ModFeature Find(string key)
        {
            for (int i = 0; i < Features.Count; i++)
                if (Features[i].Key == key) return Features[i];
            return null;
        }
    }

    /// <summary>
    /// 面板侧的文件通道客户端：读状态文件、写命令文件。
    /// 不引用 WPF，便于单元测试。
    /// </summary>
    public sealed class PanelClient
    {
        /// <summary>状态文件多久没更新就算「游戏主循环停了」。</summary>
        public const double StaleSeconds = 6.0;

        public ModState Last = new ModState();
        public DateTime LastStateWrite = DateTime.MinValue;

        /// <summary>状态文件是否新鲜（游戏主循环在跑）。</summary>
        public bool StateFresh
        {
            get { return (DateTime.Now - LastStateWrite).TotalSeconds < StaleSeconds; }
        }

        /// <summary>游戏进程在不在（即使它失焦暂停了也算在）。</summary>
        public bool GameProcessRunning
        {
            get { return GameInfo.IsRunning(); }
        }

        /// <summary>
        /// 游戏失焦时会暂停主循环（构建时未开 Run In Background）。
        /// 此时进程在、状态文件停住，命令要等用户切回游戏才会被处理。
        /// </summary>
        public bool GameFrozen
        {
            get { return GameProcessRunning && !StateFresh; }
        }

        /// <summary>主循环真的在响应（能立即往返）。</summary>
        public bool GameAlive
        {
            get { return StateFresh && GameProcessRunning; }
        }

        public bool ReadState()
        {
            try
            {
                string path = SavePaths.StateFile;
                if (!File.Exists(path))
                {
                    Last = new ModState { Ok = false, Error = "还没收到状态文件（游戏未启动，或修改器尚未安装到游戏）" };
                    return false;
                }

                LastStateWrite = File.GetLastWriteTime(path);
                Last = ParseState(File.ReadAllText(path));
                return Last.Ok;
            }
            catch (Exception ex)
            {
                Last = new ModState { Ok = false, Error = "读取状态失败：" + ex.Message };
                return false;
            }
        }

        public bool SendSet(string key, string rawValue, out string error)
        {
            return AppendCommand("{\"key\":" + MiniJson.Quote(key) + ",\"value\":" + EncodeValue(rawValue) + "}", out error);
        }

        public bool SendAction(string action, string value, out string error)
        {
            return AppendCommand("{\"action\":" + MiniJson.Quote(action) + ",\"value\":" + EncodeValue(value ?? "") + "}", out error);
        }

        /// <summary>
        /// 下发一份指令集脚本。多行源码会被 MiniJson.Escape 转成 \n，
        /// 所以仍然落在命令队列的同一行里，不会把队列拆坏。
        /// </summary>
        public bool SendScript(string source, out string error)
        {
            return AppendCommand("{\"script\":" + MiniJson.Quote(source ?? "") + "}", out error);
        }

        public bool SendStopScript(out string error)
        {
            return AppendCommand("{\"scriptStop\":true}", out error);
        }

        /// <summary>
        /// 把整份设置快照追加到命令队列。
        /// 面板每次改动都发全量，所以不依赖“每条都送达”：丢一条，下一条也会把状态拉回正确。
        /// </summary>
        public bool SendSnapshot(IEnumerable<KeyValuePair<string, string>> settings, out string error)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, string> kv in settings)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(MiniJson.Quote(kv.Key)).Append(':').Append(EncodeValue(kv.Value));
            }
            sb.Append('}');
            return AppendCommand(sb.ToString(), out error);
        }

        /// <summary>
        /// 追加一行命令。**追加而不是覆盖**：游戏失焦时命令会滞留很久，
        /// 覆盖写会让用户连点的多个开关只活下最后一个（实测就是这个把功能“吃掉”了）。
        /// 面板跑在 .NET 8 上，AppendAllText 可用；游戏侧的 mscorlib 被裁剪了，所以只能面板来追加。
        /// </summary>
        private static bool AppendCommand(string json, out string error)
        {
            error = null;
            try
            {
                SavePaths.EnsureDirExists();
                File.AppendAllText(SavePaths.CommandFile, json + "\n");
                return true;
            }
            catch (Exception ex)
            {
                error = "写入命令文件失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>值编码：bool 裸字面量 / 纯数字裸数字 / 其余当字符串。</summary>
        public static string EncodeValue(string raw)
        {
            if (raw == "true" || raw == "false") return raw;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return raw;
            return MiniJson.Quote(raw ?? "");
        }

        public static ModState ParseState(string json)
        {
            var st = new ModState();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("ok", out JsonElement okEl))
                    st.Ok = okEl.ValueKind == JsonValueKind.True;
                st.Error = GetStr(root, "error");
                st.Product = GetStr(root, "product");
                st.Version = GetStr(root, "version");
                st.ModVersion = GetStr(root, "modVersion");
                st.LastAction = GetStr(root, "lastAction");
                st.LastResult = GetStr(root, "lastResult");

                if (root.TryGetProperty("commandsHandled", out JsonElement ch) && ch.ValueKind == JsonValueKind.Number)
                    st.CommandsHandled = ch.GetInt32();
                if (root.TryGetProperty("lastActionOk", out JsonElement lao))
                    st.LastActionOk = lao.ValueKind == JsonValueKind.True;

                if (root.TryGetProperty("features", out JsonElement fs) && fs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement f in fs.EnumerateArray())
                    {
                        var mf = new ModFeature
                        {
                            Key = GetStr(f, "key"),
                            Label = GetStr(f, "label"),
                            Group = GetStr(f, "group"),
                            Kind = GetStr(f, "kind"),
                        };
                        if (f.TryGetProperty("value", out JsonElement val))
                            mf.Raw = val.ValueKind == JsonValueKind.String ? (val.GetString() ?? "") :
                                     val.ValueKind == JsonValueKind.True ? "true" :
                                     val.ValueKind == JsonValueKind.False ? "false" : val.ToString();
                        mf.Hint = GetStr(f, "hint");
                        mf.Scene = GetStr(f, "scene");
                        if (f.TryGetProperty("min", out JsonElement mn) && mn.ValueKind == JsonValueKind.Number) mf.Min = mn.GetDouble();
                        if (f.TryGetProperty("max", out JsonElement mx) && mx.ValueKind == JsonValueKind.Number) mf.Max = mx.GetDouble();

                        if (f.TryGetProperty("options", out JsonElement opts) && opts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement o in opts.EnumerateArray())
                                mf.Options.Add(new KeyValuePair<string, string>(GetStr(o, "value"), GetStr(o, "label")));
                        }
                        st.Features.Add(mf);
                    }
                }

                if (root.TryGetProperty("capabilities", out JsonElement caps) && caps.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement c in caps.EnumerateArray())
                    {
                        bool ok = c.TryGetProperty("ok", out JsonElement o) && o.ValueKind == JsonValueKind.True;
                        if (!ok) st.FailedCapabilities.Add(GetStr(c, "name") + "：" + GetStr(c, "error"));
                    }
                }

                if (!st.Ok && st.Error.Length == 0) st.Error = "状态文件 ok=false";

                // 诊断埋点（没有就是空快照，不报错）
                if (root.TryGetProperty("diag", out JsonElement dg) && dg.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in dg.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Number) st.Diag.Numbers[p.Name] = p.Value.GetInt32();
                        else if (p.Name == "scene") st.Diag.Scene = p.Value.GetString() ?? "";
                        else if (p.Name == "notes") st.Diag.Notes = p.Value.GetString() ?? "";
                    }
                }

                // 脚本状态
                if (root.TryGetProperty("script", out JsonElement sc) && sc.ValueKind == JsonValueKind.Object)
                {
                    st.Script.Running = sc.TryGetProperty("running", out JsonElement r) && r.ValueKind == JsonValueKind.True;
                    st.Script.Status = GetStr(sc, "status");
                    if (sc.TryGetProperty("line", out JsonElement ln) && ln.ValueKind == JsonValueKind.Number) st.Script.Line = ln.GetInt32();
                    if (sc.TryGetProperty("executed", out JsonElement ex) && ex.ValueKind == JsonValueKind.Number) st.Script.Executed = ex.GetInt32();
                    if (sc.TryGetProperty("errorLine", out JsonElement el) && el.ValueKind == JsonValueKind.Number) st.Script.ErrorLine = el.GetInt32();
                }

                // 指令集（由 mod 下发，首页与脚本页共用同一份，不会和实现对不上）
                if (root.TryGetProperty("instructions", out JsonElement ins) && ins.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement it in ins.EnumerateArray())
                        st.Instructions.Add(new InstructionInfo
                        {
                            Syntax = GetStr(it, "syntax"),
                            Summary = GetStr(it, "summary"),
                            Example = GetStr(it, "example"),
                        });
                }

                if (root.TryGetProperty("valueHints", out JsonElement vh) && vh.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement it in vh.EnumerateArray())
                        if (it.ValueKind == JsonValueKind.String) st.ValueHints.Add(it.GetString() ?? "");
                }
            }
            catch (Exception ex)
            {
                st.Ok = false;
                st.Error = "状态解析失败：" + ex.Message;
            }
            return st;
        }

        private static string GetStr(JsonElement el, string name)
        {
            return el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "") : "";
        }
    }
}
