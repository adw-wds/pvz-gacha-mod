using System;
using System.Collections.Generic;
using System.Text;

namespace PvzShared
{
    /// <summary>
    /// 命令与状态的编解码。**不碰 socket、不碰文件**，纯字符串进纯字符串出，
    /// 因此整套对外契约都能在测试运行器里离线验证。
    ///
    /// 文件通道：面板写「修改器命令.json」→ mod 每帧轮询读到后调 HandleCommand →
    /// 删除命令文件 → 写「修改器状态.json」（BuildStateJson）供面板读取。
    /// </summary>
    public sealed class CommandApi
    {
        public string Product = "抽卡版PVZ";
        public string Version = "0.60.0";
        public string ModVersion = "1.0.0";

        public int CommandsHandled;
        public string LastAction = "";
        public string LastActionResult = "";
        public bool LastActionOk = true;

        /// <summary>
        /// 可选的诊断数据提供者（mod 侧填埋点，返回一段 JSON 对象）。
        /// 返回内容会作为状态文件的 "diag" 字段；不填就是空对象。
        /// </summary>
        public Func<string> DiagProvider;

        /// <summary>脚本引擎的入口：传脚本源码，返回空字符串表示成功启动，否则是错误原因。</summary>
        public Func<string, string> ScriptStarter;

        /// <summary>停止当前脚本。</summary>
        public Action ScriptStopper;

        /// <summary>脚本运行状态（JSON 片段），写进状态文件给面板读。</summary>
        public Func<string> ScriptStatusProvider;

        private readonly ModSettings _settings;
        private readonly CapabilityRegistry _caps;
        private readonly Func<string, string, string> _actionHandler;

        public CommandApi(ModSettings settings, CapabilityRegistry caps, Func<string, string, string> actionHandler)
        {
            _settings = settings ?? new ModSettings();
            _caps = caps;
            _actionHandler = actionHandler;
        }

        /// <summary>处理一条命令（命令文件的 JSON 内容），返回响应 JSON。永不抛异常。</summary>
        public string HandleCommand(string json)
        {
            try
            {
                Dictionary<string, string> obj = MiniJson.ParseFlatObject(json ?? "");

                if (obj.TryGetValue("action", out string action) && !string.IsNullOrEmpty(action))
                    return RunAction(action, obj.TryGetValue("value", out string av) ? av : "");

                // 脚本：{"script":"<源码>"} 启动；{"scriptStop":true} 停止
                if (obj.TryGetValue("scriptStop", out string ss) && !string.IsNullOrEmpty(ss))
                    return StopScript();

                if (obj.TryGetValue("script", out string src) && !string.IsNullOrEmpty(src))
                    return StartScript(src);

                if (obj.TryGetValue("key", out string key) && !string.IsNullOrEmpty(key))
                    return SetFeature(key, obj.TryGetValue("value", out string kv) ? kv : "");

                // 没有 action / key → 当作「整份设置的快照」：一次把用户当前所有开关都应用上。
                // 面板每次都发全量快照，所以即使某一条在极端情况下丢了，下一条也会自愈。
                if (obj.Count > 0) return ApplySnapshot(obj);

                return Error("命令缺少 key 或 action 字段");
            }
            catch (Exception ex)
            {
                return Error("命令解析失败：" + ex.Message);
            }
        }

        /// <summary>启动指令集脚本。语法错误会直接退回去，不会跑半截。</summary>
        private string StartScript(string source)
        {
            if (ScriptStarter == null) return Error("引擎未就绪：这个版本的 mod 没有脚本支持");

            string err = ScriptStarter(source);
            if (!string.IsNullOrEmpty(err))
            {
                LastAction = "script";
                LastActionOk = false;
                LastActionResult = err;
                return Error(err);
            }

            CommandsHandled++;
            LastAction = "script";
            LastActionOk = true;
            LastActionResult = "脚本已启动";

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"detail\":").Append(MiniJson.Quote(LastActionResult));
            sb.Append(",\"state\":").Append(BuildStateJson()).Append('}');
            return sb.ToString();
        }

        private string StopScript()
        {
            if (ScriptStopper == null) return Error("引擎未就绪");
            ScriptStopper();

            CommandsHandled++;
            LastAction = "scriptStop";
            LastActionOk = true;
            LastActionResult = "脚本已停止";
            return "{\"ok\":true,\"detail\":" + MiniJson.Quote(LastActionResult) + "}";
        }

