using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.App.Interface.Views;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Events;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;

namespace CompanionOS.App.Interface;

/// <summary>
/// The window's live behaviour: syncing model values onto the screen, keyboard shortcuts,
/// closing to the overlay, and the clean shutdown path.
///
/// Why this is split from the constructor and header: layout is read top to bottom while
/// changing padding, and behaviour is read while chasing a bug. They are different tasks.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Copies live model values onto the screen. A hidden shell and Gaming mode
    /// deliberately refresh more slowly: no visible UI needs a 12.5 Hz redraw while the
    /// foreground application owns the machine.</summary>
    private void SyncFromModel()
    {
        long syncStarted = Stopwatch.GetTimestamp();

        // The board's title orb wears her state: violet when ready, cyan while listening,
        // speech violet while speaking. Recolouring happens only when the colour moves.
        Color stateColor = StatusOrb.ColorForState(_model.State);
        if (_titleOrbColor != stateColor)
        {
            _titleOrbColor = stateColor;
            IconGlyph.RecolorOrb(_titleOrb, stateColor, 0.35);
            IconGlyph.RecolorOrb(_railOrb, stateColor, 0.35);
        }

        bool modeIsAwake = _model.Mode == OperatingMode.Awake;

        // The strip speaks in the board's captions: uppercase on letterspacing 0.88.
        string stateText = StatusOrb.WordForState(_model.State, modeIsAwake).ToUpperInvariant();
        Color stateLabelColor = StatusOrb.ColorForState(_model.State);
        if (!string.Equals(_renderedStateText, stateText, StringComparison.Ordinal) ||
            _renderedStateColor != stateLabelColor)
        {
            _renderedStateText = stateText;
            _renderedStateColor = stateLabelColor;
            _stateLabel.Text = stateText;
            _stateLabel.Foreground = new SolidColorBrush(stateLabelColor);
        }

        CopyWhenChanged(_model.ModelLine.ToUpperInvariant(), _renderedModelLine,
            rendered => _renderedModelLine = rendered, _modelLabel);
        CopyWhenChanged(_model.VramText.ToUpperInvariant(), _renderedVram,
            rendered => _renderedVram = rendered, _vramLabel);
        CopyWhenChanged(_model.ContextText.ToUpperInvariant(), _renderedContext,
            rendered => _renderedContext = rendered, _contextLabel);
        CopyWhenChanged(_model.RamText.ToUpperInvariant(), _renderedRam,
            rendered => _renderedRam = rendered, _ramLabel);

        bool microphoneOpen = _runtime.Voice.IsListening;
        string microphoneText = microphoneOpen ? "MIC OPEN" : "MIC CLOSED";
        // The board puts MIC OPEN in success green; cyan is her listening orb and
        // composer ring, not the status word.
        if (!string.Equals(_renderedMicText, microphoneText, StringComparison.Ordinal) ||
            _renderedMicOpen != microphoneOpen)
        {
            _renderedMicText = microphoneText;
            _renderedMicOpen = microphoneOpen;
            _microphoneLabel.Text = microphoneText;
            _microphoneLabel.Foreground = microphoneOpen ? Palette.SuccessBrush : Palette.TextSecondaryBrush;
        }

        PermissionRequest? pendingPermission = _model.PendingPermission;
        if (pendingPermission is not null && pendingPermission != _shownPermission)
        {
            _shownPermission = pendingPermission;
            _permissionQuestion.Text = _model.PendingPermissionQuestion;
            if (_permissionAlwaysButton is not null)
            {
                _permissionAlwaysButton.IsVisible = _shownPermission.CanAlwaysAllow;
            }
            if (_permissionCaption is not null)
            {
                _permissionCaption.Text = _shownPermission.CanAlwaysAllow
                    ? "Unanswered requests expire in 15 seconds. Always allow remembers the shown capability for this module, or just this tool when no scope is listed."
                    : "This action requires your approval each time. Unanswered requests expire in 15 seconds.";
            }
            _permissionCard.IsVisible = true;
        }
        else if (!_model.HasPendingPermission && _permissionCard.IsVisible)
        {
            _shownPermission = null;
            _permissionCard.IsVisible = false;
        }

        // The memory graph only gets UI time when the memory screen is the one on
        // display. Refreshing a hidden screen was stealing a ~50ms slot from the UI
        // thread every three seconds, for nothing the user could see.
        if (ReferenceEquals(_contentHost.Content, _memoryView))
        {
            _memoryView.RefreshIfVisible();
        }
        UpdateUiSyncCadence();
        SystemWatch.RecordSync((Stopwatch.GetTimestamp() - syncStarted) * 1_000.0 / Stopwatch.Frequency);
    }

    /// <summary>Writes one status-strip value onto its label only when it moved. The
    /// comparison keeps the 80 ms sync tick from rebuilding identical text.</summary>
    private void CopyWhenChanged(string next, string rendered, Action<string> setRendered, TextBlock label)
    {
        if (string.Equals(rendered, next, StringComparison.Ordinal))
        {
            return;
        }

        setRendered(next);
        label.Text = next;
    }

    private void UpdateUiSyncCadence()
    {
        TimeSpan desired = !IsVisible
            ? TimeSpan.FromSeconds(1)
            : _model.State == MindState.Gaming
                ? TimeSpan.FromMilliseconds(500)
                : TimeSpan.FromMilliseconds(80);

        if (_uiSyncTimer.Interval != desired)
        {
            _uiSyncTimer.Interval = desired;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        bool isControlPressed = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool isShiftPressed = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool isAltPressed = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // Hidden black-box self-test: stalls the UI thread on purpose for 8.5 seconds so
        // the watchdog, the stall record and the stack capture can be proven on a real
        // machine. Ctrl+Alt+Shift+F5 — never pressed by accident.
        if (isControlPressed && isAltPressed && isShiftPressed && eventArgs.Key == Key.F5)
        {
            SystemWatch.SelfTestStall();
            eventArgs.Handled = true;
            return;
        }

        if (isControlPressed && isShiftPressed && eventArgs.Key == Key.S)
        {
            _ = _model.AskAboutScreenAsync();
            eventArgs.Handled = true;
            return;
        }

        if (!isControlPressed)
        {
            if (eventArgs.Key == Key.Escape)
            {
                _model.Interrupt();
                eventArgs.Handled = true;
            }

            return;
        }

        switch (eventArgs.Key)
        {
            case Key.D1:
                ShowChat();
                eventArgs.Handled = true;
                break;
            case Key.D2:
                ShowMemory();
                eventArgs.Handled = true;
                break;
            case Key.D3:
                ShowBody();
                eventArgs.Handled = true;
                break;
            case Key.D4:
                ShowPersona();
                eventArgs.Handled = true;
                break;
            case Key.D5:
                ShowSettings();
                eventArgs.Handled = true;
                break;
            case Key.D6:
                ShowDiagnostics();
                eventArgs.Handled = true;
                break;
            case Key.M:
                _model.ToggleMicrophone();
                eventArgs.Handled = true;
                break;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        SystemWatch.Trace("life", "window close requested");
        if (_isShuttingDown || _isAsleep)
        {
            return;
        }

        if (!_runtime.Settings.Current.Interface.CloseToOverlay)
        {
            // CloseToOverlay=false means a plain close QUITs. The old return-path left
            // the process running with nothing on screen, no tray icon and no overlay:
            // a fully-alive invisible process occupying VRAM while the user believes
            // she quit (2026-10-27 audit).
            // The close is cancelled either way; quitting runs through QuitAsync, which
            // closes the window itself after the runtime has shut down.
            eventArgs.Cancel = true;
            SystemWatch.Trace("life", "window close requested - CloseToOverlay is off, quitting");
            QuitFromClose();
            return;
        }

        // Closing the window keeps her alive in the overlay, which is what a companion
        // should do. Quitting is explicit: the Quit button in the overlay.
        eventArgs.Cancel = true;
        Hide();
        ShowOverlay();
    }

    /// <summary>Runs the quit from the close event on the UI thread, without unwinding
    /// the window-close pipeline itself.</summary>
    private async void QuitFromClose()
    {
        try
        {
            await QuitAsync();
        }
        catch (Exception exception)
        {
            SystemWatch.Trace("life", $"quit from close faulted: {exception.Message}");
        }
    }

    /// <summary>
    /// Her off switch.
    ///
    /// Everything is released in an order that cannot lose data: the UI stops reading,
    /// the microphone device is closed, the speech model is freed, the database
    /// checkpoints and the model process is killed. After this method returns the process
    /// holds a tray icon and nothing else.
    /// </summary>
    public async Task SleepAsync()
    {
        if (_isAsleep)
        {
            return;
        }

        // Ask first: she may only go dark if something remains on screen to bring her back.
        if (!_runtime.Settings.Current.System.SleepToTray || EnterSleepHost is null || !EnterSleepHost())
        {
            _runtime.Events.PublishNotice(
                "The notification area is unavailable, so I am resting instead of switching off. " +
                "Say my name to wake me.",
                NoticeSeverity.Warning);
            await _model.ToggleSleepAsync().ConfigureAwait(false);
            return;
        }

        _isAsleep = true;
        _uiSyncTimer.Stop();
        _attentionSubscription.Dispose();
        _model.Detach();

        _overlay?.Hide();
        _overlay?.Close();
        _overlay = null;

        Hide();

        try
        {
            await _runtime.DisposeAsync();
        }
        catch (Exception exception)
        {
            // A failed shutdown must still leave her off, never half-on. The tray icon
            // has already been created, so the user can still start her again.
            CompanionOS.Core.Diagnostics.CompanionLog.Error("App", "Shutdown while sleeping failed.", exception);
        }
    }

    /// <summary>Shuts the whole system down cleanly. Used by the overlay's Quit action.</summary>
    public async Task QuitAsync()
    {
        _isShuttingDown = true;
        _uiSyncTimer.Stop();
        _attentionSubscription.Dispose();
        _model.Detach();

        // No teardown exception may strand the process. SleepAsync has the same guard;
        // the Quit path used to miss it — one throw from DisposeAsync meant the window
        // never closed, the runtime never shut down and _isShuttingDown was already
        // true, so retrying quit did nothing at all (2026-10-27 audit).
        try
        {
            await _runtime.DisposeAsync();
            _overlay?.Close();
            Close();
        }
        catch (Exception exception)
        {
            CompanionOS.Core.Diagnostics.CompanionLog.Error(
                "App", "Cleanup while quitting did not finish; forcing the shutdown.", exception);
        }

        if (Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
