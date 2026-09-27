using AIHub.Core;
using System.Collections.Concurrent;
using System.Text.Json;

internal static class SharedContextLiveCheck
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output); if (Directory.Exists(output)) throw new IOException("Use a fresh profile.");
        var project = Path.Combine(output, "project"); SharedContextTests.Files(project);
        var before = await ProjectSnapshot.CaptureAsync(project, default);
        var local = new LocalStore(Path.Combine(output, "data")); var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
        var id = memory.Create("context-live", project, "Investigate why saving notes fails, without changing files.");
        var sessions = new ConcurrentDictionary<Agent, string>();
        var intervals = new ConcurrentDictionary<Agent, (DateTimeOffset Start, DateTimeOffset End)>();
        var events = new ConcurrentQueue<AgentEvent>();
        var mainStarts = new List<Agent>();
        await using var hub = new HubCoordinator(_ => throw new IOException("Legacy routing used"))
        {
            TaskMemory = memory, TaskId = id, CollaborationStore = store, AutoExchange = false,
            CollaborationBridgePath = Environment.GetEnvironmentVariable("AIHUB_TEST_BRIDGE") ?? CollaborationTests.Bridge,
            CollaborationFactory = (agent, host) =>
            {
                mainStarts.Add(agent); var options = new AgentOptions(project, false) { Collaboration = host };
                return agent == Agent.Codex ? new CodexClient(options, sessions.GetValueOrDefault(agent)) : new ClaudeClient(options, sessions.GetValueOrDefault(agent));
            },
            ContextResearchFactory = (agent, host) =>
            {
                var options = new AgentOptions(project, false) { Collaboration = host };
                IAgentClient client = agent == Agent.Codex ? new CodexClient(options) : new ClaudeClient(options);
                return new Tracked(client, (start, end) => intervals[agent] = (start, end));
            },
            PreparationFactory = agent => agent == Agent.Codex
                ? new CodexClient(new AgentOptions(project, false) { PreparationOnly = true })
                : new ClaudeClient(new AgentOptions(project, false) { PreparationOnly = true }),
            RequestApproval = (_, _) => Task.FromResult(new Decision(false))
        };
        hub.Event += e => { events.Enqueue(e); if (e.Kind == EventKind.Session) sessions[e.Agent] = e.Text; if (e.Kind == EventKind.Error) Console.WriteLine(e.Text); };
        hub.State += Console.WriteLine;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await hub.SubmitAsync("Investigate why saving a note fails. The fixture has frontend and backend directories. Split the initial context gathering between both agents in parallel, save the findings in shared task context, then explain the mismatch using both findings. Keep the answer brief and do not change files or run tests.", "Both");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        while (memory.Get(id)!.State == WorkState.Running) await Task.Delay(100, timeout.Token);
        var doc = store.Read(id); var after = await ProjectSnapshot.CaptureAsync(project, default);
        var elapsedSeconds = timer.Elapsed.TotalSeconds;
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new {
            task = memory.Get(id), mainStarts = mainStarts.Select(a => a.ToString()),
            researchers = intervals.Select(p => new { agent = p.Key.ToString(), p.Value.Start, p.Value.End }),
            sections = doc.ContextSections, events = events.ToArray(), filesUnchanged = before.Fingerprint == after.Fingerprint,
            elapsedSeconds, providerCalls = doc.ContextInputs.Count, hostInputBytes = doc.ContextInputs.Sum(i => i.InputBytes)
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (memory.Get(id)!.State != WorkState.Ready || doc.ContextSections.Count != 2 || intervals.Count != 2)
            throw new IOException("Native split context failed: " + memory.Get(id)!.Reason);
        if (mainStarts.Count != 2) throw new IOException("Research scheduled an unnecessary general peer turn.");
        var times = intervals.Values.ToArray();
        if (times.Max(t => t.Start) >= times.Min(t => t.End)) throw new IOException("Native researchers did not overlap");
        if (before.Fingerprint != after.Fingerprint || doc.ContextSections.Any(s => CollaborationStore.ContextFreshness(s, after) != "Current files"))
            throw new IOException("Read-only context changed files or is not fresh");
        var reports = string.Join("\n", memory.Get(id)!.LatestReplies.Values);
        var researchIds = doc.Assignments.Where(a => a.Role == "research").Select(a => a.Id).ToHashSet();
        var researchInputs = doc.ContextInputs.Where(i => researchIds.Contains(i.AssignmentId!)).ToArray();
        if (researchInputs.Select(i => i.CommonHash).Distinct().Count() != 1) throw new IOException("Research common snapshots differed");
        var synthesis = doc.ContextInputs.First(i => !researchIds.Contains(i.AssignmentId!) && i.PreparedAt > intervals.Values.Max(t => t.End));
        if (!synthesis.Prompt.Contains("submit-note") || !synthesis.Prompt.Contains("save-note")) throw new IOException("Main continuation lacked automatic shared findings");
        if (!reports.Contains("submit-note") || !reports.Contains("save-note")) throw new IOException("Main conversation did not combine both findings");
        File.WriteAllText(Path.Combine(output, "shared-context.txt"), await store.ContextReportAsync(id, default));
        Console.WriteLine("PASS both native researchers overlapped, published durable findings, resumed with both facts, and left workspace unchanged");
        await hub.DisposeAsync();
        var soloId = memory.Create("single-comparator", project, "Investigate why saving notes fails without changes.");
        await using var solo = new HubCoordinator(_ => throw new IOException("Legacy routing"))
        {
            TaskMemory = memory, TaskId = soloId, CollaborationStore = store, AutoExchange = false,
            CollaborationBridgePath = Environment.GetEnvironmentVariable("AIHUB_TEST_BRIDGE") ?? CollaborationTests.Bridge,
            CollaborationFactory = (agent, host) => new CodexClient(new AgentOptions(project, false) { Collaboration = host }),
            RequestApproval = (_, _) => Task.FromResult(new Decision(false))
        };
        timer.Restart();
        await solo.SubmitAsync("Investigate why saving a note fails. Read frontend and backend files and briefly explain the mismatch. Do not edit files or run tests.", "Codex");
        while (memory.Get(soloId)!.State == WorkState.Running) await Task.Delay(100, timeout.Token);
        var soloDoc = store.Read(soloId); var soloReply = memory.Get(soloId)!.LatestReplies.GetValueOrDefault(Agent.Codex) ?? "";
        if (memory.Get(soloId)!.State != WorkState.Ready || !soloReply.Contains("submit-note") || !soloReply.Contains("save-note")) throw new IOException("Single-agent comparator failed");
        if ((await ProjectSnapshot.CaptureAsync(project, default)).Fingerprint != before.Fingerprint) throw new IOException("Comparator changed files");
        File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(new {
            collaborative = new { elapsedSeconds, calls = doc.ContextInputs.Count, hostInputBytes = doc.ContextInputs.Sum(i => i.InputBytes) },
            singleAgent = new { elapsedSeconds = timer.Elapsed.TotalSeconds, calls = soloDoc.ContextInputs.Count, hostInputBytes = soloDoc.ContextInputs.Sum(i => i.InputBytes) },
            limitation = "One small fixture; elapsed time includes provider/network variability. Host bytes exclude native instructions, tools, and history."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS single-agent comparison recorded elapsed time, calls, host input bytes and unchanged files");
    }
    private sealed class Tracked(IAgentClient inner, Action<DateTimeOffset, DateTimeOffset> record) : IAgentClient
    {
        public Agent Agent => inner.Agent;
        public string? SessionId => inner.SessionId;
        public event Action<AgentEvent>? Event { add => inner.Event += value; remove => inner.Event -= value; }
        public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get => inner.RequestApproval; set => inner.RequestApproval = value; }
        public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
        { var start = DateTimeOffset.UtcNow; try { return await inner.SendAsync(prompt, token); } finally { record(start, DateTimeOffset.UtcNow); } }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