        private string SetFeature(string key, string value)
        {
            if (!FeatureCatalog.TrySet(_settings, key, value, out string error)) return Error(error);

            CommandsHandled++;
            LastAction = "";
            LastActionOk = true;
            LastActionResult = "已设置 " + key;

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"detail\":").Append(MiniJson.Quote("已设置 " + key));
            sb.Append(",\"state\":").Append(BuildStateJson()).Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 应用整份设置快照：把面板里的所有开关一次性对齐过来。
        /// 不认识的 key 直接忽略（旧面板连新 mod、或反之，都不能翻车）。
        /// </summary>
        private string ApplySnapshot(Dictionary<string, string> snapshot)
        {
            int applied = 0;
            List<string> unknown = null;

            foreach (KeyValuePair<string, string> kv in snapshot)
            {
                if (FeatureCatalog.Find(kv.Key) == null)
                {
                    // 不认识就跳过（挹2兼容），但一定要记下来：
                    // 静默忽略会让这些项在面板上永远停在「同步中」，而用户无从得知原因。
                    if (unknown == null) unknown = new List<string>();
                    unknown.Add(kv.Key);
                    continue;
                }
                try { if (FeatureCatalog.TrySet(_settings, kv.Key, kv.Value, out _)) applied++; }
                catch { }
            }

            if (applied == 0)
            {
                return Error(unknown != null
                    ? "游戏不认识这些设置项：" + Join(unknown) + "（面板与游戏里的 mod 版本不一致，重新运行「安装.cmd」即可）"
                    : "快照里没有任何可识别的设置项");
            }

            CommandsHandled++;
            LastAction = "";

            // 部分项被忽略：仍旧算成功（已应用的要落盘），但把问题显式上报。
            if (unknown != null)
            {
                LastActionOk = false;
                LastActionResult = "已同步 " + applied + " 项，但游戏不认识 " + unknown.Count
                                 + " 项：" + Join(unknown) + "（面板与 mod 版本不一致，重跑「安装.cmd」）";
            }
            else
            {
                LastActionOk = true;
                LastActionResult = "已同步 " + applied + " 项设置";
            }

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"applied\":").Append(MiniJson.Num(applied));
            if (unknown != null)
                sb.Append(",\"warn\":").Append(MiniJson.Quote("忽略未知项：" + Join(unknown)));
            sb.Append(",\"detail\":").Append(MiniJson.Quote(LastActionResult));
            sb.Append(",\"state\":").Append(BuildStateJson()).Append('}');
            return sb.ToString();
        }

        /// <summary>把未知 key 拼成一行，最多列 3 个，避免报错长到看不清。</summary>
        private static string Join(List<string> keys)
        {
            int n = keys.Count > 3 ? 3 : keys.Count;
            return string.Join("、", keys.GetRange(0, n).ToArray())
                   + (keys.Count > n ? " 等 " + keys.Count + " 项" : "");
        }

        private string RunAction(string action, string value)
        {
            LastAction = action;
            CommandsHandled++;

            if (_actionHandler == null)
            {
                LastActionOk = false;
                LastActionResult = "动作处理器未就绪";
                return Error(LastActionResult);
            }

            string result = _actionHandler(action, value);
            if (string.IsNullOrEmpty(result))
            {
                LastActionOk = false;
                LastActionResult = "动作未返回结果";
                return Error(LastActionResult);
            }

            // 解析动作返回，把 ok/detail/error 归一化后原样返回
            try
            {
                Dictionary<string, string> r = MiniJson.ParseFlatObject(result);
                bool ok = MiniJson.TryGetBool(r, "ok", out bool okv) && okv;
                LastActionOk = ok;
                LastActionResult = ok
                    ? MiniJson.GetString(r, "detail", "完成")
                    : MiniJson.GetString(r, "error", "动作失败");
            }
            catch
            {
                LastActionOk = false;
                LastActionResult = "动作返回的不是合法 JSON";
            }

            return result;
        }

        /// <summary>状态文件内容：面板读它渲染 UI（含各开关当前值与能力可用性）。</summary>
        public string BuildStateJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true");
            sb.Append(",\"product\":").Append(MiniJson.Quote(Product));
            sb.Append(",\"version\":").Append(MiniJson.Quote(Version));
            sb.Append(",\"modVersion\":").Append(MiniJson.Quote(ModVersion));
            sb.Append(",\"commandsHandled\":").Append(MiniJson.Num(CommandsHandled));
            sb.Append(",\"lastAction\":").Append(MiniJson.Quote(LastAction));
            sb.Append(",\"lastActionOk\":").Append(MiniJson.Bool(LastActionOk));
            sb.Append(",\"lastResult\":").Append(MiniJson.Quote(LastActionResult));
            sb.Append(",\"capabilities\":").Append(_caps != null ? _caps.SummaryJson() : "[]");
            sb.Append(",\"features\":").Append(FeatureCatalog.FeaturesJson(_settings));
            sb.Append(",\"diag\":").Append(SafeDiag());
            sb.Append(",\"script\":").Append(SafeScriptStatus());
            sb.Append(",\"instructions\":").Append(InstructionSet.ToJson());
            sb.Append(",\"valueHints\":").Append(InstructionSet.ValueHintsJson());
            sb.Append('}');
            return sb.ToString();
        }

        private string SafeScriptStatus()
        {
            try
            {
                if (ScriptStatusProvider == null) return "{}";
                string json = ScriptStatusProvider();
                return string.IsNullOrEmpty(json) ? "{}" : json;
            }
            catch { return "{}"; }
        }

        private string SafeDiag()
        {
            try
            {
                if (DiagProvider == null) return "{}";
                string json = DiagProvider();
                return string.IsNullOrEmpty(json) ? "{}" : json;
            }
            catch { return "{}"; }
        }

        /// <summary>
        /// 构造失败响应，并且**必须**把失败写进 LastActionOk ——
        /// 否则状态文件会继续说「上一次成功了」，面板永远看不到命令被拒，
        /// 开关就卡在「同步中」收不了尾，用户只能当成功能坏了。
        /// 这里只是「记录+返回」，不抛异常。
        /// </summary>
        private string Error(string message)
        {
            LastActionOk = false;
            LastActionResult = message;
            return "{\"ok\":false,\"error\":" + MiniJson.Quote(message) + "}";
        }
    }
}
