using AIHub.Core;
using System.Diagnostics;

/// <summary>Per-agent git worktrees (0.22.0): isolation, merging into integration, the user's checkout, and explicit conflicts.</summary>
internal static class WorktreeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task<string> Git(string cwd, params string[] args)
    {
        using var process = new Process { StartInfo = new(JsonProcess.FindExecutable("", "git")) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.invalid" }.Concat(args)) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
    /// <summary>A short-path git project with one commit, plus AI Hub stores for one edit-enabled task.</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ah-wt-" + Guid.NewGuid().ToString("N")[..8]);
        public string Project { get; }
        public LocalStore Local { get; } public TaskMemory Memory { get; } public CollaborationStore Store { get; } public string TaskId { get; }
        public int Calls; public List<Agent> Speakers { get; } = [];
        public Fixture()
        {
            Project = Path.Combine(Root, "project"); Directory.CreateDirectory(Project);
            Git(Project, "init", "-q").GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(Project, "README.md"), "hello");
            Git(Project, "add", "-A").GetAwaiter().GetResult(); Git(Project, "commit", "-q", "-m", "init").GetAwaiter().GetResult();
            Local = new(Path.Combine(Root, "data")); Memory = new(Local); TaskId = Memory.Create("room", Project, "Add files"); Store = new(Local, Memory);
        }
        public HubCoordinator Hub(Func<Agent, CollaborationMcpHost, HubCoordinator, Task<string>> respond)
        {
            HubCoordinator self = null!;
            var hub = new HubCoordinator(_ => throw new Exception("Legacy factory used"))
            {
                AllowFollowUpContributions = false, AllowEdits = true, IsolateWorktrees = true, WorktreeRoot = Path.Combine(Root, "wt"),
                TaskMemory = Memory, TaskId = TaskId, CollaborationStore = Store, CollaborationBridgePath = CollaborationTests.Bridge,
                CollaborationFactory = (agent, host) => new CollaborationRoutingTests.StructuredFake(agent, host, async (_, _) =>
                { Interlocked.Increment(ref Calls); lock (Speakers) Speakers.Add(agent); return await respond(agent, host, self); })
            };
            self = hub; return hub;
        }
        public async Task Finished()
        { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); while (Memory.Get(TaskId)!.State == WorkState.Running) await Task.Delay(20, timeout.Token); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("each agent edits in its own worktree, sees the peer's committed work, and the project changes only when the user merges", async () =>
        {
            using var f = new Fixture(); var events = new List<string>();
            await using var hub = f.Hub((agent, host, self) =>
            {
                var worktree = self.WorktreeFor(agent)!;
                Check(GitWorktrees.IsRepository(worktree) && !worktree.StartsWith(f.Project, StringComparison.OrdinalIgnoreCase), "Agent was not given its own worktree: " + worktree);
                if (agent == Agent.Claude) Check(File.Exists(Path.Combine(worktree, "Codex.txt")), "Claude's worktree did not receive Codex's committed file before its turn");
                File.WriteAllText(Path.Combine(worktree, agent + ".txt"), "by " + agent);
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message()); return Task.FromResult(agent + " added its file.");
            });
            hub.State += s => { lock (events) events.Add(s); };
            await hub.SubmitAsync("Both of you: add your own file", "Both"); await f.Finished();
            var task = f.Memory.Get(f.TaskId)!;
            Check(task.State == WorkState.Ready && f.Speakers.SequenceEqual([Agent.Codex, Agent.Claude]), $"Run did not finish with both agents: {task.State} {string.Join(",", f.Speakers)} {task.Reason}");
            var layout = f.Store.Worktrees(f.TaskId)!;
            var integrated = await Git(layout.Integration, "ls-files");
            Check(integrated.Contains("Codex.txt") && integrated.Contains("Claude.txt"), "Integration branch is missing an agent's file: " + integrated);
            Check(!File.Exists(Path.Combine(f.Project, "Codex.txt")) && !File.Exists(Path.Combine(f.Project, "Claude.txt")), "The project checkout changed before the user merged");
            var doc = f.Store.Read(f.TaskId);
            Check(doc.Events.Any(e => e.Kind == "system" && e.Text.StartsWith("Agent worktrees:")) && doc.Events.Count(e => e.Kind == "system" && e.Text.Contains("merged into " + layout.IntegrationBranch)) == 2, "Worktree events missing from the stream");
            Check(events.Contains("Working in isolated worktrees"), "State did not mention isolation");
            var conflicts = await f.Store.MergeWorktreesIntoProjectAsync(f.TaskId, default);
            Check(conflicts.Length == 0 && File.ReadAllText(Path.Combine(f.Project, "Codex.txt")) == "by Codex" && File.ReadAllText(Path.Combine(f.Project, "Claude.txt")) == "by Claude", "Merge into the project did not bring both files");
            await f.Store.RemoveWorktreesAsync(f.TaskId, default);
            Check(f.Store.Worktrees(f.TaskId) is null && !Directory.Exists(layout.Codex) && !(await Git(f.Project, "branch", "--list", layout.IntegrationBranch)).Contains("aihub/"), "Worktrees or branches were not removed");
        });
        await test("a conflicting merge is aborted and reported, leaving the agent's worktree clean with its own version", async () =>
        {
            using var f = new Fixture(); var layout = await GitWorktrees.EnsureAsync(f.Project, f.TaskId, Path.Combine(f.Root, "wt"), default);
            var again = await GitWorktrees.EnsureAsync(f.Project, f.TaskId, Path.Combine(f.Root, "wt"), default);
            Check(again.Integration == layout.Integration && again.BaseCommit == layout.BaseCommit, "Ensure was not idempotent");
            File.WriteAllText(Path.Combine(layout.Integration, "shared.txt"), "integration version"); await Git(layout.Integration, "add", "-A"); await Git(layout.Integration, "commit", "-q", "-m", "integration edit");
            File.WriteAllText(Path.Combine(layout.Codex, "shared.txt"), "codex version");
            var (committed, publishConflicts) = await GitWorktrees.PublishAsync(layout, Agent.Codex, "codex edit", default);
            Check(committed && publishConflicts.SequenceEqual(["shared.txt"]), $"Publish conflict not reported: committed={committed} conflicts={string.Join(",", publishConflicts)}");
            Check(File.ReadAllText(Path.Combine(layout.Integration, "shared.txt")) == "integration version" && (await Git(layout.Integration, "status", "--porcelain")).Trim().Length == 0, "Integration was left mid-merge or changed");
            var syncConflicts = await GitWorktrees.SyncInAsync(layout, Agent.Codex, default);
            Check(syncConflicts.SequenceEqual(["shared.txt"]) && File.ReadAllText(Path.Combine(layout.Codex, "shared.txt")) == "codex version" && (await Git(layout.Codex, "status", "--porcelain")).Trim().Length == 0, "Sync conflict not reported or worktree left dirty");
            Check((await GitWorktrees.SyncInAsync(layout, Agent.Claude, default)).Length == 0 && File.ReadAllText(Path.Combine(layout.Claude, "shared.txt")) == "integration version", "A clean sync did not bring integration into the other worktree");
        });
    }
}
