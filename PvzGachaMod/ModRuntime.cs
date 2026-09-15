using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PvzShared;
using UnityEngine;

namespace PvzGachaMod
{
    /// <summary>
    /// 游戏内运行时：常驻 GameObject 上的组件，负责
    ///   1. 轮询命令文件 → 驱动 CommandApi
    ///   2. 周期写状态文件（面板读它渲染）
    ///   3. 每帧类作弊（速度/阳光/金币/植物无敌/秒杀/零冷却/自动收阳光）
    ///   4. 画 IMGUI 悬浮窗
    ///
    /// 设计要点：对象类作弊按 4 帧轮转，摊平 FindObjectsOfType 的开销；
    /// 所有外部调用都包 try/catch，绝不让异常打断游戏（实测过异常会打断游戏自己的方法）。
    /// </summary>
    internal sealed class ModRuntime : MonoBehaviour
    {
        // 直接静态初始化：钩子可能在 Awake 之前就被游戏调用，不能留空引用
        internal static ModSettings Settings = new ModSettings();
        internal static CapabilityRegistry Caps;
        internal static CommandApi Api;
        internal static ModRuntime Instance;
        internal static ScriptEngine Script;

        private static readonly HashSet<string> LoggedOnce = new HashSet<string>(StringComparer.Ordinal);

        private int _frame;

        // ---- 反射字段（懒加载 + 容错：游戏类型缺失时不能让类初始化就崩） ----

        private static FieldInfo _proField;
        private static bool _proFieldTried;
        private static FieldInfo ProField
        {
            get
            {
                if (!_proFieldTried)
                {
                    _proFieldTried = true;
                    try
                    {
                        _proField = typeof(CardClick).GetField("pro",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    }
                    catch (Exception ex) { LogOnce("反射 CardClick.pro", ex); }
                }
                return _proField;
            }
        }

        private static FieldInfo _sunStartField;
        private static bool _sunStartTried;
        private static FieldInfo SunStartTimeField
        {
            get
            {
                if (!_sunStartTried)
                {
                    _sunStartTried = true;
                    try
                    {
                        _sunStartField = typeof(Sun).GetField("startTime",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    }
                    catch (Exception ex) { LogOnce("反射 Sun.startTime", ex); }
                }
                return _sunStartField;
            }
        }

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            Instance = this;
            Caps = new CapabilityRegistry();
            Api = new CommandApi(Settings, Caps, ModActions.Handle);
            Api.DiagProvider = ModDiag.ToJson;      // 面板「诊断」页就是读这一份
            Api.ScriptStarter = StartScript;
            Api.ScriptStopper = StopScript;
            Api.ScriptStatusProvider = () => Script != null ? Script.StatusJson() : "{}";

            // 自报构建号：面板上一眼能看出游戏里跑的是哪一版，避免又装到旧 DLL 还不知道
            try { Api.ModVersion = Api.ModVersion + "+" + BuildStamp.Value; }
            catch { }

            try { SavePaths.OverrideDir = Application.persistentDataPath; }
            catch (Exception ex) { LogOnce("取 persistentDataPath", ex); }

            LoadSettings();
            RegisterCapabilities();

            Log("ModRuntime 启动：存档目录 " + SavePaths.Root);
        }

        private void RegisterCapabilities()
        {
            Caps.Register("存档通道", !string.IsNullOrEmpty(SavePaths.Root), "取不到存档目录");
            Caps.Register("存档文件", File.Exists(SavePaths.SaveFile), "还没生成 save.json（先进一次关卡）");
            Caps.Register("对象查找", true);
            Caps.Register("卡牌私有字段", ProField != null, "取不到 CardClick.pro（零冷却不可用）");
            Caps.Register("阳光私有字段", SunStartTimeField != null, "取不到 Sun.startTime（自动收阳光不可用）");
            Caps.Register("强类型存档读写", true);
        }

        private void Update()
        {
            try
            {
                _frame++;

                if (Script != null) Script.Tick();

                if (_frame % 6 == 0) PollCommand();
                if (_frame % 30 == 0) WriteState();

                ApplySpeed();
                ApplyGlobalTuning();
                ApplyBackgroundRun();

                if (_frame % 5 == 0) ApplySunLock();
                if (_frame % 120 == 0)
                {
                    ApplyCoinLock();
                    ApplySaveFlags();
                }

                // 轮转：一帧只做一类对象查找，摊平开销。
                // 每类里把所有同类开关一起处理掉（只查一次 FindObjectsOfType）。
                switch (_frame % 4)
                {
                    case 0: ApplyPlantEffects();  break;
                    case 1: ApplyZombieEffects(); break;
                    case 2: ApplyNoCooldown();    break;
                    case 3: ApplyAutoSun();       break;
                }
            }
            catch (Exception ex) { LogOnce("Update", ex); }
        }

        /// <summary>写状态文件前顺手刷一遍环境快照，让面板「诊断」页能看到当前场景。</summary>
        private void WriteState()
        {
            try
            {
                UnityEngine.SceneManagement.Scene sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                ModDiag.SetScene(sc.name);
                ModDiag.FirstFound = ModActions.FindFirst() != null ? 1 : 0;
                ModDiag.BgInstalled = BackgroundKeepAlive.Installed ? 1 : 0;
                ModDiag.BgSwallowed = BackgroundKeepAlive.Swallowed;
            }
            catch { }

            ChannelFiles.WriteState(Api.BuildStateJson());
        }

        private void PollCommand()
        {
            string[] lines = ChannelFiles.ReadCommands();
            if (lines.Length == 0) return;

            // 先清空再逐条处理：把「读」与「清」之间的窗口压到最小。
            // 即使这几微秒里面板又追加了一条，也因为面板发的是全量快照而会在下次自愈。
            ChannelFiles.ClearCommand();

            int ok = 0, fail = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string response = Api.HandleCommand(lines[i]);
                if (response.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0) ok++;
                else
                {
                    fail++;
                    Log("命令失败：" + lines[i] + " → " + response);
                }
            }

            if (ok > 0) SaveSettings();      // 设置变了就落盘，否则关一次游戏全回默认值
            if (fail > 0) Log("本轮命令：" + ok + " 成功 / " + fail + " 失败");
        }

