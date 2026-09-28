using System.Text.RegularExpressions;

namespace AIHub.Core;

/// <summary>
/// Recognises a provider reply that means "not now": a usage or rate limit, or exhausted credits. Such a participant
/// sits out the rest of the phase while the other one continues; every other provider failure still ends the run.
/// </summary>
public static class ProviderLimits
{
    private static readonly Regex Pattern = new(
        @"usage limit|rate limit|rate-limited|quota|too many requests|\b429\b|out of credits|insufficient credits|purchase more credits",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static bool IsExhausted(string message) => Pattern.IsMatch(message);
}
