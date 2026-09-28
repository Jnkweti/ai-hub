using AIHub.Core;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

// Fixture workspaces under ignored artifacts are independent projects, not children of the source repository.
var fixtureCeiling = Path.Combine(CollaborationTests.Root, "artifacts");
Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", string.Join(Path.PathSeparator,
    new[] { fixtureCeiling, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES") }.Where(s => !string.IsNullOrEmpty(s))));

if (args.Length == 2 && args[0] == "--collaboration-export")
{ CollaborationTests.Export(args[1]); return; }
if (args.Length == 2 && args[0] == "--collaboration-live")
{ await CollaborationLiveCheck.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--collaboration-routing-live")
{ await CollaborationRoutingLiveCheck.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--collaboration-workflow-live")
{ await CollaborationWorkflowLiveCheck.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--shared-conversation-live")
{ await SharedConversationLiveCheck.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--quiet-peer-live")
{ await SharedConversationLiveCheck.Run(args[1], conciseOnly: true); return; }
if (args.Length == 2 && args[0] == "--shared-context-live")
{ await SharedContextLiveCheck.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--live-stream-live")
{ await LiveStreamLiveCheck.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--shared-work-live")
{ await SharedWorkLiveCheck.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--claude-work-live")
{ await SharedWorkLiveCheck.RunClaude(args[1]); return; }
if (args.Length == 1 && args[0] == "--collaboration-tests")
{
    await CollaborationTests.Run(async (name, test) => { await test(); Console.WriteLine("PASS " + name); }); return;
}
if (args.Length == 1 && args[0] == "--collaboration-routing-tests")
{ await CollaborationRoutingTests.Run(async (name, test) => { await test(); Console.WriteLine("PASS " + name); }); return; }
if (args.Length == 1 && args[0] == "--shared-context-tests")
{ await SharedContextTests.Run(async (name, test) => { await test(); Console.WriteLine("PASS " + name); }); return; }
if (args.Length == 1 && args[0] == "--task-context-tests")
{ await TaskContextTests.Run(async (name, test) => { await test(); Console.WriteLine("PASS " + name); }); return; }
if (args.Length == 1 && args[0] == "--reliability-tests")
{ await ReliabilityTests.Run(async (name, test) => { await test(); Console.WriteLine("PASS " + name); }); await ContextSourceTests.Run(async (name, test) => { await test(); Console.WriteLine("PASS " + name); }); return; }
if (args.Length == 2 && args[0] == "--profile-source-recovery")
{ ContextSourceTests.VerifyProfileCopy(args[1]); return; }
if (args.Length == 2 && args[0] == "--context-source-live")
{ await ContextSourceLiveCheck.Run(args[1]); return; }
if (args.Length == 1 && args[0] == "--concurrent-work-tests")
{ await ConcurrentWorkTests.Run(async (name, test) => { await test(); Console.WriteLine("PASS " + name); }); return; }

if (args.Length == 2 && args[0] == "--wire-audit")
{
    await AuditTests.WireFixture(args[1]); return;
}
if (args.Length == 2 && args[0] == "--instance-lease")
{
    using var lease = AppInstanceLease.TryAcquire(args[1]);
    Console.WriteLine(lease is null ? "BUSY" : "ACQUIRED");
    if (lease is not null) await Task.Delay(Timeout.Infinite); return;
}
if (args.Length == 2 && args[0] == "--status-live")
{
    await StatusLiveCheck.RunAsync(args[1]); return;
}
if (args.Length == 3 && args[0] == "--status-claim")
{
    var statusStore = new ProjectStatusStore(args[1]);
    await using var claim = await statusStore.ClaimAsync(args[2], CancellationToken.None);
    Console.WriteLine("CLAIMED"); await Task.Delay(Timeout.Infinite); return;
}
if (args.Contains("app-server") || args.Contains("--print"))
{
    await FakeWire.Run(args.Contains("--print")); return;
}
if (args.Contains("--connect") || args.Contains("--live"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    foreach (var agent in new[] { Agent.Codex, Agent.Claude })
    {
        await using IAgentClient client = agent == Agent.Codex ? new CodexClient(new(workspace, false)) : new ClaudeClient(new(workspace, false));
        client.Event += e => { if (e.Kind is EventKind.Error or EventKind.Status) Console.WriteLine($"{agent}: {e.Text}"); };
        if (args.Contains("--live"))
        {
            var marker = "AIHUB-" + Guid.NewGuid().ToString("N")[..8];
            var reply = await client.SendAsync("AI Hub connectivity test. Do not use any tools or modify any files throughout this test. Remember the continuity marker " + marker + ". Reply with exactly: AI Hub connected.", timeout.Token);
            Console.WriteLine($"LIVE {agent}: {reply.Text}");
            reply = await client.SendAsync("Reply with only the continuity marker I asked you to remember.", timeout.Token);
            if (!reply.Text.Contains(marker)) throw new Exception(agent + " lost second-turn context");
            var session = client.SessionId;
            await client.DisposeAsync();
            await using IAgentClient resumed = agent == Agent.Codex ? new CodexClient(new(workspace, false), session) : new ClaudeClient(new(workspace, false), session);
            reply = await resumed.SendAsync("Reconnection check. Without tools, reply with only the continuity marker I asked you to remember.", timeout.Token);
            if (!reply.Text.Contains(marker)) throw new Exception(agent + " lost resumed context");
            Console.WriteLine($"PASS {agent} second turn and session resume");
        }
        else if (client is CodexClient c) await c.ConnectAsync(timeout.Token);
        else if (client is ClaudeClient a) await a.ConnectAsync(timeout.Token);
        Console.WriteLine($"PASS {agent} connection");
    }
    return;
}

// --only <text> runs the tests whose names contain <text>; --repeat <n> runs each selected test n times.
var only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault();
var repeat = int.TryParse(args.SkipWhile(a => a != "--repeat").Skip(1).FirstOrDefault(), out var count) ? Math.Clamp(count, 1, 1000) : 1;
var passed = 0;
async Task Test(string name, Func<Task> test)
{
    if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return;
    for (var i = 0; i < repeat; i++) { await test(); passed++; Console.WriteLine("PASS " + name + (repeat > 1 ? $" [{i + 1}/{repeat}]" : "")); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void DeleteFixtureDirectory(string root)
{
    var resolved = Path.GetFullPath(root);
    Check(string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase), "Unsafe fixture cleanup");
    Directory.Delete(resolved, true);
}
async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!condition()) await Task.Delay(10, timeout.Token);
}

await Test("shared discussion is sequential and the peer receives the completed first reply", async () =>
{
    var tracker = new Tracker();
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker)) { AutoExchange = false };
    await hub.SubmitAsync("Review the design", "Both"); await Until(() => tracker.Completed == 2);
    Check(tracker.Maximum == 1, "Agents spoke concurrently");
    Check(tracker.Prompts.Count == 2 && tracker.Prompts.First().Contains("USER MESSAGE:\nReview the design") && !tracker.Prompts.Last().Contains("USER MESSAGE:\n"), "Peer received a duplicate independent user assignment");
    Check(tracker.Prompts.Last().Contains("Codex found new evidence in step 1."), "Peer spoke before receiving the first answer");
});
await Test("project editing serializes agents and shares first result", async () =>
{
    var tracker = new Tracker();
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker)) { AutoExchange = false, AllowEdits = true };
    await hub.SubmitAsync("work", "Both"); await Until(() => tracker.Completed == 2);
    Check(tracker.Maximum == 1, "Concurrent writers");
    Check(tracker.Prompts.Last().Contains("Codex found new evidence in step 1."), "Second writer did not see first writer's work");
});
await Test("productive automatic exchange continues until explicit stop", async () =>
{
    var tracker = new Tracker();
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker));
    await hub.SubmitAsync("discuss", "Both"); await Until(() => tracker.Completed >= 8);
    await hub.StopAsync(); var stopped = tracker.Started; await Task.Delay(500);
    Check(tracker.Started == stopped && tracker.Active == 0, "Work continued after stop");
    Check(tracker.Prompts.Any(p => p.Contains("PEER MESSAGE FROM Claude Code")), "No Claude-to-Codex relay");
    Check(tracker.Prompts.Any(p => p.Contains("PEER MESSAGE FROM Codex")), "No Codex-to-Claude relay");
});
await Test("new user message interrupts previous exchange", async () =>
{
    var tracker = new Tracker { Delay = 250 };
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker));
    await hub.SubmitAsync("old", "Both"); await Until(() => tracker.Started == 1);
    hub.AutoExchange = false; await hub.SubmitAsync("replacement", "Codex");
    await Until(() => tracker.Completed == 1);
    Check(tracker.Prompts.Last().Contains("replacement"), "New instruction not routed");
    Check(tracker.Cancelled == 1 && tracker.Started == 2, "Old turn or queued peer survived interruption");
});
await Test("provider failure stops the exchange before starting the queued peer", async () =>
{
    var tracker = new Tracker(); var failed = false;
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker) { Fail = a == Agent.Codex, Hang = a == Agent.Claude });
    hub.Event += e => { if (e.Kind == EventKind.Error) failed = true; };
    await hub.SubmitAsync("fail", "Both"); await Until(() => failed);
    Check(tracker.Active == 0 && tracker.Started == 1, "Peer started after a failed turn");
});
await Test("targeted send does not start automatic peer exchange", async () =>
{
    var tracker = new Tracker();
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker));
    await hub.SubmitAsync("only", "Claude"); await Until(() => tracker.Completed == 1); await Task.Delay(120);
    Check(tracker.Started == 1, "Targeted send leaked into auto exchange");
});
foreach (var greeting in new[] { "hello", "Hi everyone!", "hey codex", "Thanks both!", "How are you?" })
{
    await Test("greeting gets one shared answer: " + greeting, async () =>
    {
        var tracker = new Tracker(); string? reason = null; string? state = null;
        await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker));
        hub.AutoPaused += r => reason = r; hub.State += s => state = s;
        await hub.SubmitAsync(greeting, "Both"); await Until(() => reason is not null); await Task.Delay(100);
        Check(tracker.Started == 1 && hub.ExchangeCount == 0, "Greeting triggered a duplicate reply");
        Check(state!.StartsWith("Paused"), "Pause reason was overwritten with Ready");
    });
}
await Test("a greeting followed by real work still collaborates", async () =>
{
    var tracker = new Tracker(); var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker)) { MaxAutoRounds = 1 };
    hub.Event += e => { if (e.Kind == EventKind.Error) Console.Error.WriteLine(e.Text); };
    hub.AutoPaused += _ => paused.TrySetResult();
    await hub.SubmitAsync("Hello, review the project and compare your findings.", "Both");
    await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(tracker.Completed == 4, "Real work was mistaken for small talk");
});
await Test("a repeated reply pauses before another handoff", async () =>
{
    var tracker = new Tracker(); string? reason = null;
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker) { Reply = _ => "I agree. We are ready whenever you are." });
    hub.AutoPaused += r => reason = r;
    await hub.SubmitAsync("Discuss the design", "Both"); await Until(() => reason is not null);
    Check(tracker.Completed == 3 && reason!.Contains("repeated"), "Repetitive chatter continued");
});
await Test("near-repeat detection preserves new numerical evidence", () =>
{
    var first = "The design is ready for review and the remaining work is to agree on the next step together.";
    Check(CollaborationGuard.IsNearRepeat(first, first + " Agreed."), "Near duplicate not detected");
    Check(!CollaborationGuard.IsNearRepeat(first + " Tests passed: 12.", first + " Tests passed: 13."), "New evidence was discarded");
    return Task.CompletedTask;
});
await Test("both completion reports pause immediately", async () =>
{
    var tracker = new Tracker(); string? reason = null;
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker) { Reply = _ => "The checks passed.\n\nTask complete." });
    hub.AutoPaused += r => reason = r;
    await hub.SubmitAsync("Check the implementation", "Both"); await Until(() => reason is not null);
    Check(tracker.Completed == 2 && reason!.Contains("complete"), "Completed work continued");
});
await Test("quoted completion examples do not pause real work", () =>
{
    Check(!CollaborationGuard.IsComplete("Example status:\n> Task complete."), "A block quote was treated as completion");
    Check(!CollaborationGuard.IsComplete("Use `Task complete.`:\n`Task complete.`"), "Inline code was treated as completion");
    Check(CollaborationGuard.IsComplete("**Task complete.**"), "Formatted actual completion was ignored");
    return Task.CompletedTask;
});
await Test("a request for user input pauses the conversation", async () =>
{
    var tracker = new Tracker(); string? reason = null;
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker) { Reply = a == Agent.Claude ? _ => "Which project should we use?\n\nWaiting for your input." : null });
    hub.AutoPaused += r => reason = r;
    await hub.SubmitAsync("Plan the work", "Both"); await Until(() => reason is not null);
    Check(tracker.Completed == 2 && reason!.Contains("input"), "Agents answered on the user's behalf");
});
await Test("one agent finishing does not silence unfinished peer review", async () =>
{
    var tracker = new Tracker(); var paused = false;
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker) { Reply = a == Agent.Claude ? _ => "Task complete." : null }) { MaxAutoRounds = 1 };
    hub.AutoPaused += _ => paused = true;
    await hub.SubmitAsync("Review the implementation together", "Both"); await Until(() => paused);
    Check(tracker.Completed == 4, "Peer review was cut off by one completion report");
});
await Test("tool activity permits repeated summaries but cannot bypass the round cap", async () =>
{
    var tracker = new Tracker(); string? reason = null;
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker) { Reply = _ => "The check succeeded.", UsedTools = true }) { MaxAutoRounds = 2 };
    hub.AutoPaused += r => reason = r;
    await hub.SubmitAsync("Run checks", "Both"); await Until(() => reason is not null);
    Check(tracker.Completed == 6 && reason!.Contains("2-round"), "Tool activity bypassed the cap or was treated as idle chatter");
});
await Test("default six-round backstop stops varied chatter", async () =>
{
    var tracker = new Tracker(); string? reason = null;
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker));
    hub.AutoPaused += r => reason = r;
    await hub.SubmitAsync("Compare approaches", "Both"); await Until(() => reason is not null);
    Check(tracker.Completed == 14 && hub.ExchangeCount == 6 && reason!.Contains("6-round"), "Fallback limit failed");
});
await Test("a new task resumes after an automatic pause", async () =>
{
    var tracker = new Tracker(); var pauses = 0;
    await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker)) { MaxAutoRounds = 1 };
    hub.AutoPaused += _ => pauses++;
    await hub.SubmitAsync("hello", "Both"); await Until(() => pauses == 1);
    await hub.SubmitAsync("Review the source", "Both"); await Until(() => pauses == 2);
    Check(tracker.Completed == 5 && hub.ExchangeCount == 1, "New task did not reset the guard");
});
await Test("local persistence roundtrip and corrupt-file preservation", async () =>
{
    var root = Path.Combine(AppContext.BaseDirectory, "store-test-" + Guid.NewGuid());
    var store = new LocalStore(root); store.Save("test.json", new Room { Title = "Notes — 日本語" });
    Check(store.Load("test.json", () => new Room()).Title == "Notes — 日本語", "Unicode persistence failed");
    File.WriteAllText(Path.Combine(root, "test.json"), "broken");
    _ = store.Load("test.json", () => new Room());
    Check(Directory.GetFiles(root, "*.unreadable-*").Length == 1, "Corrupt original not preserved");
    Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory)), "Unsafe test cleanup");
    Directory.Delete(root, true); await Task.CompletedTask;
});
await Test("older conversations load active and archive state preserves room context", async () =>
{
    var root = Path.Combine(AppContext.BaseDirectory, "archive-test-" + Guid.NewGuid().ToString("N"));
    var store = new LocalStore(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "rooms.json"), "[{\"Id\":\"legacy-room\",\"Title\":\"Legacy\",\"Draft\":\"Keep my draft\",\"Target\":\"Claude\",\"CodexSession\":\"provider-session\"}]");
        var rooms = store.Load("rooms.json", () => new List<Room>());
        Check(rooms.Count == 1 && !rooms[0].IsArchived, "Older room did not default to active");
        rooms[0].IsArchived = true;
        rooms[0].Messages.Add(new SavedMessage { Text = "Saved transcript" });
        store.Save("rooms.json", rooms);
        var archived = store.Load("rooms.json", () => new List<Room>())[0];
        Check(archived.IsArchived && archived.Draft == "Keep my draft" && archived.Target == "Claude" && archived.CodexSession == "provider-session" && archived.Messages.Single().Text == "Saved transcript", "Archive lost conversation context");
        archived.IsArchived = false; store.Save("rooms.json", new[] { archived });
        Check(!store.Load("rooms.json", () => new List<Room>())[0].IsArchived, "Restore did not persist");
    }
    finally { DeleteFixtureDirectory(root); }
    await Task.CompletedTask;
});
await Test("delete removes only the selected local conversation and exact activity log", async () =>
{
    var root = Path.Combine(AppContext.BaseDirectory, "delete-test-" + Guid.NewGuid().ToString("N"));
    var store = new LocalStore(root);
    try
    {
        var rooms = new[] { new Room { Id = "selected", Draft = "Remove draft" }, new Room { Id = "selected-other", IsArchived = true, Draft = "Keep draft" } };
        store.Save("rooms.json", rooms); store.Save("settings.json", new { LastRoomId = "selected" });
        store.AppendActivity("selected", new(Agent.Codex, EventKind.Tool, "Selected log"));
        store.AppendActivity("selected-other", new(Agent.Claude, EventKind.Tool, "Unrelated log"));
        var projectPath = Path.Combine(root, "project.txt"); File.WriteAllText(projectPath, "Project and provider files stay intact");
        store.DeleteRoom(rooms, "selected");
        var remaining = store.Load("rooms.json", () => new List<Room>());
        Check(remaining.Count == 1 && remaining[0].Id == "selected-other" && remaining[0].IsArchived && remaining[0].Draft == "Keep draft", "Another conversation changed");
        Check(!File.Exists(Path.Combine(root, "activity-selected.jsonl")), "Selected activity log remains");
        Check(File.Exists(Path.Combine(root, "activity-selected-other.jsonl")) && File.ReadAllText(projectPath) == "Project and provider files stay intact" && File.Exists(Path.Combine(root, "settings.json")), "Delete touched unrelated files");
        store.DeleteRoom(remaining, "selected-other");
        var noLogRoom = new Room { Id = "no-log" }; store.DeleteRoom(new[] { noLogRoom }, noLogRoom.Id);
        Check(store.Load("rooms.json", () => new List<Room>()).Count == 0, "Deleting a room without a log failed");
    }
    finally { DeleteFixtureDirectory(root); }
    await Task.CompletedTask;
});
await Test("delete rejects unsafe or ambiguous identifiers before changing persisted data", async () =>
{
    var root = Path.Combine(AppContext.BaseDirectory, "delete-scope-test-" + Guid.NewGuid().ToString("N"));
    var store = new LocalStore(root);
    try
    {
        var safe = new Room { Id = "safe-room", Draft = "Unchanged" }; store.Save("rooms.json", new[] { safe });
        var before = File.ReadAllText(Path.Combine(root, "rooms.json"));
        foreach (var id in new[] { "../outside", "..\\outside", "C:\\outside", "*", "", "foo:bar", "foo/bar", "foo\\bar" })
        {
            try { store.DeleteRoom(new[] { new Room { Id = id } }, id); throw new Exception("Unsafe delete was accepted"); }
            catch (IOException) { }
            Check(File.ReadAllText(Path.Combine(root, "rooms.json")) == before, "Rejected delete changed the transcript store");
        }
        try { store.DeleteRoom(new[] { safe, safe }, safe.Id); throw new Exception("Ambiguous delete was accepted"); }
        catch (InvalidOperationException) { }
        Check(File.ReadAllText(Path.Combine(root, "rooms.json")) == before, "Ambiguous delete changed persisted data");
    }
    finally { DeleteFixtureDirectory(root); }
    await Task.CompletedTask;
});
await Test("locked activity log leaves a failed deletion recoverable", async () =>
{
    if (!OperatingSystem.IsWindows()) return;
    var root = Path.Combine(AppContext.BaseDirectory, "delete-lock-test-" + Guid.NewGuid().ToString("N"));
    var store = new LocalStore(root);
    try
    {
        var room = new Room { Id = "locked", Draft = "Recover me" }; store.Save("rooms.json", new[] { room });
        store.AppendHandoff(room.Id, "You", "Codex", "Keep activity");
        using (File.Open(Path.Combine(root, "activity-locked.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { store.DeleteRoom(new[] { room }, room.Id); throw new Exception("Locked log delete unexpectedly succeeded"); }
            catch (IOException) { }
        }
        Check(store.Load("rooms.json", () => new List<Room>()).Single().Draft == "Recover me", "Failed deletion lost its transcript");
        Check(File.Exists(Path.Combine(root, "activity-locked.jsonl")), "Failed deletion lost its log");
    }
    finally { DeleteFixtureDirectory(root); }
    await Task.CompletedTask;
});
foreach (var agent in new[] { Agent.Codex, Agent.Claude })
{
    await Test(agent + " real pipe protocol, streaming, approvals and second turn", async () =>
    {
        var executable = Environment.ProcessPath!;
        await using IAgentClient client = agent == Agent.Codex ? new CodexClient(new(AppContext.BaseDirectory, true, Executable: executable)) : new ClaudeClient(new(AppContext.BaseDirectory, true, Executable: executable));
        var approvals = 0; var deltas = new List<string>();
        client.RequestApproval = (a, ct) => { approvals++; return Task.FromResult(new Decision(true)); };
        client.Event += e => { if (e.Kind == EventKind.TextDelta) deltas.Add(e.Text); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await client.SendAsync("approval", timeout.Token);
        Check(result.Text == "Approved result", "Incorrect final reply: " + result.Text);
        Check(approvals == 1 && deltas.Count > 0 && client.SessionId is not null, "Missing approval/stream/session");
        result = await client.SendAsync("second", timeout.Token);
        Check(result.Text == "Second result", "Second turn failed");
    });
    await Test(agent + " approval denial reaches the subprocess", async () =>
    {
        await using IAgentClient client = agent == Agent.Codex ? new CodexClient(new(AppContext.BaseDirectory, true, Executable: Environment.ProcessPath!)) : new ClaudeClient(new(AppContext.BaseDirectory, true, Executable: Environment.ProcessPath!));
        client.RequestApproval = (_, _) => Task.FromResult(new Decision(false));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await client.SendAsync("decline", timeout.Token);
        Check(result.Text == "Declined result", "Denied action was allowed");
    });
    await Test(agent + " stop cancels a pending approval", async () =>
    {
        await using IAgentClient client = agent == Agent.Codex ? new CodexClient(new(AppContext.BaseDirectory, true, Executable: Environment.ProcessPath!)) : new ClaudeClient(new(AppContext.BaseDirectory, true, Executable: Environment.ProcessPath!));
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RequestApproval = async (_, ct) => { requested.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new(false); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = client.SendAsync("pending", timeout.Token);
        await requested.Task.WaitAsync(timeout.Token); timeout.Cancel();
        try { await task; throw new Exception("Stopped turn returned a reply"); }
        catch (OperationCanceledException) { }
    });
}
await ProjectStatusTests.Run(Test);
await ActivityFeedTests.Run(Test);
await InputQuestionTests.Run(Test);
await ConversationTurnTests.Run(Test);
await AuditTests.Run(Test);
await TaskMemoryTests.Run(Test);
await CollaborationTests.Run(Test);
await CollaborationRoutingTests.Run(Test);
await FollowUpAuditTests.Run(Test);
await LiveStreamTests.Run(Test);
await SharedContextTests.Run(Test);
await TaskContextTests.Run(Test);
await ReliabilityTests.Run(Test);
await ContextSourceTests.Run(Test);
await ConcurrentWorkTests.Run(Test);
await CollaborationEvidenceTests.Run(Test);
Console.WriteLine($"\n{passed} tests passed.");

sealed class Tracker
{
    public int Active, Maximum, Started, Completed, Cancelled, Delay = 60;
    public ConcurrentQueue<string> Prompts = new();
}
sealed class FakeAgent(Agent agent, Tracker tracker) : IAgentClient
{
    private int count;
    public Agent Agent => agent;
    public string? SessionId => "test-session";
    public event Action<AgentEvent>? Event;
    public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
    public bool Fail { get; init; }
    public bool Hang { get; init; }
    public Func<int, string>? Reply { get; init; }
    public bool UsedTools { get; init; }
    public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
    {
        tracker.Prompts.Enqueue(prompt); Interlocked.Increment(ref tracker.Started);
        var active = Interlocked.Increment(ref tracker.Active); tracker.Maximum = Math.Max(tracker.Maximum, active);
        try
        {
            await Task.Delay(Hang ? Timeout.Infinite : tracker.Delay, token);
            if (Fail) throw new IOException("test failure");
            count++;
            var body = Reply?.Invoke(count) ?? $"{agent} found new evidence in step {count}.";
            if (UsedTools) Event?.Invoke(new(agent, EventKind.Tool, "test command", "tool-" + count));
            Interlocked.Increment(ref tracker.Completed); Event?.Invoke(new(agent, EventKind.Message, body));
            return new(body, SessionId);
        }
        catch (OperationCanceledException) { Interlocked.Increment(ref tracker.Cancelled); throw; }
        finally { Interlocked.Decrement(ref tracker.Active); }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
static class FakeWire
{
    static void Emit(object data) => Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(data));
    public static async Task Run(bool claude)
    {
        var turn = 0;
        var readOnly = false;
        var inputMode = "";
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var m = JsonNode.Parse(line)!;
            if (claude)
            {
                if (m.Str("type") == "control_request") Emit(new { type = "control_response", response = new { subtype = "success", request_id = m.Str("request_id"), response = new { } } });
                else if (m.Str("type") == "user")
                {
                    turn++;
                    Emit(new { type = "system", subtype = "init", session_id = "fake-claude" });
                    if (m["message"].Str("content") == "multipart-reply-fixture")
                    {
                        Emit(new { type = "assistant", message = new { id = "first", content = new[] { new { type = "text", text = "Substantive first finding." } } } });
                        Emit(new { type = "assistant", message = new { id = "second", content = new[] { new { type = "text", text = "Additional constraint." } } } });
                        Emit(new { type = "assistant", message = new { id = "duplicate", content = new[] { new { type = "text", text = "Final acknowledgement." } } } });
                        ClaudeResult("Final acknowledgement.");
                    }
                    else if (m["message"].Str("content").StartsWith("AI HUB PROJECT STATUS"))
                    {
                        if (!Environment.GetCommandLineArgs().Contains("plan")) throw new Exception("Status worker is not read only");
                        ClaudeResult(ProjectStatusTests.Report);
                    }
                    else if (m["message"].Str("content").Contains("activity-fixture")) ActivityNoise(true);
                    else if (m["message"].Str("content").Contains("task-fixture"))
                    {
                        var prompt = m["message"].Str("content");
                        await Task.Delay(prompt.Contains("task-fixture-hold") ? 15000 : 1800);
                        ClaudeResult(TaskFixtureReply(prompt));
                    }
                    else if (m["message"].Str("content").Contains("input-fixture"))
                    {
                        inputMode = InputQuestionTests.Mode(m["message"].Str("content"));
                        if (await InputQuestionTests.EmitRequest(true, inputMode)) ClaudeResult("Question withdrawn");
                    }
                    else if (turn == 1) Emit(new { type = "control_request", request_id = "approve", request = new { subtype = "can_use_tool", tool_name = "Write", input = new { file_path = "test.txt", content = "test" } } });
                    else ClaudeResult("Second result");
                }
                else if (m.Str("type") == "control_response") ClaudeResult(m["response"].Str("request_id") == "question" ? InputQuestionTests.Reply(true, m, inputMode) : m["response"]?["response"].Str("behavior") == "allow" ? "Approved result" : "Declined result");
            }
            else
            {
                var method = m.Str("method"); var id = m["id"]?.DeepClone();
                if (method == "initialize") Emit(new { id, result = new { userAgent = "fixture" } });
                else if (method is "thread/start" or "thread/resume") { readOnly = m["params"].Str("sandbox") == "read-only"; Emit(new { id, result = new { thread = new { id = "fake-codex" } } }); }
                else if (method == "turn/start")
                {
                    turn++; Emit(new { id, result = new { turn = new { id = "t" + turn } } });
                    if (m["params"]?["input"]?[0].Str("text") == "multipart-reply-fixture")
                    {
                        Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "fake-codex", itemId = "first", delta = "First" } });
                        Emit(new { method = "item/completed", @params = new { threadId = "fake-codex", item = new { type = "agentMessage", id = "first", text = "First finding." } } });
                        CodexResult("Final finding.");
                    }
                    else if (m["params"]?["input"]?[0].Str("text") == "provider-interrupted-fixture")
                        Emit(new { method = "turn/completed", @params = new { threadId = "fake-codex", turn = new { id = "t1", status = "interrupted" } } });
                    else if (m["params"]?["input"]?[0].Str("text").StartsWith("AI HUB PROJECT STATUS") == true)
                    {
                        if (!readOnly) throw new Exception("Status worker is not read only");
                        CodexResult(ProjectStatusTests.Report);
                    }
                    else if (m["params"]?["input"]?[0].Str("text").Contains("activity-fixture") == true) ActivityNoise(false);
                    else if (m["params"]?["input"]?[0].Str("text").Contains("task-fixture") == true)
                    {
                        var prompt = m["params"]?["input"]?[0].Str("text") ?? "";
                        await Task.Delay(prompt.Contains("task-fixture-hold") ? 15000 : 1800);
                        CodexResult(TaskFixtureReply(prompt));
                    }
                    else if (m["params"]?["input"]?[0].Str("text").Contains("input-fixture") == true)
                    {
                        inputMode = InputQuestionTests.Mode(m["params"]?["input"]?[0].Str("text") ?? "");
                        if (await InputQuestionTests.EmitRequest(false, inputMode)) CodexResult("Question withdrawn");
                    }
                    else if (turn == 1) Emit(new { id = "approve", method = "item/commandExecution/requestApproval", @params = new { command = "test", threadId = "fake-codex" } });
                    else CodexResult("Second result");
                }
                else if (m.Str("id") == "approve") CodexResult(m["result"].Str("decision") == "accept" ? "Approved result" : "Declined result");
                else if (m.Str("id") == "question") CodexResult(InputQuestionTests.Reply(false, m, inputMode));
            }
        }
    }
    static void ActivityNoise(bool claude)
    {
        if (claude)
        {
            Emit(new { type = "assistant", message = new { id = "activity-tools", content = new[] { new { type = "tool_use", name = "Read", id = "read", input = new { file_path = "README.md" } } } } });
            for (var i = 0; i < 240; i++) Emit(new { type = "system", subtype = "thinking_tokens", tokens = i });
            Emit(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "read", content = "Claude read output", is_error = false } } } });
            Emit(new { type = "assistant", message = new { id = "activity-error", content = new[] { new { type = "tool_use", name = "Read", id = "missing", input = new { file_path = "missing.cs" } } } } });
            Emit(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "missing", content = "Fixture file missing", is_error = true } } } });
            ClaudeResult("Activity fixture complete.\n\nTask complete.");
        }
        else
        {
            Emit(new { method = "item/started", @params = new { threadId = "fake-codex", item = new { type = "commandExecution", id = "read", status = "inProgress", commandActions = new[] { new { type = "read", name = "README.md" } } } } });
            for (var i = 0; i < 240; i++)
            {
                Emit(new { method = "item/commandExecution/outputDelta", @params = new { threadId = "fake-codex", itemId = "read", delta = i % 2 == 0 ? "Codex read output\n" : "\r\n" } });
                if (i % 15 == 0) Emit(new { method = "thread/tokenUsage/updated", @params = new { threadId = "fake-codex", tokenUsage = new { total = new { inputTokens = i, outputTokens = 20 } } } });
            }
            Emit(new { method = "item/completed", @params = new { threadId = "fake-codex", item = new { type = "commandExecution", id = "read", status = "completed", exitCode = 0, commandActions = new[] { new { type = "read", name = "README.md" } } } } });
            Emit(new { method = "item/completed", @params = new { threadId = "fake-codex", item = new { type = "commandExecution", id = "missing", status = "failed", exitCode = 2, commandActions = new[] { new { type = "read", name = "missing.cs" } } } } });
            CodexResult("Activity fixture complete.\n\nTask complete.");
        }
    }
    static string TaskFixtureReply(string prompt) => "Task fixture result. Note included: " + prompt.Contains("Saved task keyboard note") +
        ". Historical marker included: " + prompt.Contains("OLD-TASK-MARKER") + ".\n\nTask complete.";
    static void CodexResult(string body)
    {
        StructuredWireFixture.Complete(body);
        var id = Guid.NewGuid().ToString("N");
        Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "fake-codex", itemId = id, delta = body } });
        Emit(new { method = "item/completed", @params = new { threadId = "fake-codex", item = new { id, type = "agentMessage", text = body } } });
        Emit(new { method = "turn/completed", @params = new { threadId = "fake-codex", turn = new { status = "completed" } } });
    }
    static void ClaudeResult(string body)
    {
        StructuredWireFixture.Complete(body);
        var id = Guid.NewGuid().ToString("N");
        Emit(new { type = "stream_event", @event = new { type = "message_start", message = new { id } } });
        Emit(new { type = "stream_event", @event = new { type = "content_block_delta", delta = new { type = "text_delta", text = body } } });
        Emit(new { type = "assistant", message = new { id, content = new[] { new { type = "text", text = body } } } });
        Emit(new { type = "result", subtype = "success", is_error = false, result = body, session_id = "fake-claude" });
    }
}
