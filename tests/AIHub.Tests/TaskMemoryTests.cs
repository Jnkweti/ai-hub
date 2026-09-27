using AIHub.Core;

internal static class TaskMemoryTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "task-test-" + Guid.NewGuid().ToString("N"));
        public LocalStore Store { get; }
        public TaskMemory Memory { get; }
        public Fixture() { Store = new(Root); Memory = new(Store); }
        public string Create(string room = "room", string objective = "Inspect this task") => Memory.Create(room, Root, objective);
        public void Dispose() => Directory.Delete(Root, true);
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("durable task notes and replies survive restart without auto-starting workers", () =>
        {
            using var f = new Fixture(); var id = f.Create();
            f.Memory.AddNote(id, "Keep the interface keyboard accessible.");
            var claim = f.Memory.Begin(id, true); f.Memory.Own(claim, Agent.Claude); f.Memory.Reply(claim, Agent.Claude, "Found a keyboard issue.");
            // Simulates a new application after the previous instance has exited.
            var restored = new TaskMemory(f.Store); var task = restored.Get(id)!;
            Check(task.State == WorkState.Interrupted && task.Owner == "", "A stale worker was revived");
            Check(task.Notes.Single().Text.Contains("keyboard") && task.LatestReplies[Agent.Claude].Contains("issue"), "Durable memory was lost");
            var next = restored.Begin(id, true); Check(next.Generation > claim.Generation, "Restart reused an old generation");
            restored.End(next, WorkState.Stopped, "Reviewed"); return Task.CompletedTask;
        });
        await test("exclusive task and workspace editing claims allow independent read tasks", () =>
        {
            using var f = new Fixture(); var a = f.Create("a"); var b = f.Create("b");
            var claim = f.Memory.Begin(a, true);
            var rejected = 0;
            try { f.Memory.Begin(a, false); } catch (InvalidOperationException) { rejected++; }
            try { f.Memory.Begin(b, true); } catch (InvalidOperationException) { rejected++; }
            Check(rejected == 2, "Duplicate task or workspace editor accepted");
            var read = f.Memory.Begin(b, false); f.Memory.End(read, WorkState.Ready, "Read ended");
            f.Memory.End(claim, WorkState.Ready, "Editor ended");
            var next = f.Memory.Begin(b, true); f.Memory.End(next, WorkState.Ready, "Done"); return Task.CompletedTask;
        });
        await test("only current owner and generation can publish task results", () =>
        {
            using var f = new Fixture(); var id = f.Create(); var old = f.Memory.Begin(id, false);
            f.Memory.Own(old, Agent.Codex);
            Check(!f.Memory.Reply(old, Agent.Claude, "Wrong owner"), "Non-owner published");
            f.Memory.End(old, WorkState.Stopped, "Stopped"); var next = f.Memory.Begin(id, false); f.Memory.Own(next, Agent.Claude);
            Check(!f.Memory.End(old, WorkState.Ready, "Late") && !f.Memory.Reply(old, Agent.Codex, "Late"), "Stale generation overwrote new work");
            Check(f.Memory.Get(id)!.State == WorkState.Running && f.Memory.Get(id)!.Owner == "Claude", "Current ownership changed");
            f.Memory.End(next, WorkState.Ready, "Done"); return Task.CompletedTask;
        });
        await test("saved task briefings are bounded and isolate unrelated task notes", () =>
        {
            using var f = new Fixture(); var a = f.Create("a", "Objective A"); var b = f.Create("b", "Objective B");
            f.Memory.AddNote(b, "UNRELATED-PRIVATE-TASK");
            for (var i = 0; i < 25; i++) f.Memory.AddNote(a, "Note " + i + new string('x', 3900));
            var run = f.Memory.Begin(a, false); f.Memory.Own(run, Agent.Codex); f.Memory.Reply(run, Agent.Codex, new string('z', 15000));
            var brief = f.Memory.Briefing(a);
            Check(brief.Length < 12000 && !brief.Contains("UNRELATED-PRIVATE-TASK") && brief.Contains("may be stale"), "Unbounded, cross-task or unqualified briefing");
            Check(f.Memory.Get(a)!.Notes.Count == 25 && f.Memory.Get(a)!.LatestReplies[Agent.Codex].Length < 6100, "Original notes were dropped or saved reply excerpt was unbounded");
            f.Memory.End(run, WorkState.Ready, "Done"); return Task.CompletedTask;
        });
        await test("failed durable writes prevent dispatch and roll back task state", () =>
        {
            using var f = new Fixture(); var id = f.Create();
            using (var locked = new FileStream(Path.Combine(f.Root, "tasks.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = false; try { f.Memory.Begin(id, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                Check(failed && f.Memory.Get(id)!.State == WorkState.Ready, "Claim proceeded after persistence failure");
            }
            var claim = f.Memory.Begin(id, true); f.Memory.End(claim, WorkState.Ready, "Done"); return Task.CompletedTask;
        });
        await test("deleting conversation removes only its tasks and preserves notes on failure", () =>
        {
            using var f = new Fixture(); var a = f.Create("a"); var b = f.Create("b"); f.Memory.AddNote(a, "Keep this note on failure");
            try { f.Memory.DeleteRoom("a", () => throw new IOException("Conversation cannot be deleted")); } catch (IOException) { }
            Check(f.Memory.Get(a)!.Notes.Count == 1, "Deletion failure lost notes");
            var claim = f.Memory.Begin(a, false); var refused = false;
            try { f.Memory.DeleteRoom("a"); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Live task was deleted"); f.Memory.End(claim, WorkState.Stopped, "Stopped");
            f.Memory.DeleteRoom("a"); Check(f.Memory.Get(a) is null && f.Memory.Get(b) is not null, "Task deletion exceeded conversation scope"); return Task.CompletedTask;
        });
        await test("coordinator saves task ownership and reports failure without claiming completion", async () =>
        {
            using var f = new Fixture(); var id = f.Create(); var tracker = new Tracker { Delay = 120 };
            await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker) { Fail = true })
                { TaskMemory = f.Memory, TaskId = id, AutoExchange = false };
            await hub.SubmitAsync("Inspect", "Codex");
            await Until(() => f.Memory.Get(id)!.State == WorkState.Failed);
            Check(f.Memory.Get(id)!.LatestReplies.Count == 0, "Failure fabricated a result");
        });
        await test("coordinator task briefing and saved peer replies stay tied to the current task", async () =>
        {
            using var f = new Fixture(); var id = f.Create(); f.Memory.AddNote(id, "Keyboard constraint"); var tracker = new Tracker();
            await using var hub = new HubCoordinator(a => new FakeAgent(a, tracker)) { TaskMemory = f.Memory, TaskId = id, AutoExchange = false };
            await hub.SubmitAsync("Inspect", "Both"); await Until(() => f.Memory.Get(id)!.State == WorkState.Ready);
            Check(tracker.Prompts.All(p => p.Contains("Keyboard constraint")), "Task notes were not supplied");
            Check(f.Memory.Get(id)!.LatestReplies.Count == 2 && f.Memory.Get(id)!.Owner == "", "Replies or ownership were not persisted");
        });
        await test("stopped worker keeps its editing claim until actual cleanup finishes", async () =>
        {
            using var f = new Fixture(); var a = f.Create("a"); var b = f.Create("b"); var agent = new SlowWorker();
            await using var hub = new HubCoordinator(_ => agent) { TaskMemory = f.Memory, TaskId = a, AllowEdits = true, AutoExchange = false };
            await hub.SubmitAsync("Inspect", "Codex"); await agent.Started.Task;
            await hub.StopAsync(); // Deliberately exceeds the coordinator's five-second wait.
            var refused = false; try { f.Memory.Begin(b, true); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Replacement editor started while the old worker was still alive");
            agent.Release.TrySetResult(); await Until(() => f.Memory.Get(a)!.State == WorkState.Stopped);
            var next = f.Memory.Begin(b, true); f.Memory.End(next, WorkState.Ready, "Done");
            Check(f.Memory.Get(a)!.LatestReplies.Count == 0, "Cancelled worker published a late reply");
        });
    }
    private sealed class SlowWorker : IAgentClient
    {
        public Agent Agent => Agent.Codex;
        public string? SessionId => "slow";
        public event Action<AgentEvent>? Event { add { } remove { } }
        public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentReply> SendAsync(string prompt, CancellationToken token) { Started.TrySetResult(); await Release.Task; return new("Late reply", SessionId); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
