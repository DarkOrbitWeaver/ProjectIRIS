using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Events;
using CompanionOS.Core.Perception;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// The truth screen: everything she does, as it happens.
///
/// Two diaries, both live. <see cref="CompanionLog"/> is the readable story;
/// <see cref="IrisEvent"/> is the machine-readable record with areas, event names and
/// durations. Nothing here is sampled or prettified — this is exactly what she wrote,
/// newest first, the same lines that go to disk for replaying her day.
///
/// One rule learned the hard way: entries are pure data and every row's TextBlocks are
/// built fresh inside the data template. A row object must never carry its own visuals,
/// because when the list re-emits (a filter pass, a pause resume, an insert that moves
/// a container) Avalonia re-runs the template, and a template that hands back a control
/// already owned by another parent kills the process — she used to crash the moment
/// the rail orb was tapped. Data in, fresh visuals out, forever.
/// </summary>
public sealed class DiagnosticsView : UserControl
{
    private const int MaximumRows = 300;

    private readonly TextBox _filter = Ui.Input("Filter by area, event or text");
    private readonly ToggleButton _pauseButton;
    private readonly PerceptionEngine _perception;
    private readonly StackPanel _sensingRows = new() { Spacing = Palette.SpaceSm };
    private readonly TextBlock _sensingUpdated = Ui.Text("", Palette.FontCaption, Palette.TextFaintBrush);
    private readonly ItemsControl _eventRows = new();
    private readonly ItemsControl _diaryRows = new();
    private readonly ItemsControl _systemRows = new();
    private bool _isPaused;
    private string _filterText = string.Empty;

    // Truth, newest first. Plain data only; the list sorts itself out when it rebinds.
    private readonly List<DiagnosticsEntry> _eventEntries = new();
    private readonly List<DiagnosticsEntry> _diaryEntries = new();
    private readonly List<DiagnosticsEntry> _systemEntries = new();

