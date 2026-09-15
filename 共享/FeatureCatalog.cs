using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PvzShared
{
    public enum FeatureKind
    {
        Bool = 0,
        Int = 1,
        Float = 2,
        Text = 3
    }

    /// <summary>
    /// 一个可选值。面板据此渲染成「分段按钮 / 下拉」，让用户点一下就行，不用手输数字。
    /// 选项由 mod 侧发出，面板完全数据驱动 —— 以后加预设不用改面板代码。
    /// </summary>
    public sealed class FeatureOption
    {
        public readonly string Value;
        public readonly string Label;

        public FeatureOption(string value, string label)
        {
            Value = value;
            Label = label;
        }
    }

    public sealed class FeatureDef
    {
        public string Key;
        public string Label;
        public string Group;
        public FeatureKind Kind;
        public double Min;
        public double Max;
        public string Hint;                     // 给面板显示的一句说明

        /// <summary>
        /// 生效所需的场景："" 随时可用；"level" 必须在关卡内；"shop" 靠进商店/买卡包触发。
        /// 用途：主菜单里开「零冷却」是永远不生效的（场上没有卡牌对象），
        /// 面板据此直接把状态标成「需进关卡」，而不是骗用户说已生效。
        /// </summary>
        public string RequireScene = "";

        public FeatureOption[] Options;         // 非空 → 面板渲染分段按钮；空 → 按 Kind 渲染开关/滑块
        public Func<ModSettings, string> Get;
        public Action<ModSettings, string> Set;
    }

    /// <summary>特性目录：面板完全靠这份目录渲染 UI，是 mod 与面板之间的唯一契约。</summary>
    public static class FeatureCatalog
    {
        private static FeatureOption[] Opts(params string[] pairs)
        {
            var list = new FeatureOption[pairs.Length / 2];
            for (int i = 0; i < list.Length; i++) list[i] = new FeatureOption(pairs[i * 2], pairs[i * 2 + 1]);
            return list;
        }

        public static readonly FeatureDef[] All = new[]
        {
            Bool("gacha.free",         "抽卡免费",    "抽卡", s => s.FreeGacha,   (s, v) => s.FreeGacha = v),
            IntWith("gacha.rarity", "抽卡档位", "抽卡", -1, 5, s => s.Rarity, (s, v) => s.Rarity = v,
                "想让卡包抽到哪一档：数字越大越稀有",
                Opts("-1", "关闭", "0", "1档", "1", "2档", "2", "3档", "3", "4档", "4", "5档", "5", "顶级档")),
            Bool("gacha.unlockPacks",  "卡包全解锁",  "抽卡", s => s.UnlockPacks, (s, v) => s.UnlockPacks = v),
            Bool("gacha.freeRefresh",  "商店刷新免费","抽卡", s => s.FreeRefresh, (s, v) => s.FreeRefresh = v),
            TextWith("gacha.shelf", "货架自定义", "抽卡", s => s.Shelf, (s, v) => s.Shelf = v,
                "只改你点过的格子，没选的格子保持游戏原样（选「空格」才是把该格清空）"),

            Bool("battle.noCooldown", "零冷却", "战斗", s => s.NoCooldown, (s, v) => s.NoCooldown = v, "level"),
            Bool("battle.oneHitKill", "一击必杀", "战斗", s => s.OneHitKill, (s, v) => s.OneHitKill = v, "level"),
            Bool("battle.plantGod", "植物无敌", "战斗", s => s.PlantGod, (s, v) => s.PlantGod = v, "level"),
            Bool("battle.autoSun", "自动收阳光", "战斗", s => s.AutoSun, (s, v) => s.AutoSun = v, "level"),

            Bool("battle.zombieFreeze", "僵尸无法移动", "僵尸", s => s.ZombieFreeze, (s, v) => s.ZombieFreeze = v, "level"),
            Bool("battle.zombieGod", "僵尸无敌", "僵尸", s => s.ZombieGod, (s, v) => s.ZombieGod = v, "level"),
            Bool("battle.charmZombie", "魅惑全部僵尸", "僵尸", s => s.CharmZombie, (s, v) => s.CharmZombie = v, "level"),
            Bool("battle.zombieCoin", "僵尸必掉金币", "僵尸", s => s.ZombieCoin, (s, v) => s.ZombieCoin = v, "level"),
            Bool("battle.zombieNoBack", "僵尸不后退", "僵尸", s => s.ZombieNoBack, (s, v) => s.ZombieNoBack = v, "level"),
            FloatWith("battle.hpMul", "僵尸血量倍数", "僵尸", ModLogic.MinMul, ModLogic.MaxMul, s => s.HpMul, (s, v) => s.HpMul = v,
                "游戏自己的难度系数（First.HelthCheng），1 = 原版",
                Opts("1", "原版", "2", "2倍", "5", "5倍", "10", "10倍"), "level"),
            FloatWith("battle.speedMul", "僵尸移速倍数", "僵尸", ModLogic.MinMul, ModLogic.MaxMul, s => s.SpeedMul, (s, v) => s.SpeedMul = v,
                "游戏自己的难度系数（First.WalkSpeed），调低就是慢动作",
                Opts("0", "定身", "0.3", "0.3倍", "1", "原版", "2", "2倍"), "level"),

            Bool("plant.fastShoot", "植物极速攻击", "种植", s => s.FastShoot, (s, v) => s.FastShoot = v, "level"),
            Bool("plant.dontFreeze", "植物免疫冰冻", "种植", s => s.DontFreeze, (s, v) => s.DontFreeze = v, "level"),
            Bool("plant.dontBoom", "植物免疫爆炸", "种植", s => s.DontBoom, (s, v) => s.DontBoom = v, "level"),
            Bool("plant.bulletPass", "子弹穿透", "种植", s => s.BulletPass, (s, v) => s.BulletPass = v, "level"),
            Bool("plant.nanguake", "植物自带南瓜壳", "种植", s => s.Nanguake, (s, v) => s.Nanguake = v, "level"),
            Bool("plant.isFly", "植物可飞行", "种植", s => s.IsFly, (s, v) => s.IsFly = v, "level"),
            Bool("plant.healthBar", "植物显示血条", "种植", s => s.HealthBar, (s, v) => s.HealthBar = v, "level"),

            FloatWith("entity.zombieScale", "僵尸大小", "实体属性", ModLogic.MinScale, ModLogic.MaxScale, s => s.ZombieScale, (s, v) => s.ZombieScale = v,
                "直接改 Transform 缩放，是纯视觉的，不影响判定",
                Opts("0.5", "一半", "1", "原版", "1.5", "1.5倍", "2.5", "巨型"), "level"),
            FloatWith("entity.plantScale", "植物大小", "实体属性", ModLogic.MinScale, ModLogic.MaxScale, s => s.PlantScale, (s, v) => s.PlantScale = v,
                "同上，纯视觉",
                Opts("0.5", "一半", "1", "原版", "1.5", "1.5倍", "2.5", "巨型"), "level"),

            Bool("level.hpShow", "显示僵尸血条", "关卡", s => s.HpShow, (s, v) => s.HpShow = v, "level"),

            Bool("system.zuobi", "解锁游戏自带菜单", "系统", s => s.Zuobi, (s, v) => s.Zuobi = v),
            Bool("system.background", "游戏后台运行", "系统", s => s.BackgroundRun, (s, v) => s.BackgroundRun = v),
            FloatWith("system.speed", "游戏速度", "系统", ModLogic.MinSpeed, ModLogic.MaxSpeed, s => s.Speed, (s, v) => s.Speed = v,
                "1x 是原速；游戏自己的暂停/失败界面不会被覆盖",
                Opts("1", "1x", "2", "2x", "3", "3x", "5", "5x")),

            Bool("money.lockCoin", "金币锁定", "经济", s => s.LockCoin, (s, v) => s.LockCoin = v),
            IntWith("money.coinValue", "金币值", "经济", 0, ModLogic.CoinMax, s => s.CoinValue, (s, v) => s.CoinValue = v,
                "锁定生效后金币保持这个值",
                Opts("10000", "1万", "1000000", "100万", "100000000", "1亿", "1000000000", "10亿")),
            Bool("money.lockSun", "阳光锁定", "经济", s => s.LockSun, (s, v) => s.LockSun = v),
            IntWith("money.sunValue", "阳光值", "经济", 0, ModLogic.SunMax, s => s.SunValue, (s, v) => s.SunValue = v,
                "进入关卡后阳光保持这个值",
                Opts("1000", "1000", "10000", "1万", "1000000", "100万", "1000000000", "10亿")),
        };

        public static FeatureDef Find(string key)
        {
            if (key == null || key.Length == 0) return null;
            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].Key, key, StringComparison.Ordinal)) return All[i];
            }
            return null;
        }

        public static string FeatureJson(FeatureDef d, ModSettings s)
        {
            var sb = new StringBuilder();
            sb.Append("{\"key\":").Append(MiniJson.Quote(d.Key));
            sb.Append(",\"label\":").Append(MiniJson.Quote(d.Label));
            sb.Append(",\"group\":").Append(MiniJson.Quote(d.Group));
            sb.Append(",\"kind\":").Append(MiniJson.Quote(KindName(d.Kind)));
            sb.Append(",\"value\":").Append(RawValue(d, s));
            sb.Append(",\"hint\":").Append(MiniJson.Quote(d.Hint ?? ""));
            sb.Append(",\"scene\":").Append(MiniJson.Quote(d.RequireScene ?? ""));
            sb.Append(",\"min\":").Append(MiniJson.Num(d.Min));
            sb.Append(",\"max\":").Append(MiniJson.Num(d.Max));
            sb.Append(",\"options\":[");
            if (d.Options != null)
            {
                for (int i = 0; i < d.Options.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"value\":").Append(MiniJson.Quote(d.Options[i].Value));
                    sb.Append(",\"label\":").Append(MiniJson.Quote(d.Options[i].Label)).Append('}');
                }
            }
            sb.Append("]");
            sb.Append('}');
            return sb.ToString();
        }

        public static string FeaturesJson(ModSettings s)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < All.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FeatureJson(All[i], s));
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>值编码：Bool → true/false；Int/Float → 裸数字（Float 额外做速度清洗）；Text → JSON 字符串。</summary>
        public static string RawValue(FeatureDef d, ModSettings s)
        {
            string raw = d.Get(s) ?? "";
            switch (d.Kind)
            {
                case FeatureKind.Bool:
                    return MiniJson.Bool(ParseBool(raw));
                case FeatureKind.Int:
                    return MiniJson.Num(ParseLong(raw));
                case FeatureKind.Float:
                    return MiniJson.Num(ModLogic.SanitizeSpeed(ParseDouble(raw)));
                default:
                    return MiniJson.Quote(raw);
            }
        }

        public static bool TrySet(ModSettings s, string key, string rawValue, out string error)
        {
            error = null;
            FeatureDef d = Find(key);
            if (d == null)
            {
                error = "未知特性：" + key;
                return false;
            }

            rawValue = rawValue ?? "";
            switch (d.Kind)
            {
                case FeatureKind.Bool:
                    if (rawValue == "true") { d.Set(s, "true"); return true; }
                    if (rawValue == "false") { d.Set(s, "false"); return true; }
                    error = "布尔特性只接受 true/false，收到：" + rawValue;
                    return false;

                case FeatureKind.Int:
                {
                    if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv))
                    {
                        error = "整数特性收到非数字：" + rawValue;
                        return false;
                    }
                    long clamped = iv < (long)d.Min ? (long)d.Min : (iv > (long)d.Max ? (long)d.Max : iv);
                    d.Set(s, clamped.ToString(CultureInfo.InvariantCulture));
                    return true;
                }

                case FeatureKind.Float:
                {
                    if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                    {
                        error = "浮点特性收到非数字：" + rawValue;
                        return false;
                    }
                    float v = ModLogic.SanitizeSpeed(dv);
                    d.Set(s, v.ToString("R", CultureInfo.InvariantCulture));
                    return true;
                }

                default:
                    d.Set(s, rawValue);
                    return true;
            }
        }

        public static string KindName(FeatureKind k)
        {            switch (k)
            {
                case FeatureKind.Bool: return "bool";
                case FeatureKind.Int: return "int";
                case FeatureKind.Float: return "float";
                default: return "text";
            }
        }

        /// <summary>
        /// 把当前设置压成扁平 JSON（key → 值），用于持久化。
        /// 只存 FeatureCatalog 认识的项，所以旧版本多出来的字段不会污染新版本。
        /// </summary>
        public static string SaveJson(ModSettings s)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < All.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(MiniJson.Quote(All[i].Key)).Append(':').Append(RawValue(All[i], s));
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>从扁平 JSON 恢复设置。缺字段/坏字段都跳过（用默认值），任何情况下不抛异常。</summary>
        public static void LoadJson(ModSettings s, string json)
        {
            if (string.IsNullOrEmpty(json)) return;

            Dictionary<string, string> flat;
            try { flat = MiniJson.ParseFlatObject(json); }
            catch { return; }

            foreach (FeatureDef d in All)
            {
                if (!flat.TryGetValue(d.Key, out string v)) continue;
                try { TrySet(s, d.Key, v, out _); }
                catch { }
            }
        }

        private static FeatureDef Bool(string key, string label, string group,
            Func<ModSettings, bool> get, Action<ModSettings, bool> set, string requireScene = "")
        {
            return new FeatureDef
            {
                Key = key, Label = label, Group = group, Kind = FeatureKind.Bool, Min = 0, Max = 1,
                RequireScene = requireScene,
                Get = s => get(s) ? "true" : "false",
                Set = (s, v) => set(s, string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
            };
        }

        private static FeatureDef Int(string key, string label, string group, double min, double max,
            Func<ModSettings, int> get, Action<ModSettings, int> set)
        {
            return new FeatureDef
            {
                Key = key, Label = label, Group = group, Kind = FeatureKind.Int, Min = min, Max = max,
                Get = s => get(s).ToString(CultureInfo.InvariantCulture),
                Set = (s, v) =>
                {
                    if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv))
                        set(s, (int)iv);
                }
            };
        }

        private static FeatureDef Float(string key, string label, string group, double min, double max,
            Func<ModSettings, float> get, Action<ModSettings, float> set)
        {
            return new FeatureDef
            {
                Key = key, Label = label, Group = group, Kind = FeatureKind.Float, Min = min, Max = max,
                Get = s => get(s).ToString("R", CultureInfo.InvariantCulture),
                Set = (s, v) =>
                {
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                        set(s, (float)dv);
                }
            };
        }

        private static FeatureDef Text(string key, string label, string group,
            Func<ModSettings, string> get, Action<ModSettings, string> set)
        {
            return new FeatureDef
            {
                Key = key, Label = label, Group = group, Kind = FeatureKind.Text,
                Min = 0, Max = 0, Get = get, Set = set
            };
        }

        // ---- 带预设/提示的变体：面板据此直接画按钮，用户不用手输 ----

        private static FeatureDef IntWith(string key, string label, string group, double min, double max,
            Func<ModSettings, int> get, Action<ModSettings, int> set, string hint, FeatureOption[] options,
            string requireScene = "")
        {
            FeatureDef d = Int(key, label, group, min, max, get, set);
            d.Hint = hint;
            d.Options = options;
            d.RequireScene = requireScene;
            return d;
        }

        private static FeatureDef FloatWith(string key, string label, string group, double min, double max,
            Func<ModSettings, float> get, Action<ModSettings, float> set, string hint, FeatureOption[] options,
            string requireScene = "")
        {
            FeatureDef d = Float(key, label, group, min, max, get, set);
            d.Hint = hint;
            d.Options = options;
            d.RequireScene = requireScene;
            return d;
        }

        private static FeatureDef TextWith(string key, string label, string group,
            Func<ModSettings, string> get, Action<ModSettings, string> set, string hint, string requireScene = "")
        {
            FeatureDef d = Text(key, label, group, get, set);
            d.Hint = hint;
            d.RequireScene = requireScene;
            return d;
        }

        /// <summary>带场景要求的开关。</summary>
        private static FeatureDef BoolAt(string key, string label, string group,
            Func<ModSettings, bool> get, Action<ModSettings, bool> set, string requireScene)
        {
            return Bool(key, label, group, get, set, requireScene);
        }

        private static long ParseLong(string s)
        {
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
        }

        private static double ParseDouble(string s)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        private static bool ParseBool(string s)
        {
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
