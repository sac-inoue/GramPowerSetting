using System.Diagnostics;
using System.Management;

namespace GramCooling;

/// <summary>
/// CPU の負荷 [%] とクロック [GHz]。タスクマネージャーと同じ求め方:
///   負荷 = % Processor Utility（100 を超えることがあるので丸める）
///   速度 = 基本クロック (Win32_Processor.MaxClockSpeed) × % Processor Performance / 100
/// どちらも前回呼び出しからの平均なので、一定間隔で呼ぶこと。
/// </summary>
public sealed class CpuStats : IDisposable
{
    private readonly PerformanceCounter _utility = new("Processor Information", "% Processor Utility", "_Total");
    private readonly PerformanceCounter _performance = new("Processor Information", "% Processor Performance", "_Total");
    private readonly double _baseMHz = ReadBaseMHz();

    public CpuStats()
    {
        // 初回は 0 が返るので捨てる
        _utility.NextValue();
        _performance.NextValue();
    }

    public int ReadLoad() => (int)Math.Round(Math.Clamp(_utility.NextValue(), 0f, 100f));

    /// <summary>実効クロック [GHz]。基本クロックが分からなければ null。</summary>
    public double? ReadGHz()
    {
        float perf = _performance.NextValue();
        return _baseMHz > 0 ? _baseMHz * perf / 100 / 1000 : null;
    }

    private static double ReadBaseMHz()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    return Convert.ToDouble(o["MaxClockSpeed"]);
        }
        catch
        {
            // 取れなければクロックは表示しない
        }
        return 0;
    }

    public void Dispose()
    {
        _utility.Dispose();
        _performance.Dispose();
    }
}
