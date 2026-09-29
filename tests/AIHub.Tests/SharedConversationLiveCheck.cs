using AIHub.Core;
using System.Collections.Concurrent;
using System.Text.Json;

internal static class SharedConversationLiveCheck
{
    public static async Task Run(string output, bool conciseOnly = false)
    {
        output = Path.GetFullPath(output); if (Directory.Exists(output)) throw new IOException("Use a fresh profile.");
        var workspace = Path.Combine(output, "project"); Directory.CreateDirectory(workspace);
        var local = new LocalStore(Path.Combine(output, "data")); var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
        var id = memory.Create("shared-discussion", workspace, "Discuss an offline-first personal notes app; no implementation.");
        var history = new List<ConversationEntry>(); var sessions = new ConcurrentDictionary<Agent, string>();
        var starts = new List<Agent>(); var replies = new List<object>();
        await using var hub = new HubCoordinator(_ => throw new IOException("Legacy routing used"))
        {
            TaskMemory = memory, TaskId = id, CollaborationStore = store, AutoExchange = false,
            CollaborationBridgePath = Environment.GetEnvironmentVariable("AIHUB_TEST_BRIDGE") ?? CollaborationTests.Bridge,
            CollaborationWorkflowDirectory = Path.Combine(CollaborationTests.Root, "plugins", "ai-hub-collaboration"),
            ReadConversation = _ => { lock (history) return Task.FromResult<IReadOnlyList<ConversationEntry>>(history.ToArray()); },
            CollaborationFactory = (agent, host) =>
            {
                starts.Add(agent); var options = new AgentOptions(workspace, false) { Collaboration = host };
                return agent == Agent.Codex ? new CodexClient(options, sessions.GetValueOrDefault(agent)) : new ClaudeClient(options, sessions.GetValueOrDefault(agent));
            },
            PreparationFactory = agent => agent == Agent.Codex
                ? new CodexClient(new AgentOptions(workspace, false) { PreparationOnly = true })
                : new ClaudeClient(new AgentOptions(workspace, false) { PreparationOnly = true }),
            RequestApproval = (_, _) => Task.FromResult(new Decision(false))
        };
        hub.Event += e =>
        {
            if (e.Kind == EventKind.Session) sessions[e.Agent] = e.Text;
            if (e.Kind == EventKind.Message) lock (history) history.Add(new(Guid.NewGuid().ToString("N"), e.Agent.ToString(), e.Text));
            if (e.Kind == EventKind.Error) Console.WriteLine("ERROR " + e.Text);
        };
        hub.StructuredMessage += m => Console.WriteLine(m.Envelope.Sender + " " + m.Content.Type);
        foreach (var prompt in conciseOnly ? [] : new[] {
            "I'm thinking about a very small offline-first notes app. I want something pleasant to use, not a big productivity system. What do you think I should start with? Keep this to a short discussion; don't inspect files, run commands, delegate, or implement anything.",
            "I'd actually rather skip folders and tags at first. How would that change your suggestion? Keep this conversational and brief; no implementation or workspace inspection."
        })
        {
            lock (history) history.Add(new(Guid.NewGuid().ToString("N"), "You", prompt, "Both"));
            var before = starts.Count; var oldSessions = sessions.ToDictionary(); var timer = System.Diagnostics.Stopwatch.StartNew(); await hub.SubmitAsync(prompt, "Both");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            while (memory.Get(id)!.State == WorkState.Running) await Task.Delay(100, timeout.Token);
            if (starts.Count - before != 2 || !starts.Skip(before).ToHashSet().SetEquals([Agent.Codex, Agent.Claude]))
                throw new IOException("Both did not contribute: " + memory.Get(id)!.Reason);
            var reported = memory.Get(id)!.LatestReplies;
            if (reported.Values.Any(text => new[] { "Task complete.", "Passing to Codex.", "Passing to Claude Code.", "No further contribution." }.Any(marker => text.TrimEnd().EndsWith(marker, StringComparison.OrdinalIgnoreCase))))
                throw new IOException("Legacy control sign-off leaked into ordinary discussion.");
            var inputs = store.Read(id).ContextInputs.Where(i => i.Generation == memory.Get(id)!.Generation).ToArray();
            var doc = store.Read(id);
            if (replies.Count == 0)
            {
                // First phase: fresh sessions, a lead turn, and the peer prepared concurrently then reconciled in its preparation session.
                if (inputs.Length != 3 || inputs.Any(i => !i.Prompt.Contains("offline-first") || i.Outcome != "responded")) throw new IOException("First phase lost original constraints or preparation/input evidence");
                var prepAssignment = doc.Assignments.Single(a => a.Generation == memory.Get(id)!.Generation && a.Role == "preparation");
                var prepInput = inputs.Single(i => i.DispatchId == prepAssignment.Id);
                var leadInput = inputs.Single(i => i.Agent != prepAssignment.Agent);
                var followInput = inputs.Single(i => i.Agent == prepAssignment.Agent && i.DispatchId != prepAssignment.Id);
                if (prepAssignment.State != "completed" || prepInput.CommonHash != leadInput.CommonHash || prepInput.NativeSession != followInput.NativeSession ||
                    !followInput.Prompt.Contains("PRECEDING AGENT RESPONSE")) throw new IOException("Native preparation did not reconcile in the same session");
            }
            else
            {
                // Second phase (0.26.0 session carry): both native sessions are resumed, no preparation runs, and each first turn is a delta prompt carrying the new message.
                foreach (var pair in oldSessions) if (sessions.GetValueOrDefault(pair.Key) != pair.Value) throw new IOException("Second phase did not resume the native session for " + pair.Key);
                if (inputs.Length != 2 || inputs.Any(i => i.Outcome != "responded" || !i.Prompt.Contains("NEW EVENTS SINCE YOUR LAST TURN") || i.Prompt.Contains("AI HUB COMMON TASK CONTEXT") || !i.Prompt.Contains("skip folders and tags")) ||
                    doc.Assignments.Any(a => a.Generation == memory.Get(id)!.Generation && a.Role == "preparation"))
                    throw new IOException("Second phase did not carry both sessions with delta prompts and no preparation: " + JsonSerializer.Serialize(inputs.Select(i => new { i.Agent, i.Outcome, i.InputBytes })));
            }
            replies.Add(new { prompt, speakers = starts.Skip(before).Select(a => a.ToString()).ToArray(), reported, elapsedSeconds = timer.Elapsed.TotalSeconds, providerCalls = inputs.Length, hostInputBytes = inputs.Sum(i => i.InputBytes), sessions = sessions.ToDictionary() });
            Console.WriteLine("PASS both respond to an unaddressed user message: " + string.Join(", ", starts.Skip(before)));
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(replies, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!conciseOnly && !starts.SequenceEqual([Agent.Codex, Agent.Claude, Agent.Claude, Agent.Codex])) throw new IOException("Shared conversation did not rotate its first speaker.");
        var concise = "What is 2 + 2? Answer with only the numeral. Do not inspect files, use execution tools, or add explanations.";
        int historyStart; lock (history) { historyStart = history.Count; history.Add(new(Guid.NewGuid().ToString("N"), "You", concise, "Both")); }
        var callsBefore = starts.Count; await hub.SubmitAsync(concise, "Both");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
            while (memory.Get(id)!.State == WorkState.Running) await Task.Delay(100, timeout.Token);
        ConversationEntry[] visible; lock (history) visible = history.Skip(historyStart).Where(m => m.Speaker != "You").ToArray();
        if (starts.Count - callsBefore != 1 || visible.Length != 1 || visible[0].Text.Trim() != "4")
            throw new IOException("A complete short answer produced redundant visible peer messages: " + JsonSerializer.Serialize(visible));
        replies.Add(new { prompt = concise, visible, reported = memory.Get(id)!.LatestReplies });
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(replies, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS simple answer appears once without a redundant peer dispatch");
        if (Directory.EnumerateFileSystemEntries(workspace).Any()) throw new IOException("Discussion changed project files.");
        Console.WriteLine(conciseOnly ? "PASS unchanged workspace" : "PASS lead rotation, shared follow-up context, session carry across phases, and unchanged workspace");
    }
}
