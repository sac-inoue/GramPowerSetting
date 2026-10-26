namespace GramCooling;

static class Program
{
    /// <summary>
    /// 引数なし: タスクトレイに常駐（--restarted は自動更新からの再起動）
    /// 引数あり: GramCooling.exe [silent|low|normal|high|get|fan] [--keep]
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        bool restarted = args.Contains(Updater.RestartedArg);
        if (args.Length > 0 && !restarted)
            return RunCommand(args);

        Log.Write($"起動 (ビルド {Updater.BuildTime:yyyy-MM-dd HH:mm}, 引数 [{string.Join(' ', args)}])");
        using var mutex = new Mutex(false, @"Local\GramCooling.Tray");
        // 自動更新で起動されたときは、古い版が終了するのを待つ
        if (!AcquireMutex(mutex, restarted ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
        {
            Log.Write("既に常駐しているので終了");
            return 0;
        }

        Log.HookUnhandledExceptions();
        ApplicationConfiguration.Initialize();
        // 自動更新での再起動はブーストを始め直さない（前のブーストを引き継ぐ）
        Application.Run(new TrayApp(startup: !restarted));
        Log.Write("終了");
        mutex.ReleaseMutex();
        return 0;
    }

    private static int RunCommand(string[] args)
    {
        // WinExe なので親コンソールに出力をつなぐ（リダイレクト時はそのまま）
        if (!Console.IsOutputRedirected)
            AttachConsole(-1);
        try
        {
            string cmd = args[0].ToLowerInvariant();
            bool keep = args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase));
            // 常駐プロセスから使い捨ての子プロセスとして呼ばれる（EnergySaverProcess 参照）
            if (cmd == EnergySaverProcess.SetArg && args.Length > 1)
            {
                EnergySaver.SkipCleanup = true;
                EnergySaver.Set(args[1] == "on");
                return 0;
            }
            if (cmd == "fan")
            {
                Console.WriteLine($"fan={LgEc.ReadFanRpm()?.ToString() ?? "--"}rpm cpu={LgEc.ReadCpuTemp()?.ToString() ?? "--"}C");
                return 0;
            }
            if (cmd != "get")
            {
                if (!Enum.TryParse<LgCooling.Mode>(cmd, true, out var mode) || !Enum.IsDefined(mode))
                {
                    Console.Error.WriteLine("usage: GramCooling [silent|low|normal|high|get|fan] [--keep]");
                    return 2;
                }
                LgCooling.Set(mode, keep);
            }
            var cur = LgCooling.Get();
            Console.WriteLine(cur is null ? "unknown" : cur.Value.ToString().ToLowerInvariant());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool AcquireMutex(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;  // 前の版が解放せずに終わった場合も取得できている
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);
}
