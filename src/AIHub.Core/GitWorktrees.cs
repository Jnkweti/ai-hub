using System.Diagnostics;

namespace AIHub.Core;

/// <summary>
/// Where each agent works when worktree isolation is on: one git worktree per agent plus an integration worktree, all
/// on task-scoped branches. The user's own checkout is untouched until they merge the integration branch into it.
/// </summary>
public sealed record WorktreeLayout(string Root, string Integration, string Codex, string Claude, string BranchPrefix, string BaseCommit)
{
    public string PathFor(Agent agent) => agent == Agent.Codex ? Codex : Claude;
    public string BranchFor(Agent agent) => BranchPrefix + "/" + (agent == Agent.Codex ? "codex" : "claude");
    public string IntegrationBranch => BranchPrefix + "/integration";
}

/// <summary>Git plumbing for per-agent worktrees. Every command is bounded; conflicts are reported, never resolved silently.</summary>
public static class GitWorktrees
{
    private static readonly string[] Identity = ["-c", "user.name=AI Hub", "-c", "user.email=aihub@localhost", "-c", "core.autocrlf=false"];
    public static bool IsRepository(string workspace) => Directory.Exists(Path.Combine(workspace, ".git")) || File.Exists(Path.Combine(workspace, ".git"));
    /// <summary>Creates the three worktrees for a task from the project's HEAD, or reuses the ones that already exist.</summary>
    public static async Task<WorktreeLayout> EnsureAsync(string workspace, string taskId, string worktreeRoot, CancellationToken token)
    {
        var tag = taskId[..Math.Min(8, taskId.Length)];
        var root = Path.Combine(worktreeRoot, tag); Directory.CreateDirectory(root);
        var layout = new WorktreeLayout(root, Path.Combine(root, "i"), Path.Combine(root, "c"), Path.Combine(root, "k"), "aihub/" + tag, "");
        var head = (await Run(workspace, ["rev-parse", "--verify", "HEAD"], token)).Trim();
        if (head.Length == 0) throw new IOException("The project has no commits yet; make an initial commit before using agent worktrees.");
        await TryRun(workspace, ["worktree", "prune"], token);
        foreach (var (path, branch) in new[] { (layout.Integration, layout.IntegrationBranch), (layout.Codex, layout.BranchFor(Agent.Codex)), (layout.Claude, layout.BranchFor(Agent.Claude)) })
        {
            if (IsRepository(path)) continue;
            var exists = await TryRun(workspace, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], token);
            await Run(workspace, exists ? ["worktree", "add", path, branch] : ["worktree", "add", "-b", branch, path, head], token);
        }
        return layout with { BaseCommit = head };
    }
    /// <summary>Brings the integration branch into the agent's worktree. Returns the conflicting files after aborting, or none.</summary>
    public static Task<string[]> SyncInAsync(WorktreeLayout layout, Agent agent, CancellationToken token) => MergeAsync(layout.PathFor(agent), layout.IntegrationBranch, token);
    /// <summary>Commits everything the agent changed and merges its branch into integration. Conflicts abort the merge and are returned.</summary>
    public static async Task<(bool Committed, string[] Conflicts)> PublishAsync(WorktreeLayout layout, Agent agent, string message, CancellationToken token)
    {
        var worktree = layout.PathFor(agent);
        await Run(worktree, ["add", "-A"], token);
        var committed = (await Run(worktree, ["status", "--porcelain"], token)).Trim().Length > 0;
        if (committed) await Run(worktree, [.. Identity, "commit", "-q", "-m", message], token);
        return (committed, await MergeAsync(layout.Integration, layout.BranchFor(agent), token));
    }
    /// <summary>Merges the integration branch into the user's checkout. Conflicts abort the merge and are returned.</summary>
    public static Task<string[]> MergeIntoProjectAsync(string workspace, WorktreeLayout layout, CancellationToken token) => MergeAsync(workspace, layout.IntegrationBranch, token);
    /// <summary>Removes the worktrees and their branches; the integration branch's history is lost unless merged first.</summary>
    public static async Task RemoveAsync(string workspace, WorktreeLayout layout, CancellationToken token)
    {
        foreach (var path in new[] { layout.Codex, layout.Claude, layout.Integration })
            if (Directory.Exists(path)) await TryRun(workspace, ["worktree", "remove", "--force", path], token);
        await TryRun(workspace, ["worktree", "prune"], token);
        foreach (var branch in new[] { layout.BranchFor(Agent.Codex), layout.BranchFor(Agent.Claude), layout.IntegrationBranch })
            await TryRun(workspace, ["branch", "-D", branch], token);
        if (Directory.Exists(layout.Root)) try { Directory.Delete(layout.Root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public static async Task<string> HeadAsync(string worktree, CancellationToken token) => (await Run(worktree, ["rev-parse", "--verify", "HEAD"], token)).Trim();
    private static async Task<string[]> MergeAsync(string cwd, string branch, CancellationToken token)
    {
        var (code, _, error) = await Exec(cwd, [.. Identity, "merge", "--no-edit", branch], token);
        if (code == 0) return [];
        var conflicting = (await Run(cwd, ["diff", "--name-only", "--diff-filter=U"], token)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        await TryRun(cwd, ["merge", "--abort"], token);
        return conflicting.Length > 0 ? conflicting : ["(merge failed: " + error.Trim() + ")"];
    }
    private static async Task<string> Run(string cwd, string[] args, CancellationToken token)
    {
        var (code, output, error) = await Exec(cwd, args, token);
        if (code != 0) throw new IOException($"git {string.Join(' ', args.Where(a => !a.StartsWith("-c") && !a.Contains('=')))} failed: {error.Trim()}");
        return output;
    }
    private static async Task<bool> TryRun(string cwd, string[] args, CancellationToken token) => (await Exec(cwd, args, token)).Code == 0;
    private static async Task<(int Code, string Output, string Error)> Exec(string cwd, string[] args, CancellationToken token)
    {
        using var process = new Process { StartInfo = new(JsonProcess.FindExecutable("", "git")) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0"; process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(120));
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (token.IsCancellationRequested) throw;
            throw new IOException("git did not finish in time: " + string.Join(' ', args));
        }
        return (process.ExitCode, await output, await error);
    }
}
