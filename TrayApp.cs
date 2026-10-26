using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GramCooling;

/// <summary>
/// タスクトレイ常駐。アイコンの色 = 冷却モード、数字 = CPU 温度。
/// 自分が負荷にならないよう、センサーは 5 秒ごと、めったに変わらない設定は 30 秒ごと（とメニューを開いたとき）に読み、
/// 画面ロック中・スリープ中は止める。
/// </summary>
public sealed partial class TrayApp : ApplicationContext
{
    private static readonly LgCooling.Mode[] CoolingModes =
        { LgCooling.Mode.Silent, LgCooling.Mode.Low, LgCooling.Mode.Normal, LgCooling.Mode.High };

    private static readonly LgBattery.Mode[] BatteryModes =
        { LgBattery.Mode.Off, LgBattery.Mode.Limit80, LgBattery.Mode.Ai };

    private const int SensorIntervalMs = 5000;
    private const int SettingsEveryTicks = 6;  // 5 秒 × 6 = 30 秒
    private const int MaxSensorMisses = 3;     // これだけ続けて読めなかったら "--" にする

    private readonly Settings _settings = Settings.Load();
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _boostItem;
    private readonly Dictionary<LgCooling.Mode, ToolStripMenuItem> _coolingItems = new();
    private readonly Dictionary<LgBattery.Mode, ToolStripMenuItem> _batteryItems = new();
    private readonly ToolStripMenuItem _keepItem;
    private readonly ToolStripMenuItem _saverItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly Dictionary<IconValue, ToolStripMenuItem> _iconValueItems = new();
    private readonly Updater _updater;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = SensorIntervalMs };
    private readonly CpuStats? _cpu = TryCreateCpuStats();
    private IntPtr _hIcon;
    private string? _iconKey;
    private bool _busy;
    private bool _settingsDue = true;
    private int _tick;

    // 最後に読めた値
    private LgCooling.Mode? _cooling;
    private LgBattery.Mode? _battery;
    private bool? _saver;
    private int? _rpm, _temp, _load;
    private double? _ghz;
    private int _rpmMisses, _tempMisses;
    private string? _sensorError, _settingsError;

    /// <param name="startup">通常の起動ならブーストを始める。自動更新での再起動なら false（前のブーストを引き継ぐ）。</param>
    public TrayApp(bool startup)
    {
        var menu = new ContextMenuStrip();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        menu.Opening += (_, _) => _ = RefreshAsync(settings: true);

        _statusItem = new ToolStripMenuItem("読み取り中…") { Enabled = false };
        menu.Items.Add(_statusItem);
        _boostItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        menu.Items.Add(_boostItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("冷却モード") { Enabled = false });
        foreach (var m in CoolingModes)
        {
            var item = new ToolStripMenuItem("　" + LgCooling.DisplayName(m));
            item.Click += async (_, _) => await OnUserCoolingAsync(m);
            _coolingItems[m] = item;
            menu.Items.Add(item);
        }
        _keepItem = new ToolStripMenuItem("　再起動後も維持する") { CheckOnClick = true, Checked = _settings.KeepAfterReboot };
        _keepItem.CheckedChanged += (_, _) =>
        {
            _settings.KeepAfterReboot = _keepItem.Checked;
            _settings.Save();
        };
        menu.Items.Add(_keepItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("バッテリー寿命を延ばす") { Enabled = false });
        foreach (var m in BatteryModes)
        {
            var item = new ToolStripMenuItem("　" + LgBattery.DisplayName(m));
            item.Click += async (_, _) => await ApplyAsync(() => LgBattery.Set(m));
            _batteryItems[m] = item;
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        _saverItem = new ToolStripMenuItem("省エネ機能（Windows）");
        _saverItem.Click += async (_, _) => await OnUserSaverAsync(!_saverItem.Checked);
        menu.Items.Add(_saverItem);

        menu.Items.Add(new ToolStripSeparator());
        var iconMenu = new ToolStripMenuItem("アイコンの表示");
        foreach (var (v, label) in new[] { (IconValue.Temp, "CPU 温度 [℃]"), (IconValue.Load, "CPU 負荷 [%]"), (IconValue.Clock, "CPU クロック [GHz]") })
        {
            var item = new ToolStripMenuItem(label) { Checked = _settings.IconValue == v };
            item.Click += (_, _) =>
            {
                _settings.IconValue = v;
                _settings.Save();
                foreach (var (iv, it) in _iconValueItems)
                    it.Checked = iv == v;
                Show();
            };
            _iconValueItems[v] = item;
            iconMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(iconMenu);
        _autoStartItem = new ToolStripMenuItem("Windows 起動時に自動起動");
        _autoStartItem.Click += (_, _) => ToggleAutoStart();
        menu.Items.Add(_autoStartItem);
        menu.Items.Add("終了", null, (_, _) =>
        {
            Log.Write("メニューの「終了」");
            ExitThread();
        });
        menu.Items.Add(new ToolStripMenuItem($"ビルド {Updater.BuildTime:yyyy-MM-dd HH:mm}") { Enabled = false });

        _tray = new NotifyIcon { ContextMenuStrip = menu, Text = "gram 冷却", Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            // 左クリックでもメニューを出す
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);
        };
        SetIcon(Color.DimGray, "--");

        FirstRun();
        _autoStartItem.Checked = SafeIsRegistered();

        _updater = new Updater(ExitThread, ShowError);

        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // 省エネ機能の ON/OFF は実効電源モードの変化として通知される
        EnergySaverState.Changed += OnEnergySaverChanged;
        EnergySaverState.Start();

        _timer.Tick += async (_, _) =>
        {
            // ブースト中はユーザーの変更にすぐ気づけるよう、設定も毎回読む
            if (++_tick % SettingsEveryTicks == 0 || Boosting)
                _settingsDue = true;
            await CheckBoostExpiryAsync();
            await RefreshAsync(settings: false);
        };
        _timer.Start();
        _ = StartupAsync(startup);
    }

    /// <summary>初回起動時は自動起動を ON にする（既定 ON）。</summary>
    private void FirstRun()
    {
        if (_settings.FirstRunDone)
            return;
        try
        {
            AutoStart.Set(true);
        }
        catch (Exception ex)
        {
            ShowError("自動起動の登録に失敗しました: " + ex.Message);
        }
        _settings.FirstRunDone = true;
        _settings.Save();
    }

    private void ToggleAutoStart()
    {
        try
        {
            AutoStart.Set(!_autoStartItem.Checked);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        _autoStartItem.Checked = SafeIsRegistered();
    }

    private static bool SafeIsRegistered()
    {
        try { return AutoStart.IsRegistered(); }
        catch { return false; }
    }

    // 画面ロック中・スリープ中は読み取りを止める
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) =>
        Log.Write($"Windows のセッション終了 ({e.Reason})");

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            _timer.Stop();
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
            Resume();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => _ui.Post(_ =>
    {
        if (e.Mode == PowerModes.Suspend)
            _timer.Stop();
        else if (e.Mode == PowerModes.Resume)
            Resume();
        else if (e.Mode == PowerModes.StatusChange)
            OnPowerStatusChanged();  // AC アダプタの抜き差し
    }, null);

    // 拡大率を変えたらアイコンの大きさが変わるので描き直す（大きさはキーに入っているので Show で判定される）
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Show();

    // 通知は別スレッドから来るので UI スレッドで読み直す
    private void OnEnergySaverChanged() => _ui.Post(_ => _ = RefreshAsync(settings: true), null);

    private async void Resume()
    {
        _timer.Start();
        _powerLine = SystemInformation.PowerStatus.PowerLineStatus;  // スリープ中の抜き差しはブーストしない
        await CheckBoostExpiryAsync();
        await RefreshAsync(settings: true);
    }

    private async Task ApplyAsync(Action action)
    {
        await WriteAsync(async () =>
        {
            try
            {
                await Task.Run(action);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        });
        await RefreshAsync(settings: true);
    }

    private async Task RefreshAsync(bool settings)
    {
        _settingsDue |= settings;
        if (_busy)
            return;  // 読み取り中なら次の周期に回す（_settingsDue は残る）
        _busy = true;
        try
        {
            bool readSettings = _settingsDue;
            _settingsDue = false;
            var started = DateTime.UtcNow;
            await Task.Run(() => Read(readSettings));
            if (readSettings)
                TrackUserSettings(started);
            Show();
        }
        finally
        {
            _busy = false;
        }
    }

    private static CpuStats? TryCreateCpuStats()
    {
        try { return new CpuStats(); }
        catch { return null; }  // パフォーマンスカウンタが壊れている環境では出さない
    }

    private void Read(bool settings)
    {
        try { _load = _cpu?.ReadLoad(); } catch { _load = null; }
        try { _ghz = _cpu?.ReadGHz(); } catch { _ghz = null; }
        try
        {
            // 一時的に読めなかったとき（AC アダプタの抜き差し直後など）は直前の値を出し続ける
            Keep(ref _rpm, ref _rpmMisses, LgEc.ReadFanRpm());
            Keep(ref _temp, ref _tempMisses, LgEc.ReadCpuTemp());
            _sensorError = null;
        }
        catch (Exception ex)
        {
            _sensorError = ex.Message;
        }

        if (!settings)
            return;
        string? error = null;
        try { _cooling = LgCooling.Get(); } catch (Exception ex) { error = ex.Message; }
        try { _battery = LgBattery.Get(); } catch (Exception ex) { error ??= ex.Message; }
        try { _saver = ReadSaver(); } catch (Exception ex) { _saver = null; error ??= ex.Message; }
        _settingsError = error;
    }

    private static void Keep(ref int? value, ref int misses, int? read)
    {
        if (read is not null)
        {
            value = read;
            misses = 0;
        }
        else if (++misses >= MaxSensorMisses)
        {
            value = null;
        }
    }

    private void Show()
    {
        foreach (var (m, item) in _coolingItems)
            item.Checked = m == _cooling;
        foreach (var (m, item) in _batteryItems)
            item.Checked = m == _battery;
        _saverItem.Checked = _saver == true;
        _saverItem.Enabled = _saver is not null;

        string mode = _cooling is { } c ? LgCooling.DisplayName(c) : "不明";
        string fan = _rpm is { } r ? $"{r} rpm" : "-- rpm";
        string cpu = _temp is { } t ? $"{t}℃" : "--℃";
        string load = _load is { } l ? $"{l}%" : "--%";
        string ghz = _ghz is { } g ? $"{g:0.00} GHz" : "-- GHz";
        string? error = _sensorError ?? _settingsError;
        _statusItem.Text = error is null ? $"ファン {fan}　CPU {cpu} / {load} / {ghz}" : "読み取り失敗: " + error;

        string? boost = BoostText();
        _boostItem.Visible = boost is not null;
        if (boost is not null)
            _boostItem.Text = boost;

        string tip = $"gram 冷却: {mode}\nファン {fan}\nCPU {cpu} / {load} / {ghz}" + (boost is null ? "" : "\n" + boost);
        tip = tip.Length <= 127 ? tip : tip[..127];
        if (_tray.Text != tip)
            _tray.Text = tip;

        SetIcon(ModeColor(_cooling), IconText());
    }

    private string IconText() => _settings.IconValue switch
    {
        IconValue.Load => _load is { } l ? l.ToString() : "--",
        IconValue.Clock => _ghz is { } g ? g.ToString("0.0") : "--",
        _ => _temp is { } t ? Math.Min(t, 99).ToString() : "--",
    };

    private static Color ModeColor(LgCooling.Mode? m) => m switch
    {
        LgCooling.Mode.Silent => Color.FromArgb(0x25, 0x63, 0xEB),  // 青
        LgCooling.Mode.Low => Color.FromArgb(0x05, 0x96, 0x69),     // 緑
        LgCooling.Mode.Normal => Color.FromArgb(0x52, 0x52, 0x5B),  // 灰
        LgCooling.Mode.High => Color.FromArgb(0xDC, 0x26, 0x26),    // 赤
        _ => Color.DimGray,
    };

    private void SetIcon(Color back, string text)
    {
        // 見た目が変わらないなら描き直さない
        int size = TrayIconSize();
        string key = $"{back.ToArgb()}:{text}:{size}";
        if (key == _iconKey)
            return;
        _iconKey = key;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = RoundedRect(new RectangleF(0, 0, size, size), size / 6f);
            using var brush = new SolidBrush(back);
            g.FillPath(brush, path);
            // 測るときと描くときで同じ書式（余白なし・折り返しなし）を使う。
            // 既定の StringFormat だと左右に余白が付いて幅が足りなくなり、"2.7" の "7" が次の行に折り返されて消える
            using var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            using var font = FitFont(g, text, size, sf);
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size + 1), sf);
        }

        IntPtr old = _hIcon;
        _hIcon = bmp.GetHicon();
        _tray.Icon = Icon.FromHandle(_hIcon);
        if (old != IntPtr.Zero)
            DestroyIcon(old);
    }

    /// <summary>
    /// タスクバーの DPI に合ったトレイアイコンの大きさ（125% なら 20px）。
    /// 小さい画像を渡すと引き伸ばされずに余白付きで表示され、他のアイコンより小さく見える。
    /// </summary>
    private static int TrayIconSize()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0;
        if (dpi == 0)
            dpi = GetDpiForSystem();
        int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        return size > 0 ? size : SystemInformation.SmallIconSize.Width;
    }

    /// <summary>"100" や "2.7" のように長い文字でもアイコンに収まる大きさのフォント。</summary>
    private static Font FitFont(Graphics g, string text, int size, StringFormat sf)
    {
        float px = size * 0.68f;
        while (true)
        {
            var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
            float width = g.MeasureString(text, font, PointF.Empty, sf).Width;
            if (width <= size || px <= size * 0.4f)
                return font;
            font.Dispose();
            px -= 0.5f;
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void ShowError(string message)
    {
        Log.Write("エラー通知: " + message);
        ShowBalloon(message);
    }

    private void ShowBalloon(string message) =>
        _tray.ShowBalloonTip(5000, "gram 冷却", message, ToolTipIcon.Error);

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _updater.Dispose();
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        EnergySaverState.Changed -= OnEnergySaverChanged;
        EnergySaverState.Stop();
        _cpu?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_hIcon != IntPtr.Zero)
            DestroyIcon(_hIcon);
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
