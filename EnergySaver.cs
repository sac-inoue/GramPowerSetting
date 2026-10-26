using System.Runtime.InteropServices;

namespace GramCooling;

/// <summary>
/// Windows の「省エネ機能」(クイック設定のボタン) の ON/OFF。
/// 公開 API が無いため、設定アプリと同じ設定ハンドラ (SettingsHandlers_OneCore_BatterySaver.dll の GetSetting) で
/// SystemSettings_PowerAndBattery_EnergySaverAlwaysOn を読み書きする。ISettingItem は非公開インターフェースなので、
/// get_Id が一致することを確かめてから使う。
///
/// 注意（実測）:
///   - 読み取りは信用できない。同じプロセスでは数分古いままで、別プロセスどうしでも値が食い違う。
///     状態は EnergySaverState（実効電源モード）で判定すること。
///   - 書き込みは「指定値にする」ではなく実質「切り替え」として働く（OFF のときに OFF を書くと ON になる）。
///   - ISettingItem の Release がまれに戻ってこない。常駐プロセスからは EnergySaverProcess（子プロセス）経由で使う。
/// </summary>
public static unsafe class EnergySaver
{
    private const string SettingId = "SystemSettings_PowerAndBattery_EnergySaverAlwaysOn";

    // ISettingItem の vtable 位置（IUnknown 3 + IInspectable 3 の後）
    private const int VtGetId = 6;
    private const int VtGetValue = 13;
    private const int VtSetValue = 14;

    private static readonly Guid IidPropertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    private static readonly Guid IidPropertyValueStatics = new("629BDBC8-D932-4FF4-96B9-8D96C5C1E858");

    private const string DllPath = @"C:\Windows\System32\SettingsHandlers_OneCore_BatterySaver.dll";
    private static readonly object Lock = new();

    /// <summary>
    /// ISettingItem の Release と DLL の解放を省く。Release がまれに戻ってこないため、
    /// すぐ終了する使い捨ての子プロセスでは片付けをプロセス終了に任せる。
    /// </summary>
    public static bool SkipCleanup { get; set; }

    public static bool Get() => WithItem(item =>
    {
        IntPtr val;
        using (var name = new HString("Value"))
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)VTable(item)[VtGetValue])(item, name.Handle, &val));
        try
        {
            Guid iid = IidPropertyValue;
            Check(Marshal.QueryInterface(val, in iid, out IntPtr pv));
            try
            {
                byte b;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)VTable(pv)[18])(pv, &b));  // IPropertyValue::GetBoolean
                return b != 0;
            }
            finally { Marshal.Release(pv); }
        }
        finally { Marshal.Release(val); }
    });

    public static void Set(bool on) => WithItem(item =>
    {
        IntPtr boxed = CreateBoolean(on);
        try
        {
            using var name = new HString("Value");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)VTable(item)[VtSetValue])(item, name.Handle, boxed));
        }
        finally { Marshal.Release(boxed); }
        return 0;
    });

    /// <summary>DLL を読み込んで ISettingItem を取り出し、使い終わったら DLL ごと解放する。</summary>
    private static T WithItem<T>(Func<IntPtr, T> use)
    {
        lock (Lock)
        {
            IntPtr dll = NativeLibrary.Load(DllPath);
            try
            {
                IntPtr item = OpenItem(dll);
                try
                {
                    return use(item);
                }
                finally
                {
                    if (!SkipCleanup)
                        Marshal.Release(item);
                }
            }
            finally
            {
                if (!SkipCleanup)
                    NativeLibrary.Free(dll);
            }
        }
    }

    private static IntPtr OpenItem(IntPtr dll)
    {
        var getSetting = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, IntPtr, int>)NativeLibrary.GetExport(dll, "GetSetting");

        IntPtr item;
        using (var id = new HString(SettingId))
            Check(getSetting(id.Handle, &item, IntPtr.Zero));

        // 非公開インターフェースなので、並びが想定どおりかを ID で確かめる
        IntPtr hId;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VTable(item)[VtGetId])(item, &hId));
        string? actual = HString.Read(hId);
        WindowsDeleteString(hId);
        if (actual != SettingId)
        {
            Marshal.Release(item);
            throw new NotSupportedException("この Windows では省エネ機能の切り替えに対応していません");
        }
        return item;
    }

    private static IntPtr CreateBoolean(bool v)
    {
        IntPtr statics;
        using (var cls = new HString("Windows.Foundation.PropertyValue"))
        {
            Guid iid = IidPropertyValueStatics;
            Check(RoGetActivationFactory(cls.Handle, ref iid, out statics));
        }
        try
        {
            IntPtr result;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, IntPtr*, int>)VTable(statics)[17])(statics, (byte)(v ? 1 : 0), &result));  // CreateBoolean
            return result;
        }
        finally { Marshal.Release(statics); }
    }

    private static IntPtr* VTable(IntPtr obj) => *(IntPtr**)obj;

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    private sealed class HString : IDisposable
    {
        public IntPtr Handle { get; }

        public HString(string s)
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(s, s.Length, out IntPtr h));
            Handle = h;
        }

        public static string? Read(IntPtr h)
        {
            if (h == IntPtr.Zero)
                return null;
            char* p = WindowsGetStringRawBuffer(h, out uint len);
            return new string(p, 0, (int)len);
        }

        public void Dispose() => WindowsDeleteString(Handle);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string s, int len, out IntPtr h);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr h);

    [DllImport("combase.dll")]
    private static extern char* WindowsGetStringRawBuffer(IntPtr h, out uint len);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr cls, ref Guid iid, out IntPtr factory);
}
