using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.App.Interface.Views;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Events;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;
using IOPath = System.IO.Path;

namespace CompanionOS.App.Interface;

/// <summary>
/// The main window, transcribed 1:1 from the delivered design boards ("Iris — Desktop
/// Companion"): an 1180×760 rounded panel whose own 40 px title bar carries the orb, her
/// name, the state word, Sleep/Overlay and the window glyphs — the system chrome is not
/// drawn at all, the board's chrome is.
///
/// Layout, exactly as the boards:
///   title bar   orb 20, "Iris" 20, state word, [Sleep][Overlay] | [Minimize][Close]
///   rail        64 px: five 56 px icon buttons, then the diagnostics orb at the bottom
///   content     the active screen (Chat owns its 280 px right rail; the others are full)
///   status      32 px dotted uppercase strip, mic green when her ears are open
///
/// The window never does work itself. It reads the ShellModel, which reads events.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const double WindowSide = 1180;
    private const double WindowOther = 760;

    /// <summary>The window title keeps a single name when her persona is named Iris —
    /// "Iris — Iris" reads like a station that lost its signal.</summary>
    private static string PersonasOwnName(string personaName) =>
        string.Equals(personaName, "Iris", StringComparison.OrdinalIgnoreCase)
            ? "Iris"
            : $"Iris — {personaName}";

    private readonly CompanionRuntime _runtime;
    private readonly ShellModel _model;
    private readonly ContentControl _contentHost = new();
    private Border _titleOrb = null!;
    private Border _railOrb = null!;
    private Color _titleOrbColor = Palette.StateIdle;

    // The label set below repaints at 12.5 Hz while she is awake; without a damper the
    // strip rebuilt identical strings and brushes every tick for nothing
    // (2026-10-27 audit). These hold what is currently ON SCREEN; a value only
    // reaches its label when it genuinely moved.
    private string _renderedStateText = string.Empty;
    private Color? _renderedStateColor;
    private string _renderedModelLine = string.Empty;
    private string _renderedVram = string.Empty;
    private string _renderedContext = string.Empty;
    private string _renderedRam = string.Empty;
    private string _renderedMicText = string.Empty;
    private bool? _renderedMicOpen;
    private readonly TextBlock _stateLabel = new()
    {
        FontSize = Palette.FontCaption,
        FontWeight = FontWeight.Medium,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _modelLabel = new();
    private readonly TextBlock _vramLabel = new();
    private readonly Border _dot1 = new();
    private readonly TextBlock _contextLabel = new();
    private readonly Border _dot2 = new();
    private readonly TextBlock _ramLabel = new();
    private readonly Border _dot3 = new();
    private readonly TextBlock _microphoneLabel = new();
    private readonly ItemsControl _notices;
    private readonly Border _permissionCard = new();
    private PromptCardControl _promptCard = null!;
    private readonly DispatcherTimer _uiSyncTimer;
    private readonly Dictionary<string, Button> _railButtons = new(StringComparer.Ordinal);

    private ChatView _chatView = null!;
    private MemoryView _memoryView = null!;
    private ModulesView _modulesView = null!;
    private DiagnosticsView _diagnosticsView = null!;
    private PersonaView? _personaView;
    private OverlayWindow? _overlay;
    private bool _isShuttingDown;
    private bool _isAsleep;
    private readonly IDisposable _attentionSubscription;

    /// <summary>Provided by the application: puts her in the notification area.</summary>
    public Func<bool>? EnterSleepHost { get; set; }

    public MainWindow()
    {
        long constructorStarted = Stopwatch.GetTimestamp();
        SystemWatch.Trace("life", "main window construction begin");
        _runtime = CompanionRuntime.Create();
        _model = new ShellModel(_runtime);
        _attentionSubscription = _runtime.Events.Subscribe<AttentionRequestedEvent>(attention =>
            Dispatcher.UIThread.Post(() => ShowVoiceAttention(attention.HoldFor)));

        _notices = BuildNoticeList();
        // The toasts live in her model; without this the cards had a template and no
        // data, and real notices never appeared.
        _notices.ItemsSource = _model.Notices;

        // The board draws her whole chrome, including the window buttons, inside a
        // rounded 1180×760 panel — so the system decorations are extended away.
        // The title stays a single name when her persona is also named Iris, instead
        // of reading "Iris — Iris" on the dock chip.
        Title = PersonasOwnName(_model.PersonaName);

        // A rename on the Persona screen re-syncs the title instantly, no restart.
        _runtime.Events.Subscribe<CompanionOS.Core.Events.SettingsChangedEvent>(_ =>
            Dispatcher.UIThread.Post(() => Title = PersonasOwnName(_model.PersonaName)));
        // The board is drawn at 1180×760, but a desktop window must be able to breathe:
        // a fixed size at 125–150 % Windows scaling is taller than a 1080p work area and
        // cannot be resized back, which made the whole app unusable on a scaled display.
        CanResize = true;
        Width = WindowSide;
        Height = WindowOther;
        MinWidth = 980;
        MinHeight = 640;
        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        FontFamily = Palette.UiFont;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildLayout();
        BuildViews();

        _uiSyncTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background,
            (_, _) => SyncFromModel());
        _uiSyncTimer.Start();

        KeyDown += OnKeyDown;
        Closing += OnClosing;
        Opened += (_, _) => FitToWorkArea();

        // Sleep tears down the window and the runtime together, so it lives here.
        _model.DeepSleepRequested += () => _ = SleepAsync();

        ShowChat();

        SystemWatch.Trace("life", "main window constructed",
            $"cost={(Stopwatch.GetTimestamp() - constructorStarted) * 1_000.0 / Stopwatch.Frequency:0.#}ms");
    }

    /// <summary>Starts her body. Called by the application once the window exists.</summary>
    public async Task InitializeCompanionAsync()
    {
        SystemWatch.Trace("boot", "runtime start begin — brain, senses, voice come up from here");
        long bootStarted = Stopwatch.GetTimestamp();
        try
        {
            await _model.InitializeAsync();
        }
        catch (Exception exception)
        {
            _model.Messages.Add(new ChatEntry
            {
                Role = ChatRole.System,
                Text = $"Startup hit a problem: {exception.Message}"
            });
            SystemWatch.Fault("boot", exception, "async runtime start failed");
        }

        SystemWatch.Trace("boot",
            "runtime start finished — she is answerable from here",
            $"cost={(Stopwatch.GetTimestamp() - bootStarted) * 1_000.0 / Stopwatch.Frequency:0.#}ms");

        if (_runtime.Settings.Current.Interface.ShowOverlay)
        {
            SystemWatch.Trace("boot", "auto-showing the overlay pill (Interface.ShowOverlay is on)");
            Dispatcher.UIThread.Post(ShowOverlay);
        }

        if (!_runtime.Settings.Current.Voice.ChosenByUser)
        {
            Dispatcher.UIThread.Post(ShowPersona);
        }

        OfferBrowserExtensionSetup();
        OfferTelegramSetup();
    }

    private void OfferTelegramSetup()
    {
        bool telegramInstalled = _runtime.Plugins.Loaded.Any(plugin =>
            plugin.Id.Equals("telegram-personal", StringComparison.OrdinalIgnoreCase));
        if (!telegramInstalled)
            return;

        string telegramDirectory = IOPath.Combine(
            CompanionOS.Core.Configuration.CompanionPaths.DataRoot, "telegram");
        string sessionFile = IOPath.Combine(telegramDirectory, "session.dpapi");
        string previouslyConnected = IOPath.Combine(telegramDirectory, "connected-before");
        if (File.Exists(sessionFile))
        {
            try
            {
                File.WriteAllText(previouslyConnected, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception exception)
            {
                _runtime.Events.PublishNotice(
                    $"Could not record Telegram connection status: {exception.Message}",
                    NoticeSeverity.Warning);
            }

            return;
        }

        if (!File.Exists(previouslyConnected))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            TelegramSetupWindow setup = new(ShowBody, () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("http://127.0.0.1:47832/")
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception exception)
                {
                    _runtime.Events.PublishNotice(
                        $"Telegram setup page is not available yet: {exception.Message}. Open Modules and set up Telegram first.",
                        NoticeSeverity.Warning);
                }
            });
            setup.Show(this);
        });
    }

    private void OfferBrowserExtensionSetup()
    {
        CompanionOS.Core.Plugins.PluginManifest? browser = _runtime.Plugins.Loaded.FirstOrDefault(plugin =>
            plugin.Id.Equals("browser", StringComparison.OrdinalIgnoreCase));
        if (browser is null)
        {
            return;
        }

        string noticeMarker = IOPath.Combine(
            CompanionOS.Core.Configuration.CompanionPaths.ConfigDirectory,
            "browser-extension-setup-notice-shown");
        if (File.Exists(noticeMarker))
        {
            return;
        }

        string message = browser.Tools.Count > 0
            ? "Browser is ready. Set up its Chrome extension; Iris will copy the files and open Chrome. You will load the folder and pair it once."
            : "Browser files are installed, but Iris could not start its tools. Check that Node.js is on PATH, then refresh Modules.";
        string installer = IOPath.Combine(browser.Folder, "install-extension.ps1");

        _runtime.Events.PublishNotice(message,
            browser.Tools.Count > 0 ? NoticeSeverity.Info : NoticeSeverity.Warning,
            browser.Tools.Count > 0 && File.Exists(installer) ? "Set up Chrome" : null,
            browser.Tools.Count > 0 && File.Exists(installer)
                ? () => StartBrowserExtensionSetup(installer, noticeMarker)
                : null);
    }

    private static void StartBrowserExtensionSetup(string installer, string noticeMarker)
    {
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = IOPath.GetDirectoryName(installer) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(installer);
        Process.Start(start);

        // Suppress the setup prompt on later launches only after the user chose it.
        // If they dismiss the toast, it will be offered again the next time Iris starts.
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(noticeMarker)!);
            File.WriteAllText(noticeMarker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (IOException exception)
        {
            SystemWatch.Fault("browser.setup", exception, "could not save setup-notice state");
        }
        catch (UnauthorizedAccessException exception)
        {
            SystemWatch.Fault("browser.setup", exception, "could not save setup-notice state");
        }
    }

    private Control BuildLayout()
    {
        // The board: one 1 px stroke, 12 px corners, near-black panel, overflow hidden.
        Border panel = new()
        {
            Background = Palette.BackgroundBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = BuildRoot()
        };

        Grid layered = new();
        layered.Children.Add(panel);
        layered.Children.Add(_notices);
        return layered;
    }

    private Grid BuildRoot()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions($"40,*,{Palette.StatusStripHeight}")
        };

        Control header = BuildHeader();
        Control body = BuildBody();
        Control statusStrip = BuildStatusStrip();

        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(statusStrip, 2);

        root.Children.Add(header);
        root.Children.Add(body);
        root.Children.Add(statusStrip);

        // Notices belong under the chrome, top-centre, per the States board — not in the
        // bottom-right corner where they covered the composer and the status strip.
        _notices.HorizontalAlignment = HorizontalAlignment.Center;
        _notices.VerticalAlignment = VerticalAlignment.Top;
        _notices.Margin = new Thickness(0, 48, 0, 0);
        _notices.MaxWidth = 420;
        _notices.ZIndex = 50;

        return root;
    }

    /// <summary>Notice cards, straight from real events — no notice is sampled away.</summary>
    private ItemsControl BuildNoticeList()
    {
        return new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<NoticeItem>((item, _) =>
            {
                if (item is null)
                {
                    return null;
                }

                // Board tokens: 16 px icon tinted by the news, 15/22 text, 12×16 padding,
                // a soft black lift, and Details on the ones worth looking into.
                Color tint = item.Severity switch
                {
                    NoticeSeverity.Warning => Palette.Warning,
                    NoticeSeverity.Danger => Palette.Danger,
                    _ => Palette.Success
                };
                string glyph = item.Severity is NoticeSeverity.Warning or NoticeSeverity.Danger
                    ? IconGlyph.AlertCircle
                    : IconGlyph.CheckCircle;

                TextBlock message = new()
                {
                    Text = item.Message,
                    FontFamily = Palette.UiFont,
                    FontSize = Palette.FontNotice,
                    LineHeight = 22,
                    Foreground = Palette.TextPrimaryBrush,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // The row is a grid so the message column is bounded and wraps the way
                // the board typesets it; a horizontal StackPanel would hand the text an
                // infinite width and let one sentence paint past the card edge.
                Grid row = new()
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto")
                };

                Control icon = IconGlyph.Make(glyph, 16, new SolidColorBrush(tint));
                icon.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(icon, 0);
                row.Children.Add(icon);

                message.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(message, 1);
                row.Children.Add(message);

                if (!string.IsNullOrWhiteSpace(item.ActionLabel) && item.Action is not null)
                {
                    Button action = new()
                    {
                        BorderThickness = new Thickness(0),
                        Background = Brushes.Transparent,
                        Padding = new Thickness(0),
                        Margin = new Thickness(12, 0, 0, 0),
                        FontFamily = Palette.UiFont,
                        FontSize = Palette.FontBody,
                        FontWeight = FontWeight.Medium,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Palette.AccentText),
                        Content = item.ActionLabel,
                        Cursor = new Cursor(StandardCursorType.Hand)
                    };
                    action.Click += (_, _) =>
                    {
                        try
                        {
                            item.Action.Invoke();
                        }
                        catch (Exception exception)
                        {
                            _runtime.Events.PublishNotice(
                                $"Could not open the browser setup: {exception.Message}", NoticeSeverity.Danger);
                        }
                        finally
                        {
                            _model.Notices.Remove(item);
                        }
                    };
                    Grid.SetColumn(action, 2);
                    row.Children.Add(action);
                }

                if (item.Severity == NoticeSeverity.Danger)
                {
                    Button details = new()
                    {
                        BorderThickness = new Thickness(0),
                        Background = Brushes.Transparent,
                        Padding = new Thickness(0),
                        Margin = new Thickness(12, 0, 0, 0),
                        FontFamily = Palette.UiFont,
                        FontSize = Palette.FontBody,
                        FontWeight = FontWeight.Medium,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Palette.AccentText),
                        Content = "Details"
                    };
                    details.Click += (_, _) =>
                    {
                        _model.Notices.Remove(item);
                        ShowDiagnostics();
                    };
                    Grid.SetColumn(details, 3);
                    row.Children.Add(details);
                }

                // A persistent error toast needs a way out. The board's Details is the
                // path to the truth; this quiet cross simply dismisses the card.
                Button dismiss = new()
                {
                    Content = IconGlyph.Make(IconGlyph.Close, 14, Palette.TextFaintBrush),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(8, 0, 0, 0),
                    CornerRadius = new CornerRadius(6),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Focusable = false,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                ToolTip.SetTip(dismiss, "Dismiss");
                dismiss.Click += (_, _) => _model.Notices.Remove(item);
                Grid.SetColumn(dismiss, 4);
                row.Children.Add(dismiss);

                return new Border
                {
                    Width = 420,
                    Margin = new Thickness(0, 0, 0, 12),
                    BoxShadow = new BoxShadows(new BoxShadow
                    {
                        Color = Color.FromArgb(89, 0, 0, 0),
                        Blur = 24,
                        Spread = 0,
                        OffsetX = 0,
                        OffsetY = 8
                    }),
                    Background = Palette.ElevatedBrush,
                    BorderBrush = Palette.StrokeBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(Palette.RadiusCard),
                    Padding = new Thickness(16, 12),
                    Child = row
                };
            })
        };
    }

    /// <summary>
    /// The board's title bar: orb 20, name, state word, then Sleep and Overlay as quiet
    /// icon buttons, a 1 px divider, and the real window glyphs. The empty middle drag the
    /// window like a native caption.
    /// </summary>
    private Control BuildHeader()
    {
        // Her state orb: 20 px, state-coloured, exactly the board's radial spec.
        _titleOrb = IconGlyph.Orb(20, Palette.StateIdle, Palette.Iris, 0.35);
        _stateLabel.Foreground = Palette.SuccessBrush;

        _modelLabel.FontSize = Palette.FontCaption;
        _modelLabel.FontWeight = FontWeight.Medium;
        _modelLabel.Foreground = Palette.TextPrimaryBrush;
        _modelLabel.LetterSpacing = 0.88;
        foreach (Border dot in (Border[])[_dot1, _dot2, _dot3])
        {
            dot.Width = 2;
            dot.Height = 2;
            dot.Background = Palette.TextFaintBrush;
            dot.CornerRadius = new CornerRadius(1);
            dot.VerticalAlignment = VerticalAlignment.Center;
        }
        foreach (TextBlock caption in (TextBlock[])[_vramLabel, _contextLabel, _ramLabel, _microphoneLabel])
        {
            caption.FontSize = Palette.FontCaption;
            caption.FontWeight = FontWeight.Medium;
            caption.Foreground = Palette.TextSecondaryBrush;
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.LetterSpacing = 0.88;
        }

        // Left cluster: orb, name, state — gap 10, from the board.
        StackPanel identity = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        identity.Margin = new Thickness(8, 0, 0, 0);
        identity.Children.Add(_titleOrb);

        TextBlock name = Ui.Text("Iris", Palette.FontTitle, Palette.TextPrimaryBrush, FontWeight.SemiBold, wrap: false);
        name.LineHeight = 26;
        identity.Children.Add(name);
        identity.Children.Add(_stateLabel);

        // Right cluster: Sleep, Game, Overlay, divider, minimize, close — all real.
        StackPanel actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        actions.Children.Add(TitleButton(IconGlyph.Sleep, "Sleep", _model.RequestSleep));
        actions.Children.Add(TitleButton(IconGlyph.Body, "Game", () => _ = _model.ToggleGamingAsync()));
        actions.Children.Add(TitleButton(IconGlyph.OverlayFrame, "Overlay", ToggleOverlay));

        Border divider = new() { Width = 1, Height = 16, Background = Palette.StrokeBrush, Margin = new Thickness(4, 0) };
        actions.Children.Add(divider);
        actions.Children.Add(WindowGlyph(IconGlyph.Minimize, "Minimize", () => WindowState = WindowState.Minimized));
        actions.Children.Add(WindowGlyph(IconGlyph.Close, "Close", () => Close()));

        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Background = Palette.BackgroundBrush
        };
        Grid.SetColumn(identity, 0);
        Grid.SetColumn(actions, 2);
        header.Children.Add(identity);
        header.Children.Add(actions);

        // The whole bar drags the window; buttons still receive their clicks because the
        // drag only starts when the press is on the bar itself, not a control.
        Border bar = new()
        {
            Background = Palette.BackgroundBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header
        };

        // The extended-client strip above the board padding is also draggable caption.
        // The whole bar drags, except anything inside a button. Checking only
        // `Source is not Button` let a press on a glyph or label inside Sleep/Overlay start
        // a window drag instead of a click — the button appeared dead at random.
        bar.PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
                !IsInsideButton(eventArgs.Source))
            {
                BeginMoveDrag(eventArgs);
            }
        };
        bar.Cursor = new Cursor(StandardCursorType.SizeAll);

        return bar;
    }

    private Button TitleButton(string geometry, string label, Action onClick)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(IconGlyph.Make(geometry, 16, Palette.TextSecondaryBrush));
        TextBlock text = Ui.Text(label, Palette.FontBody, Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
        text.LineHeight = 18;
        content.Children.Add(text);

        Button button = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 0),
            MinWidth = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button WindowGlyph(string geometry, string label, Action onClick)
    {
        Button button = new()
        {
            Content = IconGlyph.Make(geometry, 20, Palette.TextSecondaryBrush),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MinWidth = 32,
            MinHeight = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Control BuildBody()
    {
        Grid body = new()
        {
            ColumnDefinitions = new ColumnDefinitions($"{Palette.RailWidth},*")
        };

        Control rail = BuildRail();
        Grid.SetColumn(rail, 0);
        Control contentHostBorder = new Border
        {
            Child = _contentHost,
            ClipToBounds = true
        };
        Grid.SetColumn(contentHostBorder, 1);

        body.Children.Add(rail);
        body.Children.Add(contentHostBorder);

        // The permission card sits on top of the content area, dimming it.
        _permissionCard.IsVisible = false;
        _permissionCard.Background = new SolidColorBrush(Palette.Background, 0.82);
        _permissionCard.Child = BuildPermissionCardContent();
        _permissionCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        _permissionCard.VerticalAlignment = VerticalAlignment.Stretch;

        Grid layered = new();
        layered.Children.Add(body);
        layered.Children.Add(_permissionCard);
        return layered;
    }

    /// <summary>
    /// The rail, from the board: 64 px wide, items padded 12/4 with a 4 px gap, each item
    /// a 56×56 rounded tile with a 20 px icon over an 11 px label. Active tiles take the
    /// accent tint with accent-soft text; the diagnostics orb sits at the bottom.
    /// </summary>
    private Control BuildRail()
    {
        StackPanel items = new() { Spacing = 4 };

        RailItem(items, "Chat", IconGlyph.Chat, ShowChat);
        RailItem(items, "Memory", IconGlyph.Memory, ShowMemory);
        RailItem(items, "Body", IconGlyph.Body, ShowBody);
        RailItem(items, "Persona", IconGlyph.Persona, ShowPersona);
        RailItem(items, "Settings", IconGlyph.Settings, ShowSettings);

        // Diagnostics orb, board spec: 24 px orb with its glow. It wears her state colour
        // like the title orb — violet at rest; the keep-up recolour only fires on change,
        // so the starting colour must already be the resting one.
        _railOrb = IconGlyph.Orb(24, Color.Parse("#B09DFF"), Palette.Iris, 0.35);
        Button diagnostics = new()
        {
            Content = _railOrb,
            Background = Brushes.Transparent,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(diagnostics, "Diagnostics — every action, step and failure");
        diagnostics.Click += (_, _) => ShowDiagnostics();

        Grid railColumn = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        items.Margin = new Thickness(4, 12);
        Grid.SetRow(items, 0);
        Grid.SetRow(diagnostics, 1);
        diagnostics.Margin = new Thickness(0, 0, 0, 8);
        railColumn.Children.Add(items);
        railColumn.Children.Add(diagnostics);

        return new Border
        {
            Background = Palette.BackgroundBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = railColumn
        };
    }

    private void RailItem(StackPanel host, string label, string geometry, Action onClick)
    {
        StackPanel content = new() { Spacing = 4 };
        content.Children.Add(IconGlyph.Make(geometry, 20, Palette.TextSecondaryBrush));
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = Palette.FontCaption,
            FontWeight = FontWeight.Medium,
            LineHeight = 16,
            Foreground = Palette.TextSecondaryBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        Button button = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        ToolTip.SetTip(button, label);
        // The Show* target owns the rail marking: one click, one switch, one nav line.
        button.Click += (_, _) => onClick();
        _railButtons[label] = button;

        host.Children.Add(button);
        if (label == "Chat")
        {
            SetActiveRail("Chat");
        }
    }

    private string GetRailFamilyLabelFor(Button button)
    {
        foreach (KeyValuePair<string, Button> pair in _railButtons)
        {
            if (ReferenceEquals(pair.Value, button))
            {
                return pair.Key;
            }
        }

        return string.Empty;
    }

    private void MarkRailActive(string label, bool active)
    {
        Button? button = label.Length > 0 && _railButtons.TryGetValue(label, out Button? found) ? found : null;
        if (button is null)
        {
            return;
        }

        if (button.Content is not StackPanel content)
        {
            return;
        }

        // Board tokens: active tile takes accent tint; text/icon turn accent-soft.
        button.Background = active ? Palette.RailActiveBrush : Brushes.Transparent;
        foreach (Control child in content.Children)
        {
            if (child is Avalonia.Controls.Shapes.Path icon)
            {
                icon.Stroke = active ? Palette.AccentSoftBrush : Palette.TextSecondaryBrush;
            }
            else if (child is TextBlock text)
            {
                text.Foreground = active ? Palette.AccentSoftBrush : Palette.TextSecondaryBrush;
            }
        }
    }

    private void SetActiveRail(string label)
    {
        // Every view change goes into the black box: when a user reports "the navbar
        // does nothing" the log either shows the tap never arriving (input problem)
        // or arriving and bouncing (state problem) — instead of a shrug.
        SystemWatch.Trace("nav", $"view → {(string.IsNullOrEmpty(label) ? "<no tile>" : label)}");
        foreach (string key in _railButtons.Keys)
        {
            MarkRailActive(key, key == label);
        }
    }

    /// <summary>
    /// The board's status strip: 32 px, dotted uppercase captions, mic in green when her
    /// ears are open. Endpoint privacy is configuration-dependent, so the strip makes no
    /// blanket local-only claim.
    /// </summary>
    private Control BuildStatusStrip()
    {
        StackPanel left = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };

        left.Children.Add(_modelLabel);
        left.Children.Add(_dot1);
        left.Children.Add(_vramLabel);
        left.Children.Add(_dot2);
        left.Children.Add(_contextLabel);
        left.Children.Add(_dot3);
        left.Children.Add(_ramLabel);
        left.Children.Add(_microphoneLabel);

        left.Margin = new Thickness(10, 0, 0, 0);

        return new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = left
        };
    }

    private Control BuildPermissionCardContent()
    {
        // The same card the desktop overlay uses: approvals, choices and numbered lists.
        _promptCard = new PromptCardControl(
            (promptId, optionId) => _model.AnswerPrompt(promptId, optionId),
            _model.DismissPrompt)
        {
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return new Grid { Children = { _promptCard } };
    }

    private static Control CardCentered(Control content)
    {
        Border card = PaletteSurfaceCard(content);
        return new Grid
        {
            Children =
            {
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { card }
                }
            }
        };
    }

    private static Border PaletteSurfaceCard(Control content) => new()
    {
        Background = Palette.ElevatedBrush,
        BorderBrush = Palette.StrokeBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Palette.RadiusPanel),
        Padding = new Thickness(Palette.SpaceXl),
        Width = 460,
        Child = content
    };

    private void BuildViews()
    {
        _chatView = new ChatView(_model);
        _memoryView = new MemoryView(_runtime);
        _model.MemoryNodeOpenRequested += OpenRecallMemory;
        _modulesView = new ModulesView(_runtime);
        _diagnosticsView = new DiagnosticsView(_runtime.Perception, _runtime.Events);
    }

    private void OpenRecallMemory(string nodeId)
    {
        if (_memoryView.SelectNode(nodeId))
        {
            ShowMemory();
        }
    }

    private void ShowChat()
    {
        _contentHost.Content = _chatView;
        SetActiveRail("Chat");
    }

    private void ShowMemory()
    {
        _contentHost.Content = _memoryView;
        SetActiveRail("Memory");
    }

    private void ShowBody()
    {
        _modulesView.Refresh();
        _contentHost.Content = _modulesView;
        SetActiveRail("Body");
    }

    /// <summary>Persona is a real screen — her name, her voice — not a voice browser.</summary>
    private void ShowPersona()
    {
        _personaView ??= new PersonaView(_runtime);
        _contentHost.Content = _personaView;
        SetActiveRail("Persona");
    }

    /// <summary>
    /// Settings are rebuilt on every visit, and every control writes through the moment
    /// you touch it — the board has no Save button, so this screen must never hold power
    /// over a later edit made elsewhere, like the voice chosen on the Persona screen.
    /// </summary>
    private void ShowSettings()
    {
        _contentHost.Content = new SettingsView(_runtime, _model);
        SetActiveRail("Settings");
    }

    private void ShowDiagnostics()
    {
        _diagnosticsView.RefreshSensorHealth();
        _contentHost.Content = _diagnosticsView;
        // The orb is not one of the tiles: while her truth screen is up nothing on the
        // rail pretends to be the active screen, so every tile goes quiet.
        SetActiveRail(string.Empty);
    }

    private void ToggleOverlay()
    {
        SystemWatch.Trace("ui", "overlay toggle", $"currently_visible={_overlay is { IsVisible: true }}");
        if (_overlay is { IsVisible: true })
        {
            _overlay.Hide();
            return;
        }

        ShowOverlay();
    }

    private void ShowOverlay()
    {
        _overlay ??= new OverlayWindow(_model, ShowFromOverlay, QuitAsync);
        // Never activate the pill: doing so stole keyboard focus from whatever game or
        // editor the user was in the middle of, every time the overlay appeared.
        if (!_overlay.IsVisible)
        {
            _overlay.Show();
        }
    }

    private void ShowVoiceAttention(TimeSpan holdFor)
    {
        ShowOverlay();
        _overlay?.ShowAttention(holdFor);
    }

    private void ShowFromOverlay()
    {
        Show();
        Activate();
    }

    /// <summary>
    /// Fits the starting window inside the real work area. The design's 1180×760 is the
    /// preference, but a scaled display is allowed to make that impossible; in that case
    /// the window starts at its minimum instead of hanging off the screen.
    /// </summary>
    private void FitToWorkArea()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        double scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        double availableWidth = (screen.WorkingArea.Width / scaling) - 24;
        double availableHeight = (screen.WorkingArea.Height / scaling) - 24;

        Width = Math.Min(Width, Math.Max(MinWidth, availableWidth));
        Height = Math.Min(Height, Math.Max(MinHeight, availableHeight));
        Position = new PixelPoint(
            screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - (int)(Width * scaling)) / 2),
            screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - (int)(Height * scaling)) / 2));
    }

    /// <summary>Walks the event source up the visual tree to the owning button, if any.</summary>
    private static bool IsInsideButton(object? source)
    {
        Visual? visual = source as Visual;
        while (visual is not null)
        {
            if (visual is Button)
            {
                return true;
            }

            visual = visual.GetVisualParent();
        }

        return false;
    }
}
