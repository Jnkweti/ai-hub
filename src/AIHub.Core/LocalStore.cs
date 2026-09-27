using System.Text.Json;

namespace AIHub.Core;

public sealed class LocalStore
{
    public string DirectoryPath { get; }
    private readonly object sync = new();
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    public List<string> RecoveryNotices { get; } = [];
    public LocalStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHub"));
        Directory.CreateDirectory(DirectoryPath);
    }
    public T Load<T>(string name, Func<T> fallback, Func<T, bool>? repair = null, bool failOnInvalid = false)
    {
        lock (sync)
        {
            var path = DataPath(name);
            if (!File.Exists(path)) return fallback();
            try
            {
                var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
                if (value is null)
                {
                    if (failOnInvalid) throw new IOException("Saved state is null; file preserved: " + name);
                    BackUp(path); return fallback();
                }
                if (repair?.Invoke(value) == true) BackUp(path);
                return value;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
            {
                if (failOnInvalid) throw new IOException("Saved state cannot be parsed; file preserved and task blocked: " + name, ex);
                BackUp(path);
                return fallback();
            }
        }
    }
    public void Save<T>(string name, T value)
    {
        lock (sync)
        {
            var path = DataPath(name);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, value, Format);
                    stream.Flush(flushToDisk: true);
                }
                RejectLink(path);
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public void AppendActivity(string room, AgentEvent value)
    {
        lock (sync)
            File.AppendAllText(ActivityPath(room),
                JsonSerializer.Serialize(new { time = DateTimeOffset.Now, value.Agent, value.Kind, value.Text, value.ItemId, value.Detail }) + "\n");
    }
    internal void Delete(string name) { lock (sync) File.Delete(DataPath(name)); }
    public void AppendHandoff(string room, string from, string to, string text)
    {
        lock (sync)
            File.AppendAllText(ActivityPath(room),
                JsonSerializer.Serialize(new { time = DateTimeOffset.Now, kind = "handoff", from, to, text }) + "\n");
    }
    public void DeleteRoom(IReadOnlyCollection<Room> rooms, string roomId)
    {
        lock (sync)
        {
            // Validate before touching either file. IDs are filenames, never paths or patterns.
            var activityPath = ActivityPath(roomId);
            if (rooms.Count(r => r.Id == roomId) != 1)
                throw new InvalidOperationException("The conversation could not be identified uniquely.");
            var remaining = rooms.Where(r => r.Id != roomId).ToList();
            Save("rooms.json", remaining);
            try { File.Delete(activityPath); }
            catch
            {
                // Keep the conversation available if its activity log could not be removed.
                Save("rooms.json", rooms);
                throw;
            }
        }
    }
    public string ActivityPath(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId) || roomId.Length > 128 ||
            roomId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new IOException("The conversation has an invalid storage identifier; no files were removed.");
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, "activity-" + roomId + ".jsonl"));
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(DirectoryPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The activity log must be inside AI Hub's data folder.");
        RejectLink(path);
        return path;
    }
    private string DataPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name is "." or ".." ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new IOException("Data filenames must stay inside AI Hub's data folder.");
        var path = Path.Combine(DirectoryPath, name);
        RejectLink(path);
        return path;
    }
    internal static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("AI Hub will not write data through a linked file: " + Path.GetFileName(path));
    }
    private void BackUp(string path)
    {
        var backup = path + ".unreadable-" + Guid.NewGuid().ToString("N");
        File.Copy(path, backup, false);
        RecoveryNotices.Add("Recovered " + Path.GetFileName(path) + "; original saved as " + Path.GetFileName(backup));
    }
}