    public DiagnosticsView(PerceptionEngine perception, EventBus events)
    {
        _perception = perception;
        IrisEvent.EventLogged += OnEventLogged;
        CompanionLog.EntryWritten += OnEntryWritten;
        SystemWatch.LineWritten += OnSystemLineWritten;
        _perception.SituationUpdated += OnSituationUpdated;
        events.Subscribe<SettingsChangedEvent>(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible) RefreshSensorHealth();
            });
        });

        _filter.TextChanged += (_, _) =>
        {
            _filterText = (_filter.Text ?? string.Empty).Trim();
            Rebind();
        };

        _pauseButton = new ToggleButton
        {
            Content = Ui.Text("Pause", Palette.FontBody, Palette.TextSecondaryBrush),
            Padding = new Thickness(Palette.SpaceLg, Palette.SpaceSm),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1)
        };
        _pauseButton.IsCheckedChanged += (_, _) => _isPaused = _pauseButton.IsChecked == true;

        Button openFile = Ui.GhostButton("Open logs folder", OpenOnDisk);

        StackPanel header = new() { Spacing = Palette.SpaceSm };

        // Page header like every other board: 28 px title, letterspaced caption.
        TextBlock title = Ui.Text("Diagnostics", Palette.FontPage, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        title.LineHeight = 34;

        TextBlock caption = Ui.Text("BEHAVIOR · APP DIARY · WINDOWS WATCH",
            Palette.FontCaption, Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
        caption.LineHeight = 16;
        caption.LetterSpacing = 0.88;

        StackPanel titleBlock = new() { Spacing = 4 };
        titleBlock.Children.Add(title);
        titleBlock.Children.Add(caption);

        Grid bar = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(_filter, 0);
        Grid.SetColumn(_pauseButton, 1);
        _pauseButton.Margin = new Thickness(Palette.SpaceSm, 0, 0, 0);
        Grid.SetColumn(openFile, 2);
        openFile.Margin = new Thickness(Palette.SpaceSm, 0, 0, 0);
        bar.Children.Add(_filter);
        bar.Children.Add(_pauseButton);
        bar.Children.Add(openFile);
        header.Children.Add(titleBlock);
        header.Children.Add(bar);
        header.Children.Add(Ui.Caption(
            "Live diagnostics for Iris behavior, readable app logs and Windows responsiveness. " +
            "Detailed files are stored in her local log folder."));

        FuncDataTemplate<DiagnosticsEntry> rowTemplate = new((entry, _) =>
            entry is null ? null : RowControl(entry));
        _eventRows.ItemTemplate = rowTemplate;
        _diaryRows.ItemTemplate = rowTemplate;
        _systemRows.ItemTemplate = rowTemplate;

        TabItem sensingTab = new() { Header = "Sensing", Content = BuildSensingPanel() };
        TabItem eventsTab = new() { Header = "Behavior", Content = WrapList(_eventRows) };
        TabItem logTab = new() { Header = "App diary", Content = WrapList(_diaryRows) };
        TabItem systemTab = new() { Header = "OS watch", Content = WrapList(_systemRows) };
        TabControl tabs = new() { Items = { sensingTab, eventsTab, logTab, systemTab } };

        StackPanel stack = new() { Spacing = Palette.SpaceMd, Margin = new Thickness(Palette.SpaceLg) };
        stack.Children.Add(header);
        stack.Children.Add(tabs);

        Content = new Border
        {
            CornerRadius = new CornerRadius(Palette.RadiusCard),
            Background = Palette.BackgroundBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            Child = stack
        };

        foreach (EventRecord record in IrisEvent.Recent(MaximumRows).Reverse()) _eventEntries.Add(DiagnosticsEntry.ForEvent(record));
        foreach (LogEntry entry in CompanionLog.Recent(MaximumRows).Reverse()) _diaryEntries.Add(DiagnosticsEntry.ForEntry(entry));
        foreach (string line in SystemWatch.Recent(MaximumRows).Reverse()) _systemEntries.Add(DiagnosticsEntry.ForSystemLine(line));
        RefreshSensorHealth();
        Rebind();
    }

    private Control BuildSensingPanel()
    {
        StackPanel content = new() { Spacing = Palette.SpaceMd, Margin = new Thickness(Palette.SpaceLg) };
        content.Children.Add(Ui.Text("Current sensing status", Palette.FontSection, Palette.TextPrimaryBrush,
            FontWeight.SemiBold));
        content.Children.Add(Ui.Caption(
            "Shows what is enabled and what each source last returned. Screen pixels are only available after a requested capture succeeds."));
        content.Children.Add(_sensingUpdated);
        content.Children.Add(_sensingRows);
        return new Border
        {
            Background = Palette.BackgroundBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            Padding = new Thickness(Palette.SpaceMd),
            Child = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
    }

    private void OnSituationUpdated(SituationSnapshot _)
    {
        // Perception publishes from its background timer. Avalonia controls, including
        // IsVisible, must only be touched after hopping to the UI dispatcher.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) RefreshSensorHealth();
        });
    }

    /// <summary>Refresh the panel when opened; background sensor ticks do no UI work while hidden.</summary>
    public void RefreshSensorHealth()
    {
        _sensingRows.Children.Clear();
        foreach (PerceptionSourceHealth source in _perception.GetSourceHealth())
        {
            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitions("150,110,*"),
                MinHeight = 34
            };
            TextBlock name = Ui.Text(source.Name, Palette.FontBody, Palette.TextPrimaryBrush, FontWeight.Medium);
            TextBlock status = Ui.Text(source.Status, Palette.FontCaption, StatusBrush(source.Status), FontWeight.SemiBold);
            status.Margin = new Thickness(0, 0, Palette.SpaceMd, 0);
            TextBlock detail = Ui.Text(source.Detail, Palette.FontCaption, Palette.TextSecondaryBrush);
            detail.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            Grid.SetColumn(status, 1);
            Grid.SetColumn(detail, 2);
            row.Children.Add(name);
            row.Children.Add(status);
            row.Children.Add(detail);
            _sensingRows.Children.Add(new Border
            {
                Background = Palette.SurfaceBrush,
                BorderBrush = Palette.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Palette.RadiusControl),
                Padding = new Thickness(Palette.SpaceMd, Palette.SpaceSm),
                Child = row
            });
        }

        _sensingUpdated.Text = $"Updated {DateTimeOffset.Now:HH:mm:ss}. Sensor checks use the existing low-rate sampling cadence.";
    }

    private static IBrush StatusBrush(string status) => status switch
    {
        "Available" => Palette.SuccessBrush,
        "Unavailable" or "Stale" => Palette.WarningBrush,
        "Disabled" => Palette.TextFaintBrush,
        _ => Palette.TextSecondaryBrush
    };

    /// <summary>Fresh Grid and fresh TextBlocks for every row, every time. Never shared.</summary>
    private static Control RowControl(DiagnosticsEntry item) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("92,88,220,*,90"),
        Margin = new Thickness(0, 1, 0, 1),
        Children =
        {
            MakeCell(item.Time, item.TextBrush, FontWeight.Normal, 0),
            MakeCell(item.Area, Palette.TextSecondaryBrush, FontWeight.Medium, 1),
            MakeCell(item.Name, Palette.TextPrimaryBrush, FontWeight.Normal, 2),
            MakeCell(item.Detail, Palette.TextSecondaryBrush, FontWeight.Normal, 3),
            MakeCell(item.Duration, Palette.TextFaintBrush, FontWeight.Normal, 4)
        }
    };

    private static TextBlock MakeCell(string text, IBrush brush, FontWeight weight, int column)
    {
        TextBlock block = new()
        {
            Text = text,
            FontSize = Palette.FontCaption,
            FontWeight = weight,
            Foreground = brush,
            FontFamily = Palette.UiFont,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, Palette.SpaceMd, 0)
        };
        Grid.SetColumn(block, column);
        return block;
    }

    private Control WrapList(ItemsControl rows)
    {
        ScrollViewer scroll = new()
        {
            Content = rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        return new Border
        {
            Background = Palette.BackgroundBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusControl),
            Padding = new Thickness(Palette.SpaceMd, 0),
            MinHeight = 520,
            Child = scroll
        };
    }

    private void OnEventLogged(EventRecord record)
    {
        if (_isPaused)
        {
            return;
        }

        Add(_eventEntries, _eventRows, DiagnosticsEntry.ForEvent(record));
    }

    private void OnEntryWritten(LogEntry entry)
    {
        if (_isPaused)
        {
            return;
        }

        Add(_diaryEntries, _diaryRows, DiagnosticsEntry.ForEntry(entry));
    }

    private void OnSystemLineWritten(string line)
    {
        if (!_isPaused) Add(_systemEntries, _systemRows, DiagnosticsEntry.ForSystemLine(line));
    }

    private void Add(List<DiagnosticsEntry> entries, ItemsControl rows, DiagnosticsEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            entries.Insert(0, entry);
            while (entries.Count > MaximumRows)
            {
                entries.RemoveAt(entries.Count - 1);
            }

            Rebind(rows, entries);
        });
    }

    /// <summary>Feeds the list the slice that matches the filter, newest first.</summary>
    private void Rebind()
    {
        Rebind(_eventRows, _eventEntries);
        Rebind(_diaryRows, _diaryEntries);
        Rebind(_systemRows, _systemEntries);
    }

    private void Rebind(ItemsControl rows, List<DiagnosticsEntry> entries) =>
        rows.ItemsSource = string.IsNullOrEmpty(_filterText)
            ? entries.ToArray()
            : entries.Where(entry => entry.Matches(_filterText)).ToArray();

    private static void OpenOnDisk()
    {
        try
        {
            string path = IrisEvent.FilePath;
            string? directory = path.Length > 0 ? Path.GetDirectoryName(path) : null;
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // A diagnostics screen that crashes is worse than an unopened file.
        }
    }

    /// <summary>One row of pure data; visuals are built fresh from it, so nothing is shared.</summary>
    private sealed class DiagnosticsEntry
    {
        private readonly string _searchText;

        public static DiagnosticsEntry ForEvent(EventRecord record) => new(
            record.Timestamp.ToString("HH:mm:ss.fff"),
            record.Area,
            record.Event,
            BuildDetail(record.Message, record.Data, record.CorrelationId),
            record.DurationMs is { } ms ? $"{ms:0.#} ms" : string.Empty,
            ColorForLevel(record.Level));

        public static DiagnosticsEntry ForEntry(LogEntry entry) => new(
            entry.Timestamp.ToString("HH:mm:ss.fff"),
            entry.Scope,
            entry.Message,
            entry.Detail ?? string.Empty,
            string.Empty,
            ColorForLevel(entry.Level));

        public static DiagnosticsEntry ForSystemLine(string line)
        {
            try
            {
                using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(line);
                System.Text.Json.JsonElement root = json.RootElement;
                string stamp = root.TryGetProperty("ts", out var ts) ? ts.GetString() ?? string.Empty : string.Empty;
                string area = root.TryGetProperty("area", out var areaValue) ? areaValue.GetString() ?? "OS" : "OS";
                string message = root.TryGetProperty("msg", out var msg) ? msg.GetString() ?? string.Empty : line;
                string data = root.TryGetProperty("data", out var details) ? details.GetString() ?? string.Empty : string.Empty;
                return new(stamp.Length >= 19 ? stamp[11..] : stamp, area, message, data, string.Empty, Palette.TextFaint);
            }
            catch
            {
                return new(string.Empty, "OS", "Unparsed system event", line, string.Empty, Palette.Warning);
            }
        }

        private static string BuildDetail(string? message, IReadOnlyDictionary<string, string?>? data,
            string? correlationId = null)
        {
            string correlation = string.IsNullOrWhiteSpace(correlationId) ? string.Empty : $"id={correlationId}";
            if (data is null || data.Count == 0)
            {
                return string.Join("  ·  ", new[] { message, correlation }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            string pairs = string.Join("  ", data.Select(pair => $"{pair.Key}={Shorten(pair.Value ?? "—")}"));
            string detail = message is null ? pairs : $"{message}  ·  {pairs}";
            return correlation.Length == 0 ? detail : $"{detail}  ·  {correlation}";
        }

        private static string Shorten(string value) => value.Length <= 60 ? value : value[..60] + "…";

        private static Color ColorForLevel(LogLevel level) => level switch
        {
            LogLevel.Error => Palette.Danger,
            LogLevel.Warning => Palette.Warning,
            _ => Palette.TextFaint
        };

        private DiagnosticsEntry(string time, string area, string name, string detail,
            string duration, Color tint)
        {
            Time = time;
            Area = area;
            Name = name;
            Detail = detail;
            Duration = duration;
            TextBrush = new SolidColorBrush(tint);
            _searchText = $"{area} {name} {detail}";
        }

        public bool Matches(string filter) =>
            filter.Length == 0 || _searchText.Contains(filter, StringComparison.OrdinalIgnoreCase);

        public string Time { get; }
        public string Area { get; }
        public string Name { get; }
        public string Detail { get; }
        public string Duration { get; }
        public IBrush TextBrush { get; }
    }
}
