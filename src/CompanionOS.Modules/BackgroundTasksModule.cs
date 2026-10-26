using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Modules.BuiltIn;

/// <summary>Lets Iris inspect and stop work that an opted-in module continues in the background.</summary>
public sealed class BackgroundTasksModule : IAssistantModule
{
    private IBackgroundTaskAccess? _tasks;

    public string Id => "background_tasks";
    public string DisplayName => "Background Tasks";
    public string Purpose => "Check progress or stop a long-running module action.";
    public string IconKey => "activity";
    public string CapabilityDescription =>
        "background task tasks progress status what are you doing check work ongoing running module action cancel stop task";
    public IReadOnlyList<ModuleTool> Tools { get; } = new ModuleTool[]
    {
        new("background_task_status", "Check the current progress and result of a background module task.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"task_id":{"type":"string","description":"Optional task id; omit to list recent tasks."}},"required":[]}""",
            "background task", "progress", "status", "what are you doing", "ongoing", "running"),
        new("cancel_background_task", "Ask a running background module task to stop.", ToolTier.Reflex,
            """{"type":"object","properties":{"task_id":{"type":"string","description":"The task id to stop."}},"required":["task_id"]}""",
            "cancel task", "stop task", "abort background")
    };

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _tasks = context.BackgroundTasks;
        return Task.CompletedTask;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        IBackgroundTaskAccess tasks = _tasks ?? throw new InvalidOperationException("Background task controls are not ready.");
        string taskId = invocation.GetString("task_id").Trim();
        if (invocation.Tool.Name == "cancel_background_task")
        {
            bool cancelled = tasks.Cancel(taskId, out string message);
            return Task.FromResult(cancelled ? ToolResult.Ok(message, message) : ToolResult.Fail(message));
        }

        if (taskId.Length > 0)
        {
            BackgroundTaskSnapshot? task = tasks.Find(taskId);
            return Task.FromResult(task is null
                ? ToolResult.Ok($"No recent background task has id {taskId}.")
                : ToolResult.Ok(Describe(task)));
        }

        IReadOnlyList<BackgroundTaskSnapshot> recent = tasks.ListRecent();
        return Task.FromResult(recent.Count == 0
            ? ToolResult.Ok("There are no recent background tasks.")
            : ToolResult.Ok(string.Join("\n", recent.Select(Describe))));
    }

    public Task ShutdownAsync() => Task.CompletedTask;

    private static string Describe(BackgroundTaskSnapshot task)
    {
        string progress = task.Progress is not null
            ? $" ({task.Progress:0.##}{(task.Total is not null ? $"/{task.Total:0.##}" : "")})"
            : string.Empty;
        string result = string.IsNullOrWhiteSpace(task.Result) ? string.Empty : $" — {task.Result}";
        return $"{task.Id}: {task.ModuleName} · {task.ToolName} · {task.State} · {task.CurrentStep}{progress}{result}";
    }
}
