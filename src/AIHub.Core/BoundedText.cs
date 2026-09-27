using System.Runtime.CompilerServices;
using System.Text;

namespace AIHub.Core;

public static class BoundedText
{
    public const int MaxFrameCharacters = 4 * 1024 * 1024;
    public static async IAsyncEnumerable<string> LinesAsync(TextReader reader, int limit,
        [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new char[8192]; var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    yield return line.ToString(); line.Clear();
                }
                else
                {
                    if (line.Length >= limit) throw new IOException($"Agent output exceeded the {limit:N0}-character frame limit. This turn was stopped.");
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }
    public static async Task<string> ReadAsync(TextReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[8192]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + read > limit) throw new IOException("Process output exceeded its capture limit.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
