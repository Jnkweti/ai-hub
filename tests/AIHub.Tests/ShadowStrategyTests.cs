using AIHub.Core;
using static CollaborationRoutingTests;

// Shadow-mode strategy advice (0.32.0): a stated, versioned policy records what it would choose; the configured strategy runs.
internal static class ShadowStrategyTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static FeedbackRecord Judged(string strategy, FeedbackKind kind) => new() { RoomId = "r", TaskId = Guid.NewGuid().ToString("N"), Strategy = strategy, Kind = kind };
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("the shadow policy is deterministic over stated features and prefers feedback only once both strategies have enough", () =>
        {
            var none = Array.Empty<FeedbackRecord>();
            var judgement = StrategyAdvisor.Describe("Customers report a wrong balance; diagnose the root cause of each problem and propose the minimal fix to ship, with the evidence that supports each conclusion.", "t", 1, 2, none);
            Check(judgement.AsksForJudgement && judgement.FirstPhase && judgement.PromptWords >= 25 && StrategyAdvisor.Suggest(judgement).Strategy == "independent", "A substantial first-phase judgement question was not suggested for independent answers");
            var later = judgement with { Generation = 3, FirstPhase = false };
            Check(StrategyAdvisor.Suggest(later).Strategy == "reaction" && StrategyAdvisor.Suggest(later).Reason.Contains("later phase"), "A later phase was not kept on reaction rounds");
            var brief = StrategyAdvisor.Describe("Which file holds the fee policy?", "t", 1, 2, none);
            Check(StrategyAdvisor.Suggest(brief).Strategy == "reaction", "A short ask was suggested for independent answers");
            Check(StrategyAdvisor.Suggest(judgement with { Participants = 1 }).Strategy == "reaction", "A single participant produced a choice");
            var mixed = Enumerable.Range(0, 6).Select(_ => Judged("reaction", FeedbackKind.Useful)).Concat(Enumerable.Range(0, 2).Select(_ => Judged("independent", FeedbackKind.Useful))).Concat(Enumerable.Range(0, 4).Select(_ => Judged("independent", FeedbackKind.NeedsCorrection))).ToArray();
            var withFeedback = StrategyAdvisor.Describe(judgement.ToString(), "t", 1, 2, mixed) with { AsksForJudgement = true, PromptWords = 40, FirstPhase = true };
            Check(withFeedback.FeedbackUsefulReaction == 6 && withFeedback.FeedbackCorrectionIndependent == 4 && StrategyAdvisor.Suggest(withFeedback).Strategy == "reaction" && StrategyAdvisor.Suggest(withFeedback).Reason.StartsWith("feedback:"),
                "Enough feedback favoring reaction did not override the heuristic: " + StrategyAdvisor.Suggest(withFeedback).Reason);
            var tooFew = withFeedback with { FeedbackUsefulIndependent = 1, FeedbackCorrectionIndependent = 1 };
            Check(StrategyAdvisor.Suggest(tooFew).Reason.StartsWith("heuristic:"), "Feedback below the minimum was used");
            return Task.CompletedTask;
        });
        await test("each two-agent phase records the shadow decision beside the executed strategy, durably, and a single-agent phase records nothing", async () =>
        {
            using var f = new Fixture(); var log = new ShadowStrategyLog(f.Local); var feedback = new FeedbackStore(f.Local);
            await using var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Message()); return Task.FromResult("Answer."); });
            hub.StrategyShadow = (prompt, taskId, generation, executed) =>
            {
                var context = StrategyAdvisor.Describe(prompt, taskId, generation, 2, feedback.All());
                var (suggested, reason) = StrategyAdvisor.Suggest(context);
                return ShadowStrategyLog.Describe(log.Record(context, suggested, reason, HubCoordinator.StrategySetting(executed)));
            };
            await hub.SubmitAsync("Customers report a wrong balance; diagnose the root cause of each problem and propose the minimal fix to ship, with the evidence that supports each conclusion.", "Both"); await f.Finished();
            var first = log.ForTask(f.TaskId).Single();
            Check(first.Suggested == "independent" && first.Executed == "reaction" && first.Generation == 1 && first.PolicyVersion == StrategyAdvisor.PolicyVersion && first.Context!.AsksForJudgement, "The shadow decision was not recorded with its context");
            Check(f.Store.Read(f.TaskId).Events.Any(e => e.Kind == "system" && e.Text.StartsWith("Shadow strategy:") && e.Text.Contains("(differs)")), "The stream does not carry the shadow line");
            await hub.SubmitAsync("Thanks, now summarize", "Codex"); await f.Finished();
            Check(log.ForTask(f.TaskId).Length == 1, "A single-agent phase recorded a shadow decision");
            hub.Strategy = HubCoordinator.CollaborationStrategy.IndependentThenSynthesis;
            await hub.SubmitAsync("Compare the two fixes and decide which one we should ship, with reasons grounded in the code and the tests we already have.", "Both"); await f.Finished();
            var third = log.ForTask(f.TaskId).Last();
            Check(third.Executed == "independent" && third.Generation == 3 && third.Suggested == "reaction" && third.Reason.Contains("later phase"), "The executed strategy or phase was recorded wrongly: " + ShadowStrategyLog.Describe(third));
            Check(new ShadowStrategyLog(f.Local).All().Length == 2 && ShadowStrategyLog.Summary(log.All()).Contains("2 phases observed"), "Shadow decisions did not persist or summarize");
        });
        await test("a damaged shadow log is repaired and bounded", () =>
        {
            var root = Path.Combine(AppContext.BaseDirectory, "shadow-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, ShadowStrategyLog.FileName), "[{\"Format\":1,\"Id\":\"" + Guid.NewGuid().ToString("N") + "\",\"TaskId\":\"t\",\"Suggested\":\"bogus\",\"Executed\":\"reaction\"},{\"Format\":1,\"Id\":\"" + Guid.NewGuid().ToString("N") + "\",\"TaskId\":\"t\",\"Suggested\":\"reaction\",\"Executed\":\"independent\",\"Reason\":null},null]");
                var log = new ShadowStrategyLog(new LocalStore(root));
                Check(log.All().Length == 1 && log.All()[0].Reason == "" && Directory.GetFiles(root).Any(f => f.Contains(".unreadable-")), "Repair did not drop the invalid entries or back up the file");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
    }
}
