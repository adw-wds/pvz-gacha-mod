using System;
using PvzShared;

namespace PvzGachaMod
{
    /// <summary>
    /// 拦截类钩子。这些方法会被「注入器」在编译期以 IL 形式插进 Assembly-CSharp.dll 调用。
    /// 因此：
    ///   · 必须是 public static
    ///   · 参数只能是「目标方法的 this / 值参数 / 引用参数 / 数组参数」
    ///   · 必须整体 try/catch —— 绝不能让异常抛回游戏
    ///
    /// 注入规则见 源码\注入器\Program.cs 的 Rules 表。
    /// </summary>
    public static class Hooks
    {
        private static int _savedCost = -1;

        /// <summary>
        /// 内部枚举「真实稀有度池」时的旁路开关。
        ///
        /// 为何需要：`ModActions.PlantUniverse()` 靠 `Wins.getXiyouGroup(0..5)` 枚举六档池子
        /// 来拼出「植物全集」，而这个方法正是我们自己注入 OverrideTier 的地方 ——
        /// 只要用户设了抽卡档位（实测是 4 档），六次调用会**全部返回同一档**，
        /// 「全集」塔缩成那一档 + 几个特殊 id（**实测只剩 29 个**），
        /// 而这 29 个早就解锁了 → 于是「解锁全部卡牌」永远看不出任何变化。
        /// 枚举期间置真，让钩子放行原始档位。
        /// </summary>
        internal static bool BypassTierOverride;

        /// <summary>
        /// 安全取设置。钩子可能在 ModRuntime.Awake 之前就被游戏调用（场景加载时预计算卡池），
        /// 所以不能假定 Settings 已就绪 —— 直接改写为静态初始化并在这里兵底。
        /// </summary>
        private static ModSettings S
        {
            get { return ModRuntime.Settings ?? (ModRuntime.Settings = new ModSettings()); }
        }

        /// <summary>
        /// 插在 Wins.getXiyouGroup(int state) 开头：把档位参数就地改掉，
        /// 于是原方法自然返回我们想要的稀有度池（无需处理返回值）。
        /// 稀有度 6 档：0..5（Wins.cs:352）。
        /// </summary>
        public static void OverrideTier(ref int state)
        {
            try
            {
                // 我们自己在枚举真实池子，放行。连计数都不要（否则诊断页的 tierCalls 会虚高）
                if (BypassTierOverride) return;

                ModDiag.TierCalls++;
                int want = S.Rarity;
                if (want < 0) return;
                if (state != want) { state = want; ModDiag.TierApplied++; }
            }
            catch (Exception ex) { ModDiag.TierErrors++; ModRuntime.LogOnce("必出稀有度", ex); }
        }

        /// <summary>
        /// 插在 shangdian.buhuo(int[] shop_List, GameStart.GameData data) 开头：
        /// 货架数组长度固定 6，可以**就地改元素**，从而改写货架商品（不用处理返回值）。
        /// </summary>
        public static void OverrideShelfInPlace(int[] list)
        {
            try
            {
                ModDiag.ShelfCalls++;
                string text = S.Shelf;
                if (string.IsNullOrEmpty(text)) return;
                if (list == null) { ModDiag.ShelfErrors++; return; }

                if (!ModLogic.TryParseShelf(text, out int[] ids, out string err))
                {
                    ModDiag.ShelfErrors++;
                    ModDiag.Note("货架格式不对：" + err);
                    return;
                }
                if (list.Length != ids.Length)
                {
                    ModDiag.ShelfErrors++;
                    ModDiag.Note("货架长度不匹配：游戏 " + list.Length + " 格，设置 " + ids.Length + " 格");
                    return;
                }

                // 判定逻辑在 ModLogic.ApplyShelfKeep（纯函数、可离线单测），这里只做写回与计数。
                int written = ModLogic.ApplyShelfKeep(list, ids, out int skipped);
                ModDiag.ShelfSkipped += skipped;

                if (written == 0) { ModDiag.Note("货架 6 格全是「不改」，没东西可写"); return; }
                ModDiag.ShelfApplied++;
            }
            catch (Exception ex) { ModDiag.ShelfErrors++; ModRuntime.LogOnce("货架自定义", ex); }
        }

