using System.Diagnostics;

namespace GramCooling;

/// <summary>
/// 省エネ機能の書き込みを、使い捨ての子プロセス（自分自身を saver-set で起動）で行う。
/// 設定ハンドラの DLL は解放 (Release) で固まることがあるため、常駐プロセスでは直接使わない。
/// 子プロセスが固まっても一定時間で強制終了する。
/// 状態の読み取りは設定ハンドラではなく EnergySaverState（実効電源モード）で行う。
/// </summary>
public static class EnergySaverProcess
{
    public const string SetArg = "saver-set";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static void Set(bool on) => Run($"{SetArg} {(on ? "on" : "off")}");

    private static string Run(string args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(Timeout))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException("省エネ機能の操作が応答しませんでした");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException(stderr.Result.Trim() is { Length: > 0 } e ? e : $"省エネ機能の操作に失敗しました (終了コード {p.ExitCode})");
        return stdout.Result.Trim();
    }
}
