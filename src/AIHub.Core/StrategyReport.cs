using System.Text;

namespace AIHub.Core;

/// <summary>
/// The per-strategy comparison the learning plan asks for (0.33.0), computed from explicit feedback and shadow decisions.
/// It reports counts and rates; it does not assign credit or claim one strategy causes better outcomes.
/// </summary>
public static class StrategyReport
{
    private static readonly string[] Strategies = ["reaction", "independent"];
    public static string Build(IReadOnlyList<FeedbackRecord> feedback, IReadOnlyList<ShadowDecision> shadow, Func<string, string?>? taskLabel = null)
    {
        var text = new StringBuilder("# Strategy report\n\n");
        text.AppendLine($"Generated {DateTimeOffset.Now:g}. Feedback is explicit developer judgement, counted under the strategy setting in force when it was recorded; shadow decisions are what policy {StrategyAdvisor.PolicyVersion} would have chosen beside what ran. Counts and rates only: no causal claim, and no feedback means unknown, not approval.\n");
        text.AppendLine("## Feedback by strategy\n");
        text.AppendLine("| Strategy | Useful | Needs correction | Preferred alternative | Useful rate | Toward the policy threshold |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var strategy in Strategies)
        {
            var mine = feedback.Where(f => f.Strategy == strategy).ToArray();
            var useful = mine.Count(f => f.Kind == FeedbackKind.Useful); var correction = mine.Count(f => f.Kind == FeedbackKind.NeedsCorrection); var alternative = mine.Count(f => f.Kind == FeedbackKind.PreferredAlternative);
            var judged = useful + correction;
            text.AppendLine($"| {HubCoordinator.StrategyName(HubCoordinator.ParseStrategy(strategy))} | {useful} | {correction} | {alternative} | {(judged == 0 ? "—" : ((double)useful / judged).ToString("P0"))} | {Math.Min(judged, StrategyAdvisor.MinimumFeedbackPerStrategy)}/{StrategyAdvisor.MinimumFeedbackPerStrategy} |");
        }
        var unlabeled = feedback.Count(f => f.Strategy is not ("reaction" or "independent"));
        if (unlabeled > 0) text.AppendLine($"\n{unlabeled} feedback record(s) carry no strategy (recorded before 0.32.0) and are not counted above.");
        text.AppendLine($"\nThe shadow policy uses feedback only once both strategies have {StrategyAdvisor.MinimumFeedbackPerStrategy} judgements (useful or needs correction) and their useful rates differ by at least 15 points; until then it follows its stated heuristic.");
        var dimensions = feedback.Where(f => f.Strategy is "reaction" or "independent").SelectMany(f => f.Dimensions.Select(d => (f.Strategy, d, f.Kind))).ToArray();
        if (dimensions.Length > 0)
        {
            text.AppendLine("\n## Aspects named in feedback\n");
            text.AppendLine("| Aspect | Reaction rounds (useful / needs correction) | Independent answers (useful / needs correction) |");
            text.AppendLine("| --- | --- | --- |");
            foreach (var dimension in FeedbackStore.KnownDimensions)
            {
                var r = dimensions.Where(x => x.Strategy == "reaction" && x.d == dimension).ToArray(); var i = dimensions.Where(x => x.Strategy == "independent" && x.d == dimension).ToArray();
                if (r.Length + i.Length == 0) continue;
                text.AppendLine($"| {dimension} | {r.Count(x => x.Kind == FeedbackKind.Useful)} / {r.Count(x => x.Kind == FeedbackKind.NeedsCorrection)} | {i.Count(x => x.Kind == FeedbackKind.Useful)} / {i.Count(x => x.Kind == FeedbackKind.NeedsCorrection)} |");
            }
        }
        text.AppendLine("\n## Shadow decisions\n");
        text.AppendLine(ShadowStrategyLog.Summary(shadow));
        foreach (var strategy in Strategies)
        {
            var ran = shadow.Where(d => d.Executed == strategy).ToArray();
            if (ran.Length > 0) text.AppendLine($"- Phases that ran {HubCoordinator.StrategyName(HubCoordinator.ParseStrategy(strategy))}: {ran.Length}; the policy agreed in {ran.Count(d => d.Suggested == strategy)}.");
        }
        var disagreements = shadow.Where(d => d.Suggested != d.Executed).OrderByDescending(d => d.Time).Take(20).ToArray();
        if (disagreements.Length > 0)
        {
            text.AppendLine("\n### Where the policy would have chosen differently (newest first)\n");
            foreach (var d in disagreements)
                text.AppendLine($"- {d.Time.ToLocalTime():g} · {taskLabel?.Invoke(d.TaskId) ?? ("task " + d.TaskId[..Math.Min(8, d.TaskId.Length)])} · phase {d.Generation}: ran {HubCoordinator.StrategyName(HubCoordinator.ParseStrategy(d.Executed))}, policy says {HubCoordinator.StrategyName(HubCoordinator.ParseStrategy(d.Suggested))} — {d.Reason}");
        }
        text.AppendLine("\n## Reading this\n");
        text.AppendLine("- A judgement counts toward the strategy that was selected when it was recorded, which is normally the strategy that produced the judged message but not always; the per-phase strategy is in each task's ledger.");
        text.AppendLine("- Agreement between the shadow policy and what ran says nothing about outcomes; it only shows how often the policy would have changed the choice.");
        text.AppendLine("- Comparing strategies fairly needs the same kinds of tasks under both; this report does not control for that.");
        return text.ToString();
    }
}