        /// <summary>
        /// 插在 shangdianshuaxin.OnMouseDown() 开头：商店刷新免费。
        ///
        /// 游戏原逻辑（shangdianshuaxin.cs:39-58）写死了两步：
        ///   if (gameData.coin >= 300) { ...; gameData.coin -= 300; ... }
        /// 300 是硬编码字面量，从外面改不了。
        ///
        /// 所以换个角度：**先把存档里的金币 +300，游戏随后照常扣 300，净变化 0**。
        /// 顺带把 `coin >= 300` 那道门槛也过了 —— 没钱也能刷新。
        /// 注入点在方法开头，也就是**在游戏读盘之前**，所以游戏读到的是加过钱的版本。
        /// </summary>
        public static void FreeRefreshPrefix()
        {
            try
            {
                ModDiag.RefreshCalls++;
                if (!S.FreeRefresh) return;

                // 垫钱算法在 ModLogic.PrepareFreeRefresh（纯函数、可离线单测）
                bool ok = SaveData.Mutate(d => d.coin = ModLogic.PrepareFreeRefresh(d.coin), out string error);

                if (ok) ModDiag.RefreshFree++;
                else ModDiag.Note("刷新垫钱失败：" + error);
            }
            catch (Exception ex) { ModRuntime.LogOnce("刷新免费", ex); }
        }

        /// <summary>
        /// 插在 shopBuyBottom.OnMouseDown() 开头：抽卡免费。
        /// 把当前货架商品（仅卡包）的价格临时置 0 —— 一举通过 `cost > coin` 门槛并让 `coin -= cost` 扣 0。
        /// </summary>
        public static void BuyPrefix()
        {
            try
            {
                ModDiag.BuyCalls++;
                if (!S.FreeGacha) return;

                huojia now = huojia.nowHuojia;
                if (now == null)
                {
                    ModDiag.BuyErrors++;
                    ModDiag.Note("购买时没有选中货架商品");
                    _savedCost = -1;
                    return;
                }
                if (!ModLogic.IsCardPackId(now.id))
                {
                    // 不是卡包（比如精灵球、投资、猫粮），按设计不免费 —— 记下来，免得用户以为是故障
                    ModDiag.Note("当前商品 id=" + now.id + " 不是卡包（卡包只有 8/10/11/12）");
                    _savedCost = -1;
                    return;
                }

                _savedCost = now.cost;
                now.cost = 0;
                ModDiag.BuyApplied++;
            }
            catch (Exception ex) { ModDiag.BuyErrors++; ModRuntime.LogOnce("抽卡免费-前置", ex); }
        }

        /// <summary>插在 shopBuyBottom.OnMouseDown() 的每个 ret 之前：还原原价。</summary>
        public static void BuyPostfix()
        {
            try
            {
                if (_savedCost < 0) return;

                huojia now = huojia.nowHuojia;
                if (now != null) now.cost = _savedCost;
                _savedCost = -1;
            }
            catch (Exception ex) { ModRuntime.LogOnce("抽卡免费-后置", ex); }
        }

        /// <summary>
        /// 让 Hooks 与游戏状态脱钩：游戏回主菜单/重开一局时，把临时改过的价格复原。
        /// 否则残留的 0 价会让货架一直显示免费。
        /// </summary>
        public static void ResetTransient()
        {
            try
            {
                if (_savedCost < 0) return;
                huojia now = huojia.nowHuojia;
                if (now != null) now.cost = _savedCost;
                _savedCost = -1;
            }
            catch { }
        }
    }
}
