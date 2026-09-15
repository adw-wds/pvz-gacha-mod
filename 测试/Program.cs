using System;
using System.Collections.Generic;

namespace PvzTests
{
    /// <summary>无第三方框架的迷你断言器：失败只记录、不中断，最后汇总。</summary>
    internal static class T
    {
        public static int Failed;
        public static int Passed;

        public static void Case(string name, Action body)
        {
            try
            {
                body();
                Passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception ex)
            {
                Failed++;
                Console.WriteLine("  FAIL  " + name + "  ->  " + ex.Message);
            }
        }

        public static void Eq<T>(T expect, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expect, actual))
                throw new Exception("期望 <" + expect + ">，实际 <" + actual + ">");
        }

        public static void True(bool cond, string why = "")
        {
            if (!cond) throw new Exception("断言失败" + (why.Length > 0 ? "：" + why : ""));
        }

        public static void Contains(string haystack, string needle)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new Exception("未找到 <" + needle + ">，实际内容：<" + haystack + ">");
        }
    }

    internal static class Program
    {
        private static void Main()
        {
            JsonTests.Run();
            LogicTests.Run();
            CatalogTests.Run();
            ScriptTests.Run();
            ChannelTests.Run();
            PanelTests.Run();

            Console.WriteLine();
            Console.WriteLine("通过 " + T.Passed + " 项，失败 " + T.Failed + " 项");
            Environment.Exit(T.Failed == 0 ? 0 : 1);
        }
    }
}
