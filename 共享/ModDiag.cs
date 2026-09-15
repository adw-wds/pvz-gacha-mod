using System.Text;

namespace PvzShared
{
    /// <summary>
    /// 运行时诊断埋点。纯计数器，不碰 Unity、不碰文件，所以三处（mod / 面板 / 测试）都能编译。
    ///
    /// 为什么需要它：用户报「很多功能用不了」，而「命令已处理」只能证明命令到了，
    /// 不能证明功能真的作用到了游戏对象上。这里把每一环都记下来：
    ///   · 注入的钩子**到底有没有被游戏调用**（calls=0 就说明注入点选错了）
    ///   · 调用了但有没有真的改写（applied=0 说明开关没开或参数不合法）
    ///   · 每帧类作弊找到几个游戏对象（objects=0 说明当时不在关卡里）
    /// 面板「诊断」页直接显示这些数字，一眼就能定位断在哪一环。
    /// </summary>
    public static class ModDiag
    {
        private static readonly object Lock = new object();

        // ---- 注入的钩子：游戏调用我们才计数 ----
        public static int TierCalls, TierApplied, TierErrors;      // Wins.getXiyouGroup
        public static int ShelfCalls, ShelfApplied, ShelfErrors;   // shangdian.buhuo
        public static int BuyCalls, BuyApplied, BuyErrors;         // shopBuyBottom.OnMouseDown
        public static int RefreshCalls, RefreshFree;               // shangdianshuaxin.OnMouseDown（商店刷新）
        public static int UnlockPacksDone;                         // 卡包全解锁实际执行次数
        public static int ShelfSkipped;                            // 货架里被标为「不改」而跳过的格子数

        // ---- 每帧类作弊：跑了几次 / 实际改了几个对象 ----
        public static int PlantRuns, PlantFixed;    // 植物无敌
        public static int KillRuns, KillHits;       // 一击必杀
        public static int CoolRuns, CoolFixed;      // 零冷却
        public static int SunRuns, SunFixed;        // 自动收阳光
        public static int CoinWrites;               // 金币锁定写回次数
        public static int HpShowWrites;             // 显示血条写回次数

        // ---- 新增效果各自的命中数 ----
        public static int ShootFixed;               // 植物极速攻击
        public static int DontIceSet;               // 植物免疫冰冻
        public static int DontBoomSet;              // 植物免疫爆炸
        public static int ZombieGodFixed;           // 僵尸无敌
        public static int ZombieFrozen;             // 僵尸无法移动
        public static int ZombieCharmed;            // 魅惑僵尸
        public static int ZombieCoinSet;            // 僵尸必掉金币
        public static int ZombieNoBackSet;          // 僵尸不后退
        public static int BulletPassSet;            // 子弹穿透
        public static int HpMulSet;                 // 僵尸血量倍数
        public static int SpeedMulSet;              // 僵尸移速倍数
        public static int ZombieScaled;             // 僵尸缩放
        public static int PlantScaled;              // 植物缩放
        public static int ZuobiSet;                 // 游戏自带菜单
        public static int NanguakeSet;              // 植物自带南瓜壳
        public static int IsFlySet;                 // 植物可飞行
        public static int HealthBarSet;             // 植物显示血条
        public static int BgSwallowed;              // 吞掉的失焦消息数（后台运行是否真在起作用）
        public static int BgInstalled;              // 挂钩是否装上（0/1）

        // ---- 环境快照 ----
        public static int LastObjects;              // 最近一次 FindObjectsOfType 找到几个
        public static int LastKind;                 // 0无 1植物 2僵尸 3卡牌 4阳光
        public static int FirstFound;               // 有没有拿到关卡 First 对象
        public static string Scene = "";
        public static string Notes = "";

        /// <summary>记录一条关键失败原因（去重、限长，避免刷爆状态文件）。</summary>
        public static void Note(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (Lock)
            {
                if (Notes.IndexOf(text, System.StringComparison.Ordinal) >= 0) return;
                if (Notes.Length > 300) return;
                Notes += (Notes.Length == 0 ? "" : "；") + text;
            }
        }

        public static void SetScene(string scene)
        {
            Scene = scene ?? "";
        }

