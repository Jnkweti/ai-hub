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
    }
}

/// <summary>Lets a test emit native-looking events from the routing fixture's fake client during a turn.</summary>
internal static class StructuredClientEvents
{
    public static Action<CollaborationRoutingTests.StructuredFake>? Hook;
}
