using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PvzShared;

namespace PvzPanel
{
    public partial class MainWindow : Window
    {
        private readonly PanelClient _client = new PanelClient();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly DispatcherTimer _actionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };

        private string _page = "connect";
        private string _pendingActionLabel;
        private string _scriptSource;      // 脚本页内容缓存（切页回来还在）

        /// <summary>上一次下发快照的时间，用于「待生效」自动重发。</summary>
        private DateTime _lastSnapshotSent = DateTime.MinValue;

        /// <summary>
        /// 自动重发间隔。只要还有未确认的项，就每隔这么久把整份快照再发一次。
        ///
        /// 为什么要自愈：游戏可能因为版本不一致、卡顿、或任何原因没收下某条命令（实测踩过：
        /// 旧版 mod 直接回「未知特性」），而面板只读状态、看不到拒绝。
        /// 光靠一次下发 + 等待，就会把开关永远停在「待生效」——用户只能当成功能坏了。
        /// 重发是幂等的（全量快照），不会把已经对的项改错。
        /// </summary>
        private static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(2.5);

        /// <summary>已自动重发次数，防无限刷。用户再动手时清零。</summary>
        private int _resendCount;

        /// <summary>重发上限。超了就停下并明说，而不是默默一直显示「同步中」。</summary>
        private const int ResendMax = 5;
        private string _confirmRestore;    // 还原备份的「再点一次」确认状态
        private bool _backgroundMode;      // 后台运行：不抢游戏焦点
        private readonly DateTime _startedAt = DateTime.Now;
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

        /// <summary>
        /// 用户在面板里想要的值。游戏上报的值与它不一致 → 这一项还没生效。
        /// 用「想要的」而不是「已发出的」：游戏失焦时命令会滞留很久，
        /// 用已发出的值会让界面永远显示待生效。
        /// </summary>
        private readonly Dictionary<string, string> _desired = new Dictionary<string, string>();

        // ---- 就地刷新所需的控件引用（页面只构建一次，之后只改值） ----

        private sealed class RowRefs
        {
            public ModFeature Feature;
            public TextBlock Chip;                              // 已生效 / 同步中
            public Border ChipBox;
            public ToggleButton Switch;
            public TextBlock ShelfPick;                         // "② 点商品填进第 N 格"
            public readonly List<KeyValuePair<string, ToggleButton>> Segments = new List<KeyValuePair<string, ToggleButton>>();
            public readonly List<KeyValuePair<int, ToggleButton>> ShelfSlots = new List<KeyValuePair<int, ToggleButton>>();
            public readonly List<KeyValuePair<int, ToggleButton>> ShelfItems = new List<KeyValuePair<int, ToggleButton>>();
        }

        private readonly Dictionary<string, RowRefs> _rows = new Dictionary<string, RowRefs>();
        private string _structureSig = "";      // 页面结构指纹：变了才重建

        private readonly Dictionary<string, TextBox> _saveBoxes = new Dictionary<string, TextBox>();
        private string _saveJson;
        private string _saveLoadError;

        /// <summary>
        /// 正在编辑的存档文件。null = 电脑版默认存档。
        /// 用户选「打开手机存档」后会变成他选的那个文件 —— 因为手机版是 IL2CPP，
        /// 注入不了代码，只能让他把存档拷出来改完再拷回去，所以面板得能指向任意路径。
        /// </summary>
        private string _savePath;

        private int _shelfSlot;
        // 默认「不改」而不是 9999：9999 在游戏里是「空格」，
        // 所以旧默认值下只要点一下「应用到货架」，6 格就被清成了空气。
        private int[] _shelfIds = { -1, -1, -1, -1, -1, -1 };
        private bool _shelfInit;

        private static readonly (int Id, string Name)[] ShopItems =
        {
            (0,  "初始阳光"), (1,  "猫粮"), (2,  "樱桃炸弹"), (3, "阳光精灵球"),
            (4,  "僵尸精灵球"), (5, "天降礼盒"), (6, "投资"), (7, "货币投资"),
            (8,  "劣质卡包"), (9, "叶子保护伞"), (10, "普通卡包"), (11, "稀有卡包"),
            (12, "史诗卡包"), (-1, "不改"), (9999, "空格"),
        };

        private static readonly (string Action, string Label, string Value)[] Actions =
        {
            ("unlock.allPlants",  "图鉴全解锁（所有卡牌）", null),
            ("unlock.allPacks",   "解锁卡包类型（进货架池）", null),
            ("progress.max",      "进度拉满", null),
            ("battle.killAll",    "秒杀全场僵尸", null),
            ("battle.sunFull",    "阳光给满", null),
            ("save.clearCheater", "清除作弊标记", null),
            ("save.giveMoney",    "金币设为 88888", "88888"),
            ("set.difficulty",    "难度设为困难", "2"),
            ("settings.reset",    "恢复默认设置", null),
        };

