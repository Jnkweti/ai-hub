using System.Diagnostics;
using System.Text;

namespace AIHub.Core;

// One writer per data folder. The OS releases this lease after a crash.
public sealed class AppInstanceLease : IDisposable
{
    private readonly FileStream stream;
    private AppInstanceLease(FileStream stream) => this.stream = stream;
    public static AppInstanceLease? TryAcquire(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(Path.GetFullPath(directory), "instance.lock");
        LocalStore.RejectLink(path);
        FileStream stream;
        try { stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
        try
        {
            using var current = Process.GetCurrentProcess();
            var owner = $"{Environment.ProcessId}\n{current.StartTime.ToUniversalTime().Ticks}";
            stream.SetLength(0); stream.Write(Encoding.UTF8.GetBytes(owner)); stream.Flush(true);
            return new(stream);
        }
        catch { stream.Dispose(); throw; }
    }
    public static Process? ReadOwner(string directory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            if (!int.TryParse(reader.ReadLine(), out var pid) || !long.TryParse(reader.ReadLine(), out var start)) return null;
            var owner = Process.GetProcessById(pid);
            if (owner.StartTime.ToUniversalTime().Ticks == start && owner.MainModule?.FileName == Environment.ProcessPath) return owner;
            owner.Dispose(); return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    public void Dispose() => stream.Dispose();
}
