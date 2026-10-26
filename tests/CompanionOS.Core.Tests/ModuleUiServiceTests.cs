using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;
using System.Collections.Generic;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The module Ui scripting surface: cards narrate honestly, reach exactly one terminal
/// state, drop late updates after it, and every note carries its kind and hold. These
/// tests pin the event contract the floating feedback cards render from.
/// </summary>
public sealed class ModuleUiServiceTests
{
    private static ModuleUiService WithCollector(EventBus events, List<ModuleTaskFeedbackEvent> seen)
    {
        events.Subscribe<ModuleTaskFeedbackEvent>(payload =>
        {
            lock (seen)
            {
                seen.Add(payload);
            }
        });
        return new ModuleUiService(events);
    }

    private static List<ModuleTaskFeedbackEvent> Snapshot(List<ModuleTaskFeedbackEvent> seen)
    {
        lock (seen)
        {
            return [.. seen];
        }
    }

    [Fact]
    public void ATaskNarratesBeginStepsProgressAndOneCompletion()
    {
        EventBus events = new();
        List<ModuleTaskFeedbackEvent> seen = [];
        ModuleUiService ui = WithCollector(events, seen);
        IModuleUi bound = ui.ForModule("yt", "YouTube", "youtube");

        IModuleTaskHandle task = bound.BeginTask(new ModuleTaskSpec
        {
            Title = "Downloading song",
            IconKey = "download",
            Detail = "Hollowlight · Cold Spires"
        });
        task.Step("Requesting the stream");
        task.Progress(0.4, 1.0);
        task.Complete("Saved to music");

        List<ModuleTaskFeedbackEvent> history = Snapshot(seen);
        Assert.Equal(4, history.Count);
        Assert.All(history, payload => Assert.Equal(task.Id, payload.TaskId));
        Assert.All(history, payload => Assert.Equal("yt", payload.ModuleId));
        Assert.All(history, payload => Assert.Equal("YouTube", payload.ModuleName));
        Assert.All(history, payload => Assert.Equal("download", payload.IconKey));
        Assert.Equal(ModuleFeedbackKind.TaskStarted, history[0].Kind);
        Assert.Equal("Downloading song", history[0].Title);
        Assert.Equal("Hollowlight · Cold Spires", history[0].Detail);

        Assert.Equal(ModuleFeedbackKind.TaskRunning, history[1].Kind);
        Assert.Equal("Requesting the stream", history[1].Detail);

        Assert.Equal(ModuleFeedbackKind.TaskRunning, history[2].Kind);
        Assert.Equal(0.4, history[2].Progress!.Value, precision: 3);
        Assert.Equal(1.0, history[2].Total!.Value, precision: 3);

        Assert.Equal(ModuleFeedbackKind.TaskCompleted, history[3].Kind);
        Assert.Equal("Saved to music", history[3].Summary);
        // The card fades with its story still on it: the last step line stays as detail.
        Assert.Equal("Requesting the stream", history[3].Detail);
        Assert.Equal(history[0].TaskId, history[3].TaskId);
    }

    [Fact]
    public void OneTaskReachesExactlyOneTerminalStateAndLateWorkIsDropped()
    {
        EventBus events = new();
        List<ModuleTaskFeedbackEvent> seen = [];
        IModuleUi bound = WithCollector(events, seen).ForModule("sandbox", "Sandbox", "sandbox");

        IModuleTaskHandle task = bound.BeginTask(new ModuleTaskSpec { Title = "Moving file" });
        task.Complete("Saved to sandbox storage");
        task.Step("did this land after the end?");
        task.Complete("overwritten verdict");
        task.Fail("contradictory verdict");
        task.Cancel("contradicted again");
        task.Progress(9, 10);

        List<ModuleTaskFeedbackEvent> history = Snapshot(seen);
        Assert.Equal(2, history.Count);
        Assert.Equal(ModuleFeedbackKind.TaskStarted, history[0].Kind);
        Assert.Equal(ModuleFeedbackKind.TaskCompleted, history[1].Kind);
        Assert.Equal("Saved to sandbox storage", history[1].Summary);
    }

