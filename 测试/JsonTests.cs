using System;
using System.Collections.Generic;
using PvzShared;

namespace PvzTests
{
    internal static class JsonTests
    {
        public static void Run()
        {
            Console.WriteLine("MiniJson");

            T.Case("转义引号与反斜杠", () =>
                T.Eq("a\\\"b\\\\c", MiniJson.Escape("a\"b\\c")));

            T.Case("解析扁平对象", () =>
            {
                Dictionary<string, string> o = MiniJson.ParseFlatObject("{\"key\":\"gacha.free\",\"value\":true}");
                T.Eq("gacha.free", o["key"]);
                T.Eq("true", o["value"]);
            });

            T.Case("解析数字与负数", () =>
            {
                Dictionary<string, string> o = MiniJson.ParseFlatObject("{\"value\":-1,\"n\":3.5}");
                T.True(MiniJson.TryGetLong(o, "value", out long v));
                T.Eq(-1L, v);
                T.True(MiniJson.TryGetDouble(o, "n", out double d));
                T.Eq(3.5, d);
            });

            T.Case("解析含中文与转义的字符串", () =>
            {
                Dictionary<string, string> o = MiniJson.ParseFlatObject("{\"label\":\"抽卡\\\"免费\\\"\"}");
                T.Eq("抽卡\"免费\"", o["label"]);
            });

            T.Case("解析带空格的 JSON", () =>
            {
                Dictionary<string, string> o = MiniJson.ParseFlatObject("{ \"key\" : \"a\" , \"value\" : 1 }");
                T.Eq("a", o["key"]);
                T.Eq("1", o["value"]);
            });

            T.Case("非法 JSON 抛异常", () =>
            {
                bool threw = false;
                try { MiniJson.ParseFlatObject("{oops"); } catch { threw = true; }
                T.True(threw, "应当抛异常");
            });

            T.Case("输出转义：中文原样、引号被转义", () =>
            {
                T.Eq("\"抽卡免费\"", MiniJson.Quote("抽卡免费"));
                T.Contains(MiniJson.Quote("a\"b"), "\\\"");
            });
        }
    }
}
