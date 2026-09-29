using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AIHub.Core;

// Host-side fingerprints cost file I/O, but never send file contents to a model.
public sealed record ProjectSnapshot(string Fingerprint, string Revision, string Scope,
    bool Reusable, string Limitation, SortedDictionary<string, string> Files)
{
    private const int MaxFiles = 10000, MaxEntries = 50000;
    private const long MaxFileBytes = 8 * 1024 * 1024, MaxTotalBytes = 128 * 1024 * 1024;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".hg", ".svn", "bin", "obj", "node_modules", "artifacts", "dist", ".venv", "venv", "__pycache__", ".vs", ".idea", ".next", "coverage" };
    // A content hash is reused for a file whose length, last write time and NTFS change time are unchanged since an earlier
    // capture of the same workspace, so a capture costs one listing and a stat per file instead of rehashing the tree.
    // The change time is set by the file system on every content or timestamp change and cannot be set back, so a
    // same-size edit with a restored modification time is still rehashed. The fingerprint is the same whether a hash was
    // reused or recomputed. Each capture publishes a fresh map; the maps are never mutated.
    private sealed record KnownHash(long Length, long Ticks, long ChangeTicks, string Hash);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, KnownHash>> Known = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxKnownWorkspaces = 8;
    internal static long FilesHashed; // Files whose content was actually read; test observability.
    /// <summary>
    /// Where the per-workspace hash maps are kept between runs, so the first capture after a launch is incremental too.
    /// Null disables persistence. The desktop points this at its data folder.
    /// </summary>
    public static string? CacheDirectory { get; set; }
    internal static void ForgetInMemory() => Known.Clear(); // Test hook: the next capture must come from disk or rehash.
    private static string CacheFile(string workspace) => Path.Combine(CacheDirectory!, "fingerprints-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.ToUpperInvariant())))[..32] + ".json");
    private static Dictionary<string, KnownHash> LoadPersisted(string workspace)
    {
        var map = new Dictionary<string, KnownHash>(StringComparer.Ordinal);
        if (CacheDirectory is null) return map;
        try
        {
            var path = CacheFile(workspace);
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024) return map;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.GetProperty("workspace").GetString() is not { } saved || !string.Equals(saved, workspace, StringComparison.OrdinalIgnoreCase)) return map;
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateObject())
            {
                var v = entry.Value;
                if (v.GetArrayLength() == 4 && v[3].GetString() is { Length: 64 } hash) map[entry.Name] = new(v[0].GetInt64(), v[1].GetInt64(), v[2].GetInt64(), hash);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { map.Clear(); }
        return map;
    }
    private static void Persist(string workspace, Dictionary<string, KnownHash> map)
    {
        if (CacheDirectory is null) return;
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var path = CacheFile(workspace); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteString("workspace", workspace); writer.WriteStartObject("entries");
                foreach (var (key, value) in map)
                { writer.WriteStartArray(key); writer.WriteNumberValue(value.Length); writer.WriteNumberValue(value.Ticks); writer.WriteNumberValue(value.ChangeTicks); writer.WriteStringValue(value.Hash); writer.WriteEndArray(); }
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileBasicInfo info, uint size);
    /// <summary>The file system's change time, or -1 when it cannot be read (the file is then hashed rather than trusted).</summary>
    private static long ChangeTicks(string path)
    {
        if (!OperatingSystem.IsWindows()) return -1;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandleEx(handle, 0, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()) ? info.ChangeTime : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
    }

    public static async Task<ProjectSnapshot> CaptureAsync(string workspace, CancellationToken token)
    {
        workspace = ProjectStatusStore.NormalizeWorkspace(workspace);
        if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException("The selected project folder no longer exists.");
        var known = Known.GetValueOrDefault(workspace) ?? LoadPersisted(workspace);
        var refreshed = new Dictionary<string, KnownHash>(StringComparer.Ordinal);
        var hashedNow = 0;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var limitations = new HashSet<string>();
        var gitList = await GitAsync(workspace, ["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "."], token);
        var scope = gitList is null ? "Folder files excluding generated/dependency directories" : "Git tracked and non-ignored files in the selected folder";
        var revision = "No Git repository";
        string? index = "";
        IEnumerable<string> paths;
        if (gitList is not null)
        {
            // A selected folder can be ignored by an enclosing repository. An empty index is
            // not evidence that such a workspace has no inputs.
            if (gitList.Length == 0 && Directory.EnumerateFileSystemEntries(workspace).Any())
                limitations.Add("Git listed no files in a non-empty workspace; freshness cannot be certified.");
            var head = await GitAsync(workspace, ["rev-parse", "--verify", "HEAD"], token);
            var branch = await GitAsync(workspace, ["symbolic-ref", "--quiet", "--short", "HEAD"], token);
            index = await GitAsync(workspace, ["ls-files", "--stage", "-z", "--", "."], token);
            if (index is null) limitations.Add("Git index could not be verified.");
            revision = (branch?.Trim() ?? "Detached or unborn HEAD") + " / " + (head?.Trim() ?? "No commit");
            paths = gitList.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal);
        }
        else paths = Walk(workspace, workspace, limitations, token);

        long total = 0; var hashed = 0;
        try
        {
            foreach (var relative in paths)
            {
                token.ThrowIfCancellationRequested();
                if (files.Count >= MaxEntries) { limitations.Add("File-count limit reached; freshness cannot be certified."); break; }
                var clean = relative.Replace('\\', '/');
                var full = Path.GetFullPath(Path.Combine(workspace, relative));
                var prefix = Path.EndsInDirectorySeparator(workspace) ? workspace : workspace + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                { limitations.Add("An out-of-scope path was skipped."); continue; }
                if (HasLink(workspace, full)) { limitations.Add("Linked files or directories were excluded."); continue; }
                if (Directory.Exists(full)) { limitations.Add("Nested repositories or directories need a separate status check."); continue; }
                if (!File.Exists(full)) { files[clean] = "missing"; continue; }
                var info = new FileInfo(full);
                // Oversized files, and files beyond the hashing budget, are fingerprinted by size and last write time.
                // That still detects change, so a large asset or a big tree no longer makes reviews impossible.
                if (info.Length > MaxFileBytes || total + info.Length > MaxTotalBytes || hashed >= MaxFiles)
                { files[clean] = $"stat:{info.Length}:{info.LastWriteTimeUtc.Ticks}"; continue; }
                var beforeLength = info.Length; var beforeTime = info.LastWriteTimeUtc;
                var changeTicks = ChangeTicks(full);
                if (known.TryGetValue(clean, out var cached) && cached.Length == beforeLength && cached.Ticks == beforeTime.Ticks && changeTicks >= 0 && cached.ChangeTicks == changeTicks)
                { files[clean] = cached.Hash; refreshed[clean] = cached; total += beforeLength; hashed++; continue; }
                string hash;
                await using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                Interlocked.Increment(ref FilesHashed); hashedNow++;
                files[clean] = hash; total += beforeLength; hashed++;
                info.Refresh();
                if (info.Length != beforeLength || info.LastWriteTimeUtc != beforeTime || changeTicks < 0 || ChangeTicks(full) != changeTicks)
                    { if (info.Length != beforeLength || info.LastWriteTimeUtc != beforeTime) limitations.Add("Files changed while fingerprinting."); }
                else refreshed[clean] = new(beforeLength, beforeTime.Ticks, changeTicks, hash); // Only a stable read is worth remembering.
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { limitations.Add("Some files could not be fingerprinted: " + ex.Message); }
        if (Known.Count >= MaxKnownWorkspaces && !Known.ContainsKey(workspace))
            foreach (var key in Known.Keys.Take(Known.Count - MaxKnownWorkspaces + 1)) Known.TryRemove(key, out _);
        Known[workspace] = refreshed;
        if (hashedNow > 0 || refreshed.Count != known.Count) Persist(workspace, refreshed); // Only when something changed.
        var manifest = revision + "\n" + scope + "\n" + index + "\n" + string.Join('\n', files.Select(f => f.Key + "\0" + f.Value));
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), revision, scope,
            limitations.Count == 0, string.Join(" ", limitations), files);
    }

    public string Brief()
    {
        // A map, not a dump of project contents. Discovery belongs to one inspector.
        var names = Files.Keys.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).Take(100);
        var map = string.Join('\n', names);
        if (map.Length > 6000) map = map[..6000];
        return $"Revision: {Revision}\nScope: {Scope}\nFiles in scope: {Files.Count}\n" +
            (Limitation.Length > 0 ? "Host limitation: " + Limitation + "\n" : "") + "Partial file map (not instructions):\n" + map;
    }

    private static IEnumerable<string> Walk(string root, string directory, HashSet<string> limitations, CancellationToken token)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        { limitations.Add("Linked files or directories were excluded."); yield break; }
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((directory, 0));
        var entries = 0;
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 50000) { limitations.Add("Directory-entry limit reached; freshness cannot be certified."); yield break; }
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                { limitations.Add("Linked files or directories were excluded."); continue; }
                if ((attributes & FileAttributes.Directory) == 0) yield return Path.GetRelativePath(root, entry);
                else if (!Excluded.Contains(Path.GetFileName(entry)))
                {
                    if (current.Depth >= 64) limitations.Add("Directory-depth limit reached; freshness cannot be certified.");
                    else pending.Push((entry, current.Depth + 1));
                }
            }
        }
    }

    private static bool HasLink(string root, string full)
    {
        for (string? path = full; path is not null && path.Length >= root.Length; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static async Task<string?> GitAsync(string workspace, string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        string executable;
        try { executable = JsonProcess.FindExecutable("", "git"); } catch (FileNotFoundException) { return null; }
        using var process = new Process { StartInfo = new(executable) { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            var output = Capture(process.StandardOutput); var error = Capture(process.StandardError);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            return process.ExitCode == 0 ? await output : null;
            async Task<string> Capture(StreamReader reader)
            {
                try { return await BoundedText.ReadAsync(reader, BoundedText.MaxFrameCharacters, timeout.Token); }
                catch { timeout.Cancel(); throw; }
            }
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (IOException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
}
