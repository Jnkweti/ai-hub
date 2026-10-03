using AIHub.Core;
using System.Diagnostics;

// Diagnostic: run one tool-free preparation session alone against a workspace and prompt, with every event timed,
// to see whether the provider honors "no tools" and how long a preparation takes.
internal static class PreparationLiveCheck
{
    public static async Task Run(string workspace, string agentName, string promptFile)
    {
        var agent = Enum.Parse<Agent>(agentName, true);
        var prompt = File.ReadAllText(promptFile).Trim();
        var options = new AgentOptions(Path.GetFullPath(workspace), false) { PreparationOnly = true };
        await using IAgentClient client = agent == Agent.Codex ? new CodexClient(options) : new ClaudeClient(options);
        client.RequestApproval = (_, _) => Task.FromResult(new Decision(false));
        var started = Stopwatch.StartNew(); var tools = 0;
        client.Event += e =>
        {
            if (e.Kind == EventKind.Tool && e.ItemId.Length > 0) tools++;
            if (e.Kind is not (EventKind.TextDelta)) Console.WriteLine($"{started.Elapsed.TotalSeconds,7:0.0}s {e.Kind,-10} {(e.Text.Length > 160 ? e.Text[..160] : e.Text).Replace('\n', ' ')}");
        };
        var text = ConversationPreparation.Instructions + "\n\nAI HUB COMMON TASK CONTEXT\nTask: preparation-probe\nACTIVE USER INSTRUCTIONS: none beyond the message below.\n\nCURRENT USER MESSAGE:\n" + prompt;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var reply = await client.SendAsync(text, timeout.Token);
            Console.WriteLine($"DONE {agent} in {started.Elapsed.TotalSeconds:0.0}s, {reply.Text.Length} chars, real tool calls: {tools}\n--- NOTES ---\n{reply.Text}");
        }
        catch (Exception ex) { Console.WriteLine($"FAILED {agent} after {started.Elapsed.TotalSeconds:0.0}s, real tool calls: {tools}: {ex.Message}"); }
    }
}
