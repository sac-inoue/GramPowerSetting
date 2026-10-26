namespace GramCooling;

/// <summary>
/// ブースト: 一定時間だけ冷却モードを「高」、省エネ機能を OFF にし、時間が来たらユーザーの設定に戻す。
///   - アプリ起動時は 5 分、AC アダプタを接続したときは 3 分
///   - ブースト中にユーザーが冷却モード / 省エネ機能を変えたら、その項目はブーストをやめてユーザーの値を尊重する
///     （このアプリのメニューで変えても、My gram やクイック設定で変えても同じ）
///   - ブースト中でないときに読んだ値は「ユーザーの設定」として覚えておく
/// ブーストでの書き込みは「一時的」(Flags=2) にし、再起動後も残る設定には書かない。
/// </summary>
public sealed partial class TrayApp
{
    private static readonly TimeSpan StartupBoost = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AcPluggedBoost = TimeSpan.FromMinutes(3);
    private const LgCooling.Mode BoostCoolingMode = LgCooling.Mode.High;

    // 書き込んでから読み出しに反映されるまでの猶予
    private static readonly TimeSpan WriteSettleTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 省エネ機能を on にする。設定ハンドラの書き込みは「指定値にする」ではなく実質「切り替え」として働く
    /// （すでに OFF のときに OFF を書くと ON になる）ので、実際の状態を見て違うときだけ書き、反映を確かめる。
    /// UI スレッド以外から呼ぶこと（反映を待つ）。
    /// </summary>
    private static void SetSaver(bool on)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (EnergySaverState.IsOn == on)
                return;
            EnergySaverProcess.Set(on);
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline && EnergySaverState.IsOn != on)
                Thread.Sleep(250);
        }
        if (EnergySaverState.IsOn != on)
            throw new InvalidOperationException($"省エネ機能を {(on ? "ON" : "OFF")} にできませんでした");
    }

    /// <summary>省エネ機能の今の状態（実効電源モードから判定）。</summary>
    private static bool ReadSaver() =>
        EnergySaverState.IsOn ?? throw new InvalidOperationException("省エネ機能の状態がまだ分かりません");

    private PowerLineStatus _powerLine = SystemInformation.PowerStatus.PowerLineStatus;

    // 自分が最後に書き込んだ時刻 (UTC)。これより前に読み始めた値は古いので、ユーザーの操作の判定に使わない
    private DateTime _lastWriteUtc;

    // 書き込み中（反映待ちを含む）の数。書き込み中に読んだ値もユーザーの操作の判定に使わない
    private int _writing;

    /// <summary>書き込みを行う。最中と直後に読んだ値は、ユーザーの操作の判定から外す。</summary>
    private async Task WriteAsync(Func<Task> write)
    {
        Interlocked.Increment(ref _writing);
        try
        {
            await write();
        }
        finally
        {
            _lastWriteUtc = DateTime.UtcNow;
            Interlocked.Decrement(ref _writing);
        }
    }

    private bool Boosting => _settings.BoostUntilUtc is not null;

    /// <summary>起動時の処理。通常起動ならブーストを始め、自動更新での再起動なら前のブーストを引き継ぐ。</summary>
    private async Task StartupAsync(bool startup)
    {
        if (startup)
        {
            await RememberCurrentIfUnknownAsync();
            await StartBoostAsync(StartupBoost);
        }
        else
        {
            await RefreshAsync(settings: true);
            await CheckBoostExpiryAsync();  // 再起動の間に期限が過ぎていたら戻す
        }
    }

    /// <summary>初めて使うときは、ブーストする前に今の設定を「ユーザーの設定」として覚える。</summary>
    private async Task RememberCurrentIfUnknownAsync()
    {
        if (_settings.PreferredCooling is not null && _settings.PreferredSaver is not null)
            return;
        await RetryAsync(() =>
        {
            _settings.PreferredCooling ??= LgCooling.Get();
            try { _settings.PreferredSaver ??= ReadSaver(); } catch { }  // 省エネ機能に対応していない Windows
        });
        _settings.Save();
    }

    private async Task StartBoostAsync(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        if (_settings.BoostUntilUtc is { } current && current > until)
            until = current;  // 長い方を残す
        _settings.BoostUntilUtc = until;
        _settings.BoostCooling = true;
        _settings.BoostSaver = true;
        _settings.Save();
        Log.Write($"ブースト開始 ({duration.TotalMinutes} 分, {until.ToLocalTime():HH:mm:ss} まで)");

        await WriteAsync(async () =>
        {
            try
            {
                // ログオン直後は WMI がまだ使えないことがあるので、失敗したら少し待ってやり直す
                await RetryAsync(() =>
                {
                    LgCooling.Set(BoostCoolingMode, keepAfterReboot: false);
                    try { SetSaver(false); } catch (NotSupportedException) { }
                });
            }
            catch (Exception ex)
            {
                ShowError("ブーストを開始できませんでした: " + ex.Message);
            }
        });
        await RefreshAsync(settings: true);
    }

    /// <summary>ブーストの期限が来ていたら、ユーザーの設定に戻す。</summary>
    private async Task CheckBoostExpiryAsync()
    {
        if (_settings.BoostUntilUtc is not { } until || DateTime.UtcNow < until)
            return;

        bool cooling = _settings.BoostCooling, saver = _settings.BoostSaver;
        EndBoost();
        Log.Write($"ブースト終了: 冷却モード→{(cooling ? _settings.PreferredCooling?.ToString() : "そのまま")}, 省エネ→{(saver ? _settings.PreferredSaver?.ToString() : "そのまま")}");

        var preferredCooling = _settings.PreferredCooling;
        var preferredSaver = _settings.PreferredSaver;
        await WriteAsync(async () =>
        {
            try
            {
                await Task.Run(() =>
                {
                    if (cooling && preferredCooling is { } c)
                        LgCooling.Set(c, _settings.KeepAfterReboot);
                    if (saver && preferredSaver is { } s)
                        SetSaver(s);
                });
            }
            catch (Exception ex)
            {
                ShowError("ブースト終了後の設定に戻せませんでした: " + ex.Message);
            }
        });
        await RefreshAsync(settings: true);
    }

    private void EndBoost()
    {
        _settings.BoostUntilUtc = null;
        _settings.BoostCooling = false;
        _settings.BoostSaver = false;
        _settings.Save();
    }

    /// <summary>このアプリのメニューで冷却モードを選んだ。</summary>
    private Task OnUserCoolingAsync(LgCooling.Mode mode)
    {
        Log.Write($"メニューで冷却モードを {mode} に変更");
        _settings.PreferredCooling = mode;
        if (Boosting)
            _settings.BoostCooling = false;
        EndBoostIfNothingHeld();
        _settings.Save();
        return ApplyAsync(() => LgCooling.Set(mode, _settings.KeepAfterReboot));
    }

    /// <summary>このアプリのメニューで省エネ機能を切り替えた。</summary>
    private Task OnUserSaverAsync(bool on)
    {
        Log.Write($"メニューで省エネ機能を {(on ? "ON" : "OFF")} に変更");
        _settings.PreferredSaver = on;
        if (Boosting)
            _settings.BoostSaver = false;
        EndBoostIfNothingHeld();
        _settings.Save();
        return ApplyAsync(() => SetSaver(on));
    }

    private void EndBoostIfNothingHeld()
    {
        if (Boosting && !_settings.BoostCooling && !_settings.BoostSaver)
            EndBoost();
    }

    /// <summary>
    /// 読んだ設定から、ユーザーが（My gram やクイック設定などで）変えたかを判断する。
    /// ブースト中にブーストの値と違っていたら、その項目はブーストをやめてユーザーの値として覚える。
    /// ブースト中でない項目は、読んだ値をそのままユーザーの設定として覚える。
    /// </summary>
    private void TrackUserSettings(DateTime readStartedUtc)
    {
        if (Volatile.Read(ref _writing) > 0 || readStartedUtc < _lastWriteUtc + WriteSettleTime)
            return;  // 自分の書き込みがまだ反映されていないかもしれない値

        bool changed = false;
        if (_cooling is { } c)
        {
            if (Boosting && _settings.BoostCooling)
            {
                if (c != BoostCoolingMode)
                {
                    _settings.BoostCooling = false;
                    _settings.PreferredCooling = c;
                    changed = true;
                }
            }
            else if (_settings.PreferredCooling != c)
            {
                _settings.PreferredCooling = c;
                changed = true;
            }
        }
        if (_saver is { } s)
        {
            if (Boosting && _settings.BoostSaver)
            {
                if (s)
                {
                    _settings.BoostSaver = false;
                    _settings.PreferredSaver = s;
                    changed = true;
                }
            }
            else if (_settings.PreferredSaver != s)
            {
                _settings.PreferredSaver = s;
                changed = true;
            }
        }
        if (changed)
        {
            Log.Write($"ユーザーの設定を記憶: 冷却モード={_settings.PreferredCooling}, 省エネ={_settings.PreferredSaver}");
            EndBoostIfNothingHeld();
            _settings.Save();
        }
    }

    /// <summary>AC アダプタが接続されたらブーストする。SystemEvents のスレッドから呼ばれることがあるので UI スレッドに回す。</summary>
    private void OnPowerStatusChanged()
    {
        var line = SystemInformation.PowerStatus.PowerLineStatus;
        bool plugged = _powerLine == PowerLineStatus.Offline && line == PowerLineStatus.Online;
        _powerLine = line;
        if (plugged)
            _ = StartBoostAsync(AcPluggedBoost);
    }

    /// <summary>メニュー・ツールチップ用の残り時間表示。ブースト中でなければ null。</summary>
    private string? BoostText()
    {
        if (_settings.BoostUntilUtc is not { } until)
            return null;
        var left = until - DateTime.UtcNow;
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        var held = new List<string>();
        if (_settings.BoostCooling)
            held.Add("高");
        if (_settings.BoostSaver)
            held.Add("省エネOFF");
        return $"ブースト中（{string.Join("・", held)}）残り {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private static async Task RetryAsync(Action action, int attempts = 5)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                await Task.Run(action);
                return;
            }
            catch when (i < attempts)
            {
                await Task.Delay(3000);
            }
        }
    }
}
