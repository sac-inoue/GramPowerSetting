using Microsoft.Win32;

namespace GramCooling;

/// <summary>
/// LG gram の「バッテリー寿命を延ばす」設定。
/// My gram (LGgramAssistant PowerPage.RadioButton_BatteryLifeSpan_Checked / BatteryManager.Set_BatteryLife2) と同じ手順:
///   充電上限は Method_ULong2 の 268 番 (80 / 100)、
///   AI モードは HKCU\Software\LGE\My gram\LGSmartBattery\BatteryLifeToggled = 1 で、LGSmartBattery が上限を自動調整する。
/// </summary>
public static class LgBattery
{
    private const uint BatteryLife = 268;
    private const string SmartBatteryKey = @"Software\LGE\My gram\LGSmartBattery";
    private const string AiToggle = "BatteryLifeToggled";

    public enum Mode
    {
        Off,     // 未使用（100%まで充電）
        Limit80, // 80%まで充電
        Ai,      // AIモード
    }

    public static string DisplayName(Mode m) => m switch
    {
        Mode.Off => "未使用",
        Mode.Limit80 => "80%まで充電",
        Mode.Ai => "AIモード",
        _ => m.ToString(),
    };

    /// <summary>現在の設定。読めない値なら null。</summary>
    public static Mode? Get()
    {
        if (ReadAiToggle() == 1)
            return Mode.Ai;
        return (LgWmi.GetULong(BatteryLife) & 0xFF) switch
        {
            80 => Mode.Limit80,
            100 => Mode.Off,
            _ => null,
        };
    }

    public static void Set(Mode mode)
    {
        LgWmi.SetULong(BatteryLife, mode == Mode.Limit80 ? 80u : 100u, LgWmi.FlagNonVolatile);
        using var key = Registry.CurrentUser.CreateSubKey(SmartBatteryKey);
        key.SetValue(AiToggle, mode == Mode.Ai ? 1 : 0, RegistryValueKind.DWord);
    }

    private static int ReadAiToggle()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SmartBatteryKey);
        return key?.GetValue(AiToggle) is int v ? v : -1;
    }
}
