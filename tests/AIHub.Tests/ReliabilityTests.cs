using AIHub.Core;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

internal static class ReliabilityTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or CollaborationValidationException) { return; } throw new Exception("Invalid operation accepted"); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (var tool in new[] { "Bash", "PowerShell" })
        foreach (var exit in new int?[] { 0, null, 1 })
        await test($"Claude {tool} check evidence preserves exit status {exit?.ToString() ?? "unknown"}", () =>
        {
            using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Memory.Get(f.TaskId)!.Workspace, "source.txt"), "stable");
            var claim = f.Begin(Agent.Claude); var d = f.Dispatch(claim, Agent.Claude);
            JsonNode Call(string name, JsonObject args) => d.Call(Agent.Claude, d.Id, "s", name, args, default);
            var args = new JsonObject { ["kind"] = "check", ["operation"] = "test-command", ["scope"] = new JsonObject { ["files"] = new JsonArray("source.txt"), ["focus"] = new JsonArray() }, ["reusable"] = true, ["independent"] = false };
            var work = Call("claim_work", args);
            d.Observe(new(Agent.Claude, EventKind.Tool, tool, "cmd", "{\"command\":\"test-command\"}"));
            d.Observe(new(Agent.Claude, EventKind.ToolOutput, "output", "cmd", new JsonObject { ["isFinal"] = true, ["exitCode"] = exit, ["isError"] = exit == 1 }.ToJsonString()));
            var result = Call("complete_work", new JsonObject { ["work_id"] = work["work"]!.Str("id"), ["summary"] = "Observed result", ["evidence_refs"] = new JsonArray(f.Store.Read(f.TaskId).Evidence.Single().Id) });
            Check(result["work"]!.Bool("reusable") == (exit == 0) && result.Str("disposition") == (exit == 0 ? "completed" : "failed"), "Invalid exit status reuse");
            Check((Call("claim_work", args).Str("disposition") == "reused") == (exit == 0), "Unknown or failed check reused");
            d.Abort("done"); return Task.CompletedTask;
        });
        foreach (var raw in new[] { "{", "null", "{\"ContextFormat\":\"bad\"}" })
        await test("unparseable ledger remains blocked and byte preserved: " + raw, () =>
        {
            using var f = new Fixture(); var path = Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId)); File.WriteAllText(path, raw);
            Reject(() => new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.TaskId));
            var recovery = new CollaborationStore(f.Local, new TaskMemory(f.Local), preserveUnavailableTasks: true);
            Reject(() => recovery.Read(f.TaskId)); Check(File.ReadAllText(path) == raw, "Corrupt ledger overwritten"); return Task.CompletedTask;
        });
        await test("oversized agent originals import idempotently as marked excerpts and recover", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var original = new string('x', 150000) + "TAIL";
            f.Store.SynchronizeContext(claim, [new("big", "Codex", original)], "Review");
            f.Store.SynchronizeContext(claim, [new("big", "Codex", original)], "Review");
            var record = f.Store.Read(f.TaskId).ContextRecords.Single(r => r.Id == "chat:big");
            Check(record.OriginalCharacters == original.Length && record.OriginalHash == TaskContextBuilder.Fingerprint(original) && record.Text.Contains("Agent excerpt"), "Original provenance missing");
            var common = f.Store.BuildCommon(claim, default); Check(common.PartialIds!.Contains(record.Id), "Excerpt marked as fully delivered");
            Reject(() => f.Store.SynchronizeContext(claim, [new("big", "Codex", original + "changed")], "Review"));
            f.Store.EndRun(claim, "done"); f.Memory.End(claim, WorkState.Ready, "done");
            Check(new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.TaskId).ContextRecords.Any(r => r.Id == record.Id), "Excerpt cannot restart");
            return Task.CompletedTask;
        });
        await test("long lead reply stays bounded and omitted structured history is not marked delivered", async () =>
        {
            using var f = new Fixture(); var history = new List<ConversationEntry> { new("u", "You", "Discuss"), new("omitted", "Claude", new string('z', 90000)) };
            var cursors = new List<ConversationCursor>();
            await using var hub = f.Hub((_, host, turn, prompt, _) =>
            {
                Check(TaskContextBuilder.Bytes(prompt) <= TaskContextBuilder.PromptByteLimit, "Unbounded dispatch");
                if (turn == 2) Check(prompt.Contains("Agent excerpt"), "Peer did not receive bounded lead reply");
                Tool(host, "submit_message", Message()); return Task.FromResult(turn == 1 ? new string('x', 150000) : "Useful addition");
            });
            hub.ReadConversation = _ => Task.FromResult<IReadOnlyList<ConversationEntry>>(history.ToArray());
            hub.ContextSynchronized += (_, cursor) => cursors.Add(cursor);
            await hub.SubmitAsync("Discuss", "Both"); await f.Finished();
            Check(f.Calls == 2 && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, f.Memory.Get(f.TaskId)!.Reason);
            Check(cursors.Count == 2 && cursors.All(c => !c.MessageIds.Contains("omitted")), "Omitted history falsely marked delivered");
        });
        await test("Unicode instructions remain readable and the objective is not duplicated", () =>
        {
            var instruction = new string('\u4E2D', 10000); var doc = new CollaborationDocument { TaskId = "unicode" };
            doc.ContextRecords.Add(new("u", "user_message", "You", instruction, DateTimeOffset.UtcNow));
            var common = TaskContextBuilder.Build(doc, instruction);
            Check(common.Bytes < 40000 && common.Text.Contains(instruction) && !common.Text.Contains("\\u4E2D"), "Unicode budget inflated");
            doc.ContextRecords.Add(new("p", "pinned_instruction", "You", "Replacement", DateTimeOffset.UtcNow, "u"));
            Check(!TaskContextBuilder.Build(doc, instruction).Text.Contains(instruction), "Superseded objective retained"); return Task.CompletedTask;
        });
        foreach (var operation in new[] { "get_evidence", "observe", "submit_message" })
        await test("snapshot capture releases shared locks and rechecks ownership: " + operation, async () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var d = f.Dispatch(claim);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            f.Store.BeforeSnapshotCapture = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier"); };
            var pending = Task.Run(() => Reject(() =>
            {
                if (operation == "observe") d.Observe(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd", "{\"type\":\"commandExecution\",\"status\":\"inProgress\",\"command\":\"test\"}"));
                else d.Call(Agent.Codex, d.Id, "s", operation, operation == "get_evidence" ? new JsonObject { ["offset"] = 0, ["limit"] = 1 } : Message(), default);
            }));
            try
            {
                Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "Capture not reached");
                await Task.Run(() => { Check(f.Memory.Get(f.TaskId) is not null, "Task lost"); f.Store.Read(f.TaskId); f.Memory.ReleaseSpeaker(claim, Agent.Codex); }).WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { release.Set(); }
            await pending; f.Store.BeforeSnapshotCapture = null;
        });
        await test("evidence capacity prunes finished unreferenced records without stopping dispatch", () =>
        {
            using var f = new Fixture(); var doc = f.Store.Read(f.TaskId);
            for (var i = 0; i < 256; i++) doc.Evidence.Add(new("e" + i, Agent.Codex, "old", "s" + i, "test", "done", 0, false, true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
            for (var i = 0; i < 1024; i++) doc.Snapshots.Add("s" + i, new("s" + i, "hash", false, "test", "unknown", DateTimeOffset.UtcNow));
            f.Local.Save(CollaborationStore.Filename(f.TaskId), doc); var store = new CollaborationStore(f.Local, f.Memory);
            var claim = f.Begin(); var d = store.OpenDispatch(claim, Agent.Codex, [Agent.Codex, Agent.Claude], null, default);
            d.Observe(new(Agent.Codex, EventKind.Tool, "commandExecution", "new", "{\"type\":\"commandExecution\",\"status\":\"inProgress\",\"command\":\"test\"}"));
            var result = store.Read(f.TaskId);
            Check(result.PrunedEvidence == 1 && result.Evidence.Count == 256 && result.Evidence.Last().SourceEventId == "new" && result.ExpiredSnapshots == 1 && result.Snapshots.Count == 1024, "Capacity cleanup failed");
            d.Abort("done"); return Task.CompletedTask;
        });
        await test("ignored nested Git workspace cannot certify an empty snapshot", async () =>
        {
            using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Root, ".gitignore"), "project/\n");
            var start = new ProcessStartInfo("git") { WorkingDirectory = f.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("init"); using var process = Process.Start(start)!; await process.WaitForExitAsync(); Check(process.ExitCode == 0, "Fixture git init failed");
            var workspace = f.Memory.Get(f.TaskId)!.Workspace; File.WriteAllText(Path.Combine(workspace, "source.txt"), "before");
            var snapshot = await ProjectSnapshot.CaptureAsync(workspace, default);
            Check(!snapshot.Reusable && snapshot.Limitation.Contains("Git listed no files"), "Ignored workspace certified");
        });
        await test("faulted previous run cannot poison future submissions", async () =>
        {
            using var f = new Fixture(); await using var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Message()); return Task.FromResult("Done"); });
            typeof(HubCoordinator).GetField("running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hub, Task.FromException(new IOException("Old failure")));
            await hub.StopAsync(); await hub.SubmitAsync("Continue", "Codex"); await f.Finished();
            Check(f.Calls == 1 && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Old fault poisoned new run");
        });
        await test("invalid task identifiers preserve orphaned ledgers and surface recovery notice", () =>
        {
            using var f = new Fixture(); var filename = CollaborationStore.Filename(f.TaskId); f.Local.Save(filename, f.Store.Read(f.TaskId));
            f.Local.Save("tasks.json", new[] { new WorkTask { Id = "invalid", RoomId = "room", Workspace = f.Root } });
            var memory = new TaskMemory(f.Local);
            Check(memory.AllTasks().Length == 0 && File.Exists(Path.Combine(f.Root, filename)) && f.Local.RecoveryNotices.Any(n => n.Contains("without a valid task")), "Orphan ledger silently lost"); return Task.CompletedTask;
        });
        foreach (var mode in new[] { "multipart-reply-fixture", "provider-interrupted-fixture" })
        await test("Codex native protocol reliability: " + mode, async () =>
        {
            await using var client = new CodexClient(new(AppContext.BaseDirectory, false, Executable: Environment.ProcessPath!));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { var reply = await client.SendAsync(mode, timeout.Token); Check(mode == "multipart-reply-fixture" && reply.Text == "First finding.\n\nFinal finding.", "Completed reply lost content or interruption succeeded"); }
            catch (IOException e) when (mode == "provider-interrupted-fixture") { Check(e.Message.Contains("interrupted"), "Wrong provider failure"); }
        });
    }
}
