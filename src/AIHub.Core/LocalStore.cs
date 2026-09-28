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
                Replace(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    private static void Replace(string temp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            RejectLink(path);
            try { File.Move(temp, path, true); return; }
            catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 6 && File.Exists(path) &&
                ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xffff) is 5 or 32 or 33)
            {
                // External readers and antivirus can briefly hold the destination without delete sharing.
                // Keep the original and prepared temp intact; permanent failures still roll back normally.
                Thread.Sleep(Math.Min(10 << attempt, 100));
            }
        }
    }
    // Rooms: a small index plus one transcript file per room, so a large conversation is rewritten only when it changed.
    public const string RoomsFile = "rooms.json";
    public static string TranscriptFile(string roomId) => "room-" + roomId + ".json";
    private readonly Dictionary<string, int> transcriptCounts = [];
    /// <summary>Loads the rooms index and each room's transcript; a legacy single-file rooms.json is split on first load.</summary>
    public List<Room> LoadRooms()
    {
        var rooms = Load(RoomsFile, () => new List<Room>(), SavedStateRepair.Rooms);
        var migrate = false;
        foreach (var room in rooms)
        {
            var name = TranscriptFile(room.Id);
            var exists = File.Exists(DataPath(name));
            if (exists) room.Messages = Load(name, () => new List<SavedMessage>(), SavedStateRepair.Messages);
            else if (room.Messages.Count > 0) migrate = true;
            lock (sync) transcriptCounts[room.Id] = exists ? room.Messages.Count : -1;
        }
        if (migrate) SaveRooms(rooms, rooms.Select(r => r.Id).ToHashSet());
        return rooms;
    }
    /// <summary>Writes the index every time, and a room's transcript only when it was marked dirty, has no file yet, or its message count changed.</summary>
    public void SaveRooms(IReadOnlyList<Room> rooms, IReadOnlySet<string> dirty)
    {
        lock (sync)
        {
            Save(RoomsFile, rooms.Select(r => r.Header()).ToList());
            foreach (var room in rooms)
            {
                if (!dirty.Contains(room.Id) && transcriptCounts.TryGetValue(room.Id, out var known) && known == room.Messages.Count) continue;
                Save(TranscriptFile(room.Id), room.Messages);
                transcriptCounts[room.Id] = room.Messages.Count;
            }
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
            var remaining = rooms.Where(r => r.Id != roomId).Select(r => r.Header()).ToList();
            Save(RoomsFile, remaining);
            try { File.Delete(activityPath); }
            catch
            {
                // Keep the conversation available if its activity log could not be removed.
                Save(RoomsFile, rooms.Select(r => r.Header()).ToList());
                throw;
            }
            try { File.Delete(DataPath(TranscriptFile(roomId))); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // An orphaned transcript is harmless.
            transcriptCounts.Remove(roomId);
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
