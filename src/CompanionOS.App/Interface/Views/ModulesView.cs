using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Plugins;
using System.Diagnostics;
using System.IO;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// What I can do, transcribed from the Body board:
///
///   header   the title at 28 px, the real "N modules · M tools · all local" caption,
///            and the shield legend that explains what asks before it runs
///   cards    one per real module in a two-column grid: icon, name, status dot,
///            "Active"/"Off", a real 40 × 24 switch, the purpose line, and an
///            expandable tool grid
///   tools    each tool gets an eye (runs on reflex) or a shield (asks first), its
///            name in the board's uppercase mono caption, and its real description
///
/// The switch is real: it writes the module enabled state that the module host's
/// tool routing already obeys. A module that is always available keeps its switch
/// but explains on hover that it never turns off — no pretend toggle.
/// </summary>
public sealed class ModulesView : UserControl
{
    private readonly CompanionRuntime _runtime;
    // Two real columns with a 16 px gutter. The old fixed 532 px cards were sized as if
    // the 64 px rail did not exist, so in the shell they could never fit two abreast and
    // the whole page silently became a single long column.
    private readonly Grid _cardGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
    private readonly TextBlock _countLine;
    private readonly Button _moreOptions;
    private readonly ContentControl _pluginsHost = new();
    private readonly List<ModuleCardControl> _cards = new();
    private readonly DispatcherTimer _telegramStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TextBlock? _telegramAccountStatus;
    private Button? _telegramAccountButton;
    private bool _toolsExpanded;

    public ModulesView(CompanionRuntime runtime)
    {
        _runtime = runtime;

        _moreOptions = new Button
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
        ToolTip.SetTip(_moreOptions, "Expand or collapse every tool list");
        _moreOptions.Click += (_, _) =>
        {
            _toolsExpanded = !_toolsExpanded;
            foreach (ModuleCardControl card in _cards)
            {
                card.SetToolsExpanded(_toolsExpanded);
            }
        };

        _countLine = Ui.Text(string.Empty, Palette.FontCaption, Palette.TextSecondaryBrush,
            FontWeight.Medium, wrap: false);
        _countLine.LineHeight = 16;
        _countLine.LetterSpacing = 0.88;

        Content = BuildLayout();
        Refresh();
        _telegramStatusTimer.Tick += (_, _) => UpdateTelegramAccountState();
        AttachedToVisualTree += (_, _) =>
        {
            UpdateTelegramAccountState();
            _telegramStatusTimer.Start();
        };
        DetachedFromVisualTree += (_, _) => _telegramStatusTimer.Stop();
    }

