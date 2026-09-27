using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Core;

public sealed class StatusFinding
{
    public string Severity { get; set; } = "";
    public string Claim { get; set; } = "";
    public string[] Evidence { get; set; } = [];
}
public sealed class ProjectStatusReport
{
    public string Summary { get; set; } = "";
    public StatusFinding[] Findings { get; set; } = [];
    public string[] NextSteps { get; set; } = [];
    public string[] Limitations { get; set; } = [];
    private static readonly JsonSerializerOptions Format = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public const string Contract = """
        Return only one JSON object, without commentary or a completion sentence:
        {"summary":"concise status", "findings":[{"severity":"high|medium|low|info", "claim":"finding and root cause if known", "evidence":["relative/path.ext"]}], "nextSteps":["suggested action"], "limitations":["what you did not verify"]}
        Maximum 12 findings, 6 evidence paths per finding, 10 nextSteps and 10 limitations.
        Summary <= 2000 characters; claims <= 1000; each next step/limitation <= 500.
        Evidence must be existing files inside the supplied scope, with forward slashes and no line-number suffix.
        An empty evidence list explicitly means an unverified claim. State uncertainty and tests not run.
        Never claim tests passed unless you actually observed that result. Do not run builds or edit files for this status operation.
        """;
    public static ProjectStatusReport Parse(string text, ProjectSnapshot snapshot)
    {
        if (text.Length > 24000) throw new InvalidDataException("Status report exceeded the size limit; it was not saved.");
        text = text.Trim();
        if (text.EndsWith("Task complete.", StringComparison.Ordinal)) text = text[..^14].Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            if (firstLine < 0) throw new InvalidDataException("Status report had an invalid code fence.");
            text = text[(firstLine + 1)..^3].Trim();
        }
        ProjectStatusReport report;
        try { report = JsonSerializer.Deserialize<ProjectStatusReport>(text, Format) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException("The agent returned an unsupported status report. No shared memory was updated; retry the status check."); }
        report.Validate(snapshot); return report;
    }
    public void Validate(ProjectSnapshot snapshot)
    {
        static void Text(string? value, int max) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new InvalidDataException("Status report contains missing or oversized text."); }
        Text(Summary, 2000);
        if (Findings is null || NextSteps is null || Limitations is null || Findings.Length > 12 || NextSteps.Length > 10 || Limitations.Length > 10)
            throw new InvalidDataException("Status report contains invalid lists.");
        foreach (var finding in Findings)
        {
            if (finding is null || finding.Severity is not ("high" or "medium" or "low" or "info") || finding.Evidence is null || finding.Evidence.Length > 6)
                throw new InvalidDataException("Status report contains an invalid finding.");
            Text(finding.Claim, 1000);
            foreach (var path in finding.Evidence)
            {
                Text(path, 500);
                if (!snapshot.Files.TryGetValue(path, out var hash) || hash is "missing" or "unhashed")
                    throw new InvalidDataException("Status evidence could not be validated: " + path + ". No shared memory was updated.");
            }
        }
        foreach (var item in NextSteps.Concat(Limitations)) Text(item, 500);
    }
    public string ToJson() => JsonSerializer.Serialize(this, Format);
    public string ToMarkdown()
    {
        var output = new StringBuilder(Summary);
        foreach (var finding in Findings)
            output.Append("\n\n- **").Append(finding.Severity).Append(":** ").Append(finding.Claim)
                .Append("\n  Evidence: ").Append(finding.Evidence.Length == 0 ? "Not supplied; unverified claim." : string.Join(", ", finding.Evidence.Select(p => "`" + p.Replace("`", "") + "`")));
        if (NextSteps.Length > 0) output.Append("\n\n**Suggested next steps**\n\n- ").Append(string.Join("\n- ", NextSteps));
        if (Limitations.Length > 0) output.Append("\n\n**Not verified**\n\n- ").Append(string.Join("\n- ", Limitations));
        return output.ToString();
    }
}
