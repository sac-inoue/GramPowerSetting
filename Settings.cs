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
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IconValue IconValue { get; set; } = IconValue.Temp;

    /// <summary>初回起動の処理（自動起動の登録）を済ませたか。</summary>
    public bool FirstRunDone { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GramCooling", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
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
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
