namespace GramCooling;

/// <summary>
/// LG gram の冷却モード。
/// My gram (WindowsControl.dll LGDeviceController.Get/Set_SystemTempMode) と同じく Method_ULong2 の 1036 番を使う。
/// </summary>
public static class LgCooling
{
    private const uint SystemTempMode = 1036;

    public enum Mode
    {
        Normal = 0,  // 正常
        Low = 1,     // 低
        High = 2,    // 高
        Silent = 3,  // サイレント
    }

    public static string DisplayName(Mode m) => m switch
    {
        Mode.Silent => "サイレント",
        Mode.Low => "低",
        Mode.Normal => "正常",
        Mode.High => "高",
        _ => m.ToString(),
    };

    /// <summary>現在のモード。未知の値なら null。</summary>
    public static Mode? Get()
    {
        uint low = LgWmi.GetULong(SystemTempMode) & 0xFF;
        return Enum.IsDefined(typeof(Mode), (int)low) ? (Mode)low : null;
    }

    public static void Set(Mode mode, bool keepAfterReboot)
    {
        // 上位バイトはそのまま残し、下位バイトだけ差し替える
        uint data = (LgWmi.GetULong(SystemTempMode) & 0xFFFFFF00u) | (uint)mode;
        LgWmi.SetULong(SystemTempMode, data, keepAfterReboot ? LgWmi.FlagNonVolatile : LgWmi.FlagVolatile);
    }
}
