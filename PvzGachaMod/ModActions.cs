using System;
using System.Collections.Generic;
using System.Globalization;
using PvzShared;
using UnityEngine;

namespace PvzGachaMod
{
    /// <summary>一次性的重动作（解锁/拉满/给钱/秒杀…）。由命令文件里的 action 字段触发。</summary>
    internal static class ModActions
    {
        internal static string Handle(string action, string value)
        {
            try
            {
                switch (action)
                {
                    case "unlock.allPlants":  return UnlockAllPlants();
                    case "unlock.allPacks":   return UnlockAllPacks();
                    case "progress.max":      return MaxProgress();
                    case "set.difficulty":    return SetDifficulty(value);
                    case "shop.shelf":        return SetShelf(value);
                    case "battle.killAll":    return KillAllZombies();
                    case "save.clearCheater": return ClearCheaterFlag();
                    case "save.giveMoney":    return GiveMoney(value);
                    case "battle.sunFull":    return SunFull();
                    case "settings.reset":    return ResetSettings();
                    default:                  return Err("未知动作：" + action);
                }
            }
            catch (Exception ex)
            {
                ModRuntime.LogOnce("动作-" + action, ex);
                return Err("动作执行异常：" + ex.Message);
            }
        }

        private static string Ok(string detail) { return "{\"ok\":true,\"detail\":" + MiniJson.Quote(detail) + "}"; }
        private static string Err(string message) { return "{\"ok\":false,\"error\":" + MiniJson.Quote(message) + "}"; }

        /// <summary>把所有开关恢复成默认值（并让 mod 把新设置写回文件）。</summary>
        private static string ResetSettings()
        {
            ModRuntime.Settings.ResetToDefaults();
            return Ok("已恢复默认设置");
        }

        /// <summary>立刻把关卡内阳光拉满（不用等锁定周期）。</summary>
        private static string SunFull()
        {
            First first = FindFirst();
            if (first == null) return Err("没找到关卡对象（请先进关卡）");

            first.Sun = ModLogic.SunMax;
            return Ok("阳光已设为 10 亿");
        }

        /// <summary>
        /// 植物全集 = 6 档稀有度池并集 + 8 个特殊 id + 游戏默认的 3 个
        /// + 运行时卡牌表长度推导出的 1..(卡牌数-1)。
        ///
        /// 卡牌总数怎么拿（它才是“图鉴里到底有多少张卡”的权威来源）：
        ///   `guanqiaStart.cards` —— 图鉴/选关里都在（public，且**按植物 id 索引**，
        ///                           `Instantiate(cards[plantId])` 可证）
        ///   `First.plantId`     —— 关卡内才有
        /// 两个都试、取大值。都要不到就只能靠稀有度池拼（会漏掉不在任何池里的卡）。
        /// </summary>
        internal static int[] PlantUniverse(out int fromRuntime)
        {
            var set = new HashSet<int>();
            fromRuntime = 0;

            int maxId = 0;
            try
            {
                guanqiaStart g = UnityEngine.Object.FindObjectOfType<guanqiaStart>();
                if (g != null && g.cards != null && g.cards.Length > maxId) maxId = g.cards.Length;
            }
            catch (Exception ex) { ModRuntime.LogOnce("读取卡牌表(guanqiaStart)", ex); }

            // 兜底：对象被 SetActive(false) 时 FindObjectOfType 找不到，但 Find 找得到
            // （游戏自己的 First.cs:1805 就是这么拿的）
            if (maxId == 0)
            {
                try
                {
                    GameObject canvasCard = GameObject.Find("CanvasCard");
                    if (canvasCard != null)
                    {
                        guanqiaStart g2 = canvasCard.GetComponent<guanqiaStart>();
                        if (g2 != null && g2.cards != null) maxId = g2.cards.Length;
                    }
                }
                catch (Exception ex) { ModRuntime.LogOnce("读取卡牌表(CanvasCard)", ex); }
            }

            try
            {
                First first = FindFirst();
                if (first != null && first.plantId != null && first.plantId.Length > maxId) maxId = first.plantId.Length;
            }
            catch (Exception ex) { ModRuntime.LogOnce("读取植物表长度", ex); }

            if (maxId > 0)
            {
                for (int id = 1; id < maxId; id++)
                {
                    set.Add(id);
                    fromRuntime++;
                }
            }

            // 枚举真实池子必须绕过自己的档位钩子，否则六档被串成同一档，全集塔缩。
            Hooks.BypassTierOverride = true;
            try
            {
                for (int tier = 0; tier <= 5; tier++)
                {
                    try
                    {
                        int[] pool = Wins.getXiyouGroup(tier);
                        if (pool == null) continue;
                        foreach (int id in pool) set.Add(id);
                    }
                    catch (Exception ex) { ModRuntime.LogOnce("读取稀有度池", ex); }
                }
            }
            finally { Hooks.BypassTierOverride = false; }

            foreach (int special in new[] { 17, 20, 25, 33, 41, 141, 146, 150 }) set.Add(special);
            foreach (int baseline in new[] { 0, 1, 5 }) set.Add(baseline);

            var list = new List<int>();
            foreach (int id in set) if (id > 0 && id < 1000) list.Add(id);
            list.Sort();
            return list.ToArray();
        }

