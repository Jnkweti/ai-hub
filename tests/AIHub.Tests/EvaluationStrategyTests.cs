using AIHub.Core;
using static CollaborationRoutingTests;

// Limited exploration on designated evaluation tasks (0.34.0): the policy's choice runs only there, with its probability recorded.
internal static class EvaluationStrategyTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("the chooser executes the suggestion with the stated probability and the alternative otherwise, and records which", () =>
        {
            var context = StrategyAdvisor.Describe("Customers report a wrong balance; diagnose the root cause of each problem and propose the minimal fix to ship, with the evidence that supports each conclusion.", "t", 1, 2, []);
            var kept = StrategyAdvisor.Choose(context, draw: 0.9);
            var explored = StrategyAdvisor.Choose(context, draw: 0.1);
            Check(kept.Suggested == "independent" && kept.Chosen == "independent" && !kept.Explored && Math.Abs(kept.ProbabilityOfSuggested - (1 - StrategyAdvisor.ExplorationRate)) < 1e-9, "The suggestion was not executed on a high draw");
            Check(explored.Suggested == "independent" && explored.Chosen == "reaction" && explored.Explored, "The alternative was not executed on a low draw");
            var solo = StrategyAdvisor.Choose(context with { Participants = 1 }, draw: 0.0);
            Check(solo.Chosen == "reaction" && !solo.Explored && solo.ProbabilityOfSuggested == 1, "A single participant explored");
            return Task.CompletedTask;
        });
        await test("only a designated evaluation task lets the policy choose; the decision and the phase record it, and the flag persists", async () =>
        {
            var prompt = "Customers report a wrong balance; diagnose the root cause of each problem and propose the minimal fix to ship, with the evidence that supports each conclusion.";
            Func<Fixture, ShadowStrategyLog, HubCoordinator> Build = (f, log) =>
            {
                var hub = f.Hub((_, host, _, _, _) => { Tool(host, "submit_message", Message()); return Task.FromResult("Answer."); });
                hub.StrategyChooser = (p, taskId, generation, configured) =>
                {
                    var context = StrategyAdvisor.Describe(p, taskId, generation, 2, []);
                    var (chosen, suggested, reason, probability, explored) = StrategyAdvisor.Choose(context, draw: 0.9);
                    return (HubCoordinator.ParseStrategy(chosen), ShadowStrategyLog.Describe(log.Record(context, suggested, reason, chosen, "evaluation", probability, explored)));
                };
                return hub;
            };
            // An ordinary task: the chooser is wired but never consulted; the configured strategy (reaction rounds) runs.
            using (var ordinary = new Fixture())
            {
                var log = new ShadowStrategyLog(ordinary.Local);
                await using var hub = Build(ordinary, log);
                await hub.SubmitAsync(prompt, "Both"); await ordinary.Finished();
                Check(ordinary.Calls == 2 && log.All().Length == 0 && !ordinary.Store.Read(ordinary.TaskId).Events.Any(e => e.Text.Contains("evaluation task")), "An ordinary task let the policy choose");
            }
            // An evaluation task, marked before its first phase: the policy's choice (independent answers) runs and is recorded.
            using var f = new Fixture(); var shadowLog = new ShadowStrategyLog(f.Local); var prompts = new Dictionary<int, string>();
            f.Memory.SetEvaluation(f.TaskId, true);
            Check(f.Memory.Get(f.TaskId)!.Evaluation && new TaskMemory(f.Local).Get(f.TaskId)!.Evaluation, "The evaluation flag did not persist");
            await using var evaluated = Build(f, shadowLog);
            evaluated.StrategyShadow = (_, _, _, _) => throw new InvalidOperationException("Shadow advice must not run when the chooser runs");
            evaluated.Event += e => { if (e.Kind == EventKind.Status && e.Text.Contains("Shadow strategy was not recorded")) throw new Exception("Shadow advice ran beside the chooser"); };
            await hub2Submit();
            async Task hub2Submit() { await evaluated.SubmitAsync(prompt, "Both"); await f.Finished(); }
            var decision = shadowLog.All().Single();
            Check(f.Calls == 3 && decision.Mode == "evaluation" && decision.Executed == "independent" && !decision.Explored && decision.ProbabilityOfSuggested == 0.75 && decision.Generation == 1, $"The policy's choice did not run on the evaluation task: calls {f.Calls}, decision {ShadowStrategyLog.Describe(decision)}");
            Check(f.Store.Read(f.TaskId).Events.Any(e => e.Text.Contains("Strategy: independent answers, then synthesis (evaluation task: ")) && f.Store.Read(f.TaskId).Assignments.Any(a => a.Role == "synthesis"), "The phase did not run or record the chosen strategy");
            Check(ShadowStrategyLog.Summary(shadowLog.All()).Contains("1 evaluation phase: the policy chose the strategy (0 explored, 1 ran independent answers)."), "Summary does not count evaluation phases: " + ShadowStrategyLog.Summary(shadowLog.All()));
            // Unmarked again: the configured strategy runs and nothing more is recorded.
            f.Memory.SetEvaluation(f.TaskId, false); evaluated.StrategyShadow = null;
            await hub2Submit();
            Check(f.Calls == 5 && shadowLog.All().Length == 1, "Unmarking did not restore the configured strategy");
        });
    }
}
