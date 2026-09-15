using System;
using System.Collections.Generic;
using System.Globalization;

namespace PvzShared
{
    public enum CoinAction
    {
        None = 0,
        SetToLockedValue = 1
    }

    public struct ProgressValues
    {
        public int Maoxian;
        public int Ifa;
        public int Snow;
        public int Wujin;
        public int Wujin2;
        public int Wujin3;
        public int Wujin2Last;
    }

    /// <summary>
    /// 纯逻辑层：不依赖 Unity，可被测试运行器直接验证。
    /// 所有「容易写错的判断」都集中在这里，游戏内那一侧只留胶水代码。
    /// </summary>
    public static class ModLogic
    {
        public const float MinSpeed = 0.1f;
        public const float MaxSpeed = 5f;
        public const int CoinMax = 1000000000;   // 10 亿（int 上限 21.4 亿，留余量避免游戏自己加法溢出）
        public const int SunMax  = 1000000000;   // 10 亿

        // 实体属性/系数的取值范围（面板只给离散预设，这里做兜底夹取）
        public const float MinMul = 0f;
        public const float MaxMul = 20f;
        public const float MinScale = 0.3f;
        public const float MaxScale = 3f;

        /// <summary>夹取一个“倍率”类数值。</summary>
        public static float SanitizeMul(double v)
        {
            if (double.IsNaN(v)) return 1f;
            if (v < MinMul) return MinMul;
            if (v > MaxMul) return MaxMul;
            return (float)v;
        }

        /// <summary>夹取实体缩放（0 或负数会让物体直接消失，所以下限保护）。</summary>
        public static float SanitizeScale(double v)
        {
            if (double.IsNaN(v)) return 1f;
            if (v < MinScale) return MinScale;
            if (v > MaxScale) return MaxScale;
            return (float)v;
        }

        public const int ShelfSlots = 6;
        public const int ShelfEmptyId = 9999;

        /// <summary>
        /// 是否应把 Time.timeScale 改成目标速度。
        /// 关键规则：**current &lt;= 0 时绝不覆盖** —— 游戏用 timeScale = 0 表示暂停与失败界面
        /// （anniuClick.cs:627、shibai.cs:15），覆盖会破坏暂停。
        /// </summary>
        public static bool ShouldApplyTimeScale(float current, float desired, out float applied)
        {
            applied = desired;
            if (current <= 0f) return false;
            if (Math.Abs(current - desired) < 0.001f) return false;
            return true;
        }

        public static float SanitizeSpeed(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 1f;
            double clamped = v < MinSpeed ? MinSpeed : (v > MaxSpeed ? MaxSpeed : v);
            return (float)Math.Round(clamped, 2);
        }

        public static int SanitizeRarity(long v) { return (int)Clamp(v, -1, 5); }
        public static int SanitizeDifficulty(long v) { return (int)Clamp(v, 0, 2); }
        public static int SanitizeCoin(long v) { return (int)Clamp(v, 0, CoinMax); }
        public static int SanitizeSun(long v) { return (int)Clamp(v, 0, SunMax); }

        /// <summary>
        /// 货架配置里「这一格不改」的哨兵值。
        /// 必须与真实商品值分开：商品是 0..12，空格是 9999，所以 -1 不冲突。
        /// 之前面板把「留空」当成 9999 发下来，而 9999 在游戏里是「空格」——
        /// 于是留空 = 把货架格清成空格，用户看到的就 是「刷新空气」。
        /// </summary>
        public const int ShelfKeepId = -1;

        /// <summary>商店刷新按钮的固定花费（shangdianshuaxin.cs 里写死的 300）。</summary>
        public const int RefreshCost = 300;

        /// <summary>
        /// 货架落位。wanted 里的 ShelfKeepId(-1) 表示“这一格不改”，保留 target 原值。
        /// 返回真正写入的格数，并通过 skipped 回报被跳过的格数（诊断用）。
        ///
        /// 为什么单独抽出来：旧实现是 6 格无条件全覆盖，而面板把“留空不修改”也实现成 9999，
        /// 于是没点任何商品直接点「应用到货架」就把 6 格清成了空气（用户报的“刷新空气”）。
        ///
        /// 长度不等时只处理较短的那一段 —— 调用方（钩子）在上游已经校验过长度，
        /// 这里只是不让它因为越界而把游戏数组写坏。
        /// </summary>
        public static int ApplyShelfKeep(int[] target, int[] wanted, out int skipped)
        {
            skipped = 0;
            if (target == null || wanted == null) return 0;

            int written = 0;
            int n = target.Length < wanted.Length ? target.Length : wanted.Length;
            for (int i = 0; i < n; i++)
            {
                if (wanted[i] == ShelfKeepId) { skipped++; continue; }
                target[i] = wanted[i];
                written++;
            }
            return written;
        }

        /// <summary>
        /// 商店刷新的垫钱值。
        ///
        /// 游戏里 shangdianshuaxin.OnMouseDown 写死了两步：
        ///     if (gameData.coin >= 300) { ...; gameData.coin -= 300; ... }
        /// 300 是硬编码字面量，从外面改不了。做法是先把金币垫高 300，
        /// 游戏随后照常扣 300，净变化 0；顺带把 `coin >= 300` 那道门槛也过了 —— 没钱也能刷。
        ///
        /// 顶到 int 上限就不垫：垫了会溢出成负数，比“不免费”糟糕得多。
        /// （这种身家的存档本来也不差这 300。）
        /// </summary>
        public static int PrepareFreeRefresh(int coin)
        {
            return coin <= int.MaxValue - RefreshCost ? coin + RefreshCost : coin;
        }

