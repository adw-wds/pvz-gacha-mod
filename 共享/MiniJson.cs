using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PvzShared
{
    /// <summary>
    /// 极简 JSON：只服务本项目「扁平对象」的命令/状态文件，不处理嵌套。
    ///
    /// 注意：本游戏的 mscorlib 被裁剪过，写这个类用到的 API 都要先过 API检查 Verify。
    /// </summary>
    public static class MiniJson
    {
        public static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Quote(string s) { return "\"" + Escape(s) + "\""; }

        public static string Num(long l) { return l.ToString(CultureInfo.InvariantCulture); }

        public static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Bool(bool b) { return b ? "true" : "false"; }

        /// <summary>解析扁平对象；值可为 string / number / true / false / null。返回 key → 原始值字符串（字符串已反转义）。</summary>
        public static Dictionary<string, string> ParseFlatObject(string json)
        {
            if (json == null) throw new FormatException("JSON 为空");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            SkipWs(json, ref i);
            Expect(json, ref i, '{');
            SkipWs(json, ref i);
            if (Peek(json, i) == '}') { i++; return result; }

            while (true)
            {
                SkipWs(json, ref i);
                string key = ReadString(json, ref i);
                SkipWs(json, ref i);
                Expect(json, ref i, ':');
                SkipWs(json, ref i);

                char c = Peek(json, i);
                string value = c == '"' ? ReadString(json, ref i) : ReadLiteral(json, ref i);
                result[key] = value;

                SkipWs(json, ref i);
                char delim = Peek(json, i);
                if (delim == ',') { i++; continue; }
                if (delim == '}') { i++; break; }
                throw new FormatException("JSON 格式错误：位置 " + i);
            }

            SkipWs(json, ref i);
            if (i != json.Length) throw new FormatException("JSON 尾部有多余内容");
            return result;
        }

        public static bool TryGetLong(Dictionary<string, string> obj, string key, out long v)
        {
            v = 0;
            if (obj == null || !obj.TryGetValue(key, out string raw)) return false;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        public static bool TryGetDouble(Dictionary<string, string> obj, string key, out double v)
        {
            v = 0;
            if (obj == null || !obj.TryGetValue(key, out string raw)) return false;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        public static bool TryGetBool(Dictionary<string, string> obj, string key, out bool v)
        {
            v = false;
            if (obj == null || !obj.TryGetValue(key, out string raw)) return false;
            if (raw == "true") { v = true; return true; }
            if (raw == "false") { v = false; return true; }
            return false;
        }

        public static string GetString(Dictionary<string, string> obj, string key, string fallback = "")
        {
            if (obj != null && obj.TryGetValue(key, out string raw)) return raw;
            return fallback;
        }

        private static char Peek(string s, int i) { return i < s.Length ? s[i] : '\0'; }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException("JSON 期望 '" + c + "'，位置 " + i);
            i++;
        }

        private static string ReadString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("JSON 字符串未闭合");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("JSON 转义未结束");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON \\u 转义不完整");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("JSON 非法转义 \\" + e);
                }
            }
        }

        private static string ReadLiteral(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ' ' &&
                   s[i] != '\r' && s[i] != '\n' && s[i] != '\t') i++;
            if (i == start) throw new FormatException("JSON 值缺失，位置 " + start);
            string lit = s.Substring(start, i - start);
            if (lit == "true" || lit == "false" || lit == "null") return lit;
            if (double.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return lit;
            throw new FormatException("JSON 非法字面量：" + lit);
        }
    }
}
