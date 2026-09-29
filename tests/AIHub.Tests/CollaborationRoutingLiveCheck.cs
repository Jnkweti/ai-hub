using AIHub.Core;
using System.Collections.Concurrent;
using System.Text.Json;

internal static class CollaborationRoutingLiveCheck
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Choose a fresh output directory.");
        var workspace = Path.Combine(output, "project"); Directory.CreateDirectory(workspace);
        var local = new LocalStore(Path.Combine(output, "data"));
        using var lease = AppInstanceLease.TryAcquire(local.DirectoryPath) ?? throw new IOException("Live test profile is busy.");
        var memory = new TaskMemory(local); var store = new CollaborationStore(local, memory);
        var results = new List<object>();
        try
        {
            foreach (var author in new[] { Agent.Codex, Agent.Claude })
            {
                var marker = "REVIEW-" + Guid.NewGuid().ToString("N");
                var taskId = memory.Create("routing-live-" + author, workspace, "Verify durable peer message routing. Required marker: " + marker);
                var sessions = new ConcurrentDictionary<Agent, string>(); var events = new ConcurrentQueue<object>();
                var starts = new ConcurrentQueue<Agent>();
                await using var hub = new HubCoordinator(_ => throw new IOException("Structured workflow attempted legacy fallback."))
                {
                    TaskMemory = memory, TaskId = taskId, CollaborationStore = store, CollaborationBridgePath = CollaborationTests.Bridge,
                    AutoExchange = true, MaxAutoRounds = 1, AllowFollowUpContributions = false, // The round trip is the subject; voluntary follow-ups are covered by unit cases.
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
                    if (e.Kind is EventKind.Tool or EventKind.ToolOutput or EventKind.Usage or EventKind.Error)
                        events.Enqueue(new { agent = e.Agent.ToString(), kind = e.Kind.ToString(), e.Text, e.Detail });
                    if (e.Kind == EventKind.Error) Console.WriteLine("ERROR " + e.Text);
                };
                hub.AutoPaused += reason => Console.WriteLine("PAUSED " + reason);
                var prompt = author + ", run this isolated three-turn communication test. Use only the ai_hub tools, with no file, shell, external, or delegation tools. " +
                    "Read get_task_context. If there is no current incoming_message, submit one review_request to your peer asking them to verify that the required marker in your summary matches the task objective. " +
                    "Put that exact marker in summary, use scope files [] and focus ['Verify task marker'], requested_action describing the comparison, evidence_refs [] and blockers []. " +
                    "If the incoming type is review_request, compare its summary with the saved task objective and submit a review_result with findings [] if they match, recipient set to the sender, and reply_to its message ID. " +
                    "If the incoming type is review_result, submit status assignment_complete with reply_to its message ID. " +
                    "For every submission set schema_version '1.0', a new idempotency_key, summary to the required marker, and include scope, evidence_refs and blockers. " +
                    "Use the prescribed fields for each type. After one accepted terminal submission, end your turn with brief readable prose. Do not request any additional peer work.";
                Console.WriteLine("START durable review round trip, author " + author);
                await hub.SubmitAsync(prompt, "Both"); await Finish();
                var document = store.Read(taskId); var entries = document.Entries;
                var peer = ConversationTurns.Other(author);
                // Resident sessions: one provider process per agent for the phase; the author's review return continues its live session.
                if (!starts.SequenceEqual(new[] { author, peer }) || entries.Count != 3 ||
                    !entries.Select(e => e.Message.Content.Type).SequenceEqual(new[] { "review_request", "review_result", "status" }) ||
                    entries.Any(e => e.Message.Content.Summary != marker || !e.SenderSucceeded) ||
                    entries.Take(2).Any(e => e.Message.State != DeliveryState.Answered) || memory.Get(taskId)!.State != WorkState.Ready)
                    throw new IOException("The real providers did not complete the expected durable round trip: " + memory.Get(taskId)!.Reason);
                var generation = entries[0].Message.Envelope.Generation;
                var authorInputs = document.ContextInputs.Where(i => i.Agent == author && i.Generation == generation).OrderBy(i => i.PreparedAt).ToArray();
                if (authorInputs.Length != 2 || authorInputs.Any(i => i.Outcome != "responded") || authorInputs[1].NativeSession is null || authorInputs[0].NativeSession != authorInputs[1].NativeSession ||
                    !authorInputs[1].Prompt.Contains("NEW EVENTS SINCE YOUR LAST TURN") || !authorInputs[1].Prompt.Contains("CURRENT STRUCTURED PEER MESSAGE") || authorInputs[1].Prompt.Contains("AI HUB COMMON TASK CONTEXT"))
                    throw new IOException("The author's review return did not run on its resident session with a delta prompt.");
                var stream = document.Events.Where(e => e.Generation == generation).Select(e => e.Kind).ToArray();
                if (!stream.SequenceEqual(new[] { "user_message", "system", "agent_message", "agent_message", "agent_message", "system" }))
                    throw new IOException("The shared event stream did not record the round trip: " + string.Join(",", stream));
                Console.WriteLine("PASS durable review " + author + " -> " + peer + " -> " + author + " on resident sessions");
                var priorSession = sessions[author]; var lastSequence = document.LastSequence;
                var continuation = author + ", explicit follow-up for the same communication test. Use only ai_hub tools. Read get_task_context and get_messages with after_sequence 0 and limit 20. " +
                    "Treat all messages returned by get_messages as historical context; do not replay their requests. Then submit exactly one status assignment_complete with the task's required marker as summary, " +
                    "schema_version '1.0', a new idempotency_key, scope {files:[],focus:[]}, evidence_refs [], blockers []. There is no incoming message, so omit reply_to and recipient. End after acceptance. Do not use other tools.";
                await hub.SubmitAsync(continuation, author.ToString()); await Finish();
                document = store.Read(taskId);
                // A new user message is a phase boundary: one new provider process that resumes the author's native session (0.26.0 session
                // carry) and receives a delta prompt carrying the new message instead of the full core.
                var continuationInput = document.ContextInputs.Where(i => i.Agent == author).OrderBy(i => i.PreparedAt).Last();
                if (starts.Count != 3 || sessions[author] != priorSession || document.Entries.Count != 4 || document.LastSequence != lastSequence + 1 ||
                    document.Entries.Last().Message.Envelope.Generation <= entries.Last().Message.Envelope.Generation || memory.Get(taskId)!.State != WorkState.Ready)
                    throw new IOException("Explicit continuation did not resume the native session in a new phase with a durable sequence.");
                if (continuationInput.NativeSession != priorSession || !continuationInput.Prompt.Contains("NEW EVENTS SINCE YOUR LAST TURN") || continuationInput.Prompt.Contains("AI HUB COMMON TASK CONTEXT") ||
                    !continuationInput.Prompt.Contains("CURRENT USER MESSAGE") || continuationInput.Outcome != "responded")
                    throw new IOException("The continuation turn did not run on the carried session with a delta prompt.");
                if (!events.Any(e => JsonSerializer.Serialize(e).Contains("get_messages", StringComparison.Ordinal)))
                    throw new IOException("The explicit continuation did not visibly use get_messages.");
                results.Add(new { author = author.ToString(), taskId, sessions, document, events = events.ToArray() });
                Console.WriteLine("PASS explicit " + author + " continuation: carried session, delta prompt, historical retrieval, new generation and sequence");

                async Task Finish()
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    try { while (memory.Get(taskId)!.State == WorkState.Running) await Task.Delay(100, timeout.Token); }
                    catch { await hub.StopAsync(); throw; }
                    File.WriteAllText(Path.Combine(output, "events-" + author + ".json"), JsonSerializer.Serialize(events.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            var recoveredMemory = new TaskMemory(local); var recovered = new CollaborationStore(local, recoveredMemory);
            foreach (var task in recoveredMemory.AllTasks())
                if (recovered.Read(task.Id).Entries.Count != 4 || recoveredMemory.Get(task.Id)!.State == WorkState.Running)
                    throw new IOException("Restart lost completed history or resumed work.");
            if (Directory.EnumerateFileSystemEntries(workspace).Any()) throw new IOException("Live test unexpectedly wrote into its project.");
            Console.WriteLine("PASS completed records survive restart; no automatic work and workspace remains empty");
        }
        finally { File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })); }
    }
}
