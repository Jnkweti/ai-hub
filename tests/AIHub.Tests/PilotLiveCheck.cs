using AIHub.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

// Phase 0/2 pilot: one unprescribed task through the real coordinator, wired like the desktop, for Both or a solo provider.
// Records what happened (turns, questions, resolutions, time, usage, replies); judging correctness is the developer's job.
internal static class PilotLiveCheck
{
    public static async Task Run(string output, string workspace, string target, string promptFile, string? preferencesFile = null)
    {
        output = Path.GetFullPath(output); workspace = Path.GetFullPath(workspace);
        if (Directory.Exists(output)) throw new IOException("Choose a fresh output directory.");
        if (target is not ("Both" or "Codex" or "Claude")) throw new ArgumentException("Target must be Both, Codex or Claude.");
        var prompt = File.ReadAllText(promptFile).Trim();
        var local = new LocalStore(Path.Combine(output, "data"));
        using var lease = AppInstanceLease.TryAcquire(local.DirectoryPath) ?? throw new IOException("Pilot profile is busy.");
        // 0.30.0: an optional preferences.json is copied into the profile, and the store supplies the relevant ones as the desktop does.
        if (preferencesFile is not null) File.Copy(Path.GetFullPath(preferencesFile), Path.Combine(local.DirectoryPath, PreferenceStore.FileName));
        var preferences = new PreferenceStore(local); var shadowLog = new ShadowStrategyLog(local); var feedbackStore = new FeedbackStore(local);
        var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory) { Preferences = task => preferences.Relevant(task.Workspace, task.Id) };
        var taskId = memory.Create("pilot-" + target, workspace, prompt);
        var started = Stopwatch.StartNew();
        var events = new ConcurrentQueue<object>(); var visible = new ConcurrentQueue<object>(); var pauses = new ConcurrentQueue<string>(); var states = new ConcurrentQueue<object>();
        var codexOptions = new AgentOptions(workspace, false); var claudeOptions = new AgentOptions(workspace, false);
        await using var hub = new HubCoordinator(agent => agent == Agent.Codex ? new CodexClient(codexOptions) : new ClaudeClient(claudeOptions))
        {
            AutoExchange = true, AllowEdits = false, TaskMemory = memory, TaskId = taskId, CollaborationStore = store,
            Strategy = HubCoordinator.ParseStrategy(Environment.GetEnvironmentVariable("AIHUB_PILOT_STRATEGY")), // "independent" selects the 0.31.0 strategy.
            StrategyShadow = (p, id, generation, executed) => // 0.32.0: the shadow policy's choice is recorded in the profile and the stream.
            {
                var context = StrategyAdvisor.Describe(p, id, generation, 2, feedbackStore.All()); var (suggested, reason) = StrategyAdvisor.Suggest(context);
                return ShadowStrategyLog.Describe(shadowLog.Record(context, suggested, reason, HubCoordinator.StrategySetting(executed)));
            },
            CollaborationBridgePath = CollaborationTests.Bridge, CollaborationWorkflowDirectory = Path.Combine(CollaborationTests.Root, "plugins", "ai-hub-collaboration"),
            CollaborationFactory = (agent, connection) => agent == Agent.Codex
                ? new CodexClient(codexOptions with { Collaboration = connection }) : new ClaudeClient(claudeOptions with { Collaboration = connection }),
            ContextResearchFactory = (agent, connection) => agent == Agent.Codex
                ? new CodexClient(codexOptions with { Collaboration = connection }) : new ClaudeClient(claudeOptions with { Collaboration = connection }),
            PreparationFactory = agent => agent == Agent.Codex
                ? new CodexClient(codexOptions with { PreparationOnly = true }) : new ClaudeClient(claudeOptions with { PreparationOnly = true }),
            RequestApproval = (_, _) => Task.FromResult(new Decision(false))
        };
        static string Clip(string text) => text.Length <= 2000 ? text : text[..2000] + " [clipped]";
        hub.Event += e =>
        {
            if (e.Kind is EventKind.Tool or EventKind.ToolOutput or EventKind.Usage or EventKind.Error or EventKind.Status or EventKind.Message)
                events.Enqueue(new { t = Math.Round(started.Elapsed.TotalSeconds, 1), agent = e.Agent.ToString(), kind = e.Kind.ToString(), text = Clip(e.Text), detail = Clip(e.Detail) });
            if (e.Kind == EventKind.Message) { visible.Enqueue(new { t = Math.Round(started.Elapsed.TotalSeconds, 1), agent = e.Agent.ToString(), e.Text }); Console.WriteLine($"MESSAGE {e.Agent} at {started.Elapsed:m\\:ss} ({e.Text.Length} chars)"); }
            if (e.Kind == EventKind.Error) Console.WriteLine("ERROR " + e.Text);
        };
        hub.State += s => { states.Enqueue(new { t = Math.Round(started.Elapsed.TotalSeconds, 1), state = s }); Console.WriteLine($"STATE {started.Elapsed:m\\:ss} {s}"); };
        hub.AutoPaused += reason => { pauses.Enqueue(reason); Console.WriteLine("PAUSED " + reason); };
        hub.StructuredMessage += m => Console.WriteLine($"TERMINAL {m.Envelope.Sender} {m.Content.Type}{(m.Content.Status is null ? "" : " " + m.Content.Status)}{(m.Envelope.Recipient is null ? "" : " -> " + m.Envelope.Recipient)}");
        Console.WriteLine($"START pilot target={target}");
        await hub.SubmitAsync(prompt, target);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
        {
            try { while (memory.Get(taskId)!.State == WorkState.Running) await Task.Delay(250, timeout.Token); }
            catch (OperationCanceledException) { await hub.StopAsync(); Console.WriteLine("TIMEOUT the pilot was stopped after 15 minutes"); }
        }
        started.Stop();
        var document = store.Read(taskId); var task = memory.Get(taskId)!;
        var summary = new
        {
            target, prompt, elapsedSeconds = Math.Round(started.Elapsed.TotalSeconds, 1), state = task.State.ToString(), task.Reason, pauses = pauses.ToArray(), interventions = 0,
            entries = document.Entries.Select(e => new { e.Message.Envelope.Sequence, sender = e.Message.Envelope.Sender.ToString(), recipient = e.Message.Envelope.Recipient?.ToString(),
                e.Message.Content.Type, e.Message.Content.Status, reply_to = e.Message.Content.ReplyTo, e.Message.Content.Summary, state = e.Message.State.ToString(), e.Message.Content.EvidenceRefs, e.Message.Content.Blockers }),
            questions = document.Entries.Count(e => e.Message.Content.Type == "question"),
            peerRequests = document.Entries.Count(e => e.Message.Envelope.Recipient is not null),
            resolutions = document.Assignments.Count(a => a.Role == "resolution"),
            assignments = document.Assignments.Select(a => new { agent = a.Agent.ToString(), a.Role, a.State, a.Dependencies }),
            stream = document.Events.Select(e => new { e.Sequence, e.Kind, e.Author, e.Text }),
            inputs = document.ContextInputs.Select(i => new { agent = i.Agent.ToString(), i.Outcome, i.InputBytes, i.NativeSession }),
            usage = CollaborationPresentation.UsageSummary(document, task.Generation),
            evidence = document.Evidence.Select(e => new { e.Id, e.Provider, e.Tool, e.Command, e.ExitCode }),
            states = states.ToArray(), visible = visible.ToArray()
        };
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "events.json"), JsonSerializer.Serialize(events.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "replies.md"), string.Join("\n\n---\n\n", visible.Select(v => JsonSerializer.Serialize(v))));
        Console.WriteLine($"DONE target={target} elapsed={started.Elapsed:m\\:ss} state={task.State} entries={document.Entries.Count} questions={summary.questions} resolutions={summary.resolutions}\n{summary.usage}\n{task.Reason}");
    }
}
