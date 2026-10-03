using AIHub.Core;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

// The challenge loop: a question to a peer, the peer's answer, and the asker's recorded decision about that answer.
internal static class ChallengeResolutionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (CollaborationValidationException) { return; } throw new Exception("Invalid operation accepted"); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (var decision in new[] { "revised", "accepted" })
        await test("an answered question returns to the asker for a recorded decision: " + decision, async () =>
        {
            using var f = new Fixture(); var prompts = new Dictionary<int, string>(); var visible = new List<string>();
            string? questionId = null, answerId = null;
            await using var hub = f.Hub((agent, host, turn, prompt, _) =>
            {
                lock (prompts) prompts[turn] = prompt;
                var context = Tool(host, "get_task_context", new JsonObject());
                var incoming = context["incoming_message"]?["envelope"]?.Str("message_id");
                switch (turn)
                {
                    case 1: Tool(host, "submit_message", Message()); return Task.FromResult("Store observations locally.");
                    case 2:
                        questionId = Tool(host, "submit_message", Message("question", Agent.Codex)).Str("message_id");
                        return Task.FromResult("Codex, why local storage rather than the shared store?");
                    case 3:
                        Check(agent == Agent.Codex && incoming == questionId, "The question was not delivered to Codex");
                        answerId = Tool(host, "submit_message", Message(replyTo: incoming)).Str("message_id");
                        return Task.FromResult("Because the shared store is not durable across restarts.");
                    case 4:
                        Check(agent == Agent.Claude && incoming is null, "The resolution turn was not the asker's, or carried a peer request");
                        var answered = context["answered_question"];
                        Check(answered?["question"]?["envelope"]?.Str("message_id") == questionId && answered?["answer"]?["envelope"]?.Str("message_id") == answerId, "get_task_context did not show the question and its answer");
                        Reject(() => Tool(host, "submit_message", Message(replyTo: questionId))); // Only the answer may be replied to.
                        Tool(host, "submit_message", Message(replyTo: answerId, status: decision == "accepted" ? "no_further_contribution" : "assignment_complete"));
                        return Task.FromResult(decision == "accepted" ? "Accepted." : "Revised: durability settles it; local storage is right.");
                    default:
                        Tool(host, "submit_message", Message(status: "no_further_contribution")); return Task.FromResult("Nothing else.");
                }
            });
            // The resolution is the asker's own request coming back, not a voluntary follow-up: it runs with follow-ups off ("accepted");
            // with follow-ups on ("revised"), the answerer still gets one reaction opportunity afterwards.
            hub.AllowFollowUpContributions = decision == "revised";
            hub.Event += e => { if (e.Kind == EventKind.Message) visible.Add(e.Text); };
            await hub.SubmitAsync("Discuss where to store observations", "Both"); await f.Finished();
            var expectedCalls = decision == "revised" ? 5 : 4;
            Check(f.Calls == expectedCalls && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, $"Expected {expectedCalls} turns and a ready task: calls {f.Calls}, state {f.Memory.Get(f.TaskId)!.State}, reason '{f.Memory.Get(f.TaskId)!.Reason}', error '{f.LastError}'");
            Check(prompts[4].Contains("RESOLUTION OF YOUR QUESTION") && prompts[4].Contains("ANSWER TO YOUR QUESTION") && prompts[4].Contains(answerId!) &&
                !prompts[4].Contains("FOLLOW-UP CONTRIBUTION CHECK") && !prompts[4].Contains("initial contribution"), "The resolution turn was not framed as a decision about the answer");
            Check(prompts[2].Contains("submit a question to your teammate"), "The peer's initial turn did not invite a focused challenge");
            var document = f.Store.Read(f.TaskId);
            var resolution = document.Assignments.Single(a => a.Role == "resolution");
            Check(resolution.Agent == Agent.Claude && resolution.State == "completed" && resolution.Dependencies.SequenceEqual([questionId!, answerId!]), "The resolution assignment does not link the question and the answer");
            Check(document.Entries[1].Message.State == DeliveryState.Answered && document.Entries[3].Message.Content.ReplyTo == answerId, "The decision message does not reply to the answer");
            var note = document.Events.Single(e => e.Kind == "system" && e.Text.Contains("decides next"));
            Check(note.Ref == answerId && note.Text.Contains(questionId!), "The stream does not record which answer returned to the asker");
            var decided = document.Events.Last(e => e.Author == "Claude");
            Check(decision == "accepted" ? decided.Kind == "agent_pass" && decided.Text.StartsWith("Accepted the answer") : decided.Kind == "agent_message" && decided.Text.StartsWith("Revised"),
                "The asker's decision was not recorded distinctly: " + decided.Kind + " " + decided.Text);
            Check(visible.Count == (decision == "revised" ? 4 : 3), "An accepting pass leaked into chat, or a revision was hidden");
        });
        await test("with automatic collaboration off, an answered question pauses for the user instead of dispatching the asker", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((_, host, turn, _, _) =>
            {
                var incoming = Tool(host, "get_task_context", new JsonObject())["incoming_message"]?["envelope"]?.Str("message_id");
                Tool(host, "submit_message", turn == 1 ? Message("question", Agent.Claude) : Message(replyTo: incoming));
                return Task.FromResult(turn == 1 ? "Claude Code, does the export cover notes?" : "Yes, notes are exported.");
            });
            hub.AutoExchange = false;
            await hub.SubmitAsync("Discuss the export", "Both"); await f.Finished();
            Check(f.Calls == 2 && f.Memory.Get(f.TaskId)!.State == WorkState.Paused && f.Memory.Get(f.TaskId)!.Reason.Contains("Automatic collaboration is off"),
                $"Auto off did not pause after the answer: calls {f.Calls}, state {f.Memory.Get(f.TaskId)!.State}, reason '{f.Memory.Get(f.TaskId)!.Reason}'");
            Check(f.Store.Read(f.TaskId).Entries[0].Message.State == DeliveryState.Answered, "The question was not marked answered");
        });
        await test("a read-only Claude session is told to route a needed command to its teammate as a question", () =>
        {
            // Plan mode cannot execute; the pilots' only factual error was a hand trace. The answer returns through the resolution path.
            Check(ClaudeClient.ReadOnlyNote.Contains("submit a question to the teammate naming the exact command") && ClaudeClient.ReadOnlyNote.Contains("do not hand-trace"),
                "The read-only note does not route reproductions to the teammate");
            Check(ClaudeClient.ReadOnlyNote.Contains("a teammate is selected"), "The routing advice is not conditioned on a teammate being present");
            return Task.CompletedTask;
        });
        await test("a resolution dispatch requires a peer's answer to this agent's own question", () =>
        {
            using var f = new Fixture(); var claim = f.Begin(); var participants = new[] { Agent.Codex, Agent.Claude };
            var asking = f.Dispatch(claim, Agent.Codex); f.Submit(asking, Agent.Codex, Message("question", Agent.Claude)); var question = asking.Complete();
            var answering = f.Dispatch(claim, Agent.Claude, question.Envelope.MessageId);
            f.Submit(answering, Agent.Claude, Message(replyTo: question.Envelope.MessageId)); var answer = answering.Complete();
            f.Memory.Own(claim, Agent.Codex);
            Reject(() => f.Store.OpenDispatch(claim, Agent.Codex, participants, null, default, "missing")); // Unknown answer.
            Reject(() => f.Store.OpenDispatch(claim, Agent.Codex, participants, null, default, question.Envelope.MessageId)); // The question is not an answer.
            f.Memory.Own(claim, Agent.Claude);
            Reject(() => f.Store.OpenDispatch(claim, Agent.Claude, participants, null, default, answer.Envelope.MessageId)); // The answerer cannot resolve its own answer.
            f.Memory.Own(claim, Agent.Codex);
            var resolving = f.Store.OpenDispatch(claim, Agent.Codex, participants, null, default, answer.Envelope.MessageId);
            Reject(() => f.Submit(resolving, Agent.Codex, Message(replyTo: question.Envelope.MessageId)));
            f.Submit(resolving, Agent.Codex, Message(replyTo: answer.Envelope.MessageId)); var decided = resolving.Complete();
            Check(decided.Content.ReplyTo == answer.Envelope.MessageId && f.Store.Read(f.TaskId).Entries.Count == 3, "The resolution message was not stored with its reply_to");
            f.Memory.End(claim, WorkState.Ready, "test");
            return Task.CompletedTask;
        });
    }
}
