using AIHub.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class AuditTests
{
    private static void Check(bool condition, string failure) { if (!condition) throw new Exception(failure); }
    private static string Fixture() { var path = Path.Combine(AppContext.BaseDirectory, "audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Clean(string path)
    {
        Check(Path.GetDirectoryName(Path.GetFullPath(path)) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), "Unsafe fixture cleanup");
        Directory.Delete(path, true);
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("a delayed old pause cannot clear clients or overwrite a replacement conversation turn", async () =>
        {
            var old = new SlowCleanupAgent(true); var next = new SlowCleanupAgent(false); var count = 0;
            await using var hub = new HubCoordinator(_ => ++count == 1 ? old : next) { AutoExchange = false };
            var states = new System.Collections.Concurrent.ConcurrentQueue<string>(); hub.State += states.Enqueue;
            await hub.SubmitAsync("Plan", "Both"); await old.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await hub.SubmitAsync("Replacement", "Codex"); await next.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            old.Release.TrySetResult(); await old.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Task.Delay(100);
            Check(!states.Any(s => s.StartsWith("Paused")), "Old run overwrote the new run's state");
            await hub.StopAsync(); Check(next.Disposing.Task.IsCompleted, "Old cleanup discarded the new client without disposing it");
        });
        await test("malformed saved records recover valid messages and preserve the original backup", () =>
        {
            var root = Fixture();
            try
            {
                const string original = """[null,{"Id":"../outside","Title":null,"Workspace":null,"CodexContext":null,"ClaudeContext":{"MessageIds":null},"Messages":[null,{"Id":null,"Text":"Keep this message","Input":{"Title":null,"Options":[null,{"Label":null,"Description":null}],"Status":500}}]},{"Id":"duplicate","Messages":null},{"Id":"duplicate"}]""";
                File.WriteAllText(Path.Combine(root, "rooms.json"), original);
                var store = new LocalStore(root);
                var rooms = store.Load("rooms.json", () => new List<Room>(), SavedStateRepair.Rooms);
                Check(rooms.Count == 3 && rooms.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3, "Room IDs remain invalid");
                Check(rooms[0].Messages.Single().Text == "Keep this message" && rooms[1].Messages.Count == 0, "Valid history lost");
                Check(rooms[0].Messages[0].Input!.Status == InputStatus.Cancelled && rooms[0].ClaudeContext.MessageIds.Length == 0, "Nested invalid data survived");
                Check(store.RecoveryNotices.Count == 1 && File.ReadAllText(Directory.GetFiles(root, "rooms.json.unreadable-*").Single()) == original, "Original data not preserved");
                store.Save("rooms.json", rooms);
                Check(!SavedStateRepair.Rooms(rooms), "Repair was not idempotent");
                var settings = JsonSerializer.Deserialize<HubSettings>("""{"Workspace":null,"CodexPath":null,"ClaudeModel":null,"MaxAutoRounds":-7,"StatusInspector":999}""")!;
                Check(SavedStateRepair.Settings(settings) && settings.CodexPath == "" && settings.MaxAutoRounds == 1, "Settings did not recover");
            }
            finally { Clean(root); }
            return Task.CompletedTask;
        });
        await test("storage rejects traversal and preserves existing data on a failed atomic save", () =>
        {
            var root = Fixture();
            try
            {
                var store = new LocalStore(root); store.Save("rooms.json", new[] { "preserved" });
                File.WriteAllText(Path.Combine(root, "rooms.json.tmp"), "unrelated sentinel");
                store.Save("rooms.json", new[] { "updated" });
                Check(File.ReadAllText(Path.Combine(root, "rooms.json.tmp")) == "unrelated sentinel", "Predictable temporary file was overwritten");
                foreach (var name in new[] { "../outside.json", "..\\outside.json", "file:stream", "C:\\outside.json" })
                {
                    try { store.Save(name, "bad"); throw new Exception("Unsafe filename accepted"); } catch (IOException) { }
                }
                var path = Path.Combine(root, "rooms.json");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                { try { store.Save("rooms.json", new[] { "lost" }); throw new Exception("Locked save succeeded"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                Check(store.Load("rooms.json", () => Array.Empty<string>()).Single() == "updated", "Failed save corrupted previous data");
                Check(Directory.GetFiles(root, "rooms.json.*.tmp").Length == 0, "Failed save leaked a temporary file");
            }
            finally { Clean(root); }
            return Task.CompletedTask;
        });
        await test("instance lease excludes a second process and recovers after process termination", async () =>
        {
            var root = Fixture();
            try
            {
                var first = AppInstanceLease.TryAcquire(root)!;
                using (var child = Start("--instance-lease", root))
                {
                    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "BUSY", "Second writer entered");
                    await child.WaitForExitAsync();
                }
                first.Dispose();
                using (var child = Start("--instance-lease", root))
                {
                    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ACQUIRED", "Released lease remained locked");
                    try { Check(AppInstanceLease.TryAcquire(root) is null, "Parent bypassed child lease"); }
                    finally { child.Kill(true); await child.WaitForExitAsync(); }
                }
                using var recovered = AppInstanceLease.TryAcquire(root);
                Check(recovered is not null, "Crashed process left a stale lock");
            }
            finally { Clean(root); }
        });
        await test("executable discovery ignores empty and relative PATH entries", () =>
        {
            var root = Fixture(); var oldDirectory = Environment.CurrentDirectory; var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                var command = "hub-audit-tool"; File.WriteAllText(Path.Combine(root, command + ".exe"), "untrusted");
                Environment.CurrentDirectory = root; Environment.SetEnvironmentVariable("PATH", ";.;relative");
                try { JsonProcess.FindExecutable("", command); throw new Exception("Project executable selected"); } catch (FileNotFoundException) { }
                Environment.SetEnvironmentVariable("PATH", root);
                Check(JsonProcess.FindExecutable("", command) == Path.Combine(root, command + ".exe"), "Absolute PATH entry ignored");
            }
            finally { Environment.CurrentDirectory = oldDirectory; Environment.SetEnvironmentVariable("PATH", oldPath); Clean(root); }
            return Task.CompletedTask;
        });
        await test("bounded line reader preserves fragmented frames and rejects oversized unterminated output", async () =>
        {
            var lines = new List<string>();
            await foreach (var line in BoundedText.LinesAsync(new StringReader(new string('a', 9000) + "\r\nnext\nlast"), 10000, default)) lines.Add(line);
            Check(lines.SequenceEqual(new[] { new string('a', 9000), "next", "last" }), "Framed output changed");
            try { await foreach (var _ in BoundedText.LinesAsync(new StringReader(new string('x', 10001)), 10000, default)) { } throw new Exception("Oversized line accepted"); }
            catch (IOException) { }
        });
        foreach (var mode in new[] { "oversize", "stderr", "invalid" })
            await test("JSON transport handles " + mode + " output without hanging and disposes twice safely", async () =>
            {
                await using var wire = new JsonProcess();
                wire.Start(Environment.ProcessPath!, ["--wire-audit", mode], AppContext.BaseDirectory);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var result = await wire.RequestAsync("test", new { }, timeout.Token);
                    Check(mode == "invalid" && result.Str("ok") == "yes", "Unexpected response");
                }
                catch (IOException) when (mode != "invalid") { }
                await wire.DisposeAsync(); await wire.DisposeAsync(); Check(!wire.Alive, "Disposed process is alive");
            });
        await test("disposal after a failed process launch is safe", async () =>
        {
            await using var wire = new JsonProcess();
            try { wire.Start(Path.Combine(AppContext.BaseDirectory, "missing-audit.exe"), [], AppContext.BaseDirectory); throw new Exception("Missing process started"); }
            catch (System.ComponentModel.Win32Exception) { }
            await wire.DisposeAsync();
        });
        await test("oversized and empty provider choices are rejected before creating desktop controls", () =>
        {
            foreach (var options in new[] { Enumerable.Range(0, 65).Select(i => new { label = "Choice " + i, description = "" }).ToArray(), new[] { new { label = "", description = "" } } })
            {
                try { Approval.Question(Agent.Codex, Json.Obj(new { header = "Test", question = "Choose", options })); throw new Exception("Invalid choices accepted"); }
                catch (IOException) { }
            }
            return Task.CompletedTask;
        });
        foreach (var agent in new[] { Agent.Codex, Agent.Claude })
        foreach (var requestMode in new[] { "duplicate", "flood" })
            await test(agent + " " + requestMode + " input requests fail promptly and cancel pending questions", async () =>
            {
                await using IAgentClient client = agent == Agent.Codex
                    ? new CodexClient(new(AppContext.BaseDirectory, false, Executable: Environment.ProcessPath!))
                    : new ClaudeClient(new(AppContext.BaseDirectory, false, Executable: Environment.ProcessPath!));
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.RequestApproval = async (_, token) =>
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                    return new(false);
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.SendAsync("input-fixture-" + requestMode, timeout.Token); throw new Exception("Invalid requests accepted"); }
                catch (IOException ex) { Check(ex.Message.Contains(requestMode == "duplicate" ? "duplicate" : "limit"), "Wrong failure"); }
                await cancelled.Task.WaitAsync(timeout.Token);
            });
        await test("deep empty project trees are bounded and cannot certify an incomplete fingerprint", async () =>
        {
            var root = Fixture();
            try
            {
                var deep = root; for (var i = 0; i < 70; i++) { deep = Path.Combine(deep, "d"); Directory.CreateDirectory(deep); }
                var snapshot = await ProjectSnapshot.CaptureAsync(root, default);
                Check(!snapshot.Reusable && snapshot.Limitation.Contains("depth"), "Deep tree was certified as complete");
            }
            finally { Clean(root); }
        });
    }
    private static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    public static async Task WireFixture(string mode)
    {
        var request = JsonNode.Parse((await Console.In.ReadLineAsync())!)!;
        if (mode is "oversize" or "stderr")
        {
            var output = mode == "stderr" ? Console.Error : Console.Out;
            await output.WriteAsync(new string('x', BoundedText.MaxFrameCharacters + 1)); await output.FlushAsync();
        }
        else
        {
            Console.WriteLine("null\n[]\ninvalid json");
            Console.WriteLine(Json.Obj(new { id = request.Str("id"), result = new { ok = "yes" } }).ToJsonString());
        }
        await Task.Delay(Timeout.Infinite);
    }
    private sealed class SlowCleanupAgent(bool slow) : IAgentClient
    {
        public Agent Agent => Agent.Codex;
        public string? SessionId => "audit-session";
        public event Action<AgentEvent>? Event { add { } remove { } }
        public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
        {
            Started.TrySetResult();
            if (!slow) await Task.Delay(Timeout.Infinite, token);
            return new("Waiting for your input.", SessionId);
        }
        public async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult(); if (slow) await Release.Task;
            Disposed.TrySetResult();
        }
    }
}
