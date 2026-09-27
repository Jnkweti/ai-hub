using System.Text;
using System.Text.Json.Nodes;

namespace AIHub.Core;

public sealed class CodexClient(AgentOptions options, string? sessionId = null) : IAgentClient
{
    public Agent Agent => Agent.Codex;
    public string? SessionId { get; private set; } = options.Collaboration?.StartFreshSession == true ? null : options.Collaboration?.ResumeSessionId ?? sessionId;
    public event Action<AgentEvent>? Event;
    public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
    private JsonProcess? wire;
    private TaskCompletionSource<string>? turn;
    private CancellationToken activeToken;
    private readonly InputRequestLifetimes requests = new();
    private readonly Dictionary<string, StringBuilder> text = [];
    private readonly CompletedReplyBuffer completed = new();
    private int streamedCharacters;
    private void Emit(EventKind kind, string message, string id = "", string detail = "") => Event?.Invoke(new(Agent, kind, message, id, detail));

    public async Task ConnectAsync(CancellationToken token)
    {
        if (wire is { Alive: true }) return;
        if (wire is not null) await wire.DisposeAsync();
        wire = new JsonProcess();
        wire.Message += Handle;
        wire.Diagnostic += s => Emit(EventKind.Status, s);
        wire.Failed += ex => { requests.CancelAll(); turn?.TrySetException(ex); Emit(EventKind.Error, ex.Message); };
        var args = new List<string> { "app-server" };
        if (options.PreparationOnly)
        {
            foreach (var feature in new[] { "shell_tool", "unified_exec", "multi_agent", "apps", "plugins", "remote_plugin", "hooks", "computer_use", "browser_use", "image_generation", "code_mode", "code_mode_host", "view_image", "sleep_tool" })
                args.AddRange(["-c", "features." + feature + "=false"]);
            args.AddRange(["-c", "web_search=\"disabled\"", "-c", "mcp_servers={}"]);
        }
        if (options.Collaboration is { } collaboration) args.AddRange(collaboration.CodexArguments());
        wire.Start(JsonProcess.FindExecutable(options.Executable, "codex"), args, options.Workspace, options.Collaboration?.EnvironmentVariables());
        await wire.RequestAsync("initialize", new { clientInfo = new { name = "ai_hub", title = "AI Hub", version = "0.1.0" } }, token);
        await wire.WriteAsync(Json.Obj(new { method = "initialized" }), token);
        var parameters = new JsonObject
        {
            ["cwd"] = options.Workspace, ["approvalPolicy"] = "on-request",
            ["approvalsReviewer"] = "user", ["sandbox"] = options.AllowEdits && !options.PreparationOnly ? "workspace-write" : "read-only",
            ["developerInstructions"] = options.PreparationOnly ? ConversationPreparation.Instructions : HubCoordinator.AgentInstructions + (options.Collaboration is { } host ? "\n\n" + host.Instructions : "")
        };
        if (!string.IsNullOrWhiteSpace(options.Model)) parameters["model"] = options.Model;
        if (SessionId is not null) parameters["threadId"] = SessionId;
        var result = await wire.RequestAsync(SessionId is null ? "thread/start" : "thread/resume", parameters, token);
        SessionId = result["thread"].Str("id");
        if (string.IsNullOrEmpty(SessionId)) throw new IOException("Codex did not return a thread ID.");
        options.Collaboration?.BindSession(SessionId);
        Emit(EventKind.Session, SessionId);
        Emit(EventKind.Status, "Connected to Codex");
    }

    public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
    {
        activeToken = token;
        await ConnectAsync(token);
        text.Clear(); completed.Clear(); streamedCharacters = 0;
        turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = token.Register(() => turn.TrySetCanceled(token));
        await wire!.RequestAsync("turn/start", new
        {
            threadId = SessionId, input = new[] { new { type = "text", text = prompt } }
        }, token);
        var answer = await turn.Task.WaitAsync(token);
        if (string.IsNullOrWhiteSpace(answer)) throw new IOException("Codex finished without a reply; automatic exchange has paused.");
        return new(answer, SessionId);
    }

