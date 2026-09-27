using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using AIHub.Core;

internal static class StructuredWireFixture
{
    public static void Complete(string body) => CompleteAsync(body).GetAwaiter().GetResult();
    static async Task CompleteAsync(string body)
    {
        var name = Environment.GetEnvironmentVariable("AIHUB_COLLAB_PIPE");
        if (string.IsNullOrEmpty(name)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(Environment.GetEnvironmentVariable("AIHUB_COLLAB_TOKEN"));
        if (await reader.ReadLineAsync(timeout.Token) != "OK") throw new IOException("Fixture MCP handshake failed");
        async Task<JsonNode> Call(string method, JsonNode args)
        {
            await writer.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = args }.ToJsonString());
            var result = JsonNode.Parse((await reader.ReadLineAsync(timeout.Token))!)!;
            if (result["error"] is not null) throw new IOException(result.ToJsonString());
            return result["result"]!;
        }
        await Call("initialize", Json.Obj(new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "desktop-fixture", version = "1" } }));
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var context = await Call("tools/call", Json.Obj(new { name = "get_task_context", arguments = new { } }));
        if (context.Bool("isError")) throw new IOException(context.ToJsonString());
        var task = JsonNode.Parse(context["content"]![0]!.Str("text"))!;
        var incoming = task["incoming_message"]?["envelope"]?.Str("message_id");
        var handoff = task.Str("sender") == "Codex" && task["participants"]!.AsArray().Count == 2 && string.IsNullOrEmpty(incoming) && body.Contains("\"answers\"");
        var submission = CollaborationRoutingTests.Message(handoff ? "handoff" : "status", Agent.Claude, incoming);
        submission["content"]!["scope"] = Json.Obj(new { files = Array.Empty<string>(), focus = Array.Empty<string>() });
        var result = await Call("tools/call", new JsonObject { ["name"] = "submit_message", ["arguments"] = submission });
        if (result.Bool("isError")) throw new IOException(result.ToJsonString());
    }
}
