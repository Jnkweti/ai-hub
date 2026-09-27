using AIHub.Core;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

internal static class ConcurrentWorkTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (CollaborationValidationException) { return; } throw new Exception("Invalid work accepted"); }
    private sealed class Prep(Agent agent, Func<string, CancellationToken, Task<string>> respond) : IAgentClient
    {
        public Agent Agent => agent;
        public string? SessionId => "preparation-session";
        public event Action<AgentEvent>? Event;
        public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
        public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
        {
            Event?.Invoke(new(agent, EventKind.Message, "INTERNAL DRAFT"));
            Check(RequestApproval is not null && !(await RequestApproval(new(agent, "test", "denied"), token)).Allow, "Preparation approved native permission");
            return new(await respond(prompt, token), SessionId);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static JsonObject Claim(string kind = "discovery", bool reusable = true, bool independent = false) => new()
    {
        ["kind"] = kind, ["operation"] = kind == "check" ? "test-command" : "inspect source",
        ["scope"] = new JsonObject { ["files"] = new JsonArray("source.txt"), ["focus"] = new JsonArray("behavior") },
        ["reusable"] = reusable, ["independent"] = independent
    };
    private static JsonNode Call(CollaborationDispatch d, string tool, JsonObject args) => d.Call(d.Agent, d.Id, "session", tool, args, default);
    private static JsonObject Result(JsonNode claim, params string[] evidence) => new()
    { ["work_id"] = claim["work"]!.Str("id"), ["summary"] = "Observed the requested behavior", ["evidence_refs"] = new JsonArray(evidence.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) };
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("concurrent preparation shares frozen context, waits for lead, resumes and never publishes draft", async () =>
        {
            using var f = new Fixture(); var prepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var leadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var visible = new List<string>();
            await using var hub = f.Hub(async (agent, host, call, prompt, ct) =>
            {
                if (call == 1) { leadStarted.SetResult(); await prepStarted.Task.WaitAsync(ct); }
                else Check(prompt.Contains("FIRST UNIQUE RESPONSE") && prompt.Contains("tentative alternative") && host.ResumeSessionId == "preparation-session" && !host.StartFreshSession,
                    "Waiting agent lacked lead output or native preparation continuation");
                Tool(host, "submit_message", Message()); return call == 1 ? "FIRST UNIQUE RESPONSE" : "A useful addition";
            });
            hub.PreparationFactory = a => new Prep(a, async (_, ct) => { prepStarted.SetResult(); await leadStarted.Task.WaitAsync(ct); return "tentative alternative"; });
            hub.Event += e => { if (e.Kind == EventKind.Message) visible.Add(e.Text); };
            await hub.SubmitAsync("Discuss this design", "Both"); await f.Finished();
            Check(f.Memory.Get(f.TaskId)!.State == WorkState.Ready && f.Calls == 2, f.Memory.Get(f.TaskId)!.Reason);
            Check(visible.SequenceEqual(["FIRST UNIQUE RESPONSE", "A useful addition"]), "Preparation draft leaked or speaking order changed");
            var doc = f.Store.Read(f.TaskId); var prep = doc.Assignments.Single(a => a.Role == "preparation");
            var p = doc.ContextInputs.Single(i => i.DispatchId == prep.Id); var lead = doc.ContextInputs.Single(i => i.Agent == Agent.Codex);
            Check(p.CommonHash == lead.CommonHash && prep.State == "completed", "Initial shared context differed between models");
        });
        await test("stop cancels and joins preparation before releasing task ownership", async () =>
        {
            using var f = new Fixture(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var joined = false;
            await using var hub = f.Hub(async (_, _, _, _, ct) => { await started.Task.WaitAsync(ct); await Task.Delay(Timeout.Infinite, ct); return ""; });
            hub.PreparationFactory = a => new Prep(a, async (_, ct) => { started.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); return ""; } finally { joined = true; } });
            await hub.SubmitAsync("Discuss a change", "Both"); await started.Task.WaitAsync(TimeSpan.FromSeconds(15)); await hub.StopAsync();
            Check(joined && f.Memory.Get(f.TaskId)!.State == WorkState.Stopped, "Preparation survived task release");
            Check(f.Store.Read(f.TaskId).Assignments.All(a => a.State != "running"), "Abandoned preparation remained running");
        });
        await test("preparation failure falls back to normal peer context", async () =>
        {
            using var f = new Fixture(); await using var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Message()); return Task.FromResult("Answer"); });
            hub.PreparationFactory = a => new Prep(a, (_, _) => throw new IOException("Provider unavailable"));
            await hub.SubmitAsync("Discuss storage", "Both"); await f.Finished();
            Check(f.Calls == 2 && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Optional preparation failure stopped conversation");
            Check(f.Store.Read(f.TaskId).Assignments.Single(a => a.Role == "preparation").State == "failed", "Failed preparation not recorded");
        });
        await test("a correction preserves the interrupted speaker and explicit addressing takes priority", async () =>
        {
            using var f = new Fixture(); var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var hub = f.Hub(async (_, host, call, _, ct) =>
            {
                if (call == 1) { begun.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
                Tool(host, "submit_message", Message()); return "Current answer";
            });
            await hub.SubmitAsync("Claude, discuss the design", "Both"); await begun.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await hub.StopAsync(); await hub.SubmitAsync("Actually, use local storage", "Both"); await f.Finished();
            Check(f.Speakers.Take(2).SequenceEqual([Agent.Claude, Agent.Claude]), "Correction switched away from current speaker");
            await hub.SubmitAsync("Codex, check the tradeoff", "Both"); await f.Finished();
            Check(f.Speakers[3] == Agent.Codex, "Explicit address did not override scheduling");
        });
        await test("single-agent requests never start peer preparation", async () =>
        {
            using var f = new Fixture(); await using var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Message()); return Task.FromResult("Answer"); });
            hub.PreparationFactory = _ => throw new Exception("Unexpected peer preparation");
            await hub.SubmitAsync("Discuss storage", "Codex"); await f.Finished();
            Check(f.Calls == 1 && f.Store.Read(f.TaskId).Assignments.Count == 1 && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Single-agent routing launched peer");
        });
        await test("shared discovery retries, reuses, invalidates and preserves independent work", () =>
        {
            using var f = new Fixture(); var file = Path.Combine(f.Memory.Get(f.TaskId)!.Workspace, "source.txt"); File.WriteAllText(file, "before");
            var claim = f.Begin(); var d = f.Dispatch(claim); var work = Call(d, "claim_work", Claim());
            Check(Call(d, "claim_work", Claim())["work"]!.Str("id") == work["work"]!.Str("id"), "Claim retry duplicated work");
            Call(d, "complete_work", Result(work)); Call(d, "complete_work", Result(work));
            Check(Call(d, "claim_work", Claim()).Str("disposition") == "reused", "Current discovery was not reused");
            Check(Call(d, "claim_work", Claim(independent: true)).Str("disposition") == "claimed", "Independent review reused result");
            File.AppendAllText(file, "changed"); Check(Call(d, "claim_work", Claim()).Str("disposition") == "claimed", "Changed file reused result");
            d.Abort("test"); Check(f.Store.Read(f.TaskId).SharedWork.Where(w => w.Id != work["work"]!.Str("id")).All(w => w.State == "interrupted"), "Abandoned claims not interrupted");
            f.Memory.End(claim, WorkState.Stopped, "test");
            Check(new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.TaskId).SharedWork.Count == 3, "Work history not durable");
            return Task.CompletedTask;
        });
        await test("check reuse requires native matching successful stable evidence and owning dispatch", () =>
        {
            using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Memory.Get(f.TaskId)!.Workspace, "source.txt"), "before");
            var claim = f.Begin(); var d = f.Dispatch(claim); var work = Call(d, "claim_work", Claim("check"));
            Reject(() => Call(d, "complete_work", Result(work)));
            d.Observe(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd", "{\"type\":\"commandExecution\",\"status\":\"inProgress\",\"command\":\"test-command\"}"));
            d.Observe(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd", "{\"type\":\"commandExecution\",\"status\":\"completed\",\"exitCode\":0}"));
            var evidence = f.Store.Read(f.TaskId).Evidence.Single().Id; Call(d, "complete_work", Result(work, evidence));
            f.Submit(d, Agent.Codex, Message()); d.Complete(); f.Memory.ReleaseSpeaker(claim, Agent.Codex);
            var peer = f.Dispatch(claim, Agent.Claude);
            Check(Call(peer, "claim_work", Claim("check")).Str("disposition") == "reused", "Peer repeated a current successful check");
            Reject(() => Call(peer, "complete_work", Result(work, evidence)));
            Check(Call(peer, "claim_work", Claim("check", reusable: false)).Str("disposition") == "claimed", "Nonreusable check was cached");
            peer.Abort("test"); f.Memory.End(claim, WorkState.Ready, "test");
            var next = f.Begin(); var fresh = f.Dispatch(next);
            Check(Call(fresh, "claim_work", Claim("check")).Str("disposition") == "claimed", "Check reused across generations"); fresh.Abort("test");
            return Task.CompletedTask;
        });
        await test("shared work rejects escaping paths and malformed flags", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var d = f.Dispatch(claim);
            foreach (var path in new[] { "../secret", "C:/secret", "source/../secret", "source\\file", "/secret" })
            { var n = Claim(); n["scope"]!["files"] = new JsonArray(path); Reject(() => Call(d, "claim_work", n)); }
            var bad = Claim(); bad["independent"] = "yes"; Reject(() => Call(d, "claim_work", bad)); d.Abort("test"); return Task.CompletedTask;
        });
        await test("context snapshots deduplicate within an operation and refresh next operation", async () =>
        {
            using var f = new Fixture(); var count = 0;
            Task<ProjectSnapshot> Capture(string w, CollaborationScope s, CancellationToken ct) { count++; return Task.FromResult(new ProjectSnapshot("hash", "", "", true, "", new())); }
            var batch = new ContextSnapshotBatch(f.Root, default, Capture);
            await batch.Capture(new(["b", "a"], ["first"])); await batch.Capture(new(["a", "b"], ["second"])); Check(count == 1, "Same scoped files hashed twice");
            await new ContextSnapshotBatch(f.Root, default, Capture).Capture(new(["a", "b"], [])); Check(count == 2, "Snapshot cache crossed freshness boundary");
        });
    }
}
