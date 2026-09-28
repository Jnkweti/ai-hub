using AIHub.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CollaborationEvidenceTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (CollaborationValidationException) { return; } throw new Exception("Invalid review accepted"); }
    sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(CollaborationTests.Root, "artifacts", "evidence-test-" + Guid.NewGuid().ToString("N"));
        public string Workspace; public TaskMemory Memory; public CollaborationStore Store; public TaskClaim Claim; public LocalStore Local;
        public Fixture()
        {
            Workspace = Path.Combine(Root, "project"); Directory.CreateDirectory(Path.Combine(Workspace, "src")); File.WriteAllText(Path.Combine(Workspace, "src/main.cs"), "before");
            Local = new(Path.Combine(Root, "data")); Memory = new(Local); var id = Memory.Create("room", Workspace, "Fix and review"); Store = new(Local, Memory); Claim = Memory.Begin(id, true);
        }
        public CollaborationDispatch Open(Agent agent, string? incoming = null)
        { Memory.Own(Claim, agent); return Store.OpenDispatch(Claim, agent, [Agent.Codex, Agent.Claude], incoming, default); }
        public JsonNode Call(CollaborationDispatch d, string tool, JsonNode args) => d.Call(d.Agent, d.Id, "native", tool, args, default);
        public CollaborationMessage Submit(CollaborationDispatch d, JsonObject message)
        { Call(d, "submit_message", message); var result = d.Complete(); Memory.ReleaseSpeaker(Claim, d.Agent); return result; }
        public void Dispose() { if (Path.GetDirectoryName(Root) != Path.Combine(CollaborationTests.Root, "artifacts")) throw new Exception("Unsafe cleanup"); Directory.Delete(Root, true); }
    }
    static JsonObject Message(string type, Agent recipient = Agent.Claude, string? reply = null) => CollaborationRoutingTests.Message(type, recipient, reply);
    static void Finding(JsonObject message, string disposition, string[] refs)
    { message["content"]!["findings"] = new JsonArray(JsonSerializer.SerializeToNode(new CollaborationFinding("finding-1", "high", "src/main.cs", 1, "Boundary condition", disposition, refs), CollaborationContract.JsonOptions)); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("native execution evidence is bounded, durable, scoped and never invents an exit code", () =>
        {
            using var f = new Fixture(); var d = f.Open(Agent.Claude);
            d.Observe(new(Agent.Claude, EventKind.Tool, "Bash", "cmd1", "{\"command\":\"test\"}"));
            d.Observe(new(Agent.Claude, EventKind.ToolOutput, new string('x', 14000), "cmd1", "{\"isFinal\":true,\"isError\":false}"));
            var doc = f.Store.Read(f.Claim.TaskId); var e = doc.Evidence.Single();
            Check(e.ExitCode is null && e.Finished && e.Truncated && e.Output.Length == 12000 && e.Provider == Agent.Claude, "Evidence provenance or bounds failed");
            var response = f.Call(d, "get_evidence", new JsonObject { ["offset"] = 0, ["limit"] = 4 });
            Check(response["evidence"]![0]!.Bool("fresh"), "Unchanged evidence stale");
            File.AppendAllText(Path.Combine(f.Workspace, "src/main.cs"), "changed");
            response = f.Call(d, "get_evidence", new JsonObject { ["offset"] = 0, ["limit"] = 4 });
            Check(!response["evidence"]![0]!.Bool("fresh"), "Changed evidence reused");
            var invented = Message("status"); invented["content"]!["evidence_refs"] = new JsonArray("invented"); Reject(() => f.Call(d, "submit_message", invented));
            d.Abort("test"); f.Memory.End(f.Claim, WorkState.Stopped, "test");
            Check(new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.Claim.TaskId).Evidence.Single().Id == e.Id, "Evidence lost on restart");
            return Task.CompletedTask;
        });
        await test("review rejects changed files and altered scope before accepting results", () =>
        {
            using var f = new Fixture(); var request = f.Submit(f.Open(Agent.Codex), Message("review_request")); var d = f.Open(Agent.Claude, request.Envelope.MessageId);
            var result = Message("review_result", Agent.Codex, request.Envelope.MessageId);
            result["content"]!["scope"]!["focus"] = new JsonArray("different"); Reject(() => f.Call(d, "submit_message", result));
            result = Message("review_result", Agent.Codex, request.Envelope.MessageId);
            File.AppendAllText(Path.Combine(f.Workspace, "src/main.cs"), "changed"); Reject(() => f.Call(d, "submit_message", result));
            d.Abort("test"); return Task.CompletedTask;
        });
        await test("author addressed claim needs a fresh peer check and stable finding ID", () =>
        {
            using var f = new Fixture(); var request = f.Submit(f.Open(Agent.Codex), Message("review_request")); var reviewer = f.Open(Agent.Claude, request.Envelope.MessageId);
            var result = Message("review_result", Agent.Codex, request.Envelope.MessageId); Finding(result, "checked", []); Reject(() => f.Call(reviewer, "submit_message", result));
            Finding(result, "open", []); var review = f.Submit(reviewer, result);
            var author = f.Open(Agent.Codex, review.Envelope.MessageId); File.WriteAllText(Path.Combine(f.Workspace, "src/main.cs"), "fixed");
            Reject(() => f.Call(author, "submit_message", Message("status", reply: review.Envelope.MessageId)));
            f.Call(author, "mark_addressed", new JsonObject { ["finding_id"] = "finding-1", ["explanation"] = "Fixed boundary" });
            Check(f.Store.Read(f.Claim.TaskId).Findings.Single().Disposition == "addressed", "Author claim silently checked");
            request = f.Submit(author, Message("review_request", Agent.Claude, review.Envelope.MessageId)); reviewer = f.Open(Agent.Claude, request.Envelope.MessageId);
            reviewer.Observe(new(Agent.Claude, EventKind.Tool, "Read", "read1", "{\"file_path\":\"src/main.cs\"}"));
            reviewer.Observe(new(Agent.Claude, EventKind.ToolOutput, "fixed", "read1", "{\"isFinal\":true,\"isError\":false}"));
            var evidence = f.Store.Read(f.Claim.TaskId).Evidence.Single(); result = Message("review_result", Agent.Codex, request.Envelope.MessageId); Finding(result, "checked", [evidence.Id]);
            f.Submit(reviewer, result); Check(f.Store.Read(f.Claim.TaskId).Findings.Single().Disposition == "checked", "Peer check did not advance finding");
            return Task.CompletedTask;
        });
        await test("a review stays fresh while only files outside its scope change, and goes stale when a named file changes", () =>
        {
            using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Workspace, "README.md"), "docs v1");
            var author = f.Open(Agent.Codex); var request = f.Submit(author, Message("review_request")); // scope: src/main.cs
            File.WriteAllText(Path.Combine(f.Workspace, "README.md"), "docs v2"); // Unrelated to the review.
            var reviewer = f.Open(Agent.Claude, request.Envelope.MessageId);
            var result = Message("review_result", Agent.Codex, request.Envelope.MessageId); Finding(result, "open", []);
            var review = f.Submit(reviewer, result);
            Check(review.State != DeliveryState.Interrupted && f.Store.Read(f.Claim.TaskId).Findings.Single().Disposition == "open", "An unrelated file change made the review stale");
            var inspected = f.Store.Inspect(f.Claim.TaskId, default);
            Check(CollaborationStore.Fresh(inspected.Document, review.Envelope.SnapshotRef, inspected.Current, review.Content.Scope), "Scoped freshness not reported after an unrelated change");
            author = f.Open(Agent.Codex, review.Envelope.MessageId);
            f.Call(author, "mark_addressed", new JsonObject { ["finding_id"] = "finding-1", ["explanation"] = "Fixed" });
            request = f.Submit(author, Message("review_request", Agent.Claude, review.Envelope.MessageId)); reviewer = f.Open(Agent.Claude, request.Envelope.MessageId);
            File.AppendAllText(Path.Combine(f.Workspace, "src/main.cs"), "changed after the request"); // A named file.
            var stale = Message("review_result", Agent.Codex, request.Envelope.MessageId); Finding(stale, "open", []);
            Reject(() => f.Call(reviewer, "submit_message", stale));
            return Task.CompletedTask;
        });
        await test("a reviewer can dispute a finding instead of passing, and a disputed finding blocks completion", () =>
        {
            using var f = new Fixture();
            var invented = Message("review_result", Agent.Codex, "x"); Finding(invented, "disputed", []);
            var author = f.Open(Agent.Codex); var request = f.Submit(author, Message("review_request"));
            var reviewer = f.Open(Agent.Claude, request.Envelope.MessageId);
            var first = Message("review_result", Agent.Codex, request.Envelope.MessageId); Finding(first, "disputed", []);
            Reject(() => f.Call(reviewer, "submit_message", first)); // Nothing to dispute yet.
            Finding(first, "open", []); var review = f.Submit(reviewer, first);
            author = f.Open(Agent.Codex, review.Envelope.MessageId);
            f.Call(author, "mark_addressed", new JsonObject { ["finding_id"] = "finding-1", ["explanation"] = "Not a bug: the boundary is exclusive by design." });
            request = f.Submit(author, Message("review_request", Agent.Claude, review.Envelope.MessageId)); reviewer = f.Open(Agent.Claude, request.Envelope.MessageId);
            var second = Message("review_result", Agent.Codex, request.Envelope.MessageId); Finding(second, "disputed", []);
            review = f.Submit(reviewer, second);
            Check(f.Store.Read(f.Claim.TaskId).Findings.Single().Disposition == "disputed", "Dispute was not recorded");
            author = f.Open(Agent.Codex, review.Envelope.MessageId);
            var done = CollaborationRoutingTests.Message(status: "assignment_complete", replyTo: review.Envelope.MessageId);
            Reject(() => f.Call(author, "submit_message", done)); // The disagreement must be resolved first.
            Check(f.Store.Read(f.Claim.TaskId).Findings.Single().Disposition == "disputed", "Completion cleared the dispute");
            return Task.CompletedTask;
        });
        await test("a large asset keeps snapshots complete, and a stale review terminal is set aside for resubmission", () =>
        {
            // A 9 MiB file is fingerprinted by size and time; it no longer makes the snapshot incomplete. Changing a file
            // after the review request is what makes the terminal stale.
            using var f = new Fixture(); var large = Path.Combine(f.Workspace, "large.bin"); using (var file = File.Create(large)) file.SetLength(9 * 1024 * 1024);
            var d = f.Open(Agent.Codex); f.Call(d, "submit_message", Message("review_request"));
            Check(f.Store.Read(f.Claim.TaskId).Snapshots.Values.Single().Complete, "A large asset made the submission snapshot incomplete");
            File.AppendAllText(Path.Combine(f.Workspace, "src/main.cs"), "changed after the review request");
            try { d.Complete(); throw new Exception("Stale snapshot certified"); } catch (CollaborationValidationException) { }
            var entries = f.Store.Read(f.Claim.TaskId).Entries;
            Check(entries.Single().Message.State == DeliveryState.Interrupted, "Stale review was not set aside");
            // The same dispatch may now submit a replacement terminal message instead of failing the run.
            f.Call(d, "submit_message", CollaborationRoutingTests.Message(status: "blocked"));
            var blocked = d.Complete();
            Check(blocked.Content.Status == "blocked" && f.Store.Read(f.Claim.TaskId).Entries.Count == 2, "Replacement terminal was refused after the stale review");
            return Task.CompletedTask;
        });
        await test("files changed during a native check cannot supply reusable evidence", () =>
        {
            using var f = new Fixture(); var d = f.Open(Agent.Codex);
            d.Observe(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd", "{\"type\":\"commandExecution\",\"status\":\"inProgress\",\"command\":\"test\"}"));
            File.AppendAllText(Path.Combine(f.Workspace, "src/main.cs"), "changed during check");
            d.Observe(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd", "{\"type\":\"commandExecution\",\"status\":\"completed\",\"exitCode\":0}"));
            var page = f.Call(d, "get_evidence", new JsonObject { ["offset"] = 0, ["limit"] = 4 });
            Check(!page["evidence"]![0]!.Bool("fresh"), "Changed check was reusable");
            d.Abort("test"); return Task.CompletedTask;
        });
    }
}