    [Fact]
    public void AFailingTaskStatesTaskFailedAndIgnoresRetitle()
    {
        EventBus events = new();
        List<ModuleTaskFeedbackEvent> seen = [];
        IModuleUi bound = WithCollector(events, seen).ForModule("yt", "YouTube", "youtube");

        IModuleTaskHandle task = bound.BeginTask(new ModuleTaskSpec { Title = "Downloading song" });
        task.Fail("No connection");
        task.Retitle("Downloading song"); // may only rewrite a running card
        task.Step("should not narrate");

        List<ModuleTaskFeedbackEvent> history = Snapshot(seen);
        Assert.Equal(2, history.Count);
        Assert.Equal(ModuleFeedbackKind.TaskFailed, history[1].Kind);
        Assert.Equal("No connection", history[1].Summary);
    }

    [Fact]
    public void CancellingBeforeAnythingElseBuysCleanTermination()
    {
        EventBus events = new();
        List<ModuleTaskFeedbackEvent> seen = [];
        IModuleUi bound = WithCollector(events, seen).ForModule("telegram", "Telegram", "telegram");

        IModuleTaskHandle task = bound.BeginTask(new ModuleTaskSpec { Title = "Sending message" });
        task.Cancel("Stopped on request");
        task.Complete("must not overwrite");

        List<ModuleTaskFeedbackEvent> history = Snapshot(seen);
        Assert.Equal(2, history.Count);
        Assert.Equal(ModuleFeedbackKind.TaskCancelled, history[1].Kind);
        Assert.Equal("Stopped on request", history[1].Summary);
    }

    [Fact]
    public void RetitleAndUpdatesKeepTheCardHonest()
    {
        EventBus events = new();
        List<ModuleTaskFeedbackEvent> seen = [];
        IModuleUi bound = WithCollector(events, seen).ForModule("yt", "YouTube", "youtube");

        IModuleTaskHandle task = bound.BeginTask(new ModuleTaskSpec { Title = "Downloading song" });
        task.Retitle("Downloading Hollowlight");
        task.Step("Fetching the audio");

        List<ModuleTaskFeedbackEvent> history = Snapshot(seen);
        Assert.Equal(ModuleFeedbackKind.TaskRunning, history[1].Kind);
        Assert.Equal("Downloading Hollowlight", history[1].Title);
        Assert.Equal("Fetching the audio", history[2].Detail);
    }

    [Fact]
    public void NotesStayWithinTheirKindAndHoldContract()
    {
        EventBus events = new();
        List<ModuleTaskFeedbackEvent> seen = [];
        IModuleUi bound = WithCollector(events, seen).ForModule("telegram", "Telegram", "telegram");

        bound.Note(ModuleNoteKind.Warning, "No user found");
        bound.Note(ModuleNoteKind.Error, "The file did not send");
        bound.Note(ModuleNoteKind.Info, "   "); // empty text is refused, not rendered

        List<ModuleTaskFeedbackEvent> history = Snapshot(seen);
        Assert.Equal(2, history.Count);
        foreach (ModuleTaskFeedbackEvent payload in history)
        {
            Assert.Equal(ModuleFeedbackKind.Note, payload.Kind);
            Assert.StartsWith("note-", payload.TaskId);
        }
        Assert.Equal(ModuleNoteKind.Warning, history[0].NoteKind);
        Assert.Equal(6_000, history[0].HoldMilliseconds, precision: 1);
        Assert.Equal(ModuleNoteKind.Error, history[1].NoteKind);
        Assert.Equal(8_000, history[1].HoldMilliseconds, precision: 1);
    }

    [Theory]
    [InlineData(-2, 0)]
    [InlineData(0, 0)]
    [InlineData(6, 6_000)]
    [InlineData(999, 60_000)]
    public void HoldTimeClampsSoACardNeverLingersForever(double specSeconds, double expectedMilliseconds)
    {
        EventBus events = new();
        List<ModuleTaskFeedbackEvent> seen = [];
        IModuleUi bound = WithCollector(events, seen).ForModule("yt", "YouTube", "youtube");

        bound.BeginTask(new ModuleTaskSpec { Title = "Downloading song", HoldAfterFinishSeconds = specSeconds });

        Assert.Equal(expectedMilliseconds, Snapshot(seen)[0].HoldMilliseconds, precision: 1);
    }

    [Fact]
    public void ModulesWithoutAnIconFallBackToHerBodyGlyph()
    {
        EventBus events = new();
        List<ModuleTaskFeedbackEvent> seen = [];
        IModuleUi bound = WithCollector(events, seen).ForModule("notes", "Notes", "");

        IModuleTaskHandle task = bound.BeginTask(new ModuleTaskSpec { Title = "Writing note", IconKey = null });
        Assert.Equal("body", Snapshot(seen)[0].IconKey);

        task.Cancel("done");
    }
}
