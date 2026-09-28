using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AIHub.Core;

/// <summary>The provider process ended while a turn was in flight. Recoverable: the same native session can be resumed in a new process.</summary>
public sealed class ProviderProcessException(string message) : IOException(message);

public sealed class JsonProcess : IAsyncDisposable
{
    private Process? process;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource life = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> pending = new();
    private long sequence;
    private int disposed, failed, started;
    private Task outputTask = Task.CompletedTask, errorTask = Task.CompletedTask;
    public event Action<JsonObject>? Message;
    public event Action<string>? Diagnostic;
    public event Action<Exception>? Failed;
    public bool Alive
    {
        get { try { return disposed == 0 && !life.IsCancellationRequested && process is { HasExited: false }; } catch (InvalidOperationException) { return false; } }
    }

    public static string FindExecutable(string configured, string command)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured)) throw new FileNotFoundException($"Executable not found: {configured}");
            return Path.GetFullPath(configured);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates = command switch
        {
            "codex" => [Path.Combine(home, "AppData", "Local", "Programs", "OpenAI", "Codex", "bin", "codex.exe")],
            "claude" => [Path.Combine(home, ".local", "bin", "claude.exe")],
            _ => []
        };
        foreach (var path in candidates.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                     .Select(p => p.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
                     .Select(p => Path.Combine(p, command + (OperatingSystem.IsWindows() ? ".exe" : "")))))
            if (File.Exists(path)) return Path.GetFullPath(path);
        throw new FileNotFoundException($"{command} is not installed or on PATH. Set its executable in Settings.");
    }

    public void Start(string executable, IEnumerable<string> args, string directory, IReadOnlyDictionary<string, string>? environment = null)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("This process has already been started.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Choose an existing project folder.");
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        // Nested-session marker must not make a separately hosted Claude session look recursive.
        info.Environment.Remove("CLAUDECODE");
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        process = new Process { StartInfo = info };
        process.Start();
        outputTask = ReadOutputAsync();
        errorTask = ReadErrorsAsync();
    }
    private async Task ReadOutputAsync()
    {
        try
        {
            await foreach (var line in BoundedText.LinesAsync(process!.StandardOutput, BoundedText.MaxFrameCharacters, life.Token).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject message;
                try { message = JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException("Expected a JSON object."); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
                { Diagnostic?.Invoke("Unrecognized CLI output: " + line[..Math.Min(line.Length, 8192)]); continue; }
                var response = message["type"]?.ToString() == "control_response" ? message["response"] : message;
                var id = response?["request_id"]?.ToString() ?? response?["id"]?.ToString();
                var isResponse = message["method"] is null && message["type"]?.ToString() != "control_request";
                if (isResponse && id is not null && pending.TryRemove(id, out var waiter))
                {
                    if (response?["error"] is { } error) waiter.TrySetException(new InvalidOperationException(error.ToString()));
                    else waiter.TrySetResult(response?["result"]?.DeepClone() ?? response?["response"]?.DeepClone() ?? new JsonObject());
                }
                else Message?.Invoke(message);
            }
            if (!life.IsCancellationRequested)
                throw new ProviderProcessException("The agent process closed its output. Check sign-in and Activity for details.");
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private async Task ReadErrorsAsync()
    {
        try
        {
            await foreach (var line in BoundedText.LinesAsync(process!.StandardError, BoundedText.MaxFrameCharacters, life.Token).ConfigureAwait(false))
                if (!string.IsNullOrWhiteSpace(line)) Diagnostic?.Invoke(line);
        }
        catch (Exception ex) { Fail(ex); }
    }
    private void Fail(Exception ex)
    {
        if (life.IsCancellationRequested || Interlocked.Exchange(ref failed, 1) != 0) return;
        life.Cancel();
        foreach (var item in pending.Values) item.TrySetException(ex);
        pending.Clear();
        Failed?.Invoke(ex);
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public async Task WriteAsync(JsonNode message, CancellationToken token = default)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Alive) throw new IOException("Agent process is not running.");
            await process!.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        finally { writer.Release(); }
    }
    public async Task<JsonNode> RequestAsync(string method, object parameters, CancellationToken token, bool claude = false)
    {
        var id = Interlocked.Increment(ref sequence).ToString();
        var waiter = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        try
        {
            var message = claude
                ? Json.Obj(new { type = "control_request", request_id = id, request = parameters })
                : Json.Obj(new { id, method, @params = parameters });
            await WriteAsync(message, token).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        life.Cancel();
        foreach (var item in pending.Values) item.TrySetCanceled();
        pending.Clear();
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            process.Dispose();
        }
    }
}
