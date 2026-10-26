namespace GramCooling;

/// <summary>
/// %APPDATA%\GramCooling\log.txt への簡単なログ。いつの間にか終了している原因を追うため、起動・終了・例外を残す。
/// 1 MB を超えたら log.old.txt に回す。
/// </summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GramCooling");
    private static readonly string FilePath = Path.Combine(Dir, "log.txt");
    private const long MaxBytes = 1024 * 1024;

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Dir, "log.old.txt"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログが書けなくても本体の動作は止めない
        }
    }

    /// <summary>処理されなかった例外をすべてログに残す。</summary>
    public static void HookUnhandledExceptions()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Write("UI スレッドの例外: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write($"未処理の例外 (終了={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Task の未確認の例外: " + e.Exception);
            e.SetObserved();
        };
    }
}
