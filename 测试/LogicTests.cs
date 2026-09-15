using System;
using PvzShared;

namespace PvzTests
{
    internal static class LogicTests
    {
        public static void Run()
        {
            Console.WriteLine("ModLogic / 速度门控");

            T.Case("暂停（timeScale=0）时绝不覆盖，保护游戏暂停与失败界面", () =>
            {
                T.True(!ModLogic.ShouldApplyTimeScale(0f, 3f, out float a), "0 不应被覆盖");
                T.Eq(3f, a);
            });

            T.Case("已经在目标速度时不做无意义赋值", () =>
                T.True(!ModLogic.ShouldApplyTimeScale(2f, 2f, out _)));

            T.Case("正常奔跑中才把速度改成目标值", () =>
            {
                T.True(ModLogic.ShouldApplyTimeScale(1f, 2.5f, out float applied));
                T.Eq(2.5f, applied);
            });

            T.Case("速度清洗：夹取到 0.1..5，NaN 回落到 1", () =>
            {
                T.Eq(0.1f, ModLogic.SanitizeSpeed(0));
                T.Eq(0.1f, ModLogic.SanitizeSpeed(-3));   // 低于下限夹到下限（不跳到 1）
                T.Eq(5f, ModLogic.SanitizeSpeed(99));
                T.Eq(2.5f, ModLogic.SanitizeSpeed(2.5));
                T.Eq(1f, ModLogic.SanitizeSpeed(double.NaN));
            });

            Console.WriteLine("ModLogic / 数值清洗");

            T.Case("稀有度夹取到 -1..5", () =>
            {
                T.Eq(-1, ModLogic.SanitizeRarity(-9));
                T.Eq(5, ModLogic.SanitizeRarity(9));
                T.Eq(3, ModLogic.SanitizeRarity(3));
            });

            T.Case("难度夹取到 0..2", () =>
            {
                T.Eq(0, ModLogic.SanitizeDifficulty(-1));
                T.Eq(2, ModLogic.SanitizeDifficulty(7));
            });

            T.Case("金币/阳光上限夹取到 10 亿", () =>
            {
                T.Eq(0, ModLogic.SanitizeCoin(-5));
                T.Eq(1000000000, ModLogic.SanitizeCoin(9999999999));
                T.Eq(1000000000, ModLogic.SanitizeCoin(1000000000));
                T.Eq(1000000000, ModLogic.SanitizeSun(9999999999));
                T.Eq(12345, ModLogic.SanitizeSun(12345));
            });

            Console.WriteLine("ModLogic / 货架解析");

            T.Case("合法 6 格货架解析成功（9999 表示空格）", () =>
            {
                T.True(ModLogic.TryParseShelf("0,1,5,8,10,11", out int[] ids, out string err), err);
                T.Eq(6, ids.Length);
                T.Eq(0, ids[0]);
                T.Eq(11, ids[5]);

                T.True(ModLogic.TryParseShelf("9999,9999,9999,9999,9999,9999", out int[] empty, out _));
                T.Eq(9999, empty[0]);
            });

            T.Case("允许空格分隔", () =>
            {
                T.True(ModLogic.TryParseShelf(" 0 , 1 , 5 , 8 , 10 , 11 ", out int[] ids, out string err), err);
                T.Eq(6, ids.Length);
            });

            // 回归守卫：以前 UI 把「留空」和游戏的「空格」当成同一个 9999，
            // 于是没点任何商品就点「应用到货架」→ 6 格全被清成空格（用户看到的「刷新空气」）。
            // 现在「不改」是独立的 -1 哨兵。
            T.Case("货架支持「不改」哨兵 -1，且与「空格」9999 是两个不同的值", () =>
            {
                T.True(ModLogic.ShelfKeepId < 0, "「不改」必须是负数，才能和 0..12 的商品区分");
                T.True(ModLogic.ShelfKeepId != 9999, "「不改」绝不能等于游戏的空格标记 9999");

                T.True(ModLogic.TryParseShelf("-1,-1,8,10,-1,9999", out int[] ids, out string err), err);
                T.Eq(-1, ids[0]);       // 不改
                T.Eq(8, ids[2]);        // 指定商品
                T.Eq(9999, ids[5]);     // 真的是空格
            });

            T.Case("比「不改」更小的值仍然要被拒绝", () =>
            {
                T.True(!ModLogic.TryParseShelf("-2,0,0,0,0,0", out _, out string e));
                T.Contains(e, "不改");     // 报错文案要说清合法范围，用户才知道 -1 能用
            });

            T.Case("格数不对/非数字/越界/空串 全部拒绝并给出中文原因", () =>
            {
                T.True(!ModLogic.TryParseShelf("0,1,2", out _, out string e1));
                T.Contains(e1, "6");
                T.True(!ModLogic.TryParseShelf("a,b,c,d,e,f", out _, out _));
                T.True(!ModLogic.TryParseShelf("0,1,2,3,4,10000", out _, out string e3));
                T.True(e3.Length > 0, "越界要有原因");
                T.True(!ModLogic.TryParseShelf("", out _, out _));
                T.True(!ModLogic.TryParseShelf(null, out _, out _));
            });

            Console.WriteLine("ModLogic / 商店三功能内核");

            T.Case("货架落位：「不改」(-1) 必须保留原值，其余照写", () =>
            {
                int[] target = { 9999, 9999, 9999, 9999, 9999, 9999 };
                int[] wanted = { 8, ModLogic.ShelfKeepId, ModLogic.ShelfKeepId,
                                 ModLogic.ShelfKeepId, ModLogic.ShelfKeepId, ModLogic.ShelfKeepId };

                int written = ModLogic.ApplyShelfKeep(target, wanted, out int skipped);

                T.Eq(1, written);
                T.Eq(5, skipped);
                T.Eq(8, target[0]);
                for (int i = 1; i < 6; i++)
                    T.Eq(9999, target[i]);          // 原值保住了，没被 -1 污染
            });

            T.Case("货架落位：全「不改」时不写任何格，也不掩盖原值", () =>
            {
                int[] target = { 0, 1, 2, 3, 4, 5 };
                int[] wanted = { -1, -1, -1, -1, -1, -1 };

                int written = ModLogic.ApplyShelfKeep(target, wanted, out int skipped);

                T.Eq(0, written);
                T.Eq(6, skipped);
                for (int i = 0; i < 6; i++) T.Eq(i, target[i]);
            });

            T.Case("货架落位：-1 绝不能进入 target（游戏 Instantiate(cards[-1]) 会抛异常）", () =>
            {
                int[] target = { 3, 3, 3, 3, 3, 3 };
                int[] wanted = { -1, 8, -1, 8, -1, 8 };

                ModLogic.ApplyShelfKeep(target, wanted, out _);

                foreach (int v in target)
                    T.True(v != ModLogic.ShelfKeepId, "存档数组里出现了 -1 哨兵");
            });

            T.Case("货架落位：null 与空数组不炸", () =>
            {
                T.Eq(0, ModLogic.ApplyShelfKeep(null, new[] { 1 }, out _));
                T.Eq(0, ModLogic.ApplyShelfKeep(new[] { 1 }, null, out _));
                T.Eq(0, ModLogic.ApplyShelfKeep(new int[0], new int[0], out _));
            });

            T.Case("刷新垫钱：正常情况 +300（游戏随后扣 300，净变化 0）", () =>
            {
                T.Eq(300, ModLogic.PrepareFreeRefresh(0));       // 没钱也能刷，顺带过掉门槛
                T.Eq(599, ModLogic.PrepareFreeRefresh(299));     // 299 < 300，垫完刚好够
                T.Eq(1300, ModLogic.PrepareFreeRefresh(1000));
                T.Eq(1000000300, ModLogic.PrepareFreeRefresh(1000000000));  // 10 亿远没到顶
            });

            T.Case("刷新垫钱：顶到 int 上限就不垫（垫了会溢出成负数）", () =>
            {
                // 判据是 coin <= int.MaxValue - 300 才垫，所以恰好在这个边界上还能垫满。
                // 走查计划里我先把中间两条的期望写反了，实现是对的，改测试。
                T.Eq(int.MaxValue, ModLogic.PrepareFreeRefresh(int.MaxValue));            // 不垫（垫了溢出）
                T.Eq(int.MaxValue - 100, ModLogic.PrepareFreeRefresh(int.MaxValue - 100)); // 不垫（2147483547 > 2147483347）
                T.Eq(int.MaxValue, ModLogic.PrepareFreeRefresh(int.MaxValue - 300));       // 刚好还能垫满
                T.Eq(int.MaxValue - 1, ModLogic.PrepareFreeRefresh(int.MaxValue - 301));   // 垫完 = Max-1
            });

            T.Case("卡包全解锁：五个排除标记必须全 false", () =>
            {
                // 这条守的是 shopChouka 里那条反直觉语义：标记 true 表示“已买过 → 重抽掉这一格”，
                // 所以把它当“解锁条件”置 true 会把五种卡包/道具全踢出货架，比默认还差。
                bool[] f = ModLogic.PackFlagsForAllAvailable();

                T.Eq(5, f.Length);
                foreach (bool b in f)
                    T.True(!b, "置 true 会让 shopChouka 重抽掉这一格，把卡包踢出货架");
            });

            T.Case("卡包全解锁：每次返回独立数组，调用方改了不污染下一次", () =>
            {
                // 五个值全是 false，所以下标顺序对正确性没有影响 —— 这条不去验证（验证不了）。
                // 真正会出事的是“返回共享数组”：调用方改一个元素，下一次调用就跟着变。
                bool[] a = ModLogic.PackFlagsForAllAvailable();
                a[0] = true;                                        // 调用方乱改
                a[3] = true;

                bool[] b = ModLogic.PackFlagsForAllAvailable();
                T.True(!b[0], "返回的是共享数组，被上一次的调用方改坏了");
                T.True(!b[3], "返回的是共享数组，被上一次的调用方改坏了");
            });

            Console.WriteLine("ModLogic / 解锁、进度与金币");

            T.Case("解锁追加：去重、扩容、null 安全", () =>
            {
                T.Eq("1,2", string.Join(",", ModLogic.AppendUnique(new[] { 1, 2 }, 2)));
                T.Eq("1,2,3", string.Join(",", ModLogic.AppendUnique(new[] { 1, 2 }, 3)));
                T.Eq("5", string.Join(",", ModLogic.AppendUnique(null, 5)));
                T.Eq("5", string.Join(",", ModLogic.AppendUnique(new int[0], 5)));
                T.Eq("1,2,3,4", string.Join(",", ModLogic.AppendUniqueMany(new[] { 1, 2 }, new[] { 2, 3, 4 })));
            });

            T.Case("进度拉满常量被锁定（改动必须同步改测试与文档）", () =>
            {
                ProgressValues p = ModLogic.MaxProgress();
                T.Eq(49, p.Maoxian);
                T.Eq(100, p.Ifa);
                T.Eq(100, p.Snow);
                T.Eq(99, p.Wujin);
                T.Eq(42, p.Wujin2);
                T.Eq(99, p.Wujin3);
                T.Eq(42, p.Wujin2Last);
            });

            T.Case("卡包 id 判定：8/10/11/12 是卡包，9 不是", () =>
            {
                T.True(ModLogic.IsCardPackId(8));
                T.True(ModLogic.IsCardPackId(10));
                T.True(ModLogic.IsCardPackId(11));
                T.True(ModLogic.IsCardPackId(12));
                T.True(!ModLogic.IsCardPackId(9));
                T.True(!ModLogic.IsCardPackId(0));
            });

            T.Case("金币回补决策表", () =>
            {
                T.Eq(CoinAction.None, ModLogic.DecideCoin(false, false, false));
                T.Eq(CoinAction.SetToLockedValue, ModLogic.DecideCoin(true, false, false));
                T.Eq(CoinAction.SetToLockedValue, ModLogic.DecideCoin(true, true, false));
                T.Eq(CoinAction.None, ModLogic.DecideCoin(true, true, true));
                T.Eq(CoinAction.None, ModLogic.DecideCoin(false, true, true));
            });
        }
    }
}
