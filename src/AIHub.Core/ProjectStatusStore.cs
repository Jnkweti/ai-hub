using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Core;

public sealed class ProjectStatusRecord
{
    public int Version { get; set; } = 1;
    public string RunId { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string SourceRoomId { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Agent Inspector { get; set; }
    public Agent? Reviewer { get; set; }
    public ProjectStatusReport Inspection { get; set; } = new();
    public ProjectStatusReport? Review { get; set; }
    public string Scope { get; set; } = "";
    public Dictionary<Agent, string> ProviderUsage { get; set; } = [];
}

public sealed class ProjectStatusStore(string dataDirectory)
{
    public static string NormalizeWorkspace(string workspace) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
    public string WorkspaceDirectory(string workspace)
    {
        var normalized = NormalizeWorkspace(workspace);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(dataDirectory, "project-status", key);
    }
    // The OS owns the claim for the lifetime of this handle, including across app instances.
    // A crashed process releases it. Never delete the claim file: that can split ownership.
    public async Task<Claim> ClaimAsync(string workspace, CancellationToken token)
    {
        var directory = WorkspaceDirectory(workspace); Directory.CreateDirectory(directory);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new Claim(directory, new FileStream(Path.Combine(directory, "inspection.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11) { await Task.Delay(150, token); }
        }
    }
    public async Task ForgetAsync(string workspace, string? sourceRoomId, CancellationToken token)
    {
        if (!Directory.Exists(WorkspaceDirectory(workspace))) return;
        await using var claim = await ClaimAsync(workspace, token);
        if (sourceRoomId is null || claim.Read()?.SourceRoomId == sourceRoomId) claim.Delete();
    }
    public sealed class Claim(string directory, FileStream handle) : IAsyncDisposable
    {
        public ProjectStatusRecord? Read()
        {
            var path = Path.Combine(directory, "report.json");
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 256 * 1024) return null;
            try { return JsonSerializer.Deserialize<ProjectStatusRecord>(File.ReadAllText(path)); }
            catch (JsonException) { return null; }
        }
        public void Write(ProjectStatusRecord report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(directory, "report.json");
            var temporary = Path.Combine(directory, "report-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(report));
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void Delete() => File.Delete(Path.Combine(directory, "report.json"));
        public ValueTask DisposeAsync() => handle.DisposeAsync();
    }
}
