using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Events;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Voice;
using NAudio.Wave;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// Settings, transcribed 1:1 from the Settings board:
///
///   header    "Settings" at 28 px, the one-line uppercase subtitle, and the More
///             options button — which opens her config folder, a real action
///   cards     a two-column grid of Audio, Startup, Overlay and Brain, each exactly
///             as the board: #1A1C24 fill, one-pixel stroke, 16 px radius, 20/24 padding,
///             a 20/26 title, and rows that breathe 14 px with a hairline between
///   controls  the board's 220 × 36 dropdowns, its 28 px hotkey chips, its 40 × 24 switch
///
/// Every control writes through: a change is saved to disk the moment it happens and
/// re-applied live where the machine can — the board has no Save button, so this screen
/// has none either. Nothing here edits "a copy": what you touch is what she runs.
/// </summary>
public sealed class SettingsView : UserControl
{
    private readonly CompanionRuntime _runtime;
    private readonly ShellModel _model;

    public SettingsView(CompanionRuntime runtime, ShellModel model)
    {
        _runtime = runtime;
        _model = model;

        Content = BuildLayout();
    }

    private Control BuildLayout()
    {
        // Header: title block left, the More-options button right.
        TextBlock title = Ui.Text("Settings", Palette.FontPage, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        title.LineHeight = 34;

        TextBlock subtitle = Ui.Text("THIS MACHINE ONLY · EVERYTHING ELSE LIVES WHERE YOU USE IT",
            Palette.FontCaption, Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
        subtitle.LineHeight = 16;
        subtitle.LetterSpacing = 0.88;

        Button moreOptions = new()
        {
            Content = IconGlyph.MakeDots(20, Palette.TextSecondaryBrush),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2.4),
            MinWidth = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top
        };
        ToolTip.SetTip(moreOptions, "Open her config folder");
        moreOptions.Click += (_, _) => OpenConfigFolder();

        StackPanel heading = new() { Spacing = 4 };
        heading.Children.Add(title);
        heading.Children.Add(subtitle);

        Grid headerRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(heading, 0);
        Grid.SetColumn(moreOptions, 1);
        headerRow.Children.Add(heading);
        headerRow.Children.Add(moreOptions);

        // The board's grid: two columns with a real 16 px gutter, 16 px between rows.
        // Columns are 0/1/2 — the middle column IS the gutter, so the right-hand cards
        // live in column 2, never in it.
        Grid cards = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,16,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };
        Place(cards, BuildAudioCard(), 0, 0);
        Place(cards, BuildStartupCard(), 0, 2);
        Place(cards, BuildOverlayCard(), 1, 0);
        Place(cards, BuildBrainCard(), 1, 2);
        Control readiness = BuildReadinessCard();
        readiness.Margin = new Thickness(0, 0, 0, 16);
        Grid.SetRow(readiness, 2);
        Grid.SetColumn(readiness, 0);
        Grid.SetColumnSpan(readiness, 3);
        cards.Children.Add(readiness);

        // Board scroll: 32 px top/bottom, 48 px sides, 24 px between header and grid.
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(48, 32),
                Spacing = 24,
                Children = { headerRow, cards }
            }
        };
    }

    // ---------------------------------------------------------------- audio

    private Control BuildAudioCard()
    {
        TextBlock audioHealth = Ui.Text(_runtime.Voice.GetReadiness().Summary, Palette.FontCaption,
            Palette.TextSecondaryBrush, FontWeight.Medium, wrap: true);
        audioHealth.MaxWidth = 220;
        audioHealth.TextAlignment = TextAlignment.Right;
        Button refreshAudioHealth = Ui.GhostButton("Check", () =>
        {
            // This only re-reads device/configuration truth. It does not record,
            // synthesize speech or start a model, so it is safe during Gaming Mode.
            audioHealth.Text = _runtime.Voice.GetReadiness().Summary;
        });
        Button testMicrophone = Ui.GhostButton("Test mic", () =>
            _ = RunAudioDiagnosticAsync(audioHealth, microphone: true));
        Button testVoice = Ui.GhostButton("Test voice", () =>
            _ = RunAudioDiagnosticAsync(audioHealth, microphone: false));
        ToolTip.SetTip(testMicrophone,
            "Explicitly opens the selected microphone for 1.2 seconds and checks whether it receives a sound level. Audio is not saved or transcribed.");
        ToolTip.SetTip(testVoice,
            "Explicitly contacts the local voice engine and plays one short sentence through the selected speakers.");
        StackPanel healthControls = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Palette.SpaceSm,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { audioHealth, refreshAudioHealth }
        };

        List<(string Label, string Value)> microphoneItems = new() { ("Default microphone", "-1") };
        IReadOnlyList<string> microphones = MicrophoneCapture.ListInputDevices();
        for (int index = 0; index < microphones.Count; index++)
        {
            microphoneItems.Add((microphones[index], index.ToString()));
        }

        BoardSelect microphone = new();
        microphone.SetItems(microphoneItems, _runtime.Settings.Current.Ears.InputDeviceNumber.ToString());
        ToolTip.SetTip(microphone, "The ears she listens through. Applies immediately — she reopens cleanly.");
        microphone.Changed += value =>
        {
            int device = int.Parse(value, CultureInfo.InvariantCulture);
            if (device == _runtime.Settings.Current.Ears.InputDeviceNumber)
            {
                return;
            }

            _runtime.Settings.Current.Ears.InputDeviceNumber = device;
            PublishChanged($"Microphone set to {LabelOf(microphoneItems, value)}.");
            RebindMicrophone();
        };

        List<(string Label, string Value)> speakerItems = new() { ("Default speakers", "-1") };
        for (int index = 0; index < WaveOut.DeviceCount; index++)
        {
            speakerItems.Add((WaveOut.GetCapabilities(index).ProductName, index.ToString()));
        }

        BoardSelect speakers = new();
        speakers.SetItems(speakerItems, _runtime.Settings.Current.Voice.OutputDeviceNumber.ToString());
        ToolTip.SetTip(speakers, "Where her voice and her music play. Applies from the next thing she plays.");
        speakers.Changed += value =>
        {
            int device = int.Parse(value, CultureInfo.InvariantCulture);
            if (device == _runtime.Settings.Current.Voice.OutputDeviceNumber)
            {
                return;
            }

            _runtime.Settings.Current.Voice.OutputDeviceNumber = device;
            PublishChanged($"Her voice now plays through {LabelOf(speakerItems, value)}.");
        };

        BoardSwitch pauseForVoice = new() { IsOn = _runtime.Settings.Current.Voice.PauseMediaWhenListening };
        ToolTip.SetTip(pauseForVoice,
            "When you call Iris, she asks Windows to pause the current video or song before listening. She never pauses anything in the background until you address her.");
        pauseForVoice.Changed += enabled =>
        {
            _runtime.Settings.Current.Voice.PauseMediaWhenListening = enabled;
            _runtime.Settings.Save();
            PublishChanged(enabled
                ? "Iris will pause active media when you call her."
                : "Iris will leave media playing while she listens.");
        };

        BoardSwitch resumeAfterVoice = new() { IsOn = _runtime.Settings.Current.Voice.ResumeMediaAfterVoiceTurn };
        ToolTip.SetTip(resumeAfterVoice,
            "After a voice turn, Iris resumes only media that she herself paused. Turn this off if you prefer to resume manually.");
        resumeAfterVoice.Changed += enabled =>
        {
            _runtime.Settings.Current.Voice.ResumeMediaAfterVoiceTurn = enabled;
            _runtime.Settings.Save();
            PublishChanged(enabled
                ? "Iris will resume media she paused after a voice turn."
                : "Media Iris pauses will stay paused until you resume it.");
        };

        return Card("Audio", new[]
        {
            Row("Microphone", microphone, "Ctrl+M toggles her ears; the device here is what she listens on."),
            null,
            Row("Speakers", speakers, "-1 lets Windows choose. Naming yours pins her voice and music to it."),
            null,
            Row("Audio health", healthControls,
                "Checks whether the configured microphone, recognizer and speaker route are presently available. It does not start recording or play sound."),
            null,
            Row("Microphone test", testMicrophone,
                "Speak while the short test runs. Iris reports whether the selected input receives a signal; the samples are not saved or sent to speech recognition."),
            null,
            Row("Voice test", testVoice,
                "A deliberate end-to-end test: checks the configured local TTS engine then plays one short line through the selected output."),
            null,
            Row("Pause media for voice", pauseForVoice,
                "Iris pauses the active video or song only when you call her."),
            null,
            Row("Resume after voice", resumeAfterVoice,
                "She resumes only media she paused herself.")
        });
    }

    private async Task RunAudioDiagnosticAsync(TextBlock health, bool microphone)
    {
        try
        {
            health.Foreground = Palette.TextSecondaryBrush;
            health.Text = microphone ? "Testing microphone…" : "Checking voice and speakers…";
            VoiceDiagnosticResult result = microphone
                ? await _runtime.Voice.TestMicrophoneAsync()
                : await _runtime.Voice.TestVoiceOutputAsync();
            health.Text = result.Detail;
            health.Foreground = result.Succeeded ? Palette.SuccessBrush : Palette.WarningBrush;
            _runtime.Events.PublishNotice(result.Detail,
                result.Succeeded ? NoticeSeverity.Success : NoticeSeverity.Warning);
        }
        catch (Exception exception)
        {
            health.Text = $"Audio diagnostic failed: {exception.Message}";
            health.Foreground = Palette.DangerBrush;
            _runtime.Events.PublishNotice(health.Text, NoticeSeverity.Danger);
        }
    }

    private void RebindMicrophone()
    {
        if (!_runtime.Brain.IsMicrophoneOpen)
        {
            return;
        }

        // The device is read every time the microphone opens, so a close-and-reopen is
        // the honest, instant rebind — no restart, no lying settings screen.
        _runtime.Brain.SetMicrophoneOpen(false);
        _runtime.Brain.SetMicrophoneOpen(true);
    }

    // ---------------------------------------------------------- first-run truth

    private Control BuildReadinessCard()
    {
        List<TextBlock> states = new();
        TextBlock summary = Ui.Text(string.Empty, Palette.FontCaption, Palette.TextSecondaryBrush,
            FontWeight.Medium, wrap: false);
        Button refresh = Ui.GhostButton("Refresh", () => RefreshReadiness(states, summary));
        StackPanel headingControl = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Palette.SpaceSm,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { summary, refresh }
        };

        Control MakeStatus(string label)
        {
            TextBlock status = Ui.Text(string.Empty, Palette.FontCaption, Palette.TextSecondaryBrush,
                FontWeight.Medium, wrap: true);
            status.MaxWidth = 220;
            status.TextAlignment = TextAlignment.Right;
            states.Add(status);
            return Row(label, status, "A passive prerequisite check. It never starts the brain, microphone, voice engine or music.");
        }

        Control card = Card("Setup readiness", new[]
        {
            Row("Safe check", headingControl, "Re-reads device, file and configuration truth without exercising hardware."),
            null,
            MakeStatus("Brain files"),
            null,
            MakeStatus("Hearing"),
            null,
            MakeStatus("Voice & speakers"),
            null,
            MakeStatus("Music"),
            null,
            MakeStatus("Memory")
        });
        RefreshReadiness(states, summary);
        return card;
    }

    private void RefreshReadiness(IReadOnlyList<TextBlock> states, TextBlock summary)
    {
        CompanionOS.Core.Diagnostics.CompanionReadiness readiness = _runtime.GetReadiness();
        summary.Text = $"{readiness.ReadyCount}/{readiness.Items.Count} ready";
        for (int index = 0; index < states.Count && index < readiness.Items.Count; index++)
        {
            CompanionOS.Core.Diagnostics.ReadinessItem item = readiness.Items[index];
            states[index].Text = item.Detail;
            states[index].Foreground = item.IsReady ? Palette.SuccessBrush : Palette.WarningBrush;
            ToolTip.SetTip(states[index], item.IsReady ? "Prerequisite present" : "Needs attention before this capability can work");
        }
    }

    // -------------------------------------------------------------- startup

    private Control BuildStartupCard()
    {
        // The board's talk hotkey chips: Ctrl + M — which is real: Ctrl+M opens and
        // closes her ears while this window is focused, exactly as the composer's own
        // tooltip says.
        BoardSwitch autostart = new();
        bool registryTruth = AutoStart.IsEnabled();
        autostart.IsOn = registryTruth;

        if (registryTruth != _runtime.Settings.Current.System.StartWithWindows)
        {
            // The registry is truth; reconcile quietly instead of displaying a lie.
            _runtime.Settings.Current.System.StartWithWindows = registryTruth;
            _runtime.Settings.Save();
        }

        ToolTip.SetTip(autostart, "Runs her when you sign in, via the Windows Run key");
        autostart.Changed += enabled =>
        {
            if (enabled == AutoStart.IsEnabled())
            {
                return;
            }

            bool accepted = AutoStart.TrySet(enabled);
            if (!accepted)
            {
                _runtime.Events.PublishNotice("Windows refused to change her start-up entry.",
                    NoticeSeverity.Danger);
                autostart.IsOn = AutoStart.IsEnabled();
                return;
            }

            _runtime.Settings.Current.System.StartWithWindows = enabled;
            PublishChanged(enabled ? "She now starts when Windows does."
                : "Removed from Windows start-up.");
        };

        StackPanel chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(KeyChip("Ctrl"));
        chips.Children.Add(KeyChip("M"));

        BoardSwitch autoGaming = new() { IsOn = _runtime.Settings.Current.System.GamingAutoDetect };
        ToolTip.SetTip(autoGaming,
            "When a known game is the foreground window, Iris releases the brain automatically. Unknown games can be taught by saying it is a game.");
        autoGaming.Changed += enabled =>
        {
            if (enabled == _runtime.Settings.Current.System.GamingAutoDetect)
            {
                return;
            }

            _runtime.Settings.Current.System.GamingAutoDetect = enabled;
            PublishChanged(enabled
                ? "Iris will enter Gaming Mode when a known game comes to the front."
                : "Automatic Gaming Mode is off; use Sleep or ask Iris to enter Gaming Mode.");
        };

        BoardSwitch keepGamingEars = new() { IsOn = _runtime.Settings.Current.System.GamingEarsEnabled };
        ToolTip.SetTip(keepGamingEars,
            "Off is the performance default: Iris releases the microphone and Whisper memory while gaming. Turn it on only when voice access during games matters more than those resources.");
        keepGamingEars.Changed += enabled =>
        {
            if (enabled == _runtime.Settings.Current.System.GamingEarsEnabled)
            {
                return;
            }

            _runtime.Settings.Current.System.GamingEarsEnabled = enabled;
            PublishChanged(enabled
                ? "Iris may keep listening in Gaming Mode."
                : "Gaming Mode releases Iris' microphone and speech-recognition memory.");
        };

        return Card("Startup", new[]
        {
            Row("Talk hotkey", chips, "Ctrl+M opens and closes her ears while this window is focused."),
            null,
            Row("Start with Windows", autostart, "A real HKCU Run entry, written and re-read from the registry itself."),
            null,
            Row("Auto gaming", autoGaming, "Known foreground games make Iris release the brain automatically."),
            null,
            Row("Keep ears in games", keepGamingEars, "Off releases the mic and Whisper memory for the game.")
        });
    }

    private static Control KeyChip(string key)
    {
        TextBlock text = Ui.Text(key, Palette.FontBody, Palette.TextPrimaryBrush,
            FontWeight.Medium, wrap: false);
        text.LineHeight = 18;
        return new Border
        {
            Height = 28,
            MinWidth = 28,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            Background = Palette.SurfaceBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 0),
            Child = text
        };
    }

    // -------------------------------------------------------------- overlay

    private Control BuildOverlayCard()
    {
        List<(string Label, string Value)> positions = new()
        {
            ("Bottom right", "bottom right"),
            ("Top right", "top right"),
            ("Bottom left", "bottom left"),
            ("Top left", "top left")
        };

        BoardSelect position = new();
        position.SetItems(positions, _runtime.Settings.Current.Interface.OverlayPosition);
        ToolTip.SetTip(position, "Where her overlay pill first sits — it stays draggable.");
        position.Changed += value =>
        {
            if (string.Equals(value, _runtime.Settings.Current.Interface.OverlayPosition, StringComparison.Ordinal))
            {
                return;
            }

            _runtime.Settings.Current.Interface.OverlayPosition = value;
            // A deliberate corner choice replaces any pixel position left by a prior
            // drag. Without clearing the override this dropdown appears dead forever.
            _runtime.Settings.Current.Interface.OverlayPositionPx = string.Empty;
            PublishChanged($"She will sit in the {LabelOf(positions, value)} corner.");
        };

        BoardSwitch dimWhileGaming = new() { IsOn = _runtime.Settings.Current.Interface.DimWhileGaming };
        ToolTip.SetTip(dimWhileGaming, "While a game runs, she dims to 40 % so she is noticed, not loud");
        dimWhileGaming.Changed += enabled =>
        {
            if (enabled == _runtime.Settings.Current.Interface.DimWhileGaming)
            {
                return;
            }

            _runtime.Settings.Current.Interface.DimWhileGaming = enabled;
            PublishChanged(enabled ? "She dims her overlay while you game."
                : "She keeps her overlay at full strength while you game.");
        };

        BoardSwitch musicOverlay = new() { IsOn = _runtime.Settings.Current.Interface.ShowMusicOverlay };
        ToolTip.SetTip(musicOverlay,
            "Shows a small transport card beside Iris while music plays. Hiding it never stops playback.");
        musicOverlay.Changed += enabled =>
        {
            if (enabled == _runtime.Settings.Current.Interface.ShowMusicOverlay)
            {
                return;
            }

            _runtime.Settings.Current.Interface.ShowMusicOverlay = enabled;
            PublishChanged(enabled ? "Music controls appear beside Iris while something plays."
                : "Music controls are hidden; music keeps playing.");
        };

        BoardSwitch timerOverlay = new() { IsOn = _runtime.Settings.Current.Interface.ShowTimerOverlay };
        ToolTip.SetTip(timerOverlay,
            "Shows a small countdown card beside Iris while a timer runs. Hiding it never cancels the timer.");
        timerOverlay.Changed += enabled =>
        {
            if (enabled == _runtime.Settings.Current.Interface.ShowTimerOverlay)
            {
                return;
            }

            _runtime.Settings.Current.Interface.ShowTimerOverlay = enabled;
            PublishChanged(enabled ? "Timer controls appear while a countdown runs."
                : "Timer controls are hidden; timers keep running.");
        };

        return Card("Overlay", new[]
        {
            Row("Position", position, "Her desk corner. Dragging the pill afterwards always wins."),
            null,
            Row("Dim while gaming", dimWhileGaming, "40 % opacity during games, per the States board."),
            null,
            Row("Music controls", musicOverlay, "A contextual player appears only while music is active.")
            ,null,
            Row("Timer controls", timerOverlay, "A contextual countdown appears only while a timer is active.")
        });
    }

    // ---------------------------------------------------------------- brain

    private Control BuildBrainCard()
    {
        List<BrainProfile> profiles = _runtime.Settings.Current.Brain.Profiles;
        List<(string Label, string Value)> modelItems =
            profiles.Select(profile => (profile.DisplayName, profile.Id)).ToList();

        BoardSelect model = new();
        model.SetItems(modelItems, _runtime.Settings.Current.Brain.ActiveProfileId);
        ToolTip.SetTip(model, "Swaps her brain. She sleeps and wakes with the new model, live.");
        model.Changed += value =>
        {
            if (string.Equals(value, _runtime.Settings.Current.Brain.ActiveProfileId, StringComparison.Ordinal))
            {
                return;
            }

            _runtime.Settings.Current.Brain.ActiveProfileId = value;
            _runtime.Settings.Save();
            _runtime.Events.Publish(new SettingsChangedEvent());
            SwapBrainNow(LabelOf(modelItems, value));
        };

        List<(string Label, string Value)> contextItems = new()
        {
            ("Auto", "0"),
            ("8K", "8192"),
            ("12K", "12288"),
            ("16K", "16384"),
            ("32K", "32768"),
            ("64K", "65536")
        };

        BrainProfile active = _runtime.Settings.Current.ActiveBrainProfile;
        BoardSelect context = new();
        context.SetItems(contextItems, Math.Max(0, active.ContextTokens).ToString());
        ToolTip.SetTip(context,
            "Auto lets the model size its own context. A fixed window applies when her brain next starts.");
        context.Changed += value =>
        {
            int tokens = int.Parse(value, CultureInfo.InvariantCulture);
            if (tokens == _runtime.Settings.Current.ActiveBrainProfile.ContextTokens)
            {
                return;
            }

            _runtime.Settings.Current.ActiveBrainProfile.ContextTokens = tokens;
            _runtime.Settings.Save();
            _runtime.Events.Publish(new SettingsChangedEvent());
            SwapBrainNow(_runtime.Settings.Current.ActiveBrainProfile.DisplayName);
        };

        return Card("Brain", new[]
        {
            Row("Model", model, "She sleeps, reloads the chosen model and wakes herself — live."),
            null,
            Row("Context window", context, "Auto sizes it from the model; big windows cost VRAM (KV cache).")
        });
    }

    /// <summary>
    /// Applies a brain change live: she sleeps (unloading the old model), then wakes —
    /// and waking re-reads the active profile fresh. Outside of Awake it stays honest:
    /// the choice waits for her next wake instead of pretending it applied.
    /// </summary>
    private void SwapBrainNow(string name)
    {
        if (_model.Mode != OperatingMode.Awake)
        {
            _runtime.Events.PublishNotice(
                $"Brain becomes {name} the next time she wakes.", NoticeSeverity.Warning);
            return;
        }

        _ = SwapBrainAsync(name, CancellationToken.None);
    }

    private async Task SwapBrainAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            _runtime.Events.PublishNotice($"Swapping her brain to {name}…", NoticeSeverity.Warning);
            await _runtime.Brain.SetOperatingModeAsync(OperatingMode.Sleeping, cancellationToken);
            await _runtime.Brain.SetOperatingModeAsync(OperatingMode.Awake, cancellationToken);
            _runtime.Events.PublishNotice($"Her brain is now {name}.", NoticeSeverity.Success);
        }
        catch (Exception exception)
        {
            _runtime.Events.PublishNotice($"The brain swap failed: {exception.Message}",
                NoticeSeverity.Danger);
        }
    }

    // -------------------------------------------------------------- builders

    /// <summary>One board row: a 15/22 label on the left, the control on the right.</summary>
    private static Control Row(string label, Control control, string? tooltip = null)
    {
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            ToolTip.SetTip(control, tooltip);
        }

        TextBlock text = Ui.Text(label, Palette.FontEmphasis, Palette.TextPrimaryBrush, wrap: false);
        text.LineHeight = 22;

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        // SetColumn only writes attached properties — the children belong in the grid itself.
        grid.Children.Add(text);
        grid.Children.Add(control);

        Control row = new Border { Padding = new Thickness(0, 14), Child = grid };
        return row;
    }

    /// <summary>
    /// A card exactly as the board draws it: #1A1C24, one-pixel stroke, 16 px radius,
    /// 20/24 padding, a 20/26 title, rows breathing 14 px with a hairline between them.
    /// </summary>
    private static Control Card(string title, Control?[] rows)
    {
        TextBlock label = Ui.Text(title, Palette.FontTitle, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        label.LineHeight = 26;
        label.Margin = new Thickness(0, 0, 0, 8);

        List<Control> present = rows.Where(row => row is not null).Select(row => row!).ToList();

        StackPanel stack = new();
        stack.Children.Add(label);
        for (int index = 0; index < present.Count; index++)
        {
            // The hairline sits between rows, never after the last one — the null
            // slots in the row list are only there to say "separator goes around here".
            if (index > 0)
            {
                stack.Children.Add(new Border { Height = 1, Background = Palette.StrokeBrush });
            }

            stack.Children.Add(present[index]);
        }

        return new Border
        {
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(24, 20),
            Child = stack
        };
    }

    private static void Place(Grid grid, Control card, int row, int column)
    {
        // The gutter lives in the grid itself; rows breathe 16 px apart.
        card.Margin = row == 0 ? new Thickness(0, 0, 0, 16) : new Thickness(0);
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private static string LabelOf(IReadOnlyList<(string Label, string Value)> items, string value) =>
        items.FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.Ordinal)).Label ?? value;

    /// <summary>The one write-through path: persist, tell the machine, tell the human.</summary>
    private void PublishChanged(string message)
    {
        _runtime.Settings.Save();
        _runtime.Events.Publish(new SettingsChangedEvent());
        _runtime.Events.PublishNotice(message, NoticeSeverity.Success);
    }

    private void OpenConfigFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                CompanionPaths.ConfigDirectory)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            _runtime.Events.PublishNotice(
                $"Her config folder is at {CompanionPaths.ConfigDirectory}.", NoticeSeverity.Warning);
        }
    }
}
