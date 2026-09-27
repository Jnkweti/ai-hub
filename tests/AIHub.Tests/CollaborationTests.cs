using AIHub.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CollaborationTests
{
    internal static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    internal static string Bridge => Path.Combine(Root, "src/AIHub.McpBridge/bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "AIHub.McpBridge.exe");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static JsonObject Submission(string type = "status", string key = "probe-1")
    {
        var content = new JsonObject
        {
            ["type"] = type, ["summary"] = "Checked task context.",
            ["scope"] = new JsonObject { ["files"] = new JsonArray("src/main.cs"), ["focus"] = new JsonArray("Cancellation") },
            ["evidence_refs"] = new JsonArray(), ["blockers"] = new JsonArray()
        };
        if (type == "status") content["status"] = "assignment_complete";
        else content["recipient"] = "Claude";
        if (type is "handoff" or "review_request" or "question") content["requested_action"] = "Check cancellation handling.";
        if (type == "review_result") { content["reply_to"] = "request-1"; content["findings"] = new JsonArray(); }
        return new() { ["schema_version"] = "1.0", ["idempotency_key"] = key, ["content"] = content };
    }
    internal static IEnumerable<(string Name, JsonObject Input, bool Valid)> Corpus()
    {
        foreach (var type in new[] { "status", "handoff", "review_request", "review_result", "question" }) yield return (type, Submission(type), true);
        yield return ("context-request", SharedContextTests.Request(), true);
        var invalidContext = SharedContextTests.Request(); invalidContext["content"]!["assignments"]![0]!["scope"]!["files"] = new JsonArray("../outside");
        yield return ("context-escaping-path", invalidContext, false);
        invalidContext = SharedContextTests.Request(); invalidContext["content"]!["recipient"] = "Claude";
        yield return ("context-recipient", invalidContext, false);
        var unicode = Submission(); unicode["content"]!["summary"] = string.Concat(Enumerable.Repeat("😀", 2000)); yield return ("unicode-length", unicode, true);
        var finding = Submission("review_result");
        finding["content"]!["findings"] = new JsonArray(JsonSerializer.SerializeToNode(new CollaborationFinding("f1", "high", "src/main.cs", 12,
            "Cancellation may publish a stale result.", "open", []), CollaborationContract.JsonOptions));
        yield return ("finding", finding, true);
        var decimalLine = finding.DeepClone().AsObject(); decimalLine["content"]!["findings"]![0]!["line"] = 12.0m;
        yield return ("integer-written-as-decimal", decimalLine, true);
        var mutations = new (string Name, Action<JsonObject> Change)[]
        {
            ("unknown-root", n => n["sender"] = "Codex"),
            ("unsupported-version", n => n["schema_version"] = "2.0"),
            ("missing-key", n => n.Remove("idempotency_key")),
            ("null-summary", n => n["content"]!["summary"] = null),
            ("blank-summary", n => n["content"]!["summary"] = "  "),
            ("oversize-summary", n => n["content"]!["summary"] = new string('x', 2001)),
            ("unknown-type", n => n["content"]!["type"] = "approve_everything"),
            ("unknown-status", n => n["content"]!["status"] = "verified_complete"),
            ("contradictory-routing", n => n["content"]!["recipient"] = "Claude"),
            ("nested-authority", n => n["content"]!["scope"]!["allow_edits"] = true),
            ("parent-path", n => n["content"]!["scope"]!["files"] = new JsonArray("../secret.txt")),
            ("absolute-path", n => n["content"]!["scope"]!["files"] = new JsonArray("C:/secret.txt")),
            ("oversize-array", n => n["content"]!["blockers"] = new JsonArray(Enumerable.Range(0, 17).Select(_ => (JsonNode?)JsonValue.Create("blocked")).ToArray()))
        };
        foreach (var mutation in mutations) { var n = Submission(); mutation.Change(n); yield return (mutation.Name, n, false); }
        var missing = Submission("review_request"); missing["content"]!.AsObject().Remove("requested_action"); yield return ("missing-action", missing, false);
        var badFinding = finding.DeepClone().AsObject(); badFinding["content"]!["findings"]![0]!["line"] = 1.5; yield return ("fractional-line", badFinding, false);
    }
    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "submission.schema.json"), CollaborationContract.SubmissionSchema().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.WriteAllText(Path.Combine(directory, "contract-fixtures.json"), JsonSerializer.Serialize(Corpus().Select(c => new { name = c.Name, input = c.Input, valid = c.Valid }), new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
    private static void Reject(Action action)
    { try { action(); } catch (CollaborationValidationException) { return; } throw new Exception("Invalid collaboration operation was accepted"); }
    internal sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Root, "artifacts", "collaboration-test-" + Guid.NewGuid().ToString("N"));
        public TaskMemory Memory { get; }
        public TaskClaim Claim { get; }
        public CollaborationProbe Probe { get; }
        public Fixture()
        {
            Memory = new(new LocalStore(DirectoryPath));
            Claim = Memory.Begin(Memory.Create("test-room", DirectoryPath, "Check isolated collaboration task"), false);
            Memory.Own(Claim, Agent.Codex); Probe = new(Memory, Claim);
        }
        public void Dispose()
        {
            Memory.End(Claim, WorkState.Stopped, "Fixture complete");
            // This path is a newly created, fixed-prefix fixture inside this workspace.
            Directory.Delete(DirectoryPath, true);
        }
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("collaboration schema covers six types and rejects invalid payloads", () =>
        {
            foreach (var c in Corpus())
            {
                if (c.Valid) Check(CollaborationContract.Parse(c.Input.ToJsonString()).Content.Type == c.Input["content"]!.Str("type"), c.Name);
                else Reject(() => CollaborationContract.Parse(c.Input.ToJsonString()));
            }
            Reject(() => CollaborationContract.Parse("{\"schema_version\":\"1.0\",\"schema_version\":\"1.0\"}"));
            Reject(() => CollaborationContract.Parse(new string('x', CollaborationContract.MaxBytes + 1)));
            Reject(() => CollaborationContract.Parse(new string('[', 20) + new string(']', 20)));
            var published = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "docs/collaboration/submission.schema.json")));
            Check(JsonNode.DeepEquals(published, CollaborationContract.SubmissionSchema()), "Published schema drifted from runtime");
            return Task.CompletedTask;
        });
        await test("collaboration delivery transitions cannot revive terminal messages", () =>
        {
            Check(CollaborationContract.CanTransition(DeliveryState.Accepted, DeliveryState.Pending), "Cannot queue accepted message");
            Check(CollaborationContract.CanTransition(DeliveryState.Pending, DeliveryState.Delivered), "Cannot deliver queued message");
            Check(CollaborationContract.CanTransition(DeliveryState.Delivered, DeliveryState.Answered), "Cannot answer delivered message");
            foreach (var state in new[] { DeliveryState.Answered, DeliveryState.Canceled, DeliveryState.Interrupted })
                foreach (var next in Enum.GetValues<DeliveryState>()) Check(!CollaborationContract.CanTransition(state, next), "Terminal state revived");
            Check(!CollaborationContract.CanTransition(DeliveryState.Accepted, DeliveryState.Answered), "Skipped delivery");
            return Task.CompletedTask;
        });
        await test("collaboration probe binds identity and rejects changed retries and invented evidence", () =>
        {
            using var f = new Fixture(); var n = Submission();
            var receipt = f.Probe.Call(Agent.Codex, "dispatch-a", "session-a", "submit_message", n, default);
            var retry = f.Probe.Call(Agent.Codex, "dispatch-a", "session-a", "submit_message", n, default);
            Check(receipt.Str("message_id") == retry.Str("message_id") && retry.Bool("duplicate") && f.Probe.Messages.Length == 1, "Retry duplicated message");
            Check(f.Probe.Messages[0].Envelope.Sender == Agent.Codex && !receipt.Bool("persistent"), "Incorrect authority/durability");
            n["content"]!["summary"] = "Changed";
            Reject(() => f.Probe.Call(Agent.Codex, "dispatch-a", "session-a", "submit_message", n, default));
            n = Submission(key: "evidence"); n["content"]!["evidence_refs"] = new JsonArray("invented-result");
            Reject(() => f.Probe.Call(Agent.Codex, "dispatch-a", "session-a", "submit_message", n, default));
            n = Submission("review_result", "cross-task");
            Reject(() => f.Probe.Call(Agent.Codex, "dispatch-a", "session-a", "submit_message", n, default));
            Check(f.Probe.Messages.Length == 1, "Rejection mutated messages");
            return Task.CompletedTask;
        });
        await test("collaboration probe rejects stale ownership and canceled dispatches", () =>
        {
            using var f = new Fixture(); var n = Submission();
            f.Memory.Own(f.Claim, Agent.Claude);
            Reject(() => f.Probe.Call(Agent.Codex, "d", "s", "submit_message", n, default));
            f.Memory.Own(f.Claim, Agent.Codex);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { f.Probe.Call(Agent.Codex, "d", "s", "submit_message", n, canceled.Token); throw new Exception("Canceled call accepted"); }
            catch (OperationCanceledException) { }
            f.Memory.End(f.Claim, WorkState.Stopped, "Stopped");
            var next = f.Memory.Begin(f.Claim.TaskId, false); f.Memory.Own(next, Agent.Codex);
            Reject(() => f.Probe.Call(Agent.Codex, "d", "s", "submit_message", n, default));
            f.Memory.End(next, WorkState.Stopped, "Done");
            Check(f.Probe.Messages.Length == 0, "Stale call published"); return Task.CompletedTask;
        });
        await test("collaboration probe validates peer targets, references, paths and immutable reads", () =>
        {
            using var f = new Fixture();
            var request = Submission("review_request");
            var receipt = f.Probe.Call(Agent.Codex, "codex-dispatch", "codex-session", "submit_message", request, default);
            var copy = f.Probe.Messages; copy[0].Content.Scope.Files[0] = "tampered.txt";
            Check(f.Probe.Messages[0].Content.Scope.Files[0] == "src/main.cs", "Caller mutated host storage");
            var self = Submission("handoff", "self"); self["content"]!["recipient"] = "Codex";
            Reject(() => f.Probe.Call(Agent.Codex, "d", "s", "submit_message", self, default));
            var alias = Submission(key: "alias"); alias["content"]!["scope"]!["files"] = new JsonArray("src/NUL.txt");
            Reject(() => f.Probe.Call(Agent.Codex, "d", "s", "submit_message", alias, default));
            f.Memory.Own(f.Claim, Agent.Claude);
            var review = Submission("review_result", "review"); review["content"]!["recipient"] = "Codex";
            review["content"]!["reply_to"] = receipt.Str("message_id");
            f.Probe.Call(Agent.Claude, "claude-dispatch", "claude-session", "submit_message", review, default);
            Check(f.Probe.Messages.Length == 2 && f.Probe.Messages[1].Envelope.Recipient == Agent.Codex, "Valid peer reply rejected");
            var args = new JsonObject { ["task_id"] = "different-task" };
            Reject(() => f.Probe.Call(Agent.Claude, "d", "s", "get_task_context", args, default));
            return Task.CompletedTask;
        });
        await test("collaboration MCP bridge exposes contract, calls host, and revokes on stop", async () =>
        {
            using var f = new Fixture(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var host = new CollaborationMcpHost(f.Probe, Agent.Codex, Bridge, timeout.Token); host.BindSession("test-session");
            await using var rpc = await ProbeWire.Connect(host, f.DirectoryPath, timeout.Token);
            var tools = await rpc.Call("tools/list", new JsonObject(), timeout.Token);
            Check(tools["tools"]!.AsArray().Count == 2, "Wrong probe tools");
            var schema = tools["tools"]![1]!["inputSchema"];
            Check(JsonNode.DeepEquals(schema, CollaborationContract.SubmissionSchema()), "Exposed schema drifted");
            try { await rpc.Call("tools/call", new JsonArray(), timeout.Token); throw new Exception("Invalid RPC params accepted"); }
            catch (IOException) { }
            var result = await rpc.Tool("get_task_context", new JsonObject(), timeout.Token);
            Check(result.Str("task_id") == f.Claim.TaskId && result.Str("sender") == "Codex", "Wrong task identity");
            await rpc.Tool("submit_message", Submission(), timeout.Token);
            Check(f.Probe.Messages.Length == 1, "Host did not receive bridge call");
            for (var i = 0; i < 3; i++)
            {
                var rejected = await rpc.Call("tools/call", new JsonObject { ["name"] = "submit_message", ["arguments"] = new JsonObject() }, timeout.Token);
                Check(rejected.Bool("isError"), "Malformed submission accepted");
            }
            var capped = await rpc.Call("tools/call", new JsonObject { ["name"] = "submit_message", ["arguments"] = Submission(key: "after-cap") }, timeout.Token);
            Check(capped.Bool("isError") && f.Probe.Messages.Length == 1, "Repair budget bypassed");
            await host.DisposeAsync();
            Check(await rpc.Exited.WaitAsync(TimeSpan.FromSeconds(5)), "Bridge survived revoked host");
        });
        await test("collaboration bridge rejects wrong credentials and unbound native sessions", async () =>
        {
            using var f = new Fixture(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var host = new CollaborationMcpHost(f.Probe, Agent.Codex, Bridge, timeout.Token);
            try { await using var bad = await ProbeWire.Connect(host, f.DirectoryPath, timeout.Token, wrongCredential: true); throw new Exception("Wrong credential accepted"); }
            catch (IOException) { }
            await using var rpc = await ProbeWire.Connect(host, f.DirectoryPath, timeout.Token);
            var response = await rpc.Call("tools/call", new JsonObject { ["name"] = "get_task_context", ["arguments"] = new JsonObject() }, timeout.Token);
            Check(response.Bool("isError") && f.Probe.ContextReads == 0, "Unbound session read task context");
            host.BindSession("native-1");
            try { host.BindSession("native-2"); throw new Exception("Session identity changed"); } catch (InvalidOperationException) { }
            var context = await rpc.Tool("get_task_context", new JsonObject(), timeout.Token);
            Check(context.Str("task_id") == f.Claim.TaskId, "Valid connection failed after unauthorized attempt");
            var env = host.EnvironmentVariables();
            Check(!string.Join(" ", host.CodexArguments()).Contains(env["AIHUB_COLLAB_TOKEN"]) &&
                !host.ClaudeConfiguration().Contains(env["AIHUB_COLLAB_TOKEN"]), "Credential was embedded in CLI arguments");
        });
    }
}