        /// <summary>把状态写进面板要读的那份 JSON（无 Unity 依赖）。</summary>
        public static string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Num(sb, "tierCalls", TierCalls);        Comma(sb);
            Num(sb, "tierApplied", TierApplied);    Comma(sb);
            Num(sb, "tierErrors", TierErrors);      Comma(sb);
            Num(sb, "shelfCalls", ShelfCalls);      Comma(sb);
            Num(sb, "shelfApplied", ShelfApplied);  Comma(sb);
            Num(sb, "shelfErrors", ShelfErrors);    Comma(sb);
            Num(sb, "buyCalls", BuyCalls);          Comma(sb);
            Num(sb, "buyApplied", BuyApplied);      Comma(sb);
            Num(sb, "buyErrors", BuyErrors);        Comma(sb);
            Num(sb, "refreshCalls", RefreshCalls);  Comma(sb);
            Num(sb, "refreshFree", RefreshFree);    Comma(sb);
            Num(sb, "unlockPacksDone", UnlockPacksDone); Comma(sb);
            Num(sb, "shelfSkipped", ShelfSkipped);  Comma(sb);
            Num(sb, "plantRuns", PlantRuns);        Comma(sb);
            Num(sb, "plantFixed", PlantFixed);      Comma(sb);
            Num(sb, "killRuns", KillRuns);          Comma(sb);
            Num(sb, "killHits", KillHits);          Comma(sb);
            Num(sb, "coolRuns", CoolRuns);          Comma(sb);
            Num(sb, "coolFixed", CoolFixed);        Comma(sb);
            Num(sb, "sunRuns", SunRuns);            Comma(sb);
            Num(sb, "sunFixed", SunFixed);          Comma(sb);
            Num(sb, "coinWrites", CoinWrites);      Comma(sb);
            Num(sb, "hpShowWrites", HpShowWrites);  Comma(sb);
            Num(sb, "shootFixed", ShootFixed);      Comma(sb);
            Num(sb, "dontIceSet", DontIceSet);      Comma(sb);
            Num(sb, "dontBoomSet", DontBoomSet);    Comma(sb);
            Num(sb, "zombieGodFixed", ZombieGodFixed); Comma(sb);
            Num(sb, "zombieFrozen", ZombieFrozen);  Comma(sb);
            Num(sb, "zombieCharmed", ZombieCharmed); Comma(sb);
            Num(sb, "zombieCoinSet", ZombieCoinSet); Comma(sb);
            Num(sb, "zombieNoBackSet", ZombieNoBackSet); Comma(sb);
            Num(sb, "bulletPassSet", BulletPassSet); Comma(sb);
            Num(sb, "hpMulSet", HpMulSet);          Comma(sb);
            Num(sb, "speedMulSet", SpeedMulSet);    Comma(sb);
            Num(sb, "zombieScaled", ZombieScaled);  Comma(sb);
            Num(sb, "plantScaled", PlantScaled);    Comma(sb);
            Num(sb, "zuobiSet", ZuobiSet);          Comma(sb);
            Num(sb, "nanguakeSet", NanguakeSet);    Comma(sb);
            Num(sb, "isFlySet", IsFlySet);          Comma(sb);
            Num(sb, "healthBarSet", HealthBarSet);  Comma(sb);
            Num(sb, "bgSwallowed", BgSwallowed);    Comma(sb);
            Num(sb, "bgInstalled", BgInstalled);    Comma(sb);
            Num(sb, "objects", LastObjects);        Comma(sb);
            Num(sb, "objectsKind", LastKind);       Comma(sb);
            Num(sb, "firstFound", FirstFound);      Comma(sb);
            Str(sb, "scene", Scene);                Comma(sb);
            Str(sb, "notes", Notes);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>回到初始值（测试用）。</summary>
        public static void Reset()
        {
            TierCalls = TierApplied = TierErrors = 0;
            ShelfCalls = ShelfApplied = ShelfErrors = 0;
            BuyCalls = BuyApplied = BuyErrors = 0;
            PlantRuns = PlantFixed = 0;
            KillRuns = KillHits = 0;
            CoolRuns = CoolFixed = 0;
            SunRuns = SunFixed = 0;
            CoinWrites = 0;
            HpShowWrites = 0;
            RefreshCalls = RefreshFree = 0;
            UnlockPacksDone = 0;
            ShelfSkipped = 0;
            ShootFixed = DontIceSet = DontBoomSet = 0;
            ZombieGodFixed = ZombieFrozen = ZombieCharmed = ZombieCoinSet = 0;
            ZombieNoBackSet = BulletPassSet = 0;
            HpMulSet = SpeedMulSet = ZombieScaled = PlantScaled = ZuobiSet = 0;
            NanguakeSet = IsFlySet = HealthBarSet = 0;
            BgSwallowed = BgInstalled = 0;
            LastObjects = LastKind = FirstFound = 0;
            Scene = "";
            Notes = "";
        }

        private static void Num(StringBuilder sb, string key, int v)
        {
            sb.Append('"').Append(key).Append("\":").Append(v);
        }

        private static void Str(StringBuilder sb, string key, string v)
        {
            sb.Append('"').Append(key).Append("\":").Append(MiniJson.Quote(v ?? ""));
        }

        private static void Comma(StringBuilder sb) { sb.Append(','); }
    }
}