    private void Handle(JsonObject message)
    {
        var method = message.Str("method");
        var p = message["params"];
        if (message["id"] is not null && method.Length > 0)
        { _ = HandleRequestAsync(message.DeepClone().AsObject()); return; }
        if (p?["threadId"] is { } threadId && SessionId is not null && threadId.ToString() != SessionId) return;
        if (method == "serverRequest/resolved") { requests.Cancel(p?["requestId"]?.ToJsonString() ?? ""); return; }
        if (method == "item/agentMessage/delta")
        {
            var id = p.Str("itemId"); var delta = p.Str("delta");
            streamedCharacters += delta.Length;
            if (streamedCharacters > BoundedText.MaxFrameCharacters || text.Count > 2048)
                throw new IOException("Codex's reply exceeded AI Hub's streaming limit. This turn was stopped.");
            if (!text.TryGetValue(id, out var builder)) text[id] = builder = new();
            builder.Append(delta); Emit(EventKind.TextDelta, delta, id);
        }
        else if (method is "item/started" or "item/completed")
        {
            var item = p?["item"]; var type = item.Str("type"); var id = item.Str("id");
            if (type == "agentMessage" && method == "item/completed")
            {
                var body = item.Str("text");
                if (!string.IsNullOrWhiteSpace(body)) { completed.Add(id, body); text.Remove(id); Emit(EventKind.Message, body, id); }
            }
            else if (type is not ("agentMessage" or "userMessage" or "reasoning"))
                Emit(EventKind.Tool, $"{type} · {(method.EndsWith("started") ? "running" : item.Str("status"))}", id, item?.ToJsonString() ?? "");
        }
        else if (method == "item/commandExecution/outputDelta")
            Emit(EventKind.ToolOutput, p.Str("delta"), p.Str("itemId"));
        else if (method == "turn/completed")
        {
            requests.CancelAll();
            var status = p?["turn"].Str("status");
            if (status == "failed") turn?.TrySetException(new IOException(p?["turn"]?["error"].Str("message")));
            else if (status == "interrupted")
            {
                if (activeToken.IsCancellationRequested) turn?.TrySetCanceled(activeToken);
                else turn?.TrySetException(new IOException("Codex interrupted the turn before completion. Review activity before continuing."));
            }
            else turn?.TrySetResult(completed.Complete(text.Values.LastOrDefault()?.ToString() ?? ""));
        }
        else if (method == "turn/plan/updated") Emit(EventKind.Tool, "Plan updated", detail: p?.ToJsonString() ?? "");
        else if (method == "thread/tokenUsage/updated") Emit(EventKind.Usage, "Token usage updated", detail: p?.ToJsonString() ?? "");
        else if (method == "error") Emit(EventKind.Error, p?.ToJsonString() ?? "Codex error");
    }

    private async Task HandleRequestAsync(JsonObject message)
    {
        var method = message.Str("method"); var p = message["params"];
        var requestId = message["id"]!.ToJsonString();
        var registered = false;
        try
        {
            var requestToken = requests.Begin(requestId, activeToken).Token; registered = true;
            JsonNode result;
            if (method == "item/tool/requestUserInput")
            {
                var answers = new JsonObject();
                foreach (var q in p?["questions"]?.AsArray() ?? [])
                {
                    var decision = RequestApproval is null ? new Decision(false) : await RequestApproval(
                        Approval.Question(Agent, q), requestToken);
                    answers[q.Str("id")] = Json.Obj(new { answers = decision.Allow ? new[] { decision.Answer } : Array.Empty<string>() });
                }
                result = new JsonObject { ["answers"] = answers };
            }
            else if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
            {
                var answer = options.AllowEdits && RequestApproval is not null
                    ? await RequestApproval(new(Agent, method.Contains("fileChange") ? "Approve file changes" : "Approve command", p?.ToJsonString() ?? ""), requestToken)
                    : new Decision(false);
                result = Json.Obj(new { decision = answer.Allow ? "accept" : "decline" });
                Emit(EventKind.Tool, answer.Allow ? "You approved this action" : "Action declined", detail: p?.ToJsonString() ?? "");
            }
            else if (method == "item/permissions/requestApproval")
            {
                var answer = options.AllowEdits && RequestApproval is not null
                    ? await RequestApproval(new(Agent, "Approve requested permissions", p?.ToJsonString() ?? ""), requestToken) : new Decision(false);
                result = new JsonObject { ["permissions"] = answer.Allow ? p?["permissions"]?.DeepClone() : new JsonObject(), ["scope"] = "turn" };
            }
            else if (method == "mcpServer/elicitation/request")
                result = Json.Obj(new { action = "decline", content = (string?)null });
            else
            {
                Emit(EventKind.Error, "Unsupported Codex request: " + method);
                await wire!.WriteAsync(new JsonObject { ["id"] = message["id"]!.DeepClone(), ["error"] = Json.Obj(new { code = -32601, message = "AI Hub does not support this request yet" }) });
                return;
            }
            await wire!.WriteAsync(new JsonObject { ["id"] = message["id"]!.DeepClone(), ["result"] = result }, requestToken);
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
