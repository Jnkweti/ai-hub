using AIHub.Core;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

internal static class FollowUpAuditTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("voluntary follow-up sees peer output and quiet pass ends exchange", async () =>
        {
            using var f = new Fixture(); var visible = new List<string>(); var routes = new List<string>();
            await using var hub = f.Hub((_, host, turn, prompt, _) =>
            {
                if (turn == 3) Check(prompt.Contains("FOLLOW-UP CONTRIBUTION CHECK") && prompt.Contains("Offer an export command."), "Follow-up missing peer or assignment context");
                Tool(host, "submit_message", Message(status: turn == 4 ? "no_further_contribution" : "assignment_complete"));
                return Task.FromResult(turn switch { 1 => "Store observations locally.", 2 => "Offer an export command.", 3 => "Include timestamps so exports can be compared.", _ => "Nothing else to add." });
            });
            hub.AllowFollowUpContributions = true;
            hub.Event += e => { if (e.Kind == EventKind.Message) visible.Add(e.Text); };
            hub.Dispatch += (from, _, _) => routes.Add(from);
            await hub.SubmitAsync("Discuss the diagnostic design", "Both"); await f.Finished();
            Check(f.Calls == 4 && visible.Count == 3 && routes.SequenceEqual(["You", "You", "Claude", "Codex"]), "Follow-up route, silence or completion incorrect");
        });
        foreach (var mode in new[] { "off", "repeat", "limit", "waiting", "single" })
        await test("voluntary follow-up boundary: " + mode, async () =>
        {
            using var f = new Fixture(); var diagnostics = new List<AuditCode>();
            await using var hub = f.Hub((_, host, turn, _, _) =>
            {
                Tool(host, "submit_message", Message());
                return Task.FromResult(mode == "waiting" ? "Waiting for your input." : mode == "repeat" && turn == 3 ? "New evidence 1" : "New evidence " + turn);
            });
            hub.AllowFollowUpContributions = true; hub.AutoExchange = mode != "off"; hub.MaxAutoRounds = 1;
            hub.Diagnostic += (code, _) => diagnostics.Add(code);
            await hub.SubmitAsync("Discuss this design", mode == "single" ? "Codex" : "Both"); await f.Finished();
            Check(f.Calls == (mode switch { "off" => 2, "repeat" => 3, "limit" => 4, _ => 1 }), "Incorrect bounded dispatch count");
            if (mode == "repeat") Check(diagnostics.Contains(AuditCode.RepeatedContribution), "Repeat not recorded");
            if (mode == "limit") Check(diagnostics.Contains(AuditCode.RoundLimit) && f.Memory.Get(f.TaskId)!.State == WorkState.Paused, "Limit not paused");
        });
        await test("audit metadata bounded, deduplicated, persistent and excludes exception text", async () =>
        {
            using var f = new Fixture();
            await using (var audit = new RuntimeAudit(f.Root, "test"))
            {
                for (var i = 0; i < 250; i++) audit.Progress("", "", Agent.Codex, EventKind.Tool);
                audit.Record(AuditCode.StorageError, exception: new IOException("SECRET user prompt"));
                audit.Record(AuditCode.StorageError, exception: new IOException("SECRET user prompt"));
                Check(audit.Snapshot().Recent.Count == 200 && audit.Snapshot().Evicted == 50 && audit.Snapshot().Findings.Single().Count == 2, "Bounds or deduplication failed");
                Check(!audit.Report().Contains("SECRET"), "Exception message leaked");
                audit.Enabled = false; audit.Record(AuditCode.ProviderError);
                Check(audit.Snapshot().Findings.Count == 1, "Disabled collector recorded an event");
            }
            await using var reopened = new RuntimeAudit(f.Root, "test");
            Check(reopened.Snapshot().Findings.Single().Count == 2 && reopened.Snapshot().Recent.Count == 200, "Diagnostics did not persist");
        });
        await test("audit stalls exclude approval waits and deduplicate quiet interval", async () =>
        {
            using var f = new Fixture(); await using var audit = new RuntimeAudit(f.Root, "test");
            var room = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
            audit.Running(room, f.TaskId, true, now); audit.Waiting(room, true);
            audit.CheckStalls(now.AddMinutes(6)); Check(audit.Snapshot().Findings.Count == 0, "Approval wait flagged");
            audit.Waiting(room, false); audit.CheckStalls(now.AddMinutes(6)); audit.CheckStalls(now.AddMinutes(7));
            Check(audit.Snapshot().Findings.Single().Count == 1, "Stall missing or repeated");
            audit.Running(room, f.TaskId, false); audit.CheckStalls(now.AddHours(1));
            Check(audit.Snapshot().Findings.Single().Count == 1, "Idle room flagged");
            var review = new Room { IsAuditReview = true };
            Check(!review.EffectiveAllowEdits(new HubSettings { AllowEdits = true }), "Review inherited edit permission");
        });
        await test("diagnostic storage failure does not fail chat state", async () =>
        {
            using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Root, "diagnostics"), "occupied");
            await using var audit = new RuntimeAudit(f.Root, "test");
            audit.Record(AuditCode.ProviderError); await audit.FlushAsync();
            Check(audit.StorageStatus.Length > 0 && audit.Snapshot().Findings.Count == 1 && f.Memory.Get(f.TaskId) is not null, "Audit storage failure escaped or lost findings");
        });
        await test("audit lifecycle: toggling collection during an approval wait and disposing rooms never fabricates stalls", async () =>
        {
            using var f = new Fixture(); await using var audit = new RuntimeAudit(f.Root, "test");
            var room = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
            audit.Running(room, f.TaskId, true, now); audit.Waiting(room, true);
            audit.Enabled = false; audit.Enabled = true; // Toggled while the approval card is still open.
            audit.Running(room, f.TaskId, true, now); // The desktop timer re-registers the running room.
            audit.CheckStalls(now.AddMinutes(6)); Check(audit.Snapshot().Findings.Count == 0, "Approval wait became a stall after toggling collection");
            audit.Waiting(room, false); audit.CheckStalls(now.AddMinutes(12));
            Check(audit.Snapshot().Findings.Single().Code == AuditCode.SuspectedStall, "Quiet interval after the approval was not recorded");
            var disposed = Guid.NewGuid().ToString("N");
            audit.Running(disposed, f.TaskId, true, now); audit.Waiting(disposed, true); audit.Forget(disposed); audit.Waiting(disposed, false);
            audit.CheckStalls(now.AddHours(1)); Check(audit.Snapshot().Findings.Count == 1, "Disposed room produced a stall finding");
            audit.Enabled = false; audit.Running(room, f.TaskId, false); audit.Enabled = true;
            audit.CheckStalls(now.AddHours(2)); Check(audit.Snapshot().Findings.Single().Count == 1, "Room cleared while collection was off was still tracked");
        });
        await test("audit keeps findings per application version and flushes unhandled errors immediately", async () =>
        {
            using var f = new Fixture(); var path = Path.Combine(f.Root, "diagnostics", "audit.json");
            await using (var first = new RuntimeAudit(f.Root, "1.0.0"))
            {
                Check(first.RecordUnhandled(new InvalidOperationException("SECRET stack detail")), "Unhandled error was not flushed within the timeout");
                Check(File.Exists(path) && !File.ReadAllText(path).Contains("SECRET"), "Unhandled error was not persisted immediately or leaked its message");
            }
            await using var upgraded = new RuntimeAudit(f.Root, "2.0.0");
            upgraded.Record(AuditCode.UnhandledError, exception: new InvalidOperationException("later"));
            var findings = upgraded.Snapshot().Findings;
            Check(findings.Count == 2 && findings.Select(x => x.Version).OrderBy(v => v).SequenceEqual(["1.0.0", "2.0.0"]) && findings.All(x => x.Count == 1), "Findings from different versions were merged");
            Check(upgraded.Report().Contains("version 1.0.0") && upgraded.Report().Contains("version 2.0.0"), "Report omits the recording version");
        });
        await test("stop during a voluntary follow-up interrupts it without a visible message", async () =>
        {
            using var f = new Fixture(); var visible = new List<string>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var hub = f.Hub(async (_, host, turn, _, token) =>
            {
                if (turn == 3) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                Tool(host, "submit_message", Message()); return "Contribution " + turn;
            });
            hub.AllowFollowUpContributions = true;
            hub.Event += e => { if (e.Kind == EventKind.Message) visible.Add(e.Text); };
            await hub.SubmitAsync("Discuss the diagnostic design", "Both");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20)); await hub.StopAsync(); await f.Finished();
            var assignments = f.Store.Read(f.TaskId).Assignments;
            Check(visible.Count == 2 && f.Memory.Get(f.TaskId)!.State != WorkState.Running && f.Memory.Get(f.TaskId)!.Owner.Length == 0, "Stopped follow-up left the task running or owned");
            Check(assignments.Count(a => a.State == "completed") == 2 && assignments.Count(a => a.State == "interrupted") == 1 && assignments[^1].State == "interrupted" && assignments[^1].Role == "contribution check", "Interrupted follow-up was not recorded as interrupted");
        });
        await test("explicit peer request followed by a voluntary follow-up stays bounded", async () =>
        {
            using var f = new Fixture(); var visible = new List<string>();
            await using var hub = f.Hub((_, host, turn, prompt, _) =>
            {
                if (turn == 3) Check(!prompt.Contains("FOLLOW-UP CONTRIBUTION CHECK") && prompt.Contains("CURRENT STRUCTURED PEER MESSAGE"), "Requested peer work was framed as optional");
                if (turn == 4) Check(prompt.Contains("FOLLOW-UP CONTRIBUTION CHECK"), "Post-request follow-up lost its optional framing");
                var incoming = Tool(host, "get_task_context", new JsonObject())["incoming_message"]?["envelope"]?.Str("message_id");
                Check((incoming is not null) == (turn == 3), "Peer request context appeared on the wrong turn");
                Tool(host, "submit_message", turn == 2 ? Message("handoff", Agent.Codex) : Message(replyTo: incoming, status: turn == 4 ? "no_further_contribution" : "assignment_complete"));
                return Task.FromResult(turn switch { 1 => "Store observations locally.", 2 => "Codex, please confirm the export path.", 3 => "The export path writes Markdown; confirmed.", _ => "Nothing else to add." });
            });
            hub.AllowFollowUpContributions = true;
            hub.Event += e => { if (e.Kind == EventKind.Message) visible.Add(e.Text); };
            await hub.SubmitAsync("Discuss the diagnostic design", "Both"); await f.Finished();
            Check(f.Calls == 4 && visible.Count == 3 && f.Memory.Get(f.TaskId)!.State == WorkState.Ready,
                $"Handoff plus follow-up produced the wrong dispatch count or state: calls {f.Calls}, visible {visible.Count}, state {f.Memory.Get(f.TaskId)!.State}, reason '{f.Memory.Get(f.TaskId)!.Reason}', error '{f.LastError}'");
        });
    }
}
