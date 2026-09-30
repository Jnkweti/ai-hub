using AIHub.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

static class ProjectStatusTests
{
    public const string Report = """
        {"summary":"A small C# project with a documented entry point.","findings":[{"severity":"medium","claim":"README does not document validation.","evidence":["README.md"]}],"nextSteps":["Document the validation command."],"limitations":["Build and tests were not run."]}
        """;
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("status aliases route narrowly without stealing arbitrary tasks", () =>
        {
            foreach (var value in new[] { "status", "status check", "Check project status", "Please check the project status.", "Give me a project status update" })
                Check(ProjectStatusWorkflow.IsStatusRequest(value), "Missed status alias: " + value);
            foreach (var value in new[] { "Explain the status design", "Check project status and fix it", "hello", "Continue task: check project status" })
                Check(!ProjectStatusWorkflow.IsStatusRequest(value), "Hijacked task: " + value);
            return Task.CompletedTask;
        });
        await test("folder fingerprints detect content, additions, deletion and source app directories", async () =>
        {
            using var f = new Fixture();
            var first = await f.Snapshot(); Check(first.Reusable, first.Limitation);
            var path = Path.Combine(f.Workspace, "README.md"); var time = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, "changed!"); File.SetLastWriteTimeUtc(path, time);
            var second = await f.Snapshot(); Check(first.Fingerprint != second.Fingerprint, "Same-size/mtime edit missed");
            Directory.CreateDirectory(Path.Combine(f.Workspace, "app")); File.WriteAllText(Path.Combine(f.Workspace, "app", "main.cs"), "source");
            var third = await f.Snapshot(); Check(third.Files.ContainsKey("app/main.cs") && third.Fingerprint != second.Fingerprint, "Source app directory excluded");
            File.Delete(Path.Combine(f.Workspace, "app", "main.cs")); var fourth = await f.Snapshot();
            Check(fourth.Fingerprint == second.Fingerprint, "Deletion was not reflected");
            Directory.CreateDirectory(Path.Combine(f.Workspace, "node_modules")); File.WriteAllText(Path.Combine(f.Workspace, "node_modules", "library.js"), "dependency");
            Check((await f.Snapshot()).Fingerprint == fourth.Fingerprint, "Generated dependency changed folder scope");
        });
        await test("Git fingerprints detect edits to already dirty files, index and branch changes", async () =>
        {
            using var f = new Fixture();
            await Git(f.Workspace, "init", "-b", "main");
            File.WriteAllText(Path.Combine(f.Workspace, ".gitignore"), "ignored.txt\n");
            await Git(f.Workspace, "add", ".");
            await Git(f.Workspace, "-c", "user.name=AI Hub test", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
            File.WriteAllText(Path.Combine(f.Workspace, "README.md"), "dirty one"); var first = await f.Snapshot();
            File.WriteAllText(Path.Combine(f.Workspace, "README.md"), "dirty two"); var second = await f.Snapshot();
            Check(first.Fingerprint != second.Fingerprint, "Already dirty content change missed");
            await Git(f.Workspace, "add", "README.md"); var staged = await f.Snapshot();
            Check(staged.Fingerprint != second.Fingerprint, "Index change missed");
            File.WriteAllText(Path.Combine(f.Workspace, "ignored.txt"), "ignored"); Check((await f.Snapshot()).Fingerprint == staged.Fingerprint, "Ignored file was included");
            await Git(f.Workspace, "checkout", "-b", "other"); Check((await f.Snapshot()).Fingerprint != staged.Fingerprint, "Branch change missed");
        });
        await test("status claim excludes a separate process and releases after crash", async () =>
        {
            using var f = new Fixture();
            using var child = new Process { StartInfo = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in new[] { "--status-claim", f.Data, f.Workspace }) child.StartInfo.ArgumentList.Add(arg);
            child.Start();
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)) == "CLAIMED", "Child did not claim");
                using var blocked = new CancellationTokenSource(300);
                try { await using var unexpected = await f.Store.ClaimAsync(f.Workspace, blocked.Token); throw new Exception("Two processes owned one inspection"); }
                catch (OperationCanceledException) { }
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
            using var timeout = new CancellationTokenSource(3000);
            await using var recovered = await f.Store.ClaimAsync(f.Workspace, timeout.Token);
        });
        await test("one inspection then focused review, shared warm cache and no session leakage", async () =>
        {
            using var f = new Fixture(); var events = new List<AgentEvent>(); var notices = new List<string>();
            var workflow = f.Workflow(); workflow.Event += events.Add; workflow.Notice += notices.Add;
            await workflow.RunAsync(f.Request(), CancellationToken.None);
            Check(f.Calls.Count == 2 && f.Maximum == 1, "Duplicated or concurrent broad inspection");
            Check(f.Calls[0].Agent == Agent.Codex && f.Calls[1].Agent == Agent.Claude, "Incorrect assignment");
            Check(f.Calls[1].Prompt.Contains("INSPECTOR REPORT") && !f.Calls[1].Prompt.Contains("Partial file map"), "Review received a broad discovery brief");
            Check(events.All(e => e.Kind != EventKind.Session) && events.Count(e => e.Kind == EventKind.Message) == 2, "Native history or raw output leaked");
            var next = f.Workflow(); next.Notice += notices.Add;
            await next.RunAsync(f.Request() with { RoomId = "another-room" }, CancellationToken.None);
            Check(f.Calls.Count == 2 && notices.Any(n => n.Contains("No new model turns")), "Unchanged project did not reuse across rooms");
            await using var claim = await f.Store.ClaimAsync(f.Workspace, CancellationToken.None);
            Check(claim.Read()!.ProviderUsage.Count == 2, "Actual usage events not saved");
        });
        await test("simultaneous status requests wait and reuse the first owner's report", async () =>
        {
            using var f = new Fixture();
            await Task.WhenAll(f.Workflow().RunAsync(f.Request(), CancellationToken.None), f.Workflow().RunAsync(f.Request() with { RoomId = "peer" }, CancellationToken.None));
            Check(f.Calls.Count == 2 && f.Maximum == 1, "Duplicate inspection after waiting for owner");
        });
        await test("changed files, settings and explicit refresh invalidate saved status", async () =>
        {
            using var f = new Fixture();
            await f.Run();
            File.WriteAllText(Path.Combine(f.Workspace, "README.md"), "changed"); await f.Run(); Check(f.Calls.Count == 4, "Stale report reused");
            await f.Workflow().RunAsync(f.Request() with { ForceRefresh = true }, CancellationToken.None); Check(f.Calls.Count == 6, "Explicit refresh skipped");
            await f.Workflow().RunAsync(f.Request() with { ConnectionKey = "new-model" }, CancellationToken.None); Check(f.Calls.Count == 8, "Different settings reused");
        });
        await test("single recipient and preferred inspector retain their own scope", async () =>
        {
            using var f = new Fixture();
            await f.Workflow().RunAsync(f.Request() with { Target = "Claude" }, CancellationToken.None);
            Check(f.Calls.Count == 1 && f.Calls[0].Agent == Agent.Claude, "Single recipient started peer");
            await f.Workflow().RunAsync(f.Request() with { PreferredInspector = Agent.Claude }, CancellationToken.None);
            Check(f.Calls.Count == 3 && f.Calls[1].Agent == Agent.Claude && f.Calls[2].Agent == Agent.Codex, "Preferred inspector was ignored");
        });
        await test("in-flight edits prevent saving an apparently current report", async () =>
        {
            using var f = new Fixture(); var notices = new List<string>();
            f.Respond = (_, _, _) => { File.WriteAllText(Path.Combine(f.Workspace, "README.md"), Guid.NewGuid().ToString()); return Task.FromResult(Report); };
            var workflow = f.Workflow(); workflow.Notice += notices.Add;
            await workflow.RunAsync(f.Request(), CancellationToken.None);
            Check(!File.Exists(f.ReportPath) && notices.Any(n => n.Contains("changed during")), "Changed project was cached");
        });
        await test("unsupported or invented evidence does not publish shared memory", async () =>
        {
            foreach (var response in new[] { "I agree with the plan", Report.Replace("README.md", "../elsewhere.txt"), "{\"summary\":\"text\",\"findings\":null}" })
            {
                using var f = new Fixture(); f.Respond = (_, _, _) => Task.FromResult(response);
                try { await f.Run(); throw new Exception("Invalid report accepted"); } catch (InvalidDataException) { }
                Check(f.Calls.Count == 1 && !File.Exists(f.ReportPath), "Invalid report was reviewed or cached");
            }
        });
        await test("progress notes before the report are skipped but text after it is not", async () =>
        {
            foreach (var response in new[] { "Confirmed the cited path; now checking the handoff claims.\n\n" + Report, "Checked the evidence.\n```json\n" + Report + "\n```" })
            {
                using var f = new Fixture(); f.Respond = (_, _, _) => Task.FromResult(response);
                await f.Run();
                Check(f.Calls.Count == 2 && File.Exists(f.ReportPath), "Report after a progress note was rejected");
            }
            foreach (var response in new[] { Report + "\n\nOne more thought after the report.", "Notes only.\n{ not json" })
            {
                using var f = new Fixture(); f.Respond = (_, _, _) => Task.FromResult(response);
                try { await f.Run(); throw new Exception("Report with trailing text accepted"); } catch (InvalidDataException) { }
                Check(f.Calls.Count == 1 && !File.Exists(f.ReportPath), "Invalid report was reviewed or cached");
            }
        });
        await test("oversized file is fingerprinted by size and time and keeps freshness reuse", async () =>
        {
            using var f = new Fixture();
            using (var file = File.Create(Path.Combine(f.Workspace, "large.dat"))) file.SetLength(9 * 1024 * 1024);
            var snapshot = await f.Snapshot();
            Check(snapshot.Reusable && snapshot.Limitation.Length == 0 && snapshot.Files["large.dat"].StartsWith("stat:"), $"A large file made the fingerprint incomplete: '{snapshot.Limitation}'");
            await f.Run(); Check(File.Exists(f.ReportPath), "Complete snapshot with a large asset was not cached");
        });
        await test("canceled uncooperative worker cannot save a delayed completion", async () =>
        {
            using var f = new Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Respond = async (_, _, _) => { entered.SetResult(); await release.Task; return Report; };
            using var cancel = new CancellationTokenSource(); var task = f.Workflow().RunAsync(f.Request(), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            using var wait = new CancellationTokenSource(250);
            try { await using var claim = await f.Store.ClaimAsync(f.Workspace, wait.Token); throw new Exception("Claim released before worker finished"); } catch (OperationCanceledException) { }
            release.SetResult();
            try { await task; throw new Exception("Canceled report accepted"); } catch (OperationCanceledException) { }
            Check(!File.Exists(f.ReportPath) && f.Disposed == 1, "Canceled worker persisted results or remained owned");
        });
        await test("deleting the source room clears only its own latest project report", async () =>
        {
            using var f = new Fixture(); await f.Run();
            await f.Store.ForgetAsync(f.Workspace, "different-room", CancellationToken.None); Check(File.Exists(f.ReportPath), "Other room's report deleted");
            await f.Store.ForgetAsync(f.Workspace, "source-room", CancellationToken.None); Check(!File.Exists(f.ReportPath), "Source report retained");
            await f.Run(); await f.Store.ForgetAsync(f.Workspace, null, CancellationToken.None); Check(!File.Exists(f.ReportPath), "Forget action retained report");
        });
        await test("expired and malformed cache records are refreshed", async () =>
        {
            using var f = new Fixture(); await f.Run();
            await using (var claim = await f.Store.ClaimAsync(f.Workspace, CancellationToken.None))
            { var record = claim.Read()!; record.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1); claim.Write(record, CancellationToken.None); }
            await f.Run(); Check(f.Calls.Count == 4, "Expired report reused");
            File.WriteAllText(f.ReportPath, "malformed"); await f.Run(); Check(f.Calls.Count == 6, "Malformed report reused");
        });
        await test("status coordinator stops after review even with automatic collaboration enabled", async () =>
        {
            using var f = new Fixture(); var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var sessionLeak = false;
            await using var hub = new HubCoordinator(_ => throw new Exception("Room history factory must not serve status tasks")) { AutoExchange = true };
            hub.State += s => { if (s == "Ready") finished.TrySetResult(); };
            hub.Event += e => { if (e.Kind == EventKind.Session) sessionLeak = true; };
            await hub.SubmitProjectStatusAsync(f.Request(), f.Store, f.Client);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check(f.Calls.Count == 2 && hub.ExchangeCount == 0 && !sessionLeak, "Status entered room history or automatic loop");
        });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Git(string workspace, params string[] arguments)
    {
        using var p = new Process { StartInfo = new("git") { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) p.StartInfo.ArgumentList.Add(argument);
        p.Start(); var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(); await output; Check(p.ExitCode == 0, await error);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "status-fixture-" + Guid.NewGuid().ToString("N"));
        public string Workspace { get; }
        public string Data { get; }
        public ProjectStatusStore Store { get; }
        public string ReportPath => Path.Combine(Store.WorkspaceDirectory(Workspace), "report.json");
        public List<(Agent Agent, string Prompt)> Calls { get; } = [];
        public int Active, Maximum, Disposed;
        public Func<Agent, string, CancellationToken, Task<string>>? Respond;
        public Fixture()
        {
            Workspace = Path.Combine(root, "project"); Data = Path.Combine(root, "data");
            Directory.CreateDirectory(Workspace); File.WriteAllText(Path.Combine(Workspace, "README.md"), "original");
            Store = new(Data);
        }
        public ProjectStatusRequest Request() => new(Workspace, "source-room", "Both", Agent.Codex, "default");
        public Task<ProjectSnapshot> Snapshot() => ProjectSnapshot.CaptureAsync(Workspace, CancellationToken.None);
        public ProjectStatusWorkflow Workflow() => new(Store, Client);
        public IAgentClient Client(Agent agent) => new StatusAgent(agent, this);
        public Task Run() => Workflow().RunAsync(Request(), CancellationToken.None);
        public void Dispose()
        {
            var full = Path.GetFullPath(root);
            Check(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), "Unsafe fixture cleanup");
            foreach (var path in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            Directory.Delete(full, true);
        }
    }
    private sealed class StatusAgent(Agent agent, Fixture fixture) : IAgentClient
    {
        public Agent Agent => agent;
        public string? SessionId => "isolated-status-session";
        public event Action<AgentEvent>? Event;
        public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
        public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
        {
            fixture.Calls.Add((agent, prompt)); fixture.Active++; fixture.Maximum = Math.Max(fixture.Maximum, fixture.Active);
            try
            {
                Event?.Invoke(new(agent, EventKind.Session, SessionId!));
                await Task.Delay(35, token);
                var body = fixture.Respond is null ? Report : await fixture.Respond(agent, prompt, token);
                Event?.Invoke(new(agent, EventKind.Message, body, "native-message"));
                Event?.Invoke(new(agent, EventKind.Usage, "Fixture usage", Detail: "{\"input_tokens\":100,\"output_tokens\":50}"));
                return new(body, SessionId);
            }
            finally { fixture.Active--; }
        }
        public ValueTask DisposeAsync() { fixture.Disposed++; return ValueTask.CompletedTask; }
    }
}
