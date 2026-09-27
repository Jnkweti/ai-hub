using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Core;

public sealed record SharedWork(string Id, string Key, string Kind, string Operation, CollaborationScope Scope,
    Agent Owner, string DispatchId, long Generation, string Fingerprint, string EnvironmentHash,
    bool Reusable, bool Independent, string State, string Summary, string[] EvidenceRefs,
    DateTimeOffset Created, DateTimeOffset Updated);

public sealed partial class CollaborationStore
{
    private const int WorkLimit = 128;
    private static string WorkEnvironment() => TaskContextBuilder.Fingerprint(Environment.OSVersion + "\n" + Environment.Version + "\n" +
        string.Join("\n", Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().OrderBy(e => e.Key.ToString(), StringComparer.Ordinal)
            .Select(e => e.Key + "=" + e.Value)));
    private static void WorkFields(JsonNode? args, params string[] keys)
    {
        if (args is not JsonObject o || o.Count != keys.Length || keys.Any(k => !o.ContainsKey(k)))
            throw new CollaborationValidationException("Supply exactly: " + string.Join(", ", keys));
    }
    private static string WorkString(JsonNode? n, int max)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s) || s.Length > max)
            throw new CollaborationValidationException("Invalid shared work text length (1-" + max + ").");
        return s;
    }
    private static bool WorkBool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b :
        throw new CollaborationValidationException("Shared work flags must be booleans.");
    private static CollaborationScope WorkScope(JsonNode? node, string workspace)
    {
        WorkFields(node, "files", "focus");
        if (node!["files"] is not JsonArray paths || paths.Count is < 1 or > 16 || node["focus"] is not JsonArray focus || focus.Count > 16)
            throw new CollaborationValidationException("Supply 1-16 scoped paths and up to 16 focus entries.");
        var files = paths.Select(p => WorkString(p, 512)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var path in files)
        {
            if (Path.IsPathRooted(path) || path.IndexOfAny(['\\', ':', '\0']) >= 0)
                throw new CollaborationValidationException("Use relative forward-slash paths.");
            CollaborationPaths.Validate(workspace, path);
        }
        return new(files, focus.Select(p => WorkString(p, 500)).ToArray());
    }
    private static (string Fingerprint, bool Complete) WorkSnapshot(string workspace, string kind, CollaborationScope scope, CancellationToken token)
    {
        if (kind == "check") { var s = CaptureSnapshot(workspace, token); return (s.Fingerprint, s.Complete); }
        var scoped = CaptureContextAsync(workspace, scope, token).GetAwaiter().GetResult();
        return (scoped.Fingerprint, scoped.Reusable);
    }
    private JsonNode ClaimWork(WorkTask task, CollaborationDocument original, CollaborationDispatch dispatch, JsonNode? args, CancellationToken token)
    {
        WorkFields(args, "kind", "operation", "scope", "reusable", "independent");
        var kind = WorkString(args!["kind"], 16); var operation = WorkString(args["operation"], 2000).Trim();
        if (kind is not ("discovery" or "check")) throw new CollaborationValidationException("Work kind must be discovery or check.");
        var scope = WorkScope(args["scope"], task.Workspace); var reusable = WorkBool(args["reusable"]); var independent = WorkBool(args["independent"]);
        var snapshot = WorkSnapshot(task.Workspace, kind, scope, token); var environment = WorkEnvironment();
        var key = TaskContextBuilder.Fingerprint(JsonSerializer.Serialize(new { kind, operation, scope, snapshot.Fingerprint,
            environment = kind == "check" ? environment : "", generation = kind == "check" ? dispatch.Claim.Generation : 0 }));
        var retry = original.SharedWork.LastOrDefault(w => w.Key == key && w.DispatchId == dispatch.Id && w.Independent == independent && w.State == "running");
        if (retry is not null) return WorkReceipt("claimed", retry);
        if (!independent)
        {
            var running = original.SharedWork.LastOrDefault(w => w.Key == key && w.State == "running" && !w.Independent);
            if (running is not null) return WorkReceipt("in_progress", running);
            var result = original.SharedWork.LastOrDefault(w => w.Key == key && w.State == "completed" && w.Reusable && !w.Independent);
            if (reusable && snapshot.Complete && result is not null) return WorkReceipt("reused", result);
        }
        if (original.SharedWork.Count >= WorkLimit) throw new CollaborationValidationException("Shared work storage is full. Start a new task; history was retained.");
        var work = new SharedWork(Guid.NewGuid().ToString("N"), key, kind, operation, scope, dispatch.Agent, dispatch.Id,
            dispatch.Claim.Generation, snapshot.Fingerprint, environment, reusable && snapshot.Complete, independent,
            "running", "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var changed = Copy(original); changed.SharedWork.Add(work); token.ThrowIfCancellationRequested(); Save(changed);
        return WorkReceipt("claimed", work);
    }
    private JsonNode CompleteWork(WorkTask task, CollaborationDocument original, CollaborationDispatch dispatch, JsonNode? args, CancellationToken token)
    {
        WorkFields(args, "work_id", "summary", "evidence_refs");
        var id = WorkString(args!["work_id"], 32); var summary = WorkString(args["summary"], 4000);
        if (args["evidence_refs"] is not JsonArray refs || refs.Count > 16) throw new CollaborationValidationException("Supply up to 16 captured evidence IDs.");
        var evidence = refs.Select(p => WorkString(p, 128)).Distinct().ToArray();
        var at = original.SharedWork.FindIndex(w => w.Id == id); var work = at < 0 ? null : original.SharedWork[at];
        if (work is null || work.DispatchId != dispatch.Id || work.Owner != dispatch.Agent || work.Generation != dispatch.Claim.Generation)
            throw new CollaborationValidationException("Only the current owning dispatch can finish this work claim.");
        if (work.State is "completed" or "failed")
        {
            if (work.Summary != summary || !work.EvidenceRefs.SequenceEqual(evidence)) throw new CollaborationValidationException("Retry the same result unchanged.");
            return WorkReceipt(work.State, work);
        }
        if (work.State != "running") throw new CollaborationValidationException("This work claim is closed.");
        var observed = evidence.Select(e => original.Evidence.SingleOrDefault(v => v.Id == e && v.DispatchId == dispatch.Id && v.Finished)
            ?? throw new CollaborationValidationException("Evidence must be a finished native observation from this dispatch.")).ToArray();
        var snapshot = WorkSnapshot(task.Workspace, work.Kind, work.Scope, token);
        var stable = snapshot.Complete && work.Fingerprint == snapshot.Fingerprint && work.EnvironmentHash == WorkEnvironment();
        var success = true;
        if (work.Kind == "check")
        {
            var matching = observed.Where(e => e.Command.Trim() == work.Operation && e.Tool == "command").ToArray();
            if (matching.Length == 0) throw new CollaborationValidationException("A check requires captured native command evidence matching its exact operation.");
            success = matching.All(e => e.ExitCode == 0 && e.IsError != true);
            var current = new CollaborationSnapshot("", snapshot.Fingerprint, snapshot.Complete, "", "", DateTimeOffset.UtcNow);
            stable &= matching.All(e => StableEvidence(original, e, current));
        }
        var completed = work with { State = success ? "completed" : "failed", Summary = summary, EvidenceRefs = evidence,
            Reusable = work.Reusable && stable && success, Updated = DateTimeOffset.UtcNow };
        var changed = Copy(original); changed.SharedWork[at] = completed; token.ThrowIfCancellationRequested(); Save(changed);
        return WorkReceipt(completed.State, completed);
    }
    private static JsonNode WorkReceipt(string disposition, SharedWork work) => JsonSerializer.SerializeToNode(new { disposition, work,
        meaning = "Host-bound cooperative work record. Summaries are agent claims. Reuse concerns recorded inputs only; native permissions and independent review still apply. Calls outside this protocol are not deduplicated." }, CollaborationContract.JsonOptions)!;
    private static JsonNode WorkPage(CollaborationDocument document, JsonNode? args)
    {
        WorkFields(args, "offset", "limit");
        if (!Integer(args!["offset"], out var offset) || offset is < 0 or > WorkLimit || !Integer(args["limit"], out var limit) || limit is < 1 or > 8)
            throw new CollaborationValidationException("Supply offset 0-128 and limit 1-8.");
        return JsonSerializer.SerializeToNode(new { work = document.SharedWork.AsEnumerable().Reverse().Skip((int)offset).Take((int)limit),
            total = document.SharedWork.Count, meaning = "Historical records; use claim_work to check current reuse eligibility before relying on a result." }, CollaborationContract.JsonOptions)!;
    }
    private static bool InterruptWork(CollaborationDocument document, string? dispatch = null)
    {
        var changed = false;
        for (var i = 0; i < document.SharedWork.Count; i++)
            if (document.SharedWork[i].State == "running" && (dispatch is null || document.SharedWork[i].DispatchId == dispatch))
            { document.SharedWork[i] = document.SharedWork[i] with { State = "interrupted", Reusable = false, Updated = DateTimeOffset.UtcNow }; changed = true; }
        return changed;
    }
    private static void ValidateWork(CollaborationDocument document, WorkTask task)
    {
        if (document.WorkFormat != 1 || document.SharedWork is null || document.SharedWork.Count > WorkLimit) throw new IOException("Invalid shared work storage; file preserved.");
        var ids = new HashSet<string>();
        foreach (var w in document.SharedWork)
        {
            if (w is null || !Guid.TryParseExact(w.Id, "N", out _) || !ids.Add(w.Id) || !Guid.TryParseExact(w.DispatchId, "N", out _) || !Enum.IsDefined(w.Owner) ||
                w.Generation < 1 || w.Generation > task.Generation || w.Key is not { Length: 64 } || w.Fingerprint is not { Length: 64 } || w.EnvironmentHash is not { Length: 64 } ||
                w.Kind is not ("discovery" or "check") || w.State is not ("running" or "completed" or "failed" or "interrupted") ||
                string.IsNullOrWhiteSpace(w.Operation) || w.Operation.Length > 2000 || w.Summary is null || w.Summary.Length > 4000 || w.EvidenceRefs is null || w.EvidenceRefs.Length > 16)
                throw new IOException("Invalid shared work record; file preserved.");
            try { WorkScope(JsonSerializer.SerializeToNode(w.Scope, CollaborationContract.JsonOptions), task.Workspace); }
            catch (CollaborationValidationException ex) { throw new IOException("Invalid shared work scope; file preserved.", ex); }
        }
    }
}
