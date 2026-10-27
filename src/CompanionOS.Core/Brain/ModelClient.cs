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
        Action? onToolCallStarted = null)
    {
        JsonObject requestBody = BuildRequestBody(profile, messages, tools);

        using HttpRequestMessage request = new(HttpMethod.Post, ComposeEndpoint(profile.Endpoint, "chat/completions"));
        request.Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        }

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string trimmedError = errorBody.Length > 500 ? errorBody[..500] : errorBody;
            throw new ModelClientException(
                $"The model endpoint answered {(int)response.StatusCode}. {trimmedError}");
        }

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
