using AIHub.Core;
using System.Text.Json;

internal static class SharedWorkLiveCheck
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output); if (Directory.Exists(output)) throw new IOException("Use a fresh profile.");
        var workspace = Path.Combine(output, "project"); Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "source.txt"), "Disposable shared-work fixture.");
        var local = new LocalStore(Path.Combine(output, "data")); var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
        var id = memory.Create("shared-work-live", workspace, "Verify cooperative native check reuse without changing any files.");
        var starts = new List<Agent>();
        var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
        await using var hub = new HubCoordinator(_ => throw new IOException("Legacy dispatch"))
        {
            TaskMemory = memory, TaskId = id, AllowEdits = true, AutoExchange = false, CollaborationStore = store,
            CollaborationBridgePath = Environment.GetEnvironmentVariable("AIHUB_TEST_BRIDGE") ?? CollaborationTests.Bridge,
            CollaborationFactory = (agent, host) =>
            {
                starts.Add(agent); var options = new AgentOptions(workspace, true) { Collaboration = host };
                return agent == Agent.Codex ? new CodexClient(options) : new ClaudeClient(options);
            },
            RequestApproval = (a, _) => Task.FromResult(new Decision(a.Agent == Agent.Codex && !a.IsQuestion && a.Title == "Approve command" && a.Detail.Contains("SHARED_CHECK_OK")))
        };
        hub.Event += e => { events.Enqueue(e); if (e.Kind == EventKind.Error) Console.WriteLine(e.Text); };
        var prompt = "Codex, start this isolated shared-work protocol verification. Do not edit files, inspect other directories, use network, delegate, or split research. " +
            "Codex: call claim_work kind=check, operation exactly Write-Output 'SHARED_CHECK_OK', scope files=['source.txt'] focus=['deterministic output'], reusable=true, independent=false. " +
            "Run that exact PowerShell command through your native shell only if claimed; it has no external dependencies and changes no files. Get its native evidence ID using get_evidence. " +
            "Call complete_work with the claimed ID, concise summary, and that evidence ID. Submit status assignment_complete, then briefly report the result. " +
            "Claude: call claim_work with exactly those same arguments. Expect disposition reused. Read its evidence using get_evidence, and submit status assignment_complete with the existing evidence ID. " +
            "Do not execute or independently repeat the command. Say explicitly whether the check was reused. Each agent supplies one terminal status, no handoffs. Omit reply_to entirely for both agents because neither has an incoming structured peer message. " +
            "If evidence reports a different native command string, Codex may claim that exact recorded string and complete it with the matching evidence; tell Claude the actual operation to reuse.";
        await hub.SubmitAsync(prompt, "Both"); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        while (memory.Get(id)!.State == WorkState.Running) await Task.Delay(100, timeout.Token);
        var doc = store.Read(id);
        File.WriteAllText(Path.Combine(output, "events.json"), JsonSerializer.Serialize(events.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { task = memory.Get(id), starts, doc.SharedWork, doc.Evidence }, new JsonSerializerOptions { WriteIndented = true }));
        if (memory.Get(id)!.State != WorkState.Ready || !starts.SequenceEqual([Agent.Codex, Agent.Claude]) ||
            doc.SharedWork.Count(w => w.State == "completed" && w.Reusable && w.Owner == Agent.Codex) != 1 ||
            doc.SharedWork.Any(w => w.Owner == Agent.Claude) || doc.Evidence.Any(e => e.Provider == Agent.Claude) ||
            !memory.Get(id)!.LatestReplies[Agent.Claude].Contains("reus", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Native shared-work reuse failed: " + memory.Get(id)!.Reason);
        Console.WriteLine("PASS Codex published a native successful check and Claude reused its result without repeating execution");
    }
}
