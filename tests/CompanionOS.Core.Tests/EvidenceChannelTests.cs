using System.Text.Json;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Modules.BuiltIn;
using CompanionOS.Core.Perception;
using CompanionOS.Core.Recovery;
using CompanionOS.Core.Voice;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The evidence channel (Batch 17, from the 2026-10-27 "resume"/"play my playlist"
/// autopsies): the records used to keep THAT a hand moved, not WITH WHAT she moved it,
/// and her final words were never written at all — every post-mortem started with a
/// character count. These pins prove the channel now carries what actually happened.
/// </summary>
public class EvidenceChannelTests
{
    private static (string filePath, IDisposable cleanup) NewJournalFile()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"iris-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        return (Path.Combine(temp, "journal.jsonl"), new TempDirectory(temp));
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _path;
        public TempDirectory(string path) => _path = path;
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_path))
                {
                    Directory.Delete(_path, recursive: true);
                }
            }
            catch
            {
                // A locked temp file must not fail the test runner on cleanup.
            }
        }
    }

    [Fact]
    public void ToolRecordKeepsTheArgumentsTheModelSent()
    {
        (string file, IDisposable cleanup) = NewJournalFile();
        using IDisposable disposer = cleanup;
        TurnJournal journal = new(file);

        journal.RecordToolCall("t1", "router", "control_windows_media", "{\"action\":\"pause\"}");
        string content = File.ReadAllText(file);

        using JsonDocument line = JsonDocument.Parse(content.TrimEnd());
        Assert.Equal("control_windows_media", line.RootElement.GetProperty("Detail").GetString().Split('/')[1]);
        Assert.Equal("{\"action\":\"pause\"}", line.RootElement.GetProperty("Arguments").GetString());
    }

    [Fact]
    public void OversizedArgumentStringsAreTruncatedNotDropped()
    {
        (string file, IDisposable cleanup) = NewJournalFile();
        using IDisposable disposer = cleanup;
        TurnJournal journal = new(file);

        string runaway = new('x', 10000);
        journal.RecordToolCall("t1", "router", "remember", runaway);
        string content = File.ReadAllText(file);

        using JsonDocument line = JsonDocument.Parse(content.TrimEnd());
        string arguments = line.RootElement.GetProperty("Arguments").GetString()!;
        Assert.Equal(4000 + "… (truncated)".Length, arguments.Length);
        Assert.EndsWith("… (truncated)", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void AnswerCompletionKeepsHerFinalWords()
    {
        (string file, IDisposable cleanup) = NewJournalFile();
        using IDisposable disposer = cleanup;
        TurnJournal journal = new(file);

        journal.RecordAnswerCompleted("t1", "Resumed; the record failed the first time, this one worked.");
        string content = File.ReadAllText(file);

        using JsonDocument line = JsonDocument.Parse(content.TrimEnd());
        Assert.Equal("answer-completed", line.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("Resumed; the record failed the first time, this one worked.",
            line.RootElement.GetProperty("Text").GetString());
    }

    [Theory]
    [InlineData("please ressume", true)]
    [InlineData("unpause the music", true)]
    [InlineData("rewsume", true)]
    [InlineData("fine", false)]
    [InlineData("none", false)]
    [InlineData("what song is playing", false)]
    public void OneLetterTypoedActionVerbsStillGateTheTurn(string userText, bool expected)
    {
        // The boss's "please ressume" slid past the receipt gate and died unanswered;
        // "unpause" never was in the verb list. The fuzzy catch is bounded (one edit,
        // five letters or more) and judges the REQUEST — never her words.
        Assert.Equal(expected, SpeechPlanner.IsActionRequest(userText));
    }

    [Fact]
    public void MusicModuleIsMcpShapedWithSixOrthogonalTools()
    {
        using MusicPlayerModule module = new();
        string[] names = module.Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[]
        {
            "music_find_download", "music_import_file", "music_library", "music_library_folders",
            "music_player", "music_playlists"
        }, names);

        ModuleTool player = module.Tools.Single(tool => tool.Name == "music_player");
        using JsonDocument playerSchema = JsonDocument.Parse(player.ParametersJson);
        string[] actions = playerSchema.RootElement.GetProperty("properties").GetProperty("action")
            .GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains("play", actions);
        Assert.Contains("pause", actions);
        Assert.Contains("resume", actions);
        Assert.Contains("set_volume", actions);
        Assert.Equal("action", playerSchema.RootElement.GetProperty("required")[0].GetString());

        ModuleTool folders = module.Tools.Single(tool => tool.Name == "music_library_folders");
        Assert.Equal(ToolTier.SystemCritical, folders.Tier);
    }

    [Fact]
    public async Task PlayerActionDispatchAnswersTheActionListWhenTheActionIsUnknown()
    {
        using MusicPlayerModule module = new();
        ModuleTool player = module.Tools.Single(tool => tool.Name == "music_player");

        ToolResult result = await module.ExecuteAsync(new ToolInvocation
        {
            ModuleId = "music",
            Tool = player,
            Arguments = JsonDocument.Parse("{\"action\":\"explode\"}").RootElement.Clone(),
            TurnId = "t1"
        }, CancellationToken.None);

        Assert.False(result.Succeeded);
        // Corrective evidence, not a bare no: the model needs the valid actions in reach.
        Assert.Contains("set_volume", result.ModelText, StringComparison.Ordinal);
        Assert.Contains("show_controls", result.ModelText, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemMediaToolSaysPlayIsHowResumeHappens()
    {
        // The boss's "resume my song" ask died twice while Qwen saw `play` and
        // `resume` as different verbs on different tools. The description now says
        // the mapping outright.
        CoreToolsModule module = new();
        ModuleTool smtc = module.Tools.Single(tool => tool.Name == "control_windows_media");

        Assert.Contains("action=play is ALSO how you resume", smtc.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaContextLineNamesTheAppStateEvenWithoutATitle()
    {
        string withTitle = PerceptionEngine.DescribeMediaSource("Night Drive", "Hollow Light", "Playing", "YouTube");
        Assert.Contains("Media: \"Night Drive\" by Hollow Light is playing — owned by YouTube.", withTitle);

        string withoutTitle = PerceptionEngine.DescribeMediaSource("", "", "Paused", "YouTube");
        Assert.Contains("no track title", withoutTitle, StringComparison.Ordinal);
        Assert.Contains("(paused)", withoutTitle, StringComparison.Ordinal);
        Assert.Contains("owned by YouTube", withoutTitle, StringComparison.Ordinal);
        // The control path: she must be told the session is still commandable.
        Assert.Contains("system media transport commands", withoutTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void PersonaTeachesAnnotationsWrappedInOsMarkers()
    {
        // The nudges ride user-role messages wrapped in <os>; the persona teaches what
        // they are, or the model reads them as her owner mimicking a machine.
        string prompt = Persona.CreateDefault().BuildSystemPrompt("");
        Assert.Contains("<os>", prompt, StringComparison.Ordinal);
        Assert.Contains("the OS speaking to you mid-turn", prompt, StringComparison.Ordinal);
    }
}
