using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace GramCooling;

/// <summary>
/// ログオン時の自動起動。requireAdministrator のアプリは HKCU\...\Run では起動されないため、
/// タスクスケジューラに「最高の権限で実行」で登録する。バッテリー駆動中でも起動するよう XML で作る。
/// </summary>
public static class AutoStart
{
    private const string TaskName = "GramCooling";

    public static bool IsRegistered() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void Set(bool enable)
    {
        if (!enable)
        {
            if (IsRegistered())
                Run($"/Delete /TN \"{TaskName}\" /F");
            return;
        }

        string user = WindowsIdentity.GetCurrent().Name;
        string exe = Environment.ProcessPath!;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>gram 冷却モード（タスクトレイ常駐）</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT10S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command></Exec>
              </Actions>
            </Task>
            """;

        string path = Path.Combine(Path.GetTempPath(), "GramCooling-task.xml");
        File.WriteAllText(path, xml, Encoding.Unicode);
        try
        {
            Run($"/Create /TN \"{TaskName}\" /XML \"{path}\" /F");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Run(string args)
    {
        if (Schtasks(args) != 0)
            throw new InvalidOperationException("schtasks " + args.Split(' ')[0] + " に失敗しました");
    }

    private static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
