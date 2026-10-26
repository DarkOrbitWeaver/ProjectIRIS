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

        _stateLabel.Text = StatusOrb.WordForState(_model.State, modeIsAwake).ToUpperInvariant();
        _stateLabel.Foreground = new SolidColorBrush(StatusOrb.ColorForState(_model.State));
        // The strip speaks in the board's captions: uppercase on letterspacing 0.88.
        _modelLabel.Text = _model.ModelLine.ToUpperInvariant();
        _vramLabel.Text = _model.VramText.ToUpperInvariant();
        _contextLabel.Text = _model.ContextText.ToUpperInvariant();
        _ramLabel.Text = _model.RamText.ToUpperInvariant();
        bool microphoneOpen = _runtime.Voice.IsListening;
        _microphoneLabel.Text = microphoneOpen ? "MIC OPEN" : "MIC CLOSED";
        // The board puts MIC OPEN in success green; cyan is her listening orb and
        // composer ring, not the status word.
        _microphoneLabel.Foreground = microphoneOpen ? Palette.SuccessBrush : Palette.TextSecondaryBrush;

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
        if (_isShuttingDown || _isAsleep || !_runtime.Settings.Current.Interface.CloseToOverlay)
        {
            return;
        }

        // Closing the window keeps her alive in the overlay, which is what a companion
        // should do. Quitting is explicit: the Quit button in the overlay.
        eventArgs.Cancel = true;
        Hide();
        ShowOverlay();
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
        await _runtime.DisposeAsync();
        _overlay?.Close();
        Close();

        if (Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
