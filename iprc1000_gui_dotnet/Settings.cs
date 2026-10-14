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
        catch { /* 忽略损坏的设置，用默认值 */ }
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
            File.WriteAllText(File_, JsonSerializer.Serialize(_data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private sealed class SettingsData
    {
        public bool AutoRun { get; set; }
        public bool StartMinimized { get; set; }
        public bool AutoStartBridge { get; set; } = true;
        public bool AutoSwitchDriver { get; set; } = true;
        public bool AutoKeepDefaultMic { get; set; } = true;
        public string KeySource { get; set; } = "auto";
    }
}
