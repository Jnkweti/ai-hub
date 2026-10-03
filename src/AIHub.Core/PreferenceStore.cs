using System.Text;
using System.Text.Json;

namespace AIHub.Core;

/// <summary>
/// A developer preference the host may supply to agents on relevant tasks (0.30.0). The developer writes or confirms every
/// preference; the host never promotes a one-time correction into one. It carries its scope, the feedback that supports it,
/// a version that rises on every edit (input manifests record which version an agent saw), and an enabled flag.
/// </summary>
public sealed class PreferenceRecord
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Version { get; set; } = 1;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public string Text { get; set; } = "";
    public FeedbackScope Scope { get; set; } = FeedbackScope.Project;
    public string Workspace { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Why it is disabled, when the host did it: for example its supporting feedback was deleted.</summary>
    public string DisabledReason { get; set; } = "";
    /// <summary>"stated": written by the developer; "from_feedback": confirmed by the developer from a feedback record.</summary>
    public string Origin { get; set; } = "stated";
    public string[] SupportingFeedbackIds { get; set; } = [];
    /// <summary>"stated" for a developer-written preference; "tentative" while it rests only on feedback the developer has not restated.</summary>
    public string Confidence { get; set; } = "stated";
}

/// <summary>Local, inspectable preference memory. Relevance is by scope only; nothing is inferred.</summary>
public sealed class PreferenceStore
{
    public const string FileName = "preferences.json";
    public const int MaxRecords = 512, MaxText = 1000, MaxSupplied = 12, SuppliedByteBudget = 6000;
    private readonly LocalStore store;
    private readonly object gate = new();
    private readonly List<PreferenceRecord> records;
    public PreferenceStore(LocalStore store)
    {
        this.store = store;
        records = store.Load(FileName, () => new List<PreferenceRecord>(), Repair);
    }
    public PreferenceRecord Add(PreferenceRecord record)
    {
        Validate(record);
        lock (gate)
        {
            if (records.Any(r => r.Id == record.Id)) throw new InvalidOperationException("A preference with this identity already exists.");
            if (records.Count >= MaxRecords) throw new IOException("Preference storage is full. Delete preferences you no longer want; existing ones were preserved.");
            record.Version = 1; record.Created = record.Updated = DateTimeOffset.UtcNow;
            records.Add(record);
            try { Persist(); } catch { records.Remove(record); throw; }
            return Copy(record);
        }
    }
    /// <summary>Edits raise the version, so a manifest that names an earlier version is distinguishable from one that saw this text.</summary>
    public PreferenceRecord Update(string id, Action<PreferenceRecord> change)
    {
        lock (gate)
        {
            var index = records.FindIndex(r => r.Id == id);
            if (index < 0) throw new InvalidOperationException("This preference no longer exists.");
            var before = Copy(records[index]); var changed = Copy(records[index]);
            change(changed);
            changed.Id = before.Id; changed.Created = before.Created; changed.Updated = DateTimeOffset.UtcNow;
            changed.Version = before.Version + (changed.Text != before.Text || changed.Scope != before.Scope || changed.Workspace != before.Workspace || changed.TaskId != before.TaskId || changed.Category != before.Category ? 1 : 0);
            Validate(changed);
            records[index] = changed;
            try { Persist(); } catch { records[index] = before; throw; }
            return Copy(changed);
        }
    }
    public PreferenceRecord SetEnabled(string id, bool enabled, string reason = "") => Update(id, r => { r.Enabled = enabled; r.DisabledReason = enabled ? "" : reason; });
    public bool Delete(string id)
    {
        lock (gate)
        {
            var index = records.FindIndex(r => r.Id == id);
            if (index < 0) return false;
            var removed = records[index]; records.RemoveAt(index);
            try { Persist(); } catch { records.Insert(index, removed); throw; }
            return true;
        }
    }
    /// <summary>
    /// A deleted feedback record stops supporting preferences. A preference that rested only on it is disabled, not deleted,
    /// so the developer can restate it or let it go; one the developer wrote themselves keeps its enabled state.
    /// </summary>
    public PreferenceRecord[] FeedbackDeleted(string feedbackId)
    {
        var affected = new List<PreferenceRecord>();
        lock (gate)
        {
            foreach (var r in records.Where(r => r.SupportingFeedbackIds.Contains(feedbackId)).ToArray())
            {
                var remaining = r.SupportingFeedbackIds.Where(f => f != feedbackId).ToArray();
                var disable = remaining.Length == 0 && r.Origin == "from_feedback" && r.Enabled;
                affected.Add(Update(r.Id, p => { p.SupportingFeedbackIds = remaining; if (disable) { p.Enabled = false; p.DisabledReason = "The feedback this preference was made from was deleted. Restate it to enable it again."; } }));
            }
        }
        return affected.ToArray();
    }
    public PreferenceRecord[] All() { lock (gate) return records.OrderByDescending(r => r.Updated).Select(Copy).ToArray(); }
    public PreferenceRecord? Get(string id) { lock (gate) return records.FirstOrDefault(r => r.Id == id) is { } r ? Copy(r) : null; }
    /// <summary>
    /// The enabled preferences that apply to a task: general ones, this project's, and this task's own. Category-scoped
    /// preferences are not applied automatically (tasks carry no category); they stay visible in the Preferences window.
    /// </summary>
    public PreferenceRecord[] Relevant(string workspace, string taskId)
    {
        var path = NormalizePath(workspace);
        lock (gate) return records.Where(r => r.Enabled && (r.Scope == FeedbackScope.General ||
                r.Scope == FeedbackScope.Project && SamePath(r.Workspace, path) ||
                r.Scope == FeedbackScope.Task && r.TaskId == taskId))
            .OrderBy(r => r.Scope switch { FeedbackScope.Task => 0, FeedbackScope.Project => 1, _ => 2 }).ThenBy(r => r.Created).Select(Copy).ToArray();
    }
    /// <summary>The identifier a manifest records for a supplied preference: id and version, so edits are distinguishable later.</summary>
    public static string SuppliedId(PreferenceRecord r) => "pref:" + r.Id + ":v" + r.Version;
    public static string Describe(PreferenceRecord r) =>
        $"{(r.Enabled ? "" : "(disabled) ")}{r.Text.Replace('\n', ' ')} · applies to {FeedbackStore.Label(r.Scope)}{(r.Scope == FeedbackScope.Category && r.Category.Length > 0 ? " (" + r.Category + ")" : "")} · v{r.Version} · {(r.Origin == "from_feedback" ? "from feedback" : "stated")}{(r.Confidence == "tentative" ? ", tentative" : "")}{(r.DisabledReason.Length > 0 ? " · " + r.DisabledReason : "")}";
    public static string Export(IEnumerable<PreferenceRecord> items)
    {
        var text = new StringBuilder("# AI Hub preferences\n\nDeveloper-confirmed preferences the host supplies to agents on relevant tasks, below active instructions. Disabled ones are not supplied.\n");
        foreach (var r in items)
            text.AppendLine($"\n- {Describe(r)}\n  Links: {(r.Scope == FeedbackScope.Project ? "project " + r.Workspace : r.Scope == FeedbackScope.Task ? "task " + r.TaskId : r.Scope.ToString().ToLowerInvariant())}; supporting feedback: {(r.SupportingFeedbackIds.Length == 0 ? "none" : string.Join(", ", r.SupportingFeedbackIds))}; record {r.Id} (created {r.Created.ToLocalTime():g}, updated {r.Updated.ToLocalTime():g})");
        return text.ToString();
    }
    internal static string NormalizePath(string path) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; } }
    private static bool SamePath(string a, string b) => string.Equals(NormalizePath(a), b, StringComparison.OrdinalIgnoreCase);
    private static PreferenceRecord Copy(PreferenceRecord r) => JsonSerializer.Deserialize<PreferenceRecord>(JsonSerializer.Serialize(r))!;
    private void Persist() => store.Save(FileName, records);
    private static bool Identifier(string value) => value.Length > 0 && value.Length <= 160 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static void Validate(PreferenceRecord r)
    {
        if (!Guid.TryParseExact(r.Id, "N", out _)) throw new ArgumentException("Preference identity is invalid.");
        if (string.IsNullOrWhiteSpace(r.Text) || r.Text.Length > MaxText) throw new ArgumentException($"Write the preference in 1 to {MaxText:N0} characters.");
        if (!Enum.IsDefined(r.Scope)) throw new ArgumentException("Choose where the preference applies.");
        if (r.Scope == FeedbackScope.Project && string.IsNullOrWhiteSpace(r.Workspace)) throw new ArgumentException("A project preference needs its project folder.");
        if (r.Scope == FeedbackScope.Task && !Identifier(r.TaskId)) throw new ArgumentException("A task preference needs its task.");
        if (r.Scope == FeedbackScope.Category && string.IsNullOrWhiteSpace(r.Category) || r.Category is null || r.Category.Length > FeedbackStore.MaxCategory) throw new ArgumentException("Name the kind of task (up to 100 characters).");
        if (r.Origin is not ("stated" or "from_feedback") || r.Confidence is not ("stated" or "tentative")) throw new ArgumentException("Preference origin or confidence is invalid.");
        if (r.SupportingFeedbackIds is null || r.SupportingFeedbackIds.Length > 64 || r.SupportingFeedbackIds.Any(f => !Guid.TryParseExact(f, "N", out _)) || r.SupportingFeedbackIds.Distinct().Count() != r.SupportingFeedbackIds.Length)
            throw new ArgumentException("Supporting feedback links are invalid.");
        if (r.Workspace is null || r.Workspace.Length > 1024 || r.TaskId is null || r.TaskId.Length > 160 || r.DisabledReason is null || r.DisabledReason.Length > 500 || r.Version < 1) throw new ArgumentException("Preference metadata is oversized.");
    }
    private static bool Repair(List<PreferenceRecord> items)
    {
        var before = JsonSerializer.Serialize(items); var ids = new HashSet<string>(StringComparer.Ordinal);
        items.RemoveAll(r => r is null || r.Format != 1 || !Guid.TryParseExact(r.Id, "N", out _) || !ids.Add(r.Id) || !Enum.IsDefined(r.Scope) || string.IsNullOrWhiteSpace(r.Text));
        foreach (var r in items)
        {
            r.Text = r.Text.Length <= MaxText ? r.Text : r.Text[..MaxText]; r.Workspace ??= ""; r.TaskId ??= ""; r.Category ??= ""; r.DisabledReason ??= "";
            if (r.Workspace.Length > 1024) r.Workspace = r.Workspace[..1024]; if (r.TaskId.Length > 160 || r.TaskId.Length > 0 && !Identifier(r.TaskId)) r.TaskId = ""; if (r.Category.Length > FeedbackStore.MaxCategory) r.Category = r.Category[..FeedbackStore.MaxCategory];
            if (r.DisabledReason.Length > 500) r.DisabledReason = r.DisabledReason[..500];
            if (r.Origin is not ("stated" or "from_feedback")) r.Origin = "stated"; if (r.Confidence is not ("stated" or "tentative")) r.Confidence = "stated";
            r.SupportingFeedbackIds = (r.SupportingFeedbackIds ?? []).Where(f => f is not null && Guid.TryParseExact(f, "N", out _)).Distinct().ToArray();
            if (r.Version < 1) r.Version = 1;
            // A scope whose anchor is missing cannot be matched; it is disabled rather than silently widened.
            if (r.Scope == FeedbackScope.Project && r.Workspace.Length == 0 || r.Scope == FeedbackScope.Task && r.TaskId.Length == 0 || r.Scope == FeedbackScope.Category && r.Category.Trim().Length == 0)
            { r.Enabled = false; r.DisabledReason = "Its scope lost the project, task or category it referred to."; }
        }
        while (items.Count > MaxRecords) items.RemoveAt(0);
        return before != JsonSerializer.Serialize(items);
    }
}
