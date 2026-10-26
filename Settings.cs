using System.Text.Json;
using System.Text.Json.Serialization;

namespace GramCooling;

/// <summary>トレイアイコンに出す値。</summary>
public enum IconValue
{
    Temp,   // CPU 温度 [℃]
    Load,   // CPU 負荷 [%]
    Clock,  // CPU クロック [GHz]
}

/// <summary>%APPDATA%\GramCooling\settings.json に保存する設定。</summary>
public sealed class Settings
{
    /// <summary>モード変更を再起動後も保持する（SetULong Flags=1）。</summary>
    public bool KeepAfterReboot { get; set; } = true;

    /// <summary>トレイアイコンに出す値。</summary>
    public IconValue IconValue { get; set; } = IconValue.Temp;

    /// <summary>初回起動の処理（自動起動の登録）を済ませたか。</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>ユーザーが選んだ冷却モード。ブーストが終わったらこれに戻す。</summary>
    public LgCooling.Mode? PreferredCooling { get; set; }

    /// <summary>ユーザーが選んだ省エネ機能の状態。ブーストが終わったらこれに戻す。</summary>
    public bool? PreferredSaver { get; set; }

    /// <summary>
    /// ブースト（冷却モード高・省エネ機能 OFF）の終了時刻 (UTC)。ブースト中でなければ null。
    /// 自動更新での再起動や、ブースト中に PC が落ちた場合も正しく戻せるよう保存しておく。
    /// </summary>
    public DateTime? BoostUntilUtc { get; set; }

    /// <summary>ブースト中、冷却モードを押さえているか（ユーザーが変えたら false）。</summary>
    public bool BoostCooling { get; set; }

    /// <summary>ブースト中、省エネ機能を押さえているか（ユーザーが変えたら false）。</summary>
    public bool BoostSaver { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GramCooling", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch
        {
            // 壊れていたら既定値で作り直す
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
