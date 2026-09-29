using AIHub.Core;
using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>Real providers: reaction rounds with a user message that joins the phase while an agent is working.</summary>
internal static class LiveStreamLiveCheck
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output); if (Directory.Exists(output)) throw new IOException("Use a fresh profile.");
        var workspace = Path.Combine(output, "project"); Directory.CreateDirectory(workspace);
        var local = new LocalStore(Path.Combine(output, "data")); var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
        var id = memory.Create("live-stream", workspace, "Discuss a small offline-first notes app; no implementation.");
        var history = new List<ConversationEntry>(); var sessions = new ConcurrentDictionary<Agent, string>();
        var starts = new List<Agent>(); var messages = new List<(Agent Agent, string Text)>(); var diagnostics = new List<AuditCode>();
        var firstMessage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubCoordinator(_ => throw new IOException("Legacy routing used"))
        {
            TaskMemory = memory, TaskId = id, CollaborationStore = store, AutoExchange = true, MaxAutoRounds = 2,
            CollaborationBridgePath = Environment.GetEnvironmentVariable("AIHUB_TEST_BRIDGE") ?? CollaborationTests.Bridge,
            CollaborationWorkflowDirectory = Path.Combine(CollaborationTests.Root, "plugins", "ai-hub-collaboration"),
            ReadConversation = _ => { lock (history) return Task.FromResult<IReadOnlyList<ConversationEntry>>(history.ToArray()); },
            CollaborationFactory = (agent, host) =>
            {
                lock (starts) starts.Add(agent); var options = new AgentOptions(workspace, false) { Collaboration = host };
                return agent == Agent.Codex ? new CodexClient(options, sessions.GetValueOrDefault(agent)) : new ClaudeClient(options, sessions.GetValueOrDefault(agent));
            },
            RequestApproval = (_, _) => Task.FromResult(new Decision(false))
        };
        hub.Diagnostic += (code, _) => { lock (diagnostics) diagnostics.Add(code); };
        hub.Event += e =>
        {
            if (e.Kind == EventKind.Session) sessions[e.Agent] = e.Text;
            if (e.Kind == EventKind.Message)
            {
                lock (history) history.Add(new(Guid.NewGuid().ToString("N"), e.Agent.ToString(), e.Text));
                lock (messages) messages.Add((e.Agent, e.Text)); firstMessage.TrySetResult();
            }
            if (e.Kind == EventKind.Error) Console.WriteLine("ERROR " + e.Text);
        };
        hub.State += s => Console.WriteLine("STATE " + s);
        var prompt = "Discuss how a very small offline-first notes app should store its notes: plain files or SQLite. Keep it to a short discussion, at most two short paragraphs each; " +
            "do not inspect files, run commands, delegate, or implement anything. React to your teammate's specific points or pass quietly when you have nothing to add.";
        lock (history) history.Add(new(Guid.NewGuid().ToString("N"), "You", prompt, "Both"));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await hub.SubmitAsync(prompt, "Both");
        using (var wait = new CancellationTokenSource(TimeSpan.FromMinutes(3))) await firstMessage.Task.WaitAsync(wait.Token);
        var aside = "One more constraint from me: notes must survive a crash mid-write. Mention in one sentence how your preferred option handles that.";
        lock (history) history.Add(new(Guid.NewGuid().ToString("N"), "You", aside, "Both"));
        var generation = memory.Get(id)!.Generation;
        if (!await hub.InterjectAsync(aside)) throw new IOException("The running phase refused the user's message.");
        Console.WriteLine("INTERJECTED after the first contribution");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6)))
            while (memory.Get(id)!.State == WorkState.Running) await Task.Delay(200, timeout.Token);
        var task = memory.Get(id)!; var doc = store.Read(id);
        if (task.Generation != generation || task.State is not (WorkState.Ready or WorkState.Paused)) throw new IOException("The interjection restarted or broke the phase: " + task.Reason);
        var events = doc.Events.Where(e => e.Generation == generation).ToArray();
        var asideEvent = events.FirstOrDefault(e => e.Kind == "user_message" && e.Text == aside) ?? throw new IOException("The user's message did not enter the stream.");
        // Resident sessions receive the aside once: in each agent's first turn after it (a full core or a delta). Later
        // deltas carry only newer events, so only the first turn per agent must contain it.
        var afterAside = doc.ContextInputs.Where(i => i.Generation == generation && i.Outcome == "responded" && i.PreparedAt > asideEvent.Time).ToArray();
        if (afterAside.Length == 0) throw new IOException("No turn ran after the interjection.");
        foreach (var participant in afterAside.Select(i => i.Agent).Distinct())
        {
            var firstAfter = afterAside.Where(i => i.Agent == participant).OrderBy(i => i.PreparedAt).First();
            if (!firstAfter.Prompt.Contains(aside)) throw new IOException($"{participant}'s first turn after the interjection did not receive the user's message.");
        }
        if (starts.Count != 2 || messages.Count < 2 || messages.Select(m => m.Agent).Distinct().Count() != 2) throw new IOException("Both agents did not contribute on resident sessions: " + string.Join(",", starts));
        var contributions = events.Count(e => e.Kind == "agent_message"); var passes = events.Count(e => e.Kind == "agent_pass");
        if (contributions < 2) throw new IOException("Reaction rounds produced fewer than two contributions.");
        if (!task.Reason.StartsWith("Every participant passed") && task.State != WorkState.Paused) throw new IOException("Unexpected end of phase: " + task.Reason);
        if (Directory.EnumerateFileSystemEntries(workspace).Any()) throw new IOException("Discussion changed project files.");
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            prompt, aside, elapsedSeconds = timer.Elapsed.TotalSeconds, providerStarts = starts.Select(a => a.ToString()), contributions, passes,
            outcome = task.State.ToString(), reason = task.Reason, diagnostics = diagnostics.Select(d => d.ToString()),
            stream = events.Select(e => new { e.Sequence, e.Kind, e.Author, text = e.Text.Length > 300 ? e.Text[..300] + "…" : e.Text }),
            inputs = doc.ContextInputs.Where(i => i.Generation == generation).Select(i => new { i.Agent, i.Outcome, i.InputBytes, i.NativeSession, delta = i.Prompt.Contains("NEW EVENTS SINCE YOUR LAST TURN"), carriesAside = i.Prompt.Contains(aside) }),
            messages = messages.Select(m => new { agent = m.Agent.ToString(), m.Text })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS reaction rounds with a mid-phase user message: {contributions} contributions, {passes} passes, {starts.Count} provider starts, outcome {task.State}: {task.Reason}");
    }
}
