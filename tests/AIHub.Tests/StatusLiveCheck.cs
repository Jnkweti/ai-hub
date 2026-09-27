using AIHub.Core;
using System.Text.Json;

static class StatusLiveCheck
{
    // Explicit opt-in: two real, read-only turns in a tiny disposable sample project.
    public static async Task RunAsync(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root)) throw new IOException("Use a new evidence directory for the live check.");
        var project = Path.Combine(root, "sample-project"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "README.md"), "# Status sample\nA deliberately small console program that adds two integers. No test project or build configuration is supplied. Review only these two files. Do not inspect outside this sample folder.\n");
        File.WriteAllText(Path.Combine(project, "Program.cs"), "int Add(int a, int b) => a + b;\nConsole.WriteLine(Add(2, 3));\n");
        var store = new ProjectStatusStore(Path.Combine(root, "data"));
        var calls = 0;
        var workflow = new ProjectStatusWorkflow(store, agent =>
        {
            calls++;
            return agent == Agent.Codex ? new CodexClient(new(project, false)) : new ClaudeClient(new(project, false));
        });
        var sync = new object();
        workflow.Event += item =>
        {
            lock (sync) File.AppendAllText(Path.Combine(root, "events.jsonl"), JsonSerializer.Serialize(item) + "\n");
            if (item.Kind is EventKind.Status or EventKind.Error) Console.WriteLine(item.Agent + ": " + item.Text);
        };
        workflow.Notice += text => { File.AppendAllText(Path.Combine(root, "report.md"), text + "\n\n---\n\n"); Console.WriteLine(text.Split('\n')[0]); };
        workflow.Dispatch += (from, to, text) => File.AppendAllText(Path.Combine(root, "assignments.jsonl"), JsonSerializer.Serialize(new { from, to, text }) + "\n");
        var request = new ProjectStatusRequest(project, "live-fixture", "Both", Agent.Codex, "installed-defaults");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await workflow.RunAsync(request, timeout.Token);
        if (calls != 2) throw new Exception("Cold check did not use two assigned workers.");
        await workflow.RunAsync(request with { RoomId = "warm-fixture" }, timeout.Token);
        if (calls != 2) throw new Exception("Warm check made additional model calls.");
        Console.WriteLine("PASS live inspection/review, structured reports and warm reuse: 2 cold turns, 0 warm turns.");
        Console.WriteLine(root);
    }
}