        /// <summary>
        /// “让四种卡包 + 叶子保护伞都能进货架随机池”时，五个排除标记应该取的值。
        ///
        /// 【语义是反的，必须钉死在一处】shangdian.shopChouka 的抽签循环：
        ///     while (flag7) {                          // flag7 = true 表示“这一格重抽”
        ///         num2 = Random.Range(0, shopItem.Length);
        ///         flag7 = false;
        ///         if (flag3 &amp;&amp; num2 == 8)  flag7 = true;   // liekabao     为真 → 排除商品 8（劣质卡包）
        ///         if (flag  &amp;&amp; num2 == 9)  flag7 = true;   // canbaohusan 为真 → 排除 9（叶子保护伞）
        ///         if (flag4 &amp;&amp; num2 == 10) flag7 = true;   // ptkabao     为真 → 排除 10（普通卡包）
        ///         if (flag5 &amp;&amp; num2 == 11) flag7 = true;   // xykabao     为真 → 排除 11（稀有卡包）
        ///         if (flag6 &amp;&amp; num2 == 12) flag7 = true;   // sskabao     为真 → 排除 12（史诗卡包）
        ///     }
        /// 也就是 true = “已买过 → 别再刷出来了”（购买时游戏自己置 true）。
        /// 所以“全部可刷” = 全部 false。
        ///
        /// 曾经照设计文档写成 true，等于把五种卡包/道具全踢出货架 —— 比默认状态还差。
        /// 这个函数存在的唯一目的就是把那条反直觉的事实钉在一处，并且可测。
        ///
        /// 返回顺序（调用方按此下标赋值）：[0]=liekabao [1]=ptkabao [2]=xykabao [3]=sskabao [4]=canbaohusan。
        /// 因为五个值全是 false，下标顺序对正确性其实没有影响；这里保持顺序只是为了可读性，
        /// 并且每次返回**新数组**，避免调用方改了元素污染下一次调用。
        /// </summary>
        public static bool[] PackFlagsForAllAvailable()
        {
            return new[] { false, false, false, false, false };
        }

        /// <summary>
        /// 解析货架配置：“0,1,5,8,10,11”（恰好 6 个整数）。
        ///   -1    = 这一格不改（保持游戏原样）
        ///   0..12 = 指定商品
        ///   9999  = 这一格留空（游戏自己的空格标记）
        /// </summary>
        public static bool TryParseShelf(string text, out int[] ids, out string error)
        {
            ids = null;
            error = null;

            if (text == null || text.Trim().Length == 0)
            {
                error = "货架配置为空";
                return false;
            }

            string[] parts = text.Split(',');
            if (parts.Length != ShelfSlots)
            {
                error = "货架必须是 6 个商品 id（当前 " + parts.Length + " 个）";
                return false;
            }

            var parsed = new int[ShelfSlots];
            for (int i = 0; i < ShelfSlots; i++)
            {
                string p = parts[i].Trim();
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    error = "第 " + (i + 1) + " 个不是整数：" + p;
                    return false;
                }
                if (id < ShelfKeepId || id > ShelfEmptyId)
                {
                    error = "第 " + (i + 1) + " 个超出范围（-1 = 不改，0..12 = 商品，9999 = 空格）：" + id;
                    return false;
                }
                parsed[i] = id;
            }

            ids = parsed;
            return true;
        }

        public static int[] AppendUnique(int[] arr, int id)
        {
            if (arr == null) return new[] { id };
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] == id) return arr;
            }
            var copy = new int[arr.Length + 1];
            Array.Copy(arr, copy, arr.Length);
            copy[arr.Length] = id;
            return copy;
        }

        public static int[] AppendUniqueMany(int[] arr, IEnumerable<int> ids)
        {
            int[] result = arr;
            if (result == null) result = new int[0];
            if (ids == null) return result;
            foreach (int id in ids) result = AppendUnique(result, id);
            return result;
        }

        public static ProgressValues MaxProgress()
        {
            return new ProgressValues
            {
                Maoxian = 49,      // Wins.wuqiong = 49，冒险模式最后一关
                Ifa = 100,         // 关卡编号 500..599 段
                Snow = 100,        // 关卡编号 600..699 段
                Wujin = 99,
                Wujin2 = 42,       // 由 "6 + wujinceng2 - 1 超过 47 就钳到 47" 反推
                Wujin3 = 99,
                Wujin2Last = 42
            };
        }

        /// <summary>商品 id 8=随机劣卡包、10=普通、11=稀有、12=史诗；9 是破烂叶子保护伞（不是卡包）。</summary>
        public static bool IsCardPackId(int itemId)
        {
            return itemId == 8 || itemId == 10 || itemId == 11 || itemId == 12;
        }

        public static CoinAction DecideCoin(bool lockCoin, bool freeGacha, bool isCardPack)
        {
            if (!lockCoin) return CoinAction.None;
            if (freeGacha && isCardPack) return CoinAction.None;
            return CoinAction.SetToLockedValue;
        }

        private static long Clamp(long v, long min, long max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
