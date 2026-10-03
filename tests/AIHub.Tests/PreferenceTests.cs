using AIHub.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

// Inspectable preference memory (0.30.0): developer-confirmed, scoped, versioned, supplied below instructions and recorded per input.
internal static class PreferenceTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return; } throw new Exception("Invalid preference accepted"); }
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "preferences-" + Guid.NewGuid().ToString("N"));
    private static PreferenceRecord Pref(string text, FeedbackScope scope = FeedbackScope.General, string workspace = "", string taskId = "") =>
        new() { Text = text, Scope = scope, Workspace = workspace, TaskId = taskId };
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("preferences are versioned on edit, scoped for relevance, disabled rather than widened, and survive restart", () =>
        {
            var root = Root(); var project = Path.Combine(root, "proj"); var other = Path.Combine(root, "other");
            try
            {
                var store = new PreferenceStore(new LocalStore(root));
                var general = store.Add(Pref("Cite file and line for every claim."));
                var projectOnly = store.Add(Pref("Prefer Decimal over float in money code.", FeedbackScope.Project, project));
                var taskOnly = store.Add(Pref("Answer in one paragraph.", FeedbackScope.Task, taskId: "task-1"));
                var category = store.Add(new PreferenceRecord { Text = "Start with the failing test.", Scope = FeedbackScope.Category, Category = "bug diagnosis" });
                Check(general.Version == 1 && store.Update(general.Id, r => r.Text += " Always.").Version == 2 && store.Update(general.Id, r => r.Enabled = false).Version == 2, "Versions did not follow text edits only");
                store.SetEnabled(general.Id, true);
                var relevant = store.Relevant(project + Path.DirectorySeparatorChar, "task-1");
                Check(relevant.Select(r => r.Id).SequenceEqual([taskOnly.Id, projectOnly.Id, general.Id]), "Relevance or ordering wrong: " + string.Join(",", relevant.Select(r => r.Text)));
                Check(store.Relevant(other, "task-2").Single().Id == general.Id, "A project or task preference leaked into another project or task");
                Check(!store.Relevant(project, "task-1").Any(r => r.Id == category.Id), "A category preference was applied automatically");
                store.SetEnabled(projectOnly.Id, false, "trying without it");
                Check(!store.Relevant(project, "task-1").Any(r => r.Id == projectOnly.Id) && store.Get(projectOnly.Id)!.DisabledReason == "trying without it", "A disabled preference was still supplied");
                Reject(() => store.Add(Pref("")));
                Reject(() => store.Add(Pref("x", FeedbackScope.Project)));
                Reject(() => store.Add(Pref("x", FeedbackScope.Task)));
                Reject(() => store.Add(new PreferenceRecord { Text = "x", Scope = FeedbackScope.Category, Category = " " }));
                var reloaded = new PreferenceStore(new LocalStore(root));
                Check(reloaded.All().Length == 4 && reloaded.Get(general.Id)!.Version == 2 && !reloaded.Get(projectOnly.Id)!.Enabled, "Preferences did not survive a restart");
                Check(reloaded.Delete(taskOnly.Id) && !reloaded.Delete(taskOnly.Id) && new PreferenceStore(new LocalStore(root)).All().Length == 3, "Delete was not durable");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await test("a preference made from feedback is disabled, not deleted, when that feedback is removed; a stated one is kept", () =>
        {
            var root = Root();
            try
            {
                var store = new PreferenceStore(new LocalStore(root)); var feedbackId = Guid.NewGuid().ToString("N"); var otherFeedback = Guid.NewGuid().ToString("N");
                var derived = store.Add(new PreferenceRecord { Text = "Name the test that would catch it.", Scope = FeedbackScope.General, Origin = "from_feedback", SupportingFeedbackIds = [feedbackId] });
                var twice = store.Add(new PreferenceRecord { Text = "Cite evidence ids.", Scope = FeedbackScope.General, Origin = "from_feedback", SupportingFeedbackIds = [feedbackId, otherFeedback] });
                var stated = store.Add(new PreferenceRecord { Text = "Keep replies short.", Scope = FeedbackScope.General, Origin = "stated", SupportingFeedbackIds = [feedbackId] });
                var affected = store.FeedbackDeleted(feedbackId);
                Check(affected.Length == 3 && !store.Get(derived.Id)!.Enabled && store.Get(derived.Id)!.DisabledReason.Contains("deleted") && store.Get(twice.Id)!.Enabled && store.Get(twice.Id)!.SupportingFeedbackIds.SequenceEqual([otherFeedback]) && store.Get(stated.Id)!.Enabled && store.Get(stated.Id)!.SupportingFeedbackIds.Length == 0,
                    "Feedback deletion did not disable exactly the preferences that rested on it");
                Check(store.Relevant("", "").Select(r => r.Id).OrderBy(x => x).SequenceEqual(new[] { twice.Id, stated.Id }.OrderBy(x => x)), "Relevance after invalidation is wrong");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await test("a damaged preferences file is repaired: invalid records dropped, anchorless scopes disabled, the original backed up", () =>
        {
            var root = Root();
            try
            {
                Directory.CreateDirectory(root);
                var good = Pref("Good one."); var anchorless = Pref("Project without folder.", FeedbackScope.Project); var dup = Pref("Dup."); dup.Id = good.Id;
                var node = JsonSerializer.SerializeToNode(new[] { good, anchorless, dup })!.AsArray(); node.Add(null); node.Add(JsonNode.Parse("{\"Format\":1,\"Id\":\"" + Guid.NewGuid().ToString("N") + "\",\"Text\":\"\",\"Scope\":0}"));
                File.WriteAllText(Path.Combine(root, PreferenceStore.FileName), node.ToJsonString());
                var store = new PreferenceStore(new LocalStore(root));
                var all = store.All();
                Check(all.Length == 2 && all.Any(r => r.Id == good.Id && r.Enabled) && all.Single(r => r.Id == anchorless.Id) is { Enabled: false, DisabledReason.Length: > 0 }, "Repair outcome wrong: " + all.Length);
                Check(Directory.GetFiles(root).Any(f => f.Contains(".unreadable-")), "Damaged original not backed up");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await test("relevant preferences enter the common context below the instructions and each supplied version is recorded in the input manifest", async () =>
        {
            using var f = new Fixture(); var prompts = new Dictionary<int, string>();
            var pref = new PreferenceRecord { Text = "End with a one-line Confidence statement.", Scope = FeedbackScope.Project, Workspace = f.Memory.Get(f.TaskId)!.Workspace };
            f.Store.Preferences = task => task.Id == f.TaskId ? [pref] : [];
            await using var hub = f.Hub((_, host, turn, prompt, _) => { lock (prompts) prompts[turn] = prompt; Tool(host, "submit_message", Message()); return Task.FromResult("Done. Confidence: high."); });
            await hub.SubmitAsync("Explain the storage design", "Both"); await f.Finished();
            Check(f.Calls == 2, "Unexpected turn count " + f.Calls);
            var block = prompts[1].IndexOf("DEVELOPER PREFERENCES", StringComparison.Ordinal); var instructions = prompts[1].IndexOf("ACTIVE USER INSTRUCTIONS", StringComparison.Ordinal);
            Check(block > instructions && instructions >= 0 && prompts[1].Contains(PreferenceStore.SuppliedId(pref)) && prompts[1].Contains("lower precedence than every instruction above"), "The preference block is missing, misplaced or lacks its precedence note");
            var manifests = f.Store.Read(f.TaskId).ContextInputs;
            Check(manifests.All(m => m.IncludedIds.Contains(PreferenceStore.SuppliedId(pref))), "Input manifests do not record the supplied preference version");
            var document = new CollaborationDocument { TaskId = "t" };
            var many = Enumerable.Range(0, PreferenceStore.MaxSupplied + 3).Select(i => new PreferenceRecord { Text = "Preference " + i }).ToList();
            var common = TaskContextBuilder.Build(document, "objective", preferences: many);
            Check(common.IncludedIds.Count(id => id.StartsWith("pref:")) == PreferenceStore.MaxSupplied && common.OmittedIds.Count(id => id.StartsWith("pref:")) == 3 && common.Text.Contains("not supplied: over the preference budget"), "Preference budget was not applied");
            Check(!TaskContextBuilder.Build(document, "objective").Text.Contains("DEVELOPER PREFERENCES"), "An empty preference set produced a block");
        });
    }
}
