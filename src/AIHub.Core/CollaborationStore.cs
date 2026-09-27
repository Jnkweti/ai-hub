using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Core;

public sealed record StoredCollaborationMessage(CollaborationMessage Message, string IdempotencyKey, string PayloadHash,
    bool SenderSucceeded = false, string? DeliveredToDispatch = null, string? Reason = null);
public sealed class CollaborationDocument
{
    public string Version { get; set; } = CollaborationContract.Version;
    public string TaskId { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string Workspace { get; set; } = "";
    public long LastSequence { get; set; }
    public List<StoredCollaborationMessage> Entries { get; set; } = [];
    public List<CollaborationEvidence> Evidence { get; set; } = [];
    public Dictionary<string, CollaborationSnapshot> Snapshots { get; set; } = [];
    public List<CollaborationFindingState> Findings { get; set; } = [];
    public List<SharedContextSection> ContextSections { get; set; } = [];
    public int ContextFormat { get; set; } = 1;
    public long ContextRevision { get; set; }
    public List<TaskContextRecord> ContextRecords { get; set; } = [];
    public List<ContextInputManifest> ContextInputs { get; set; } = [];
    public List<WorkAssignment> Assignments { get; set; } = [];
}

/// <summary>One store per application owner. Lock order is always TaskMemory, then this store.</summary>
public sealed partial class CollaborationStore
{
    public const int MaxMessages = 512;
    public const int MaxDocumentBytes = 8 * 1024 * 1024;
    private readonly LocalStore store;
    private readonly TaskMemory memory;
    private readonly object gate = new();
    private readonly Dictionary<string, CollaborationDocument> documents = [];
    private readonly Dictionary<string, CollaborationDispatch> active = [];
    private readonly Dictionary<string, string> unavailable = [];
    public CollaborationStore(LocalStore store, TaskMemory memory, bool preserveUnavailableTasks = false)
    {
        this.store = store; this.memory = memory;
        // Construct once at application startup, after TaskMemory has recovered abandoned runs.
        foreach (var task in memory.AllTasks())
        {
          try { memory.WithTask(task.Id, current =>
          {
            lock (gate)
            {
                if (!File.Exists(Path.Combine(store.DirectoryPath, Filename(current.Id)))) return 0;
                var document = Copy(Load(current)); var changed = false;
                for (var i = 0; i < document.Entries.Count; i++)
                    if (Unsettled(document.Entries[i]))
                    { document.Entries[i] = ChangeState(document.Entries[i], DeliveryState.Interrupted, "Application restarted; review history and explicitly continue. No message was replayed."); changed = true; }
                changed |= InterruptContext(document);
                if (changed) Save(document);
                return 0;
            }
          }); }
          catch (Exception ex) when (preserveUnavailableTasks && ex is IOException or UnauthorizedAccessException)
          {
              unavailable[task.Id] = ex.Message;
              store.RecoveryNotices.Add("Task collaboration history unavailable; preserved for recovery: " + task.Id + ". " + ex.Message);
          }
        }
    }
    public CollaborationDocument Read(string taskId) => memory.WithTask(taskId, task => { lock (gate) return Copy(Load(task)); });
    public CollaborationDispatch OpenDispatch(TaskClaim claim, Agent agent, IReadOnlyCollection<Agent> participants,
        string? incomingMessageId, CancellationToken token) => memory.WithOwner(claim, agent, task =>
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (active.ContainsKey(task.Id)) throw new CollaborationValidationException("A collaboration dispatch still owns this task.");
            if (!participants.Contains(agent) || participants.Any(p => !Enum.IsDefined(p))) throw new CollaborationValidationException("Invalid task participants.");
            var dispatch = new CollaborationDispatch(this, claim, agent, participants.ToArray(), incomingMessageId, token);
            var document = Copy(Load(task));
            for (var i = 0; i < document.Entries.Count; i++)
                if (document.Entries[i].Message.Envelope.Generation != claim.Generation && Unsettled(document.Entries[i]))
                    document.Entries[i] = ChangeState(document.Entries[i], DeliveryState.Interrupted, "An older run ended; this message was not replayed.");
            if (incomingMessageId is not null)
            {
                var index = document.Entries.FindIndex(e => e.Message.Envelope.MessageId == incomingMessageId);
                if (index < 0) throw new CollaborationValidationException("The incoming message is unavailable.");
                var entry = document.Entries[index];
                if (entry.Message.State != DeliveryState.Pending || !entry.SenderSucceeded || entry.Message.Envelope.Generation != claim.Generation || entry.Message.Envelope.Recipient != agent)
                    throw new CollaborationValidationException("This message is not eligible for delivery in this run.");
                document.Entries[index] = ChangeState(entry, DeliveryState.Delivered, "Supplied for this provider dispatch; model receipt or understanding is not guaranteed.") with { DeliveredToDispatch = dispatch.Id };
            }
            Save(document); // Fail before starting the provider when persistence is unavailable.
            active.Add(task.Id, dispatch);
            return dispatch;
        }
    });
    private T Use<T>(CollaborationDispatch dispatch, Func<WorkTask, CollaborationDocument, T> action) => memory.WithOwner(dispatch.Claim, dispatch.Agent, task =>
    {
        lock (gate)
        {
            dispatch.Token.ThrowIfCancellationRequested();
            if (!active.TryGetValue(task.Id, out var owner) || owner != dispatch) throw new CollaborationValidationException("This dispatch is closed or superseded.");
            return action(task, Load(task));
        }
    });
    internal JsonNode Call(CollaborationDispatch dispatch, Agent agent, string id, string? nativeSession,
        string tool, JsonNode? args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (agent != dispatch.Agent || id != dispatch.Id || string.IsNullOrWhiteSpace(nativeSession))
            throw new CollaborationValidationException("The tool call does not match its host-bound dispatch.");
        return Use(dispatch, (task, original) =>
        {
            token.ThrowIfCancellationRequested();
            if (tool == "get_task_context")
            {
                CollaborationContract.ValidateEmpty(args);
                return JsonSerializer.SerializeToNode(new
                {
                    schema_version = CollaborationContract.Version, task_id = task.Id, room_id = task.RoomId,
                    sender = agent.ToString(), dispatch_id = id, generation = dispatch.Claim.Generation,
                    participants = dispatch.Participants.Select(p => p.ToString()), workspace = task.Workspace,
                    allow_edits = task.AllowEdits, briefing = memory.Briefing(task.Id), snapshot_status = "host_fingerprints",
                    persistent = true, incoming_message = Incoming(original, dispatch), shared_context_sections = original.ContextSections.Count,
                    context_revision = original.ContextRevision, assignments = original.Assignments.TakeLast(8),
                    history_last_sequence = original.LastSequence, evidence_status = "host_captured_provider_events", findings = original.Findings.TakeLast(32), findings_total = original.Findings.Count
                }, CollaborationContract.JsonOptions)!;
            }
            if (tool == "get_messages") return Page(original, args);
            if (tool == "get_shared_context") return ContextPage(task, original, args, token);
            if (tool == "get_context_records") return ContextRecordsPage(original, args);
            if (tool == "read_context_record") return ReadContextRecord(original, args);
            if (tool == "get_evidence") return EvidencePage(task, original, args, token);
            if (tool == "mark_addressed")
            {
                if (args is not JsonObject fields || fields.Count != 2 || fields["finding_id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var findingId) || findingId.Length is < 1 or > 128 ||
                    fields["explanation"] is not JsonValue explanationValue || !explanationValue.TryGetValue<string>(out var explanation) || string.IsNullOrWhiteSpace(explanation) || explanation.Length > 2000)
                    throw new CollaborationValidationException("Supply finding_id and explanation (1-2000 characters).");
                var at = original.Findings.FindIndex(f => f.Id == findingId);
                if (at < 0 || !task.AllowEdits) throw new CollaborationValidationException("An existing finding and editing permission are required.");
                var changed = Copy(original);
                changed.Findings[at] = changed.Findings[at] with { Disposition = "addressed", Explanation = explanation, UpdatedBy = agent, SnapshotRef = null };
                Save(changed);
                return JsonSerializer.SerializeToNode(new { finding_id = findingId, disposition = "addressed", meaning = "Author fix claim; peer verification remains required." })!;
            }
            if (tool != "submit_message") throw new CollaborationValidationException("Unknown collaboration tool.");
            var submission = CollaborationContract.Parse(args?.ToJsonString() ?? "null");
            var hash = Hash(submission);
            var duplicate = original.Entries.FirstOrDefault(e => e.Message.Envelope.DispatchId == id && e.IdempotencyKey == submission.IdempotencyKey);
            if (duplicate is not null)
            {
                if (duplicate.PayloadHash != hash) throw new CollaborationValidationException("This idempotency key was already used for different content.");
                return Receipt(duplicate.Message, true);
            }
            if (original.Entries.Count >= MaxMessages) throw new CollaborationValidationException("This task reached its 512-message storage limit. Start a new task; existing history is retained.");
            var mine = original.Entries.Where(e => e.Message.Envelope.DispatchId == id).ToArray();
            if (mine.Length >= 16) throw new CollaborationValidationException("Dispatch message limit reached.");
            if (mine.Any(e => IsTerminal(e.Message.Content))) throw new CollaborationValidationException("This dispatch already has its terminal message. Retry its key unchanged or end the turn.");
            var content = submission.Content;
            ValidateContextRequest(task, original, dispatch, content);
            Agent? recipient = content.Recipient is null ? null : Enum.Parse<Agent>(content.Recipient);
            if (recipient == agent || recipient is { } peer && !dispatch.Participants.Contains(peer))
                throw new CollaborationValidationException("Recipient must be a different participant selected by the user.");
            ValidateReferences(original, dispatch, content);
            if (content.Findings is { } findings && findings.Select(f => f.Id).Distinct().Count() != findings.Length)
                throw new CollaborationValidationException("Finding IDs must be unique.");
            foreach (var path in content.Scope.Files.Concat(content.Findings?.Select(f => f.File) ?? [])) CollaborationPaths.Validate(task.Workspace, path);
            var document = Copy(original);
            var snapshot = CaptureSnapshot(task.Workspace, token);
            ValidateEvidenceAndReview(document, dispatch, content, snapshot);
            document.Snapshots[snapshot.Id] = snapshot;
            var envelope = new CollaborationEnvelope(CollaborationContract.Version, Guid.NewGuid().ToString("N"), task.Id, task.RoomId,
                agent, recipient, DateTimeOffset.UtcNow, dispatch.Claim.Generation, id, ++document.LastSequence, nativeSession, snapshot.Id);
            var message = new CollaborationMessage(envelope, content, DeliveryState.Accepted);
            document.Entries.Add(new(message, submission.IdempotencyKey, hash));
            token.ThrowIfCancellationRequested(); dispatch.Token.ThrowIfCancellationRequested();
            Save(document); // The receipt is returned only after the atomic document replacement succeeds.
            return Receipt(message, false);
        });
    }
    internal CollaborationMessage Complete(CollaborationDispatch dispatch) => Use(dispatch, (_, original) =>
    {
        var terminal = original.Entries.SingleOrDefault(e => e.Message.Envelope.DispatchId == dispatch.Id && IsTerminal(e.Message.Content));
        if (terminal is null) throw new CollaborationValidationException("Submit exactly one terminal message: a peer request/review result, or status blocked, assignment_complete, or no_further_contribution. Prose cannot finish a structured dispatch.");
        var document = Copy(original);
        if (terminal.Message.Content.Type is "review_request" or "review_result" || terminal.Message.Content.Status == "assignment_complete" && document.Findings.Count > 0)
        {
            var current = CaptureSnapshot(document.Workspace, dispatch.Token);
            if (!Fresh(document, terminal.Message.Envelope.SnapshotRef, current))
                throw new IOException("Project changed after review submission, or snapshot coverage is incomplete. Review is retained as interrupted; request a new review.");
            ValidateEvidenceAndReview(document, dispatch, terminal.Message.Content, current);
        }
        for (var i = 0; i < document.Entries.Count; i++)
        {
            var entry = document.Entries[i];
            if (entry.Message.Envelope.DispatchId == dispatch.Id)
            {
                var pending = ChangeState(entry, DeliveryState.Pending, "Sender turn finished successfully.") with { SenderSucceeded = true };
                document.Entries[i] = entry.Message.Envelope.Recipient is null
                    ? ChangeState(pending, DeliveryState.Delivered, "Received by the Hub as an agent report; not verified task completion.") : pending;
            }
        }
        if (dispatch.IncomingMessageId is { } incoming)
        {
            var index = document.Entries.FindIndex(e => e.Message.Envelope.MessageId == incoming);
            if (terminal.Message.Content.Status != "blocked")
                document.Entries[index] = ChangeState(document.Entries[index], DeliveryState.Answered, "A successful peer turn supplied a structured answer; its claims remain unverified.");
        }
        ApplyFindings(document, terminal.Message);
        dispatch.Token.ThrowIfCancellationRequested(); Save(document);
        active.Remove(dispatch.Claim.TaskId);
        return Copy(document).Entries.Single(e => e.Message.Envelope.MessageId == terminal.Message.Envelope.MessageId).Message;
    });
    internal CollaborationMessage? Incoming(CollaborationDispatch dispatch) => Use(dispatch, (_, document) => Incoming(Copy(document), dispatch));
    private static CollaborationMessage? Incoming(CollaborationDocument document, CollaborationDispatch dispatch) =>
        document.Entries.FirstOrDefault(e => e.Message.Envelope.MessageId == dispatch.IncomingMessageId)?.Message;
    internal void Abort(CollaborationDispatch dispatch, string reason)
    {
        memory.WithClaim(dispatch.Claim, task =>
        {
            lock (gate)
            {
                if (!active.TryGetValue(task.Id, out var owner) || owner != dispatch) return 0;
                var document = Copy(Load(task));
                for (var i = 0; i < document.Entries.Count; i++)
                {
                    var entry = document.Entries[i];
                    if (Unsettled(entry) && (entry.Message.Envelope.DispatchId == dispatch.Id || entry.DeliveredToDispatch == dispatch.Id))
                        document.Entries[i] = ChangeState(entry, DeliveryState.Interrupted, Bound(reason));
                }
                try { Save(document); } finally { active.Remove(task.Id); }
                return 0;
            }
        });
    }
    public void EndRun(TaskClaim claim, string reason) => memory.WithClaim(claim, task =>
    {
        lock (gate)
        {
            var document = Copy(Load(task)); var changed = false;
            for (var i = 0; i < document.Entries.Count; i++)
                if (document.Entries[i].Message.Envelope.Generation == claim.Generation && Unsettled(document.Entries[i]))
                { document.Entries[i] = ChangeState(document.Entries[i], DeliveryState.Interrupted, Bound(reason)); changed = true; }
            changed |= InterruptContext(document, claim.Generation);
            try { if (changed) Save(document); } finally { active.Remove(task.Id); }
            return 0;
        }
    });
    public void DeleteRoom(string roomId, Action deleteConversation) => memory.WithRoom(roomId, tasks =>
    {
        memory.DeleteRoom(roomId, () => DeleteTasks(tasks.Select(t => t.Id).ToArray(), deleteConversation));
        return 0;
    });
    private void DeleteTasks(IReadOnlyCollection<string> taskIds, Action deleteConversation)
    {
        lock (gate)
        {
            if (taskIds.Any(active.ContainsKey)) throw new IOException("A collaboration dispatch is still active.");
            var existing = taskIds.Distinct().Where(id => File.Exists(Path.Combine(store.DirectoryPath, Filename(id))))
                .ToDictionary(id => id, id => store.Load(Filename(id), () => new CollaborationDocument()));
            try
            {
                foreach (var id in existing.Keys) store.Delete(Filename(id));
                deleteConversation();
                foreach (var id in taskIds) documents.Remove(id);
            }
            catch
            {
                foreach (var pair in existing) store.Save(Filename(pair.Key), pair.Value);
                throw;
            }
        }
    }
    private CollaborationDocument Load(WorkTask task)
    {
        if (unavailable.TryGetValue(task.Id, out var reason)) throw new IOException("Collaboration history was preserved and this task cannot dispatch: " + reason);
        if (documents.TryGetValue(task.Id, out var found)) return found;
        var path = Path.Combine(store.DirectoryPath, Filename(task.Id));
        if (File.Exists(path) && new FileInfo(path).Length > MaxDocumentBytes) throw new IOException("Collaboration history exceeds its storage bound; the existing file was preserved.");
        var document = store.Load(Filename(task.Id), () => new CollaborationDocument { TaskId = task.Id, RoomId = task.RoomId, Workspace = task.Workspace });
        ValidateSaved(document, task);
        documents.Add(task.Id, document); return document;
    }
    private static void ValidateSaved(CollaborationDocument document, WorkTask task)
    {
        ValidateTaskContext(document, task);
        if (document.Version != CollaborationContract.Version || document.TaskId != task.Id || document.RoomId != task.RoomId || document.Workspace != task.Workspace ||
            document.Entries is null || document.Entries.Count > MaxMessages || document.LastSequence < 0 ||
            document.Evidence is null || document.Evidence.Count > 256 || document.Snapshots is null || document.Snapshots.Count > 1024 || document.Findings is null || document.Findings.Count > 512 ||
            document.ContextSections is null || document.ContextSections.Count > 16)
            throw new IOException("Unsupported or mismatched collaboration history. It was preserved without dispatching work.");
        var ids = new HashSet<string>(); var keys = new HashSet<string>(); long sequence = 0;
        foreach (var entry in document.Entries)
        {
            if (entry?.Message?.Envelope is not { } e || entry.Message.Content is null || e.SchemaVersion != CollaborationContract.Version ||
                e.TaskId != task.Id || e.RoomId != task.RoomId || e.Generation < 1 || e.Generation > task.Generation ||
                !Guid.TryParseExact(e.MessageId, "N", out _) || !Guid.TryParseExact(e.DispatchId, "N", out _) || !ids.Add(e.MessageId) ||
                !Enum.IsDefined(e.Sender) || (e.Recipient is { } recipient && !Enum.IsDefined(recipient)) || !Enum.IsDefined(entry.Message.State) ||
                e.Sequence <= sequence || !keys.Add(e.DispatchId + ":" + entry.IdempotencyKey))
                throw new IOException("Invalid collaboration history. The existing file was preserved.");
            try
            {
                var submission = new CollaborationSubmission(CollaborationContract.Version, entry.IdempotencyKey, entry.Message.Content);
                CollaborationContract.Parse(JsonSerializer.Serialize(submission, CollaborationContract.JsonOptions));
                if (Hash(submission) != entry.PayloadHash) throw new CollaborationValidationException("Payload checksum mismatch.");
            }
            catch (CollaborationValidationException ex) { throw new IOException("Invalid saved collaboration payload; file preserved.", ex); }
            sequence = e.Sequence;
        }
        if (sequence != document.LastSequence) throw new IOException("Invalid collaboration sequence; file preserved.");
        var contextKeys = new HashSet<string>();
        foreach (var section in document.ContextSections)
        {
            var request = document.Entries.SingleOrDefault(e => e.Message.Envelope.MessageId == section?.RequestId);
            if (section is null || request is null || !request.SenderSucceeded || request.Message.Content.Type != "context_request" ||
                section.Generation != request.Message.Envelope.Generation || !Enum.IsDefined(section.Agent) ||
                !Guid.TryParseExact(section.Id, "N", out _) || !Guid.TryParseExact(section.DispatchId, "N", out _) || string.IsNullOrWhiteSpace(section.SessionId) ||
                !contextKeys.Add(section.RequestId + ":" + section.Agent) ||
                JsonSerializer.Serialize(request.Message.Content.Assignments?.SingleOrDefault(a => a.Agent == section.Agent)?.Scope) != JsonSerializer.Serialize(section.Scope))
                throw new IOException("Invalid saved shared context; file preserved.");
            try { ContextResearchSession.ParseNotes(JsonSerializer.SerializeToNode(section.Notes, CollaborationContract.JsonOptions)); }
            catch (CollaborationValidationException ex) { throw new IOException("Invalid saved context notes; file preserved.", ex); }
        }
    }
    private void Save(CollaborationDocument document)
    {
        if (document.ContextRecords.Count > 1024 || document.ContextInputs.Count > 128 || document.Assignments.Count > 256)
            throw new IOException("Task context/input/assignment storage is full. Start a new task; existing records are retained.");
        var previous = documents.GetValueOrDefault(document.TaskId);
        if (previous is null || ContextStateFingerprint(previous) != ContextStateFingerprint(document))
            document.ContextRevision = (previous?.ContextRevision ?? document.ContextRevision) + 1;
        if (document.Snapshots.Count > 1024 || document.Findings.Count > 512 || document.ContextSections.Count > 16) throw new IOException("Task snapshot/finding/context storage is full. Start a new task; existing records are retained.");
        if (JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true }).Length > MaxDocumentBytes)
            throw new IOException("Collaboration history is full. Start a new task; existing messages were preserved.");
        store.Save(Filename(document.TaskId), document);
        documents[document.TaskId] = document;
    }
    private static CollaborationDocument Copy(CollaborationDocument document) => JsonSerializer.Deserialize<CollaborationDocument>(JsonSerializer.Serialize(document))!;
    internal static string Filename(string taskId)
    {
        if (!Guid.TryParseExact(taskId, "N", out _)) throw new IOException("Invalid task storage identifier.");
        return "collaboration-" + taskId + ".json";
    }
    private static string Hash(CollaborationSubmission submission) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(submission, CollaborationContract.JsonOptions))));
    private static string Bound(string text) => text.Length <= 2000 ? text : text[..2000];
    private static bool Unsettled(StoredCollaborationMessage entry) => entry.Message.State is DeliveryState.Accepted or DeliveryState.Pending ||
        entry.Message.State == DeliveryState.Delivered && entry.Message.Envelope.Recipient is not null;
    internal static bool IsTerminal(CollaborationContent content) => content.Type != "status" || content.Status != "progress";
    private static StoredCollaborationMessage ChangeState(StoredCollaborationMessage entry, DeliveryState state, string reason)
    {
        if (!CollaborationContract.CanTransition(entry.Message.State, state)) throw new IOException("Invalid collaboration delivery transition.");
        return entry with { Message = entry.Message with { State = state }, Reason = reason };
    }
    private static void ValidateReferences(CollaborationDocument document, CollaborationDispatch dispatch, CollaborationContent content)
    {
        if (content.ReplyTo is not null && content.ReplyTo != dispatch.IncomingMessageId)
            throw new CollaborationValidationException("reply_to must identify this dispatch's incoming message, not another task or older request.");
        var incoming = Incoming(document, dispatch);
        if (content.Type == "review_result" && incoming?.Content.Type != "review_request")
            throw new CollaborationValidationException("Review result must answer the current review request.");
        if (incoming is null || !IsTerminal(content)) return;
        if (content.ReplyTo != incoming.Envelope.MessageId) throw new CollaborationValidationException("Terminal replies must include the incoming message ID in reply_to.");
        if (incoming.Content.Type == "review_request" && content.Type != "review_result" && content.Status != "blocked")
            throw new CollaborationValidationException("Answer this review_request with review_result, or a blocked status explaining why review cannot finish.");
    }
    private static JsonNode Receipt(CollaborationMessage message, bool duplicate) => JsonSerializer.SerializeToNode(new
    {
        message_id = message.Envelope.MessageId, sequence = message.Envelope.Sequence, state = message.State.ToString().ToLowerInvariant(), duplicate,
        persistent = true, automatic_dispatch = false,
        meaning = "Stored by AI Hub. Scheduling requires successful turn completion and current user routing settings. This receipt does not verify the work."
    })!;
    private static JsonNode Page(CollaborationDocument document, JsonNode? args)
    {
        if (args is not JsonObject o || o.Count != 2 || !Integer(o["after_sequence"], out var after) || after < 0 || after > 9007199254740991L ||
            !Integer(o["limit"], out var limit) || limit is < 1 or > 20)
            throw new CollaborationValidationException("Supply after_sequence (nonnegative integer) and limit (1-20), with no other fields.");
        var items = new JsonArray(); var size = 0; var next = after;
        foreach (var entry in document.Entries.Where(e => e.Message.Envelope.Sequence > after).Take((int)limit))
        {
            var node = JsonSerializer.SerializeToNode(entry, CollaborationContract.JsonOptions)!; var length = node.ToJsonString().Length;
            if (items.Count > 0 && size + length > 48000) break;
            items.Add(node); size += length; next = entry.Message.Envelope.Sequence;
        }
        return new JsonObject { ["messages"] = items, ["next_sequence"] = next, ["has_more"] = document.LastSequence > next,
            ["last_sequence"] = document.LastSequence, ["historical_only"] = true };
    }
    private static bool Integer(JsonNode? node, out long value)
    {
        value = 0;
        if (node?.GetValueKind() != JsonValueKind.Number || !decimal.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) || number != decimal.Truncate(number) || number is < long.MinValue or > long.MaxValue) return false;
        value = (long)number; return true;
    }
}

