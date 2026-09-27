using AIHub.Core;
using System.Collections.Concurrent;
using System.Text.Json;

internal static class CollaborationWorkflowLiveCheck
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output); if (Directory.Exists(output)) throw new IOException("Use a fresh output folder"); Directory.CreateDirectory(output);
        var summaries = new List<object>();
        foreach (var author in new[] { Agent.Codex, Agent.Claude })
        {
            var workspace = Path.Combine(output, author.ToString(), "project"); Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "check.py"), "from calc import add\nassert add(2, 3) == 5\nassert add(-2, 3) == 1\nassert add(0, 0) == 0\nprint('ALL CHECKS PASSED')\n");
            var local = new LocalStore(Path.Combine(output, author.ToString(), "data")); var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
            var taskId = memory.Create("workflow-" + author, workspace, "Isolated implementation-review-fix fixture. Implement add(a,b) as mathematical addition and verify positive, negative, and zero inputs.");
            var sessions = new ConcurrentDictionary<Agent, string>(); var events = new ConcurrentQueue<AgentEvent>(); var starts = new List<Agent>();
            await using var hub = new HubCoordinator(_ => throw new IOException("Unexpected legacy dispatch"))
            {
                TaskMemory = memory, TaskId = taskId, AllowEdits = true, CollaborationStore = store,
                CollaborationBridgePath = Environment.GetEnvironmentVariable("AIHUB_TEST_BRIDGE") ?? CollaborationTests.Bridge,
                CollaborationWorkflowDirectory = Path.Combine(CollaborationTests.Root, "plugins", "ai-hub-collaboration"), MaxAutoRounds = 3,
                CollaborationFactory = (agent, host) =>
                {
                    starts.Add(agent); var options = new AgentOptions(workspace, true) { Collaboration = host };
                    return agent == Agent.Codex ? new CodexClient(options, sessions.GetValueOrDefault(agent)) : new ClaudeClient(options, sessions.GetValueOrDefault(agent));
                },
                RequestApproval = (approval, _) =>
                {
                    var allowed = !approval.IsQuestion && approval.Title != "Approve requested permissions" &&
                        (approval.Detail.Contains("calc.py") || approval.Detail.Contains("check.py") || approval.Title == "Approve Read");
                    Console.WriteLine("APPROVAL " + approval.Agent + " " + approval.Title + " " + allowed);
                    return Task.FromResult(new Decision(allowed));
                }
            };
            hub.Event += e => { events.Enqueue(e); if (e.Kind == EventKind.Session) sessions[e.Agent] = e.Text; if (e.Kind == EventKind.Error) Console.WriteLine("ERROR " + e.Text); };
            hub.StructuredMessage += m => Console.WriteLine("MESSAGE " + m.Envelope.Sender + " " + m.Content.Type + " " + m.Content.Status);
            hub.AutoPaused += reason => Console.WriteLine("PAUSED " + reason);
            var prompt = author + ", start this isolated integration test. Both agents must use the AI Hub structured tools. " +
                "The only authorized files are calc.py and existing check.py in this disposable workspace. Do not change check.py, use network, install packages, delegate, or touch other directories. " +
                "Fixture sequence: (1) The first author creates calc.py with exactly def add(a, b): followed by an indented return abs(a) + b. " +
                "This intentional fixture bug tests the reviewer; do not fix it before the first review. Submit review_request to the peer with scope files ['calc.py'] and focus ['Check mathematical addition for negative and zero inputs']. " +
                "(2) The peer reads calc.py and check.py, optionally runs python check.py, and submits review_result with one open finding ID add-negative for calc.py line 2 and the exact same scope. " +
                "(3) The author fixes calc.py to return a + b, runs python check.py, calls mark_addressed on add-negative, reads get_evidence, then submits another review_request with the same scope and relevant evidence IDs. " +
                "(4) The peer independently reads the fixed calc.py and runs python check.py if available, calls get_evidence, then returns review_result with finding add-negative disposition checked and fresh evidence references. " +
                "Claude may cite its captured Read evidence with a missing shell exit status left unknown. (5) The author submits status assignment_complete, reply_to the review message. " +
                "Read get_task_context each turn. Every response includes the current incoming message ID as reply_to. Reuse the exact review scope. End each dispatch after one accepted terminal message. " +
                "For the first dispatch omit reply_to. Native Python is already installed. Briefly describe the observed result after each submission.";
            Console.WriteLine("START implement/review/fix author " + author);
            try
            {
                await hub.SubmitAsync(prompt, "Both"); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                while (memory.Get(taskId)!.State == WorkState.Running) await Task.Delay(100, timeout.Token);
                var doc = store.Read(taskId);
                if (memory.Get(taskId)!.State != WorkState.Ready || doc.Findings.SingleOrDefault()?.Disposition != "checked" ||
                    starts[0] != author || doc.Entries.Count != 5 || !doc.Entries.All(e => e.SenderSucceeded) || doc.Evidence.Count < 2 ||
                    !File.ReadAllText(Path.Combine(workspace, "calc.py")).Contains("a + b"))
                    throw new IOException("Native workflow did not finish: " + memory.Get(taskId)!.Reason);
                summaries.Add(new { author = author.ToString(), taskId, starts, evidence = doc.Evidence.Count, findings = doc.Findings, messages = doc.Entries.Count });
                Console.WriteLine("PASS implementation, open finding, author fix, peer check, completion: " + author);
            }
            finally
            {
                await hub.StopAsync();
                File.WriteAllText(Path.Combine(output, "events-" + author + ".json"), JsonSerializer.Serialize(events.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
}
