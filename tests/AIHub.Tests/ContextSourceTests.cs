using AIHub.Core;
using System.Text;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

internal static class ContextSourceTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static JsonObject Args(string id, int start = 0, string query = "") => new() { ["id"] = id, ["start"] = start, ["length"] = 8000, ["query"] = query };
    static void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or CollaborationValidationException) { return; } throw new Exception("Invalid source accepted"); }
    internal static string Transcript() => "Analyze this transcript.\n" + new string('a', 145000) + "\nALPHA: ORCHID-714\n" + new string('\u4E2D', 137000) + "\nBETA: COBALT-926\nFinal user request: compare the findings.";
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("file imports preserve bytes, existing files, collisions and source files", async () =>
        {
            using var f = new Fixture(); var workspace = f.Memory.Get(f.TaskId)!.Workspace;
            var source = Path.Combine(f.Root, "meeting notes.txt"); var bytes = Encoding.UTF8.GetBytes(Transcript()); File.WriteAllBytes(source, bytes);
            File.WriteAllText(Path.Combine(workspace, "meeting notes.txt"), "Existing project file");
            var results = await Task.WhenAll(WorkspaceImports.CopyAsync(source, workspace), WorkspaceImports.CopyAsync(source, workspace));
            Check(results.Select(r => r.Name).Distinct().Count() == 2 && results.All(r => File.ReadAllBytes(r.FullPath).SequenceEqual(bytes)), "Concurrent import collision lost bytes");
            Check(File.ReadAllText(Path.Combine(workspace, "meeting notes.txt")) == "Existing project file" && File.ReadAllBytes(source).SequenceEqual(bytes), "Import changed an existing file");
            var same = await WorkspaceImports.CopyAsync(results[0].FullPath, workspace);
            Check(same.AlreadyInWorkspace && same.FullPath == results[0].FullPath && same.Sha256 == results[0].Sha256, "In-project file copied over itself");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await WorkspaceImports.CopyAsync(source, workspace, cancelled.Token); throw new Exception("Cancelled import succeeded"); } catch (OperationCanceledException) { }
            Check(Directory.GetFiles(workspace).Length == 3, "Cancelled import left a partial file");
        });
        await test("three-hour transcript remains exact, searchable and task scoped after restart", () =>
        {
            using var f = new Fixture(); var transcript = Transcript(); var claim = f.Begin();
            f.Store.SynchronizeContext(claim, [new("transcript", "You", transcript)], transcript);
            var doc = f.Store.Read(f.TaskId); var record = doc.ContextRecords.Single();
            Check(record.SourceStored && record.OriginalCharacters == transcript.Length && new FileInfo(Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId))).Length < 20000, "Transcript inflated task ledger");
            var common = f.Store.BuildCommon(claim, default); Check(common.PartialIds!.Contains(record.Id) && common.Bytes < 15000, "Transcript preview misrepresented");
            var d = f.Dispatch(claim); var reconstructed = new StringBuilder(); var start = 0;
            while (true)
            {
                var part = d.Call(Agent.Codex, d.Id, "s", "read_context_source", Args(record.Id, start), default);
                reconstructed.Append(part.Str("text")); if (part.Bool("complete")) break; start = part["next_start"]!.GetValue<int>();
            }
            Check(reconstructed.ToString() == transcript, "Source text changed or chunks omitted text");
            var hit = d.Call(Agent.Codex, d.Id, "s", "read_context_source", Args(record.Id, query: "ALPHA"), default);
            Check(hit["matches"]![0]!.Str("text").Contains("ORCHID-714"), "Full-source search missed interior text");
            Reject(() => d.Call(Agent.Codex, d.Id, "s", "read_context_source", Args("../other-task"), default));
            d.Abort("done"); f.Store.EndRun(claim, "done"); f.Memory.End(claim, WorkState.Ready, "done");
            var memory = new TaskMemory(f.Local); var store = new CollaborationStore(f.Local, memory); var next = memory.Begin(f.TaskId, false); memory.Own(next, Agent.Claude);
            var peer = store.OpenDispatch(next, Agent.Claude, [Agent.Claude], null, default);
            Check(peer.Call(Agent.Claude, peer.Id, "s", "read_context_source", Args(record.Id, query: "BETA"), default)["matches"]![0]!.Str("text").Contains("COBALT-926"), "Peer cannot retrieve persisted source");
            var file = Directory.GetFiles(f.Root, "source-*.json").Single(); f.Local.Save(Path.GetFileName(file), "tampered");
            Reject(() => peer.Call(Agent.Claude, peer.Id, "s", "read_context_source", Args(record.Id), default));
            peer.Abort("done"); return Task.CompletedTask;
        });
        await test("a failed large-message import can continue in the existing task with bounded prompts", async () =>
        {
            using var f = new Fixture(); var transcript = Transcript(); var history = new[] { new ConversationEntry("transcript", "You", transcript) };
            var cursors = new List<ConversationCursor>();
            await using var hub = f.Hub((_, host, _, prompt, _) =>
            {
                // A carried session's later phase gets a delta prompt; the source pointer and its instructions were delivered when the session began.
                Check(TaskContextBuilder.Bytes(prompt) < 30000 && (prompt.Contains("read_context_source") || prompt.Contains("NEW EVENTS SINCE YOUR LAST TURN")), "Unbounded user message reached provider");
                Check(Tool(host, "read_context_source", Args("chat:transcript", query: "ALPHA"))["matches"]![0]!.Str("text").Contains("ORCHID-714"), "Provider lost source access");
                Tool(host, "submit_message", Message()); return Task.FromResult("One useful contribution " + f.Calls);
            });
            hub.ReadConversation = _ => Task.FromResult<IReadOnlyList<ConversationEntry>>(history);
            hub.ContextSynchronized += (_, c) => cursors.Add(c);
            await hub.SubmitAsync(transcript, "Both"); await f.Finished();
            await hub.SubmitAsync("Continue with the saved transcript", "Both"); await f.Finished();
            Check(f.Calls == 4 && f.Memory.Get(f.TaskId)!.State == WorkState.Ready && cursors.All(c => !c.MessageIds.Contains("transcript")), "Source continuation failed or preview counted as fully delivered");
            Check(f.Store.Read(f.TaskId).ContextInputs.All(i => i.InputBytes < 30000), "Full transcript duplicated in manifests");
        });
        await test("inline long user records migrate losslessly and source deletion rolls back", () =>
        {
            using var f = new Fixture(); var old = new string('x', 30000); var doc = f.Store.Read(f.TaskId);
            doc.ContextRecords.Add(new("chat:old", "user_message", "You", old, DateTimeOffset.UtcNow)); f.Local.Save(CollaborationStore.Filename(f.TaskId), doc);
            var store = new CollaborationStore(f.Local, f.Memory); var claim = f.Begin(); store.SynchronizeContext(claim, [new("old", "You", old)], old);
            Check(store.Read(f.TaskId).ContextRecords.Single().SourceStored, "Inline record was not migrated");
            store.EndRun(claim, "done"); f.Memory.End(claim, WorkState.Ready, "done");
            Reject(() => store.DeleteRoom("routing-room", () => throw new IOException("Conversation delete failed")));
            Check(Directory.GetFiles(f.Root, "source-*.json").Length == 1 && store.Read(f.TaskId).ContextRecords.Count == 1, "Deletion rollback lost source");
            store.DeleteRoom("routing-room", () => { }); Check(Directory.GetFiles(f.Root, "source-*.json").Length == 0, "Deleted room left source files"); return Task.CompletedTask;
        });
    }
    public static void VerifyProfileCopy(string path)
    {
        var local = new LocalStore(path); var rooms = local.Load("rooms.json", () => new List<Room>());
        var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
        var room = rooms.Single(r => r.Messages.Any(m => m.Speaker == "You" && m.Text.Length > 128000));
        var message = room.Messages.Last(m => m.Speaker == "You" && m.Text.Length > 128000); var claim = memory.Begin(room.ActiveTaskId, false);
        var entries = room.Messages.Where(m => m.TaskId == room.ActiveTaskId).Select(m => new ConversationEntry(m.Id, m.Speaker, m.Text, m.Route)).ToArray();
        store.SynchronizeContext(claim, entries, message.Text); var common = store.BuildCommon(claim, default);
        var record = store.Read(room.ActiveTaskId).ContextRecords.Single(r => r.Id == "chat:" + message.Id);
        Check(record.SourceStored && record.OriginalHash == TaskContextBuilder.Fingerprint(message.Text) && common.Bytes <= TaskContextBuilder.CommonByteLimit, "Existing failed transcript did not recover");
        store.EndRun(claim, "Isolated recovery verification"); memory.End(claim, WorkState.Ready, "Isolated recovery verification");
        Console.WriteLine($"PASS copied profile recovers {message.Text.Length} character transcript in existing task; common context {common.Bytes} bytes; production untouched");
    }
}
