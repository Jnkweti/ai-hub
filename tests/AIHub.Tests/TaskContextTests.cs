using AIHub.Core;
using System.Text;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

internal static class TaskContextTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action)
    { try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or CollaborationValidationException) { return; } throw new Exception("Invalid context accepted"); }
    static void End(Fixture f, TaskClaim claim) { f.Store.EndRun(claim, "Done"); f.Memory.End(claim, WorkState.Ready, "Done"); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("multi-message provider replies retain content and deduplicate exact finals", async () =>
        {
            await using var client = new ClaudeClient(new(AppContext.BaseDirectory, false, Executable: Environment.ProcessPath!));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var reply = await client.SendAsync("multipart-reply-fixture", timeout.Token);
            Check(reply.Text == "Substantive first finding.\n\nAdditional constraint.\n\nFinal acknowledgement.", "Provider reply lost earlier content or repeated final event: " + reply.Text);
            var buffer = new CompletedReplyBuffer(); buffer.Add("a", "old"); buffer.Add("a", "updated");
            Check(buffer.Complete("updated") == "updated", "Same native ID duplicated content");
            Reject(() => buffer.Add("b", new string('x', BoundedText.MaxFrameCharacters)));
        });
        await test("canonical instructions retain originals, supersession, disagreements and restart", () =>
        {
            using var f = new Fixture(); var claim = f.Begin();
            ConversationEntry[] entries = [new("u", "You", "Use blue"), new("a", "Codex", "Cache is safe"), new("b", "Claude", "Cache is unsafe")];
            f.Store.SynchronizeContext(claim, entries, "Use blue"); var revision = f.Store.Read(f.TaskId).ContextRevision;
            f.Store.SynchronizeContext(claim, entries, "Use blue");
            Check(f.Store.Read(f.TaskId).ContextRevision == revision, "Idempotent synchronization advanced state");
            Reject(() => f.Store.SynchronizeContext(claim, [new("u", "You", "Different original")], "Use blue"));
            Reject(() => f.Store.PinInstruction(f.TaskId, "Unsafe live edit")); End(f, claim);
            var pin = f.Store.PinInstruction(f.TaskId, "Use green", "chat:u");
            Reject(() => f.Store.PinInstruction(f.TaskId, "Use red", "chat:u"));
            var restarted = new CollaborationStore(f.Local, new TaskMemory(f.Local)); var doc = restarted.Read(f.TaskId);
            var common = TaskContextBuilder.Build(doc, "Design");
            Check(common.Text.Contains("Use green") && !common.Text.Contains("Use blue") && common.Text.Contains("Cache is safe") && common.Text.Contains("Cache is unsafe"), "Authority or disagreement lost");
            Check(doc.ContextRecords.Count == 4 && common.OmittedIds.Contains("chat:u") && TaskContextBuilder.ActiveInstructions(doc).Single().Id == pin, "History or active state lost");
            return Task.CompletedTask;
        });
        await test("imported instructions preserve message time and interleave notes chronologically", () =>
        {
            using var f = new Fixture(); var first = DateTimeOffset.UtcNow.AddDays(-1);
            f.Memory.AddNote(f.TaskId, "Intermediate green note"); var last = DateTimeOffset.UtcNow.AddSeconds(1); var claim = f.Begin();
            f.Store.SynchronizeContext(claim, [new("first", "You", "Original blue", CreatedAt: first), new("last", "You", "Latest violet correction", CreatedAt: last)], "Latest violet correction");
            var active = TaskContextBuilder.ActiveInstructions(f.Store.Read(f.TaskId));
            Check(active.Select(r => r.Text).SequenceEqual(["Original blue", "Intermediate green note", "Latest violet correction"]) && active[0].Created == first && active[2].Created == last, "Import timestamps reversed instruction precedence");
            End(f, claim); return Task.CompletedTask;
        });
        await test("bounded context preserves instructions and reports optional omissions deterministically", () =>
        {
            var doc = new CollaborationDocument { TaskId = "bounded" };
            doc.ContextRecords.Add(new("u", "user_message", "You", "Keep this constraint", DateTimeOffset.UnixEpoch));
            for (var i = 0; i < 30; i++) doc.ContextRecords.Add(new("a" + i, "agent_message", "Codex", new string('z', 3000), DateTimeOffset.UnixEpoch));
            var a = TaskContextBuilder.Build(doc, "Work"); var b = TaskContextBuilder.Build(doc, "Work");
            Check(a.Hash == b.Hash && a.Bytes <= TaskContextBuilder.CommonByteLimit && a.OmittedIds.Length > 0 && a.IncludedIds.Contains("u"), "Budget or deterministic selection failed");
            doc.ContextRecords.Add(new("huge", "user_message", "You", new string('x', 48000), DateTimeOffset.UnixEpoch));
            Reject(() => TaskContextBuilder.Build(doc, "Work")); return Task.CompletedTask;
        });
        await test("retrieval returns full originals with authority labels and cannot forge user pins", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var original = new string('x', 25000) + "END-OF-ORIGINAL";
            f.Store.SynchronizeContext(claim, [new("long", "Codex", original)], "Inspect");
            var d = f.Dispatch(claim);
            var page = d.Call(Agent.Codex, d.Id, "s", "get_context_records", new JsonObject { ["query"] = "chat:long", ["offset"] = 0, ["limit"] = 1 }, default);
            Check(page["records"]![0]!.Bool("truncated") && !page["records"]![0]!.Bool("active_user_instruction"), "Missing retrieval labels");
            var text = new StringBuilder(); long offset = 0;
            while (true)
            {
                var part = d.Call(Agent.Codex, d.Id, "s", "read_context_record", new JsonObject { ["id"] = "chat:long", ["start"] = offset, ["length"] = 8000 }, default);
                text.Append(part.Str("record_json_fragment")); if (part.Bool("complete")) break; offset = part["next_start"]!.GetValue<int>();
            }
            Check(JsonNode.Parse(text.ToString())!.Str("text") == original, "Original could not be reconstructed");
            Reject(() => d.Call(Agent.Codex, d.Id, "s", "pin_instruction", new JsonObject(), default));
            Reject(() => d.Call(Agent.Codex, d.Id, "s", "read_context_record", new JsonObject { ["id"] = "another-task", ["start"] = 0, ["length"] = 80 }, default));
            d.Abort("Done"); End(f, claim); return Task.CompletedTask;
        });
        await test("exact manifest is durable before dispatch and recovery interrupts uncertain work", () =>
        {
            using var f = new Fixture(); var claim = f.Begin();
            f.Store.SynchronizeContext(claim, [], "Keep unicode: café");
            f.Store.Assign(claim, new("work", Agent.Codex, "research", "Inspect", [], new([], []), "Publish facts", claim.Generation, "running", DateTimeOffset.UtcNow));
            var common = f.Store.BuildCommon(claim, default); var prompt = common.Text + "\nAssignment";
            var input = f.Store.PrepareInput(claim, Agent.Codex, "work", common, prompt);
            Check(input.InputBytes == Encoding.UTF8.GetByteCount(prompt) && input.PromptHash == TaskContextBuilder.Fingerprint(prompt) && f.Store.Read(f.TaskId).ContextRevision == common.Revision, "Manifest changed semantic revision or wrong byte/hash");
            var restart = new CollaborationStore(f.Local, new TaskMemory(f.Local)); var doc = restart.Read(f.TaskId);
            Check(doc.ContextInputs.Single().Prompt == prompt && doc.ContextInputs.Single().Outcome == "interrupted" && doc.Assignments.Single().State == "interrupted", "Uncertain dispatch was lost or resumed");
            return Task.CompletedTask;
        });
        foreach (var corruption in new[] { "version", "hash", "supersession" })
        await test("malformed canonical state is preserved: " + corruption, () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); f.Store.SynchronizeContext(claim, [], "Work");
            var common = f.Store.BuildCommon(claim, default); f.Store.PrepareInput(claim, Agent.Codex, "d", common, common.Text); End(f, claim);
            var path = Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId)); var raw = JsonNode.Parse(File.ReadAllText(path))!;
            if (corruption == "version") raw["ContextFormat"] = 99;
            if (corruption == "hash") raw["ContextInputs"]![0]!["Prompt"] = "tampered";
            if (corruption == "supersession") { var r = raw["ContextRecords"]![0]!; r["Kind"] = "pinned_instruction"; r["Supersedes"] = r.Str("Id"); }
            File.WriteAllText(path, raw.ToJsonString()); var before = File.ReadAllText(path);
            Reject(() => new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.TaskId));
            Check(File.ReadAllText(path) == before, "Invalid originals were overwritten"); return Task.CompletedTask;
        });
        await test("fresh phases reconstruct context and progress polls do not cancel or dispatch", async () =>
        {
            using var f = new Fixture(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var flags = new List<bool>();
            await using var hub = f.Hub(async (_, host, turn, prompt, token) =>
            {
                flags.Add(host.StartFreshSession);
                Check(f.Store.Read(f.TaskId).ContextInputs.Last().Prompt == prompt, "Provider called before manifest saved");
                if (turn == 1) { ready.SetResult(); await release.Task.WaitAsync(token); }
                Check(prompt.Contains("Keep blue"), "Fresh provider missed original user constraint");
                Tool(host, "submit_message", Message()); return "Distinct answer " + turn;
            });
            await hub.SubmitAsync("Keep blue", "Both"); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var generation = f.Memory.Get(f.TaskId)!.Generation;
            Check(hub.TryGetProgress("How is it going?", out var report) && report.Length > 0 && f.Calls == 1 && f.Memory.Get(f.TaskId)!.Generation == generation && f.Memory.Get(f.TaskId)!.State == WorkState.Running, "Progress changed active execution");
            release.SetResult(); await f.Finished(); await hub.SubmitAsync("Discuss next step", "Both"); await f.Finished();
            Check(flags.Count == 4 && flags.All(v => v), "New phases inherited private native sessions");
            Check(f.Store.Read(f.TaskId).ContextInputs.All(i => i.Outcome == "responded") && f.Store.Read(f.TaskId).Assignments.All(a => a.State == "completed"), "Completion lifecycle missing");
        });
        await test("inline human answers become authoritative context for the next structured participant", async () =>
        {
            using var f = new Fixture(); var history = new List<ConversationEntry> { new("u", "You", "Choose a layout") };
            await using var hub = f.Hub((_, host, turn, prompt, _) =>
            {
                if (turn == 1) history.Add(new("answer", "You", "Human selected violet", "Codex"));
                else Check(prompt.Contains("Human selected violet"), "Peer did not receive the inline answer");
                Tool(host, "submit_message", Message()); return Task.FromResult("Contribution " + turn);
            });
            hub.ReadConversation = _ => Task.FromResult<IReadOnlyList<ConversationEntry>>(history.ToArray());
            await hub.SubmitAsync("Choose a layout", "Both"); await f.Finished();
            Check(TaskContextBuilder.ActiveInstructions(f.Store.Read(f.TaskId)).Any(r => r.Id == "chat:answer"), "Inline answer lost user authority");
        });
        await test("within-phase review returns keep the resident native session", async () =>
        {
            using var f = new Fixture(); var flags = new List<bool>(); var prompts = new List<string>();
            await using var hub = f.Hub((agent, host, turn, prompt, _) =>
            {
                flags.Add(host.StartFreshSession); prompts.Add(prompt); var context = Tool(host, "get_task_context", new JsonObject());
                var incoming = context["incoming_message"]?["envelope"]?.Str("message_id");
                Tool(host, "submit_message", turn == 1 ? Message("review_request") : turn == 2 ? Message("review_result", Agent.Codex, incoming) : Message(replyTo: incoming));
                return Task.FromResult("Contribution " + turn);
            });
            var created = 0; var inner = hub.CollaborationFactory!;
            hub.CollaborationFactory = (agent, host) => { created++; return inner(agent, host); };
            await hub.SubmitAsync("Implement and review", "Both"); await f.Finished();
            // One resident client and one fresh host per agent for the phase; the review return continues the live session with a delta prompt.
            Check(flags.SequenceEqual([true, true, true]) && created == 2 && prompts[2].Contains("NEW EVENTS SINCE YOUR LAST TURN") && prompts[2].Contains("CURRENT STRUCTURED PEER MESSAGE"),
                "Host restarted the native session during the dependent review return");
        });
        await test("provider failures remain failed assignments and failed input records", async () =>
        {
            using var f = new Fixture(); await using var hub = f.Hub((_, _, _, _, _) => throw new IOException("Fixture failure"));
            await hub.SubmitAsync("Work", "Codex"); await f.Finished(); var doc = f.Store.Read(f.TaskId);
            Check(doc.Assignments.Single().State == "failed" && doc.ContextInputs.Single().Outcome == "failed", "Failed work mislabeled");
        });
        await test("legacy cursor repair retains only valid content hashes", () =>
        {
            var message = new ConversationEntry("safe", "You", "Original");
            var room = new Room { CodexContext = new() { MessageIds = ["safe", "bad"], MessageHashes = new() { ["safe"] = ConversationTurns.ContentHash(message), ["bad"] = "invalid", ["missing"] = new string('A', 64) } } };
            SavedStateRepair.Rooms([room]); Check(room.CodexContext.MessageHashes.Count == 1 && room.CodexContext.MessageHashes.ContainsKey("safe"), "Cursor repair discarded valid delivery evidence");
            return Task.CompletedTask;
        });
    }
}
