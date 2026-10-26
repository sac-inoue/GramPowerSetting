using System.Runtime.InteropServices;

namespace GramCooling;

/// <summary>
/// 省エネ機能が実際に効いているかを、公式 API の「実効電源モード」で判定する。
/// 設定ハンドラ (EnergySaver) の読み取りはプロセスによって値が食い違い、信用できないため使わない。
///
/// 省エネ機能が ON のとき、実効電源モードは
///   - バッテリー駆動中: BatterySaver
///   - AC 電源中: BetterBattery（ユーザーが選んだ電源モードが「最適な電力効率」に上書きされる）
/// になる。ユーザー自身が「最適な電力効率」を選んでいる場合も BetterBattery なので、ユーザー設定の電源モードと比べて見分ける。
/// 変化は Windows から通知されるので、ポーリングはしない。
/// </summary>
public static class EnergySaverState
{
    // EFFECTIVE_POWER_MODE
    private const int BatterySaver = 0;
    private const int BetterBattery = 1;

    // 電源モード「最適な電力効率」
    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    private static int _mode = -1;
    private static IntPtr _handle;
    private static EffectivePowerModeCallback? _callback;  // GC に回収されないよう保持する

    /// <summary>実効電源モードが変わったとき（別スレッドから呼ばれる）。</summary>
    public static event Action? Changed;

    public static void Start()
    {
        if (_handle != IntPtr.Zero)
            return;
        _callback = (mode, _) =>
        {
            _mode = mode;
            Changed?.Invoke();
        };
        int hr = PowerRegisterForEffectivePowerModeNotifications(EffectivePowerModeV2, _callback, IntPtr.Zero, out _handle);
        if (hr != 0)
            Log.Write($"実効電源モードの通知を登録できませんでした (hr=0x{hr:X8})");
    }

    public static void Stop()
    {
        if (_handle == IntPtr.Zero)
            return;
        PowerUnregisterFromEffectivePowerModeNotifications(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>省エネ機能が効いているか。まだ通知を受けていなければ null。</summary>
    public static bool? IsOn
    {
        get
        {
            int mode = _mode;
            if (mode < 0)
                return null;
            if (mode == BatterySaver)
                return true;
            if (mode != BetterBattery)
                return false;
            // BetterBattery は、ユーザー自身が「最適な電力効率」を選んでいるだけかもしれない
            bool online = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            uint rc = online ? PowerGetUserConfiguredACPowerMode(out Guid user) : PowerGetUserConfiguredDCPowerMode(out user);
            return rc != 0 || user != BestEfficiency;
        }
    }

    private const uint EffectivePowerModeV2 = 2;

    private delegate void EffectivePowerModeCallback(int mode, IntPtr context);

    [DllImport("powrprof.dll")]
    private static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, EffectivePowerModeCallback callback, IntPtr context, out IntPtr handle);

    [DllImport("powrprof.dll")]
    private static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr handle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
}