        private static string UnlockAllPlants()
        {
            int[] universe = PlantUniverse(out int fromRuntime);
            if (universe.Length == 0) return Err("无法推导植物全集（请先进入一关让游戏加载植物表）");

            int before = 0;
            if (!SaveData.Mutate(d =>
                {
                    before = d.scores != null ? d.scores.Length : 0;
                    d.scores = ModLogic.AppendUniqueMany(d.scores, universe);
                }, out string error))
            {
                return Err(error);
            }

            GameStart.GameData after = SaveData.TryLoad();
            int now = after != null && after.scores != null ? after.scores.Length : 0;

            // 图鉴里的卡牌是在各自 Start() 里读一次 scores 决定要不要盖「未解锁」遮罩的，
            // 不会自动重读。所以人正站在图鉴里时重载一次场景，让结果立刻可见。
            string reloaded = "";
            try
            {
                UnityEngine.SceneManagement.Scene sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (sc.name == "植物图鉴" || sc.name == "僵尸图鉴")
                {
                    UnityEngine.SceneManagement.SceneManager.LoadScene(sc.name);
                    reloaded = "，图鉴已重新载入";
                }
            }
            catch (Exception ex) { ModRuntime.LogOnce("重载图鉴", ex); }

            // 没拿到卡牌表时，"全集"只能靠稀有度池拼，会漏掉不在任何池里的卡（实测漏 47 号）。
            // 这种不完整的结果必须说出来，否则用户看到"图鉴里还有一张锁着"只会认为功能坏了。
            string tip = fromRuntime > 0
                ? ""
                : "；未读到运行时卡牌表，可能有漏卡 —— 在「植物图鉴」里再点一次即可拿到完整卡牌表";

            return Ok("植物全集 " + universe.Length + " 个（运行时卡牌表贡献 " + fromRuntime +
                      " 个），已解锁 " + before + " → " + now + reloaded + tip);
        }

        /// <summary>
        /// 卡包全解锁。
        ///
        /// 【重要】这五个标记是 `shangdian.shopChouka` 的**排除条件**，不是解锁条件。
        /// 看它的抽签循环：
        ///     while (flag7) {                       // flag7 = true 表示「这一格重抽」
        ///         num2 = Random.Range(0, shopItem.Length);
        ///         if (flag3 &amp;&amp; num2 == 8)  flag7 = true;   // liekabao 为真 → 排除商品 8
        ///         if (flag  &amp;&amp; num2 == 9)  flag7 = true;   // canbaohusan 为真 → 排除 9
        ///         if (flag4 &amp;&amp; num2 == 10) flag7 = true;
        ///         if (flag5 &amp;&amp; num2 == 11) flag7 = true;
        ///         if (flag6 &amp;&amp; num2 == 12) flag7 = true;
        ///     }
        /// 也就是 true = 「已买过 → 别再刷出来了」（购买时游戏自己置 true）。
        /// 所以「让所有卡包类型都能刷进货架」必须置 **false**。
        /// （设计文档里写的「置 true」是错的，这里按 shangdian.shopChouka 实际代码纠正。）
        ///
        /// 清 `shangdianYishou` 是顺带抹掉「已售」标记；
        /// 最后重抽一次货架，在商店里能立刻看到效果（不在商店时自然跳过）。
        /// </summary>
        internal static string UnlockAllPacks()
        {
            // 取值集中在 ModLogic.PackFlagsForAllAvailable（纯函数、可离线单测），
            // 免得“排除条件”这条反直觉语义散落在调用点里再被写反。
            bool[] flags = ModLogic.PackFlagsForAllAvailable();

            bool ok = SaveData.Mutate(d =>
            {
                d.liekabao = flags[0];
                d.ptkabao = flags[1];
                d.xykabao = flags[2];
                d.sskabao = flags[3];
                d.canbaohusan = flags[4];
                d.shangdianYishou = new bool[ModLogic.ShelfSlots];
            }, out string error);

            if (!ok) return Err(error);

            string extra = "";
            try
            {
                shangdian shop = UnityEngine.Object.FindObjectOfType<shangdian>();
                if (shop != null)
                {
                    shop.shuaxinHuojia();      // 用新池子重抽，不用退出去再进
                    extra = "，货架已立即重抽";
                }
            }
            catch (Exception ex) { ModRuntime.LogOnce("解锁后重抽货架", ex); }

            return Ok("五种卡包/道具都已回到货架随机池，已售标记已清空" + extra);
        }

