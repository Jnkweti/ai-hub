using AIHub.Core;
using System.Text.Json;

internal static class ContextSourceLiveCheck
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output); if (Directory.Exists(output)) throw new IOException("Use a fresh fixture directory.");
        var workspace = Path.Combine(output, "project"); Directory.CreateDirectory(workspace);
        var local = new LocalStore(Path.Combine(output, "data")); var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
        var id = memory.Create("source-live", workspace, "Read two separate sections of one shared long transcript.");
        var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
        var request = "Codex and Claude: isolated shared-source retrieval verification. No shell, file edits, network, delegation or extra research. " +
            "Use read_context_source to search this full saved message. Codex: find ALPHA and report its code. Claude: find BETA and report its different code; do not repeat Codex. " +
            "Each must call read_context_source with query ALPHA or BETA, start 0, length 8000, and the saved source ID in common context. " +
            "Call submit_message status assignment_complete with no reply_to, then give your short unique finding. The codes are inside this transcript, not its preview.\n" +
            new string('a', 140000) + "\nALPHA: ORCHID-714\n" + new string('b', 70000) + "\nBETA: COBALT-926\n" + new string('c', 70000);
        await using var hub = new HubCoordinator(_ => throw new IOException("Legacy provider"))
        {
            TaskMemory = memory, TaskId = id, CollaborationStore = store, AutoExchange = false,
            CollaborationBridgePath = Environment.GetEnvironmentVariable("AIHUB_TEST_BRIDGE") ?? CollaborationTests.Bridge,
            CollaborationFactory = (agent, host) => { var options = new AgentOptions(workspace, false) { Collaboration = host }; return agent == Agent.Codex ? new CodexClient(options) : new ClaudeClient(options); },
            RequestApproval = (_, _) => Task.FromResult(new Decision(false))
        };
        hub.Event += e => { events.Enqueue(e); if (e.Kind == EventKind.Error) Console.WriteLine(e.Text); };
        await hub.SubmitAsync(request, "Both"); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        while (memory.Get(id)!.State == WorkState.Running) await Task.Delay(100, timeout.Token);
        var task = memory.Get(id)!; var doc = store.Read(id);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { task.State, task.Reason, replies = task.LatestReplies, inputs = doc.ContextInputs.Select(i => new { i.Agent, i.InputBytes, i.PartialIds }), sources = doc.ContextRecords.Where(r => r.SourceStored).Select(r => new { r.Id, r.OriginalHash, r.OriginalCharacters }) }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "events.json"), JsonSerializer.Serialize(events.ToArray()));
        if (task.State != WorkState.Ready || !task.LatestReplies.GetValueOrDefault(Agent.Codex, "").Contains("ORCHID-714") || !task.LatestReplies.GetValueOrDefault(Agent.Claude, "").Contains("COBALT-926") || doc.ContextInputs.Any(i => i.InputBytes > 60000))
            throw new IOException("Live source retrieval failed: " + task.Reason);
        Console.WriteLine("PASS both native providers retrieved different interior sections of the same 280k-character saved source through the packaged bridge");
    }
}
