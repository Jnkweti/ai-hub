namespace AIHub.Core;

// Providers may withdraw a question while its UI is still waiting for an answer.
internal sealed class InputRequestLifetimes
{
    private readonly object gate = new();
    private readonly Dictionary<string, CancellationTokenSource> requests = [];
    public CancellationTokenSource Begin(string id, CancellationToken token)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(id) || requests.ContainsKey(id))
                throw new IOException("The agent sent a missing or duplicate input request ID. This turn was stopped.");
            if (requests.Count >= 16) throw new IOException("The agent exceeded the pending input request limit. This turn was stopped.");
            var source = CancellationTokenSource.CreateLinkedTokenSource(token);
            requests.Add(id, source); return source;
        }
    }
    public void End(string id)
    { lock (gate) if (requests.Remove(id, out var source)) source.Dispose(); }
    public void Cancel(string id)
    { lock (gate) if (requests.TryGetValue(id, out var source)) source.Cancel(); }
    public void CancelAll()
    { lock (gate) foreach (var source in requests.Values.ToArray()) source.Cancel(); }
}
