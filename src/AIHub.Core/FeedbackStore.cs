using System.Text;
using System.Text.Json;

namespace AIHub.Core;

public enum FeedbackKind { Useful, NeedsCorrection, PreferredAlternative }
/// <summary>How widely the developer means the feedback to apply; the developer chooses, the host never widens it.</summary>
public enum FeedbackScope { Task, Project, Category, General }

/// <summary>
/// One explicit piece of developer feedback on a contribution (a saved message) or on a task. It links to what it judges by
/// ID and records the outcome and the coordination settings in force at the time; it never copies the message text.
/// Explicit feedback only: nothing here is inferred from approvals, edits or silence.
/// </summary>
public sealed class FeedbackRecord
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public string RoomId { get; set; } = "";
    /// <summary>The saved message judged; null for feedback on the task as a whole.</summary>
    public string? MessageId { get; set; }
    /// <summary>The host dispatch that produced the message, when the message came from a structured turn.</summary>
    public string? DispatchId { get; set; }
    /// <summary>The ledger message, when the saved message is a structured collaboration card.</summary>
    public string? LedgerMessageId { get; set; }
    public string TaskId { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string Agent { get; set; } = "";
    public FeedbackKind Kind { get; set; }
    public string[] Dimensions { get; set; } = [];
    public string Explanation { get; set; } = "";
    public FeedbackScope Scope { get; set; } = FeedbackScope.Task;
    public string Category { get; set; } = "";
    /// <summary>For a preferred alternative: the saved message the developer preferred instead, when one exists.</summary>
    public string? AlternativeMessageId { get; set; }
    public string OutcomeState { get; set; } = "";
    public string OutcomeReason { get; set; } = "";
    public string AppVersion { get; set; } = "";
    /// <summary>Fingerprint of the coordination settings in force when the judged work ran, so later comparisons know what strategy produced it.</summary>
    public string StrategyVersion { get; set; } = "";
    /// <summary>SHA-256 of the judged text at the time, so a later edit of the message is detectable without storing the text.</summary>
    public string ExcerptHash { get; set; } = "";
}

