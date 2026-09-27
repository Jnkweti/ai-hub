using System.Text.RegularExpressions;

namespace AIHub.Core;

/// <summary>Host-side loop checks. Providers cannot extend or disable the round limit.</summary>
public sealed class CollaborationGuard(string userMessage)
{
    public const int DefaultMaxRounds = 6;
    private readonly Dictionary<Agent, Queue<string>> history = [];
    private readonly Dictionary<Agent, string> latest = [];
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SocialOnly = new(
        @"^(?:(?:hi|hello|hey|hiya|howdy|greetings|good morning|good afternoon|good evening)(?: (?:there|everyone|both|you two|guys|team|codex|claude|astra|ai hub))?|how are (?:you|you both|you two)|what s up|thanks(?: you)?(?: both| everyone| a lot| so much)?|thank you(?: both| everyone| so much)?|ok(?:ay)?|cool|great|awesome|sounds good|got it|nice|goodbye|bye|see you|\p{So}+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Whole-message matching preserves requests such as "Hello, review my project".
    public static bool IsSocialOnly(string text)
    {
        var normalized = Normalize(text);
        return normalized.Length > 0 && SocialOnly.IsMatch(normalized);
    }
    public void Observe(Agent agent, string reply)
    {
        latest[agent] = reply;
        if (!history.TryGetValue(agent, out var replies)) history[agent] = replies = new();
        replies.Enqueue(reply); while (replies.Count > 4) replies.Dequeue();
    }
    public string? InitialPauseReason()
    {
        if (IsSocialOnly(userMessage)) return "This was a greeting or acknowledgement, so one agent replied.";
        return StatusPauseReason();
    }
    public string? NextPauseReason(Agent agent, string reply, bool usedTools)
    {
        var repeated = !usedTools && history.TryGetValue(agent, out var replies) && replies.Any(previous => IsNearRepeat(previous, reply));
        Observe(agent, reply);
        if (IsSocialOnly(userMessage) && ConversationTurns.Handoff(reply) is null) return "This was a greeting or acknowledgement, so one agent replied.";
        if (ConversationTurns.IsPass(reply) && latest.Count == 2) return $"{ConversationTurns.Name(agent)} has no further contribution. The conversation is ready for you.";
        return StatusPauseReason() ?? (repeated ? $"{agent} repeated an earlier reply without new tool activity." : null);
    }
    private string? StatusPauseReason()
    {
        if (latest.Any(p => IsWaiting(p.Value))) return "An agent needs your input before the work can continue.";
        if (latest.Count == 2 && latest.Values.All(IsComplete)) return "Both agents reported that the task is complete.";
        return null;
    }
    private static string LastLine(string text)
    {
        var line = text.Split('\n').LastOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim().Trim('*','_').Trim() ?? "";
        // Quoted examples and inline code are not the agent's own status declaration.
        return line.Length > 0 && char.IsLetter(line[0]) ? line : "";
    }
    public static bool IsComplete(string text) => Normalize(LastLine(text)) is "task complete" or "no further action is needed";
    public static bool IsWaiting(string text) => Normalize(LastLine(text)) is "waiting for your input" or "waiting for your decision";
    private static string Normalize(string text) => string.Join(' ', Words.Matches(text.ToLowerInvariant()).Select(m => m.Value));
    public static bool IsNearRepeat(string earlier, string current)
    {
        var first = Normalize(earlier); var second = Normalize(current);
        if (first.Length == 0 || second.Length == 0) return false;
        if (first == second) return true;
        var a = first.Split(' '); var b = second.Split(' ');
        if (a.Length < 12 || b.Length < 12) return false;
        // A new number may be a test result or other new evidence, not a restatement.
        if (b.Where(w => w.Any(char.IsDigit)).Except(a).Any()) return false;
        static HashSet<string> Pairs(string[] words) => words.Zip(words.Skip(1), (x, y) => x + " " + y).ToHashSet();
        var left = Pairs(a); var right = Pairs(b);
        return (double)left.Intersect(right).Count() / left.Union(right).Count() >= .85;
    }
}
