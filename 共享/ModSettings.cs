namespace PvzShared
{
    /// <summary>修改器全部开关的唯一状态源（内存态）。默认值见设计文档「契约 A」。</summary>
    public sealed class ModSettings
    {
        public bool FreeGacha = false;
        public int Rarity = -1;
        public bool UnlockPacks = false;
        public bool FreeRefresh = false;
        public string Shelf = "";

        public bool NoCooldown = false;
        public bool OneHitKill = false;
        public bool PlantGod = false;
        public bool AutoSun = false;
        public float Speed = 1f;

        // ---- 植物侧（对应游戏 Planting 的公开字段）----
        public bool FastShoot = false;      // shootTime = 0，攻速拉满
        public bool DontFreeze = false;     // dontICE
        public bool DontBoom = false;       // dontBoom
        public bool BulletPass = false;     // zidanwushi，子弹穿透
        public bool Nanguake = false;       // nanguake，自带南瓜壳
        public bool IsFly = false;          // isFly，可飞行
        public bool HealthBar = false;      // haveHealthBar，显示植物血条

        // ---- 僵尸侧（对应游戏 Zombie 的公开字段）----
        public bool ZombieFreeze = false;   // walkSpeed = 0
        public bool CharmZombie = false;    // isMeihuo = true（游戏自带的魅惑）
        public bool ZombieGod = false;      // 血量锁满
        public bool ZombieCoin = false;     // coinPt = 100，必掉金币
        public bool ZombieNoBack = false;   // noHui = true，不退回去
        public float HpMul = 1f;            // First.HelthCheng：僵尸血量系数
        public float SpeedMul = 1f;         // First.WalkSpeed：僵尸移速系数

        // ---- 实体属性（对应你那份的实体属性分类）----
        public float ZombieScale = 1f;      // 僵尸大小
        public float PlantScale = 1f;       // 植物大小

        // ---- 系统 ----
        public bool Zuobi = false;          // First.zuobi：游戏自带的调试菜单

        /// <summary>让游戏失去焦点后继续跑（钩窗口过程吞掉失焦消息）。</summary>
        public bool BackgroundRun = false;

        // ---- 关卡/存档字段 ----
        public bool HpShow = false;         // GameData.hpShow，显示血条

        public bool LockCoin = false;
        public int CoinValue = ModLogic.CoinMax;      // 10 亿（默认就是满值，用户不必手输）
        public bool LockSun = false;
        public int SunValue = ModLogic.SunMax;        // 10 亿

        public void ResetToDefaults()
        {
            FreeGacha = false;
            Rarity = -1;
            UnlockPacks = false;
            FreeRefresh = false;
            Shelf = "";
            NoCooldown = false;
            OneHitKill = false;
            PlantGod = false;
            AutoSun = false;
            Speed = 1f;

            FastShoot = false;
            DontFreeze = false;
            DontBoom = false;
            BulletPass = false;
            Nanguake = false;
            IsFly = false;
            HealthBar = false;
            ZombieFreeze = false;
            CharmZombie = false;
            ZombieGod = false;
            ZombieCoin = false;
            ZombieNoBack = false;
            HpMul = 1f;
            SpeedMul = 1f;
            ZombieScale = 1f;
            PlantScale = 1f;
            Zuobi = false;
            BackgroundRun = false;
            HpShow = false;

            LockCoin = false;
            CoinValue = ModLogic.CoinMax;
            LockSun = false;
            SunValue = ModLogic.SunMax;
        }
    }
}
