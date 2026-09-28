using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Core;

/// <summary>
/// Reads the providers' usage reports. Codex reports cumulative per-thread totals (tokenUsage.total); Claude Code
/// reports per-turn tokens and a cumulative session cost (total_cost_usd). Cached input tokens are counted inside input.
/// </summary>
public static class ProviderUsage
{
    public readonly record struct Sample(long Input, long Cached, long Output, decimal Cost, bool TokensCumulative, bool CostCumulative);
    public static bool TryParse(Agent agent, string detail, out Sample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(detail)) return false;
        JsonNode? node;
        try { node = JsonNode.Parse(detail); } catch (JsonException) { return false; }
        if (node?["tokenUsage"]?["total"] is JsonObject total)
        {
            sample = new(Number(total, "inputTokens"), Number(total, "cachedInputTokens"), Number(total, "outputTokens"), 0m, true, false);
            return true;
        }
        if (node?["usage"] is JsonObject usage)
        {
            var cached = Number(usage, "cache_read_input_tokens");
            var input = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + cached;
            var cost = node["costUsd"] is JsonValue c && c.TryGetValue<decimal>(out var d) ? d : node["costUsd"] is JsonValue f && f.TryGetValue<double>(out var dd) ? (decimal)dd : 0m;
            sample = new(input, cached, Number(usage, "output_tokens"), cost, false, true);
            return true;
        }
        return false;
    }
    private static long Number(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : o[key] is JsonValue w && w.TryGetValue<double>(out var x) ? (long)x : 0;
    public static string Format(long tokens) => tokens.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
