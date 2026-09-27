using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Core;

/// <summary>A revocable, provider/dispatch-bound MCP connection; all tools execute in the host process.</summary>
public sealed class CollaborationMcpHost : IAsyncDisposable
{
    private readonly CancellationTokenSource life;
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string pipeName = "aihub-collaboration-" + Guid.NewGuid().ToString("N");
    private readonly ICollaborationTools tools;
    private readonly Agent agent;
    private readonly Task serving;
    private string? sessionId;
    private int calls, repairs;
    private int disposed;
    public string DispatchId { get; }
    public bool StartFreshSession { get; init; }
    public string? ResumeSessionId { get; init; }
    public bool LimitReached => Volatile.Read(ref repairs) >= 3 || Volatile.Read(ref calls) > 256;
    public string Instructions => tools.Instructions + WorkflowInstructions;
    public string WorkflowInstructions { get; init; } = "";
    public string BridgeExecutable { get; }
    public const int FrameLimit = 262144;
    public CollaborationMcpHost(ICollaborationTools tools, Agent agent, string bridgeExecutable, CancellationToken token, string? dispatchId = null)
    {
        if (!Path.IsPathFullyQualified(bridgeExecutable) || !File.Exists(bridgeExecutable))
            throw new FileNotFoundException("The collaboration MCP bridge executable is unavailable.");
        this.tools = tools; this.agent = agent; BridgeExecutable = bridgeExecutable;
        DispatchId = dispatchId ?? Guid.NewGuid().ToString("N");
        life = CancellationTokenSource.CreateLinkedTokenSource(token);
        serving = ServeAsync();
    }
    public void BindSession(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A native session ID is required.");
        var previous = Interlocked.CompareExchange(ref sessionId, id, null);
        if (previous is not null && previous != id) throw new InvalidOperationException("Connection belongs to a different provider session.");
    }
    internal Dictionary<string, string> EnvironmentVariables() => new()
    { ["AIHUB_COLLAB_PIPE"] = pipeName, ["AIHUB_COLLAB_TOKEN"] = secret };
    internal string[] CodexArguments() => ["-c", "mcp_servers.ai_hub=" +
        "{command=" + JsonSerializer.Serialize(BridgeExecutable) + ",args=[],env_vars=[\"AIHUB_COLLAB_PIPE\",\"AIHUB_COLLAB_TOKEN\"],required=true,enabled=true,default_tools_approval_mode=\"approve\"}"];
    internal string ClaudeConfiguration() => JsonSerializer.Serialize(new { mcpServers = new
    { ai_hub = new { type = "stdio", command = BridgeExecutable, args = Array.Empty<string>() } } });
    internal static bool IsTool(string name) => name is "mcp__ai_hub__get_task_context" or "mcp__ai_hub__submit_message" or "mcp__ai_hub__get_messages" or "mcp__ai_hub__get_evidence" or "mcp__ai_hub__mark_addressed" or "mcp__ai_hub__get_shared_context" or "mcp__ai_hub__publish_context" or "mcp__ai_hub__get_context_records" or "mcp__ai_hub__read_context_source" or "mcp__ai_hub__read_context_record" or "mcp__ai_hub__claim_work" or "mcp__ai_hub__complete_work" or "mcp__ai_hub__get_work";
    private async Task ServeAsync()
    {
        using var slots = new SemaphoreSlim(4, 4);
        var connections = new List<Task>();
        try
        {
            while (!life.IsCancellationRequested)
            {
                await slots.WaitAsync(life.Token).ConfigureAwait(false);
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(life.Token).ConfigureAwait(false); }
                catch { pipe.Dispose(); slots.Release(); throw; }
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
                try { await ServeConnectionAsync(pipe).ConfigureAwait(false); }
                catch (Exception) { if (!life.IsCancellationRequested) Interlocked.Increment(ref repairs); }
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
            { if (method == "notifications/initialized" && initialized) ready = true; continue; }
            JsonNode? result = null;
            if (method == "initialize" && !initialized)
            {
                initialized = true;
                result = JsonSerializer.SerializeToNode(new
                {
                    protocolVersion = "2025-03-26", capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "AI Hub collaboration", version = "0.2.0" },
                    instructions = tools.Instructions
                });
            }
            else if (method == "ping") result = new JsonObject();
            else if (!ready) { await WriteError(id, -32600, "Initialize this connection first."); continue; }
            else if (method == "tools/list") result = new JsonObject { ["tools"] = tools.Definitions };
            else if (method == "tools/call")
            {
                var p = request["params"];
                result = InvokeTool(p.Str("name"), p?["arguments"]);
            }
            else { await WriteError(id, -32601, "Unsupported method."); continue; }
            await writer.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString().AsMemory(), life.Token).ConfigureAwait(false);
        }
        async Task WriteError(JsonNode? id, int code, string message) => await writer.WriteLineAsync(new JsonObject
        { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = JsonSerializer.SerializeToNode(new { code, message }) }.ToJsonString().AsMemory(), life.Token).ConfigureAwait(false);
    }
    internal JsonNode InvokeTool(string name, JsonNode? arguments)
    {
        life.Token.ThrowIfCancellationRequested();
        try
        {
            if (Interlocked.Increment(ref calls) > 256 || Volatile.Read(ref repairs) >= 3)
                throw new CollaborationValidationException("Dispatch tool/repair limit reached; start a new dispatch.");
            if (sessionId is null) throw new CollaborationValidationException("Native provider session has not been bound.");
            return ToolResult(tools.Call(agent, DispatchId, sessionId, name, arguments, life.Token).ToJsonString(), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Interlocked.Increment(ref repairs); return ToolResult(ex.Message, true); }
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
