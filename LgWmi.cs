using System.Management;

namespace GramCooling;

/// <summary>
/// LG gram の ACPI WMI (root\wmi : Method_ULong2) の GetULong / SetULong。
/// My gram (WMIConn.dll ACPIHelper.GetMethod2 / SetMethod2) と同じ呼び出し方。
/// インスタンスの列挙は重いので一度取ったら使い回し、失敗したときだけ取り直す。
/// </summary>
public static class LgWmi
{
    // SetULong の Flags: 1 = 再起動後も保持, 2 = 一時的
    public const uint FlagNonVolatile = 1;
    public const uint FlagVolatile = 2;

    private static readonly CachedInstance Method = new("Method_ULong2");

    public static uint GetULong(uint offset) => Method.Invoke(obj =>
    {
        using var inParams = obj.GetMethodParameters("GetULong");
        inParams["InOffset"] = offset;
        using var outParams = obj.InvokeMethod("GetULong", inParams, null);
        uint status = Convert.ToUInt32(outParams["OutStatus"]);
        if (status != 0)
            throw new InvalidOperationException($"GetULong({offset}) 失敗 (OutStatus={status})");
        return Convert.ToUInt32(outParams["OutData"]);
    });

    public static void SetULong(uint offset, uint data, uint flags) => Method.Invoke(obj =>
    {
        using var inParams = obj.GetMethodParameters("SetULong");
        inParams["InOffset"] = offset;
        inParams["InData"] = data;
        inParams["Flags"] = flags;
        using var outParams = obj.InvokeMethod("SetULong", inParams, null);
        uint status = Convert.ToUInt32(outParams["OutStatus"]);
        if (status != 0)
            throw new InvalidOperationException($"SetULong({offset}) 失敗 (OutStatus={status})");
        return 0;
    });
}

/// <summary>root\wmi のクラスの最初のインスタンスをキャッシュする。</summary>
public sealed class CachedInstance(string className)
{
    private readonly object _lock = new();
    private ManagementObject? _obj;

    public T Invoke<T>(Func<ManagementObject, T> call)
    {
        lock (_lock)
        {
            try
            {
                return call(_obj ??= Find());
            }
            catch (ManagementException)
            {
                // スリープ復帰などでインスタンスが無効になった場合に備えて1回だけ取り直す
                _obj?.Dispose();
                _obj = Find();
                return call(_obj);
            }
        }
    }

    private ManagementObject Find()
    {
        using var cls = new ManagementClass(@"root\wmi", className, null);
        foreach (ManagementObject o in cls.GetInstances())
            return o;
        throw new InvalidOperationException($"{className} が見つかりません（LG gram 以外か、権限不足）");
    }
}
