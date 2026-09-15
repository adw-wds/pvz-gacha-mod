using System;
using System.Collections.Generic;
using System.Linq;
using PvzShared;

namespace PvzTests
{
    internal static class CatalogTests
    {
        public static void Run()
        {
            Console.WriteLine("FeatureCatalog");

            T.Case("契约 A 的 33 个特性齐备且 key 唯一", () =>
            {
                T.Eq(33, FeatureCatalog.All.Length);
                var seen = new HashSet<string>();
                foreach (FeatureDef d in FeatureCatalog.All)
                    T.True(seen.Add(d.Key), "key 重复：" + d.Key);
            });

            T.Case("分组为约定的 6 个", () =>
            {
                var groups = new HashSet<string>();
                foreach (FeatureDef d in FeatureCatalog.All) groups.Add(d.Group);

                T.Eq(8, groups.Count);
                T.True(groups.Contains("抽卡") && groups.Contains("战斗") && groups.Contains("经济"));
                T.True(groups.Contains("僵尸") && groups.Contains("种植") && groups.Contains("关卡"));
                T.True(groups.Contains("实体属性") && groups.Contains("系统"));

            // 后台运行开关必须在场，且不限制场景（主菜单也能开）
            FeatureDef bg = FeatureCatalog.Find("system.background");
            T.True(bg != null);
            T.Eq("系统", bg.Group);
            T.Eq("", bg.RequireScene);
            T.Eq(FeatureKind.Bool, bg.Kind);
            });

            T.Case("每个特性都属于某个中文分组，且 key 前缀合法", () =>
            {
                foreach (FeatureDef d in FeatureCatalog.All)
                {
                    T.True(!string.IsNullOrEmpty(d.Group), d.Key + " 没有分组");
                    T.True(d.Key.IndexOf('.') > 0, d.Key + " 缺少 前缀.名称 形式");
                    T.True(!string.IsNullOrEmpty(d.Label), d.Key + " 没有标签");
                }
            });

            T.Case("用户不需要输数字：所有 int/float 特性都必须带预设选项", () =>
            {
                foreach (FeatureDef d in FeatureCatalog.All)
                {
                    if (d.Kind != FeatureKind.Int && d.Kind != FeatureKind.Float) continue;
                    T.True(d.Options != null && d.Options.Length > 0, d.Key + " 没有预设选项，用户得手输数字");

                    // 预设值必须落在 Min..Max 且能真的被设置
                    var s = new ModSettings();
                    foreach (FeatureOption o in d.Options)
                    {
                        T.True(FeatureCatalog.TrySet(s, d.Key, o.Value, out string e),
                            d.Key + " 预设 " + o.Value + " 不被接受：" + e);
                    }
                }
            });

            T.Case("数值上限是 10 亿，且预设里含这一项", () =>
            {
                T.Eq(1000000000, ModLogic.CoinMax);
                T.Eq(1000000000, ModLogic.SunMax);

                foreach (string key in new[] { "money.coinValue", "money.sunValue" })
                {
                    FeatureDef d = FeatureCatalog.Find(key);
                    T.True(d.Options.Any(o => o.Value == "1000000000"), key + " 的预设里没有 10 亿");
                }
            });

            T.Case("int/float 特性的 Min/Max 是有效区间", () =>
            {
                foreach (FeatureDef d in FeatureCatalog.All)
                {
                    if (d.Kind == FeatureKind.Int || d.Kind == FeatureKind.Float)
                        T.True(d.Max > d.Min, d.Key + " 区间非法");
                }
            });

            T.Case("单特性 JSON 形状被锁定（面板按此渲染）", () =>
            {
                string json = FeatureCatalog.FeatureJson(FeatureCatalog.Find("gacha.free"), new ModSettings());
                T.Eq("{\"key\":\"gacha.free\",\"label\":\"抽卡免费\",\"group\":\"抽卡\",\"kind\":\"bool\",\"value\":false" +
                     ",\"hint\":\"\",\"scene\":\"\",\"min\":0,\"max\":1,\"options\":[]}", json);
            });

            T.Case("需要进关卡才能生效的功能都标了场景（否则用户会以为功能坏了）", () =>
            {
                // 这些在主菜单里开是永远没效果的：场上没有对应对象
                string[] needLevel =
                {
                    "battle.noCooldown", "battle.oneHitKill", "battle.plantGod", "battle.autoSun",
                    "battle.zombieFreeze", "battle.zombieGod", "battle.charmZombie", "battle.zombieCoin",
                    "battle.zombieNoBack", "battle.hpMul", "battle.speedMul",
                    "plant.fastShoot", "plant.dontFreeze", "plant.dontBoom", "plant.bulletPass",
                    "plant.nanguake", "plant.isFly", "plant.healthBar",
                    "entity.zombieScale", "entity.plantScale", "level.hpShow",
                };

                foreach (string key in needLevel)
                {
                    FeatureDef d = FeatureCatalog.Find(key);
                    T.True(d != null, "特性不存在：" + key);
                    T.Eq("level", d.RequireScene);
                }
            });

            T.Case("随时可用的功能不该标场景", () =>
            {
                foreach (string key in new[] { "system.speed", "system.zuobi", "gacha.free", "money.lockCoin" })
                {
                    FeatureDef d = FeatureCatalog.Find(key);
                    T.True(d != null, "特性不存在：" + key);
                    T.Eq("", d.RequireScene);
                }
            });

            T.Case("种植类功能都齐（用户点名要的一组）", () =>
            {
                string[] plants = { "plant.fastShoot", "plant.dontFreeze", "plant.dontBoom", "plant.bulletPass",
                                    "plant.nanguake", "plant.isFly", "plant.healthBar" };
                foreach (string key in plants)
                {
                    FeatureDef d = FeatureCatalog.Find(key);
                    T.True(d != null, "缺少种植功能：" + key);
                    T.Eq("种植", d.Group);
                }
            });

            T.Case("带预设的特性把 options 发给面板（面板据此画按钮）", () =>
            {
                string json = FeatureCatalog.FeatureJson(FeatureCatalog.Find("money.coinValue"), new ModSettings());
                T.Contains(json, "\"options\":[{\"value\":\"10000\",\"label\":\"1万\"}");
                T.Contains(json, "{\"value\":\"1000000000\",\"label\":\"10亿\"}");
                T.Contains(json, "\"hint\":\"锁定生效后金币保持这个值\"");
            });

            T.Case("文本特性的 value 带引号", () =>
            {
                var s = new ModSettings { Shelf = "0,1,5" };
                T.Contains(FeatureCatalog.FeatureJson(FeatureCatalog.Find("gacha.shelf"), s), "\"value\":\"0,1,5\"");
            });

            T.Case("FeaturesJson 是 33 个对象的数组", () =>
            {
                string json = FeatureCatalog.FeaturesJson(new ModSettings());
                T.True(json.StartsWith("[{", StringComparison.Ordinal), json.Substring(0, 10));
                T.True(json.EndsWith("}]", StringComparison.Ordinal), json.Substring(json.Length - 10));

                int count = 0;
                for (int i = 0; i < json.Length - 7; i++) if (json.Substring(i, 7) == "\"group\"") count++;
                T.Eq(33, count);
            });

            T.Case("TrySet：未知 key 拒绝并给中文原因", () =>
            {
                T.True(!FeatureCatalog.TrySet(new ModSettings(), "no.such", "1", out string err));
                T.Contains(err, "未知");
            });

            T.Case("TrySet：bool 只接受 true/false，非法输入不改动原值", () =>
            {
                var s = new ModSettings();
                T.True(FeatureCatalog.TrySet(s, "gacha.free", "true", out _));
                T.True(s.FreeGacha);
                T.True(!FeatureCatalog.TrySet(s, "gacha.free", "yes", out string err));
                T.True(err.Length > 0, err);
                T.True(s.FreeGacha, "非法输入不得改动原值");
            });

            T.Case("TrySet：数值会被清洗夹取", () =>
            {
                var s = new ModSettings();
                T.True(FeatureCatalog.TrySet(s, "system.speed", "9", out _));
                T.Eq(5f, s.Speed);
                T.True(FeatureCatalog.TrySet(s, "gacha.rarity", "-9", out _));
                T.Eq(-1, s.Rarity);
                T.True(FeatureCatalog.TrySet(s, "money.coinValue", "123456", out _));
                T.Eq(123456, s.CoinValue);
            });

            T.Case("TrySet：非数字被拒绝", () =>
            {
                T.True(!FeatureCatalog.TrySet(new ModSettings(), "system.speed", "快", out string err));
                T.True(err.Length > 0, err);
            });

            T.Case("TrySet：文本特性直接写入（空串表示不干预）", () =>
            {
                var s = new ModSettings();
                T.True(FeatureCatalog.TrySet(s, "gacha.shelf", "0,1,5,8,10,11", out _));
                T.Eq("0,1,5,8,10,11", s.Shelf);
                T.True(FeatureCatalog.TrySet(s, "gacha.shelf", "", out _));
                T.Eq("", s.Shelf);
            });

            T.Case("设置能存盘再读回（否则关一次游戏所有开关都回默认值）", () =>
            {
                var a = new ModSettings
                {
                    FreeGacha = true, Rarity = 3, UnlockPacks = true, FreeRefresh = true,
                    Shelf = "0,1,5,8,10,11",
                    NoCooldown = true, OneHitKill = true, PlantGod = true, AutoSun = true, Speed = 3f,
                    LockCoin = true, CoinValue = 1000000000,
                    LockSun = true, SunValue = 1000000000,
                };

                string json = FeatureCatalog.SaveJson(a);
                var b = new ModSettings();
                FeatureCatalog.LoadJson(b, json);

                T.True(b.FreeGacha && b.UnlockPacks && b.FreeRefresh);
                T.Eq(3, b.Rarity);
                T.Eq("0,1,5,8,10,11", b.Shelf);
                T.True(b.NoCooldown && b.OneHitKill && b.PlantGod && b.AutoSun);
                T.Eq(3f, b.Speed);
                T.True(b.LockCoin && b.LockSun);
                T.Eq(1000000000, b.CoinValue);
                T.Eq(1000000000, b.SunValue);
            });

            T.Case("设置文件损坏/缺字段不炸，用默认值兵底", () =>
            {
                var s = new ModSettings();

                FeatureCatalog.LoadJson(s, "{\"gacha.free\":true,\"system.speed\":\"abc\"}");
                T.True(s.FreeGacha);        // 合法的照用
                T.Eq(1f, s.Speed);          // 非法值忽略

                FeatureCatalog.LoadJson(s, "{\"money.coinValue\":99999999999}");
                T.Eq(1000000000, s.CoinValue);   // 超上限被夹到 10 亿

                FeatureCatalog.LoadJson(s, "");
                FeatureCatalog.LoadJson(s, null);
                FeatureCatalog.LoadJson(s, "{ not json");
                FeatureCatalog.LoadJson(s, "[]");
                T.True(s.FreeGacha);        // 坏输入不能把已有设置清掉
            });

            T.Case("默认值符合契约 A", () =>
            {
                var s = new ModSettings();
                T.True(!s.FreeGacha);
                T.Eq(-1, s.Rarity);
                T.Eq(1f, s.Speed);
                // 数值默认就是满值 10 亿：用户不必手输
                T.Eq(1000000000, s.CoinValue);
                T.Eq(1000000000, s.SunValue);
                T.True(!s.LockCoin && !s.LockSun);
            });

            T.Case("诊断计数能序列化成面板可读的 JSON", () =>
            {
                ModDiag.Reset();
                ModDiag.TierCalls = 3;
                ModDiag.TierApplied = 1;
                ModDiag.Note("测试原因");
                ModDiag.SetScene("Zhucaidan");

                string json = ModDiag.ToJson();
                T.Contains(json, "\"tierCalls\":3");
                T.Contains(json, "\"tierApplied\":1");
                T.Contains(json, "\"scene\":\"Zhucaidan\"");
                T.Contains(json, "\"notes\":\"测试原因\"");
                T.True(json.StartsWith("{", StringComparison.Ordinal) && json.EndsWith("}", StringComparison.Ordinal));

                ModDiag.Reset();
                T.Eq(0, ModDiag.TierCalls);
                T.Eq("", ModDiag.Notes);
            });

            T.Case("状态 JSON 里带 diag 字段，diag 抛异常也不影响整体", () =>
            {
                var caps = new CapabilityRegistry();
                var api = new CommandApi(new ModSettings(), caps, null);
                api.DiagProvider = () => "{\"tierCalls\":7}";
                T.Contains(api.BuildStateJson(), "\"diag\":{\"tierCalls\":7}");

                api.DiagProvider = () => throw new InvalidOperationException("boom");
                T.Contains(api.BuildStateJson(), "\"diag\":{}");   // 降级为空对象，不能让面板读不了状态

                api.DiagProvider = null;
                T.Contains(api.BuildStateJson(), "\"diag\":{}");
            });
        }
    }
}
