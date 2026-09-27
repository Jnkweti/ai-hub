using AIHub.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class InputQuestionTests
{
    private static void Check(bool condition, string failure) { if (!condition) throw new Exception(failure); }
    private static IAgentClient Client(Agent agent) => agent == Agent.Codex
        ? new CodexClient(new(AppContext.BaseDirectory, false, Executable: Environment.ProcessPath!))
        : new ClaudeClient(new(AppContext.BaseDirectory, false, Executable: Environment.ProcessPath!));
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (var agent in new[] { Agent.Codex, Agent.Claude })
        {
            await test(agent + " preserves question choices and routes each answer by its original key", async () =>
            {
                await using var client = Client(agent);
                var count = 0;
                client.RequestApproval = (question, _) =>
                {
                    count++;
                    Check(question.Agent == agent && question.IsQuestion, "Question identity lost");
                    if (count == 1)
                    {
                        Check(question.Title == "Layout" && question.Detail == "Which layout should we use?", "Question flattened or changed");
                        Check(question.Options is { Length: 2 } && question.Options[1] == new QuestionChoice("Comfortable", "More room for each message"), "Choice labels/descriptions lost");
                        Check(question.AllowFreeText && question.MultiSelect == (agent == Agent.Claude), "Choice mode lost");
                        return Task.FromResult(new Decision(true, agent == Agent.Claude ? "Compact, Comfortable" : "Comfortable"));
                    }
                    Check(question.Options is { Length: 0 } && question.AllowFreeText, "Open-ended question became a choice");
                    return Task.FromResult(new Decision(true, "A custom answer\nwith another line"));
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var reply = await client.SendAsync("input-fixture-batch", timeout.Token);
                var data = JsonNode.Parse(reply.Text)!;
                var answers = data["answers"]!;
                Check(count == 2, "Lost a batched question");
                if (agent == Agent.Codex)
                {
                    Check(answers["layout"]?["answers"]?[0]?.ToString() == "Comfortable", "Wrong Codex choice payload");
                    Check(answers["notes"]?["answers"]?[0]?.ToString() == "A custom answer\nwith another line", "Free text changed");
                }
                else
                {
                    Check(answers.Str("Which layout should we use?") == "Compact, Comfortable", "Wrong Claude multi-select payload");
                    Check(answers.Str("What else should we know?") == "A custom answer\nwith another line", "Free text changed");
                    Check(data["questions"]?.AsArray().Count == 2, "Original Claude input was not passed through");
                }
            });
            await test(agent + " question decline sends no fabricated answer", async () =>
            {
                await using var client = Client(agent);
                client.RequestApproval = (_, _) => Task.FromResult(new Decision(false));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var reply = await client.SendAsync("input-fixture-choice", timeout.Token);
                Check(agent == Agent.Claude ? reply.Text == "Question declined" : JsonNode.Parse(reply.Text)?["answers"]?["layout"]?["answers"]?.AsArray().Count == 0, "Decline was answered");
            });
            await test(agent + " withdrawing a question cancels its waiting UI", async () =>
            {
                await using var client = Client(agent);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.RequestApproval = async (_, token) =>
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                    return new(false);
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var reply = await client.SendAsync("input-fixture-withdraw", timeout.Token);
                await cancelled.Task.WaitAsync(timeout.Token);
                Check(reply.Text == "Question withdrawn", "Withdrawn question held up the provider");
            });
            await test(agent + " stopping a question cancels the pending turn", async () =>
            {
                await using var client = Client(agent);
                var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.RequestApproval = async (_, token) => { requested.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(false); };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var turn = client.SendAsync("input-fixture-open", timeout.Token);
                await requested.Task.WaitAsync(timeout.Token); timeout.Cancel();
                try { await turn; throw new Exception("Cancelled turn produced an answer"); }
                catch (OperationCanceledException) { }
            });
        }
        await test("Codex secret and closed-choice flags survive parsing", () =>
        {
            var secret = Approval.Question(Agent.Codex, JsonNode.Parse("""{"header":"Private","question":"Private answer?","isSecret":true,"options":null}"""));
            Check(secret.IsSecret && secret.AllowFreeText, "Secret flag lost");
            var closed = Approval.Question(Agent.Codex, JsonNode.Parse("""{"header":"Choice","question":"Choose?","options":[{"label":"Yes","description":"Accept"}]}"""));
            Check(!closed.AllowFreeText, "Closed choice gained arbitrary input");
            return Task.CompletedTask;
        });
    }
    private static void Emit(object data) => Console.WriteLine(JsonSerializer.Serialize(data));
    public static string Mode(string prompt) => System.Text.RegularExpressions.Regex.Matches(prompt, "input-fixture[-a-z]*").Last().Value;
    public static async Task<bool> EmitRequest(bool claude, string mode)
    {
        if (mode.Contains("delayed")) await Task.Delay(5000);
        var choices = new[] { new { label = "Compact", description = "Fit more messages on screen" }, new { label = "Comfortable", description = "More room for each message" } };
        var choice = Json.Obj(new { id = "layout", header = "Layout", question = "Which layout should we use?", options = choices, isOther = true, multiSelect = claude && (mode.Contains("multi") || mode.Contains("batch")) });
        var open = Json.Obj(new { id = "notes", header = "Notes", question = "What else should we know?", isSecret = mode.Contains("secret") });
        var questions = new JsonArray();
        questions.Add(mode.Contains("open") || mode.Contains("secret") ? open : choice);
        if (mode.Contains("batch")) questions.Add(open);
        if (claude) Emit(new { type = "control_request", request_id = "question", request = new { subtype = "can_use_tool", tool_name = "AskUserQuestion", input = new { questions } } });
        else Emit(new { id = "question", method = "item/tool/requestUserInput", @params = new { threadId = "fake-codex", turnId = "t1", itemId = "q1", questions } });
        if (mode.Contains("duplicate"))
        {
            await Task.Delay(100);
            if (claude) Emit(new { type = "control_request", request_id = "question", request = new { subtype = "can_use_tool", tool_name = "AskUserQuestion", input = new { questions } } });
            else Emit(new { id = "question", method = "item/tool/requestUserInput", @params = new { threadId = "fake-codex", turnId = "t1", itemId = "q1", questions } });
        }
        if (mode.Contains("flood"))
            for (var i = 0; i < 17; i++)
            {
                if (claude) Emit(new { type = "control_request", request_id = "flood-" + i, request = new { subtype = "can_use_tool", tool_name = "AskUserQuestion", input = new { questions } } });
                else Emit(new { id = "flood-" + i, method = "item/tool/requestUserInput", @params = new { threadId = "fake-codex", turnId = "t1", itemId = "q1", questions } });
            }
        if (!mode.Contains("withdraw")) return false;
        await Task.Delay(350);
        if (claude) Emit(new { type = "control_cancel_request", request_id = "question" });
        else Emit(new { method = "serverRequest/resolved", @params = new { threadId = "fake-codex", requestId = "question" } });
        return true;
    }
    public static string Reply(bool claude, JsonNode message, string mode)
    {
        if (mode.Contains("secret")) return "Private answer received";
        if (!claude) return message["result"]!.ToJsonString();
        var response = message["response"]?["response"];
        return response.Str("behavior") == "allow" ? response!["updatedInput"]!.ToJsonString() : "Question declined";
    }
}