/// <summary>Local, inspectable feedback: added, edited, deleted and exported by the developer; never supplied to agents by this store.</summary>
public sealed class FeedbackStore
{
    public const string FileName = "feedback.json";
    public const int MaxRecords = 4096, MaxExplanation = 2000, MaxCategory = 100;
    public static readonly string[] KnownDimensions = ["correctness", "relevance", "explanation", "scope", "initiative", "collaboration", "effort"];
    private readonly LocalStore store;
    private readonly object gate = new();
    private readonly List<FeedbackRecord> records;
    public FeedbackStore(LocalStore store)
    {
        this.store = store;
        records = store.Load(FileName, () => new List<FeedbackRecord>(), Repair);
    }
    public static string Fingerprint(string text) => TaskContextBuilder.Fingerprint(text);
    public static string StrategyFingerprint(HubSettings settings) => Fingerprint(JsonSerializer.Serialize(new
    { settings.AutoExchange, settings.MaxAutoRounds, settings.AllowEdits, settings.CodexModel, settings.ClaudeModel, settings.MidTurnPush, settings.IsolateAgentWorktrees, settings.Strategy }));
    public FeedbackRecord Add(FeedbackRecord record)
    {
        Validate(record);
        lock (gate)
        {
            if (records.Any(r => r.Id == record.Id)) throw new InvalidOperationException("A feedback record with this identity already exists.");
            if (records.Count >= MaxRecords) throw new IOException("Feedback storage is full. Export and delete older feedback; existing records were preserved.");
            record.Created = record.Updated = DateTimeOffset.UtcNow;
            records.Add(record);
            try { Persist(); } catch { records.Remove(record); throw; }
            return Copy(record);
        }
    }
    public FeedbackRecord Update(string id, Action<FeedbackRecord> change)
    {
        lock (gate)
        {
            var index = records.FindIndex(r => r.Id == id);
            if (index < 0) throw new InvalidOperationException("This feedback record no longer exists.");
            var before = Copy(records[index]); var changed = Copy(records[index]);
            change(changed);
            changed.Id = before.Id; changed.Created = before.Created; changed.Updated = DateTimeOffset.UtcNow;
            Validate(changed);
            records[index] = changed;
            try { Persist(); } catch { records[index] = before; throw; }
            return Copy(changed);
        }
    }
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
    /// <summary>Removes a deleted conversation's feedback; returns the ids that went with it so dependent preferences can react.</summary>
    public string[] DeleteRoom(string roomId)
    {
        lock (gate)
        {
            var removed = records.Where(r => r.RoomId == roomId).ToArray();
            if (removed.Length == 0) return [];
            records.RemoveAll(r => r.RoomId == roomId);
            try { Persist(); } catch { records.AddRange(removed); throw; }
            return removed.Select(r => r.Id).ToArray();
        }
    }
    public FeedbackRecord[] All() { lock (gate) return records.OrderByDescending(r => r.Updated).Select(Copy).ToArray(); }
    public FeedbackRecord? Get(string id) { lock (gate) return records.FirstOrDefault(r => r.Id == id) is { } r ? Copy(r) : null; }
    public FeedbackRecord[] ForMessage(string messageId) { lock (gate) return records.Where(r => r.MessageId == messageId).Select(Copy).ToArray(); }
    public FeedbackRecord[] ForTask(string taskId) { lock (gate) return records.Where(r => r.TaskId == taskId).Select(Copy).ToArray(); }
    public FeedbackRecord[] ForRoom(string roomId) { lock (gate) return records.Where(r => r.RoomId == roomId).Select(Copy).ToArray(); }
    public static string Label(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Useful => "Useful", FeedbackKind.NeedsCorrection => "Needs correction", FeedbackKind.PreferredAlternative => "Preferred alternative", _ => kind.ToString()
    };
    public static string Label(FeedbackScope scope) => scope switch
    {
        FeedbackScope.Task => "this task", FeedbackScope.Project => "this project", FeedbackScope.Category => "this kind of task", FeedbackScope.General => "in general", _ => scope.ToString()
    };
    /// <summary>One line per record for lists; the export and the review packet use the same wording.</summary>
    public static string Summary(FeedbackRecord r) =>
        $"{r.Updated.ToLocalTime():g} · {Label(r.Kind)}{(r.Agent.Length > 0 ? " · " + r.Agent : "")}{(r.MessageId is null ? " · task" : "")} · applies to {Label(r.Scope)}{(r.Scope == FeedbackScope.Category && r.Category.Length > 0 ? " (" + r.Category + ")" : "")}" +
        $"{(r.Dimensions.Length > 0 ? " · " + string.Join(", ", r.Dimensions) : "")}{(r.Explanation.Length > 0 ? ": " + r.Explanation.Replace('\n', ' ') : "")}";
    public static string Export(IEnumerable<FeedbackRecord> items)
    {
        var text = new StringBuilder("# AI Hub feedback\n\nExplicit developer feedback, linked by ID to saved messages, host dispatches and tasks. The judged text is not copied here; its hash is. Absence of feedback means unknown, not approval.\n");
        foreach (var r in items)
        {
            text.AppendLine($"\n## {r.Updated.ToLocalTime():g} · {Label(r.Kind)}{(r.Agent.Length > 0 ? " · " + r.Agent : "")}\n");
            text.AppendLine($"- Applies to: {Label(r.Scope)}{(r.Scope == FeedbackScope.Category && r.Category.Length > 0 ? " (" + r.Category + ")" : "")}");
            if (r.Dimensions.Length > 0) text.AppendLine("- Dimensions: " + string.Join(", ", r.Dimensions));
            if (r.Explanation.Length > 0) text.AppendLine("- Explanation: " + r.Explanation.Replace("\n", "\n  "));
            text.AppendLine($"- Links: room {r.RoomId}{(r.MessageId is null ? "" : ", message " + r.MessageId)}{(r.DispatchId is null ? "" : ", dispatch " + r.DispatchId)}{(r.LedgerMessageId is null ? "" : ", ledger message " + r.LedgerMessageId)}{(r.TaskId.Length > 0 ? ", task " + r.TaskId : "")}{(r.AlternativeMessageId is null ? "" : ", preferred message " + r.AlternativeMessageId)}");
            text.AppendLine($"- Outcome when recorded: {(r.OutcomeState.Length > 0 ? r.OutcomeState : "unknown")}{(r.OutcomeReason.Length > 0 ? " — " + r.OutcomeReason.Replace('\n', ' ') : "")}");
            text.AppendLine($"- App {r.AppVersion}, strategy {Short(r.StrategyVersion)}, judged text hash {Short(r.ExcerptHash)}, record {r.Id} (created {r.Created.ToLocalTime():g})");
        }
        return text.ToString();
    }
    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash.Length == 0 ? "none" : hash;
    private static FeedbackRecord Copy(FeedbackRecord r) => JsonSerializer.Deserialize<FeedbackRecord>(JsonSerializer.Serialize(r))!;
    private void Persist() => store.Save(FileName, records);
    private static bool Identifier(string? value, bool required) => value is null ? !required : value.Length > 0 && value.Length <= 160 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
    private static void Validate(FeedbackRecord r)
    {
        if (!Guid.TryParseExact(r.Id, "N", out _)) throw new ArgumentException("Feedback record identity is invalid.");
        if (!Enum.IsDefined(r.Kind) || !Enum.IsDefined(r.Scope)) throw new ArgumentException("Choose a feedback kind and where it applies.");
        if (!Identifier(r.RoomId, true) || !Identifier(r.MessageId, false) || !Identifier(r.DispatchId, false) || !Identifier(r.LedgerMessageId, false) || !Identifier(r.AlternativeMessageId, false) || !Identifier(r.TaskId.Length == 0 ? null : r.TaskId, false))
            throw new ArgumentException("Feedback must link to a saved conversation, and optionally a message, dispatch or task, by their identifiers.");
        if (r.Explanation is null || r.Explanation.Length > MaxExplanation) throw new ArgumentException($"The explanation is limited to {MaxExplanation:N0} characters.");
        if (r.Category is null || r.Category.Length > MaxCategory || r.Scope == FeedbackScope.Category && string.IsNullOrWhiteSpace(r.Category)) throw new ArgumentException("Name the kind of task this feedback applies to (up to 100 characters).");
        if (r.Dimensions is null || r.Dimensions.Distinct().Count() != r.Dimensions.Length || r.Dimensions.Any(d => !KnownDimensions.Contains(d))) throw new ArgumentException("Dimensions must be distinct and from the known set.");
        if (r.Kind == FeedbackKind.PreferredAlternative && r.AlternativeMessageId is null && r.Explanation.Trim().Length == 0) throw new ArgumentException("Say what you preferred instead, or pick the message you preferred.");
        if (r.Agent is null || r.Agent.Length > 32 || r.Workspace is null || r.Workspace.Length > 1024 || r.OutcomeState is null || r.OutcomeState.Length > 32 || r.OutcomeReason is null || r.OutcomeReason.Length > 2000 ||
            r.AppVersion is null || r.AppVersion.Length > 32 || r.StrategyVersion is null || r.StrategyVersion.Length > 64 || r.ExcerptHash is null || r.ExcerptHash.Length > 64)
            throw new ArgumentException("Feedback metadata is oversized.");
    }
    private static bool Repair(List<FeedbackRecord> items)
    {
        var before = JsonSerializer.Serialize(items); var ids = new HashSet<string>(StringComparer.Ordinal);
        items.RemoveAll(r => r is null || r.Version != 1 || !Guid.TryParseExact(r.Id, "N", out _) || !ids.Add(r.Id) || !Enum.IsDefined(r.Kind) || !Enum.IsDefined(r.Scope) || !Identifier(r.RoomId, true));
        foreach (var r in items)
        {
            r.MessageId = Identifier(r.MessageId, false) ? r.MessageId : null; r.DispatchId = Identifier(r.DispatchId, false) ? r.DispatchId : null;
            r.LedgerMessageId = Identifier(r.LedgerMessageId, false) ? r.LedgerMessageId : null; r.AlternativeMessageId = Identifier(r.AlternativeMessageId, false) ? r.AlternativeMessageId : null;
            r.TaskId = Identifier(r.TaskId, true) ? r.TaskId : "";
            r.Explanation = Bound(r.Explanation, MaxExplanation); r.Category = Bound(r.Category, MaxCategory); r.Agent = Bound(r.Agent, 32); r.Workspace = Bound(r.Workspace, 1024);
            r.OutcomeState = Bound(r.OutcomeState, 32); r.OutcomeReason = Bound(r.OutcomeReason, 2000); r.AppVersion = Bound(r.AppVersion, 32); r.StrategyVersion = Bound(r.StrategyVersion, 64); r.ExcerptHash = Bound(r.ExcerptHash, 64);
            r.Dimensions = (r.Dimensions ?? []).Where(d => d is not null && KnownDimensions.Contains(d)).Distinct().ToArray();
            if (r.Scope == FeedbackScope.Category && r.Category.Trim().Length == 0) r.Scope = FeedbackScope.Task;
        }
        while (items.Count > MaxRecords) items.RemoveAt(0);
        return before != JsonSerializer.Serialize(items);
    }
    private static string Bound(string? text, int limit) => text is null ? "" : text.Length <= limit ? text : text[..limit];
}