        // ---------------------------------------------------------------- 脚本

        private static string StartScript(string source)
        {
            if (Script == null) Script = new ScriptEngine(Api);
            return Script.Start(source, out string err) ? null : err;
        }

        private static void StopScript()
        {
            if (Script != null) Script.Stop("被面板停止");
        }

        // ---------------------------------------------------------------- 设置持久化

        private void LoadSettings()
        {
            try
            {
                string path = SavePaths.SettingsFile;
                if (!File.Exists(path)) return;
                FeatureCatalog.LoadJson(Settings, File.ReadAllText(path));
                Log("已恢复上次的设置：" + path);
            }
            catch (Exception ex) { LogOnce("读设置", ex); }
        }

        private void SaveSettings()
        {
            try
            {
                SavePaths.EnsureDirExists();
                File.WriteAllText(SavePaths.SettingsFile, FeatureCatalog.SaveJson(Settings));
            }
            catch (Exception ex) { LogOnce("存设置", ex); }
        }

        // ---------------------------------------------------------------- 每帧类作弊
        // 全部带诊断计数：光看「命令已处理」判断不了功能有没有真的作用到游戏对象上，
        // 必须记录「跑了没 / 找到几个对象 / 真改了几个」——面板「诊断」页直接显示。

        private void ApplySpeed()
        {
            if (ModLogic.ShouldApplyTimeScale(Time.timeScale, Settings.Speed, out float applied))
                Time.timeScale = applied;
        }

        private void ApplySunLock()
        {
            if (!Settings.LockSun) return;
            First first = ModActions.FindFirst();
            if (first == null) return;
            if (first.Sun != Settings.SunValue) first.Sun = Settings.SunValue;
        }

        private void ApplyCoinLock()
        {
            if (!Settings.LockCoin) return;

            GameStart.GameData data = SaveData.TryLoad();
            if (data == null) return;
            if (data.coin == Settings.CoinValue) return;
            if (SaveData.Mutate(d => { d.coin = Settings.CoinValue; }, out _)) ModDiag.CoinWrites++;
        }

        /// <summary>写在存档里、游戏下次进关卡才读的开关（目前是显示血条）。</summary>
        /// <summary>卡包全解锁是否已执行过（边沿触发用，避免每帧写盘）。</summary>
        private bool _unlockPacksApplied;

