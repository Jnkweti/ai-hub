using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Core;

/// <summary>The context a strategy choice is made from: defined before any learning, so later comparisons use the same features.</summary>
public sealed record StrategyContext(string TaskId, long Generation, int PromptWords, bool AsksForJudgement, bool MentionsCode, bool FirstPhase, int Participants,
    int FeedbackUsefulReaction, int FeedbackCorrectionReaction, int FeedbackUsefulIndependent, int FeedbackCorrectionIndependent);

/// <summary>One shadow decision: what the advisor would have chosen, why, and what actually ran. Joined to feedback by task and phase.</summary>
public sealed class ShadowDecision
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public string TaskId { get; set; } = "";
    public long Generation { get; set; }
    public string PolicyVersion { get; set; } = "";
    public string Suggested { get; set; } = "";
    public string Executed { get; set; } = "";
    public string Reason { get; set; } = "";
    public StrategyContext? Context { get; set; }
}

/// <summary>
/// Shadow-mode strategy advice (0.32.0). Policy v1 is a stated heuristic, not a trained model: it is deterministic, versioned,
/// and only recorded; the configured strategy always executes. Its suggestions become comparable once feedback accumulates.
/// </summary>
public static class StrategyAdvisor
{
    public const string PolicyVersion = "shadow-v1";
    public const int MinimumFeedbackPerStrategy = 5;
    private static readonly Regex Judgement = new(@"\b(diagnos|root cause|compare|which is (?:right|better)|decide|evaluate|assess|review|audit|trade-?off|why does|why is|what causes)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Code = new(@"\b(bug|test|function|class|file|module|\.py|\.cs|\.ts|\.js|stack ?trace|exception|compile|build|commit)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public static StrategyContext Describe(string prompt, string taskId, long generation, int participants, IReadOnlyList<FeedbackRecord> feedback) => new(
        taskId, generation, prompt.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length, Judgement.IsMatch(prompt), Code.IsMatch(prompt), generation <= 1, participants,
        feedback.Count(f => f.Strategy == "reaction" && f.Kind == FeedbackKind.Useful), feedback.Count(f => f.Strategy == "reaction" && f.Kind == FeedbackKind.NeedsCorrection),
        feedback.Count(f => f.Strategy == "independent" && f.Kind == FeedbackKind.Useful), feedback.Count(f => f.Strategy == "independent" && f.Kind == FeedbackKind.NeedsCorrection));
    /// <summary>Returns the suggested strategy setting ("reaction" or "independent") and the reason, from the context alone.</summary>
    public static (string Strategy, string Reason) Suggest(StrategyContext c)
    {
        if (c.Participants < 2) return ("reaction", "one participant: no choice to make");
        // With enough explicit feedback on both strategies, prefer the one judged useful more often; ties keep the default.
        var reactionTotal = c.FeedbackUsefulReaction + c.FeedbackCorrectionReaction; var independentTotal = c.FeedbackUsefulIndependent + c.FeedbackCorrectionIndependent;
        if (reactionTotal >= MinimumFeedbackPerStrategy && independentTotal >= MinimumFeedbackPerStrategy)
        {
            var reactionRate = (double)c.FeedbackUsefulReaction / reactionTotal; var independentRate = (double)c.FeedbackUsefulIndependent / independentTotal;
            if (Math.Abs(reactionRate - independentRate) >= 0.15)
                return independentRate > reactionRate
                    ? ("independent", $"feedback: independent judged useful {independentRate:P0} of {independentTotal} vs reaction {reactionRate:P0} of {reactionTotal}")
                    : ("reaction", $"feedback: reaction judged useful {reactionRate:P0} of {reactionTotal} vs independent {independentRate:P0} of {independentTotal}");
        }
        // Heuristic: a first-phase judgement question of some substance benefits from two independent views; follow-ups and short asks do not.
        if (c.FirstPhase && c.AsksForJudgement && c.PromptWords >= 25) return ("independent", "heuristic: first phase, asks for a judgement, substantial prompt");
        return ("reaction", c.FirstPhase ? "heuristic: no judgement signal or short prompt" : "heuristic: a later phase builds on shared context");
    }
}

/// <summary>Durable, bounded shadow log; the developer can disable recording without affecting collaboration.</summary>
public sealed class ShadowStrategyLog
{
    public const string FileName = "strategy-shadow.json";
    public const int MaxDecisions = 2048;
    private readonly LocalStore store;
    private readonly object gate = new();
    private readonly List<ShadowDecision> decisions;
    public ShadowStrategyLog(LocalStore store)
    {
        this.store = store;
        decisions = store.Load(FileName, () => new List<ShadowDecision>(), Repair);
    }
    public ShadowDecision Record(StrategyContext context, string suggested, string reason, string executed)
    {
        var decision = new ShadowDecision { TaskId = context.TaskId, Generation = context.Generation, PolicyVersion = StrategyAdvisor.PolicyVersion, Suggested = suggested, Executed = executed, Reason = reason, Context = context };
        lock (gate)
        {
            decisions.Add(decision);
            while (decisions.Count > MaxDecisions) decisions.RemoveAt(0);
            try { store.Save(FileName, decisions); } catch { decisions.Remove(decision); throw; }
            return Copy(decision);
        }
    }
    public ShadowDecision[] All() { lock (gate) return decisions.Select(Copy).ToArray(); }
    public ShadowDecision[] ForTask(string taskId) { lock (gate) return decisions.Where(d => d.TaskId == taskId).Select(Copy).ToArray(); }
    public static string Describe(ShadowDecision d) => $"{d.Time.ToLocalTime():g} · phase {d.Generation} · suggested {HubCoordinator.StrategyName(HubCoordinator.ParseStrategy(d.Suggested))}, ran {HubCoordinator.StrategyName(HubCoordinator.ParseStrategy(d.Executed))}{(d.Suggested == d.Executed ? " (same)" : " (differs)")} · {d.Reason} · {d.PolicyVersion}";
    /// <summary>How often the shadow policy agreed with what ran, and the counts behind it.</summary>
    public static string Summary(IReadOnlyList<ShadowDecision> items)
    {
        if (items.Count == 0) return "No shadow decisions recorded yet.";
        var same = items.Count(d => d.Suggested == d.Executed);
        return $"{items.Count} phase{(items.Count == 1 ? "" : "s")} observed; the shadow policy would have chosen the executed strategy in {same} ({(double)same / items.Count:P0}). Suggested independent {items.Count(d => d.Suggested == "independent")} times, reaction {items.Count(d => d.Suggested == "reaction")} times. Suggestions were never executed by the host.";
    }
    private static ShadowDecision Copy(ShadowDecision d) => JsonSerializer.Deserialize<ShadowDecision>(JsonSerializer.Serialize(d))!;
    private static bool Repair(List<ShadowDecision> items)
    {
        var before = JsonSerializer.Serialize(items); var ids = new HashSet<string>();
        items.RemoveAll(d => d is null || d.Format != 1 || !Guid.TryParseExact(d.Id, "N", out _) || !ids.Add(d.Id) || d.Suggested is not ("reaction" or "independent") || d.Executed is not ("reaction" or "independent") || string.IsNullOrWhiteSpace(d.TaskId));
        foreach (var d in items) { d.Reason ??= ""; if (d.Reason.Length > 500) d.Reason = d.Reason[..500]; d.PolicyVersion ??= ""; if (d.PolicyVersion.Length > 32) d.PolicyVersion = d.PolicyVersion[..32]; }
        while (items.Count > MaxDecisions) items.RemoveAt(0);
        return before != JsonSerializer.Serialize(items);
    }
}