internal sealed class ProbeWire : IAsyncDisposable
{
    private readonly System.Diagnostics.Process process;
    private int sequence;
    public Task<bool> Exited { get; }
    private ProbeWire(System.Diagnostics.Process process) { this.process = process; Exited = Wait(); }
    private async Task<bool> Wait() { await process.WaitForExitAsync(); return true; }
    public static async Task<ProbeWire> Connect(CollaborationMcpHost host, string cwd, CancellationToken token, bool wrongCredential = false)
    {
        var info = new System.Diagnostics.ProcessStartInfo(host.BridgeExecutable)
        { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var pair in host.EnvironmentVariables()) info.Environment[pair.Key] = pair.Value;
        if (wrongCredential) info.Environment["AIHUB_COLLAB_TOKEN"] = "wrong-credential";
        var wire = new ProbeWire(System.Diagnostics.Process.Start(info)!);
        try
        {
            await wire.Call("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" } }, token);
            await wire.process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await wire.process.StandardInput.FlushAsync(token);
            return wire;
        }
        catch { await wire.DisposeAsync(); throw; }
    }
    public async Task<JsonNode> Call(string method, JsonNode args, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = method, ["params"] = args }.ToJsonString());
        await process.StandardInput.FlushAsync(token);
        var line = await process.StandardOutput.ReadLineAsync(token);
        if (line is null) throw new IOException("Bridge exited: " + await process.StandardError.ReadToEndAsync(token));
        var response = JsonNode.Parse(line)!;
        if (response["error"] is { } error) throw new IOException(error.ToJsonString());
        return response["result"]!;
    }
    public async Task<JsonNode> Tool(string name, JsonNode input, CancellationToken token)
    {
        var result = await Call("tools/call", new JsonObject { ["name"] = name, ["arguments"] = input }, token);
        var text = result["content"]![0]!.Str("text");
        if (result.Bool("isError")) throw new IOException(text);
        return JsonNode.Parse(text)!;
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(); process.Dispose();
    }
}
