using AIHub.Core;
using System.Collections.Concurrent;
using System.Text.Json;

// Real providers: one contributes, the other asks it a question, the answer returns to the asker, and the asker records its decision.
internal static class ChallengeResolutionLiveCheck
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Choose a fresh output directory.");
        var workspace = Path.Combine(output, "project"); Directory.CreateDirectory(workspace);
        var local = new LocalStore(Path.Combine(output, "data"));
        using var lease = AppInstanceLease.TryAcquire(local.DirectoryPath) ?? throw new IOException("Live test profile is busy.");
        var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
        var marker = "CHALLENGE-" + Guid.NewGuid().ToString("N");
        var taskId = memory.Create("challenge-live", workspace, "Verify the question resolution loop. Required marker: " + marker);
        var sessions = new ConcurrentDictionary<Agent, string>(); var events = new ConcurrentQueue<object>(); var starts = new ConcurrentQueue<Agent>();
        await using var hub = new HubCoordinator(_ => throw new IOException("Structured workflow attempted legacy fallback."))
        {
            TaskMemory = memory, TaskId = taskId, CollaborationStore = store, CollaborationBridgePath = CollaborationTests.Bridge,
            AutoExchange = true, MaxAutoRounds = 2, AllowFollowUpContributions = false, // The resolution is the subject; voluntary reactions are covered by unit cases.
            CollaborationFactory = (agent, host) =>
            {
                starts.Enqueue(agent);
                var options = new AgentOptions(workspace, false) { Collaboration = host };
                return agent == Agent.Codex ? new CodexClient(options, sessions.GetValueOrDefault(agent)) : new ClaudeClient(options, sessions.GetValueOrDefault(agent));
            },
            RequestApproval = (_, _) => Task.FromResult(new Decision(false))
        };
        hub.Event += e =>
        {
            if (e.Kind == EventKind.Session && e.Text.Length > 0) sessions[e.Agent] = e.Text;
            if (e.Kind is EventKind.Tool or EventKind.ToolOutput or EventKind.Usage or EventKind.Error or EventKind.Message)
                events.Enqueue(new { agent = e.Agent.ToString(), kind = e.Kind.ToString(), e.Text, e.Detail });
            if (e.Kind == EventKind.Error) Console.WriteLine("ERROR " + e.Text);
        };
        hub.AutoPaused += reason => Console.WriteLine("PAUSED " + reason);
        var prompt = "Run this isolated four-turn communication test between Codex and Claude Code. Use only the ai_hub tools, with no file, shell, external or delegation tools. " +
            "On every submission set schema_version '1.0', a new idempotency_key, summary to the required marker, scope {files:[],focus:[]}, evidence_refs [] and blockers []. " +
            "Submit exactly one terminal message per turn, then end the turn with one short readable sentence for the user. Read get_task_context first and act on its state: " +
            "(1) no incoming_message, no answered_question and history_last_sequence 0: submit status assignment_complete claiming that local storage is the right choice. " +
            "(2) no incoming_message, no answered_question, but your teammate already contributed: submit a question to your teammate with recipient set to them and requested_action 'Why local storage rather than the shared store?'. " +
            "(3) incoming_message of type question: submit status assignment_complete with reply_to its message_id, answering that local storage survives restarts. " +
            "(4) answered_question present: submit status assignment_complete with reply_to the answer's message_id, stating whether the answer changes your view and why. " +
            "Do not request any further peer work and do not submit a second terminal message.";
        Console.WriteLine("START question resolution loop");
        await hub.SubmitAsync(prompt, "Both");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
        {
            try { while (memory.Get(taskId)!.State == WorkState.Running) await Task.Delay(100, timeout.Token); }
            catch { await hub.StopAsync(); throw; }
        }
        var document = store.Read(taskId); var entries = document.Entries;
        try
        {
            if (starts.Count != 2 || entries.Count != 4 || !entries.Select(e => e.Message.Content.Type).SequenceEqual(new[] { "status", "question", "status", "status" }) ||
                entries.Any(e => e.Message.Content.Summary != marker || !e.SenderSucceeded) || memory.Get(taskId)!.State != WorkState.Ready)
                throw new IOException("The real providers did not complete the question loop: " + memory.Get(taskId)!.Reason);
            var asker = entries[1].Message.Envelope.Sender;
            if (entries[1].Message.State != DeliveryState.Answered || entries[2].Message.Content.ReplyTo != entries[1].Message.Envelope.MessageId ||
                entries[3].Message.Envelope.Sender != asker || entries[3].Message.Content.ReplyTo != entries[2].Message.Envelope.MessageId)
                throw new IOException("The answer did not return to the asker, or the decision does not reply to the answer.");
            var resolution = document.Assignments.SingleOrDefault(a => a.Role == "resolution");
            if (resolution is null || resolution.Agent != asker || resolution.State != "completed" ||
                !resolution.Dependencies.SequenceEqual([entries[1].Message.Envelope.MessageId, entries[2].Message.Envelope.MessageId]))
                throw new IOException("The resolution assignment is missing or does not link the question and the answer.");
            var input = document.ContextInputs.Where(i => i.AssignmentId == resolution.Id).SingleOrDefault();
            if (input is null || input.Outcome != "responded" || !input.Prompt.Contains("RESOLUTION OF YOUR QUESTION") || !input.Prompt.Contains("ANSWER TO YOUR QUESTION"))
                throw new IOException("The asker's resolution turn did not receive the answer in its host input.");
            if (!document.Events.Any(e => e.Kind == "system" && e.Text.Contains("decides next") && e.Ref == entries[2].Message.Envelope.MessageId) ||
                document.Events.Last(e => e.Author == asker.ToString()).Kind != "agent_message")
                throw new IOException("The stream does not record the answer's return and the asker's decision.");
            Console.WriteLine($"PASS question loop: {ConversationTurns.Name(entries[0].Message.Envelope.Sender)} claimed, {ConversationTurns.Name(asker)} asked, the answer returned and the decision was recorded on resident sessions");
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "events.json"), JsonSerializer.Serialize(events.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { taskId, sessions, document }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
