using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Brain;

/// <summary>A tool call the model asked for.</summary>
public sealed class ToolCallRequest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }
}

/// <summary>
/// One message in the conversation, in the shape the OpenAI API expects.
/// The constructors keep the awkward parts (image payloads, tool result ids) out
/// of the rest of the codebase.
/// </summary>
public sealed class ChatMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;

    /// <summary>Optional screenshot as a data URI, used for vision turns.</summary>
    public string? ImageDataUri { get; init; }

    /// <summary>Tool calls the assistant asked for in this message.</summary>
    public IReadOnlyList<ToolCallRequest> ToolCalls { get; init; } = Array.Empty<ToolCallRequest>();

    /// <summary>Set on tool result messages so the model can match them up.</summary>
    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };

    public static ChatMessage User(string content, string? imageDataUri = null) =>
        new() { Role = "user", Content = content, ImageDataUri = imageDataUri };

    public static ChatMessage Assistant(string content, IReadOnlyList<ToolCallRequest>? toolCalls = null) =>
        new() { Role = "assistant", Content = content, ToolCalls = toolCalls ?? Array.Empty<ToolCallRequest>() };

    public static ChatMessage ToolResult(string toolCallId, string toolName, string content) =>
        new() { Role = "tool", Content = content, ToolCallId = toolCallId, ToolName = toolName };
}

/// <summary>What one model turn produced.</summary>
public sealed class ModelTurn
{
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<ToolCallRequest> ToolCalls { get; init; } = Array.Empty<ToolCallRequest>();
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public string FinishReason { get; init; } = string.Empty;

    /// <summary>
    /// Tool-call attempts the model started but that assembled into nothing usable
    /// (a fragment that never carried a function name). Before this field existed the
    /// drop was silent: "she chose not to act" and "her call arrived broken" looked
    /// identical in the records, and no autopsy could tell the two apart.
    /// </summary>
    public int DroppedToolCallCount { get; init; }
}

