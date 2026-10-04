using AIHub.Core;

// The per-strategy report (0.33.0): counts and rates from feedback and shadow decisions, with its caveats stated.
internal static class StrategyReportTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static FeedbackRecord Judged(string strategy, FeedbackKind kind, params string[] dimensions) => new() { RoomId = "r", Strategy = strategy, Kind = kind, Dimensions = dimensions };
    private static ShadowDecision Decided(string suggested, string executed, string task = "task1", long generation = 1) => new() { TaskId = task, Generation = generation, Suggested = suggested, Executed = executed, Reason = "heuristic: test", PolicyVersion = StrategyAdvisor.PolicyVersion };
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("the strategy report counts feedback per strategy, shows the threshold, lists disagreements and states its caveats", () =>
        {
            var feedback = new List<FeedbackRecord>
            {
                Judged("reaction", FeedbackKind.Useful, "correctness"), Judged("reaction", FeedbackKind.Useful), Judged("reaction", FeedbackKind.NeedsCorrection, "scope"),
                Judged("independent", FeedbackKind.Useful, "correctness"), Judged("independent", FeedbackKind.PreferredAlternative), new FeedbackRecord { RoomId = "r", Kind = FeedbackKind.Useful }
            };
            var shadow = new List<ShadowDecision> { Decided("independent", "reaction", "task1", 1), Decided("reaction", "reaction", "task1", 2), Decided("reaction", "independent", "task2", 1) };
            var report = StrategyReport.Build(feedback, shadow, id => id == "task1" ? "Ledger bug" : null);
            Check(report.Contains("| reaction rounds | 2 | 1 | 0 | 67% | 3/5 |") && report.Contains("| independent answers, then synthesis | 1 | 0 | 1 | 100% | 1/5 |"), "Per-strategy counts are wrong:\n" + report);
            Check(report.Contains("1 feedback record(s) carry no strategy"), "Unlabeled feedback was not reported");
            Check(report.Contains("| correctness | 1 / 0 | 1 / 0 |") && report.Contains("| scope | 0 / 1 | 0 / 0 |"), "Aspect table is wrong:\n" + report);
            Check(report.Contains("3 phases observed") && report.Contains("Phases that ran reaction rounds: 2; the policy agreed in 1.") && report.Contains("Phases that ran independent answers, then synthesis: 1; the policy agreed in 0."), "Shadow summary is wrong:\n" + report);
            Check(report.Contains("Ledger bug · phase 1: ran reaction rounds, policy says independent answers, then synthesis") && report.Contains("task task2 · phase 1"), "Disagreements are not listed with labels");
            Check(report.Contains("no causal claim") && report.Contains("no feedback means unknown") && report.Contains("does not control for that"), "Caveats are missing");
            var empty = StrategyReport.Build([], []);
            Check(empty.Contains("| reaction rounds | 0 | 0 | 0 | — | 0/5 |") && empty.Contains("No shadow decisions recorded yet.") && !empty.Contains("## Aspects"), "Empty report is wrong:\n" + empty);
            return Task.CompletedTask;
        });
    }
}
