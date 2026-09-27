using System.Text.RegularExpressions;

namespace AIHub.Core;

/// <summary>Participation decisions belong to the host; models contribute content within these boundaries.</summary>
public static class CollaborationScheduler
{
    public static bool IsProgressQuestion(string prompt) => Regex.IsMatch(prompt.Trim(),
        @"^(?:where are we|how(?:'s| is) (?:it|the task|the work) going|what(?:'s| is) (?:the )?progress|progress(?: update)?|task status)[?.!]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool NeedsOptionalPeer(string prompt) => !CollaborationGuard.IsSocialOnly(prompt) &&
        !Regex.IsMatch(prompt.Trim(), @"^(?:(?:reply|respond) (?:with )?(?:only|exactly)\b|what is \d+\s*[+*/-]\s*\d+\?\s*answer with only the numeral\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static Agent First(string prompt, Agent? previousLead, IReadOnlyList<ConversationEntry> conversation) =>
        ConversationTurns.AddressedSpeaker(prompt) ?? (previousLead is { } lead ? ConversationTurns.Other(lead) : ConversationTurns.FirstSpeaker(prompt, conversation));
    public static bool Duplicate(string a, string b) => b.Length > 0 && string.Equals(
        string.Concat(a.Where(char.IsLetterOrDigit)), string.Concat(b.Where(char.IsLetterOrDigit)), StringComparison.OrdinalIgnoreCase);
}
