using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CompanionOS.App.Interface.Controls;
using CompanionOS.App.Interface.Theme;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Events;
using CompanionOS.Core.Voice;

namespace CompanionOS.App.Interface.Views;

/// <summary>
/// Who she is, transcribed from the Persona board:
///
///   header     her name at 34 px with a rename pencil, and the one-line subtitle
///   voices     a two-column grid of every voice the local engine offers, a 32 px
///              waveform per card, "Her voice" on the one that is pinned, and a
///              40 px circular preview button that plays real buffered audio
///   footer     the honest count, in the board's faint uppercase caption
///
/// Renaming writes the persona file; choosing a voice goes through the same
/// apply-then-persist order the pipeline uses. Nothing on this screen is fake.
/// </summary>
public sealed class PersonaView : UserControl
{
    private readonly CompanionRuntime _runtime;
    private readonly VoiceGallery _gallery;
    private SpeechAudioPlayer? _previewPlayer;
    private CancellationTokenSource? _previewCancellation;

    private readonly TextBlock _nameText;
    private readonly TextBox _nameEdit;
    private readonly Button _renameButton;
    // The board's two-column voice grid: two equal columns with a real 16 px gutter.
    // Fixed 402 px cards left a third of the page empty on a wide window and wrapped to
    // one column on a narrow one, instead of tracking the surface.
    private readonly Grid _voiceGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
    private readonly TextBlock _voiceCount = new()
    {
        FontFamily = Palette.UiFont,
        FontSize = Palette.FontCaption,
        LineHeight = 16,
        FontWeight = FontWeight.Medium,
        Foreground = Palette.TextFaintBrush,
        LetterSpacing = 0.88
    };
    private readonly TextBlock _status = new() { IsVisible = false };

    private IReadOnlyList<VoiceCandidate> _candidates = Array.Empty<VoiceCandidate>();

    public PersonaView(CompanionRuntime runtime)
    {
        _runtime = runtime;
        _gallery = new VoiceGallery(runtime.Settings.Current, runtime.Voice);

        // Her name, and the pencil that lets the user change it in place.
        _nameText = Ui.Text(_runtime.Persona.Current.Name, 34, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        _nameText.LineHeight = 40;
        _nameEdit = new TextBox
        {
            FontFamily = Palette.UiFont,
            FontSize = 34,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TextPrimaryBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Palette.IrisBrush,
            IsVisible = false,
            MinHeight = 40,
            Width = 260,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _nameEdit.LostFocus += EndRename;
        _nameEdit.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter)
            {
                // Enter is the obvious way to confirm a name; without this the only exit
                // was clicking somewhere else, which felt like the field was stuck.
                CommitRename();
                eventArgs.Handled = true;
            }
            else if (eventArgs.Key == Key.Escape)
            {
                CancelRename();
                eventArgs.Handled = true;
            }
        };
        _renameButton = new Button
        {
            Content = IconGlyph.Make(IconGlyph.Rename, 20, Palette.TextSecondaryBrush),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8)
        };
        ToolTip.SetTip(_renameButton, "Rename");
        _renameButton.Click += (_, _) => BeginRename();

        Content = BuildLayout();
        RefreshVoices();

