using AIHub.Core;
using System.Text.Json;

// Explicit developer feedback (0.29.0): local, linked by ID, editable, deletable, exportable, and never inferred.
internal static class FeedbackTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return; } throw new Exception("Invalid feedback accepted"); }
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "feedback-" + Guid.NewGuid().ToString("N"));
    private static FeedbackRecord Sample(string room = "room1") => new()
    {
        RoomId = room, MessageId = Guid.NewGuid().ToString("N"), DispatchId = Guid.NewGuid().ToString("N"), TaskId = Guid.NewGuid().ToString("N"),
        Workspace = @"C:\project", Agent = "Codex", Kind = FeedbackKind.Useful, Dimensions = ["correctness", "scope"], Explanation = "Found the real cause.",
        Scope = FeedbackScope.Project, OutcomeState = "Ready", OutcomeReason = "Every participant passed.", AppVersion = "0.29.0", StrategyVersion = new string('a', 64), ExcerptHash = new string('b', 64)
    };
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("feedback is added, edited, deleted and survives a restart with its links and outcome intact", () =>
        {
            var root = Root();
            try
            {
                var store = new FeedbackStore(new LocalStore(root));
                var added = store.Add(Sample());
                Check(added.Created == added.Updated && store.ForMessage(added.MessageId!).Length == 1 && store.ForTask(added.TaskId).Length == 1 && store.ForRoom("room1").Length == 1, "Added feedback is not retrievable by its links");
                var updated = store.Update(added.Id, r => { r.Kind = FeedbackKind.NeedsCorrection; r.Explanation = "Missed the second defect."; r.Scope = FeedbackScope.Category; r.Category = "bug diagnosis"; });
                Check(updated.Kind == FeedbackKind.NeedsCorrection && updated.Category == "bug diagnosis" && updated.Created == added.Created && updated.Updated >= added.Updated && updated.MessageId == added.MessageId, "Update lost links or identity");
                var reloaded = new FeedbackStore(new LocalStore(root));
                var back = reloaded.Get(added.Id)!;
                Check(back.Kind == FeedbackKind.NeedsCorrection && back.DispatchId == added.DispatchId && back.OutcomeState == "Ready" && back.StrategyVersion == added.StrategyVersion && back.ExcerptHash == added.ExcerptHash, "Feedback did not survive a restart intact");
                Check(reloaded.Delete(added.Id) && !reloaded.Delete(added.Id) && reloaded.All().Length == 0 && new FeedbackStore(new LocalStore(root)).All().Length == 0, "Delete was not durable or repeatable");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await test("feedback validation rejects unknown dimensions, oversized text, bad links and an empty preferred alternative", () =>
        {
            var root = Root();
            try
            {
                var store = new FeedbackStore(new LocalStore(root));
                Reject(() => store.Add(Sample().with_dimensions(["correctness", "vibes"])));
                Reject(() => store.Add(Mutate(Sample(), r => r.Explanation = new string('x', FeedbackStore.MaxExplanation + 1))));
                Reject(() => store.Add(Mutate(Sample(), r => r.RoomId = "../rooms")));
                Reject(() => store.Add(Mutate(Sample(), r => r.MessageId = "not a message id!")));
                Reject(() => store.Add(Mutate(Sample(), r => { r.Scope = FeedbackScope.Category; r.Category = " "; })));
                Reject(() => store.Add(Mutate(Sample(), r => { r.Kind = FeedbackKind.PreferredAlternative; r.Explanation = ""; r.AlternativeMessageId = null; })));
                var preferred = store.Add(Mutate(Sample(), r => { r.Kind = FeedbackKind.PreferredAlternative; r.Explanation = ""; r.AlternativeMessageId = Guid.NewGuid().ToString("N"); }));
                Check(preferred.AlternativeMessageId is not null, "A preferred alternative naming a message was rejected");
                var taskLevel = store.Add(Mutate(Sample(), r => { r.MessageId = null; r.DispatchId = null; }));
                Check(taskLevel.MessageId is null && store.ForTask(taskLevel.TaskId).Single().MessageId is null, "Task-level feedback was not stored");
                Reject(() => store.Update(Guid.NewGuid().ToString("N"), _ => { }));
                Check(store.All().Length == 2, "Rejected feedback leaked into the store");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await test("a damaged feedback file is repaired on load, the original backed up, and the record cap holds", () =>
        {
            var root = Root();
            try
            {
                var local = new LocalStore(root);
                var good = Sample(); var dup = Sample(); dup.Id = good.Id;
                var tooLong = Sample(); tooLong.Explanation = new string('y', 5000); tooLong.Dimensions = ["correctness", "bogus"]; tooLong.Scope = FeedbackScope.Category; tooLong.Category = "";
                var badKind = JsonSerializer.SerializeToNode(Sample())!; badKind["Kind"] = 42;
                var list = JsonSerializer.SerializeToNode(new[] { good, dup, tooLong })!.AsArray(); list.Add(badKind); list.Add(null);
                File.WriteAllText(Path.Combine(root, FeedbackStore.FileName), list.ToJsonString());
                var store = new FeedbackStore(local);
                var all = store.All();
                Check(all.Length == 2 && all.Any(r => r.Id == good.Id) && all.Single(r => r.Id == tooLong.Id) is { Explanation.Length: FeedbackStore.MaxExplanation, Dimensions: ["correctness"], Scope: FeedbackScope.Task },
                    "Repair did not drop duplicates and invalid records or bound the oversized ones: " + all.Length);
                Check(Directory.GetFiles(root).Any(f => f.Contains(".unreadable-")), "The damaged original was not backed up");
                for (var i = all.Length; i < FeedbackStore.MaxRecords; i++) store.Add(Sample());
                Reject(() => store.Add(Sample()));
                Check(store.All().Length == FeedbackStore.MaxRecords, "The cap did not hold");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await test("deleting a conversation removes only its feedback, and the export lists links and outcome without the judged text", () =>
        {
            var root = Root();
            try
            {
                var store = new FeedbackStore(new LocalStore(root));
                var kept = store.Add(Sample("room-keep")); store.Add(Sample("room-gone")); store.Add(Sample("room-gone"));
                Check(store.DeleteRoom("room-gone").Length == 2 && store.DeleteRoom("room-gone").Length == 0 && store.All().Single().Id == kept.Id, "Room deletion removed the wrong feedback");
                var markdown = FeedbackStore.Export(store.All());
                Check(markdown.Contains("Useful · Codex") && markdown.Contains("message " + kept.MessageId) && markdown.Contains("dispatch " + kept.DispatchId) && markdown.Contains("task " + kept.TaskId) && markdown.Contains("Outcome when recorded: Ready") && markdown.Contains("correctness, scope") && markdown.Contains("Found the real cause."),
                    "Export is missing links, outcome or dimensions");
                Check(markdown.Contains("Absence of feedback means unknown"), "Export does not state that silence is not approval");
                Check(FeedbackStore.Summary(kept).StartsWith(kept.Updated.ToLocalTime().ToString("g")) && FeedbackStore.Summary(kept).Contains("applies to this project"), "Summary line is wrong: " + FeedbackStore.Summary(kept));
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await test("a saved message keeps its dispatch id and repair drops an unsafe one", () =>
        {
            var messages = new List<SavedMessage> { new() { Speaker = "Codex", Text = "reply", DispatchId = "d1" }, new() { Speaker = "Claude", Text = "reply", DispatchId = "../bad" }, new() { Speaker = "You", Text = "hi" } };
            var changed = SavedStateRepair.Messages(messages);
            Check(changed && messages[0].DispatchId == "d1" && messages[1].DispatchId is null && messages[2].DispatchId is null, "Dispatch id repair failed");
            Check(JsonSerializer.Deserialize<SavedMessage>(JsonSerializer.Serialize(messages[0]))!.DispatchId == "d1", "Dispatch id did not round-trip");
            Check(JsonSerializer.Deserialize<SavedMessage>("{\"Speaker\":\"Codex\",\"Text\":\"old\"}")!.DispatchId is null, "A record saved before 0.29.0 did not load");
            return Task.CompletedTask;
        });
    }
    private static FeedbackRecord Mutate(FeedbackRecord r, Action<FeedbackRecord> change) { change(r); return r; }
    private static FeedbackRecord with_dimensions(this FeedbackRecord r, string[] dimensions) => Mutate(r, x => x.Dimensions = dimensions);
}
