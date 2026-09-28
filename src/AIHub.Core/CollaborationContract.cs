using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIHub.Core;

public enum DeliveryState { Accepted, Pending, Delivered, Answered, Canceled, Interrupted }
public sealed record CollaborationScope(string[] Files, string[] Focus);
public sealed record ContextAssignment(Agent Agent, CollaborationScope Scope);
public sealed record CollaborationFinding(string Id, string Severity, string File, int Line,
    string Explanation, string Disposition, string[] EvidenceRefs);
public sealed record CollaborationContent(string Type, string Summary, CollaborationScope Scope,
    string[] EvidenceRefs, string[] Blockers, string? Recipient = null, string? RequestedAction = null,
    string? ReplyTo = null, string? Status = null, CollaborationFinding[]? Findings = null,
    ContextAssignment[]? Assignments = null);
public sealed record CollaborationSubmission(string SchemaVersion, string IdempotencyKey, CollaborationContent Content);
public sealed record CollaborationEnvelope(string SchemaVersion, string MessageId, string TaskId, string RoomId,
    Agent Sender, Agent? Recipient, DateTimeOffset CreatedAt, long Generation, string DispatchId,
    long Sequence, string? ProviderSessionId, string? SnapshotRef);
public sealed record CollaborationMessage(CollaborationEnvelope Envelope, CollaborationContent Content, DeliveryState State);
public sealed class CollaborationValidationException(string message) : Exception(message);

