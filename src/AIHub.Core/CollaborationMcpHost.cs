using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Core;

/// <summary>
/// A revocable, provider-bound MCP connection that lives for a phase; all tools execute in the host process.
/// The resident provider session keeps one pipe while the host attaches the current dispatch to it per turn.
/// </summary>
public sealed class CollaborationMcpHost : IAsyncDisposable
{
    private readonly CancellationTokenSource life;
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string pipeName = "aihub-collaboration-" + Guid.NewGuid().ToString("N");
    private readonly ICollaborationTools baseline;
    private volatile ICollaborationTools? tools;
    private volatile string dispatchId;
    private readonly Agent agent;
    private readonly Task serving;
    private string? sessionId;
    private int calls, repairs;
    private int disposed;
    // Initialized connections (normally one per resident session) that can receive server-to-client notifications.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (StreamWriter Writer, SemaphoreSlim Gate)> live = new();
    private int connectionIds;
    internal event Action<string, JsonNode?>? Pushed; // Test observability.
    /// <summary>
    /// Sends a JSON-RPC notification to every initialized connection and returns how many received it. Claude Code
    /// surfaces <c>notifications/claude/channel</c> inside a running turn when started with its channels flag; other
    /// clients ignore unknown notifications. A failed write means that connection is gone.
    /// </summary>
    public async Task<int> PushAsync(string method, JsonNode? parameters)
    {
        var line = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters?.DeepClone() }.ToJsonString(Wire);
        var delivered = 0;
        foreach (var (id, connection) in live.ToArray())
        {
            await connection.Gate.WaitAsync(life.Token).ConfigureAwait(false);
            try { await connection.Writer.WriteLineAsync(line.AsMemory(), life.Token).ConfigureAwait(false); delivered++; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { live.TryRemove(id, out _); }
            finally { connection.Gate.Release(); }
        }
        Pushed?.Invoke(method, parameters);
        return delivered;
    }
    public string DispatchId => dispatchId;
    public bool StartFreshSession { get; init; }
    public string? ResumeSessionId { get; init; }
    public bool LimitReached => Volatile.Read(ref repairs) >= 3 || Volatile.Read(ref calls) > 256;
    public string Instructions => baseline.Instructions + WorkflowInstructions;
    public string WorkflowInstructions { get; init; } = "";
    public string BridgeExecutable { get; }
    // One frame must hold a whole tool result after wire escaping: the largest tool page is 128,000 characters of
    // record JSON, quoted once more for the wire. Non-ASCII text is sent as UTF-8 rather than \u escapes.
    public const int FrameLimit = 1048576;
    private static readonly JsonSerializerOptions Wire = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public CollaborationMcpHost(ICollaborationTools tools, Agent agent, string bridgeExecutable, CancellationToken token, string? dispatchId = null)
    {
        if (!Path.IsPathFullyQualified(bridgeExecutable) || !File.Exists(bridgeExecutable))
            throw new FileNotFoundException("The collaboration MCP bridge executable is unavailable.");
        baseline = tools; this.tools = tools; this.agent = agent; BridgeExecutable = bridgeExecutable;
        this.dispatchId = dispatchId ?? Guid.NewGuid().ToString("N");
        life = CancellationTokenSource.CreateLinkedTokenSource(token);
        serving = ServeAsync();
    }
    /// <summary>Routes this session's tool calls to the given dispatch and resets the per-dispatch call and repair budget.</summary>
    public void Attach(ICollaborationTools dispatch, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A dispatch ID is required.");
        dispatchId = id; tools = dispatch;
        Interlocked.Exchange(ref calls, 0); Interlocked.Exchange(ref repairs, 0);
    }
    /// <summary>Between turns the resident session keeps its pipe but has no dispatch; tool calls are refused, not counted.</summary>
    public void Detach() => tools = null;
    /// <summary>
    /// Binds the provider's native session. A different id later means the CLI started a new session (a failed resume);
    /// the phase continues with the new session and the replaced id is returned so the adapter can say so. Never throws
    /// on the provider's reader thread.
    /// </summary>
    public string? BindSession(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A native session ID is required.");
        var previous = Interlocked.Exchange(ref sessionId, id);
        return previous is not null && previous != id ? previous : null;
    }
    internal Dictionary<string, string> EnvironmentVariables() => new()
    { ["AIHUB_COLLAB_PIPE"] = pipeName, ["AIHUB_COLLAB_TOKEN"] = secret };
    internal string[] CodexArguments() => ["-c", "mcp_servers.ai_hub=" +
        "{command=" + JsonSerializer.Serialize(BridgeExecutable) + ",args=[],env_vars=[\"AIHUB_COLLAB_PIPE\",\"AIHUB_COLLAB_TOKEN\"],required=true,enabled=true,default_tools_approval_mode=\"approve\"}"];
    internal string ClaudeConfiguration() => JsonSerializer.Serialize(new { mcpServers = new
    { ai_hub = new { type = "stdio", command = BridgeExecutable, args = Array.Empty<string>() } } });
    internal static bool IsTool(string name) => name is "mcp__ai_hub__get_task_context" or "mcp__ai_hub__submit_message" or "mcp__ai_hub__get_messages" or "mcp__ai_hub__get_evidence" or "mcp__ai_hub__mark_addressed" or "mcp__ai_hub__get_shared_context" or "mcp__ai_hub__publish_context" or "mcp__ai_hub__get_context_records" or "mcp__ai_hub__read_context_source" or "mcp__ai_hub__read_context_record" or "mcp__ai_hub__claim_work" or "mcp__ai_hub__complete_work" or "mcp__ai_hub__get_work" or "mcp__ai_hub__get_events";
    private async Task ServeAsync()
    {
        using var slots = new SemaphoreSlim(4, 4);
        var connections = new List<Task>();
        try
        {
            while (!life.IsCancellationRequested)
            {
                await slots.WaitAsync(life.Token).ConfigureAwait(false);
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { slots.Release(); await Task.Delay(100, life.Token).ConfigureAwait(false); continue; } // Keep listening; the bridge retries its connection.
                try { await pipe.WaitForConnectionAsync(life.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { pipe.Dispose(); slots.Release(); throw; }
                catch (Exception) { pipe.Dispose(); slots.Release(); await Task.Delay(100, life.Token).ConfigureAwait(false); continue; }
                connections.RemoveAll(t => t.IsCompletedSuccessfully);
                connections.Add(ServeOwned(pipe));
            }
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { }
        finally { await life.CancelAsync().ConfigureAwait(false); await Task.WhenAll(connections).ConfigureAwait(false); }
        async Task ServeOwned(NamedPipeServerStream pipe)
        {
            await using (pipe)
            {
                // Transport failures (handshake timeouts, dropped pipes) are not the agent's fault and do not count as repairs.
                try { await ServeConnectionAsync(pipe).ConfigureAwait(false); }
                catch (Exception) { }
                finally
                {
                    // Disconnect each instance explicitly before closing it so every bridge observes EOF,
                    // including when another instance of this pipe name remains connected.
                    try { if (pipe.IsConnected) pipe.Disconnect(); } catch (IOException) { }
                    slots.Release();
                }
            }
        }
    }
    private async Task ServeConnectionAsync(Stream pipe)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await using var lines = BoundedText.LinesAsync(reader, FrameLimit, life.Token).GetAsyncEnumerator();
        if (!await lines.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), life.Token).ConfigureAwait(false)) return;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(lines.Current), Encoding.UTF8.GetBytes(secret))) return;
        await writer.WriteLineAsync("OK".AsMemory(), life.Token).ConfigureAwait(false);
        var initialized = false; var ready = false;
        using var gate = new SemaphoreSlim(1, 1); var connectionId = Interlocked.Increment(ref connectionIds);
        async Task Write(string line)
        {
            await gate.WaitAsync(life.Token).ConfigureAwait(false);
            try { await writer.WriteLineAsync(line.AsMemory(), life.Token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        try
        {
        while (await lines.MoveNextAsync().ConfigureAwait(false))
        {
            JsonObject? request;
            try
            {
                using var document = JsonDocument.Parse(lines.Current, new JsonDocumentOptions { MaxDepth = 24 });
                CollaborationContract.RejectDuplicateKeys(document.RootElement);
                request = JsonNode.Parse(lines.Current) as JsonObject;
            }
            catch (Exception ex) when (ex is JsonException or CollaborationValidationException)
            { await WriteError(null, -32700, "Invalid or duplicate-key JSON."); continue; }
            var id = request?["id"];
            if (request is null || request.Str("jsonrpc") != "2.0" || request["method"] is not JsonValue methodValue ||
                !methodValue.TryGetValue<string>(out _) || id is JsonArray or JsonObject ||
                (request["params"] is { } parameters && parameters is not JsonObject))
            { await WriteError(id, -32600, "Invalid JSON-RPC request."); continue; }
            var method = request.Str("method");
            if (id is null)
            {
                if (method == "notifications/initialized" && initialized && !ready) { ready = true; live[connectionId] = (writer, gate); }
                continue;
            }
            JsonNode? result = null;
            if (method == "initialize" && !initialized)
            {
                initialized = true;
                result = JsonSerializer.SerializeToNode(new
                {
                    protocolVersion = "2025-03-26", capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "AI Hub collaboration", version = "0.2.0" },
                    instructions = Instructions
                });
            }
            else if (method == "ping") result = new JsonObject();
            else if (!ready) { await WriteError(id, -32600, "Initialize this connection first."); continue; }
            else if (method == "tools/list") result = new JsonObject { ["tools"] = baseline.Definitions };
            else if (method == "tools/call")
            {
                var p = request["params"];
                result = InvokeTool(p.Str("name"), p?["arguments"]);
            }
            else { await WriteError(id, -32601, "Unsupported method."); continue; }
            await Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString(Wire)).ConfigureAwait(false);
        }
        }
        finally { live.TryRemove(connectionId, out _); }
        async Task WriteError(JsonNode? id, int code, string message) => await Write(new JsonObject
        { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = JsonSerializer.SerializeToNode(new { code, message }) }.ToJsonString(Wire)).ConfigureAwait(false);
    }
    internal JsonNode InvokeTool(string name, JsonNode? arguments)
    {
        life.Token.ThrowIfCancellationRequested();
        var current = tools;
        if (current is null) return ToolResult("No active dispatch is attached to this session. Wait for the host's next turn before calling tools.", true);
        try
        {
            if (Interlocked.Increment(ref calls) > 256 || Volatile.Read(ref repairs) >= 3)
                throw new CollaborationValidationException("Dispatch tool/repair limit reached; start a new dispatch.");
            if (sessionId is null) throw new CollaborationValidationException("Native provider session has not been bound.");
            return ToolResult(current.Call(agent, dispatchId, sessionId, name, arguments, life.Token).ToJsonString(Wire), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only an invalid structured submission spends the repair budget; argument mistakes on read tools just return an error.
            if (name == "submit_message" && ex is CollaborationValidationException) Interlocked.Increment(ref repairs);
            return ToolResult(ex.Message, true);
        }
    }
    private static JsonNode ToolResult(string text, bool error) => JsonSerializer.SerializeToNode(new
    { content = new[] { new { type = "text", text } }, isError = error })!;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await life.CancelAsync();
        await serving.ConfigureAwait(false);
        life.Dispose();
    }
}
