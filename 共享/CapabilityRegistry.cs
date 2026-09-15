using System;
using System.Collections.Generic;
using System.Text;

namespace PvzShared
{
    /// <summary>
    /// 能力登记表：记录每个功能/挂钩在本次运行中到底生效了没有，供面板显示
    /// （游戏版本变化导致某个目标方法不存在时，只降级为「不可用」，不影响其它功能）。
    /// </summary>
    public sealed class CapabilityRegistry
    {
        private readonly List<string> _names = new List<string>();
        private readonly Dictionary<string, bool> _ok = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _error = new Dictionary<string, string>(StringComparer.Ordinal);

        public IEnumerable<string> Names { get { return _names; } }

        public int Count { get { return _names.Count; } }

        public int OkCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _names.Count; i++) if (IsOk(_names[i])) n++;
                return n;
            }
        }

        /// <summary>登记一项能力。重复登记同名会覆盖结果。</summary>
        public void Register(string name, bool ok, string error = "")
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!_ok.ContainsKey(name)) _names.Add(name);
            _ok[name] = ok;
            _error[name] = ok ? "" : (error ?? "");
        }

        public bool IsOk(string name)
        {
            return name != null && _ok.TryGetValue(name, out bool v) && v;
        }

        public string ErrorOf(string name)
        {
            if (name == null) return "";
            return _error.TryGetValue(name, out string e) ? e : "";
        }

        public string SummaryJson()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < _names.Count; i++)
            {
                string n = _names[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(MiniJson.Quote(n));
                sb.Append(",\"ok\":").Append(MiniJson.Bool(IsOk(n)));
                sb.Append(",\"error\":").Append(MiniJson.Quote(ErrorOf(n)));
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
