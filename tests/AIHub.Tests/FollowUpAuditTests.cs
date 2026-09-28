using AIHub.Core;
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
    }
}
