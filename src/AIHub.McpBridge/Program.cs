using System.IO.Pipes;
using System.Text;
using AIHub.Core;

// A transport bridge only: no task store, filesystem tools, model calls, or authority supplied by tool arguments.
var pipeName = Environment.GetEnvironmentVariable("AIHUB_COLLAB_PIPE");
var credential = Environment.GetEnvironmentVariable("AIHUB_COLLAB_TOKEN");
Environment.SetEnvironmentVariable("AIHUB_COLLAB_TOKEN", null);
if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(credential))
{ Console.Error.WriteLine("AI Hub did not supply an active collaboration connection."); return 2; }
using var life = new CancellationTokenSource();
try
{
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(10000, life.Token);
    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync(credential); credential = null;
    if (await reader.ReadLineAsync(life.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) != "OK")
        throw new IOException("The host declined the collaboration connection.");
    Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
    async Task Pump(TextReader input, TextWriter output)
    {
        await foreach (var line in BoundedText.LinesAsync(input, CollaborationMcpHost.FrameLimit, life.Token))
        {
            await output.WriteLineAsync(line.AsMemory(), life.Token);
            await output.FlushAsync(life.Token);
        }
    }
    // Console.In's synchronized reader may block synchronously while waiting for more stdin.
    // Isolate that wait so it cannot prevent the host EOF pump or bridge shutdown from running.
    var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, false, 4096, leaveOpen: true);
    var inbound = Task.Factory.StartNew(() => Pump(input, writer), CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    var outbound = Pump(reader, Console.Out);
    var finished = await Task.WhenAny(inbound, outbound);
    await finished;
    await life.CancelAsync();
    pipe.Close();
    return 0;
}
catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
{ Console.Error.WriteLine("AI Hub collaboration connection closed or unavailable (" + ex.GetType().Name + ")."); return 1; }
