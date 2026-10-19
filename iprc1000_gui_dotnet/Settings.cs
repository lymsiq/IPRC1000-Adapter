using System.Text.Json;

namespace Iprc1000Adapter;

/// <summary>
/// 应用级设置（开机自启、启动时最小化、按键来源等），落在
/// `%APPDATA%\IPRC1000 Adapter\settings.json`。
/// </summary>
internal static class Settings
{
    private static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Env.AppName, "settings.json");

    private static SettingsData _data = new();

    static Settings()
    {
        try
        {
            if (File.Exists(File_))
                _data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(File_)) ?? new();
        }
        catch (Exception e)
        {
            // ★ 配置损坏不能"静默回落默认值"——用户会以为设置自己变了。
            Safe.Log(e, "读取 settings.json 失败，本次已用默认值（文件保留，未覆盖）");
        }

        // ★ 迁移（只跑一次）：v1.2.14 及更早的 settings.json 里 `startMinimized` 已被写成 false
        //   （因为默认值就是 false，Save 过一次就落盘了）。用户要求"默认进托盘"，
        //   所以这里强制改回 true 并升版本号 —— 只在 SchemaVersion 还是旧值时执行。
        if (_data.SchemaVersion < 1)
        {
            _data.StartMinimized = true;
            _data.SchemaVersion = 1;
            Save();
        }
    }

    public static bool AutoRun
    {
        get => _data.AutoRun;
        set { _data.AutoRun = value; Save(); }
    }

    public static bool StartMinimized
    {
        get => _data.StartMinimized;
        set { _data.StartMinimized = value; Save(); }
    }

    public static bool AutoStartBridge
    {
        get => _data.AutoStartBridge;
        set { _data.AutoStartBridge = value; Save(); }
    }

    public static bool AutoSwitchDriver
    {
        get => _data.AutoSwitchDriver;
        set { _data.AutoSwitchDriver = value; Save(); }
    }

    /// <summary>
    /// 默认录音设备被系统改走时，是否自动改回 CABLE Output（默认开）。
    ///
    /// Windows 会在蓝牙耳机/耳麦连接或断开、设备热插拔时自动切换默认录音设备。
    /// 一旦切走，输入法拿到的是别的设备 —— 表现为「录音启动失败」或通话无波形。
    /// </summary>
    public static bool AutoKeepDefaultMic
    {
        get => _data.AutoKeepDefaultMic;
        set { _data.AutoKeepDefaultMic = value; Save(); }
    }

    /// <summary>是否写文本日志（默认关；见 LogSwitch）</summary>
    /// <summary>「截图点击」模板（PNG base64，空 = 还没采集）</summary>
    public static string ClickTemplate
    {
        get => _data.ClickTemplate;
        set { _data.ClickTemplate = value; Save(); }
    }
    public static string ClickWindowClass
    {
        get => _data.ClickWindowClass;
        set { _data.ClickWindowClass = value; Save(); }
    }
    public static int ClickTemplateX
    {
        get => _data.ClickTemplateX;
        set { _data.ClickTemplateX = value; Save(); }
    }
    public static string ClickProcess
    {
        get => _data.ClickProcess;
        set { _data.ClickProcess = value; Save(); }
    }
    public static double ClickMinScore
    {
        get => _data.ClickMinScore is >= 0.5 and <= 0.98 ? _data.ClickMinScore : 0.75;
        set { _data.ClickMinScore = Math.Clamp(value, 0.5, 0.98); Save(); }
    }

    public static int ClickRelBottom
    {
        get => _data.ClickRelBottom;
        set { _data.ClickRelBottom = value; Save(); }
    }

    public static bool ClickLastOk
    {
        get => _data.ClickLastOk;
        set { _data.ClickLastOk = value; Save(); }
    }
    public static int ClickLastX
    {
        get => _data.ClickLastX;
        set { _data.ClickLastX = value; Save(); }
    }
    public static int ClickLastY
    {
        get => _data.ClickLastY;
        set { _data.ClickLastY = value; Save(); }
    }

    public static string ClickTitle
    {
        get => _data.ClickTitle;
        set { _data.ClickTitle = value; Save(); }
    }
    public static bool VoiceAudioRelease
    {
        get => _data.VoiceAudioRelease;
        set { _data.VoiceAudioRelease = value; Save(); }
    }
    public static bool ClickReturnToOrigin
    {
        get => _data.ClickReturnToOrigin;
        set { _data.ClickReturnToOrigin = value; Save(); }
    }
    public static int ClickRelX
    {
        get => _data.ClickRelX;
        set { _data.ClickRelX = value; Save(); }
    }
    public static int ClickRelY
    {
        get => _data.ClickRelY;
        set { _data.ClickRelY = value; Save(); }
    }
    public static int ClickTemplateY
    {
        get => _data.ClickTemplateY;
        set { _data.ClickTemplateY = value; Save(); }
    }

    public static bool LogEnabled
    {
        get => _data.LogEnabled;
        set { _data.LogEnabled = value; Save(); }
    }

    public static string KeySource
    {
        get => string.IsNullOrEmpty(_data.KeySource) ? "auto" : _data.KeySource;
        set { _data.KeySource = value; Save(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            // ★ 原子写：先写 .tmp，再 Replace 覆盖（保留一份 .bak）。
            //   直接 WriteAllText 遇到断电/崩溃会留下半截 JSON，而解析失败又会静默回落默认值
            //   ⇒ 用户所有设置"自己变回去了"，且没有任何提示。
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            var tmp = File_ + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(File_)) File.Replace(tmp, File_, File_ + ".bak", true);
            else File.Move(tmp, File_);
        }
        catch { }
    }

    private sealed class SettingsData
    {
        public bool AutoRun { get; set; }
        /// <summary>
        /// 启动时只进托盘、不打开窗口（默认 **true**）。
        ///
        /// ⚠ 这里原来是 `{ get; set; }`（没有初始值 ⇒ **false**），而 MainForm 的注释一直写着
        /// "默认开" —— 注释与实现不一致，实际表现是**开机自启会直接弹出窗口**。
        /// </summary>
        public bool StartMinimized { get; set; } = true;
        public bool AutoStartBridge { get; set; } = true;
        public bool AutoSwitchDriver { get; set; } = true;
        /// <summary>
        /// 默认麦克风被系统改走时**自动改回** CABLE Output。
        ///
        /// ★ 2026-10-19 默认值 true → **false**（参考无线麦 SayAll 的原则：
        ///   "不会为了语音输入自动修改系统默认输入/输出设备"）。
        ///   自动改默认设备会与输入法/其他程序的设备缓存打架，反而造成"录音启动失败/无波形"。
        ///   现在改为**只提示**，由用户在输入法里自己选 CABLE Output；需要时点按钮手动改。
        /// </summary>
        public bool AutoKeepDefaultMic { get; set; }
        public string KeySource { get; set; } = "auto";
        /// <summary>
        /// 是否写文本日志。**默认 false**（发布版性能优先，见 LogSwitch 的说明）。
        /// 排查问题时在环境设置页勾上，或设环境变量 `IPRC1000_LOG=on`。
        /// </summary>
        public bool LogEnabled { get; set; }

        /// <summary>「截图点击」的图标模板（PNG base64，由用户采集）</summary>
        public string ClickTemplate { get; set; } = "";
        /// <summary>采集时所在顶层窗口的类名（如 OimeDirectUIWindow）</summary>
        public string ClickWindowClass { get; set; } = "";
        /// <summary>命中点相对模板左上角的偏移（通常等于半个采集边长）</summary>
        public int ClickTemplateX { get; set; }
        public int ClickTemplateY { get; set; }
        /// <summary>采集时光标相对**目标窗口左上角**的位置；多处同形图标时用它挑对的那个</summary>
        /// <summary>采集时目标窗口所属进程名（类名通用的场景靠它确认是同一个程序）</summary>
        public string ClickProcess { get; set; } = "";
        /// <summary>
        /// 截图点击的相似度门槛（默认 **0.75**）。
        ///
        /// 可配置的理由：模板质量天生不同 —— 小图标、低对比、半透明按钮的分数就是上不去
        /// （实测某目标最高只有 0.76，卡在 0.80 之下就"找不到"）。
        /// SikuliX 也是把 `MinSimilarity` 暴露给用户的（默认 0.7）。
        /// 有"唯一性校验"兜底：分数不高但**明显领先次佳**时才接受，避免误点。
        /// </summary>
        public double ClickMinScore { get; set; } = 0.75;

        /// <summary>上次命中点（窗口内坐标）—— SikuliX 的 "still-there" 预检用，省掉整段搜索</summary>
        public bool ClickLastOk { get; set; }
        public int ClickLastX { get; set; }
        public int ClickLastY { get; set; }

        /// <summary>采集时目标窗口的标题（同进程多窗口时用于精确匹配）</summary>
        public string ClickTitle { get; set; } = "";
        /// <summary>
        /// 截图点击后**回到原来的光标位置再点一下**（默认开）。
        ///
        /// 为什么需要（2026-10-19 真机）：点目标按钮会让焦点落到那个按钮上，
        /// 之后按 OK（Enter）就不再发给输入框 —— 表现为"Enter 发不出去"。
        /// 点回原位（通常就是输入框）可以把焦点还回去。
        /// </summary>
        public bool ClickReturnToOrigin { get; set; } = true;
        public int ClickRelX { get; set; }
        /// <summary>
        /// 图标距**窗口底部**的距离。
        ///
        /// ★ 2026-10-19 真机：输入框（以及模型名旁的 ✨）是**贴底布局** ——
        ///   WorkBuddy 从全屏变成窗口时高度从 ~1440 掉到 1248，✨ 实际**上移了约 192px**，
        ///   而只记"距顶部 1341"的话，这个值直接超出窗口高度 ⇒ 判"采集位置超出" ⇒ 一次都不搜。
        ///   所以同时记底部距离：缩放后用 `height - ClickRelBottom` 重新推算。
        /// </summary>
        public int ClickRelBottom { get; set; }
        public int ClickRelY { get; set; }

        /// <summary>
        /// 「音频静默就释放语音键」——**默认关闭**（2026-10-19 真机）。
        ///
        /// 这条是 v1.2.7 为了"UP 丢了也能放开"加的保险。但遥控器在**不说话时会停止出流**
        /// （VAD 行为），于是"按住想一下再说"会被判成"静默超时"⇒ 右 Alt 被主动放掉
        /// ⇒ 豆包显示"已识别"，用户以为按键自己弹起来了。
        /// UP 路径在 v1.2.50 之后已可靠，这条反而成了主要误伤源，因此默认关闭。
        /// 若哪天又出现"松开不弹起"，可在设置里重新打开。
        /// </summary>
        public bool VoiceAudioRelease { get; set; }

        /// <summary>配置版本号，用于一次性迁移（0 = v1.2.14 及更早）</summary>
        public int SchemaVersion { get; set; }
    }
}