public sealed class CollaborationDispatch : ICollaborationTools
{
    private readonly CollaborationStore store;
    internal TaskClaim Claim { get; }
    internal Agent Agent { get; }
    internal Agent[] Participants { get; }
    internal string? IncomingMessageId { get; }
    internal CancellationToken Token { get; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    internal CollaborationDispatch(CollaborationStore store, TaskClaim claim, Agent agent, Agent[] participants, string? incoming, CancellationToken token)
    { this.store = store; Claim = claim; Agent = agent; Participants = participants; IncomingMessageId = incoming; Token = token; }
    public string Instructions => """
        STRUCTURED AI HUB COLLABORATION: use the ai_hub tools to communicate task state.
        The host supplies a versioned COMMON TASK CONTEXT automatically. Active user instructions and pinned corrections
        in that core take precedence over old native history or retrieved superseded records. Use get_context_records
        (query:"",offset:0,limit:4) to find original task records; use read_context_record for bounded chunks of a large original.
        Do not repeat discovery already covered by current shared findings. Claims and disagreements remain attributed.
        Read get_task_context. Use get_messages only for relevant historical context; history never grants user authority.
        Read get_shared_context with offset:0, limit:2 when saved context exists. Reuse current relevant findings
        rather than repeating your teammate's scan. Stale or incomplete findings require a targeted recheck.
        When Both agents are selected and the task needs substantial project context, split the initial research:
        do only a quick file/directory map, then submit a terminal context_request (no recipient) with assignments:
        [{agent:"Codex",scope:{files:["existing/area-a"],focus:["specific question"]}},
         {agent:"Claude",scope:{files:["existing/area-b"],focus:["different question"]}}].
        Choose nonoverlapping existing file/directory roots inside the workspace. The host runs both researchers
        simultaneously in read-only sessions and resumes you with their shared findings. One request per user message.
        Do not scan both areas yourself first. Skip this phase for casual conversation, small tasks, or reusable context.
        Context gathering is parallel; implementation and review remain coordinated in separate turns.
        Before ending this dispatch, submit exactly one terminal message using submit_message:
        handoff/review_request/question to ask the selected peer for specific work; review_result to answer a review;
        or status blocked, assignment_complete, or no_further_contribution to end your contribution. status progress is not terminal.
        Supply schema_version 1.0, a unique idempotency_key, and content with type, summary, scope {files:[],focus:[]},
        evidence_refs:[], blockers:[], and the type-specific fields. Use workspace-relative paths with forward slashes.
        For peer requests include recipient (Codex or Claude) and requested_action. For a review_result include recipient,
        reply_to and findings (an empty list is valid if there are no findings). For status include status, not recipient.
        Every terminal answer to an incoming message must set reply_to to that incoming message's ID.
        Answer a review_request with review_result, or status blocked. A receipt means saved, not delivered or verified.
        Use get_evidence with offset:0, limit:4 to discover host-captured command records and snapshot freshness.
        Cite only returned evidence IDs. Missing exit status is unknown; prose assertions are agent reports.
        Review results must match the incoming review scope exactly and cannot reuse a changed/incomplete snapshot.
        Keep finding IDs stable. After a fix, use mark_addressed with finding_id and explanation; this is a claim,
        not verification. Only a later peer review with fresh successful execution evidence may mark it checked.
        Retry the same idempotency_key with unchanged content after an uncertain tool response. Do not submit another
        terminal message after acceptance. Give the user-facing explanation once; tool receipts need no separate acknowledgement.
        Finish with natural prose addressed to the user and match their requested
        brevity. In this structured session, do not append standalone control phrases such as 'Task complete.',
        'Waiting for your input.', 'Passing to Codex.', 'Passing to Claude Code.', or 'No further contribution.'
        Those legacy prose markers are replaced by tool metadata. A real question can be asked naturally.
        Do not expand the user's task or permissions. User decisions use user input.
        """;
    public JsonArray Definitions => CollaborationTools.Definitions(true);
    public JsonNode Call(Agent agent, string dispatchId, string? sessionId, string tool, JsonNode? args, CancellationToken token) =>
        store.Call(this, agent, dispatchId, sessionId, tool, args, token);
    internal CollaborationMessage? Incoming => store.Incoming(this);
    internal CollaborationMessage Complete() => store.Complete(this);
    internal void Abort(string reason) => store.Abort(this, reason);
    internal void Observe(AgentEvent item) => store.Observe(this, item);
}