/// <summary>The published schema and runtime validation share one definition. Host authority is never an input field.</summary>
public static class CollaborationContract
{
    public const string Version = "1.0";
    public const int MaxBytes = 32768;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonObject Schema = BuildSchema();
    public static JsonObject SubmissionSchema() => Schema.DeepClone().AsObject();
    public static JsonObject EmptySchema() => Object(new JsonObject(), []);
    private static JsonArray Strings(params string[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static JsonObject Text(int max, string pattern = @"\S") => new()
    { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = max, ["pattern"] = pattern };
    private static JsonObject Choice(params string[] values) => new() { ["type"] = "string", ["enum"] = Strings(values) };
    private static JsonObject ArrayOf(JsonNode item, int max) => new() { ["type"] = "array", ["maxItems"] = max, ["items"] = item };
    private static JsonObject Object(JsonObject properties, string[] required) => new()
    { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = Strings(required) };
    private static JsonObject Identifier() => Text(128, "^[A-Za-z0-9_-]+$");
    private static JsonObject RelativePath() => Text(512, @"^(?!/)(?!.*(?:^|/)\.{1,2}(?:/|$))[^\\:\x00-\x1F<>""|?*]+$");
    private static JsonObject BuildSchema()
    {
        var variants = new JsonArray();
        foreach (var type in new[] { "handoff", "review_request", "review_result", "question", "status", "context_request" })
        {
            var p = new JsonObject
            {
                ["type"] = new JsonObject { ["const"] = type }, ["summary"] = Text(2000),
                ["scope"] = Object(new() { ["files"] = ArrayOf(RelativePath(), 32), ["focus"] = ArrayOf(Text(500), 16) }, ["files", "focus"]),
                ["evidence_refs"] = ArrayOf(Identifier(), 32), ["blockers"] = ArrayOf(Text(1000), 16)
            };
            var required = new List<string> { "type", "summary", "scope", "evidence_refs", "blockers" };
            if (type is not ("status" or "context_request")) { p["recipient"] = Choice("Codex", "Claude"); required.Add("recipient"); }
            if (type == "context_request")
            {
                p["assignments"] = ArrayOf(Object(new() {
                    ["agent"] = Choice("Codex", "Claude"),
                    ["scope"] = Object(new() { ["files"] = ArrayOf(RelativePath(), 16), ["focus"] = ArrayOf(Text(500), 8) }, ["files", "focus"])
                }, ["agent", "scope"]), 2);
                required.Add("assignments");
            }
            if (type is "handoff" or "review_request" or "question")
            { p["requested_action"] = Text(4000); required.Add("requested_action"); }
            p["reply_to"] = Identifier();
            if (type == "review_result")
            {
                required.AddRange(["reply_to", "findings"]);
                p["findings"] = ArrayOf(Object(new()
                {
                    ["id"] = Identifier(), ["severity"] = Choice("info", "low", "medium", "high", "critical"),
                    ["file"] = RelativePath(), ["line"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10000000 },
                    ["explanation"] = Text(2000), ["disposition"] = Choice("open", "addressed", "checked", "disputed"),
                    ["evidence_refs"] = ArrayOf(Identifier(), 16)
                }, ["id", "severity", "file", "line", "explanation", "disposition", "evidence_refs"]), 32);
            }
            if (type == "status")
            { p["status"] = Choice("progress", "blocked", "assignment_complete", "no_further_contribution"); required.Add("status"); }
            variants.Add(Object(p, required.ToArray()));
        }
        var schema = Object(new()
        {
            ["schema_version"] = new JsonObject { ["const"] = Version },
            ["idempotency_key"] = Identifier(),
            ["content"] = new JsonObject { ["oneOf"] = variants }
        }, ["schema_version", "idempotency_key", "content"]);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["title"] = "AI Hub collaboration submission 1.0";
        schema["description"] = "Agent content only. Maximum encoded submission: 32768 UTF-8 bytes. Host validates ownership, references and workspace paths separately.";
        return schema;
    }
    public static CollaborationSubmission Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new CollaborationValidationException("Submission exceeds 32768 UTF-8 bytes.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateKeys(document.RootElement);
            var node = JsonNode.Parse(json)!;
            Validate(Schema, node, "$");
            // JSON Schema treats 12.0 as an integer; normalize it for System.Text.Json's Int32 reader.
            if (node["content"]?["findings"] is JsonArray findings)
                foreach (var finding in findings) finding!["line"] = decimal.ToInt32(finding["line"]!.GetValue<decimal>());
            return node.Deserialize<CollaborationSubmission>(JsonOptions)!;
        }
        catch (JsonException) { throw new CollaborationValidationException("Expected valid JSON with at most 16 nesting levels."); }
    }
    public static void ValidateEmpty(JsonNode? value) => Validate(EmptySchema(), value, "arguments");
    // Only evaluates the fixed schema vocabulary constructed above, never a caller-supplied schema.
    private static void Validate(JsonObject schema, JsonNode? value, string path)
    {
        void Fail(string reason) => throw new CollaborationValidationException(path + ": " + reason);
        if (schema["oneOf"] is JsonArray variants)
        {
            var matches = 0; string last = "Unknown message type.";
            foreach (var variant in variants.OfType<JsonObject>())
            {
                try { Validate(variant, value, path); matches++; }
                catch (CollaborationValidationException ex)
                {
                    if (value is JsonObject body && body.Str("type") == variant["properties"]?["type"]?["const"]?.ToString()) last = ex.Message;
                }
            }
            if (matches != 1) Fail(last);
            return;
        }
        if (schema["const"] is { } constant && !JsonNode.DeepEquals(constant, value)) Fail("Unsupported constant or version.");
        if (schema["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, value))) Fail("Unsupported value.");
        switch (schema.Str("type"))
        {
            case "object":
                if (value is not JsonObject obj) { Fail("Expected object."); return; }
                var props = schema["properties"]!.AsObject();
                foreach (var key in schema["required"]!.AsArray()) if (!obj.ContainsKey(key!.ToString())) Fail("Missing " + key + ".");
                foreach (var prop in obj)
                {
                    if (props[prop.Key] is not JsonObject child) { Fail("Unknown field " + prop.Key + "."); return; }
                    Validate(child, prop.Value, path + "." + prop.Key);
                }
                break;
            case "array":
                if (value is not JsonArray array) { Fail("Expected array."); return; }
                if (array.Count > schema["maxItems"]!.GetValue<int>()) Fail("Too many items.");
                for (var i = 0; i < array.Count; i++) Validate(schema["items"]!.AsObject(), array[i], path + "[" + i + "]");
                break;
            case "string":
                if (value is not JsonValue v || !v.TryGetValue<string>(out var s)) { Fail("Expected string."); return; }
                var length = s.EnumerateRunes().Count();
                if (schema["minLength"] is { } min && length < min.GetValue<int>() || schema["maxLength"] is { } max && length > max.GetValue<int>()) Fail("Invalid string length.");
                if (schema["pattern"] is { } pattern && !Regex.IsMatch(s, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) Fail("Invalid string format.");
                break;
            case "integer":
                if (value is not JsonValue n || !n.TryGetValue<decimal>(out var number) || number != decimal.Truncate(number) ||
                    number < schema["minimum"]!.GetValue<int>() || number > schema["maximum"]!.GetValue<int>()) Fail("Invalid integer.");
                break;
        }
    }
    internal static void RejectDuplicateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
            { if (!keys.Add(p.Name)) throw new CollaborationValidationException("Duplicate JSON field: " + p.Name); RejectDuplicateKeys(p.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) RejectDuplicateKeys(item);
    }
    public static bool CanTransition(DeliveryState from, DeliveryState to) => (from, to) switch
    {
        (DeliveryState.Accepted, DeliveryState.Pending or DeliveryState.Canceled or DeliveryState.Interrupted) => true,
        (DeliveryState.Pending, DeliveryState.Delivered or DeliveryState.Canceled or DeliveryState.Interrupted) => true,
        (DeliveryState.Delivered, DeliveryState.Answered or DeliveryState.Canceled or DeliveryState.Interrupted) => true,
        _ => false
    };
}
