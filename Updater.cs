using System.Diagnostics;

namespace GramCooling;

/// <summary>
/// 自動更新。インストール先の update フォルダに新しい版が publish されたら、
/// 実行中のファイルを *.old に退避（実行中でも名前の変更はできる）してコピーし、新しい版を起動して自分は終了する。
/// 監視は FileSystemWatcher なのでポーリングはしない。
/// </summary>
public sealed class Updater : IDisposable
{
    public const string RestartedArg = "--restarted";

    private const string MainFile = "GramCooling.dll";
    private const string OldSuffix = ".old";

    private readonly string _installDir = AppContext.BaseDirectory;
    private readonly string _updateDir;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 3000 };
    private readonly Action _exit;
    private readonly Action<string> _error;
    private readonly SynchronizationContext _ui;

    /// <summary>UI スレッドで作ること。</summary>
    /// <param name="exit">新しい版を起動したあとに呼ぶ（自分を終了させる）。UI スレッドで呼ばれる。</param>
    /// <param name="error">更新に失敗したときの通知。UI スレッドで呼ばれる。</param>
    public Updater(Action exit, Action<string> error)
    {
        _exit = exit;
        _error = error;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _updateDir = Path.Combine(_installDir, "update");
        Directory.CreateDirectory(_updateDir);

        CleanupOldFiles();

        // publish はファイルを次々に書くので、最後の変更から 3 秒たってから入れ替える
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            TryUpdate();
        };
        _watcher = new FileSystemWatcher(_updateDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;

        // 起動前に置かれていた分も拾う
        if (File.Exists(Path.Combine(_updateDir, MainFile)))
            _debounce.Start();
    }

    /// <summary>ビルド日時（メニュー表示用）。</summary>
    public static DateTime BuildTime => File.GetLastWriteTime(Path.Combine(AppContext.BaseDirectory, MainFile));

    // FileSystemWatcher のイベントは別スレッドから来るので、UI スレッドでタイマーを再始動させる
    private void OnChanged(object sender, FileSystemEventArgs e) => _ui.Post(_ => RestartDebounce(), null);

    private void RestartDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void TryUpdate()
    {
        if (!File.Exists(Path.Combine(_updateDir, MainFile)) || !File.Exists(Path.Combine(_updateDir, "GramCooling.exe")))
            return;

        var files = Directory.GetFiles(_updateDir, "*", SearchOption.AllDirectories);
        if (!files.All(IsComplete))
        {
            RestartDebounce();  // まだ書き込み中
            return;
        }

        var moved = new List<string>();  // 退避したファイル（失敗時に戻す）
        var copied = new List<string>(); // 新しくできたファイル（失敗時に消す）
        try
        {
            foreach (var src in files)
            {
                string rel = Path.GetRelativePath(_updateDir, src);
                string dst = Path.Combine(_installDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (File.Exists(dst))
                {
                    File.Move(dst, dst + OldSuffix, overwrite: true);
                    moved.Add(dst);
                }
                File.Copy(src, dst);
                copied.Add(dst);
            }
        }
        catch (Exception ex)
        {
            foreach (var dst in copied)
                TryDelete(dst);
            foreach (var dst in moved)
            {
                try { File.Move(dst + OldSuffix, dst, overwrite: true); } catch { }
            }
            _error("更新に失敗しました: " + ex.Message);
            return;
        }

        foreach (var src in files)
            TryDelete(src);

        Process.Start(new ProcessStartInfo(Path.Combine(_installDir, "GramCooling.exe"), RestartedArg) { UseShellExecute = false });
        _exit();
    }

    private static bool IsComplete(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void CleanupOldFiles()
    {
        foreach (var old in Directory.GetFiles(_installDir, "*" + OldSuffix, SearchOption.AllDirectories))
            TryDelete(old);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
