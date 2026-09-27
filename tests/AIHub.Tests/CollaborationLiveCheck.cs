using AIHub.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CollaborationLiveCheck
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Choose a fresh output directory for the isolated probe.");
        Directory.CreateDirectory(output);
        var workspace = Path.Combine(output, "project"); Directory.CreateDirectory(workspace);
        var store = new LocalStore(Path.Combine(output, "data"));
        using var lease = AppInstanceLease.TryAcquire(store.DirectoryPath) ?? throw new IOException("Probe profile is busy.");
        var memory = new TaskMemory(store);
        var results = new List<object>();
        try
        {
            foreach (var allowEdits in new[] { false, true })
            {
                var marker = "CONTEXT-" + Guid.NewGuid().ToString("N");
                var task = memory.Create("probe-room-" + allowEdits, workspace, "Validate communication tools. Required summary marker: " + marker);
                var claim = memory.Begin(task, allowEdits);
                var probe = new CollaborationProbe(memory, claim);
                try
                {
                    foreach (var agent in new[] { Agent.Codex, Agent.Claude })
                    {
                        string? nativeSession = null;
                        foreach (var resumed in allowEdits ? new[] { false } : new[] { false, true })
                        {
                            memory.Own(claim, agent);
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                            await using var host = new CollaborationMcpHost(probe, agent, CollaborationTests.Bridge, timeout.Token);
                            var options = new AgentOptions(workspace, allowEdits) { Collaboration = host };
                            await using IAgentClient client = agent == Agent.Codex ? new CodexClient(options, nativeSession) : new ClaudeClient(options, nativeSession);
                            var events = new List<object>();
                            client.RequestApproval = (_, _) => Task.FromResult(new Decision(false));
                            client.Event += e =>
                            {
                                if (e.Kind is EventKind.Tool or EventKind.ToolOutput or EventKind.Error or EventKind.Usage)
                                { lock (events) events.Add(new { kind = e.Kind.ToString(), e.Text, e.Detail }); }
                                if (e.Kind is EventKind.Error) Console.WriteLine(agent + " ERROR " + e.Text);
                            };
                            var key = Guid.NewGuid().ToString("N");
                            var before = probe.ContextReads;
                            var prompt = "AI Hub isolated communication integration test. Use only the ai_hub MCP tools; do not read or edit files, run shell commands, delegate, or use other tools. " +
                                "First call get_task_context with {}. Then call submit_message exactly once with schema_version '1.0', idempotency_key '" + key + "', " +
                                "and content {type:'status', summary: the exact required summary marker from the task objective, scope:{files:[],focus:[]}, evidence_refs:[], blockers:[], status:'assignment_complete'}. " +
                                "Use valid JSON tool arguments. Do not include sender, task ID, recipient, reply_to or requested_action. " +
                                "After the tool returns an accepted receipt, reply briefly with the receipt's message_id. If the tool fails, report the error; do not pretend it succeeded.";
                            Console.WriteLine($"START {agent} {(allowEdits ? "editing" : "read-only")} {(resumed ? "resumed" : "fresh")}");
                            AgentReply reply;
                            try { reply = await client.SendAsync(prompt, timeout.Token); }
                            catch
                            {
                                lock (events) File.WriteAllText(Path.Combine(output, $"failure-{agent}-{allowEdits}-{resumed}.json"), JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
                                throw;
                            }
                            var received = probe.Messages.LastOrDefault(m => m.Envelope.DispatchId == host.DispatchId);
                            if (probe.ContextReads <= before || received is null || received.Content.Summary != marker || received.Envelope.Sender != agent ||
                                received.Envelope.TaskId != task || received.Envelope.ProviderSessionId != client.SessionId)
                                throw new Exception(agent + " did not complete the host-observed tool exchange.");
                            if (resumed && client.SessionId != nativeSession) throw new Exception(agent + " did not resume the same native session.");
                            nativeSession = client.SessionId;
                            results.Add(new { agent = agent.ToString(), allowEdits, resumed, nativeSession, received.Envelope,
                                received.Content, reply = reply.Text, contextReads = probe.ContextReads - before, events = events.ToArray() });
                            await host.DisposeAsync();
                            Console.WriteLine($"PASS {agent} {(allowEdits ? "editing" : "read-only")} {(resumed ? "resumed" : "fresh")}: context read and accepted message observed by host");
                        }
                    }
                }
                finally { memory.End(claim, WorkState.Stopped, "Connection probe finished; no peer dispatch performed."); }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (Directory.EnumerateFileSystemEntries(workspace).Any()) throw new Exception("A provider unexpectedly wrote into the probe workspace.");
        Console.WriteLine("PASS probe workspace stayed empty; results saved to " + output);
    }
}