        public MainWindow()
        {
            InitializeComponent();

            // 改为壁纸背景方案，不再用 DWM 毛玻璃（两者会打架：毛玻璃会把壁纸慕化并压暗）
            Loaded += (s, e) => LoadWallpaper();

            // 提示条代替弹窗：点完按钮不打断操作，信息就在标题栏下面一行
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); ToastBar.Visibility = Visibility.Collapsed; };

            _actionTimer.Tick += (s, e) =>
            {
                _actionTimer.Stop();
                _client.ReadState();
                string msg = string.IsNullOrEmpty(_client.Last.LastResult) ? "（没有返回结果）" : _client.Last.LastResult;
                Toast((_pendingActionLabel ?? "动作") + "：" + msg, !_client.Last.LastActionOk);
            };

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => Tick();
            _timer.Start();

            _client.ReadState();
            Render();
        }

        // ================================================================ 标题栏

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            try { DragMove(); } catch { }
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void BtnClose_Click(object sender, RoutedEventArgs e) { Close(); }

        // ================================================================ 壁纸

        /// <summary>
        /// 加载 exe 同目录的 wallpaper.*（没有就保持白底，不报错）。
        /// 幂等：重复调用只是重新读一次文件，可以安全地在每次渲染时兜底调用。
        /// 优先级：wallpaper.mp4（视频）> wallpaper.jpg/png/webp/bmp（静态图）> 纯色底板。
        /// </summary>
        private void LoadWallpaper()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;

                // ★ 视频优先。注意两个坑：
                //   ① 不能再给 Wallpaper.Source 赋值，否则静态图会盖在视频上面
                //   ② BgImage 那张图也要清掉
                string vid = System.IO.Path.Combine(baseDir, "wallpaper.mp4");
                if (System.IO.File.Exists(vid))
                {
                    BgImage.Source = null;
                    PlayBgVideo(vid);
                    return;
                }

                foreach (string ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                {
                    string p = System.IO.Path.Combine(baseDir, "wallpaper" + ext);
                    if (!System.IO.File.Exists(p)) continue;

                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(p);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    Wallpaper.Source = bmp;
                    StopBgVideo();      // 从视频切回图片时，必须把视频层收掉
                    return;
                }

                // 两个都没有（比如用户手动删了文件）：把视频层也收掉，回到纯色底板
                StopBgVideo();
                Wallpaper.Source = null;
            }
            catch (Exception ex) { Toast("背景加载失败：" + ex.Message, true); }
        }

        /// <summary>
        /// 播放背景视频（静音、循环、铺满）。
        /// ★ XAML 里是 LoadedBehavior=Manual —— 播完不会自动循环，必须自己接 MediaEnded 回头重播。
        /// ★ 背景层绝不能加 BlurEffect/OpacityMask：WPF 要先把视频画进位图再处理，
        ///   会导致整体发糊、而且视频只渲染出一部分。
        /// </summary>
        private void PlayBgVideo(string path)
        {
            try
            {
                BgVideo.MediaEnded -= OnBgVideoEnded;
                BgVideo.MediaEnded += OnBgVideoEnded;
                BgVideo.MediaFailed -= OnBgVideoFailed;
                BgVideo.MediaFailed += OnBgVideoFailed;
                BgVideo.Source = new Uri(path);
                BgVideo.Position = TimeSpan.Zero;
                BgVideo.Visibility = Visibility.Visible;
                BgVideo.Play();
            }
            catch (Exception ex) { Toast("背景视频播放失败：" + ex.Message, true); }
        }

        private void OnBgVideoEnded(object sender, RoutedEventArgs e)
        {
            try { BgVideo.Position = TimeSpan.Zero; BgVideo.Play(); } catch { }
        }

        private void OnBgVideoFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string why = e?.ErrorException?.Message ?? "未知";
            Toast("背景视频解码失败：" + why + "（WPF 走系统解码器，只认 mp4）", true);
        }

        /// <summary>停掉视频。换成静态图时必须调用，否则视频层盖着图片，看起来像没换。</summary>
        private void StopBgVideo()
        {
            try
            {
                BgVideo.Stop();
                BgVideo.Source = null;
                BgVideo.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>当前生效的背景是什么（显示给用户看，避免“设了没反应”说不清）。</summary>
        private string BackgroundKind()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                if (System.IO.File.Exists(System.IO.Path.Combine(baseDir, "wallpaper.mp4")))
                    return "自定义视频（静音循环）";
                foreach (string ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                    if (System.IO.File.Exists(System.IO.Path.Combine(baseDir, "wallpaper" + ext)))
                        return "自定义图片";
            }
            catch { }
            return "纯色（默认）";
        }

        /// <summary>让用户挑一张背景（图片或视频）→ 复制到 exe 同目录 → 立即生效。</summary>
        private void PickWallpaper()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择背景（图片或视频）",
                Filter = "图片或视频|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.mp4"
                       + "|图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp"
                       + "|视频 (mp4)|*.mp4",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                string baseDir = AppContext.BaseDirectory;

                if (ext == ".webm")
                {
                    Toast("webm 播不了：WPF 视频走系统解码器，只认 mp4，请先转码。", true);
                    return;
                }

                // ---- 视频：存成 wallpaper.mp4，和杂交版同一个文件名 ----
                if (ext == ".mp4")
                {
                    // 先把静态图删掉，否则它和视频抢同一层
                    foreach (string e2 in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                    {
                        string oldPic = System.IO.Path.Combine(baseDir, "wallpaper" + e2);
                        if (System.IO.File.Exists(oldPic)) System.IO.File.Delete(oldPic);
                    }
                    System.IO.File.Copy(dlg.FileName,
                        System.IO.Path.Combine(baseDir, "wallpaper.mp4"), true);
                    LoadWallpaper();
                    Toast("背景视频已设置（静音循环播放）");
                    return;
                }

                // ---- 图片：删掉视频，否则视频层会盖着图片 ----
                string oldVid = System.IO.Path.Combine(baseDir, "wallpaper.mp4");
                if (System.IO.File.Exists(oldVid)) System.IO.File.Delete(oldVid);
                StopBgVideo();

                // 删掉别的扩展名，避免多个 wallpaper.* 抢着加载
                foreach (string e2 in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                {
                    string old = System.IO.Path.Combine(baseDir, "wallpaper" + e2);
                    if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                }

                System.IO.File.Copy(dlg.FileName,
                    System.IO.Path.Combine(baseDir, "wallpaper" + ext), true);

                LoadWallpaper();
                Toast("背景图已设置");
            }
            catch (Exception ex) { Toast("设置背景失败：" + ex.Message, true); }
        }

        /// <summary>清掉自定义背景，回到纯色底板。</summary>
        private void ClearWallpaper()
        {
            try
            {
                StopBgVideo();
                if (Wallpaper != null) Wallpaper.Source = null;
                BgImage.Source = null;

                string baseDir = AppContext.BaseDirectory;
                foreach (string e2 in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                {
                    string f = System.IO.Path.Combine(baseDir, "wallpaper" + e2);
                    if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
                }
                string v = System.IO.Path.Combine(baseDir, "wallpaper.mp4");
                if (System.IO.File.Exists(v)) System.IO.File.Delete(v);

                Toast("已恢复纯色背景");
            }
            catch (Exception ex) { Toast("清除背景失败：" + ex.Message, true); }
        }

        /// <summary>
        /// 后台运行模式下点面板不会把焦点从游戏里抢走 —— 游戏继续保持焦点、继续跑，
        /// 改一项马上就能在屏幕上看到，不用“切回去 → 看效果 → 再切回来”。
        /// 代价：窗口拿不到键盘，文本框输不了字；要输字时再点一下关掉。
        /// </summary>
        private void BtnBackground_Click(object sender, RoutedEventArgs e)
        {
            _backgroundMode = !_backgroundMode;
            AcrylicHelper.SetNoActivate(this, _backgroundMode);

            BtnBackground.Foreground = (Brush)FindResource(_backgroundMode ? "AccentBrush" : "SubTextBrush");
            BtnBackground.Content = _backgroundMode ? "后台✓" : "后台";

            Toast(_backgroundMode
                ? "后台运行已开：点面板不再抢游戏焦点，游戏会继续跑。\n要输入文字时（存档页 / 脚本页），再点一下「后台✓」关掉。"
                : "后台运行已关：面板恢复普通行为，可以正常输入文字。");
        }

        // ================================================================ 内联提示

        /// <summary>
        /// 把结果写在标题栏下面那一行，不再弹 MessageBox。
        /// 弹窗会打断操作、还得移动鼠标去点「确定」，连着调几个参数时很恼人。
        /// </summary>
        private void Toast(string message, bool error = false)
        {
            ToastText.Text = message ?? "";
            ToastText.Foreground = (Brush)FindResource(error ? "WarnBrush" : "AccentBrush");
            ToastBar.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ================================================================ 刷新

        private void Tick()
        {
            _client.ReadState();
            UpdateStatus();

            // 自愈：还有没被游戏确认的改动，就定期重发整份快照。
            // 封顶 ResendMax 次：若游戏一直不认，说明不是「没送到」而是真有问题，
            // 无限重发只会默默刷文件、并把界面永远钉在「同步中」。
            if (PendingCount() > 0 && _resendCount < ResendMax
                && DateTime.Now - _lastSnapshotSent > ResendAfter)
            {
                if (_client.SendSnapshot(_desired, out _))
                {
                    _lastSnapshotSent = DateTime.Now;
                    _resendCount++;
                }
            }

            switch (_page)
            {
                case "connect":
                    if (_structureSig != StructureSignature()) Render();
                    else RefreshValues();
                    break;

                case "mods":
                    // 结构没变就只刷值 —— 绝不重建控件，否则每秒重建会让点击落空、滚动条乱跳
                    if (_structureSig != StructureSignature()) Render();
                    else RefreshValues();
                    break;

                case "diag":
                    RefreshValues();
                    break;

                case "script":
                    if (_scriptStatus != null)
                        _scriptStatus.Text = ScriptStatusText();
                    break;
            }
        }

        /// <summary>页面结构指纹：功能清单/分组/选项变了才需要重建（mod 升级时）。</summary>
        private string StructureSignature()
        {
            if (_page == "connect")
                return "connect|" + (_client.Last.Ok ? 1 : 0) + "|" + (_client.GameFrozen ? 1 : 0)
                     + "|" + (_client.GameAlive ? 1 : 0) + "|" + _client.Last.Instructions.Count;

            if (_page != "mods") return _page;

            var sb = new System.Text.StringBuilder("mods");
            sb.Append(_client.Last.Ok ? 1 : 0);
            foreach (ModFeature f in _client.Last.Features)
            {
                sb.Append('|').Append(f.Key).Append(':').Append(f.Kind).Append(':').Append(f.Options.Count);
            }
            return sb.ToString();
        }

        private static bool SameValue(string a, string b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a == b) return true;
            if (double.TryParse(a, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double da) &&
                double.TryParse(b, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double db))
                return Math.Abs(da - db) < 0.0001;
            return false;
        }

        private string ValueOf(ModFeature f)
        {
            return _desired.TryGetValue(f.Key, out string v) ? v : f.Raw;
        }

        private bool NeedsApply(ModFeature f)
        {
            return _desired.TryGetValue(f.Key, out string want) && !SameValue(f.Raw, want);
        }

        private int PendingCount()
        {
            int n = 0;
            foreach (ModFeature f in _client.Last.Features) if (NeedsApply(f)) n++;
            return n;
        }

        // ================================================================ 状态

        private void UpdateStatus()
        {
            string shortText;
            Brush color;
            string longText;

            if (_client.GameAlive && _client.Last.Ok)
            {
                shortText = "已连接";
                color = (Brush)FindResource("AccentBrush");
                longText = "游戏在响应，改动即时生效。";
            }
            else if (_client.GameFrozen)
            {
                shortText = "后台暂停";
                color = (Brush)FindResource("AccentBrush");
                longText = "游戏失去焦点，主循环停住（引擎行为）。改动不会丢，切回游戏时一次性生效。";
            }
            else if (_client.GameProcessRunning)
            {
                shortText = "等待状态";
                color = (Brush)FindResource("WarnBrush");
                longText = "游戏进程在，但还没读到状态文件。";
            }
            else if (_client.Last.Ok)
            {
                shortText = "游戏已退出";
                color = (Brush)FindResource("WarnBrush");
                longText = "游戏已退出（状态文件是旧的）。";
            }
            else
            {
                shortText = "未连接";
                color = (Brush)FindResource("WarnBrush");
                longText = "游戏未启动或修改器未安装。";
            }

            int pending = PendingCount();
            if (pending > 0) shortText += " · 同步中 " + pending;

            // 命令被游戏拒了：必须看得见，否则用户只能当成「功能坏了」
            string cmdError = CommandErrorText();
            if (cmdError != null)
            {
                color = (Brush)FindResource("WarnBrush");
                longText = cmdError;
            }

            string pendingNote = null;
            if (pending > 0)
            {
                pendingNote = _resendCount >= ResendMax
                    ? "\n同步中：" + pending + " 项（已重试 " + ResendMax + " 次仍未被确认，"
                      + "多数是该项需要进关卡/进商店才有作用对象）"
                    : "\n同步中：" + pending + " 项（会自动重发）";
            }

            // 标题栏的状态文字（左栏那个长状态块已被杂交版布局取代，长文在「连接」页里）
            StatusText.Text = shortText;
            StatusDot.Fill = color;
        }

        // ================================================================ 两级导航

        /// <summary>
        /// 父类。形态照搬杂交版（Win10 设置式），父类按抽卡版真实内容定 ——
        /// 杂交版那 6 个父类里有「网络和 Internet」，抽卡版没有联机，硬套会出现空分类。
        /// </summary>
        private static readonly (string Key, string Zh, string Icon)[] Parents =
        {
            ("sys",  "系统", "\uE770"),
            ("game", "游戏", "\uE7FC"),
            ("data", "数据", "\uE7B8"),
            ("app",  "应用", "\uE71D"),
            ("acc",  "账户", "\uE77B"),
        };

        /// <summary>
        /// 子页。功能组页的 Key 与 FeatureCatalog 的组名一一对应（见 PageToGroup）。
        /// </summary>
        private static readonly (string Key, string Zh, string Icon, string Parent)[] Cats =
        {
            ("connect", "连接",     "\uE703", "sys"),
            ("system",  "系统",     "\uE713", "sys"),
            ("log",     "日志",     "\uE9D9", "sys"),

            ("gacha",   "抽卡",     "\uE8C7", "game"),
            ("money",   "经济",     "\uE8C7", "game"),
            ("battle",  "战斗",     "\uE7FC", "game"),
            ("zombie",  "僵尸",     "\uE7EE", "game"),
            ("plant",   "种植",     "\uE8D4", "game"),
            ("entity",  "实体属性", "\uE7C3", "game"),
            ("level",   "关卡",     "\uE81E", "game"),

            ("save",    "存档",     "\uE7B8", "data"),
            ("diag",    "诊断",     "\uE9D9", "data"),

            ("script",  "脚本",     "\uE943", "app"),

            ("about",   "关于",     "\uE946", "acc"),
            ("help",    "使用说明", "\uE897", "acc"),
        };

        /// <summary>子页 key → FeatureCatalog 里的中文组名。</summary>
        private static readonly Dictionary<string, string> PageToGroup = new Dictionary<string, string>
        {
            { "gacha",  "抽卡" },
            { "money",  "经济" },
            { "battle", "战斗" },
            { "zombie", "僵尸" },
            { "plant",  "种植" },
            { "entity", "实体属性" },
            { "level",  "关卡" },
            { "system", "系统" },
        };

        /// <summary>当前展开的父类；空串 = 停在父类层。</summary>
        private string _openParent = "";

        /// <summary>导航搜索关键词（空 = 不过滤）。</summary>
        private string _navQuery = "";

        /// <summary>
        /// 造一个 Win10 导航项：30px 图标列 + 文字 + 右侧箭头。
        /// 字形必须放在 TextBlock 里并显式设 IconFont —— 不给 Button 设 FontFamily，
        /// 否则可能渲染成白色空心方框（本项目踩过）。
        /// </summary>
        private Button NavButton(string zh, string icon, string trail)
        {
            var b = new Button { Style = (Style)FindResource("NavItem") };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ic = new TextBlock
            {
                Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14,
                Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            var tx = new TextBlock
            {
                Text = zh, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 14,
                Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            var tr = new TextBlock
            {
                Text = trail, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11,
                Foreground = (Brush)FindResource("SubBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(ic, 0); Grid.SetColumn(tx, 1); Grid.SetColumn(tr, 2);
            g.Children.Add(ic); g.Children.Add(tx); g.Children.Add(tr);
            b.Content = g;
            return b;
        }

        /// <summary>按 _openParent 重建左边导航列表。有搜索词时直接列匹配到的子页（跨父类）。</summary>
        private void BuildCats()
        {
            if (CatPanel == null) return;
            CatPanel.Children.Clear();

            if (_navQuery.Length > 0)
            {
                foreach (var c in Cats)
                {
                    if (c.Zh.IndexOf(_navQuery, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var b = NavButton(c.Zh, c.Icon, "");
                    b.DataContext = c.Key;
                    string k = c.Key;
                    b.Click += (s, e) => { HighlightCat(k); SelectCat(k); };
                    CatPanel.Children.Add(b);
                }
                HighlightCat(_page);
                return;
            }

            if (_openParent.Length == 0)
            {
                foreach (var p in Parents)
                {
                    var b = NavButton(p.Zh, p.Icon, "\uE76C");
                    string pk = p.Key;
                    b.Click += (s, e) => OpenParent(pk);
                    CatPanel.Children.Add(b);
                }
            }
            else
            {
                var par = Array.Find(Parents, x => x.Key == _openParent);
                var back = NavButton(par.Zh, "", "\uE72B");
                back.Click += (s, e) => { _openParent = ""; BuildCats(); };
                CatPanel.Children.Add(back);

                foreach (var c in Cats)
                {
                    if (c.Parent != _openParent) continue;
                    var b = NavButton(c.Zh, c.Icon, "");
                    b.DataContext = c.Key;
                    b.Margin = new Thickness(14, 0, 0, 0);   // 子页缩进（对齐 Win10）
                    string k = c.Key;
                    b.Click += (s, e) => { HighlightCat(k); SelectCat(k); };
                    CatPanel.Children.Add(b);
                }
            }
            HighlightCat(_page);
        }

        /// <summary>进入父类：先列子页，再显示父类概览页。</summary>
        private void OpenParent(string key)
        {
            _openParent = key;
            BuildCats();
            ShowParentOverview(key);
        }

        /// <summary>给当前页对应的导航项上选中色（其余清掉）。</summary>
        private void HighlightCat(string key)
        {
            foreach (UIElement child in CatPanel.Children)
            {
                if (!(child is Button b)) continue;
                bool on = (child as FrameworkElement)?.DataContext as string == key;
                b.Background = on ? (Brush)FindResource("NavSelBrush") : Brushes.Transparent;
            }
        }

        /// <summary>父类概览：210px 卡片网格，图标 + 名称 + 「打开设置」。</summary>
        private void ShowParentOverview(string parentKey)
        {
            ContentPanel.Children.Clear();
            PageTitle.Text = Array.Find(Parents, x => x.Key == parentKey).Zh;

            var wrap = new WrapPanel();
            foreach (var c in Cats)
            {
                if (c.Parent != parentKey) continue;

                var card = new Border
                {
                    Style = (Style)FindResource("SettingsCard"),
                    Width = 210, Margin = new Thickness(0, 0, 10, 10),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                var sp = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock
                {
                    Text = c.Icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16,
                    Foreground = (Brush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
                });
                head.Children.Add(new TextBlock
                {
                    Text = c.Zh, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 14,
                    Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(head);
                sp.Children.Add(new TextBlock
                {
                    Text = "打开设置", FontFamily = (FontFamily)FindResource("UiFont"),
                    FontSize = 11, Foreground = (Brush)FindResource("SubBrush"), Margin = new Thickness(0, 6, 0, 0),
                });
                card.Child = sp;

                string k = c.Key;
                card.MouseLeftButtonUp += (s, e) => { HighlightCat(k); SelectCat(k); };
                wrap.Children.Add(card);
            }
            ContentPanel.Children.Add(wrap);
        }

        private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            _navQuery = (SearchInput.Text ?? "").Trim();
            BuildCats();
        }

        private void OnUserCenter(object sender, RoutedEventArgs e)
        {
            _openParent = "acc";
            BuildCats();
            HighlightCat("about");
            SelectCat("about");
        }

        /// <summary>标题栏的「检测连接」：重读一次状态并刷新界面。</summary>
        private void OnDetect(object sender, RoutedEventArgs e)
        {
            _client.ReadState();
            Render();
            Toast(_client.Last.Ok ? "已连接：" + _client.Last.Version : "未连接：" + _client.Last.Error,
                  !_client.Last.Ok);
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }

        /// <summary>窗口加载完成：加载壁纸 + 建导航 + 进首页。</summary>
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            LoadWallpaper();
            BuildCats();
            SelectCat(_page);
        }

        private void SelectCat(string key)
        {
            ClosePanel();
            _page = key;
            Render();
        }

        /// <summary>切页时淡入（与杂交版同参数）。</summary>
        private void AnimatePageIn()
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            ContentPanel.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>收起底部展开面板。</summary>
        private void ClosePanel()
        {
            if (SliderPanel != null) SliderPanel.Visibility = Visibility.Collapsed;
            if (SliderBody != null) SliderBody.Children.Clear();
        }

        /// <summary>
        /// 日志子页：纯展示 mod 自己写的 PvzGachaMod.log 尾部。
        /// 不新增任何游戏侧功能、不改通信协议 —— 只是把一个已经在写、但界面上看不见的文件显示出来。
        /// </summary>
        private void RenderLog()
        {
            PageTitle.Text = "日志";

            string text;
            try
            {
                string path = SavePaths.ModLogFile;
                if (!File.Exists(path)) text = "（还没有日志文件。启动一次游戏并安装好 mod 就会出现。）";
                else
                {
                    string[] lines = File.ReadAllLines(path);
                    int from = Math.Max(0, lines.Length - 200);
                    text = string.Join("\n", lines, from, lines.Length - from);
                }
            }
            catch (Exception ex) { text = "读取日志失败：" + ex.Message; }

            ContentPanel.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 11.5,
                AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 520, Padding = new Thickness(12),
            });
        }

        // ================================================================ 渲染

        private StackPanel _head;

        private Panel Head
        {
            get
            {
                if (_head == null || !ContentPanel.Children.Contains(_head))
                {
                    _head = new StackPanel();
                    ContentPanel.Children.Insert(0, _head);
                }
                return _head;
            }
        }

        private void Render()
        {
            if (ContentPanel == null) return;
            ContentPanel.Children.Clear();
            _head = null;
            _rows.Clear();

            PageTitle.Text = PageTitleOf(_page);

            switch (_page)
            {
                case "script": RenderScript(); break;
                case "save":   RenderSave();   break;
                case "diag":   RenderDiag();   break;
                case "about":  RenderAbout();  break;
                case "help":   RenderHelp();   break;
                case "log":    RenderLog();    break;
                case "mods":                         // 旧 key，兼容保留
                case "gacha": case "money": case "battle": case "zombie":
                case "plant": case "entity": case "level": case "system":
                    RenderMods(); break;
                default: RenderConnect(); break;
            }

            _structureSig = StructureSignature();
            RefreshValues();
            AnimatePageIn();
        }

        private static string PageTitleOf(string key)
        {
            foreach (var c in Cats) if (c.Key == key) return c.Zh;
            if (key == "mods") return "修改器";
            return key;
        }

        /// <summary>只刷值，不碰控件树。点击开关、游戏状态变化都走这里。</summary>
        private void RefreshValues()
        {
            UpdateStatus();

            if (_page == "mods")
            {
                foreach (KeyValuePair<string, RowRefs> kv in _rows)
                {
                    ModFeature f = kv.Value.Feature;
                    string val = ValueOf(f);

                    RefreshChip(kv.Value, f);

                    if (kv.Value.Switch != null)
                    {
                        bool should = val == "true";
                        if (kv.Value.Switch.IsChecked != should) kv.Value.Switch.IsChecked = should;
                    }

                    foreach (KeyValuePair<string, ToggleButton> seg in kv.Value.Segments)
                    {
                        bool should = SameValue(seg.Key, val);
                        if (seg.Value.IsChecked != should) seg.Value.IsChecked = should;
                    }

                    if (kv.Value.ShelfPick != null)
                        kv.Value.ShelfPick.Text = "② 点商品填进第 " + (_shelfSlot + 1) + " 格";

                    foreach (KeyValuePair<int, ToggleButton> slot in kv.Value.ShelfSlots)
                    {
                        bool should = slot.Key == _shelfSlot;
                        if (slot.Value.IsChecked != should) slot.Value.IsChecked = should;
                        slot.Value.Content = (slot.Key + 1) + "· " + ItemName(_shelfIds[slot.Key]);
                    }

                    foreach (KeyValuePair<int, ToggleButton> item in kv.Value.ShelfItems)
                    {
                        bool should = _shelfIds[_shelfSlot] == item.Key;
                        if (item.Value.IsChecked != should) item.Value.IsChecked = should;
                    }
                }
            }
            else if (_page == "connect")
            {
                if (_connectBody != null) _connectBody.Text = ConnectBodyText();
            }
        }

        // ---------------------------------------------------------------- 连接页

        private TextBlock _connectBody;

        /// <summary>
        /// 这个开关现在能不能真的生效。
        /// 主菜单里开「零冷却」永远不会有效果（场上没有卡牌对象），
        /// 所以必须根据当前场景给提示 —— 否则用户只看到「已开」却毫无反应，只能当成功能坏了。
        /// </summary>
        private string SceneWarning(ModFeature f)
        {
            if (string.IsNullOrEmpty(f.Scene)) return null;
            if (!_client.Last.Ok) return null;

            string scene = _client.Last.Diag.Scene;
            if (string.IsNullOrEmpty(scene)) return null;

            if (f.Scene == "level") return scene == "SampleScene" ? null : "需进关卡";
            if (f.Scene == "shop") return scene == "shangdian" ? null : "需进商店";
            return null;
        }

        /// <summary>状态标记优先级：同步中 &gt; 场景不对 &gt; 已生效。</summary>
        private void RefreshChip(RowRefs refs, ModFeature f)
        {
            if (refs.Chip == null) return;

            // 用「同步中」而不是「待生效」：自动重发会在几秒内把它变成已生效，
            // 字样太吓人反而让用户以为功能坏了。
            if (NeedsApply(f)) { SetChip(refs, "同步中", (Brush)FindResource("SubTextBrush")); return; }

            string warn = SceneWarning(f);
            if (warn != null) { SetChip(refs, warn, (Brush)FindResource("SubTextBrush")); return; }

            if (f.IsBool && ValueOf(f) == "true") SetChip(refs, "已生效", (Brush)FindResource("AccentBrush"));
            else SetChip(refs, null, null);
        }

        /// <summary>
        /// 游戏最近一条命令的结果。失败时要显式报出来 ——
        /// 之前面板只读状态文件、不看错误，命令被拒了也一无所知，
        /// 开关就永远卡在「同步中」，用户只能当成功能坏了（实测就是这个）。
        /// </summary>
        private string CommandErrorText()
        {
            if (!_client.Last.Ok) return null;
            if (_client.Last.LastActionOk) return null;
            if (string.IsNullOrEmpty(_client.Last.LastResult)) return null;

            return "游戏拒绝了最近的命令：" + _client.Last.LastResult + "\n" +
                   "（多半是面板和游戏里的 mod 版本不一致，重新运行一次「安装.cmd」即可）";
        }

        private string ConnectBodyText()
        {
            if (_client.GameAlive && _client.Last.Ok)
            {
                return "一切正常。游戏在响应，面板上的改动会立刻生效。\n\n" +
                       "游戏版本：" + _client.Last.Version + "　MOD：" + _client.Last.ModVersion + "\n" +
                       "已处理命令：" + _client.Last.CommandsHandled + "\n" +
                       "当前场景：" + SceneName() + "\n\n" + SceneHint();
            }
            if (_client.GameFrozen)
            {
                return "游戏失去了焦点，主循环停住了 —— 这是引擎行为，不是故障。\n\n" +
                       "· 面板上的改动不会丢：命令在排队，切回游戏时一次性生效\n" +
                       "· 想立刻生效：点上面的「切到游戏」\n\n" +
                       "当前待生效：" + PendingCount() + " 项\n" +
                       "游戏版本：" + _client.Last.Version + "　MOD：" + _client.Last.ModVersion;
            }
            if (_client.GameProcessRunning)
                return "游戏进程在，但还没读到状态文件。\n等它进到主菜单再看看。";

            return (_client.Last.Error.Length > 0 ? _client.Last.Error : "未连接") + "\n\n" +
                   "1. 运行修改器目录下的「安装.cmd」\n" +
                   "2. 启动游戏\n" +
                   "3. 回到这里点「刷新状态」";
        }

        private void RenderConnect()
        {
            Head.Children.Add(Header("连接"));

            var row = new WrapPanel();
            row.Children.Add(Btn("刷新状态", 116, () => { _client.ReadState(); RefreshValues(); }));
            row.Children.Add(Btn("切到游戏", 106, () => { GameInfo.FocusGame(); }, true));
            row.Children.Add(Btn("启动游戏", 106, LaunchGame));
            row.Children.Add(Btn("打开存档目录", 136, () => OpenFolder(SavePaths.Root)));
            Card(row, null);

            _connectBody = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
            Card(_connectBody, "状态");

            // 首页就把指令集写清楚：用户不用翻文档就知道脚本能写什么
            BuildInstructionCards();

            if (_client.Last.FailedCapabilities.Count > 0)
                Card(new TextBlock
                {
                    Text = string.Join("\n", _client.Last.FailedCapabilities),
                    TextWrapping = TextWrapping.Wrap
                }, "不可用的能力（不影响其它功能）");
        }

        // ---------------------------------------------------------------- 修改器页

        private void RenderMods()
        {
            Head.Children.Add(Header("修改器"));
            _shelfInit = false;

            if (!_client.Last.Ok)
            {
                Card(new TextBlock
                {
                    Text = "还没收到游戏状态，请先启动游戏。\n「存档」页不需要连接，随时可用。",
                    TextWrapping = TextWrapping.Wrap
                }, "未连接");
                Head.Children.Add(Btn("启动游戏", 126, LaunchGame, true));
                return;
            }

            foreach (IGrouping<string, ModFeature> group in _client.Last.Features.GroupBy(f => f.Group))
            {
                ModFeature[] items = group.ToArray();

                // 纯开关走双列紧凑排布，界面短一半；带选项/输入的还是整行放，免得挤
                var panel = new StackPanel();
                panel.Children.Add(BuildGroupHead(group.Key, items));

                var dense = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                bool anyDense = false;

                foreach (ModFeature f in items)
                {
                    if (f.IsBool)
                    {
                        dense.Children.Add(BuildCompactToggle(f));
                        anyDense = true;
                    }
                    else
                    {
                        panel.Children.Add(BuildFeatureRow(f));
                    }
                }

                if (anyDense) panel.Children.Insert(1, dense);
                Card(panel, null);
            }

            Head.Children.Add(Header("一键动作"));
            var acts = new WrapPanel();
            foreach (var item in Actions)
            {
                string a = item.Action, v = item.Value, l = item.Label;
                acts.Children.Add(Btn(l, 0, () => RunAction(a, v, l)));
            }
            Card(acts, null);
        }

        /// <summary>分组标题：左边一条强调色竖条 + 分组名 + 「全开 / 全关」。</summary>
        private UIElement BuildGroupHead(string group, ModFeature[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

            row.Children.Add(new Border
            {
                Width = 3,
                Height = 15,
                CornerRadius = new CornerRadius(2),
                Background = GroupAccent(group),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            });

            row.Children.Add(new TextBlock
            {
                Text = group,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center
            });

            ModFeature[] bools = items.Where(f => f.IsBool).ToArray();
            if (bools.Length > 1)
            {
                var all = new Button
                {
                    Content = "全开",
                    Style = (Style)FindResource("GlassButton"),
                    FontSize = 11,
                    Padding = new Thickness(9, 3, 9, 3),
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                all.Click += (s, e) => ApplyMany(bools, "true");
                row.Children.Add(all);

                var none = new Button
                {
                    Content = "全关",
                    Style = (Style)FindResource("GlassButton"),
                    FontSize = 11,
                    Padding = new Thickness(9, 3, 9, 3),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                none.Click += (s, e) => ApplyMany(bools, "false");
                row.Children.Add(none);
            }

            return row;
        }

        private static Brush GroupAccent(string group)
        {
            switch (group)
            {
                case "抽卡": return new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                case "战斗": return new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
                case "僵尸": return new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));
                case "植物": return new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                case "数值": return new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                case "关卡": return new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));
                default: return new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            }
        }

        /// <summary>双列紧凑开关：一行放下「名称 + 开关」。</summary>
        private UIElement BuildCompactToggle(ModFeature f)
        {
            var refs = new RowRefs { Feature = f };
            _rows[f.Key] = refs;

            var box = new Border
            {
                Width = 208,
                Margin = new Thickness(0, 0, 10, 10),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 10, 8),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                ToolTip = string.IsNullOrEmpty(f.Hint) ? f.Label : f.Hint
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = f.Label, FontSize = 12.5 });

            refs.ChipBox = new Border
            {
                Margin = new Thickness(0, 2, 0, 0),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed,
                Child = new TextBlock { FontSize = 10 }
            };
            refs.Chip = (TextBlock)refs.ChipBox.Child;
            left.Children.Add(refs.ChipBox);
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            var sw = new ToggleButton
            {
                Style = (Style)FindResource("GlassSwitch"),
                IsChecked = ValueOf(f) == "true",
                VerticalAlignment = VerticalAlignment.Center
            };
            sw.Click += (s, e) => Apply(f.Key, sw.IsChecked == true ? "true" : "false");
            refs.Switch = sw;
            Grid.SetColumn(sw, 1);
            row.Children.Add(sw);

            box.Child = row;
            return box;
        }

        /// <summary>一次改一组开关（全开/全关），只发一条快照。</summary>
        private void ApplyMany(ModFeature[] items, string value)
        {
            foreach (ModFeature f in items) _desired[f.Key] = value;
            _resendCount = 0;

            if (!_client.SendSnapshot(_desired, out string error))
            {
                Toast(error, true);
                return;
            }
            _lastSnapshotSent = DateTime.Now;
            RefreshValues();
        }

        private UIElement BuildFeatureRow(ModFeature f)
        {
            var refs = new RowRefs { Feature = f };
            _rows[f.Key] = refs;

            var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = f.Label, FontWeight = FontWeights.SemiBold, MinWidth = 92 });

            refs.ChipBox = new Border
            {
                Margin = new Thickness(8, 1, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 1.5, 7, 1.5),
                Visibility = Visibility.Collapsed,
                Child = new TextBlock { FontSize = 10.5 }
            };
            refs.Chip = (TextBlock)refs.ChipBox.Child;
            head.Children.Add(refs.ChipBox);
            outer.Children.Add(head);

            if (!string.IsNullOrEmpty(f.Hint))
            {
                outer.Children.Add(new TextBlock
                {
                    Text = f.Hint,
                    FontSize = 11.5,
                    Foreground = (Brush)FindResource("SubTextBrush"),
                    Margin = new Thickness(0, 3, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                outer.Children.Add(new StackPanel { Height = 8 });
            }

            UIElement editor;
            if (f.IsBool) editor = BuildSwitch(f, refs);
            else if (f.Key == "gacha.shelf") editor = BuildShelfEditor(f, refs);
            else if (f.HasOptions) editor = BuildSegments(f, refs);
            else editor = BuildPlainInput(f);

            outer.Children.Add(editor);
            return outer;
        }

        private void SetChip(RowRefs refs, string text, Brush color)
        {
            if (string.IsNullOrEmpty(text))
            {
                refs.ChipBox.Visibility = Visibility.Collapsed;
                return;
            }

            refs.Chip.Text = text;
            refs.Chip.Foreground = color;
            var c = ((SolidColorBrush)color).Color;
            refs.ChipBox.Background = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B));
            refs.ChipBox.Visibility = Visibility.Visible;
        }

        private UIElement BuildSwitch(ModFeature f, RowRefs refs)
        {
            var sw = new ToggleButton
            {
                Style = (Style)FindResource("GlassSwitch"),
                IsChecked = ValueOf(f) == "true",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            sw.Click += (s, e) => Apply(f.Key, sw.IsChecked == true ? "true" : "false");
            refs.Switch = sw;
            return sw;
        }

        private UIElement BuildSegments(ModFeature f, RowRefs refs)
        {
            var wrap = new WrapPanel();
            string cur = ValueOf(f);

            foreach (KeyValuePair<string, string> opt in f.Options)
            {
                string val = opt.Key;
                var b = new ToggleButton
                {
                    Content = opt.Value,
                    Style = (Style)FindResource("Segment"),
                    IsChecked = SameValue(val, cur)
                };
                b.Click += (s, e) => Apply(f.Key, val);
                refs.Segments.Add(new KeyValuePair<string, ToggleButton>(val, b));
                wrap.Children.Add(b);
            }
            return wrap;
        }

        private UIElement BuildPlainInput(ModFeature f)
        {
            var wrap = new StackPanel { Orientation = Orientation.Horizontal };
            var box = new TextBox { Text = ValueOf(f), Width = 150 };
            wrap.Children.Add(box);
            var ok = Btn("应用", 72, () => Apply(f.Key, box.Text.Trim()));
            ok.Margin = new Thickness(8, 0, 0, 0);
            wrap.Children.Add(ok);
            return wrap;
        }

        private UIElement BuildShelfEditor(ModFeature f, RowRefs refs)
        {
            SyncShelfFromSetting(f);

            var root = new StackPanel();

            root.Children.Add(new TextBlock
            {
                Text = "① 点要改的槽位",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 0, 0, 7)
            });

            var slots = new WrapPanel();
            for (int i = 0; i < _shelfIds.Length; i++)
            {
                int idx = i;
                var b = new ToggleButton
                {
                    Content = (idx + 1) + "· " + ItemName(_shelfIds[idx]),
                    Style = (Style)FindResource("Segment"),
                    IsChecked = idx == _shelfSlot
                };
                // 选槽位只刷新值，不重建页面（否则整页闪一下）
                b.Click += (s, e) => { _shelfSlot = idx; RefreshValues(); };
                refs.ShelfSlots.Add(new KeyValuePair<int, ToggleButton>(idx, b));
                slots.Children.Add(b);
            }
            root.Children.Add(slots);

            refs.ShelfPick = new TextBlock
            {
                Text = "② 点商品填进第 " + (_shelfSlot + 1) + " 格",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 9, 0, 7)
            };
            root.Children.Add(refs.ShelfPick);

            var items = new WrapPanel();
            foreach (var item in ShopItems)
            {
                int id = item.Id;
                var b = new ToggleButton
                {
                    Content = item.Name,
                    Style = (Style)FindResource("Tile"),
                    IsChecked = _shelfIds[_shelfSlot] == id
                };
                b.Click += (s, e) => { _shelfIds[_shelfSlot] = id; RefreshValues(); };
                refs.ShelfItems.Add(new KeyValuePair<int, ToggleButton>(id, b));
                items.Children.Add(b);
            }
            root.Children.Add(items);

            var actions = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
            actions.Children.Add(Btn("应用到货架", 130, () => Apply(f.Key, string.Join(",", _shelfIds)), true));
            actions.Children.Add(Btn("全放卡包", 106, () => { _shelfIds = new[] { 8, 10, 11, 12, 8, 10 }; RefreshValues(); }));
            actions.Children.Add(Btn("全部不改", 106, () => { _shelfIds = new[] { -1, -1, -1, -1, -1, -1 }; RefreshValues(); }));
            actions.Children.Add(Btn("全部留空", 106, () => { _shelfIds = new[] { 9999, 9999, 9999, 9999, 9999, 9999 }; RefreshValues(); }));
            root.Children.Add(actions);

            return root;
        }

        private void SyncShelfFromSetting(ModFeature f)
        {
            string cur = ValueOf(f);
            if (string.IsNullOrWhiteSpace(cur))
            {
                if (!_shelfInit) { _shelfIds = new[] { -1, -1, -1, -1, -1, -1 }; _shelfInit = true; }
                return;
            }

            string[] parts = cur.Split(',');
            for (int i = 0; i < _shelfIds.Length && i < parts.Length; i++)
                if (int.TryParse(parts[i].Trim(), out int id)) _shelfIds[i] = id;
            _shelfInit = true;
        }

        private static string ItemName(int id)
        {
            foreach (var item in ShopItems) if (item.Id == id) return item.Name;
            return "?" + id;
        }

        // ---------------------------------------------------------------- 脚本页

        private TextBox _scriptBox;
        private TextBlock _scriptStatus;

        private string ScriptStatusText()
        {
            ModScriptState s = _client.Last.Script;
            if (!_client.Last.Ok) return "未连接游戏，脚本需要在游戏运行时才能跑。";
            if (string.IsNullOrEmpty(s.Status)) return "就绪。";

            return s.Status +
                   (s.Line > 0 ? "\n当前执行到第 " + s.Line + " 行" : "") +
                   (s.Executed > 0 ? "，已执行 " + s.Executed + " 条指令" : "");
        }

        private void RenderScript()
        {
            _head = null;
            Head.Children.Add(Header("脚本"));
            _scriptBox = null;
            _scriptStatus = null;

            Card(new TextBlock
            {
                Text = "用下面的指令集写一段脚本，点「运行」后由游戏里的 mod 逐条解释执行。\n" +
                       "脚本跑在游戏进程里，所以能直接改开关、执行动作；wait 不会卡住游戏。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 21
            }, "怎么用");

            var box = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 210,
                FontFamily = (FontFamily)FindResource("NumFont"),
                FontSize = 12.5,
                Padding = new Thickness(12, 10, 12, 10)
            };
            box.Text = _scriptSource ?? InstructionSet.SampleScript;
            _scriptBox = box;

            var wrap = new Grid();
            wrap.Children.Add(box);
            Card(wrap, "脚本内容");

            var row = new WrapPanel();
            row.Children.Add(Btn("运行脚本", 120, RunScript, true));
            row.Children.Add(Btn("停止", 90, StopScript));
            row.Children.Add(Btn("载入示例", 110, () =>
            {
                if (_scriptBox != null) _scriptBox.Text = InstructionSet.SampleScript;
            }));
            row.Children.Add(Btn("清空", 90, () => { if (_scriptBox != null) _scriptBox.Text = ""; }));
            Card(row, null);

            _scriptStatus = new TextBlock
            {
                Text = ScriptStatusText(),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21
            };
            Card(_scriptStatus, "运行状态");

            BuildInstructionCards();
        }

        private void RunScript()
        {
            if (_scriptBox == null) return;

            string src = _scriptBox.Text ?? "";
            if (src.Trim().Length == 0) { Toast("脚本是空的，先写点指令。", true); return; }

            _scriptSource = src;

            if (!_client.SendScript(src, out string error))
            {
                Toast(error, true);
                return;
            }

            if (_scriptStatus != null)
                _scriptStatus.Text = "已下发，等游戏执行…\n（若游戏在后台暂停，切回游戏才会开始）";
        }

        private void StopScript()
        {
            string error;
            if (!_client.SendStopScript(out error))
                Toast(error, true);
        }

        /// <summary>
        /// 指令集速查表。内容直接来自 mod 下发的 InstructionSet，
        /// 所以「文档」和「解析器实现」永远是同一份，不会写歪。
        /// </summary>
        private void BuildInstructionCards()
        {
            var list = _client.Last.Instructions;
            if (list.Count == 0)
            {
                Card(new TextBlock
                {
                    Text = "指令集需要连上游戏后才会显示（由 mod 下发）。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("SubTextBrush")
                }, "指令集");
                return;
            }

            var panel = new StackPanel();

            var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            AddCell(head, 0, "指令", true);
            AddCell(head, 1, "说明", true);
            AddCell(head, 2, "示例", true);
            panel.Children.Add(head);

            foreach (InstructionInfo it in list)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

                AddCell(row, 0, it.Syntax, false, (Brush)FindResource("AccentBrush"), (FontFamily)FindResource("NumFont"));
                AddCell(row, 1, it.Summary, false);
                AddCell(row, 2, it.Example, false, (Brush)FindResource("SubTextBrush"), (FontFamily)FindResource("NumFont"));

                panel.Children.Add(row);
            }

            Card(panel, "指令集（写脚本就用这些）");

            if (_client.Last.ValueHints.Count > 0)
            {
                Card(new TextBlock
                {
                    Text = string.Join("\n", _client.Last.ValueHints),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21,
                    Foreground = (Brush)FindResource("SubTextBrush")
                }, "值的写法");
            }
        }

        private static void AddCell(Grid row, int col, string text, bool header,
            Brush color = null, FontFamily font = null)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = header ? 11.5 : 12,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (color != null) tb.Foreground = color;
            else if (header) tb.Foreground = (Brush)Application.Current.FindResource("SubTextBrush");
            if (font != null) tb.FontFamily = font;

            Grid.SetColumn(tb, col);
            row.Children.Add(tb);
        }

        // ---------------------------------------------------------------- 场景

        private string SceneName()
        {
            string s = _client.Last.Diag.Scene;
            if (string.IsNullOrEmpty(s)) return "未知";
            if (s == "Zhucaidan") return "主菜单";
            if (s == "SampleScene") return "关卡内";
            return s;
        }

        private string SceneHint()
        {
            if (!_client.Last.Ok) return "";

            string scene = _client.Last.Diag.Scene;
            if (scene == "Zhucaidan")
                return "你在主菜单：抽卡类要去商店买一次卡包才会触发，战斗类要进关卡才有作用对象。";
            if (scene == "SampleScene")
                return "你在关卡内：战斗类功能现在有效；抽卡类仍然要去商店。";
            return "进关卡后战斗类才有效；在商店买卡包才会触发抽卡类。";
        }

        // ---------------------------------------------------------------- 诊断页

        private void RenderDiag()
        {
            Head.Children.Add(Header("诊断"));

            var hint = new TextBlock
            {
                Text = "这些是 mod 在游戏里实测的计数。功能「没反应」时，看这里就知道断在哪一环。",
                Foreground = (Brush)FindResource("SubTextBrush"),
                TextWrapping = TextWrapping.Wrap
            };
            Card(hint, null);

            if (!_client.Last.Ok) return;

            ModDiagInfo d = _client.Last.Diag;

            var env = new StackPanel();
            DiagRow(env, "当前场景", SceneName() + "（" + (d.Scene.Length > 0 ? d.Scene : "?") + "）", null);
            DiagRow(env, "关卡对象", d.Get("objects") + " 个（" + KindName(d.Get("objectsKind")) + "）", null);
            DiagRow(env, "金币写回", d.Get("coinWrites") + " 次", null);
            DiagRow(env, "MOD 版本", _client.Last.ModVersion, null);
            Card(env, "环境");

            var bg = new StackPanel();
            int bgIns = d.Get("bgInstalled");
            int bgSw = d.Get("bgSwallowed");
            DiagRow(bg, "窗口挂钩", bgIns == 1 ? "已装上" : "没装上",
                bgIns == 1 ? "" : "打开「系统」里的「游戏后台运行」就会自动装上");
            DiagRow(bg, "已拦下的失焦消息", bgSw + " 条",
                bgIns == 1 && bgSw > 0
                    ? ""
                    : (bgIns == 1
                        ? "挂上了但还没失焦过 —— 切到别的窗口就会开始拦"
                        : "挂钩没装上，游戏切出去还是会停"));
            DiagRow(bg, "判断依据", "失焦后若「状态更新」还是「刚刚」，说明游戏没被暂停",
                null);
            Card(bg, "后台运行");

            var inj = new StackPanel();
            DiagRow(inj, "抽卡档位钩子", d.Get("tierCalls") + " 次调用 / 生效 " + d.Get("tierApplied") + " 次",
                Judge(d.Get("tierCalls"), d.Get("tierApplied"),
                    "游戏还没调用到这个钩子 —— 还没买过卡包",
                    "调用到了但档位一直是「关闭」，去上面把档位选上"));
            DiagRow(inj, "货架钩子", d.Get("shelfCalls") + " 次调用 / 生效 " + d.Get("shelfApplied") + " 次",
                Judge(d.Get("shelfCalls"), d.Get("shelfApplied"),
                    "游戏还没重建过货架（进一次商店会触发）",
                    "货架设置没通过校验"));
            DiagRow(inj, "购买钩子", d.Get("buyCalls") + " 次调用 / 生效 " + d.Get("buyApplied") + " 次",
                Judge(d.Get("buyCalls"), d.Get("buyApplied"),
                    "还没在商店里点过购买",
                    "买过但都不是卡包，或「抽卡免费」没打开"));
            Card(inj, "抽卡拦截");

            var bat = new StackPanel();
            DiagRow(bat, "植物无敌", d.Get("plantRuns") + " 次执行 / 修复 " + d.Get("plantFixed") + " 个",
                Judge(d.Get("plantRuns"), d.Get("plantFixed"), "开关没打开", "执行过但场上没植物"));
            DiagRow(bat, "一击必杀", d.Get("killRuns") + " 次执行 / 命中 " + d.Get("killHits") + " 个",
                Judge(d.Get("killRuns"), d.Get("killHits"), "开关没打开", "执行过但场上没有活僵尸"));
            DiagRow(bat, "零冷却", d.Get("coolRuns") + " 次执行 / 清零 " + d.Get("coolFixed") + " 个",
                Judge(d.Get("coolRuns"), d.Get("coolFixed"), "开关没打开", "执行过但冷却本来就是 0"));
            DiagRow(bat, "自动收阳光", d.Get("sunRuns") + " 次执行 / 处理 " + d.Get("sunFixed") + " 个",
                Judge(d.Get("sunRuns"), d.Get("sunFixed"), "开关没打开", "执行过但场上没有可收的阳光"));
            Card(bat, "战斗类");

            if (d.Notes.Length > 0)
                Card(new TextBlock
                {
                    Text = d.Notes.Replace("；", "\n"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("WarnBrush")
                }, "mod 记录到的情况");
        }

        private static string Judge(int runs, int applied, string noRuns, string noEffect)
        {
            if (runs == 0) return noRuns;
            return applied == 0 ? noEffect : "";
        }

        private static void DiagRow(Panel parent, string label, string value, string warn)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.FindResource("SubTextBrush")
            });
            row.Children.Add(new TextBlock { Text = value, FontSize = 13.5 });
            if (!string.IsNullOrEmpty(warn))
            {
                row.Children.Add(new TextBlock
                {
                    Text = "→ " + warn,
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.FindResource("WarnBrush"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            parent.Children.Add(row);
        }

        private static string KindName(int kind)
        {
            switch (kind)
            {
                case 1: return "植物";
                case 2: return "僵尸";
                case 3: return "卡牌";
                case 4: return "阳光";
                default: return "没找到对象";
            }
        }

        // ---------------------------------------------------------------- 存档页

        private void RenderSave()
        {
            _saveBoxes.Clear();
            Head.Children.Add(Header("存档编辑"));

            string cur = _savePath ?? SavePaths.SaveFile;
            bool isDefault = _savePath == null;

            _saveJson = SaveEditorLogic.LoadTextFrom(cur, out _saveLoadError);

            if (_saveJson == null)
            {
                Card(new TextBlock
                {
                    Text = _saveLoadError + "\n\n" +
                           (isDefault
                               ? "可以先点下面的按钮生成一份默认存档。"
                               : "这份文件不可用，换一个文件，或切回电脑版存档。") + "\n" +
                           "注意：改存档前请先退出游戏，否则游戏会用内存数据覆盖掉。",
                    TextWrapping = TextWrapping.Wrap
                }, "没有存档");

                var missRow = new WrapPanel();
                if (isDefault)
                {
                    missRow.Children.Add(Btn("生成默认存档", 180, () =>
                    {
                        string def = SaveEditorLogic.SynthesizeDefaultSaveJson();
                        if (SaveEditorLogic.SaveWithBackup(def, out string bp, out string err))
                            Toast("已生成默认存档。备份：" + (bp ?? "（原本无存档，未做备份）"));
                        else
                            Toast(err, true);
                        Render();
                    }, true));
                }
                missRow.Children.Add(Btn("打开手机存档…", 0, PickSaveFile));
                if (!isDefault) missRow.Children.Add(Btn("切回电脑版存档", 0, UseDefaultSave));
                Card(missRow, null);
                return;
            }

            bool hashOk = SaveHash.Verify(cur, SaveEditorLogic.HashPathOf(cur));

            var stateCard = new StackPanel();
            stateCard.Children.Add(InfoRow("当前编辑", isDefault ? "电脑版存档（默认）" : "外部存档（手机版）"));
            stateCard.Children.Add(InfoRow("文件", cur));
            stateCard.Children.Add(InfoRow("校验", hashOk ? "通过" : "不通过（保存一次会自动重算）"));

            // 拷错文件是手机版最常见的坑：拿成 wujin.json 或别的游戏的 json，
            // 存回去游戏就读不了存档了。所以这里提前提示，而不是等出事。
            if (!isDefault && !SaveEditorLogic.LooksLikeSaveFile(cur, out string why))
                stateCard.Children.Add(InfoRow("⚠ 警告", why));

            stateCard.Children.Add(new TextBlock
            {
                Text = "请先退出游戏再改；保存时自动备份，可随时还原。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = 12,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 8, 0, 0)
            });
            Card(stateCard, "状态");

            var pickRow = new WrapPanel();
            pickRow.Children.Add(Btn("打开手机存档…", 0, PickSaveFile));
            if (!isDefault) pickRow.Children.Add(Btn("切回电脑版存档", 0, UseDefaultSave));
            Card(pickRow, null);

            var card = new StackPanel();
            Dictionary<string, string> fields = SaveEditorLogic.FlattenFields(_saveJson);
            List<string> keys = fields.Keys.ToList();
            keys.Sort(StringComparer.Ordinal);

            foreach (string key in keys)
            {
                if (key == SaveEditorLogic.ProtectedField) continue;

                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(186) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                var box = new TextBox { Text = fields[key] };
                Grid.SetColumn(box, 1);
                row.Children.Add(box);

                _saveBoxes[key] = box;
                card.Children.Add(row);
            }
            Card(card, "字段");

            var btnRow = new WrapPanel();
            btnRow.Children.Add(Btn("保存（自动备份 + 重算校验）", 226, SaveEdits, true));
            btnRow.Children.Add(Btn("重新载入", 106, Render));
            Card(btnRow, null);

            string[] backups = SaveEditorLogic.RestoreBackupsFrom(SaveEditorLogic.BackupDirOf(cur));
            if (backups.Length > 0)
            {
                Head.Children.Add(Header("还原备份"));
                var wrap = new WrapPanel();
                foreach (string path in backups)
                {
                    string p = path;
                    wrap.Children.Add(Btn("还原 " + Path.GetFileName(p), 0, () =>
                    {
                        // 两段式确认代替弹窗：第一次点只是「武装」，再点一次才真覆盖。
                        // 弹窗要移动鼠标去点确定，频繁操作时很烦；这样也不会误触。
                        if (_confirmRestore != p)
                        {
                            _confirmRestore = p;
                            Toast("要覆盖当前存档吗？再点一下这个按钮就确认。存档会先自动备份。");
                            return;
                        }
                        _confirmRestore = null;

                        if (SaveEditorLogic.RestoreToPath(_savePath ?? SavePaths.SaveFile, p, out string err))
                        {
                            Toast("已还原备份。");
                            Render();
                        }
                        else Toast(err, true);
                    }));
                }
                Card(wrap, null);
            }
        }

        /// <summary>
        /// 选一份外部存档（手机版）。
        /// 旁边那个 .md5 会一起处理（保存时重算）——所以提示用户两个文件都要拷出来。
        /// </summary>
        private void PickSaveFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择手机版的 save.json",
                Filter = "抽卡版存档 (save.json)|*.json|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;

            if (!SaveEditorLogic.LooksLikeSaveFile(dlg.FileName, out string why))
            {
                Toast("这个文件不能用：" + why, true);
                return;
            }

            _savePath = dlg.FileName;
            Toast("已切换到这个存档，保存时会写回同一位置并重算配套的 .md5");
            Render();
        }

        private void UseDefaultSave()
        {
            _savePath = null;
            Toast("已切回电脑版存档");
            Render();
        }

        private void SaveEdits()
        {
            Dictionary<string, string> fields = SaveEditorLogic.FlattenFields(_saveJson);
            var edits = new Dictionary<string, string>();

            foreach (KeyValuePair<string, TextBox> kv in _saveBoxes)
                if (kv.Value.Text != fields[kv.Key]) edits[kv.Key] = kv.Value.Text.Trim();

            if (edits.Count == 0) { Toast("没有改动。"); return; }

            try
            {
                string updated = SaveEditorLogic.ApplyEdits(_saveJson, edits);
                string target = _savePath ?? SavePaths.SaveFile;

                bool ok = _savePath == null
                    ? SaveEditorLogic.SaveWithBackup(updated, out string bp, out string err)
                    : SaveEditorLogic.SaveWithBackupToPath(target, updated, out bp, out err);

                if (ok)
                {
                    Toast("已保存 " + edits.Count + " 个字段。备份：" + bp);
                    Render();
                }
                else Toast(err, true);
            }
            catch (Exception ex)
            {
                Toast("字段内容不是合法 JSON：" + ex.Message, true);
            }
        }

        // ---------------------------------------------------------------- 说明页

        // ================================================================ 关于 / 用户中心

        private const string SponsorUrl = "https://www.html2web.com/p/wvtla9l8";
        private const string BiliUrl    = "https://space.bilibili.com/3546789573561037";

        /// <summary>使用时长，和参考版那个用户中心一样的写法。</summary>
        private string FormatUptime()
        {
            TimeSpan t = DateTime.Now - _startedAt;
            if (t.TotalHours >= 1) return string.Format("{0} 小时 {1} 分", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0} 分 {1} 秒", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0} 秒", (int)t.TotalSeconds);
        }

        private void RenderAbout()
        {
            Head.Children.Add(Header("关于"));

            var state = _client.Last;

            // ---- 连接信息 ----
            var conn = new StackPanel();
            conn.Children.Add(InfoRow("连接状态", !string.IsNullOrEmpty(state.Version) ? "已连接" : "未连接"));
            conn.Children.Add(InfoRow("已开功能", _desired.Count == 0 ? "全默认" : _desired.Count + " 项"));
            conn.Children.Add(InfoRow("游戏版本", string.IsNullOrEmpty(state.Version) ? "?" : state.Version));
            conn.Children.Add(InfoRow("Mod 版本", string.IsNullOrEmpty(state.ModVersion) ? "?" : state.ModVersion));
            Card(conn, "连接信息");

            // ---- 后台运行 ----
            var bg = new StackPanel();
            var bgRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var bgSwitch = new ToggleButton
            {
                Style = (Style)FindResource("GlassSwitch"),
                IsChecked = _backgroundMode,
                VerticalAlignment = VerticalAlignment.Center
            };
            bgSwitch.Click += (s, e) =>
            {
                _backgroundMode = bgSwitch.IsChecked == true;
                AcrylicHelper.SetNoActivate(this, _backgroundMode);
                BtnBackground.Foreground = (Brush)FindResource(_backgroundMode ? "AccentBrush" : "SubTextBrush");
                BtnBackground.Content = _backgroundMode ? "后台✓" : "后台";
                Toast(_backgroundMode ? "后台运行已开。" : "后台运行已关。");
            };
            var bgLabel = new TextBlock { Text = "后台运行", FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            bgRow.Children.Add(bgSwitch);
            bgRow.Children.Add(bgLabel);
            bg.Children.Add(bgRow);
            bg.Children.Add(new TextBlock
            {
                Text = "开了之后点面板不会把焦点从游戏里抢走，游戏保持运行 —— 改一项马上能看到效果。\n" +
                       "需要输入文字时（存档页 / 脚本页）再关掉，也可以直接点标题栏右侧的「后台」。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = 12,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 8, 0, 0)
            });
            Card(bg, "后台运行");

            // ---- 存档状态 ----
            var save = new StackPanel();
            save.Children.Add(InfoRow("存档文件", SavePaths.SaveFile));
            try
            {
                if (File.Exists(SavePaths.SaveFile))
                {
                    var fi = new FileInfo(SavePaths.SaveFile);
                    save.Children.Add(InfoRow("文件大小", (fi.Length / 1024) + " KB"));
                    save.Children.Add(InfoRow("最后修改", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
                }
                else save.Children.Add(InfoRow("存档文件", "不存在"));
            }
            catch { }
            save.Children.Add(InfoRow("目录", SavePaths.Dir));
            var saveBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            saveBtns.Children.Add(MiniButton("打开存档目录", () => OpenFolder(SavePaths.Dir)));
            save.Children.Add(saveBtns);
            Card(save, "存档状态");

            // ---- 使用时长 ----
            var up = new StackPanel();
            up.Children.Add(InfoRow("本次已运行", FormatUptime()));
            up.Children.Add(InfoRow("已收命令", state.CommandsHandled.ToString()));
            Card(up, "使用时长");

            // ---- 背景（图片 / 视频）----
            var bgCard = new StackPanel();
            bgCard.Children.Add(InfoRow("当前", BackgroundKind()));
            bgCard.Children.Add(new TextBlock
            {
                Text = "视频存成 exe 同目录的 wallpaper.mp4，静音循环播放。"
                     + "WPF 的播放走系统解码器，只认 mp4，webm 请先转码。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = 12,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 8, 0, 0)
            });
            var bgCardBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            bgCardBtns.Children.Add(MiniButton("选择背景（图片或视频）…", PickWallpaper, true));
            bgCardBtns.Children.Add(MiniButton("恢复纯色背景", ClearWallpaper));
            bgCard.Children.Add(bgCardBtns);
            Card(bgCard, "背景");

            // ---- 关于与赞助 ----
            var about = new StackPanel();
            about.Children.Add(InfoRow("Mod 版本", "抽卡版修改器 " + (string.IsNullOrEmpty(state.ModVersion) ? "?" : state.ModVersion)));
            about.Children.Add(InfoRow("作者", "小晓Air"));
            about.Children.Add(InfoRow("反馈", "有功能不好使，把「诊断」页的计数发我"));

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            btns.Children.Add(MiniButton("赞助作者", () => OpenUrl(SponsorUrl), true));
            btns.Children.Add(MiniButton("B 站视频", () => OpenUrl(BiliUrl)));
            about.Children.Add(btns);

            about.Children.Add(new TextBlock
            {
                Text = "这个修改器免费，不打广告不联网。觉得好用的话，点上面两个按钮支持一下就好。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = 12,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 12, 0, 0)
            });
            Card(about, "关于与赞助");
        }

        /// <summary>信息行：左边名称，右边值。参考版用户中心里那一套。</summary>
        private FrameworkElement InfoRow(string name, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var n = new TextBlock
            {
                Text = name, FontSize = 12.5,
                Foreground = (Brush)FindResource("SubTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var v = new TextBlock
            {
                Text = value ?? "", FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (value != null && value.Length > 34) v.FontSize = 11.5;

            Grid.SetColumn(n, 0);
            Grid.SetColumn(v, 1);
            g.Children.Add(n);
            g.Children.Add(v);
            return g;
        }

        /// <summary>小按钮，和参考版那个 MakeMiniBtn 一个用途。</summary>
        private Button MiniButton(string text, Action onClick, bool accent = false)
        {
            var b = new Button
            {
                Content = text,
                Height = 30,
                MinWidth = 84,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Style = (Style)FindResource(accent ? "AccentButton" : "GlassButton")
            };
            b.Click += (s, e) => { try { onClick(); } catch (Exception ex) { Toast(ex.Message, true); } };
            return b;
        }

        /// <summary>用系统浏览器打开链接。不弹窗，失败就写在提示条上。</summary>
        private void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                Toast("已在浏览器打开。");
            }
            catch (Exception ex)
            {
                Toast("打不开链接：" + ex.Message, true);
            }
        }

        // ================================================================ 使用说明

        private void RenderHelp()
        {
            Head.Children.Add(Header("使用说明"));

            Card(new TextBlock
            {
                Text = "1. 退出游戏\n2. 运行修改器目录下的「安装.cmd」\n3. 打开本面板 → 点「启动游戏」\n" +
                       "4. 游戏进主菜单后，标题栏状态变成「已连接」",
                TextWrapping = TextWrapping.Wrap, LineHeight = 21
            }, "安装与启动");

            Card(new TextBlock
            {
                Text = "本作没开 Run In Background，游戏一旦失去焦点，主循环就停住 —— 这是引擎行为。\n\n" +
                       "· 点标题栏右侧的「后台」就能解决：开启后游戏失焦也照常跑，改一项马上生效\n" +
                       "· 没开启后台时改动也不会丢：命令会排队，切回游戏时一次性生效\n" +
                       "· 实在不确定时看标题栏：显示「同步中 N」就是还没被游戏确认，面板会自动重发",
                TextWrapping = TextWrapping.Wrap, LineHeight = 21
            }, "游戏失焦时会暂停");

            Card(new TextBlock
            {
                Text = "· 抽卡免费 / 抽卡档位 / 货架：要在商店买一次卡包或进一次商店才触发\n" +
                       "· 一击必杀 / 植物无敌 / 零冷却 / 自动收阳光：必须在关卡内才有作用对象\n" +
                       "· 金币锁定、阳光锁定、游戏速度：随时可用\n\n" +
                       "搞不清就打开「诊断」页，它会告诉你卡在哪一环。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 21
            }, "哪些功能需要特定场景");

            Card(new TextBlock
            {
                Text = "· 数值全部点选预设，不用输数字；金币和阳光最高可以一键设到 10 亿\n" +
                       "· 设置会自动保存，关游戏重开还在",
                TextWrapping = TextWrapping.Wrap, LineHeight = 21
            }, "数值说明");

            Card(new TextBlock
            {
                Text = "Q：标题栏显示「同步中 N」，一直不消？\n" +
                       "A：说明游戏没确认这些改动。面板每 2.5 秒会自动重发一次，一般几秒内就会消掉。\n" +
                       "   若长期不消，标题栏会直接写出游戏拒绝的原因（常见是 mod 版本不一致，重跑「安装.cmd」即可）。\n\n" +
                       "Q：某功能点了没反应？\n" +
                       "A：去「诊断」页看计数，它会说是「没走到那一环」还是「走到了但没生效」。\n" +
                       "   很多功能必须在关卡内才有作用对象，没进关卡就是没反应。\n\n" +
                       "Q：卡片上出现「作弊者」？\n" +
                       "A：存档校验没过，用「存档」页保存一次即可修好。\n\n" +
                       "Q：改了存档进游戏没变化？\n" +
                       "A：游戏运行中会用内存数据覆盖存档，请先退出游戏再改。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 21
            }, "常见问题");

            Card(new TextBlock
            {
                Text = "运行修改器目录下的「卸载.cmd」，会还原所有被改过的游戏文件并删除备份。",
                TextWrapping = TextWrapping.Wrap, LineHeight = 21
            }, "卸载");
        }

        // ================================================================ 行为

        /// <summary>
        /// 发送整份设置快照（只含用户动过的项）。命令走追加队列，每条都是全量，
        /// 所以连点也不会丢；发完只就地刷新，不重建控件树（否则界面会闪、滚动条会跳）。
        /// </summary>
        private void Apply(string key, string rawValue)
        {
            _desired[key] = (rawValue ?? "").Trim();
            _resendCount = 0;

            if (!_client.SendSnapshot(_desired, out string error))
            {
                Toast(error, true);
                return;
            }

            _lastSnapshotSent = DateTime.Now;
            RefreshValues();
        }

        private void RunAction(string action, string value, string label)
        {
            if (!_client.SendAction(action, value, out string error))
            {
                Toast(error, true);
                return;
            }
            _pendingActionLabel = label;
            _actionTimer.Start();
        }

        private void LaunchGame()
        {
            if (!GameInfo.Launch(out string error))
                Toast(error, true);
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path)) { Toast("目录不存在：" + path, true); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Toast("打开失败：" + ex.Message, true);
            }
        }

        // ================================================================ 构件

        private TextBlock Header(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 12)
            };
        }

        private Button Btn(string text, double minWidth, Action onClick, bool accent = false)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource(accent ? "AccentButton" : "GlassButton"),
                Margin = new Thickness(0, 0, 8, 8)
            };
            if (minWidth > 0) b.MinWidth = minWidth;
            b.Click += (s, e) => onClick();
            return b;
        }

        /// <summary>把内容塞进一张玻璃卡片。</summary>
        private void Card(UIElement child, string title)
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(title))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12.5,
                    Foreground = (Brush)FindResource("SubTextBrush"),
                    Margin = new Thickness(0, 0, 0, 11)
                });
            }
            if (child != null) panel.Children.Add(child);

            ContentPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("SettingsCard"),
                Child = panel,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
        }
    }
}
