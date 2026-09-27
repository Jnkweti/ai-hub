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
