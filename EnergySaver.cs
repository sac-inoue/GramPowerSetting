using System.Runtime.InteropServices;

namespace GramCooling;

/// <summary>
/// Windows の「省エネ機能」(クイック設定のボタン) の ON/OFF。
/// 公開 API が無いため、設定アプリと同じ設定ハンドラ (SettingsHandlers_OneCore_BatterySaver.dll の GetSetting) で
/// SystemSettings_PowerAndBattery_EnergySaverAlwaysOn を読み書きする。ISettingItem は非公開インターフェースなので、
/// get_Id が一致することを確かめてから使う。
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

    // DLL のロードと ISettingItem の取得・検証は最初の1回だけ行い、プロセス終了まで使い回す
    private static readonly object Lock = new();
    private static IntPtr _item;

    public static bool Get()
    {
        lock (Lock)
        {
            IntPtr item = Item();
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
        }
    }

    public static void Set(bool on)
    {
        lock (Lock)
        {
            IntPtr item = Item();
            IntPtr boxed = CreateBoolean(on);
            try
            {
                using var name = new HString("Value");
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)VTable(item)[VtSetValue])(item, name.Handle, boxed));
            }
            finally { Marshal.Release(boxed); }
        }
    }

    private static IntPtr Item() => _item != IntPtr.Zero ? _item : (_item = OpenItem());

    private static IntPtr OpenItem()
    {
        IntPtr dll = NativeLibrary.Load(@"C:\Windows\System32\SettingsHandlers_OneCore_BatterySaver.dll");
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
