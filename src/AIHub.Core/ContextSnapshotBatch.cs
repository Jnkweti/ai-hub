using System.Text.Json;

namespace AIHub.Core;

// One instance per context rendering operation. Never share this cache across command or review boundaries.
internal sealed class ContextSnapshotBatch(string workspace, CancellationToken token,
    Func<string, CollaborationScope, CancellationToken, Task<ProjectSnapshot>>? capture = null)
{
    private readonly Dictionary<string, Task<ProjectSnapshot>> captures = [];
    internal Task<ProjectSnapshot> Capture(CollaborationScope scope)
    {
        token.ThrowIfCancellationRequested();
        var key = JsonSerializer.Serialize(scope.Files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (!captures.TryGetValue(key, out var result))
            captures[key] = result = (capture ?? CollaborationStore.CaptureContextAsync)(workspace, scope, token);
        return result;
    }
}
