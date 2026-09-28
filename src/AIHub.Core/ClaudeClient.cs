using System.Text;
using System.Text.Json.Nodes;

namespace AIHub.Core;

public sealed class ClaudeClient(AgentOptions options, string? sessionId = null) : IAgentClient
{
    public Agent Agent => Agent.Claude;
    public string? SessionId { get; private set; } = options.Collaboration?.StartFreshSession == true ? null : options.Collaboration?.ResumeSessionId ?? sessionId;
    public event Action<AgentEvent>? Event;
    public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
    private JsonProcess? wire;
    private TaskCompletionSource<string>? turn;
    private CancellationToken activeToken;
    private readonly InputRequestLifetimes requests = new();
    private string messageId = Guid.NewGuid().ToString("N");
    private readonly StringBuilder streamed = new();
    private int streamedCharacters;
    private readonly CompletedReplyBuffer completed = new();
    private readonly object turnGate = new(); // Per-turn state is reset by the run thread and written by the reader thread.
    private void Emit(EventKind kind, string message, string id = "", string detail = "") => Event?.Invoke(new(Agent, kind, message, id, detail));

    public async Task ConnectAsync(CancellationToken token)
    {
        if (wire is { Alive: true }) return;
        if (wire is not null) await wire.DisposeAsync();
        wire = new(); wire.Message += Handle;
        wire.Diagnostic += s => Emit(EventKind.Status, s);
        wire.Failed += ex => { requests.CancelAll(); turn?.TrySetException(ex); Emit(EventKind.Error, ex.Message); };
        var args = new List<string> { "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--permission-prompt-tool", "stdio", "--permission-mode", options.AllowEdits ? "manual" : "plan",
            "--append-system-prompt", options.PreparationOnly ? ConversationPreparation.Instructions : HubCoordinator.AgentInstructions + (options.Collaboration is { } host ? "\n\n" + host.Instructions : "") };
        if (options.PreparationOnly) args.AddRange(["--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}"]);
        else if (!options.AllowEdits) args.AddRange(["--tools", "Read,Glob,Grep,AskUserQuestion"]);
        if (options.Collaboration is { } collaboration)
            args.AddRange(["--strict-mcp-config", "--mcp-config", collaboration.ClaudeConfiguration(),
                "--allowedTools", "mcp__ai_hub__get_task_context,mcp__ai_hub__submit_message,mcp__ai_hub__get_messages,mcp__ai_hub__get_evidence,mcp__ai_hub__mark_addressed,mcp__ai_hub__get_shared_context,mcp__ai_hub__publish_context,mcp__ai_hub__get_context_records,mcp__ai_hub__read_context_record,mcp__ai_hub__read_context_source,mcp__ai_hub__claim_work,mcp__ai_hub__complete_work,mcp__ai_hub__get_work,mcp__ai_hub__get_events"]);
        else if (!options.AllowEdits && !options.PreparationOnly) args.AddRange(["--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}"]);
        if (SessionId is not null) args.Add("--resume=" + SessionId);
        if (!string.IsNullOrWhiteSpace(options.Model)) args.AddRange(["--model", options.Model]);
        wire.Start(JsonProcess.FindExecutable(options.Executable, "claude"), args, options.Workspace, options.Collaboration?.EnvironmentVariables());
        await wire.RequestAsync("initialize", new { subtype = "initialize", hooks = new { } }, token, claude: true);
        Emit(EventKind.Status, "Connected to Claude Code");
    }
    public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
    {
        activeToken = token;
        await ConnectAsync(token);
        lock (turnGate) { streamed.Clear(); completed.Clear(); streamedCharacters = 0; messageId = Guid.NewGuid().ToString("N"); }
        turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = token.Register(() => turn.TrySetCanceled(token));
        await wire!.WriteAsync(Json.Obj(new
        {
            type = "user", session_id = SessionId ?? "", client_composed = true,
            message = new { role = "user", content = prompt }, parent_tool_use_id = (string?)null
        }), token);
        var result = await turn.Task.WaitAsync(token);
        if (string.IsNullOrWhiteSpace(result)) throw new IOException("Claude finished without a reply; automatic exchange has paused.");
        return new(result, SessionId);
    }
    private void Handle(JsonObject message)
    {
        switch (message.Str("type"))
        {
            case "control_request": _ = HandleRequestAsync(message.DeepClone().AsObject()); return;
            case "control_cancel_request": requests.Cancel(message.Str("request_id")); return;
        }
        lock (turnGate) HandleTurn(message);
    }
    private void HandleTurn(JsonObject message)
    {
        switch (message.Str("type"))
        {
            case "system":
                if (message["session_id"] is { } session)
                {
                    SessionId = session.ToString();
                    if (options.Collaboration?.BindSession(SessionId) is { } replaced)
                        Emit(EventKind.Status, $"Claude Code started a new native session; session {replaced} did not resume. The phase continues with the new session.");
                    Emit(EventKind.Session, SessionId);
                }
                Emit(EventKind.Status, "Claude · " + message.Str("subtype"), detail: message.ToJsonString());
                break;
            case "stream_event":
                var ev = message["event"];
                if (ev.Str("type") == "message_start")
                { messageId = ev?["message"].Str("id") ?? Guid.NewGuid().ToString("N"); streamed.Clear(); }
                if (ev?["delta"].Str("type") == "text_delta")
                {
                    var delta = ev?["delta"].Str("text") ?? ""; streamedCharacters += delta.Length;
                    if (streamedCharacters > BoundedText.MaxFrameCharacters)
                        throw new IOException("Claude's reply exceeded AI Hub's streaming limit. This turn was stopped.");
                    streamed.Append(delta); Emit(EventKind.TextDelta, delta, messageId);
                }
                break;
            case "assistant":
                var id = message["message"].Str("id");
                var body = new StringBuilder();
                foreach (var block in message["message"]?["content"]?.AsArray() ?? [])
                {
                    if (block.Str("type") == "text") body.Append(block.Str("text"));
                    else if (block.Str("type") == "tool_use") Emit(EventKind.Tool, block.Str("name"), block.Str("id"), block?["input"]?.ToJsonString() ?? "");
                }
                if (body.Length > 0) { completed.Add(id, body.ToString()); Emit(EventKind.Message, body.ToString(), id); }
                break;
            case "user":
                if (message["message"]?["content"] is JsonArray content)
                    foreach (var block in content)
                        if (block.Str("type") == "tool_result") Emit(EventKind.ToolOutput, block?["content"]?.ToString() ?? "", block.Str("tool_use_id"),
                            Json.Obj(new { isFinal = true, isError = block.Bool("is_error"),
                                exitCode = block?["exit_code"]?.DeepClone() ?? (message["tool_use_result"] as JsonObject)?["exitCode"]?.DeepClone() ?? (message["tool_use_result"] as JsonObject)?["exit_code"]?.DeepClone() }).ToJsonString());
                break;
            case "result":
                requests.CancelAll();
                if (message["session_id"] is { } sid) { SessionId = sid.ToString(); Emit(EventKind.Session, SessionId); }
                Emit(EventKind.Usage, "Claude usage", detail: Json.Obj(new { costUsd = message["total_cost_usd"]?.DeepClone(), usage = message["usage"]?.DeepClone() }).ToJsonString());
                if (message.Bool("is_error")) turn?.TrySetException(new IOException(message.Str("result") + " " + message["errors"]?.ToJsonString()));
                else turn?.TrySetResult(completed.Complete(message.Str("result") is { Length: > 0 } r ? r : streamed.ToString()));
                break;
        }
    }
    private async Task HandleRequestAsync(JsonObject message)
    {
        var requestId = message.Str("request_id");
        var registered = false;
        try
        {
            var request = message["request"]; var input = request?["input"];
            var requestToken = requests.Begin(requestId, activeToken).Token; registered = true;
            JsonNode response;
            if (request.Str("subtype") != "can_use_tool") response = Json.Obj(new { behavior = "deny", message = "Unsupported host request" });
            else if (request.Str("tool_name") == "AskUserQuestion")
            {
                var answers = new JsonObject();
                var declined = false;
                foreach (var q in input?["questions"]?.AsArray() ?? [])
                {
                    var decision = RequestApproval is null ? new Decision(false) : await RequestApproval(
                        Approval.Question(Agent, q), requestToken);
                    if (!decision.Allow) { declined = true; break; }
                    answers[q.Str("question")] = decision.Answer;
                }
                var updated = input?.DeepClone().AsObject() ?? new JsonObject(); updated["answers"] = answers;
                response = declined ? Json.Obj(new { behavior = "deny", message = "The user declined to answer this question." })
                    : new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updated };
            }
            else if (options.Collaboration is not null && CollaborationMcpHost.IsTool(request.Str("tool_name")))
                response = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input?.DeepClone() ?? new JsonObject() };
            else
            {
                var decision = options.AllowEdits && RequestApproval is not null
                    ? await RequestApproval(new(Agent, "Approve " + request.Str("tool_name"), input?.ToJsonString() ?? ""), requestToken)
                    : new Decision(false);
                response = decision.Allow
                    ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input?.DeepClone() ?? new JsonObject() }
                    : Json.Obj(new { behavior = "deny", message = options.AllowEdits ? "User declined this action" : "AI Hub is in discussion mode; only read tools are available" });
                Emit(EventKind.Tool, decision.Allow ? "You approved this action" : "Action declined", detail: input?.ToJsonString() ?? "");
            }
            await wire!.WriteAsync(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject
            {
                ["subtype"] = "success", ["request_id"] = message["request_id"]!.DeepClone(), ["response"] = response
            } }, requestToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { requests.CancelAll(); turn?.TrySetException(ex); Emit(EventKind.Error, ex.Message); }
        finally { if (registered) requests.End(requestId); }
    }
    public async ValueTask DisposeAsync()
    {
        requests.CancelAll();
        turn?.TrySetCanceled();
        if (wire is not null) await wire.DisposeAsync();
        wire = null;
    }
}
