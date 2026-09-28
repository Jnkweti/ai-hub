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

    private static readonly Regex DateReset = new(@"(?:try again|resets?|available|until)(?: again)?(?: at| on)?\s+([A-Za-z]{3,9}\.? \d{1,2}(?:st|nd|rd|th)?,? \d{4},? \d{1,2}:\d{2} ?[AaPp][Mm])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TimeReset = new(@"(?:try again|resets?|available|until)(?: again)?(?: at)?\s+(\d{1,2}(?::\d{2})? ?(?:[AaPp][Mm]))(?:\s*\(([^)]{2,64})\))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RelativeReset = new(@"(?:in|after)\s+(\d{1,4})\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    /// <summary>
    /// When the provider will be usable again, taken from the limit message when it states a time ("try again at Oct 3rd,
    /// 2026 11:21 PM", "resets 3pm (America/New_York)", "in 20 minutes"); otherwise a short cooldown: five minutes for a
    /// rate limit, an hour for a usage limit.
    /// </summary>
    public static DateTimeOffset UnavailableUntil(string message, DateTimeOffset now)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (DateReset.Match(message) is { Success: true } date)
        {
            var text = Regex.Replace(date.Groups[1].Value, @"(\d)(st|nd|rd|th)", "$1").Replace(".", "");
            if (DateTime.TryParse(text, culture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var at))
                return new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), now.Offset);
        }
        if (TimeReset.Match(message) is { Success: true } time)
        {
            var zone = TimeZoneInfo.Local;
            if (time.Groups[2].Success) try { zone = TimeZoneInfo.FindSystemTimeZoneById(time.Groups[2].Value.Trim()); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
            var zoned = TimeZoneInfo.ConvertTime(now, zone);
            if (DateTime.TryParse(time.Groups[1].Value.Replace(" ", ""), culture, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out var clock))
            {
                var candidate = new DateTimeOffset(zoned.Date.Add(clock.TimeOfDay), zoned.Offset);
                if (candidate <= zoned) candidate = candidate.AddDays(1);
                return candidate;
            }
        }
        if (RelativeReset.Match(message) is { Success: true } relative && int.TryParse(relative.Groups[1].Value, out var amount))
        {
            var unit = relative.Groups[2].Value.ToLowerInvariant();
            return now.Add(unit.StartsWith('h') ? TimeSpan.FromHours(amount) : unit.StartsWith('m') ? TimeSpan.FromMinutes(amount) : TimeSpan.FromSeconds(amount));
        }
        return now.Add(Regex.IsMatch(message, @"rate limit|rate-limited|too many requests|\b429\b", RegexOptions.IgnoreCase) ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1));
    }
}
