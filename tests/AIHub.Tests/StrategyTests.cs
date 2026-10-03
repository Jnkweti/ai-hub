using AIHub.Core;
using System.Text.Json.Nodes;
using static CollaborationRoutingTests;

// Selectable collaboration strategies (0.31.0): independent answers, then synthesis, beside the default reaction rounds.
internal static class StrategyTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("independent answers: the second answer is formed without the first, the first speaker synthesizes with the peer's answer, and no reactions follow", async () =>
        {
            using var f = new Fixture(); var prompts = new Dictionary<int, string>(); var visible = new List<string>(); var states = new List<string>();
            await using var hub = f.Hub((agent, host, turn, prompt, _) =>
            {
                lock (prompts) prompts[turn] = prompt;
                Tool(host, "submit_message", Message());
                return Task.FromResult(turn switch { 1 => "Codex answer ALPHA: the sort is the cause.", 2 => "Claude answer BETA: the sort, plus a latent fee defect.", _ => "Synthesis GAMMA: both agree on the sort; BETA's extra defect holds." });
            });
            hub.Strategy = HubCoordinator.CollaborationStrategy.IndependentThenSynthesis; hub.AllowFollowUpContributions = true;
            hub.PreparationFactory = _ => throw new Exception("Preparation must not start for an independent answer");
            hub.Event += e => { if (e.Kind == EventKind.Message) visible.Add(e.Text); }; hub.State += states.Add;
            await hub.SubmitAsync("Diagnose the statement bug", "Both"); await f.Finished();
            var task = f.Memory.Get(f.TaskId)!;
            Check(f.Calls == 3 && f.Speakers.SequenceEqual([Agent.Codex, Agent.Claude, Agent.Codex]) && task.State == WorkState.Ready, $"Expected three turns ending ready: calls {f.Calls}, state {task.State}, reason '{task.Reason}', error '{f.LastError}'");
            Check(prompts[2].Contains("INDEPENDENT ANSWER") && prompts[2].Contains("AI HUB COMMON TASK CONTEXT") && !prompts[2].Contains("ALPHA") && !prompts[2].Contains("PRECEDING AGENT RESPONSE") && !prompts[2].Contains("initial contribution to the user's message"),
                "The second answer saw the first, or was not framed as independent");
            Check(prompts[3].Contains("SYNTHESIS STEP") && prompts[3].Contains("BETA") && prompts[3].Contains("NEW EVENTS SINCE YOUR LAST TURN") && !prompts[3].Contains("FOLLOW-UP CONTRIBUTION CHECK"),
                "The synthesis turn lacked the peer's answer or was framed as a follow-up");
            Check(visible.Count == 3 && task.Reason.Contains("synthesized"), "Both answers and the synthesis were not all visible, or the outcome does not say so: " + task.Reason);
            var roles = f.Store.Read(f.TaskId).Assignments.Select(a => a.Role).ToArray();
            Check(roles.SequenceEqual(["contribution", "independent contribution", "synthesis"]), "Assignment roles wrong: " + string.Join(",", roles));
            var stream = f.Store.Read(f.TaskId).Events;
            Check(stream.Any(e => e.Kind == "system" && e.Text.Contains("Strategy: independent answers, then synthesis")) && stream.Any(e => e.Kind == "system" && e.Text.Contains("synthesizes next")), "The stream does not record the strategy and the synthesis step");
            Check(states.Contains("Synthesizing both answers"), "No synthesis state was reported");
        });
        await test("the independent strategy yields to an addressed message, split asks and simple requests, and the default strategy is unchanged", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((agent, host, turn, prompt, _) => { Check(!prompt.Contains("INDEPENDENT ANSWER"), "Independent framing leaked into a routed message"); Tool(host, "submit_message", Message()); return Task.FromResult(agent + " reply " + turn); });
            hub.Strategy = HubCoordinator.CollaborationStrategy.IndependentThenSynthesis;
            await hub.SubmitAsync("Claude, what does the fee pass do?", "Both"); await f.Finished();
            Check(f.Calls == 1 && !f.Memory.Get(f.TaskId)!.Reason.Contains("synthesized"), "An addressed message was not answered by the named agent alone");
            await hub.SubmitAsync("thanks", "Both"); await f.Finished();
            Check(f.Calls == 2, "A greeting got more than one reply");
            await hub.SubmitAsync("@codex list the files, @claude summarize the README", "Both"); await f.Finished();
            Check(f.Calls == 4 && !f.Memory.Get(f.TaskId)!.Reason.Contains("synthesized") && f.Store.Read(f.TaskId).Assignments.All(a => a.Role != "synthesis"), "Split asks were not kept on the usual routing");
            hub.Strategy = HubCoordinator.CollaborationStrategy.ReactionRounds;
            await hub.SubmitAsync("Discuss the design", "Both"); await f.Finished();
            Check(f.Calls == 6 && f.Store.Read(f.TaskId).Events.Any(e => e.Text.Contains("Strategy: reaction rounds")), "The default strategy changed or is not recorded");
            Check(HubCoordinator.ParseStrategy("independent") == HubCoordinator.CollaborationStrategy.IndependentThenSynthesis && HubCoordinator.ParseStrategy("anything else") == HubCoordinator.CollaborationStrategy.ReactionRounds, "Strategy parsing is wrong");
        });
        await test("a synthesis that asks the peer a question continues on the usual routing and comes back for a decision", async () =>
        {
            using var f = new Fixture();
            await using var hub = f.Hub((agent, host, turn, prompt, _) =>
            {
                var incoming = Tool(host, "get_task_context", new JsonObject())["incoming_message"]?["envelope"]?.Str("message_id");
                if (turn == 3) Tool(host, "submit_message", Message("question", Agent.Claude));
                else if (turn == 4) Tool(host, "submit_message", Message(replyTo: incoming));
                else Tool(host, "submit_message", Message(replyTo: turn == 5 ? Tool(host, "get_task_context", new JsonObject())["answered_question"]?["answer"]?["envelope"]?.Str("message_id") : null));
                return Task.FromResult(turn switch { 3 => "Claude, which test did you mean?", 4 => "The cross-month one.", 5 => "Then we agree.", _ => agent + " answer " + turn });
            });
            hub.Strategy = HubCoordinator.CollaborationStrategy.IndependentThenSynthesis;
            await hub.SubmitAsync("Diagnose the statement bug", "Both"); await f.Finished();
            var document = f.Store.Read(f.TaskId);
            Check(f.Calls == 5 && f.Memory.Get(f.TaskId)!.State == WorkState.Ready && document.Entries[2].Message.Content.Type == "question" && document.Entries[2].Message.State == DeliveryState.Answered && document.Assignments.Any(a => a.Role == "resolution"),
                $"The synthesis question did not route and resolve: calls {f.Calls}, reason '{f.Memory.Get(f.TaskId)!.Reason}', error '{f.LastError}'");
        });
    }
}