        private static string MaxProgress()
        {
            ProgressValues p = ModLogic.MaxProgress();
            bool ok = SaveData.Mutate(d =>
            {
                d.Maoxian = p.Maoxian;
                d.MaoxianIFA = p.Ifa;
                d.MaoxianSnow = p.Snow;
                d.wujinceng = p.Wujin;
                d.wujinceng2 = p.Wujin2;
                d.wujinceng3 = p.Wujin3;
                d.wujinceng2Last = p.Wujin2Last;
            }, out string error);

            return ok
                ? Ok("冒险 " + p.Maoxian + " / IFA " + p.Ifa + " / 雪地 " + p.Snow +
                     " / 无尽 " + p.Wujin + "," + p.Wujin2 + "," + p.Wujin3 + " 已拉满")
                : Err(error);
        }

        private static string SetDifficulty(string value)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
                return Err("难度需要 0/1/2，收到：" + value);

            int diff = ModLogic.SanitizeDifficulty(raw);
            bool ok = SaveData.Mutate(d => d.Difficulty = diff, out string error);
            return ok ? Ok("难度已设为 " + diff + "（重进关卡后生效）") : Err(error);
        }

        private static string SetShelf(string value)
        {
            if (!ModLogic.TryParseShelf(value, out int[] ids, out string parseError)) return Err(parseError);

            // 存档里的 shangdian 是**真实商品数组**，绝不能留「不改」哨兵 -1 ——
            // 游戏拿到 -1 会当成未知商品。所以先把当前货架读出来，
            // 把标了 -1 的格子换成它原本的值（效果等同于"不改"）；
            // 万一读不到存档，兜底成「空格」也比写 -1 强。
            GameStart.GameData cur = SaveData.TryLoad();
            int[] shelfNow = cur != null ? cur.shangdian : null;

            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] != ModLogic.ShelfKeepId) continue;
                ids[i] = (shelfNow != null && i < shelfNow.Length) ? shelfNow[i] : ModLogic.ShelfEmptyId;
            }

            ModRuntime.Settings.Shelf = string.Join(",", ToStrings(ids));
            bool ok = SaveData.Mutate(d => d.shangdian = ids, out string error);
            return ok ? Ok("货架已设为 " + string.Join(",", ToStrings(ids)) + "（回到商店即可看到）") : Err(error);
        }

        private static string[] ToStrings(int[] ids)
        {
            var result = new string[ids.Length];
            for (int i = 0; i < ids.Length; i++) result[i] = ids[i].ToString(CultureInfo.InvariantCulture);
            return result;
        }

        private static string KillAllZombies()
        {
            int n = 0;
            try
            {
                Zombie[] zombies = UnityEngine.Object.FindObjectsOfType<Zombie>();
                if (zombies != null)
                {
                    foreach (Zombie z in zombies)
                    {
                        if (z == null || z.ZombieHp <= 0) continue;
                        z.TakeDamage(9999999, 1);
                        n++;
                    }
                }
            }
            catch (Exception ex) { ModRuntime.LogOnce("秒杀", ex); }

            return Ok("已秒杀 " + n + " 个僵尸");
        }

        private static string ClearCheaterFlag()
        {
            try { First.ZUOBIZHE = false; }
            catch (Exception ex) { ModRuntime.LogOnce("清作弊标记", ex); }

            GameStart.GameData data = SaveData.TryLoad();
            if (data == null) return Err("找不到存档，无法补写哈希");
            if (!SaveData.Save(data, out string error)) return Err(error);

            return Ok("作弊标记已清除，哈希已补写");
        }

        private static string GiveMoney(string value)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
                return Err("金币值需要整数，收到：" + value);

            int coin = ModLogic.SanitizeCoin(raw);
            bool ok = SaveData.Mutate(d => d.coin = coin, out string error);
            return ok ? Ok("金币已设为 " + coin) : Err(error);
        }

        /// <summary>当前场景里的 First（没有就返回 null）。</summary>
        internal static First FindFirst()
        {
            First[] arr = UnityEngine.Object.FindObjectsOfType<First>();
            return arr != null && arr.Length > 0 ? arr[0] : null;
        }
    }
}
