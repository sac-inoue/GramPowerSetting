using System.Management;

namespace GramCooling;

/// <summary>
/// LG gram の EC を ACPI WMI (root\wmi : MfgFwiPackage.FwiExecL) 経由で読む。
/// My gram (LGgramAssistant CPUTempHelper → ACPIHelper.Use_ReadECMethod) と同じ読み取りコマンドで、
/// BIOS が EC を読むので OS の EC ドライバと競合しない。EC ポートを直接叩いてはいけない。
/// </summary>
public static class LgEc
{
    private static readonly CachedInstance Package = new("MfgFwiPackage");
    private static readonly Lazy<ManagementClass> PackageClass = new(() => new ManagementClass(@"root\wmi", "FwiPackageLarge", null));

    /// <summary>bank/row で指定した EC の16バイトを読む。</summary>
    public static byte[] ReadRow(int bank, int row)
    {
        var buf = new byte[272];
        buf[0] = 0x08; buf[1] = 0x01;  // ddOffSet = 264
        buf[4] = 0x02; buf[5] = 0x01;  // ddCmd = 0x0102 (EC 読み取り)
        buf[12] = 0x0B; buf[13] = 0xEC;
        buf[14] = (byte)((bank << 4) | row);

        return Package.Invoke(obj =>
        {
            using var pkg = PackageClass.Value.CreateInstance();
            pkg["Bytes"] = buf;
            using var inParams = obj.GetMethodParameters("FwiExecL");
            inParams["iData"] = pkg;
            using var outParams = obj.InvokeMethod("FwiExecL", inParams, null);
            var bytes = (byte[])((ManagementBaseObject)outParams["oData"])["Bytes"];
            return bytes[16..32];
        });
    }

    /// <summary>
    /// ファン回転数 [rpm]（bank1 0xC8-0xC9）。読めなかったら null。
    /// AC アダプタの抜き差し直後などは EC が全部 0 の行を返すことがあるので、それは失敗として扱う。
    /// （サイレントモードではファンが本当に 0 rpm になるので、値そのものでは判定しない）
    /// </summary>
    public static int? ReadFanRpm()
    {
        var r = ReadRow(1, 0xC);
        return IsBlank(r) ? null : r[8] | (r[9] << 8);
    }

    /// <summary>CPU 温度 [℃]（bank0 0xC8）。読めなかったか、ありえない値なら null。</summary>
    public static int? ReadCpuTemp()
    {
        var r = ReadRow(0, 0xC);
        int t = r[8];
        return IsBlank(r) || t < 1 || t > 120 ? null : t;
    }

    private static bool IsBlank(byte[] row) => row.All(b => b == 0);
}