        /// <summary>
        /// 存档类开关。这些功能改的是 save.json —— 游戏每个操作都重新读盘
        /// （shopBuyBottom / huojia / shangdianshuaxin 全是 File.ReadAllText → FromJson），
        /// 所以改完就生效，不用重进游戏。
        ///
        /// 但不能每帧刷：写盘 + 重算哈希开销大且无意义，所以做成「从关到开那一下执行一次」，
        /// 关掉再开可以重新触发。
        /// </summary>
        private void ApplySaveFlags()
        {
            // 显示血条：幂等，只要没置上就补
            if (Settings.HpShow)
            {
                GameStart.GameData data = SaveData.TryLoad();
                if (data != null && !data.hpShow)
                    if (SaveData.Mutate(d => { d.hpShow = true; }, out _)) ModDiag.HpShowWrites++;
            }

            // 卡包全解锁：边沿触发（这也是之前「开了没反应」的原因 —— 开关压根没接实现）
            if (Settings.UnlockPacks)
            {
                if (!_unlockPacksApplied)
                {
                    _unlockPacksApplied = true;
                    string result = ModActions.UnlockAllPacks();
                    if (result.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0) ModDiag.UnlockPacksDone++;
                    else ModDiag.Note("卡包全解锁失败：" + result);
                }
            }
            else _unlockPacksApplied = false;
        }

        /// <summary>
        /// 全局调参：僵尸血量/移速系数（游戏的 First.HelthCheng / First.WalkSpeed，
        /// 本来就是给无尽模式做难度用的）、以及游戏自带的调试菜单开关。
        /// 这些是 static 字段，改一次全局生效，不用逐只僵尸遍历。
        /// </summary>
        private void ApplyGlobalTuning()
        {
            try
            {
                if (Settings.HpMul > 0f && Math.Abs(First.HelthCheng - Settings.HpMul) > 0.001f)
                {
                    First.HelthCheng = (int)ModLogic.SanitizeMul(Settings.HpMul);
                    ModDiag.HpMulSet++;
                }

                if (Math.Abs(First.WalkSpeed - Settings.SpeedMul) > 0.001f)
                {
                    First.WalkSpeed = ModLogic.SanitizeMul(Settings.SpeedMul);
                    ModDiag.SpeedMulSet++;
                }

                if (Settings.Zuobi && !First.zuobi)
                {
                    First.zuobi = true;
                    ModDiag.ZuobiSet++;
                }
            }
            catch (Exception ex) { LogOnce("全局调参", ex); }
        }

        /// <summary>
        /// 后台运行开关：按需挂钩/卸钩游戏窗口的 WndProc。
        /// 只在状态变化时动手，不会每帧反复设置。
        /// </summary>
        private void ApplyBackgroundRun()
        {
            if (Settings.BackgroundRun == BackgroundKeepAlive.Installed) return;

            if (Settings.BackgroundRun)
            {
                // 拿不到窗口句柄时（比如还在启动）不要每帧重试，隔一会儿再试
                if (_frame % 60 != 0) return;

                if (BackgroundKeepAlive.Install())
                    Log("已开启后台运行：游戏失焦后仍会继续跑");
                else if (_frame % 600 == 0)
                    Log("后台运行挂钩失败：" + BackgroundKeepAlive.LastError);
            }
            else
            {
                BackgroundKeepAlive.Uninstall();
                Log("已关闭后台运行");
            }
        }

        private void OnApplicationQuit()
        {
            // 退出时一定要把窗口过程还原，否则留下指向已卸载程序集的指针会在退出时崩
            try { BackgroundKeepAlive.Uninstall(); } catch { }
        }

        private void ApplyPlantGod()
        {
            if (!Settings.PlantGod) return;
            ModDiag.PlantRuns++;

            Planting[] plants = UnityEngine.Object.FindObjectsOfType<Planting>();
            if (plants == null) return;
            ModDiag.LastObjects = plants.Length;
            ModDiag.LastKind = 1;

            for (int i = 0; i < plants.Length; i++)
            {
                Planting p = plants[i];
                if (p == null) continue;
                if (p.maxHP <= 0) continue;               // 未初始化，跳过免得把血改成 0
                if (p.HP >= p.maxHP) continue;
                p.HP = p.maxHP;
                ModDiag.PlantFixed++;
            }
        }

