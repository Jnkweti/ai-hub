using AIHub.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CollaborationRoutingTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception ex) when (ex is CollaborationValidationException or IOException or InvalidOperationException or UnauthorizedAccessException) { return; } throw new Exception("Invalid operation accepted"); }
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    internal static JsonNode Tool(CollaborationMcpHost host, string name, JsonNode args)
    {
        var result = host.InvokeTool(name, JsonNode.Parse(args.ToJsonString()));
        if (result.Bool("isError")) throw new CollaborationValidationException(result["content"]![0]!.Str("text"));
        return JsonNode.Parse(result["content"]![0]!.Str("text"))!;
    }
    internal static JsonObject Message(string type = "status", Agent recipient = Agent.Claude, string? replyTo = null, string status = "assignment_complete")
    {
        var n = CollaborationTests.Submission(type, Guid.NewGuid().ToString("N"));
        if (type != "status") n["content"]!["recipient"] = recipient.ToString();
        else n["content"]!["status"] = status;
        if (replyTo is not null) n["content"]!["reply_to"] = replyTo;
        return n;
    }
    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(CollaborationTests.Root, "artifacts", "routing-test-" + Guid.NewGuid().ToString("N"));
        public LocalStore Local { get; }
        public TaskMemory Memory { get; }
        public CollaborationStore Store { get; }
        public string TaskId { get; }
        public int Calls;
        public List<Agent> Speakers { get; } = [];
        public Fixture()
        {
            Local = new(Root); Memory = new(Local); var project = Path.Combine(Root, "project"); Directory.CreateDirectory(project);
            TaskId = Memory.Create("routing-room", project, "Implement and review the requested change."); Store = new(Local, Memory);
        }
        public TaskClaim Begin(Agent agent = Agent.Codex)
        { var claim = Memory.Begin(TaskId, false); Memory.Own(claim, agent); return claim; }
        public CollaborationDispatch Dispatch(TaskClaim claim, Agent agent = Agent.Codex, string? incoming = null)
        { Memory.Own(claim, agent); return Store.OpenDispatch(claim, agent, [Agent.Codex, Agent.Claude], incoming, default); }
        public JsonNode Submit(CollaborationDispatch dispatch, Agent agent, JsonObject message) =>
            dispatch.Call(agent, dispatch.Id, "fixture-session", "submit_message", message, default);
        public HubCoordinator Hub(Func<Agent, CollaborationMcpHost, int, string, CancellationToken, Task<string>> respond) => new(_ => throw new Exception("Legacy factory used"))
        {
            TaskMemory = Memory, TaskId = TaskId, CollaborationStore = Store, CollaborationBridgePath = CollaborationTests.Bridge,
            CollaborationFactory = (agent, host) => new StructuredFake(agent, host, async (prompt, token) =>
            {
                var index = Interlocked.Increment(ref Calls); lock (Speakers) Speakers.Add(agent);
                return await respond(agent, host, index, prompt, token);
            })
        };
        public Task Finished() => Until(() => Memory.Get(TaskId)!.State != WorkState.Running);
        public void Dispose()
        {
            if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Path.Combine(CollaborationTests.Root, "artifacts")) throw new Exception("Unsafe fixture cleanup");
            Directory.Delete(Root, true);
        }
    }
    internal sealed class StructuredFake(Agent agent, CollaborationMcpHost host, Func<string, CancellationToken, Task<string>> respond) : IAgentClient
    {
        public Agent Agent => agent;
        public string? SessionId => agent + "-native-fixture";
        public event Action<AgentEvent>? Event;
        public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
        public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
        {
            await Task.Delay(1, token); host.BindSession(SessionId!);
            var reply = await respond(prompt, token); Event?.Invoke(new(agent, EventKind.Message, reply)); return new(reply, SessionId);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (var mode in new[] { "pass", "duplicate", "new" }) await test("automatic peer contribution is quiet unless useful: " + mode, async () =>
        {
            using var f = new Fixture(); var messages = new List<AgentEvent>();
            await using var hub = f.Hub((_, host, turn, prompt, _) =>
            {
                if (turn == 2) Check(prompt.Contains("A second message is optional") && prompt.Contains("no_further_contribution"), "Peer was encouraged to repeat");
                Tool(host, "submit_message", Message(status: turn == 2 && mode == "pass" ? "no_further_contribution" : "assignment_complete"));
                return Task.FromResult(turn == 1 || mode == "duplicate" ? "Keep notes locally." : mode == "pass" ? "Nothing else to add." : "Add an export command so users can recover their data.");
            });
            hub.Event += e => { if (e.Kind is EventKind.Message or EventKind.TextDelta) messages.Add(e); };
            await hub.SubmitAsync("Discuss storage", "Both"); await f.Finished();
            Check(f.Calls == 2 && messages.Count == (mode == "new" ? 2 : 1), "Pass or duplicate leaked into chat, or useful contribution was hidden");
            Check(f.Store.Read(f.TaskId).Entries.Count == 2, "Silent peer check was not retained in task history");
        });
        foreach (var automatic in new[] { false, true })
        await test("Both invites the peer after completion without a handoff, auto=" + automatic, async () =>
        {
            using var f = new Fixture(); var routes = new List<string>();
            await using var hub = f.Hub((agent, host, turn, prompt, _) =>
            {
                var context = Tool(host, "get_task_context", new JsonObject());
                Check(context["incoming_message"] is null, "Invented peer request for a user contribution");
                if (turn == 2) Check(prompt.Contains("initial contribution") && prompt.Contains("Discuss our options"), "Second agent lost user/peer context");
                Tool(host, "submit_message", Message()); return Task.FromResult(agent + " adds a perspective.");
            });
            hub.AutoExchange = automatic; hub.Dispatch += (from, _, _) => routes.Add(from);
            await hub.SubmitAsync("Discuss our options", "Both"); await f.Finished();
            Check(f.Speakers.SequenceEqual([Agent.Codex, Agent.Claude]) && routes.SequenceEqual(["You", "You"]) && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "First completion silenced a participant");
        });
        await test("shared starting speaker rotates across coordinator recreation and explicit names choose first", async () =>
        {
            using var f = new Fixture();
            for (var run = 0; run < 3; run++)
            {
                await using var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Message()); return Task.FromResult("A useful contribution."); });
                await hub.SubmitAsync(run == 2 ? "Claude, discuss the tradeoff" : "Discuss the tradeoff", "Both"); await f.Finished();
            }
            Check(f.Speakers.SequenceEqual([Agent.Codex, Agent.Claude, Agent.Claude, Agent.Codex, Agent.Claude, Agent.Codex]), "Rotation or explicit first speaker failed");
        });
        await test("a blocked contribution pauses for the user before inviting another agent", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Message(status: "blocked")); return Task.FromResult("I need your decision."); });
            await hub.SubmitAsync("Discuss", "Both"); await f.Finished();
            Check(f.Calls == 1 && f.Memory.Get(f.TaskId)!.State == WorkState.Paused, "Peer answered for the user");
        });
        await test("durable message acceptance atomically saves one receipt and preserves retries", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var dispatch = f.Dispatch(claim); var n = Message("handoff");
            var first = f.Submit(dispatch, Agent.Codex, n); var again = f.Submit(dispatch, Agent.Codex, n);
            Check(first.Str("message_id") == again.Str("message_id") && again.Bool("duplicate") && first.Bool("persistent"), "Receipt not stable or durable");
            var disk = JsonSerializer.Deserialize<CollaborationDocument>(File.ReadAllText(Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId))))!;
            Check(disk.Entries.Count == 1 && disk.Entries[0].Message.State == DeliveryState.Accepted && !disk.Entries[0].SenderSucceeded, "Acceptance incorrectly delivered");
            n["content"]!["summary"] = "Different"; Reject(() => f.Submit(dispatch, Agent.Codex, n));
            var terminal = dispatch.Complete(); Check(terminal.State == DeliveryState.Pending, "Successful sender did not queue message");
            Reject(() => f.Submit(dispatch, Agent.Codex, Message()));
            f.Store.EndRun(claim, "Paused"); f.Memory.End(claim, WorkState.Paused, "Paused"); return Task.CompletedTask;
        });
        await test("failed atomic acceptance emits no receipt and permits a clean retry", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var dispatch = f.Dispatch(claim); var n = Message();
            var path = Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId)); var before = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => f.Submit(dispatch, Agent.Codex, n));
            Check(File.ReadAllText(path) == before && f.Store.Read(f.TaskId).Entries.Count == 0, "Failed acceptance mutated state");
            f.Submit(dispatch, Agent.Codex, n); dispatch.Complete(); f.Store.EndRun(claim, "Done"); f.Memory.End(claim, WorkState.Ready, "Done"); return Task.CompletedTask;
        });
        await test("restart interrupts saved requests and never replays an older generation", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var dispatch = f.Dispatch(claim);
            f.Submit(dispatch, Agent.Codex, Message("review_request")); var request = dispatch.Complete();
            var restartedMemory = new TaskMemory(f.Local); var restarted = new CollaborationStore(f.Local, restartedMemory);
            Check(restartedMemory.Get(f.TaskId)!.State == WorkState.Interrupted && restarted.Read(f.TaskId).Entries[0].Message.State == DeliveryState.Interrupted, "Restart left runnable requests");
            var next = restartedMemory.Begin(f.TaskId, false); restartedMemory.Own(next, Agent.Claude);
            Reject(() => restarted.OpenDispatch(next, Agent.Claude, [Agent.Claude, Agent.Codex], request.Envelope.MessageId, default));
            var fresh = restarted.OpenDispatch(next, Agent.Claude, [Agent.Claude, Agent.Codex], null, default);
            var page = fresh.Call(Agent.Claude, fresh.Id, "s", "get_messages", JsonNode.Parse("{\"after_sequence\":0,\"limit\":1}"), default);
            Check(page["messages"]!.AsArray().Count == 1 && page.Bool("historical_only"), "Interrupted history unavailable");
            fresh.Abort("Done"); restarted.EndRun(next, "Done"); restartedMemory.End(next, WorkState.Ready, "Done"); return Task.CompletedTask;
        });
        await test("durable dispatch authority rejects owner changes and stale run generations", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var d = f.Dispatch(claim);
            f.Memory.Own(claim, Agent.Claude); Reject(() => f.Submit(d, Agent.Codex, Message()));
            f.Memory.Own(claim, Agent.Codex); d.Abort("Stopped"); f.Store.EndRun(claim, "Stopped"); f.Memory.End(claim, WorkState.Stopped, "Stopped");
            var next = f.Begin(); var fresh = f.Dispatch(next);
            Reject(() => f.Submit(d, Agent.Codex, Message())); Reject(() => d.Complete());
            f.Submit(fresh, Agent.Codex, Message()); fresh.Complete();
            Check(f.Store.Read(f.TaskId).Entries.Single().Message.Envelope.Generation == next.Generation, "Old run published into new generation");
            f.Store.EndRun(next, "Done"); f.Memory.End(next, WorkState.Ready, "Done"); return Task.CompletedTask;
        });
        await test("durable history is bounded, isolated and rejects malformed versions without overwriting", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var d = f.Dispatch(claim);
            for (var i = 0; i < 3; i++) f.Submit(d, Agent.Codex, Message(status: "progress"));
            var args = JsonNode.Parse("{\"after_sequence\":0,\"limit\":2}")!;
            var page = d.Call(Agent.Codex, d.Id, "s", "get_messages", args, default);
            Check(page["messages"]!.AsArray().Count == 2 && page.Bool("has_more") && page["next_sequence"]!.GetValue<long>() == 2, "History cursor incorrect");
            var numericPage = d.Call(Agent.Codex, d.Id, "s", "get_messages", JsonNode.Parse("{\"after_sequence\":0.0,\"limit\":2.0}"), default);
            Check(numericPage["messages"]!.AsArray().Count == 2, "JSON Schema integer representation rejected");
            args["task_id"] = "another-task"; Reject(() => d.Call(Agent.Codex, d.Id, "s", "get_messages", args, default));
            d.Abort("End"); f.Store.EndRun(claim, "End"); f.Memory.End(claim, WorkState.Ready, "End");
            var path = Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId)); var bad = JsonNode.Parse(File.ReadAllText(path))!; bad["Version"] = "99"; File.WriteAllText(path, bad.ToJsonString());
            var before = File.ReadAllText(path); Reject(() => new CollaborationStore(f.Local, new TaskMemory(f.Local))); Check(File.ReadAllText(path) == before, "Unsupported version overwritten");
            using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write)) oversized.SetLength(CollaborationStore.MaxDocumentBytes + 1L);
            Reject(() => new CollaborationStore(f.Local, new TaskMemory(f.Local))); return Task.CompletedTask;
        });
        await test("deleting a room removes its durable history and rolls back ordinary failures", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var d = f.Dispatch(claim); f.Submit(d, Agent.Codex, Message()); d.Complete(); f.Store.EndRun(claim, "Done"); f.Memory.End(claim, WorkState.Ready, "Done");
            var otherId = f.Memory.Create("other-room", f.Root, "Other task"); var otherClaim = f.Memory.Begin(otherId, false); f.Memory.Own(otherClaim, Agent.Codex);
            var other = f.Store.OpenDispatch(otherClaim, Agent.Codex, [Agent.Codex], null, default); other.Call(Agent.Codex, other.Id, "s", "submit_message", Message(), default); other.Complete(); f.Store.EndRun(otherClaim, "Done"); f.Memory.End(otherClaim, WorkState.Ready, "Done");
            Reject(() => f.Store.DeleteRoom("routing-room", () => throw new IOException("Transcript deletion failed")));
            Check(f.Memory.Get(f.TaskId) is not null && f.Store.Read(f.TaskId).Entries.Count == 1, "Failed delete lost task history");
            f.Store.DeleteRoom("routing-room", () => { });
            Check(f.Memory.Get(f.TaskId) is null && !File.Exists(Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId))) && f.Store.Read(otherId).Entries.Count == 1, "Deletion touched unrelated task");
            Reject(() => f.Store.Read(f.TaskId)); return Task.CompletedTask;
        });
        await test("structured routing honors a validated request despite contradictory prose and deduplicates it", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((agent, host, turn, prompt, token) =>
            {
                var context = Tool(host, "get_task_context", new JsonObject());
                if (turn == 1)
                {
                    var n = Message("handoff"); var receipt = Tool(host, "submit_message", n); var retry = Tool(host, "submit_message", n);
                    Check(receipt.Str("message_id") == retry.Str("message_id"), "Duplicate receipt changed");
                }
                else
                {
                    Check(agent == Agent.Claude && prompt.Contains("CURRENT STRUCTURED PEER MESSAGE"), "Peer did not receive structured input");
                    var incoming = context["incoming_message"]!["envelope"]!.Str("message_id");
                    Tool(host, "submit_message", Message(replyTo: incoming));
                }
                return Task.FromResult(turn == 1 ? "Task complete." : "Passing to Codex.");
            });
            await hub.SubmitAsync("Implement the change", "Both"); await f.Finished();
            var records = f.Store.Read(f.TaskId).Entries;
            Check(f.Calls == 2 && f.Speakers.SequenceEqual([Agent.Codex, Agent.Claude]) && records.Count == 2 && records[0].Message.State == DeliveryState.Answered, "Structured route duplicated or followed prose");
        });
        await test("review requests require review results and route back to the author", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((agent, host, turn, _, _) =>
            {
                var context = Tool(host, "get_task_context", new JsonObject());
                var incoming = context["incoming_message"]?["envelope"]?.Str("message_id");
                if (turn == 1) Tool(host, "submit_message", Message("review_request"));
                else if (turn == 2)
                {
                    Reject(() => Tool(host, "submit_message", Message(replyTo: incoming)));
                    Tool(host, "submit_message", Message("review_result", Agent.Codex, incoming));
                }
                else Tool(host, "submit_message", Message(replyTo: incoming));
                return Task.FromResult("Reported this assignment.");
            });
            await hub.SubmitAsync("Implement then review", "Both"); await f.Finished();
            Check(f.Calls == 3 && f.Speakers.SequenceEqual([Agent.Codex, Agent.Claude, Agent.Codex]) && f.Store.Read(f.TaskId).Entries.Take(2).All(e => e.Message.State == DeliveryState.Answered), "Review workflow failed");
        });
        await test("single-agent targeting stays exclusive and auto off allows one contribution each", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((agent, host, _, _, _) =>
            {
                Reject(() => Tool(host, "submit_message", Message("handoff")));
                Tool(host, "submit_message", Message()); return Task.FromResult("Passing to Claude Code.");
            });
            await hub.SubmitAsync("Work alone", "Codex"); await f.Finished(); Check(f.Calls == 1, "Targeted send dispatched peer");
            await hub.DisposeAsync();
            await using var second = f.Hub((agent, host, _, _, _) => {
                var context = Tool(host, "get_task_context", new JsonObject());
                Tool(host, "submit_message", Message("handoff", ConversationTurns.Other(agent), context["incoming_message"]?["envelope"]?.Str("message_id")));
                return Task.FromResult("A concrete follow-up."); });
            second.AutoExchange = false; await second.SubmitAsync("Codex, discuss", "Both"); await f.Finished();
            Check(f.Calls == 3 && f.Speakers.TakeLast(2).SequenceEqual([Agent.Codex, Agent.Claude]) && f.Memory.Get(f.TaskId)!.State == WorkState.Paused && f.Store.Read(f.TaskId).Entries.Last().Message.State == DeliveryState.Interrupted, "Initial contributions or disabled follow-ups failed");
        });
        await test("sender failure after acceptance never dispatches the queued peer", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Message("handoff")); throw new IOException("Provider failed after tool response"); });
            await hub.SubmitAsync("Work", "Both"); await f.Finished();
            Check(f.Calls == 1 && f.Memory.Get(f.TaskId)!.State == WorkState.Failed && f.Store.Read(f.TaskId).Entries.Single().Message.State == DeliveryState.Interrupted, "Failure dispatched or hid accepted history");
        });
        await test("failed sender commit cannot make an accepted handoff runnable", async () =>
        {
            using var f = new Fixture(); FileStream? held = null;
            await using var hub = f.Hub((_, host, _, _, _) =>
            {
                Tool(host, "submit_message", Message("handoff"));
                held = new FileStream(Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId)), FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.FromResult("Passing to Claude Code.");
            });
            try
            {
                await hub.SubmitAsync("Work", "Both"); await f.Finished();
                Check(f.Calls == 1 && f.Memory.Get(f.TaskId)!.State == WorkState.Failed && !f.Store.Read(f.TaskId).Entries.Single().SenderSucceeded, "Failed commit dispatched peer");
            }
            finally { held?.Dispose(); }
            var recovered = new CollaborationStore(f.Local, new TaskMemory(f.Local));
            Check(recovered.Read(f.TaskId).Entries.Single().Message.State == DeliveryState.Interrupted, "Recovery left failed handoff pending");
        });
        await test("exhausted tool repairs prevent routing even after an earlier terminal submission", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((_, host, _, _, _) =>
            {
                Tool(host, "submit_message", Message("handoff"));
                for (var i = 0; i < 3; i++) Reject(() => Tool(host, "submit_message", new JsonObject()));
                return Task.FromResult("Done");
            });
            await hub.SubmitAsync("Work", "Both"); await f.Finished();
            Check(f.Calls == 1 && f.Memory.Get(f.TaskId)!.State == WorkState.Paused && f.Store.Read(f.TaskId).Entries.Single().Message.State == DeliveryState.Interrupted, "Repair exhaustion dispatched peer");
        });
        await test("missing terminal messages receive two repairs then pause without prose fallback", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((_, _, _, _, _) => Task.FromResult("Passing to Claude Code."));
            await hub.SubmitAsync("Work", "Both"); await f.Finished();
            Check(f.Calls == 3 && f.Speakers.All(a => a == Agent.Codex) && f.Memory.Get(f.TaskId)!.State == WorkState.Paused && f.Store.Read(f.TaskId).Entries.Count == 0, "Unbounded repair or prose fallback");
        });
        await test("a corrected terminal message can finish within the repair budget", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((_, host, turn, prompt, _) =>
            {
                if (turn == 2) { Check(prompt.Contains("HOST VALIDATION REPAIR"), "Missing repair explanation"); Tool(host, "submit_message", Message()); }
                return Task.FromResult("Done.");
            });
            await hub.SubmitAsync("Work", "Codex"); await f.Finished();
            Check(f.Calls == 2 && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Valid repair did not finish");
        });
        await test("structured exchanges retain the configured round backstop", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((agent, host, _, _, _) =>
            {
                var context = Tool(host, "get_task_context", new JsonObject());
                Tool(host, "submit_message", Message("handoff", ConversationTurns.Other(agent), context["incoming_message"]?["envelope"]?.Str("message_id")));
                return Task.FromResult("New authorized peer request.");
            });
            hub.MaxAutoRounds = 1; await hub.SubmitAsync("Work", "Both"); await f.Finished();
            Check(f.Calls == 4 && f.Memory.Get(f.TaskId)!.State == WorkState.Paused && f.Store.Read(f.TaskId).Entries.Last().Message.State == DeliveryState.Interrupted, "Round cap failed");
        });
        await test("cancellation revokes accepted work and prevents late structured publication", async () =>
        {
            using var f = new Fixture(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CollaborationMcpHost? oldHost = null;
            await using var hub = f.Hub(async (_, host, _, _, token) =>
            {
                oldHost = host; Tool(host, "submit_message", Message("handoff")); ready.TrySetResult();
                await Task.Delay(Timeout.Infinite, token); return "Late reply";
            });
            await hub.SubmitAsync("Work", "Both"); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); await hub.StopAsync(); await f.Finished();
            Check(f.Calls == 1 && f.Memory.Get(f.TaskId)!.State == WorkState.Stopped && f.Store.Read(f.TaskId).Entries.Single().Message.State == DeliveryState.Interrupted, "Stop dispatched peer");
            try { Tool(oldHost!, "submit_message", Message()); throw new Exception("Revoked host accepted a call"); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        });
    }
}
