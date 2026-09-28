using AIHub.Core;
using System.Text.Json.Nodes;

/// <summary>Regression cases for the 0.15.0 hardening of the design-review findings.</summary>
internal static class HardeningTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("a failed final task write releases the claim so the next send is not refused", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Memory.Begin(f.TaskId, false);
            using (new FileStream(Path.Combine(f.Root, "tasks.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Exception? failure = null;
                try { f.Memory.End(claim, WorkState.Ready, "done"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
                var after = f.Memory.Get(f.TaskId)!;
                Check(failure is not null && after.State == WorkState.Ready && after.Owner.Length == 0, $"End did not fail or left the task running: failure={failure?.GetType().Name}, state={after.State}, owner='{after.Owner}'");
            }
            var again = f.Memory.Begin(f.TaskId, false); // Would throw "This task still owns a worker" if the claim leaked.
            f.Memory.End(again, WorkState.Ready, "done");
            return Task.CompletedTask;
        });
        await test("only invalid structured submissions spend the repair budget", async () =>
        {
            using var f = new CollaborationTests.Fixture(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var host = new CollaborationMcpHost(f.Probe, Agent.Codex, CollaborationTests.Bridge, timeout.Token); host.BindSession("test-session");
            for (var i = 0; i < 5; i++) Check(host.InvokeTool("get_task_context", new JsonObject { ["unexpected"] = 1 }).Bool("isError"), "Bad arguments accepted");
            for (var i = 0; i < 5; i++) Check(host.InvokeTool("no_such_tool", new JsonObject()).Bool("isError"), "Unknown tool accepted");
            Check(!host.LimitReached, "Read-tool argument errors counted as repairs");
            for (var i = 0; i < 3; i++) host.InvokeTool("submit_message", new JsonObject());
            Check(host.LimitReached, "Invalid submissions did not reach the repair limit");
        });
        await test("input manifests are pruned oldest-first across generations and old prompt text is stripped", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture();
            var common = new CommonContext(0, new string('a', 64), "core", [], [], 4);
            for (var generation = 0; generation < 3; generation++)
            {
                var claim = f.Memory.Begin(f.TaskId, false);
                for (var i = 0; i < 50; i++) f.Store.PrepareInput(claim, Agent.Codex, Guid.NewGuid().ToString("N"), common, $"prompt {generation}-{i}");
                f.Memory.End(claim, WorkState.Ready, "done");
            }
            var inputs = f.Store.Read(f.TaskId).ContextInputs;
            Check(inputs.Count == CollaborationStore.MaxInputs, $"Manifest cap not enforced by eviction: {inputs.Count}");
            // 150 manifests over three generations: the 22 evicted all come from the oldest finished generation.
            Check(inputs.Count(i => i.Generation == 1) == 28 && inputs.Count(i => i.Generation == 2) == 50 && inputs.Count(i => i.Generation == 3) == 50, "Oldest generation was not evicted first");
            Check(inputs.Count(i => i.PromptRetained) == CollaborationStore.RetainedPrompts && inputs.TakeLast(CollaborationStore.RetainedPrompts).All(i => i.PromptRetained && i.Prompt.Length > 0), "Prompt text retention wrong");
            Check(inputs.Where(i => !i.PromptRetained).All(i => i.Prompt.Length == 0 && i.PromptHash.Length == 64 && i.InputBytes > 0), "Stripped manifests lost their hash or size");
            var reloaded = new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.TaskId);
            Check(reloaded.ContextInputs.Count == CollaborationStore.MaxInputs, "Pruned ledger did not reload");
            return Task.CompletedTask;
        });
        await test("evidence capture runs off the event thread and is drained before the terminal commit", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var observed = new List<string>();
            f.Store.BeforeSnapshotCapture = () => { lock (observed) observed.Add(Environment.CurrentManagedThreadId.ToString()); };
            await using var hub = f.Hub((agent, host, turn, _, _) =>
            {
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message());
                return Task.FromResult("Ran a check " + turn);
            });
            StructuredClientEvents.Hook = client =>
            {
                client.Emit(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd-1", "{\"type\":\"commandExecution\",\"status\":\"inProgress\",\"command\":\"dotnet test\"}"));
                client.Emit(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd-1", "{\"type\":\"commandExecution\",\"status\":\"completed\",\"command\":\"dotnet test\",\"exitCode\":0,\"aggregatedOutput\":\"ok\"}"));
            };
            try { await hub.SubmitAsync("Run the checks", "Codex"); await f.Finished(); }
            finally { StructuredClientEvents.Hook = null; }
            var doc = f.Store.Read(f.TaskId);
            var evidence = doc.Evidence.SingleOrDefault(e => e.SourceEventId == "cmd-1");
            Check(evidence is { Finished: true, ExitCode: 0 } && doc.Events.Any(e => e.Kind == "tool" && e.Text.Contains("dotnet test")), "Evidence was not recorded before the commit");
            Check(f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Turn did not finish: " + f.Memory.Get(f.TaskId)!.Reason);
        });
        // 0.16.0: the remaining design-review findings.
        await test("oversized files are fingerprinted by size and time so a large asset never blocks reviews", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ah-snap-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "small.txt"), "small");
                using (var big = new FileStream(Path.Combine(root, "asset.bin"), FileMode.CreateNew)) big.SetLength(9L * 1024 * 1024);
                var first = await ProjectSnapshot.CaptureAsync(root, default);
                Check(first.Reusable && first.Limitation.Length == 0 && first.Files["asset.bin"].StartsWith("stat:"), $"A large asset made the snapshot incomplete: '{first.Limitation}' {first.Files.GetValueOrDefault("asset.bin")}");
                await Task.Delay(20);
                using (var big = new FileStream(Path.Combine(root, "asset.bin"), FileMode.Open)) { big.Seek(0, SeekOrigin.End); big.WriteByte(1); }
                var second = await ProjectSnapshot.CaptureAsync(root, default);
                Check(second.Reusable && second.Fingerprint != first.Fingerprint, "A change to the large asset went undetected");
            }
            finally { Directory.Delete(root, true); }
        });
        await test("a re-keyed native session is reported to the phase instead of killing the provider", async () =>
        {
            using var f = new CollaborationTests.Fixture(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var host = new CollaborationMcpHost(f.Probe, Agent.Codex, CollaborationTests.Bridge, timeout.Token);
            Check(host.BindSession("session-a") is null && host.BindSession("session-a") is null, "Rebinding the same session was reported as a change");
            Check(host.BindSession("session-b") == "session-a", "A new native session was not reported");
            Check(!host.InvokeTool("get_task_context", new JsonObject()).Bool("isError"), "Tools stopped working after the session re-keyed");
        });
        await test("preparation ignores plan updates and approval notices but stops on real tool use", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Memory.Begin(f.TaskId, false);
            var common = new CommonContext(0, new string('a', 64), "core", [], [], 4);
            async Task<PreparedContribution?> Prepare(params AgentEvent[] events)
            {
                await using var preparation = new ConversationPreparation(f.Store, claim, Agent.Claude, common, "prompt",
                    _ => new ScriptedAgent(Agent.Claude, events, "tentative notes"), _ => { }, CancellationToken.None);
                return await preparation.Completion;
            }
            var benign = await Prepare(new AgentEvent(Agent.Claude, EventKind.Tool, "Plan updated", Detail: "{}"), new AgentEvent(Agent.Claude, EventKind.Tool, "Action declined", Detail: "{}"));
            Check(benign?.Notes == "tentative notes", "Benign tool notices discarded the preparation");
            var executed = await Prepare(new AgentEvent(Agent.Claude, EventKind.Tool, "Bash", "toolu_1", "{\"command\":\"ls\"}"));
            Check(executed is null, "Real tool execution was accepted in a preparation");
            f.Memory.End(claim, WorkState.Ready, "done");
        });
        await test("check reuse depends only on the curated environment the operation names", () =>
        {
            var noise = "AIHUB_TEST_NOISE_" + Guid.NewGuid().ToString("N")[..8];
            var before = CollaborationStore.WorkEnvironment("dotnet test");
            Environment.SetEnvironmentVariable(noise, "changed");
            try
            {
                Check(CollaborationStore.WorkEnvironment("dotnet test") == before, "An unrelated environment variable changed the check environment");
                var named = CollaborationStore.WorkEnvironment("echo $env:" + noise);
                Environment.SetEnvironmentVariable(noise, "changed again");
                Check(CollaborationStore.WorkEnvironment("echo $env:" + noise) != named, "A variable the operation names was ignored");
            }
            finally { Environment.SetEnvironmentVariable(noise, null); }
            return Task.CompletedTask;
        });
        await test("context records archive the oldest agent replies at the cap and archived entries are not re-imported", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Memory.Begin(f.TaskId, false);
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            var conversation = Enumerable.Range(0, 1100).Select(i => new ConversationEntry("m" + i, i % 2 == 0 ? "You" : "Codex", "message " + i, "Both", start.AddSeconds(i))).ToArray();
            f.Store.SynchronizeContext(claim, conversation, "message 1098");
            var doc = f.Store.Read(f.TaskId);
            Check(doc.ContextRecords.Count == CollaborationStore.MaxRecords && doc.ArchivedRecords == 76, $"Cap not held by archiving: {doc.ContextRecords.Count} records, {doc.ArchivedRecords} archived");
            Check(doc.ContextRecords.Count(r => r.Kind == "user_message") == 550 && doc.ContextRecords.Where(r => r.Kind == "agent_message").All(r => int.Parse(r.Id["chat:m".Length..]) >= 153), "Archiving did not take the oldest agent replies first");
            f.Store.SynchronizeContext(claim, conversation, "message 1098");
            var again = f.Store.Read(f.TaskId);
            Check(again.ArchivedRecords == 76 && again.ContextRecords.Count == CollaborationStore.MaxRecords, $"Archived entries were re-imported: {again.ArchivedRecords} archived");
            Check(new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.TaskId).ArchivedThrough == again.ArchivedThrough, "Archive watermark did not persist");
            f.Memory.End(claim, WorkState.Ready, "done");
            return Task.CompletedTask;
        });
        await test("the ledger cache is bounded and evicted tasks reload on demand", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var task = f.Memory.Get(f.TaskId)!;
            var ids = Enumerable.Range(0, CollaborationStore.CacheLimit + 4).Select(i => f.Memory.Create(task.RoomId, task.Workspace, "objective " + i)).ToList();
            foreach (var id in ids) f.Store.Read(id);
            Check(f.Store.CachedDocuments <= CollaborationStore.CacheLimit, $"Cache grew past its bound: {f.Store.CachedDocuments}");
            Check(f.Store.Read(ids[0]).TaskId == ids[0] && f.Store.Read(f.TaskId).TaskId == f.TaskId, "Evicted ledgers did not reload");
            return Task.CompletedTask;
        });
        await test("a file already in a project subfolder is referenced in place rather than copied to the root", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var workspace = f.Memory.Get(f.TaskId)!.Workspace;
            var sub = Path.Combine(workspace, "docs"); Directory.CreateDirectory(sub); var file = Path.Combine(sub, "notes.md"); File.WriteAllText(file, "notes");
            var imported = await WorkspaceImports.CopyAsync(file, workspace);
            Check(imported.AlreadyInWorkspace && imported.Name == "docs/notes.md" && imported.FullPath == file && !File.Exists(Path.Combine(workspace, "notes.md")), $"Subfolder file was copied to the root: {imported.Name}");
        });
        await test("Codex stops asking after the first declined question, as Claude does", async () =>
        {
            await using var client = new CodexClient(new(AppContext.BaseDirectory, false, Executable: Environment.ProcessPath!));
            var asked = 0; client.RequestApproval = (_, _) => { asked++; return Task.FromResult(new Decision(false)); };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await client.SendAsync("input-fixture-batch", timeout.Token);
            var answers = JsonNode.Parse(reply.Text)!["answers"]!;
            Check(asked == 1 && answers["layout"]!["answers"]!.AsArray().Count == 0 && answers["notes"]!["answers"]!.AsArray().Count == 0, $"Codex kept asking after a decline: asked {asked}");
        });
    }
}

/// <summary>A provider stand-in that emits scripted native events during its turn, then replies; cancellation is honoured.</summary>
internal sealed class ScriptedAgent(Agent agent, AgentEvent[] events, string reply) : IAgentClient
{
    public Agent Agent => agent;
    public string? SessionId => "scripted";
    public event Action<AgentEvent>? Event;
    public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
    public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
    {
        foreach (var e in events) Event?.Invoke(e);
        await Task.Delay(20, token);
        return new(reply, SessionId);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Lets a test emit native-looking events from the routing fixture's fake client during a turn.</summary>
internal static class StructuredClientEvents
{
    public static Action<CollaborationRoutingTests.StructuredFake>? Hook;
}