        /// <summary>
        /// 植物侧全部开关一次处理掉（只查一次 FindObjectsOfType）：
        ///   无敌 / 极速攻击 / 免疫冰冻 / 免疫爆炸
        /// 字段全部来自游戏 Planting 的公开成员，改的就是游戏自己在读的那份数据。
        /// </summary>
        private void ApplyPlantEffects()
        {
            if (!Settings.PlantGod && !Settings.FastShoot && !Settings.DontFreeze && !Settings.DontBoom
                && !Settings.BulletPass && !Settings.Nanguake && !Settings.IsFly && !Settings.HealthBar
                && Math.Abs(Settings.PlantScale - 1f) <= 0.001f) return;

            ModDiag.PlantRuns++;

            Planting[] plants = UnityEngine.Object.FindObjectsOfType<Planting>();
            if (plants == null) return;
            ModDiag.LastObjects = plants.Length;
            ModDiag.LastKind = 1;

            for (int i = 0; i < plants.Length; i++)
            {
                Planting p = plants[i];
                if (p == null) continue;

                try
                {
                    if (Settings.PlantGod && p.maxHP > 0 && p.HP < p.maxHP)
                    {
                        p.HP = p.maxHP;
                        ModDiag.PlantFixed++;
                    }

                    if (Math.Abs(Settings.PlantScale - 1f) > 0.001f)
                    {
                        var want = new Vector3(Settings.PlantScale, Settings.PlantScale, 1f);
                        if (p.transform != null && p.transform.localScale.x != want.x)
                        {
                            p.transform.localScale = want;
                            ModDiag.PlantScaled++;
                        }
                    }

                    if (Settings.FastShoot && p.shootTime > 0f)
                    {
                        p.shootTime = 0f;      // 游戏每帧扇它作倒计时，归零就是“随时可打”
                        ModDiag.ShootFixed++;
                    }

                    if (Settings.DontFreeze && !p.dontICE) { p.dontICE = true; ModDiag.DontIceSet++; }
                    if (Settings.DontBoom && !p.dontBoom) { p.dontBoom = true; ModDiag.DontBoomSet++; }
                    if (Settings.BulletPass && !p.zidanwushi) { p.zidanwushi = true; ModDiag.BulletPassSet++; }

                    // 种植类：南瓜壳 / 可飞行 / 显示血条（都是游戏自己的字段）
                    if (Settings.Nanguake && !p.nanguake) { p.nanguake = true; ModDiag.NanguakeSet++; }
                    if (Settings.IsFly && !p.isFly) { p.isFly = true; ModDiag.IsFlySet++; }
                    if (Settings.HealthBar && !p.haveHealthBar) { p.haveHealthBar = true; ModDiag.HealthBarSet++; }
                }
                catch (Exception ex) { ModDiag.Note("植物属性写入失败：" + ex.GetType().Name); }
            }
        }

        private void ApplyOneHitKill()
        {
            if (!Settings.OneHitKill) return;
            ModDiag.KillRuns++;

            Zombie[] zombies = UnityEngine.Object.FindObjectsOfType<Zombie>();
            if (zombies == null) return;
            ModDiag.LastObjects = zombies.Length;
            ModDiag.LastKind = 2;

            for (int i = 0; i < zombies.Length; i++)
            {
                Zombie z = zombies[i];
                if (z == null || z.ZombieHp <= 0) continue;
                try { z.TakeDamage(9999999, 1); ModDiag.KillHits++; }
                catch (Exception ex) { ModDiag.Note("僵尸受伤调用失败：" + ex.GetType().Name); }
            }
        }

