using AIHub.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

internal static class SharedContextTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception ex) when (ex is CollaborationValidationException or IOException or InvalidOperationException or OperationCanceledException) { return; } throw new Exception("Invalid research call accepted"); }
    internal static JsonObject Request()
    {
        var n = Message(); var c = n["content"]!.AsObject(); c["type"] = "context_request"; c.Remove("status");
        c["scope"] = JsonNode.Parse("""{"files":["frontend","backend"],"focus":["Trace both halves"]}""");
        c["assignments"] = JsonNode.Parse("""[{"agent":"Codex","scope":{"files":["frontend"],"focus":["Trace the UI"]}},{"agent":"Claude","scope":{"files":["backend"],"focus":["Trace storage"]}}]""");
        return n;
    }
    internal static void Files(string project)
    {
        Directory.CreateDirectory(Path.Combine(project, "frontend")); Directory.CreateDirectory(Path.Combine(project, "backend"));
        File.WriteAllText(Path.Combine(project, "frontend", "view.txt"), "The UI emits submit-note.");
        File.WriteAllText(Path.Combine(project, "backend", "store.txt"), "The store accepts save-note.");
    }
    internal static JsonNode Notes(Agent agent) => JsonSerializer.SerializeToNode(new ContextNotes(
        agent == Agent.Codex ? "UI sends submit-note." : "Storage expects save-note.",
        [agent == Agent.Codex ? "frontend/view.txt" : "backend/store.txt"], []), CollaborationContract.JsonOptions)!;
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("parallel researchers overlap and share findings before the conversation resumes", async () =>
        {
            using var f = new Fixture(); Files(f.Memory.Get(f.TaskId)!.Workspace);
            var started = 0; var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessions = new List<Agent>(); var chat = new List<string>();
            await using var hub = f.Hub((_, host, call, prompt, _) =>
            {
                if (call == 1) Tool(host, "submit_message", Request());
                else
                {
                    Check(prompt.Contains("UI sends submit-note.") && prompt.Contains("Storage expects save-note."), "Research was not automatically supplied");
                    var context = Tool(host, "get_shared_context", JsonNode.Parse("""{"offset":0,"limit":2}""")!);
                    Check(context["sections"]!.AsArray().Count == 2 && prompt.Contains("get_shared_context"), "Findings were not shared before main resume");
                    Reject(() => Tool(host, "submit_message", Request()));
                    Tool(host, "submit_message", Message());
                }
                return Task.FromResult("Useful contribution");
            });
            hub.Event += e => { if (e.Kind == EventKind.Session) sessions.Add(e.Agent); if (e.Kind == EventKind.Message) chat.Add(e.Text); };
            hub.ContextResearchFactory = (agent, host) => new StructuredFake(agent, host, async (_, token) =>
            {
                var context = Tool(host, "get_task_context", new JsonObject());
                Check(!context.Bool("allow_edits") && context["assignment"]!.Str("agent") == agent.ToString(), "Wrong research permissions/identity");
                if (Interlocked.Increment(ref started) == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(5), token); // Fails if the host runs researchers sequentially.
                var receipt = Tool(host, "publish_context", Notes(agent));
                Check(Tool(host, "publish_context", Notes(agent)).Bool("duplicate"), "Publication retry duplicated findings");
                Reject(() => Tool(host, "submit_message", Message()));
                return "Published " + receipt.Str("section_id");
            });
            await hub.SubmitAsync("Find why notes fail", "Both"); await f.Finished();
            // Requester synthesizes, then the peer gets one reaction opportunity to the synthesis: three main turns.
            Check(f.Memory.Get(f.TaskId)!.State == WorkState.Ready && started == 2 && f.Calls == 3, "Unexpected main turn count: " + f.Calls + " · " + f.Memory.Get(f.TaskId)!.Reason);
            Check(sessions.Count == 3, "Research session replaced a main session");
            Check(!chat.Any(t => t.StartsWith("Published ")), "Research prose leaked into chat instead of the shared window");
            var doc = f.Store.Read(f.TaskId);
            var workIds = doc.Assignments.Where(a => a.Role == "research").Select(a => a.Id).ToHashSet();
            var inputs = doc.ContextInputs.Where(i => workIds.Contains(i.AssignmentId!)).ToArray();
            Check(inputs.Length == 2 && inputs.Select(i => i.CommonHash).Distinct().Count() == 1, "Parallel researchers received different common snapshots");
            var section = doc.ContextSections[0];
            doc.ContextSections[0] = section with { Notes = section.Notes with { Findings = new string('x', 3000) } };
            var excerpted = TaskContextBuilder.Build(doc, "Research");
            Check(excerpted.PartialIds!.Contains("research:" + section.Id) && excerpted.Text.Contains("retrieve original"), "Research excerpt was marked fully supplied");
            var restarted = new CollaborationStore(f.Local, new TaskMemory(f.Local));
            Check(restarted.Read(f.TaskId).ContextSections.Count == 2, "Shared findings did not survive restart");
            Check((await restarted.ContextReportAsync(f.TaskId, default)).Contains("Current files"), "Shared window report lacks freshness");
        });
        await test("split context rejects overlapping missing escaping and duplicate-agent scopes and single-agent routing", () =>
        {
            using var f = new Fixture(); Files(f.Memory.Get(f.TaskId)!.Workspace); var claim = f.Begin(); var d = f.Dispatch(claim);
            foreach (var path in new[] { "frontend", "frontend/child", "../secret", "missing", "C:/secret" })
            {
                var n = Request(); n["content"]!["assignments"]![1]!["scope"]!["files"] = new JsonArray(path);
                Reject(() => f.Submit(d, Agent.Codex, n));
            }
            var dup = Request(); dup["content"]!["assignments"]![1]!["agent"] = "Codex"; Reject(() => f.Submit(d, Agent.Codex, dup));
            d.Abort("Done"); f.Memory.ReleaseSpeaker(claim, Agent.Codex); f.Memory.Own(claim, Agent.Codex);
            var only = f.Store.OpenDispatch(claim, Agent.Codex, [Agent.Codex], null, default);
            Reject(() => f.Submit(only, Agent.Codex, Request())); only.Abort("Done"); f.Memory.End(claim, WorkState.Ready, "Done");
            return Task.CompletedTask;
        });
        await test("shared context detects scoped additions deletions changes but ignores unrelated files", async () =>
        {
            using var f = new Fixture(); var project = f.Memory.Get(f.TaskId)!.Workspace; Files(project);
            var claim = f.Begin(); var d = f.Dispatch(claim); f.Submit(d, Agent.Codex, Request()); var request = d.Complete(); f.Memory.ReleaseSpeaker(claim, Agent.Codex);
            var before = await ProjectSnapshot.CaptureAsync(project, default);
            using var worker = f.Store.OpenResearch(claim, request, request.Content.Assignments![0], before, default);
            JsonNode Publish(JsonNode notes) => worker.Call(Agent.Codex, worker.Id, "s", "publish_context", notes, default);
            Reject(() => Publish(Notes(Agent.Claude))); Reject(() => worker.Call(Agent.Claude, worker.Id, "s", "get_task_context", new JsonObject(), default));
            Publish(Notes(Agent.Codex)); var section = f.Store.Read(f.TaskId).ContextSections.Single();
            var changed = Notes(Agent.Codex); changed["findings"] = "Changed"; Reject(() => Publish(changed));
            File.WriteAllText(Path.Combine(project, "backend", "other.txt"), "unrelated");
            Check(CollaborationStore.ContextFreshness(section, await ProjectSnapshot.CaptureAsync(project, default)) == "Current files", "Unrelated file invalidated context");
            var added = Path.Combine(project, "frontend", "extra.txt"); File.WriteAllText(added, "new");
            Check(CollaborationStore.ContextFreshness(section, await ProjectSnapshot.CaptureAsync(project, default)).StartsWith("Stale"), "Added scoped file not detected");
            File.Delete(added); File.Delete(Path.Combine(project, "frontend", "view.txt"));
            Check(CollaborationStore.ContextFreshness(section, await ProjectSnapshot.CaptureAsync(project, default)).StartsWith("Stale"), "Deleted source not detected");
            Files(project); File.AppendAllText(Path.Combine(project, "frontend", "view.txt"), "changed");
            Check(CollaborationStore.ContextFreshness(section, await ProjectSnapshot.CaptureAsync(project, default)).StartsWith("Stale"), "Changed source not detected");
            worker.Dispose(); Reject(() => Publish(Notes(Agent.Codex)));
            f.Memory.End(claim, WorkState.Ready, "Done");
            f.Store.DeleteRoom("routing-room", () => { }); Check(!File.Exists(Path.Combine(f.Root, CollaborationStore.Filename(f.TaskId))), "Room deletion retained task context");
        });
        await test("stop cancels both researchers and revokes late publication", async () =>
        {
            using var f = new Fixture(); Files(f.Memory.Get(f.TaskId)!.Workspace);
            var started = 0; var canceled = 0; var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hosts = new List<CollaborationMcpHost>();
            await using var hub = f.Hub((_, host, call, prompt, _) =>
            {
                if (call == 1) Tool(host, "submit_message", Request());
                else { Check(prompt.Contains("Correction: keep discussion only"), "Replacement phase lost user correction"); Tool(host, "submit_message", Message()); }
                return Task.FromResult(call == 1 ? "Research needed" : "Corrected contribution " + call);
            });
            hub.ContextResearchFactory = (agent, host) => new StructuredFake(agent, host, async (_, token) =>
            {
                lock (hosts) hosts.Add(host); if (Interlocked.Increment(ref started) == 2) both.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
                return "Never";
            });
            await hub.SubmitAsync("Research", "Both"); await both.Task.WaitAsync(TimeSpan.FromSeconds(10)); await hub.StopAsync(); await f.Finished();
            Check(canceled == 2 && f.Store.Read(f.TaskId).ContextSections.Count == 0 && f.Calls == 1, "Stop leaked research or resumed main worker");
            foreach (var host in hosts) Reject(() => Tool(host, "publish_context", Notes(Agent.Codex)));
            await hub.SubmitAsync("Correction: keep discussion only", "Both"); await f.Finished();
            Check(f.Calls == 3 && f.Memory.Get(f.TaskId)!.Generation == 2 && f.Store.Read(f.TaskId).ContextInputs.Where(i => i.Generation == 2).All(i => i.Prompt.Contains("Correction: keep discussion only")), "Correction did not reconstruct both agents after cleanup");
        });
        await test("research failure keeps partial findings and prevents automatic continuation", async () =>
        {
            using var f = new Fixture(); Files(f.Memory.Get(f.TaskId)!.Workspace);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Request()); return Task.FromResult("Research needed"); });
            hub.ContextResearchFactory = (agent, host) => new StructuredFake(agent, host, async (_, token) =>
            {
                if (agent == Agent.Codex) { Tool(host, "publish_context", Notes(agent)); published.TrySetResult(); return "Saved"; }
                await published.Task.WaitAsync(token); throw new IOException("Research provider unavailable");
            });
            await hub.SubmitAsync("Research", "Both"); await f.Finished();
            Check(f.Memory.Get(f.TaskId)!.State == WorkState.Failed && f.Calls == 1 && f.Store.Read(f.TaskId).ContextSections.Count == 1, "Failed research resumed or lost partial results");
            Check((await f.Store.ContextReportAsync(f.TaskId, default)).Contains("pending or interrupted"), "Partial research was presented as complete");
        });
    }
}