/// <summary>
/// Talks to any OpenAI-compatible chat endpoint over plain HTTP.
///
/// This single file is the reason the framework survives the model of the month:
/// llama-server today, LM Studio or a hosted frontier model tomorrow, a future
/// omni model after that. Nothing above this layer knows which one is answering.
///
/// It streams tokens, assembles tool calls that arrive in fragments, and reports
/// progress through a callback so speech and text can start before the answer ends.
/// </summary>
public sealed class ModelClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public ModelClient()
    {
        // No global timeout: a slow local model may legitimately think for minutes.
        // Cancellation is handled per turn with CancellationToken instead.
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CompanionOS/1.0");
    }

    /// <summary>
    /// Runs one streaming chat turn.
    /// </summary>
    /// <param name="profile">Which brain to use.</param>
    /// <param name="messages">Full prompt for this turn.</param>
    /// <param name="tools">Tools the model may call (already routed).</param>
    /// <param name="onTextDelta">Called for every text fragment, on a background thread.</param>
    /// <param name="cancellationToken">Cancels the stream; used for barge-in and stop.</param>
    /// <param name="onToolCallStarted">
    /// Fired once, the moment the first native <c>tool_calls</c> delta arrives, so the
    /// caller can stop speculative speech before she narrates over her own hands. This
    /// is a signal from the tool interface itself — nothing is inferred from the text.
    /// </param>
    public async Task<ModelTurn> StreamChatAsync(
        BrainProfile profile,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ModuleTool> tools,
        Func<string, Task>? onTextDelta,
        CancellationToken cancellationToken,
        Action? onToolCallStarted = null,
        IReadOnlyList<int>? historySpanLengths = null,
        int currentTurnStartIndex = 1)
    {
        FittedPrompt fitted = FitPrompt(profile, messages, tools, historySpanLengths, currentTurnStartIndex);
        HttpResponseMessage response = await SendRequestAsync(profile, fitted, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (TryReadContextOverflow(errorBody, out int actualPromptTokens, out int actualContextTokens))
            {
                int context = actualContextTokens > 0 ? actualContextTokens : Math.Max(2_048, profile.ContextTokens);
                double observedRatio = (double)actualPromptTokens / Math.Max(1, fitted.EstimatedTokens);
                int retryBudget = Math.Max(512, (int)Math.Floor(fitted.InputBudget / Math.Max(1.0, observedRatio) * 0.9));
                response.Dispose();
                fitted = FitPrompt(profile, messages, tools, historySpanLengths, currentTurnStartIndex,
                    Math.Min(retryBudget, context - Math.Clamp(profile.MaxResponseTokens, 256, Math.Max(256, context / 3))));
                CompanionLog.Warning("Model",
                    $"The endpoint tokenizer measured {actualPromptTokens:N0}/{context:N0} prompt tokens after local estimation; compacted and retried once with a {fitted.InputBudget:N0}-token input budget.");
                response = await SendRequestAsync(profile, fitted, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    response.Dispose();
                    string retryError = errorBody.Length > 500 ? errorBody[..500] : errorBody;
                    throw new ModelClientException(
                        $"The model endpoint still rejected the context-fitted retry ({(int)response.StatusCode}). {retryError}");
                }
            }
            else
            {
                response.Dispose();
                string trimmedError = errorBody.Length > 500 ? errorBody[..500] : errorBody;
                throw new ModelClientException(
                    $"The model endpoint answered {(int)response.StatusCode}. {trimmedError}");
            }
        }

        using HttpResponseMessage responseLifetime = response;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8);

        StringBuilder textBuilder = new();
        Dictionary<int, ToolCallBuilder> toolBuilders = new();
        string finishReason = string.Empty;
        int promptTokens = 0;
        int completionTokens = 0;
        bool toolCallSignalled = false;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]")
            {
                break;
            }

            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(payload);
            }
            catch (JsonException)
            {
                // Servers sometimes emit keep-alive text; ignoring it is correct.
                continue;
            }

            if (chunk is null)
            {
                continue;
            }

            if (chunk["usage"] is JsonObject usageObject)
            {
                promptTokens = usageObject["prompt_tokens"]?.GetValue<int>() ?? promptTokens;
                completionTokens = usageObject["completion_tokens"]?.GetValue<int>() ?? completionTokens;
            }

            JsonArray? choices = chunk["choices"] as JsonArray;
            if (choices is null || choices.Count == 0)
            {
                continue;
            }

            JsonNode? choice = choices[0];
            string? reason = choice?["finish_reason"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                finishReason = reason;
            }

            JsonNode? delta = choice?["delta"];
            if (delta is null)
            {
                continue;
            }

            string? contentPiece = delta["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(contentPiece))
            {
                textBuilder.Append(contentPiece);
                if (onTextDelta is not null)
                {
                    await onTextDelta(contentPiece).ConfigureAwait(false);
                }
            }

            if (delta["tool_calls"] is JsonArray toolCallArray)
            {
                // The native signal fires on the first fragment of the first call: a
                // tool decision can arrive broken across many deltas, and the caller
                // must stop streaming speech before any call has fully assembled.
                if (toolCallArray.Count > 0 && !toolCallSignalled)
                {
                    toolCallSignalled = true;
                    onToolCallStarted?.Invoke();
                }

                foreach (JsonNode? toolCallNode in toolCallArray)
                {
                    if (toolCallNode is null)
                    {
                        continue;
                    }

                    int index = toolCallNode["index"]?.GetValue<int>() ?? 0;
                    if (!toolBuilders.TryGetValue(index, out ToolCallBuilder? builder))
                    {
                        builder = new ToolCallBuilder();
                        toolBuilders[index] = builder;
                    }

                    builder.Absorb(toolCallNode);
                }
            }
        }

        List<ToolCallRequest> toolCalls = new();
        int droppedToolCalls = 0;
        foreach ((int key, ToolCallBuilder builder) in toolBuilders.OrderBy(pair => pair.Key))
        {
            ToolCallRequest built = builder.Build();
            if (string.IsNullOrWhiteSpace(built.Name))
            {
                // The model started a call but it never assembled a name. Count it: the
                // turn receipt has to be able to say "she tried and the fragment died"
                // instead of reading like she declined to act.
                droppedToolCalls++;
                continue;
            }

            toolCalls.Add(built);
        }

        return new ModelTurn
        {
            Text = textBuilder.ToString().Trim(),
            ToolCalls = toolCalls,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            FinishReason = finishReason,
            DroppedToolCallCount = droppedToolCalls
        };
    }

    /// <summary>Cheap availability probe used before a turn and by the diagnostics screen.</summary>
    public async Task<bool> IsEndpointReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ComposeEndpoint(endpoint, "models"));
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Model", $"Endpoint probe failed: {exception.Message}");
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static JsonObject BuildRequestBody(BrainProfile profile, IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ModuleTool> tools)
    {
        JsonArray messageArray = new();
        foreach (ChatMessage message in messages)
        {
            messageArray.Add(ConvertMessage(message));
        }

        JsonObject body = new()
        {
            ["model"] = profile.ModelName,
            ["stream"] = true,
            ["temperature"] = profile.Temperature,
            ["top_p"] = profile.TopP,
            ["max_tokens"] = profile.MaxResponseTokens,
            ["messages"] = messageArray,
            // Ask the server for true token counts in the final chunk; without this the
            // usage object never arrives and every turn receipt guesses from chars.
            // (OpenAI-compatible field; llama.cpp and LM Studio both honor it.)
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };

        if (tools.Count > 0)
        {
            JsonArray toolArray = new();
            foreach (ModuleTool tool in tools)
            {
                JsonNode parameters;
                try
                {
                    parameters = JsonNode.Parse(tool.ParametersJson) ?? new JsonObject();
                }
                catch (JsonException)
                {
                    parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                }

                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = parameters
                    }
                });
            }

            body["tools"] = toolArray;
            body["tool_choice"] = "auto";
        }

        return body;
    }

    /// <summary>
    /// Estimates the complete prompt, including native tool schemas and image cost.
    /// The endpoint's tokenizer is authoritative, so the estimate deliberately errs
    /// high and keeps space for the requested answer.
    /// </summary>
    public static int EstimatePromptTokens(IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ModuleTool> tools)
    {
        long characters = 0;
        int imageCount = 0;
        foreach (ChatMessage message in messages)
        {
            characters += message.Content.Length;
            if (!string.IsNullOrWhiteSpace(message.ImageDataUri)) imageCount++;
            foreach (ToolCallRequest call in message.ToolCalls)
                characters += call.Name.Length + call.ArgumentsJson.Length + 48;
        }
        foreach (ModuleTool tool in tools)
            characters += tool.Name.Length + tool.Description.Length + tool.ParametersJson.Length + 96;

        // JSON schemas and URLs tokenize less efficiently than prose. 2.8 characters
        // per token is a conservative cross-model estimate; image patches are reserved
        // separately because base64 length is not their model-token cost.
        return checked((int)Math.Min(int.MaxValue,
            Math.Ceiling(characters / 2.8) + (messages.Count * 8L) + (tools.Count * 16L) + (imageCount * 1_200L)));
    }

    private static FittedPrompt FitPrompt(BrainProfile profile, IReadOnlyList<ChatMessage> sourceMessages,
        IReadOnlyList<ModuleTool> sourceTools, IReadOnlyList<int>? historySpanLengths,
        int currentTurnStartIndex, int? inputBudgetLimit = null)
    {
        List<ChatMessage> messages = sourceMessages.ToList();
        List<ModuleTool> tools = sourceTools.ToList();
        List<int> spans = historySpanLengths?.ToList() ?? new List<int>();
        int currentStart = Math.Clamp(currentTurnStartIndex, 1, messages.Count);
        int removedTurns = 0;
        int shortenedResults = 0;
        int omittedImages = 0;

        int context = Math.Max(2_048, profile.ContextTokens);
        int responseReserve = Math.Clamp(profile.MaxResponseTokens, 256, Math.Max(256, context / 3));
        int safetyMargin = Math.Max(512, context / 10);
        int inputBudget = Math.Max(512, Math.Min(context - responseReserve - safetyMargin,
            inputBudgetLimit ?? int.MaxValue));

        while (EstimatePromptTokens(messages, tools) > inputBudget && spans.Count > 0)
        {
            int removeCount = Math.Clamp(spans[0], 0, Math.Max(0, currentStart - 1));
            if (removeCount == 0) break;
            messages.RemoveRange(1, removeCount);
            spans.RemoveAt(0);
            currentStart -= removeCount;
            removedTurns++;
        }

        foreach (int maximumResultCharacters in new[] { 4_000, 2_000, 1_000, 500, 240 })
        {
            if (EstimatePromptTokens(messages, tools) <= inputBudget) break;
            for (int index = currentStart; index < messages.Count; index++)
            {
                ChatMessage message = messages[index];
                if (message.Role != "tool" || message.Content.Length <= maximumResultCharacters) continue;
                messages[index] = CopyWithContent(message, ShortenToolResult(message.Content, maximumResultCharacters));
                shortenedResults++;
            }
        }

        // A vision result from an earlier step in this same turn is expendable once
        // textual evidence is present. Keep an image attached to the user's current
        // request intact.
        if (EstimatePromptTokens(messages, tools) > inputBudget)
        {
            for (int index = currentStart + 1; index < messages.Count; index++)
            {
                ChatMessage message = messages[index];
                if (string.IsNullOrWhiteSpace(message.ImageDataUri)) continue;
                messages[index] = new ChatMessage
                {
                    Role = message.Role,
                    Content = message.Content + " [Earlier image omitted to fit the model context.]",
                    ToolCalls = message.ToolCalls,
                    ToolCallId = message.ToolCallId,
                    ToolName = message.ToolName
                };
                omittedImages++;
                if (EstimatePromptTokens(messages, tools) <= inputBudget) break;
            }
        }

        int estimated = EstimatePromptTokens(messages, tools);
        if (removedTurns > 0 || shortenedResults > 0 || omittedImages > 0)
        {
            CompanionLog.Warning("Model",
                $"Kept this request within its context budget: estimated {estimated:N0}/{inputBudget:N0} input tokens; " +
                $"dropped {removedTurns} older turn(s), shortened {shortenedResults} tool result(s), omitted {omittedImages} earlier image(s).");
        }

        if (estimated > inputBudget)
        {
            throw new ModelClientException(
                $"This request needs about {estimated:N0} input tokens, but the {context:N0}-token brain has {inputBudget:N0} available after answer space and safety margin. " +
                "Iris kept the current request intact but could not fit its current tool catalog and results. Turn off unused modules or choose a brain with a larger context window.");
        }

        return new FittedPrompt(messages, tools, estimated, inputBudget);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(BrainProfile profile, FittedPrompt fitted,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, ComposeEndpoint(profile.Endpoint, "chat/completions"));
        request.Content = new StringContent(
            BuildRequestBody(profile, fitted.Messages, fitted.Tools).ToJsonString(), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryReadContextOverflow(string errorBody, out int promptTokens, out int contextTokens)
    {
        promptTokens = 0;
        contextTokens = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(errorBody);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                promptTokens = error.TryGetProperty("n_prompt_tokens", out JsonElement prompt) && prompt.TryGetInt32(out int promptValue)
                    ? promptValue : 0;
                contextTokens = error.TryGetProperty("n_ctx", out JsonElement context) && context.TryGetInt32(out int contextValue)
                    ? contextValue : 0;
                string type = error.TryGetProperty("type", out JsonElement typeValue) ? typeValue.GetString() ?? string.Empty : string.Empty;
                string message = error.TryGetProperty("message", out JsonElement messageValue) ? messageValue.GetString() ?? string.Empty : string.Empty;
                return type.Contains("context", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("exceeds the available context", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static ChatMessage CopyWithContent(ChatMessage message, string content) => new()
    {
        Role = message.Role,
        Content = content,
        ImageDataUri = message.ImageDataUri,
        ToolCalls = message.ToolCalls,
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName
    };

    private static string ShortenToolResult(string content, int maximumCharacters)
    {
        const string marker = "\n[Tool output shortened to fit the model context; ask for a narrower result if needed.]\n";
        int keep = Math.Max(0, maximumCharacters - marker.Length);
        int head = keep * 3 / 4;
        int tail = keep - head;
        return content[..head] + marker + (tail == 0 ? string.Empty : content[^tail..]);
    }

    private sealed record FittedPrompt(List<ChatMessage> Messages, List<ModuleTool> Tools,
        int EstimatedTokens, int InputBudget);

    private static JsonObject ConvertMessage(ChatMessage message)
    {
        JsonObject converted = new() { ["role"] = message.Role };

        if (message.Role == "tool")
        {
            converted["content"] = message.Content;
            converted["tool_call_id"] = message.ToolCallId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(message.ToolName))
            {
                converted["name"] = message.ToolName;
            }

            return converted;
        }

        if (message.Role == "assistant" && message.ToolCalls.Count > 0)
        {
            JsonArray callArray = new();
            foreach (ToolCallRequest call in message.ToolCalls)
            {
                callArray.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                    }
                });
            }

            converted["content"] = message.Content;
            converted["tool_calls"] = callArray;
            return converted;
        }

        if (!string.IsNullOrWhiteSpace(message.ImageDataUri))
        {
            // Multimodal form: text and image in one message.
            converted["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = message.Content },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = message.ImageDataUri }
                }
            };
            return converted;
        }

        converted["content"] = message.Content;
        return converted;
    }

    private static string ComposeEndpoint(string baseEndpoint, string relativePath)
    {
        string trimmed = (baseEndpoint ?? string.Empty).TrimEnd('/');
        return $"{trimmed}/{relativePath}";
    }

    /// <summary>Collects a tool call that arrives in several small fragments.</summary>
    private sealed class ToolCallBuilder
    {
        private readonly StringBuilder _arguments = new();
        private string _name = string.Empty;
        private string _id = string.Empty;

        public void Absorb(JsonNode node)
        {
            string? id = node["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
            {
                _id = id;
            }

            JsonNode? function = node["function"];
            if (function is null)
            {
                return;
            }

            string? name = function["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                _name = name;
            }

            string? argumentPiece = function["arguments"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(argumentPiece))
            {
                _arguments.Append(argumentPiece);
            }
        }

        public ToolCallRequest Build() => new()
        {
            Id = string.IsNullOrWhiteSpace(_id) ? Guid.NewGuid().ToString("N") : _id,
            Name = _name,
            ArgumentsJson = _arguments.Length == 0 ? "{}" : _arguments.ToString()
        };
    }
}

/// <summary>Raised when the model endpoint refuses or breaks a request.</summary>
public sealed class ModelClientException : Exception
{
    public ModelClientException(string message) : base(message)
    {
    }
}