    /// <summary>Rebuilds every card from the live registry. Called on entry and on changes.</summary>
    public void Refresh()
    {
        _cards.Clear();
        _telegramAccountStatus = null;
        _telegramAccountButton = null;
        _cardGrid.Children.Clear();
        _cardGrid.RowDefinitions.Clear();

        IReadOnlyList<IAssistantModule> allModules = _runtime.Modules.Modules;
        IAssistantModule[] modules = allModules
            .Where(module => !module.Id.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        PluginManifest[] disabledPlugins = _runtime.Plugins.Loaded.Where(plugin => plugin.Disabled).ToArray();
        int moduleCount = allModules.Count + disabledPlugins.Length;
        int toolCount = allModules.Sum(module => module.Tools.Count) +
                        disabledPlugins.Sum(plugin => plugin.Tools.Count);

        // The board's status line, with real numbers.
        _countLine.Text = $"{moduleCount} modules · {toolCount} tools · all local"
            .ToUpperInvariant();

        for (int index = 0; index < modules.Length; index++)
        {
            IAssistantModule module = modules[index];
            int row = index / 2;
            int column = index % 2 == 0 ? 0 : 2;
            while (_cardGrid.RowDefinitions.Count <= row)
            {
                _cardGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            ModuleCardControl? card = null;
            card = new ModuleCardControl(
                module,
                _runtime.Modules.IsEnabled(module.Id),
                _toolsExpanded,
                () =>
                {
                    if (card is null)
                    {
                        return;
                    }

                    bool current = _runtime.Modules.IsEnabled(module.Id);
                    _runtime.Modules.SetEnabled(module.Id, !current);
                    // The whole grid re-renders: counts, status and every card stay true.
                    Refresh();
                });
            _cards.Add(card);
            Grid.SetRow(card.Control, row);
            Grid.SetColumn(card.Control, column);
            card.Control.Margin = new Thickness(0, 0, 0, 16);
            _cardGrid.Children.Add(card.Control);
        }

        if (modules.Length == 0)
        {
            Control empty = Ui.Text("No built-in modules are installed.",
                Palette.FontBody, Palette.TextSecondaryBrush);
            Grid.SetColumnSpan(empty, 3);
            _cardGrid.Children.Add(empty);
        }

        _pluginsHost.Content = BuildPluginsSection();
        UpdateTelegramAccountState();
    }

    private static Control LegendItem(string glyph, IBrush brush, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children =
        {
            IconGlyph.Make(glyph, 12, brush),
            Ui.Text(text, Palette.FontCaption, Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false)
        }
    };

    private Control BuildLayout()
    {
        TextBlock title = Ui.Text("What I can do", Palette.FontPage, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        title.LineHeight = 34;

        // One legend, three icons: how each action behaves, so no card has to repeat it.
        StackPanel shieldNote = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                LegendItem(IconGlyph.Eye, Palette.TextSecondaryBrush, "Runs on its own"),
                LegendItem(IconGlyph.Shield, Palette.WarningBrush, "Asks the first time"),
                LegendItem(IconGlyph.Clock14, Palette.WarningBrush, "Asks once a session"),
                LegendItem(IconGlyph.Shield, Palette.DangerBrush, "Asks every time")
            }
        };

        StackPanel heading = new() { Spacing = 4 };
        heading.Children.Add(title);
        heading.Children.Add(_countLine);
        heading.Children.Add(shieldNote);

        Grid headerRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(heading, 0);
        Grid.SetColumn(_moreOptions, 1);
        headerRow.Children.Add(heading);
        headerRow.Children.Add(_moreOptions);

        // The board scroll: 32 top/bottom, 48 sides, 24 between header and grid.
        return new ScrollViewer
        {
            AllowAutoHide = false,
            Content = new StackPanel
            {
                Margin = new Thickness(48, 32),
                Spacing = 24,
                Children = { headerRow, _cardGrid, _pluginsHost }
            }
        };
    }

    /// <summary>
    /// The plugin section: exactly what her folder loaded, and nothing else. A rejected
    /// manifest is named with its reason — the folder is the truth, and the screen must
    /// never dress up absence as presence.
    /// </summary>
    private Control BuildPluginsSection()
    {
        TextBlock caption = Ui.Text("Plugins \u00B7 from her folder", Palette.FontCaption,
            Palette.TextSecondaryBrush, FontWeight.SemiBold, wrap: false);
        caption.LineHeight = 16;
        caption.LetterSpacing = 0.88;
        caption.Margin = new Thickness(0, 16, 0, 0);

        Button refresh = null!;
        refresh = Ui.GhostButton("Refresh modules", async () =>
        {
            refresh.IsEnabled = false;
            _runtime.Events.PublishNotice("Refreshing module folders. Active MCP sessions will stop.",
                NoticeSeverity.Info);
            try
            {
                await _runtime.RefreshExternalModulesAsync(CancellationToken.None);
                Refresh();
                _runtime.Events.PublishNotice(
                    $"Module scan complete: {_runtime.Plugins.Loaded.Count} loaded, {_runtime.Plugins.Rejected.Count} rejected.",
                    _runtime.Plugins.Rejected.Count == 0 ? NoticeSeverity.Success : NoticeSeverity.Warning);
            }
            catch (Exception exception)
            {
                Refresh();
                _runtime.Events.PublishNotice($"Module refresh failed: {exception.Message}", NoticeSeverity.Danger);
            }
            finally
            {
                refresh.IsEnabled = true;
            }
        });
        ToolTip.SetTip(refresh,
            "Scan module folders, validate manifests, refresh tools, and replace module registrations.");
        Grid sectionHeader = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(caption, 0);
        Grid.SetColumn(refresh, 1);
        sectionHeader.Children.Add(caption);
        sectionHeader.Children.Add(refresh);

        Grid cards = new() { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        StackPanel section = new() { Spacing = 12, Children = { sectionHeader, cards } };
        bool anyChild = false;
        int pluginIndex = 0;

        if (!_runtime.Plugins.Loaded.Any(plugin =>
                plugin.Id.Equals("telegram-personal", StringComparison.OrdinalIgnoreCase)))
        {
            string bundledTelegram = Path.Combine(AppContext.BaseDirectory, "modules", "telegram-personal");
            if (Directory.Exists(bundledTelegram))
            {
                anyChild = true;
                Button installTelegram = null!;
                installTelegram = Ui.GhostButton("Install Telegram module", async () =>
                {
                    installTelegram.IsEnabled = false;
                    try
                    {
                        string target = Path.Combine(_runtime.Plugins.Root, "telegram-personal");
                        if (Directory.Exists(target))
                            throw new IOException($"Telegram module folder already exists at {target}. Refresh modules to load it.");
                        CopyDirectory(bundledTelegram, target);
                        await _runtime.RefreshExternalModulesAsync(CancellationToken.None);
                        Refresh();
                        _runtime.Events.PublishNotice(
                            "Telegram module installed. Set up its runtime, then enter api_id and api_hash in its card.",
                            NoticeSeverity.Success);
                    }
                    catch (Exception exception)
                    {
                        _runtime.Events.PublishNotice($"Could not install Telegram module: {exception.Message}",
                            NoticeSeverity.Danger);
                    }
                    finally { installTelegram.IsEnabled = true; }
                });
                ToolTip.SetTip(installTelegram,
                    "Copy the bundled Telegram MCP module into Iris's modules folder.");
                section.Children.Add(installTelegram);
            }
        }

        foreach (PluginManifest plugin in _runtime.Plugins.Loaded)
        {
            anyChild = true;
            int row = pluginIndex / 2;
            int column = pluginIndex % 2 == 0 ? 0 : 2;
            pluginIndex++;
            while (cards.RowDefinitions.Count <= row)
            {
                cards.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            // Same card as the built-ins. A plugin that is switched off is not registered, so a
            // display-only adapter supplies its tool list.
            string moduleId = $"plugin:{plugin.Id}";
            IAssistantModule module = _runtime.Modules.Modules.FirstOrDefault(candidate =>
                                          string.Equals(candidate.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                                      ?? new PluginModuleAdapter(_runtime.Plugins, plugin);
            bool isOn = !plugin.Disabled && _runtime.Modules.IsEnabled(moduleId);

            StackPanel footer = new() { Spacing = 10 };
            if (BuildPluginSetupActions(plugin) is { } setup)
            {
                footer.Children.Add(setup);
            }

            foreach (string secretName in plugin.Secrets)
            {
                footer.Children.Add(BuildModuleSecretRow(plugin, secretName));
            }

            ModuleCardControl card = new(module, isOn, _toolsExpanded,
                () => _ = SetPluginLaunchEnabledAsync(plugin, !isOn),
                tag: $"Plugin \u00B7 {plugin.Transport.ToUpperInvariant()}",
                footer: footer.Children.Count > 0 ? footer : null);
            _cards.Add(card);
            Grid.SetRow(card.Control, row);
            Grid.SetColumn(card.Control, column);
            cards.Children.Add(card.Control);
        }

        foreach ((string folder, string reason) in _runtime.Plugins.Rejected)
        {
            anyChild = true;
            TextBlock rejected = Ui.Text(
                $"{Path.GetFileName(folder)} — rejected: {reason}", Palette.FontBody,
                Palette.WarningBrush);
            rejected.LineHeight = 20;
            section.Children.Add(rejected);
        }

        if (!anyChild)
        {
            TextBlock empty = Ui.Text(
                $"No plugins in her folder yet — drop one in {_runtime.Plugins.Root} " +
                "and she picks it up on the next start.", Palette.FontBody,
                Palette.TextSecondaryBrush);
            empty.LineHeight = 20;
            empty.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(empty);
        }

        return section;
    }

    /// <summary>The one-time setup buttons a plugin needs, in the same button style as everything else.</summary>
    private Control? BuildPluginSetupActions(PluginManifest plugin)
    {
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (plugin.Id.Equals("browser", StringComparison.OrdinalIgnoreCase))
        {
            string installer = Path.Combine(plugin.Folder, "install-extension.ps1");
            if (File.Exists(installer))
            {
                Button setup = Ui.GhostButton("Set up Chrome", () => StartBrowserExtensionSetup(installer));
                ToolTip.SetTip(setup, "Copy the Iris extension into place and open Chrome to load it once, then pair");
                actions.Children.Add(setup);
            }
        }
        else if (plugin.Id.Equals("telegram-personal", StringComparison.OrdinalIgnoreCase))
        {
            string script = Path.Combine(plugin.Folder, "setup.ps1");
            if (File.Exists(script))
            {
                Button runtime = null!;
                runtime = Ui.GhostButton("Set up Telegram", async () => await SetupTelegramRuntimeAsync(runtime, script));
                _telegramAccountStatus = Ui.Caption(string.Empty);
                _telegramAccountButton = Ui.GhostButton("Connect account",
                    async () => await OpenTelegramSetupPageAsync());
                ToolTip.SetTip(_telegramAccountButton,
                    "Open the local Telegram setup page to connect or manage the linked account.");
                actions.Children.Add(runtime);
                actions.Children.Add(_telegramAccountButton);
                actions.Children.Add(_telegramAccountStatus);
            }
        }

        return actions.Children.Count == 0 ? null : actions;
    }

    private void UpdateTelegramAccountState()
    {
        if (_telegramAccountStatus is null || _telegramAccountButton is null)
        {
            return;
        }

        string sessionFile = Path.Combine(CompanionPaths.DataRoot, "telegram", "session.dpapi");
        bool accountLinked = File.Exists(sessionFile);
        _telegramAccountStatus.Text = accountLinked
            ? "Telegram account linked. Ask Iris to verify access if needed."
            : "Telegram account not linked.";
        _telegramAccountButton.Content = accountLinked ? "Manage account" : "Connect account";
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private async Task SetupTelegramRuntimeAsync(Button button, string script)
    {
        button.IsEnabled = false;
        _runtime.Events.PublishNotice("Installing Telegram's Python runtime. This may take a few minutes.",
            NoticeSeverity.Info);
        try
        {
            ProcessStartInfo start = new("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the Telegram runtime setup script.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0)
            {
                string detail = (await stderr) + Environment.NewLine + await stdout;
                detail = detail.Trim();
                if (detail.Length > 1400) detail = detail[^1400..];
                throw new InvalidOperationException(
                    $"Setup exited with code {process.ExitCode}. {detail} Check that Python 3.12 and network access are available.");
            }
            await _runtime.RefreshExternalModulesAsync(CancellationToken.None);
            Refresh();
            _runtime.Events.PublishNotice(
                "Telegram runtime is ready. Add api_id and api_hash to connect your account.",
                NoticeSeverity.Success);
        }
        catch (Exception exception)
        {
            _runtime.Events.PublishNotice($"Telegram setup failed: {exception.Message}", NoticeSeverity.Danger);
        }
        finally { button.IsEnabled = true; }
    }

    private async Task OpenTelegramSetupPageAsync()
    {
        try
        {
            await _runtime.RefreshExternalModulesAsync(CancellationToken.None);
            Refresh();
            PluginManifest? telegram = _runtime.Plugins.Loaded.FirstOrDefault(plugin =>
                plugin.Id.Equals("telegram-personal", StringComparison.OrdinalIgnoreCase));
            if (telegram is null || telegram.Tools.Count == 0)
                throw new InvalidOperationException("Iris could not start the Telegram MCP server. Run Set up Telegram and refresh the module first.");
            Process.Start(new ProcessStartInfo("http://127.0.0.1:47832/") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _runtime.Events.PublishNotice($"Could not start Telegram setup: {exception.Message}. Set up the runtime and enter API credentials first.",
                NoticeSeverity.Warning);
        }
    }

    private Control BuildModuleSecretRow(PluginManifest plugin, string secretName)
    {
        bool isStored;
        try
        {
            isStored = _runtime.Plugins.Secrets.Contains(plugin.Id, secretName);
        }
        catch (Exception exception)
        {
            return Ui.Text($"Credential store unavailable: {exception.Message}",
                Palette.FontCaption, Palette.WarningBrush);
        }
        TextBlock state = Ui.Caption(isStored
            ? "Saved encrypted for this Windows account."
            : "Not set. Iris will not include it in prompts or tool arguments.");
        TextBox input = new()
        {
            Width = 210,
            Height = 34,
            PasswordChar = '*',
            Watermark = $"Set {secretName}",
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(input, "Credential value is stored encrypted and sent only to this module process.");

        Button save = Ui.GhostButton("Save", async () =>
        {
            string value = input.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                _runtime.Events.PublishNotice("Enter a credential value first.", NoticeSeverity.Warning);
                return;
            }
            try
            {
                _runtime.Plugins.Secrets.Set(plugin.Id, secretName, value);
                input.Text = string.Empty;
                if (plugin.Transport == "mcp")
                {
                    await _runtime.Plugins.CloseMcpSessionAsync(plugin.Id);
                    state.Text = "Saved encrypted. Any active MCP process was stopped; it will reconnect with this value on its next call.";
                }
                else
                {
                    state.Text = "Saved encrypted for this Windows account.";
                }
                _runtime.Events.PublishNotice($"Saved {secretName} for {plugin.Name}.", NoticeSeverity.Success);
            }
            catch (Exception exception)
            {
                _runtime.Events.PublishNotice($"Could not save {secretName}: {exception.Message}", NoticeSeverity.Danger);
            }
        });

        Button remove = Ui.GhostButton("Remove", async () =>
        {
            try
            {
                _runtime.Plugins.Secrets.Remove(plugin.Id, secretName);
                input.Text = string.Empty;
                if (plugin.Transport == "mcp")
                {
                    await _runtime.Plugins.CloseMcpSessionAsync(plugin.Id);
                    state.Text = "Removed from storage and stopped any active MCP process. It will reconnect without this value on its next call.";
                }
                else
                {
                    state.Text = "Not set. Iris will not include it in prompts or tool arguments.";
                }
                _runtime.Events.PublishNotice($"Removed {secretName} for {plugin.Name}.", NoticeSeverity.Success);
            }
            catch (Exception exception)
            {
                _runtime.Events.PublishNotice($"Could not remove {secretName}: {exception.Message}", NoticeSeverity.Danger);
            }
        });

        StackPanel controls = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { input, save, remove }
        };
        StackPanel content = new()
        {
            Spacing = 5,
            Children =
            {
                Ui.Text($"Credential · {secretName} · {PluginManifest.ToSecretEnvironmentName(secretName)}",
                    Palette.FontCaption, Palette.TextPrimaryBrush, FontWeight.Medium),
                controls,
                state
            }
        };
        return content;
    }

    private async Task SetPluginLaunchEnabledAsync(PluginManifest plugin, bool enable)
    {
        string moduleId = $"plugin:{plugin.Id}";
        if (_runtime.Plugins.SetLaunchEnabled(plugin.Id, enable, out string message))
        {
            // Persist the manifest preference and runtime routing together, then
            // replace the adapter/catalog so discovery and process state match the UI.
            _runtime.Modules.SetEnabled(moduleId, enable);
            try
            {
                await _runtime.RefreshExternalModulesAsync(CancellationToken.None);
                message = enable ? "Enabled and refreshed now." : "Disabled and stopped now.";
                _runtime.Events.PublishNotice($"{plugin.Name}: {message}", NoticeSeverity.Success);
            }
            catch (Exception exception)
            {
                _runtime.Events.PublishNotice(
                    $"{plugin.Name}: preference saved, but refresh failed: {exception.Message}",
                    NoticeSeverity.Danger);
            }
            Refresh();
        }
        else
        {
            _runtime.Events.PublishNotice($"{plugin.Name}: {message}", NoticeSeverity.Danger);
        }
    }

    /// <summary>
    /// Runs the plugin's own extension installer in a hidden PowerShell window, then
    /// says exactly what Chrome will — and will not — do for you. Same contract as
    /// the one-time toast action: a failure is logged, never a crash.
    /// </summary>
    private void StartBrowserExtensionSetup(string installer)
    {
        try
        {
            ProcessStartInfo start = new("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(installer) ?? Environment.CurrentDirectory
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(installer);
            Process.Start(start);
            CompanionLog.Info("Interface", $"Browser extension installer started: {installer}");
            _runtime.Events.PublishNotice(
                "The extension is copied and its folder is on your clipboard. Chrome is open at its " +
                "extensions page: turn on Developer mode (top right), click \"Load unpacked\" and paste " +
                "the folder, then pair the browser with Iris once.",
                NoticeSeverity.Info);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Interface",
                $"Could not start the browser extension installer ({exception.Message}).");
            _runtime.Events.PublishNotice(
                "The extension installer could not start. Check the log, or run " +
                "install-extension.ps1 in her browser module by hand.",
                NoticeSeverity.Warning);
        }
    }
}

/// <summary>One module card: its switch, purpose and expandable tool grid, all real data.</summary>
internal sealed class ModuleCardControl
{
    private static readonly IReadOnlyDictionary<string, string> GlyphById =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["core"] = IconGlyph.Eye,        // Core Senses: memory, eyes, sleep, self
            ["music"] = IconGlyph.Play,      // Music: playback and transport
            ["system"] = IconGlyph.Settings, // System Control: timers, notes, launches
        };

    private readonly IAssistantModule _module;
    private readonly Action _onToggle;
    private readonly Border _dot;
    private readonly TextBlock _statusText;
    private readonly Button _switch;
    private readonly Border _switchKnob;
    private readonly Control _glyph;
    private readonly StackPanel _toggleRow;
    private readonly ContentControl _toolsIconHost;
    private readonly TextBlock _toolsLabel;
    private readonly Control _expandedHost;
    private readonly Grid _toolsGrid;

    private bool _isEnabled;
    private bool _toolsExpanded;

    /// <param name="onToggle">Invoked instead of touching module state directly, so the
    /// collection that owns this card can decide what a switch press means.</param>
    public ModuleCardControl(IAssistantModule module, bool isEnabled, bool toolsExpanded,
        Action onToggle, string? tag = null, Control? footer = null)
    {
        _module = module;
        _isEnabled = isEnabled;
        _toolsExpanded = toolsExpanded;
        _onToggle = onToggle;

        _dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };

        _statusText = Ui.Text(_isEnabled ? "Active" : "Off", Palette.FontBody,
            Palette.TextSecondaryBrush, FontWeight.Medium, wrap: false);
        _statusText.LineHeight = 18;
        _statusText.VerticalAlignment = VerticalAlignment.Center;

        (_switch, _switchKnob) = BuildSwitch();
        _switch.VerticalAlignment = VerticalAlignment.Center;

        _glyph = IconGlyph.Make(
            GlyphById.TryGetValue(module.Id, out string? geometry)
                ? geometry
                : module.Id.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase)
                    ? ModuleIconGlyph.Resolve(module.IconKey)
                    : IconGlyph.Monitor,
            24, Palette.TextPrimaryBrush);
        _glyph.VerticalAlignment = VerticalAlignment.Center;

        TextBlock title = Ui.Text(module.DisplayName, Palette.FontTitle, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        title.LineHeight = 26;
        title.VerticalAlignment = VerticalAlignment.Center;

        // Identity + name on the left; dot, word and switch pinned to the right edge.
        StackPanel identity = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        identity.Children.Add(_glyph);
        identity.Children.Add(title);
        if (tag is not null)
        {
            Border chip = Ui.Chip(tag, Palette.Iris);
            chip.VerticalAlignment = VerticalAlignment.Center;
            identity.Children.Add(chip);
        }

        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            Children = { identity, _dot, _statusText, _switch }
        };
        Grid.SetColumn(identity, 0);
        Grid.SetColumn(_dot, 2);
        Grid.SetColumn(_statusText, 3);
        Grid.SetColumn(_switch, 4);
        _statusText.Margin = new Thickness(8, 0, 12, 0);

        TextBlock purpose = Ui.Text(module.Purpose, Palette.FontEmphasis, Palette.TextSecondaryBrush);
        purpose.LineHeight = 22;

        // The board's "6 tools ▾" affordance: accent text, chevron direction = state.
        _toolsIconHost = new ContentControl { Content = MakeChevron(_toolsExpanded) };
        _toolsLabel = Ui.Text($"{module.Tools.Count} tools", Palette.FontBody,
            Palette.AccentSoftBrush, FontWeight.Medium, wrap: false);
        _toolsLabel.LineHeight = 18;
        _toggleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        _toggleRow.Children.Add(_toolsIconHost);
        _toggleRow.Children.Add(_toolsLabel);
        Button toolsToggle = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        toolsToggle.Content = _toggleRow;
        toolsToggle.Click += (_, _) => SetToolsExpanded(!_toolsExpanded);

        // The expanded area: hairline, then the two-column tool rows. The middle
        // 24 px column is the gutter between tool cells, matching the cards grid.
        _toolsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
        StackPanel expanded = new()
        {
            Spacing = 12,
            Children = { Divider(), _toolsGrid }
        };

        StackPanel shell = new() { Spacing = 12 };
        shell.Children.Add(header);
        shell.Children.Add(purpose);
        shell.Children.Add(BuildSummary(module.Tools));
        shell.Children.Add(toolsToggle);
        shell.Children.Add(expanded);
        if (footer is not null)
        {
            shell.Children.Add(Divider());
            shell.Children.Add(footer);
        }

        Border card = new()
        {
            Margin = new Thickness(0, 0, 0, 16),
            Background = Palette.ElevatedBrush,
            BorderBrush = Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(20),
            Child = shell
        };

        Control = card;
        _expandedHost = expanded;
        // State must be APPLIED at construction, not assumed: the panel defaults to
        // visible, so a "collapsed" chevron shipped with an expanded grid and every
        // first tap was swallowed re-syncing reality to the label - the "press twice"
        // the user felt. One call makes the initial state true.
        SetToolsExpanded(_toolsExpanded);

        Render();
        BuildToolRows();
    }

    public Control Control { get; }

    private static Control Divider() => new Border
    {
        Height = 1,
        Background = Palette.StrokeBrush
    };

    private static Control MakeChevron(bool expanded) =>
        IconGlyph.Make(
            expanded ? IconGlyph.ChevronDown : IconGlyph.ChevronRight, 12, Palette.AccentSoftBrush);

    private (Button button, Border knob) BuildSwitch()
    {
        Border knob = new()
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9)
        };

        Panel track = new();
        track.Children.Add(knob);

        Button button = new()
        {
            Width = 40,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Content = track
        };
        button.Click += (_, _) => _onToggle();
        return (button, knob);
    }

    /// <summary>Repaints dot, word, track, knob and glyph from the current state.</summary>
    private void Render()
    {
        // Board: on = #7C5CFF track, white knob on the right; off = #262A36 track,
        // grey knob on the left.
        _switch.Background = _isEnabled ? Palette.IrisBrush : Palette.StrokeBrush;
        _switchKnob.Background = _isEnabled ? Brushes.White : Palette.TextSecondaryBrush;
        _switchKnob.HorizontalAlignment = _isEnabled ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        _switchKnob.Margin = new Thickness(3);

        _switch.IsEnabled = true;
        // Fluent's disabled style paints the track grey and would hide the on state,
        // so the switch stays clickable and the tooltip carries the caveat instead.
        ToolTip.SetTip(_switch, _module.AlwaysAvailable
            ? "Her senses never sleep while this is on — switching it off still stands down its tools"
            : "Switch this module on or off");

        _dot.Background = _isEnabled ? Palette.SuccessBrush : Palette.TextFaintBrush;
        _statusText.Text = _isEnabled ? "Active" : "Off";
        if (IconGlyph.GetPath(_glyph) is { } glyphPath)
        {
            glyphPath.Stroke = _isEnabled ? Palette.TextPrimaryBrush : Palette.TextSecondaryBrush;
        }
    }

    private enum Asking { Free, FirstTime, Session, EveryTime }

    private static Asking Classify(ModuleTool tool)
    {
        if (tool.AlwaysConfirm)
        {
            return Asking.EveryTime;
        }

        if (tool.Tier == ToolTier.Reflex)
        {
            return Asking.Free;
        }

        return tool.SessionTrust ? Asking.Session : Asking.FirstTime;
    }

    private static (string Words, string Glyph, IBrush Brush) Describe(Asking asking) => asking switch
    {
        Asking.Free => ("Runs on its own", IconGlyph.Eye, Palette.TextSecondaryBrush),
        Asking.Session => ("Asks once a session", IconGlyph.Clock14, Palette.WarningBrush),
        Asking.EveryTime => ("Asks every time", IconGlyph.Shield, Palette.DangerBrush),
        _ => ("Asks the first time", IconGlyph.Shield, Palette.WarningBrush)
    };

    /// <summary>One calm line instead of a warning tag on every row.</summary>
    private static Control BuildSummary(IReadOnlyList<ModuleTool> tools)
    {
        List<string> parts = new();
        int free = tools.Count(tool => Classify(tool) == Asking.Free);
        int first = tools.Count(tool => Classify(tool) == Asking.FirstTime);
        int session = tools.Count(tool => Classify(tool) == Asking.Session);
        int every = tools.Count(tool => Classify(tool) == Asking.EveryTime);
        if (free > 0) { parts.Add($"{free} run on their own"); }
        if (first > 0) { parts.Add($"{first} ask the first time"); }
        if (session > 0) { parts.Add($"{session} ask once a session"); }
        if (every > 0) { parts.Add($"{every} ask every time"); }

        TextBlock line = Ui.Text(string.Join(" \u00B7 ", parts), Palette.FontCaption,
            Palette.TextFaintBrush, FontWeight.Medium, wrap: true);
        line.LineHeight = 16;
        return line;
    }

    private void BuildToolRows()
    {
        _toolsGrid.Children.Clear();
        _toolsGrid.RowDefinitions.Clear();

        IReadOnlyList<ModuleTool> tools = _module.Tools;
        for (int index = 0; index < tools.Count; index++)
        {
            ModuleTool tool = tools[index];
            (string words, string glyphGeometry, IBrush brush) = Describe(Classify(tool));

            // Plain words on top, how it behaves underneath. The technical name and the
            // model-facing description are one hover away, not on the page.
            TextBlock name = Ui.Text(ToolPresentationCatalog.Resolve(tool, default).Doing, Palette.FontBody,
                Palette.TextPrimaryBrush, FontWeight.Medium, wrap: false);
            name.LineHeight = 18;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            TextBlock how = Ui.Text(words, Palette.FontCaption, Palette.TextSecondaryBrush,
                FontWeight.Medium, wrap: false);
            how.LineHeight = 16;

            StackPanel text = new() { Spacing = 1, Children = { name, how } };
            Grid cell = new()
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Control glyph = IconGlyph.Make(glyphGeometry, 14, brush);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            glyph.Margin = new Thickness(0, 2, 10, 0);
            Grid.SetColumn(glyph, 0);
            Grid.SetColumn(text, 1);
            cell.Children.Add(glyph);
            cell.Children.Add(text);
            ToolTip.SetTip(cell, $"{tool.Name}\n{tool.Description}");

            int row = index / 2;
            while (_toolsGrid.RowDefinitions.Count <= row)
            {
                _toolsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, index % 2 == 0 ? 0 : 2);
            _toolsGrid.Children.Add(cell);
        }
    }

    /// <summary>Opens or closes the tool grid, and turns the chevron to match.</summary>
    public void SetToolsExpanded(bool expanded)
    {
        _toolsExpanded = expanded;
        _expandedHost.IsVisible = expanded;
        _toolsIconHost.Content = MakeChevron(expanded);
    }
}
