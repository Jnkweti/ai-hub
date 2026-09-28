using AIHub.Core;
using System.Text.Json.Nodes;

internal static class LiveStreamTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (CollaborationValidationException) { return; } throw new Exception("Invalid operation was accepted"); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("resident sessions: one provider per agent per phase and delta prompts after the first turn", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var prompts = new List<(Agent Agent, string Prompt)>();
            await using var hub = f.Hub((agent, host, turn, prompt, _) =>
            {
                lock (prompts) prompts.Add((agent, prompt));
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message(status: turn == 4 ? "no_further_contribution" : "assignment_complete"));
                return Task.FromResult(turn switch { 1 => "Store observations locally.", 2 => "Offer an export command.", 3 => "Include timestamps in exports.", _ => "Nothing else to add." });
            });
            var created = 0; var inner = hub.CollaborationFactory!;
            hub.CollaborationFactory = (agent, host) => { Interlocked.Increment(ref created); return inner(agent, host); };
            hub.AllowFollowUpContributions = true;
            await hub.SubmitAsync("Discuss the diagnostic design", "Both"); await f.Finished();
            Check(f.Calls == 4 && created == 2, $"Expected one resident client per agent, got {created} clients for {f.Calls} turns");
            Check(prompts[0].Prompt.Contains("AI HUB COMMON TASK CONTEXT") && prompts[1].Prompt.Contains("AI HUB COMMON TASK CONTEXT"), "First turns lost the common core");
            Check(prompts[2].Agent == Agent.Codex && prompts[2].Prompt.Contains("NEW EVENTS SINCE YOUR LAST TURN") && !prompts[2].Prompt.Contains("AI HUB COMMON TASK CONTEXT"), "Resident turn did not receive a delta prompt");
            Check(prompts[2].Prompt.Contains("agent_message · Claude") && prompts[2].Prompt.Contains("Offer an export command.") && !prompts[2].Prompt.Contains("agent_message · Codex"), "Delta prompt lacks the peer's contribution or repeats the agent's own");
            Check(prompts[3].Agent == Agent.Claude && prompts[3].Prompt.Contains("Include timestamps in exports.") && !prompts[3].Prompt.Contains("Store observations locally.\n"), "Second resident turn missed the newest event or replayed old ones");
            var inputs = f.Store.Read(f.TaskId).ContextInputs;
            Check(inputs.Count(i => i.Outcome == "responded") == 4 && inputs.All(i => i.Prompt.Length > 0 && i.CommonHash.Length == 64), "Delta prompts were not recorded as exact inputs");
            Check(f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Phase did not end Ready");
        });
        await test("event stream records user, agent, pass and system events in order and pages through get_events", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture();
            await using var hub = f.Hub((_, host, turn, _, _) =>
            {
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message(status: turn == 2 ? "no_further_contribution" : "assignment_complete"));
                return Task.FromResult(turn == 1 ? "First answer." : "Nothing to add.");
            });
            await hub.SubmitAsync("Discuss the stream", "Both"); await f.Finished();
            var events = f.Store.Read(f.TaskId).Events;
            var kinds = events.Select(e => e.Kind).ToArray();
            Check(kinds.SequenceEqual(["user_message", "system", "agent_message", "agent_pass", "system"]), "Unexpected event order: " + string.Join(",", kinds));
            Check(events[0].Author == "You" && events[0].Text == "Discuss the stream" && events[2].Author == "Codex" && events[2].Text == "First answer." && events[3].Author == "Claude" && events[2].Ref is { Length: 32 }, "Event attribution incorrect");
            Check(events.Select(e => e.Sequence).SequenceEqual([1L, 2, 3, 4, 5]) && events.All(e => e.Generation == 1), "Event sequences or generations are wrong");
            var claim = f.Begin(); var dispatch = f.Dispatch(claim);
            try
            {
                var page = dispatch.Call(Agent.Codex, dispatch.Id, "fixture-session", "get_events", new JsonObject { ["after_sequence"] = 2, ["limit"] = 2 }, default);
                Check(page["events"]!.AsArray().Count == 2 && page["next_sequence"]!.GetValue<long>() == 4 && page["has_more"]!.GetValue<bool>() && page["evicted"]!.GetValue<long>() == 0, "get_events paging incorrect");
                var rest = dispatch.Call(Agent.Codex, dispatch.Id, "fixture-session", "get_events", new JsonObject { ["after_sequence"] = 4, ["limit"] = 32 }, default);
                Check(rest["events"]!.AsArray().Count == 1 && !rest["has_more"]!.GetValue<bool>(), "get_events tail incorrect");
                Reject(() => dispatch.Call(Agent.Codex, dispatch.Id, "fixture-session", "get_events", new JsonObject { ["after_sequence"] = 0, ["limit"] = 99 }, default));
                Reject(() => dispatch.Call(Agent.Codex, dispatch.Id, "fixture-session", "get_events", new JsonObject { ["after_sequence"] = 0 }, default));
            }
            finally { dispatch.Abort("test"); f.Memory.End(claim, WorkState.Stopped, "test"); }
        });
        await test("pipe host refuses tool calls while no dispatch is attached and resets limits per dispatch", async () =>
        {
            using var f = new CollaborationTests.Fixture(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var host = new CollaborationMcpHost(f.Probe, Agent.Codex, CollaborationTests.Bridge, timeout.Token); host.BindSession("test-session");
            host.Detach();
            var refused = host.InvokeTool("get_task_context", new JsonObject());
            Check(refused.Bool("isError") && refused["content"]![0]!.Str("text").Contains("No active dispatch") && !host.LimitReached, "Detached host accepted a call or counted it");
            for (var i = 0; i < 3; i++) host.InvokeTool("submit_message", new JsonObject());
            Check(!host.LimitReached && f.Probe.ContextReads == 0, "Detached calls reached the tools or counted as repairs");
            host.Attach(f.Probe, "dispatch-2");
            Check(host.DispatchId == "dispatch-2" && !host.LimitReached, "Attach did not set the dispatch identity");
            Check(!host.InvokeTool("get_task_context", new JsonObject()).Bool("isError") && f.Probe.ContextReads == 1, "Attached host refused a valid call");
            for (var i = 0; i < 3; i++) host.InvokeTool("submit_message", new JsonObject());
            Check(host.LimitReached, "Repair limit not counted after attach");
            host.Attach(f.Probe, "dispatch-3");
            Check(!host.LimitReached && host.DispatchId == "dispatch-3", "Limits did not reset for the next dispatch");
            Check(host.Instructions.Contains("probe") && !host.Instructions.Contains("get_events"), "Baseline instructions changed on attach");
        });
        await test("a user message during a running phase joins the stream without stopping the run", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var history = new List<ConversationEntry> { new("u1", "You", "Design the export", "Both") };
            var firstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompts = new List<(Agent Agent, string Prompt)>();
            await using var hub = f.Hub(async (agent, host, turn, prompt, token) =>
            {
                lock (prompts) prompts.Add((agent, prompt));
                if (turn == 1) { firstTurn.TrySetResult(); await release.Task.WaitAsync(token); }
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message(status: turn >= 4 ? "no_further_contribution" : "assignment_complete"));
                return "Contribution " + turn;
            });
            hub.AllowFollowUpContributions = true;
            hub.ReadConversation = _ => { lock (history) return Task.FromResult<IReadOnlyList<ConversationEntry>>(history.ToArray()); };
            Check(!hub.IsRunning && !await hub.InterjectAsync("too early"), "Interjection accepted with no running phase");
            await hub.SubmitAsync("Design the export", "Both"); await firstTurn.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var generation = f.Memory.Get(f.TaskId)!.Generation;
            lock (history) history.Add(new("u2", "You", "Also check the tests", "Both"));
            Check(hub.IsRunning && await hub.InterjectAsync("Also check the tests"), "Interjection refused during a running phase");
            release.SetResult(); await f.Finished();
            Check(f.Memory.Get(f.TaskId)!.Generation == generation && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Interjection restarted or broke the phase: " + f.Memory.Get(f.TaskId)!.Reason);
            var events = f.Store.Read(f.TaskId).Events;
            Check(events.Any(e => e.Kind == "user_message" && e.Text == "Also check the tests" && e.Author == "You"), "Aside missing from the stream");
            // Claude's first turn carries the aside in its common core; Codex's next turn carries it as a stream event; Claude's later delta omits what it already saw.
            Check(f.Calls == 4 && prompts[1].Prompt.Contains("Also check the tests") && prompts[2].Prompt.Contains("user_message · You") && prompts[2].Prompt.Contains("Also check the tests") && !prompts[3].Prompt.Contains("Also check the tests"),
                $"The aside was not supplied exactly once per participant ({f.Calls} calls)");
            Check(TaskContextBuilder.ActiveInstructions(f.Store.Read(f.TaskId)).Any(r => r.Id == "chat:u2"), "Aside did not keep user authority");
        });
        await test("every participant reacts to each contribution and the phase ends when all pass", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var order = new List<Agent>();
            await using var hub = f.Hub((agent, host, turn, _, _) =>
            {
                lock (order) order.Add(agent);
                // Each contribution gives the other participant a reaction opportunity; Claude's pass on its second opportunity ends the phase.
                var pass = agent == Agent.Claude && turn >= 4;
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message(status: pass ? "no_further_contribution" : "assignment_complete"));
                return Task.FromResult(pass ? "Nothing to add." : $"{agent} point {turn}");
            });
            hub.AllowFollowUpContributions = true; var diagnostics = new List<AuditCode>(); hub.Diagnostic += (code, _) => diagnostics.Add(code);
            await hub.SubmitAsync("Discuss the stream design", "Both"); await f.Finished();
            Check(order.SequenceEqual([Agent.Codex, Agent.Claude, Agent.Codex, Agent.Claude]), "Reaction order wrong: " + string.Join(",", order));
            Check(f.Memory.Get(f.TaskId)!.State == WorkState.Ready && f.Memory.Get(f.TaskId)!.Reason.StartsWith("Every participant passed"), "Phase did not end when everyone passed: " + f.Memory.Get(f.TaskId)!.Reason);
            Check(!diagnostics.Contains(AuditCode.StreamImbalance), "Imbalance flagged although both contributed");
        });
        await test("event stream is bounded, clips long text, evicts oldest first and persists", () =>
        {
            var document = new CollaborationDocument();
            for (var i = 0; i < CollaborationStore.MaxEvents + 40; i++) CollaborationStore.AppendEvent(document, "system", "AI Hub", "event " + i, null, 1);
            CollaborationStore.AppendEvent(document, "user_note", "You", new string('x', 5000), "note:1", 1);
            Check(document.Events.Count == CollaborationStore.MaxEvents && document.EvictedEvents == 41 && document.LastEventSequence == CollaborationStore.MaxEvents + 41, "Event bounds not enforced");
            Check(document.Events[0].Sequence == 42 && document.Events[^1].Text.Length < 2100 && document.Events[^1].Text.EndsWith("]"), "Oldest events were not evicted first or long text was not clipped");
            try { CollaborationStore.AppendEvent(document, "bogus", "You", "x", null, 1); throw new Exception("Unknown kind accepted"); } catch (ArgumentException) { }
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Begin();
            f.Store.AppendEvent(claim, "user_note", "You", "persisted note", "note:2");
            f.Memory.End(claim, WorkState.Stopped, "test");
            var reloaded = new CollaborationStore(f.Local, f.Memory).Read(f.TaskId);
            Check(reloaded.Events.Count == 1 && reloaded.Events[0].Kind == "user_note" && reloaded.LastEventSequence == 1, "Event did not survive reload");
            return Task.CompletedTask;
        });
    }
}
