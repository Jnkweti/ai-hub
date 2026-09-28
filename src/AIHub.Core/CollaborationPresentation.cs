using System.Text;

namespace AIHub.Core;

public static class CollaborationPresentation
{
    public static string LoadWorkflows(string directory)
    {
        var content = new StringBuilder("\n\nAI HUB PACKAGED WORKFLOWS (apply only the relevant workflow):\n");
        foreach (var name in new[] { "implement-review", "investigate-check", "resume-task" })
        {
            var path = Path.Combine(directory, "skills", name, "SKILL.md");
            if (!File.Exists(path) || new FileInfo(path).Length > 16000) throw new IOException("Required collaboration workflow is missing or oversized: " + name);
            content.AppendLine(File.ReadAllText(path));
        }
        return content.ToString();
    }
    public static string Message(CollaborationMessage message)
    {
        var c = message.Content; var e = message.Envelope;
        var text = new StringBuilder($"**{c.Type.Replace('_', ' ')}** · {e.Sender} → {e.Recipient?.ToString() ?? "AI Hub"}\n\n{c.Summary}\n\n");
        if (c.RequestedAction is { } action) text.AppendLine("Requested action: " + action + "\n");
        foreach (var assignment in c.Assignments ?? []) text.AppendLine($"{ConversationTurns.Name(assignment.Agent)} researches {string.Join(", ", assignment.Scope.Files)}: {string.Join("; ", assignment.Scope.Focus)}\n");
        text.AppendLine("Scope: " + string.Join(", ", c.Scope.Files.Concat(c.Scope.Focus)) + "\n");
        foreach (var finding in c.Findings ?? []) text.AppendLine($"- {finding.Id} · {finding.Disposition} · {finding.File}:{finding.Line}: {finding.Explanation}");
        if (c.EvidenceRefs.Length > 0) text.AppendLine("\nEvidence: " + string.Join(", ", c.EvidenceRefs));
        if (c.Blockers.Length > 0) text.AppendLine("\nBlockers: " + string.Join("; ", c.Blockers));
        text.AppendLine($"\nMessage {e.Sequence} · {message.State} at publication · request {c.ReplyTo ?? "none"}\n\nAgent report; review freshness and delivery history are available in Tasks and notes.");
        return text.ToString();
    }
    /// <summary>
    /// A review packet: everything a reviewer needs in one markdown file, with each claim attributed and its freshness
    /// stated. Generated from the ledger; nothing here is host certification of the agents' conclusions.
    /// </summary>
    public static string ReviewPacket(CollaborationDocument document, WorkTask task, CollaborationSnapshot? current)
    {
        bool Fresh(string? reference) => current is { Complete: true } && reference is not null && document.Snapshots.TryGetValue(reference, out var s) && s.Complete && s.Fingerprint == current.Fingerprint;
        var objective = task.Objective.Replace('\n', ' '); if (objective.Length > 90) objective = objective[..90] + "…";
        var text = new StringBuilder($"# Review packet · {objective}\n\n");
        text.AppendLine($"Task `{task.Id}` in `{task.Workspace}` · state {task.State}{(task.Owner.Length > 0 ? " · owner " + task.Owner : "")} · updated {task.Updated:g} · phase {task.Generation}");
        if (task.Reason.Length > 0) text.AppendLine($"\n{task.Reason}");
        if (current is not null) text.AppendLine($"\nSnapshot at {current.CapturedAt:g}: {(current.Complete ? "complete" : "incomplete")} · {current.Coverage}{(current.Limitation.Length > 0 ? " · " + current.Limitation : "")}");
        text.AppendLine("\n## Objective and active instructions\n");
        text.AppendLine(task.Objective);
        foreach (var r in TaskContextBuilder.ActiveInstructions(document).Where(r => r.Kind != "user_message")) text.AppendLine($"\n- {r.Kind.Replace('_', ' ')} ({r.Created:g}): {r.Text}");
        var assignments = document.Assignments.Where(a => a.Generation == task.Generation).ToArray();
        if (assignments.Length > 0)
        {
            text.AppendLine("\n## Assignments in this phase\n");
            foreach (var a in assignments) text.AppendLine($"- {ConversationTurns.Name(a.Agent)} · {a.Role} · {a.State}: {a.Goal.Replace('\n', ' ')}");
        }
        text.AppendLine("\n## Findings\n");
        if (document.Findings.Count == 0) text.AppendLine("None recorded.");
        foreach (var group in document.Findings.GroupBy(f => f.Disposition).OrderBy(g => g.Key switch { "open" => 0, "disputed" => 1, "addressed" => 2, _ => 3 }))
        {
            text.AppendLine($"### {group.Key}\n");
            foreach (var f in group)
                text.AppendLine($"- `{f.Id}` {f.File}:{f.Line} · reported by {f.Reporter}, last updated by {f.UpdatedBy} · {(f.SnapshotRef is null ? "no snapshot" : Fresh(f.SnapshotRef) ? "fresh against the current snapshot" : "stale: files changed since")}\n  {f.Explanation.Replace('\n', ' ')}");
        }
        var reviews = document.Entries.Where(e => e.SenderSucceeded && e.Message.Content.Type == "review_result").ToArray();
        if (reviews.Length > 0)
        {
            text.AppendLine("\n## Reviews\n");
            foreach (var e in reviews.TakeLast(10))
                text.AppendLine($"- #{e.Message.Envelope.Sequence} {e.Message.Envelope.Sender} → {e.Message.Envelope.Recipient} ({e.Message.Envelope.CreatedAt:g}) · {(Fresh(e.Message.Envelope.SnapshotRef) ? "fresh" : "stale or unchecked")} · {e.Message.Content.Summary.Replace('\n', ' ')}");
        }
        var outstanding = document.Entries.Where(e => e.SenderSucceeded && e.Message.Envelope.Recipient is not null && e.Message.Envelope.Generation == task.Generation &&
            e.Message.Content.Type is "handoff" or "review_request" or "question" && e.Message.State is not (DeliveryState.Answered or DeliveryState.Canceled)).ToArray();
        if (outstanding.Length > 0)
        {
            text.AppendLine("\n## Peer requests not yet answered\n");
            foreach (var e in outstanding) text.AppendLine($"- #{e.Message.Envelope.Sequence} {e.Message.Content.Type} from {e.Message.Envelope.Sender} to {e.Message.Envelope.Recipient}: {e.Message.Content.Summary.Replace('\n', ' ')}");
        }
        var checks = document.SharedWork.Where(w => w.State is "completed" or "failed").ToArray();
        if (checks.Length > 0)
        {
            text.AppendLine("\n## Shared checks and discovery\n");
            foreach (var w in checks.TakeLast(24)) text.AppendLine($"- {w.Kind} by {ConversationTurns.Name(w.Owner)} · {w.State}{(w.Reusable ? " · reusable" : "")}: `{w.Operation.Replace('\n', ' ')}` — {w.Summary.Replace('\n', ' ')}");
        }
        text.AppendLine("\n## Evidence (host-captured native provider records)\n");
        if (document.Evidence.Count == 0) text.AppendLine("None captured.");
        foreach (var e in document.Evidence.TakeLast(30))
            text.AppendLine($"- `{e.Id}` {e.Provider} {e.Tool} at {e.StartedAt:g}: `{e.Command.Replace('\n', ' ')}` · exit {e.ExitCode?.ToString() ?? "unknown"}{(e.IsError == true ? " (error)" : "")} · {(e.Finished ? Fresh(e.SnapshotRef) && Fresh(e.StartSnapshotRef) ? "files unchanged since" : "files changed since, or freshness unchecked" : "unfinished")}");
        text.AppendLine("\n## Provenance\n");
        text.AppendLine($"- Messages: {document.Entries.Count} · findings: {document.Findings.Count} · evidence records: {document.Evidence.Count} (pruned {document.PrunedEvidence}, omitted {document.OmittedEvidence}) · events: {document.Events.Count} (evicted {document.EvictedEvents}) · archived conversation records: {document.ArchivedRecords}");
        text.AppendLine("- Findings, summaries and shared-check results are agent claims. Freshness compares saved snapshots with the current one; it certifies that files did not change, not that conclusions are right.");
        return text.ToString();
    }
    public static string History(CollaborationDocument document, CollaborationSnapshot? current = null, bool includeOutput = true)
    {
        var text = new StringBuilder("\n\nCollaboration history:\n");
        if (current is not null) text.AppendLine($"Snapshot checked {current.CapturedAt:g}; complete: {current.Complete}\nCoverage: {current.Coverage}\n{current.Limitation}\n");
        foreach (var entry in document.Entries.TakeLast(30)) text.AppendLine($"#{entry.Message.Envelope.Sequence} {entry.Message.Envelope.Sender} → {entry.Message.Envelope.Recipient?.ToString() ?? "Hub"} · {entry.Message.Content.Type} · {entry.Message.State}\n{entry.Message.Content.Summary}\n{entry.Reason}\n");
        foreach (var entry in document.Entries.Where(e => e.Message.Content.Type == "review_result").TakeLast(20))
        {
            var reference = entry.Message.Envelope.SnapshotRef;
            var fresh = current is not null && current.Complete && reference is not null && document.Snapshots.TryGetValue(reference, out var snapshot) && snapshot.Complete && snapshot.Fingerprint == current.Fingerprint;
            text.AppendLine($"Review #{entry.Message.Envelope.Sequence}: {(current is null ? "freshness not checked" : fresh && entry.SenderSucceeded ? "matches current snapshot" : "stale, incomplete, or uncommitted — new review required")}\n");
        }
        text.AppendLine("Findings (historical; a new review must check current files):");
        text.AppendLine($"Shared context: {document.ContextSections.Count} saved sections. Open Shared context to check freshness.");
        if (includeOutput) foreach (var section in document.ContextSections)
            text.AppendLine($"\n{section.Agent} · {section.CollectedAt:g} · {string.Join(", ", section.Scope.Files)}\n{section.Notes.Findings}\nSources: {string.Join(", ", section.Notes.Sources)}\nOpen questions: {string.Join("; ", section.Notes.OpenQuestions)}\n{section.Limitation}\nFile freshness must be rechecked in Shared context.");
        foreach (var f in document.Findings) text.AppendLine($"{f.Id}: {f.Disposition} · {f.File}:{f.Line} · {f.Explanation}");
        text.AppendLine("\nCaptured evidence (native provider records, not independent host reruns):");
        foreach (var e in document.Evidence.TakeLast(20)) text.AppendLine($"{e.Id} · {e.Provider} · {e.StartedAt:g}\n{e.Command}\nExit: {e.ExitCode?.ToString() ?? "unknown"}; finished: {e.Finished}; truncated: {e.Truncated}\n{(includeOutput ? e.Output : "Use Check evidence for captured output.")}\n");
        return text.ToString();
    }
}
