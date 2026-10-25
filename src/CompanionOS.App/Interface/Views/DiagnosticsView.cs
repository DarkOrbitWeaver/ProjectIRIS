using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Theme;
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
    private readonly ItemsControl _rows = new();
    private bool _isPaused;
    private string _filterText = string.Empty;

    // Truth, newest first. Plain data only; the list sorts itself out when it rebinds.
    private readonly List<DiagnosticsEntry> _entries = new();

    public DiagnosticsView()
    {
        IrisEvent.EventLogged += OnEventLogged;
        CompanionLog.EntryWritten += OnEntryWritten;

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

        Button openFile = Ui.GhostButton("Open on disk", OpenOnDisk);

        StackPanel header = new() { Spacing = Palette.SpaceSm };

        // Page header like every other board: 28 px title, letterspaced caption.
        TextBlock title = Ui.Text("Diagnostics", Palette.FontPage, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        title.LineHeight = 34;

        TextBlock caption = Ui.Text("EVERYTHING SHE DID · NEWEST FIRST · NOTHING SAMPLED AWAY",
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
            "Events are one JSON line each, stored daily under her home. " +
            "Every action, step and failure is there."));

        _rows.ItemTemplate = new FuncDataTemplate<DiagnosticsEntry>((entry, _) =>
            entry is null ? null : RowControl(entry));

        TabItem eventsTab = new() { Header = "Events", Content = WrapList() };
        TabItem logTab = new() { Header = "Diary", Content = Ui.Caption("The readable diary mirrors the events above.") };
        TabControl tabs = new() { Items = { eventsTab, logTab } };

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

        Rebind();
    }

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

    private Control WrapList()
    {
        ScrollViewer scroll = new()
        {
            Content = _rows,
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

        Add(DiagnosticsEntry.ForEvent(record));
    }

    private void OnEntryWritten(LogEntry entry)
    {
        if (_isPaused)
        {
            return;
        }

        Add(DiagnosticsEntry.ForEntry(entry));
    }

    private void Add(DiagnosticsEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _entries.Insert(0, entry);
            while (_entries.Count > MaximumRows)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }

            Rebind();
        });
    }

    /// <summary>Feeds the list the slice that matches the filter, newest first.</summary>
    private void Rebind()
    {
        IReadOnlyList<DiagnosticsEntry> visible = string.IsNullOrEmpty(_filterText)
            ? _entries.ToArray()
            : _entries.Where(entry => entry.Matches(_filterText)).ToArray();
        _rows.ItemsSource = visible;
    }

    private static void OpenOnDisk()
    {
        try
        {
            string path = IrisEvent.FilePath;
            if (path.Length > 0 && File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
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
            BuildDetail(record.Message, record.Data),
            record.DurationMs is { } ms ? $"{ms:0.#} ms" : string.Empty,
            ColorForLevel(record.Level));

        public static DiagnosticsEntry ForEntry(LogEntry entry) => new(
            entry.Timestamp.ToString("HH:mm:ss.fff"),
            entry.Scope,
            entry.Message,
            entry.Detail ?? string.Empty,
            string.Empty,
            ColorForLevel(entry.Level));

        private static string BuildDetail(string? message, IReadOnlyDictionary<string, string?>? data)
        {
            if (data is null || data.Count == 0)
            {
                return message ?? string.Empty;
            }

            string pairs = string.Join("  ", data.Select(pair => $"{pair.Key}={Shorten(pair.Value ?? "—")}"));
            return message is null ? pairs : $"{message}  ·  {pairs}";
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