        /// <summary>
        /// 僵尸侧全部开关一次处理掉（只查一次 FindObjectsOfType）：
        ///   一击必杀 / 无法移动 / 无敌 / 魅惑 / 必掉金币
        /// </summary>
        private void ApplyZombieEffects()
        {
            if (!Settings.OneHitKill && !Settings.ZombieFreeze && !Settings.ZombieGod
                && !Settings.CharmZombie && !Settings.ZombieCoin && !Settings.ZombieNoBack
                && Math.Abs(Settings.ZombieScale - 1f) <= 0.001f) return;

            ModDiag.KillRuns++;

            Zombie[] zombies = UnityEngine.Object.FindObjectsOfType<Zombie>();
            if (zombies == null) return;
            ModDiag.LastObjects = zombies.Length;
            ModDiag.LastKind = 2;

            for (int i = 0; i < zombies.Length; i++)
            {
                Zombie z = zombies[i];
                if (z == null) continue;

                try
                {
                    if (Settings.OneHitKill && z.ZombieHp > 0)
                    {
                        z.TakeDamage(9999999, 1);
                        ModDiag.KillHits++;
                        continue;                     // 已经死了，后面的不用改
                    }

                    if (Settings.ZombieGod && z.maxZombieHp > 0 && z.ZombieHp < z.maxZombieHp)
                    {
                        z.ZombieHp = z.maxZombieHp;
                        ModDiag.ZombieGodFixed++;
                    }

                    if (Settings.ZombieFreeze && z.walkSpeed != 0f)
                    {
                        z.walkSpeed = 0f;
                        ModDiag.ZombieFrozen++;
                    }

                    if (Settings.CharmZombie && !z.isMeihuo)
                    {
                        z.isMeihuo = true;
                        ModDiag.ZombieCharmed++;
                    }

                    if (Settings.ZombieCoin && z.coinPt < 100)
                    {
                        z.coinPt = 100;
                        ModDiag.ZombieCoinSet++;
                    }

                    if (Settings.ZombieNoBack && !z.noHui)
                    {
                        z.noHui = true;
                        ModDiag.ZombieNoBackSet++;
                    }

                    if (Math.Abs(Settings.ZombieScale - 1f) > 0.001f)
                    {
                        var want = new Vector3(Settings.ZombieScale, Settings.ZombieScale, 1f);
                        if (z.transform != null && z.transform.localScale.x != want.x)
                        {
                            z.transform.localScale = want;
                            ModDiag.ZombieScaled++;
                        }
                    }
                }
                catch (Exception ex) { ModDiag.Note("僵尸属性写入失败：" + ex.GetType().Name); }
            }
        }

        private void ApplyNoCooldown()
        {
            if (!Settings.NoCooldown) return;
            ModDiag.CoolRuns++;

            FieldInfo proField = ProField;
            if (proField == null) { ModDiag.Note("取不到 CardClick.pro"); return; }

            CardClick[] cards = UnityEngine.Object.FindObjectsOfType<CardClick>();
            if (cards == null) return;
            ModDiag.LastObjects = cards.Length;
            ModDiag.LastKind = 3;

            for (int i = 0; i < cards.Length; i++)
            {
                CardClick card = cards[i];
                if (card == null) continue;
                if (!(proField.GetValue(card) is Cardproperties pro)) continue;
                if (pro.nowcoolDown <= 0f) continue;
                pro.nowcoolDown = 0f;
                ModDiag.CoolFixed++;
            }
        }

        private void ApplyAutoSun()
        {
            if (!Settings.AutoSun) return;
            ModDiag.SunRuns++;

            FieldInfo f = SunStartTimeField;
            if (f == null) { ModDiag.Note("取不到 Sun.startTime"); return; }

            Sun[] suns = UnityEngine.Object.FindObjectsOfType<Sun>();
            if (suns == null) return;
            ModDiag.LastObjects = suns.Length;
            ModDiag.LastKind = 4;

            for (int i = 0; i < suns.Length; i++)
            {
                Sun s = suns[i];
                if (s == null) continue;
                object v = f.GetValue(s);
                // 只往前推一次（置 0），不做累计减法 —— 否则每帧减 10 会减成负数
                if (v is float fv && fv > 0f) { f.SetValue(s, 0f); ModDiag.SunFixed++; }
            }
        }

        // ---------------------------------------------------------------- 日志

        internal static void LogOnce(string tag, Exception ex)
        {
            if (!LoggedOnce.Add(tag)) return;
            Log("异常[" + tag + "] " + ex.GetType().Name + "：" + ex.Message);
        }

        internal static void Log(string line)
        {
            // AppendAllText 被裁剪，用「读 + 写」自己追加；顺带限制文件大小避免 O(n²) 膨胀
            try
            {
                string path = SavePaths.ModLogFile;
                string old = File.Exists(path) ? File.ReadAllText(path) : "";
                if (old.Length > 64 * 1024) old = "（日志已截断）\n";
                File.WriteAllText(path, old + line + "\n");
            }
            catch { }
        }
    }
}
