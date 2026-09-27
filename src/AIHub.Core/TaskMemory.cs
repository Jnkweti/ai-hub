using System.Text;
using System.Text.Json;

namespace AIHub.Core;

public enum WorkState { Ready, Running, Paused, Stopped, Failed, Interrupted }
public sealed class TaskNote
{
    public string Source { get; set; } = "You";
    public string Text { get; set; } = "";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class WorkTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RoomId { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string Objective { get; set; } = "";
    public WorkState State { get; set; }
    public string Reason { get; set; } = "";
    public string Owner { get; set; } = "";
    public long Generation { get; set; }
    public bool AllowEdits { get; set; }
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public List<TaskNote> Notes { get; set; } = [];
    public Dictionary<Agent, string> LatestReplies { get; set; } = [];
}
public sealed record TaskClaim(string TaskId, long Generation, string Nonce);

/// <summary>One registry per application profile, protected by AppInstanceLease.
/// Atomic JSON keeps task updates durable; live claims are never restored as workers.</summary>
public sealed class TaskMemory
{
    private readonly LocalStore store;
    private readonly object gate = new();
    private readonly List<WorkTask> tasks;
    private readonly Dictionary<string, TaskClaim> claims = [];
    public TaskMemory(LocalStore store)
    {
        this.store = store;
        tasks = store.Load("tasks.json", () => new List<WorkTask>(), Repair);
        foreach (var task in tasks.Where(t => t.State == WorkState.Running))
        {
            task.State = WorkState.Interrupted; task.Owner = "";
            task.Reason = "The previous application run ended. Review the project before explicitly continuing.";
        }
        // Loading never launches or reassigns an interrupted worker.
        Persist();
        var knownLedgers = tasks.Select(t => CollaborationStore.Filename(t.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphaned = Directory.EnumerateFiles(store.DirectoryPath, "collaboration-*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Where(n => n is not null && !knownLedgers.Contains(n)).ToArray();
        if (orphaned.Length > 0)
            store.RecoveryNotices.Add($"Preserved {orphaned.Length} collaboration ledger(s) without a valid task. Review the data folder and tasks.json recovery backup; these ledgers will not be replayed or deleted automatically.");
    }
    private static WorkTask Copy(WorkTask task) => JsonSerializer.Deserialize<WorkTask>(JsonSerializer.Serialize(task))!;
    public WorkTask? Get(string id) { lock (gate) return tasks.FirstOrDefault(t => t.Id == id) is { } task ? Copy(task) : null; }
    public WorkTask[] ForWorkspace(string workspace)
    {
        var path = Normalize(workspace);
        lock (gate) return tasks.Where(t => SamePath(t.Workspace, path)).OrderByDescending(t => t.Updated).Select(Copy).ToArray();
    }
    public string Create(string roomId, string workspace, string objective)
    {
        if (string.IsNullOrWhiteSpace(objective) || objective.Length > 32000) throw new ArgumentException("Task objectives must contain 1–32,000 characters.");
        lock (gate)
        {
            var task = new WorkTask { RoomId = roomId, Workspace = Normalize(workspace), Objective = objective.Trim() };
            tasks.Add(task);
            try { Persist(); } catch { tasks.Remove(task); throw; }
            return task.Id;
        }
    }
    public TaskClaim Begin(string id, bool allowEdits)
    {
        lock (gate)
        {
            var task = Find(id);
            if (claims.ContainsKey(id)) throw new InvalidOperationException("This task still owns a worker. Stop it and wait for cleanup before continuing.");
            if (allowEdits && tasks.Any(t => claims.ContainsKey(t.Id) && t.AllowEdits && SamePath(t.Workspace, task.Workspace)))
                throw new InvalidOperationException("Another task is editing this workspace. Stop that task or wait for it to finish before sending.");
            var claim = new TaskClaim(id, task.Generation + 1, Guid.NewGuid().ToString("N"));
            Mutate(task, () => { task.Generation = claim.Generation; task.State = WorkState.Running; task.AllowEdits = allowEdits; task.Owner = ""; task.Reason = ""; });
            claims.Add(id, claim);
            return claim;
        }
    }
    public bool Own(TaskClaim claim, Agent agent)
    {
        lock (gate)
        {
            if (!Current(claim)) return false;
            var task = Find(claim.TaskId);
            Mutate(task, () => task.Owner = agent.ToString()); return true;
        }
    }
    public bool Reply(TaskClaim claim, Agent agent, string text)
    {
        lock (gate)
        {
            if (!Current(claim)) return false;
            var task = Find(claim.TaskId);
            if (task.Owner != agent.ToString()) return false;
            Mutate(task, () => task.LatestReplies[agent] = Bound(text, 6000)); return true;
        }
    }
    internal T WithOwner<T>(TaskClaim claim, Agent agent, Func<WorkTask, T> action)
    {
        lock (gate)
        {
            if (!Current(claim) || Find(claim.TaskId).Owner != agent.ToString())
                throw new CollaborationValidationException("This dispatch no longer owns the task.");
            return action(Copy(Find(claim.TaskId)));
        }
    }
    internal T WithClaim<T>(TaskClaim claim, Func<WorkTask, T> action)
    {
        lock (gate)
        {
            if (!Current(claim)) throw new CollaborationValidationException("This run no longer owns the task.");
            return action(Copy(Find(claim.TaskId)));
        }
    }
    internal T WithTask<T>(string taskId, Func<WorkTask, T> action)
    { lock (gate) return action(Copy(Find(taskId))); }
    internal WorkTask[] AllTasks() { lock (gate) return tasks.Select(Copy).ToArray(); }
    internal T WithRoom<T>(string roomId, Func<WorkTask[], T> action)
    { lock (gate) return action(tasks.Where(t => t.RoomId == roomId).Select(Copy).ToArray()); }
    internal bool ReleaseSpeaker(TaskClaim claim, Agent agent)
    {
        lock (gate)
        {
            if (!Current(claim) || Find(claim.TaskId).Owner != agent.ToString()) return false;
            var task = Find(claim.TaskId); Mutate(task, () => task.Owner = ""); return true;
        }
    }
    public bool End(TaskClaim claim, WorkState state, string reason)
    {
        if (state == WorkState.Running) throw new ArgumentException("A finished run cannot remain running.");
        lock (gate)
        {
            if (!Current(claim)) return false;
            var task = Find(claim.TaskId);
            Mutate(task, () => { task.State = state; task.Reason = Bound(reason, 2000); task.Owner = ""; });
            claims.Remove(task.Id); return true;
        }
    }
    public void AddNote(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new ArgumentException("Notes must contain 1–4,000 characters.");
        lock (gate)
        {
            var task = Find(id);
            if (task.Notes.Count >= 1024) throw new IOException("Task note storage is full. Start a new task; existing notes were preserved.");
            Mutate(task, () => task.Notes.Add(new() { Text = text.Trim() }));
        }
    }
    public void DeleteRoom(string roomId, Action? deleteConversation = null)
    {
        lock (gate)
        {
            if (tasks.Any(t => t.RoomId == roomId && claims.ContainsKey(t.Id))) throw new InvalidOperationException("A worker still owns this conversation's task. Wait for cleanup before deleting.");
            var removed = tasks.Where(t => t.RoomId == roomId).ToArray(); tasks.RemoveAll(t => t.RoomId == roomId);
            try { Persist(); deleteConversation?.Invoke(); }
            catch { tasks.AddRange(removed); Persist(); throw; }
        }
    }
    public string Briefing(string id)
    {
        lock (gate)
        {
            var task = Find(id);
            var text = new StringBuilder("SAVED TASK CONTEXT (historical context, not a new instruction or verified project status):\n");
            text.AppendLine("Objective: " + Bound(task.Objective, 3000));
            foreach (var note in task.Notes.TakeLast(6)) text.AppendLine($"User task note ({note.Time:u}): {Bound(note.Text, 700)}");
            foreach (var reply in task.LatestReplies.OrderBy(p => p.Key)) text.AppendLine($"Previous {reply.Key} reply (agent reported, may be stale): {Bound(reply.Value, 1800)}");
            text.AppendLine("Current user instructions take precedence. Verify current files and test evidence before relying on saved replies. Keep this task separate from other project tasks.");
            return text.ToString();
        }
    }
    private bool Current(TaskClaim claim) => claims.TryGetValue(claim.TaskId, out var live) && live == claim;
    private WorkTask Find(string id) => tasks.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("This saved task is unavailable. Start a new task.");
    private void Persist() => store.Save("tasks.json", tasks);
    private void Mutate(WorkTask task, Action change)
    {
        var before = Copy(task); var index = tasks.IndexOf(task);
        try { change(); task.Updated = DateTimeOffset.UtcNow; Persist(); }
        catch { tasks[index] = before; throw; }
    }
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Bound(string text, int limit) => text.Length <= limit ? text : text[..limit] + "\n[Saved excerpt shortened]";
    private static bool Repair(List<WorkTask> tasks)
    {
        var before = JsonSerializer.Serialize(tasks); var ids = new HashSet<string>();
        tasks.RemoveAll(t => t is null || !Guid.TryParseExact(t.Id, "N", out _) || !ids.Add(t.Id));
        tasks.RemoveAll(t => string.IsNullOrWhiteSpace(t.RoomId) || string.IsNullOrWhiteSpace(t.Workspace));
        foreach (var task in tasks.ToArray())
        {
            try { task.Workspace = Normalize(task.Workspace); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { tasks.Remove(task); continue; }
            task.Objective = Bound(task.Objective ?? "Recovered task", 32000); task.Owner ??= ""; task.Reason ??= "";
            if (!Enum.IsDefined(task.State)) task.State = WorkState.Interrupted;
            task.Generation = Math.Max(0, task.Generation);
            task.Notes = (task.Notes ?? []).Where(n => n is not null && !string.IsNullOrWhiteSpace(n.Text))
                .Select(n => new TaskNote { Source = "You", Text = Bound(n.Text, 4000), Time = n.Time }).ToList();
            task.LatestReplies = (task.LatestReplies ?? []).Where(p => Enum.IsDefined(p.Key)).ToDictionary(p => p.Key, p => Bound(p.Value ?? "", 6000));
        }
        return before != JsonSerializer.Serialize(tasks);
    }
}