        AttachedToVisualTree += (_, _) =>
        {
            // Voice availability can legitimately change between visits; every visit re-asks.
            RefreshVoices();
        };
        DetachedFromVisualTree += (_, _) => StopPreview();
    }

    private Control BuildLayout()
    {
        // Header: name, pencil, subtitle.
        StackPanel nameRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(_nameText);
        nameRow.Children.Add(_nameEdit);
        nameRow.Children.Add(_renameButton);
        _nameText.VerticalAlignment = VerticalAlignment.Center;
        _renameButton.VerticalAlignment = VerticalAlignment.Center;

        StackPanel header = new() { Spacing = 8 };
        header.Children.Add(nameRow);
        header.Children.Add(Ui.Text("She speaks with the voice you pick — rendered locally.",
            Palette.FontEmphasis, Palette.TextSecondaryBrush));

        // The voices section: caption, grid, honest count.
        TextBlock voiceLabel = new()
        {
            Text = "VOICE",
            FontFamily = Palette.UiFont,
            FontSize = Palette.FontCaption,
            LineHeight = 16,
            FontWeight = FontWeight.Medium,
            Foreground = Palette.TextSecondaryBrush,
            LetterSpacing = 0.88
        };

        StackPanel voicesSection = new() { Spacing = 12 };
        voicesSection.Children.Add(voiceLabel);
        voicesSection.Children.Add(_voiceGrid);
        voicesSection.Children.Add(_voiceCount);
        voicesSection.Children.Add(_status);

        // Board scroll: 32 px top/bottom, 48 px sides, 24 px between the header and voices.
        ScrollViewer scroll = new()
        {
            Content = new StackPanel
            {
                Margin = new Thickness(48, 32),
                Spacing = 24,
                Children = { header, voicesSection }
            }
        };
        return scroll;
    }

    /// <summary>Rebuilds the grid from the real gallery; the pinned voice gets the board's pill.</summary>
    private void RefreshVoices()
    {
        StopPreview();
        _candidates = _gallery.DescribeCandidates();
        _voiceGrid.Children.Clear();
        _voiceGrid.RowDefinitions.Clear();

        // The board section: the label above, the grid, the honest count below.
        _voiceCount.Text = _candidates.Count == 0
            ? "0 VOICES · LOCAL ENGINE"
            : $"{_candidates.Count} VOICES · LOCAL ENGINE · CHANGED VOICES TAKE EFFECT INSTANTLY";

        if (_candidates.Count == 0)
        {
            // One resolver everywhere: the picker must name the port the chosen engine
            // really speaks through, never a leftover default of another engine.
            string endpoint = _runtime.Settings.Current.Voice.GetEffectiveEndpoint();
            Control empty = Ui.Text(
                "No voices are available right now. The speech server at " +
                endpoint + " is not answering, so she has nothing to pick from.",
                Palette.FontBody, Palette.TextSecondaryBrush);
            Grid.SetColumnSpan(empty, 3);
            _voiceGrid.Children.Add(empty);
            return;
        }

        string pinned = _runtime.Settings.Current.Voice.ChosenByUser
            ? _runtime.Settings.Current.Voice.VoiceId
            : string.Empty;
        for (int index = 0; index < _candidates.Count; index++)
        {
            int row = index / 2;
            int column = index % 2 == 0 ? 0 : 2;
            while (_voiceGrid.RowDefinitions.Count <= row)
            {
                _voiceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            Control card = BuildVoiceCard(_candidates[index], _candidates[index].VoiceId == pinned);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            _voiceGrid.Children.Add(card);
        }
    }

    private Control BuildVoiceCard(VoiceCandidate candidate, bool isPinned)
    {
        // Preview: the real engine renders one real sample; failures are shown, never faked.
        Button preview = new()
        {
            Content = IconGlyph.Make(IconGlyph.Play, 16, Palette.TextPrimaryBrush),
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            BorderBrush = Palette.StrokeBrush,
            Background = Palette.SurfaceBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(preview, $"Preview {candidate.Label}");
        preview.Click += (_, _) => _ = PreviewAsync(candidate);

        // Clicking the card applies her voice for real — a single honest action.
        Border card = new()
        {
            Margin = new Thickness(0, 0, 0, 12),
            Background = Palette.ElevatedBrush,
            BorderBrush = isPinned ? Palette.IrisBrush : Palette.StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Palette.RadiusPanel),
            Padding = new Thickness(16),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (isPinned)
        {
            // The board marks the owned voice with a one-pixel ring inside the border.
            card.BoxShadow = new BoxShadows(new BoxShadow
            {
                Color = Palette.Iris,
                OffsetX = 0,
                OffsetY = 0,
                Blur = 0,
                Spread = 1
            });
        }

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children =
            {
                IconGlyph.Make(IconGlyph.VoiceWave, 32,
                    isPinned ? Palette.AccentSoftBrush : Palette.TextSecondaryBrush),
                BuildTextCell(candidate, isPinned),
                preview
            }
        };
        preview.VerticalAlignment = VerticalAlignment.Center;
        card.Child = row;
        card.PointerReleased += (_, eventArgs) =>
        {
            if (eventArgs.InitialPressMouseButton == MouseButton.Left)
            {
                _ = ChooseAsync(candidate);
            }
        };

        return card;
    }

    private static Control BuildTextCell(VoiceCandidate candidate, bool isPinned)
    {
        Grid cell = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        StackPanel text = new();
        TextBlock name = Ui.Text(candidate.Label, Palette.FontEmphasis, Palette.TextPrimaryBrush,
            FontWeight.SemiBold, wrap: false);
        name.LineHeight = 22;
        text.Children.Add(name);
        TextBlock detail = Ui.Text(candidate.Detail, Palette.FontBody, Palette.TextSecondaryBrush);
        detail.LineHeight = 18;
        detail.FontWeight = FontWeight.Medium;
        text.Children.Add(detail);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(text, 0);
        cell.Children.Add(text);

        if (isPinned)
        {
            Border pill = new()
            {
                Background = new SolidColorBrush(Palette.Iris, 0.14),
                CornerRadius = new CornerRadius(Palette.RadiusPill),
                Padding = new Thickness(10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.Text("Her voice", Palette.FontBody, Palette.AccentSoftBrush,
                    FontWeight.Medium, wrap: false)
            };
            Grid.SetColumn(pill, 1);
            cell.Children.Add(pill);
        }

        return cell;
    }

    private void BeginRename()
    {
        _nameText.IsVisible = false;
        _nameEdit.IsVisible = true;
        _nameEdit.Text = _runtime.Persona.Current.Name;
        _nameEdit.SelectAll();
        _nameEdit.Focus();
    }

    private void EndRename(object? sender, RoutedEventArgs eventArguments) => CommitRename();

    private void CommitRename()
    {
        if (!_nameEdit.IsVisible)
        {
            return;
        }

        string name = _nameEdit.Text?.Trim() ?? string.Empty;
        _nameEdit.IsVisible = false;
        _nameText.IsVisible = true;

        if (name.Length == 0)
        {
            // An empty box is not a rename; keep the name she already had.
            name = _runtime.Persona.Current.Name;
            _nameEdit.Text = name;
            return;
        }

        if (name == _runtime.Persona.Current.Name)
        {
            return;
        }

        // A rename is a real file write, not a memory trick: her name also lives in the
        // wake matcher's vocabulary, so it must persist before anything uses it.
        _runtime.Persona.Current.Name = name;
        _runtime.Persona.Current.WakeName = name.ToLowerInvariant();
        _runtime.Persona.Save();
        _nameText.Text = name;

        _runtime.Events.PublishNotice($"Renamed to {name}.", NoticeSeverity.Success);
        _runtime.Events.Publish(new SettingsChangedEvent());
    }

    private void CancelRename()
    {
        _nameEdit.Text = _runtime.Persona.Current.Name;
        _nameEdit.IsVisible = false;
        _nameText.IsVisible = true;
    }

    /// <summary>Plays one real sample through the real engine. Never throws; failure is shown.</summary>
    private async Task PreviewAsync(VoiceCandidate candidate)
    {
        StopPreview();
        _previewCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = _previewCancellation.Token;

        ShowStatus($"Rendering {_candidateName(candidate)}…");
        try
        {
            SpeechAudio? audio = await _gallery.PreviewAsync(candidate, token);
            if (audio is null || audio.Bytes.Length == 0)
            {
                ShowStatus("This machine cannot render that voice right now.");
                return;
            }

            _previewPlayer ??= new SpeechAudioPlayer();
            await _previewPlayer.PlayAsync(audio,
                Math.Clamp(_runtime.Settings.Current.Voice.Volume, 0.2, 1.0), token);
            ShowStatus($"Played {_candidateName(candidate)}.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Preview stopped.");
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not play: {exception.Message}");
        }
    }

    private string _candidateName(VoiceCandidate candidate)
    {
        // "Heart" comes from "Heart · warm, American English" — the voice's name only.
        int separator = candidate.Label.IndexOf(' ');
        return separator > 0 ? candidate.Label[..separator] : candidate.Label;
    }

    /// <summary>Locks a voice in: apply through the pipeline first, then persist, then rebuild.</summary>
    private async Task ChooseAsync(VoiceCandidate candidate)
    {
        if (string.Equals(_runtime.Settings.Current.Voice.VoiceId, candidate.VoiceId,
                StringComparison.Ordinal))
        {
            return; // already her voice; choosing again would replay audio for nothing
        }

        StopPreview();
        ShowStatus("Swapping her voice…");
        try
        {
            VoiceProfile profile = _gallery.BuildProfile(candidate);

            // Apply first, persist second: a refused profile leaves nothing on disk
            // claiming a voice she cannot produce.
            await _runtime.Voice.ApplyVoiceProfileAsync(profile);

            _runtime.Settings.Current.Voice.Engine = profile.Engine;
            _runtime.Settings.Current.Voice.VoiceId = profile.VoiceId;
            _runtime.Settings.Current.Voice.ChosenByUser = true;
            _runtime.Settings.Save();
            _runtime.Events.Publish(new SettingsChangedEvent());

            _runtime.Events.PublishNotice($"Voice changed to {candidate.Label}.", NoticeSeverity.Success);
            HighlightStatus($"Voice changed to {candidate.Label}.");
            RefreshVoices();
        }
        catch (Exception exception)
        {
            ShowStatus($"That voice could not be applied: {exception.Message}");
        }
    }

    /// <summary>A single quiet line for feedback the board has no room for.</summary>
    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.FontFamily = Palette.UiFont;
        _status.FontSize = Palette.FontBody;
        _status.LineHeight = 18;
        _status.Foreground = Palette.TextSecondaryBrush;
        _status.IsVisible = true;
    }

    private void HighlightStatus(string text)
    {
        ShowStatus(text);
        _status.Foreground = Palette.AccentSoftBrush;
    }

    private void StopPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;

        try
        {
            _previewPlayer?.Stop();
            _runtime.Voice.StopSpeaking();
        }
        catch
        {
            // Nothing here is worth a dialog; playback teardown is best effort.
        }
    }
}
