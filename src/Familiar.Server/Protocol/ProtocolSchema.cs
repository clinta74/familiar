using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;
using Familiar.Server.Auth;
using Familiar.Server.Calendars;
using Familiar.Server.Conversation;
using Familiar.Server.Devices;
using Familiar.Server.Familiars;
using Familiar.Server.Hooks;
using Familiar.Server.Memories;
using Familiar.Server.Notifications;
using Familiar.Server.Scheduling;
using Familiar.Server.Tools;

namespace Familiar.Server.Protocol;

/// <summary>
/// What the clients and the server say to each other (MOBILE.md §6): every event on the conversation
/// stream with its payload, and the request and response bodies the web client and the phone apps
/// share. The C# records are the source of truth; <see cref="ProtocolSchema"/> writes them out as
/// <c>protocol/familiar.schema.json</c>, and the TypeScript and Kotlin types are generated from that
/// (<c>client/scripts/protocol.mjs</c>). A test keeps the file current.
/// </summary>
public static class ProtocolCatalog
{
    /// <summary>The stream's events, in the order they're documented: the name and its payload (null: none).</summary>
    public static readonly IReadOnlyList<(string Name, Type? Payload)> Events =
    [
        (EventTypes.Ready, typeof(ReadyEvent)),
        (EventTypes.State, typeof(StateEvent)),
        (EventTypes.Message, typeof(MessageView)),
        (EventTypes.ReplyStart, typeof(ReplyStartEvent)),
        (EventTypes.ReplyDelta, typeof(ReplyDeltaEvent)),
        (EventTypes.ReplyEnd, typeof(ReplyEndEvent)),
        (EventTypes.ReplyTruncated, typeof(ReplyTruncatedEvent)),
        (EventTypes.Expression, typeof(ExpressionEvent)),
        (EventTypes.Gesture, typeof(GestureEvent)),
        (EventTypes.Sound, typeof(SoundEvent)),
        (EventTypes.Audio, typeof(AudioEvent)),
        (EventTypes.AudioEnd, typeof(AudioEndEvent)),
        (EventTypes.Error, typeof(ErrorEvent)),
        (EventTypes.ToolStarted, typeof(ToolStartedEvent)),
        (EventTypes.ToolFinished, typeof(ToolFinishedEvent)),
        (EventTypes.ToolConfirm, typeof(ToolConfirmEvent)),
        (EventTypes.ToolConfirmed, typeof(ToolConfirmedEvent)),
        (EventTypes.ToolInvoke, typeof(ToolInvokeEvent)),
        (EventTypes.Jobs, typeof(JobsEvent)),
        (EventTypes.Notify, typeof(NotifyEvent)),
        (EventTypes.Cleared, null),
        (EventTypes.Resync, null),
    ];

    /// <summary>The REST API's request and response bodies, for the web client and the phone apps.</summary>
    public static readonly IReadOnlyList<Type> Bodies =
    [
        // Accounts, sessions and devices.
        typeof(RegisterRequest),
        typeof(LoginRequest),
        typeof(ChangePasswordRequest),
        typeof(AccountResponse),
        typeof(DeviceLoginRequest),
        typeof(DeviceLoginResponse),
        typeof(DeviceSummary),
        typeof(ErrorResponse),

        // User administration.
        typeof(AdminUserView),
        typeof(CreateUserRequest),
        typeof(UpdateUserRequest),
        typeof(ResetPasswordRequest),

        // The conversation.
        typeof(SendMessageRequest),
        typeof(InterruptRequest),
        typeof(ConfirmRequest),
        typeof(ToolResultRequest),

        // The familiar: who it is, how it sounds and looks.
        typeof(FamiliarView),
        typeof(VoicesView),
        typeof(AvatarView),

        // Calendars.
        typeof(CalendarView),
        typeof(EventView),
        typeof(AddFeedRequest),
        typeof(DeviceCalendarUpload),

        // Memories.
        typeof(MemoryView),
        typeof(MemoryRequest),

        // Timers, reminders and notifications.
        typeof(JobView),
        typeof(NotificationView),
        typeof(SubscribeRequest),
        typeof(UnsubscribeRequest),
        typeof(PushKeyResponse),

        // Connections: hook tokens, webhooks, MCP servers, Home Assistant.
        typeof(HookTokenView),
        typeof(NewHookTokenView),
        typeof(NameRequest),
        typeof(AnnounceRequest),
        typeof(WebhookView),
        typeof(NewWebhookView),
        typeof(WebhookRequest),
        typeof(WebhookTestResult),
        typeof(McpServerView),
        typeof(McpServerRequest),
        typeof(McpToolsRequest),
        typeof(Home.HomeAssistantView),
        typeof(Home.HomeDeviceView),
        typeof(Home.HomeAssistantRequest),
    ];
}

/// <summary>
/// The protocol as a JSON Schema (draft 2020-12), from the records themselves: property names, types
/// and nullability as <see cref="ConversationHub.Json"/> (the same options as the HTTP API) writes
/// them, and descriptions from the XML documentation. Every object, enum and the event list are named
/// definitions, so the generators can turn each into a type. Deterministic: the same records give the
/// same text.
/// </summary>
public static class ProtocolSchema
{
    public static string Generate()
    {
        // The stream's options as they are, with the reflection metadata they resolve on first use.
        var builder = new Builder(new JsonSerializerOptions(ConversationHub.Json) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
        // Each event: its name in EventTypes (what the clients call the constant) and its payload.
        var constants = typeof(EventTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name);
        var events = new JsonObject();
        foreach (var (name, payload) in ProtocolCatalog.Events)
        {
            events[name] = new JsonObject
            {
                ["constant"] = constants[name],
                ["payload"] = payload is null ? null : builder.Define(payload),
            };
        }

        foreach (var body in ProtocolCatalog.Bodies)
        {
            builder.Define(body);
        }

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Familiar protocol",
            ["description"] =
                "The conversation stream's events and the request and response bodies the clients share. " +
                "Generated from the server's records (src/Familiar.Server/Protocol/ProtocolSchema.cs) - don't edit; " +
                "the TypeScript and Kotlin types are generated from this (client/scripts/protocol.mjs).",
            ["x-events"] = events,
            ["$defs"] = builder.Definitions(),
        };

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .ReplaceLineEndings("\n") + "\n";
    }

    private sealed class Builder(JsonSerializerOptions options)
    {
        private readonly SortedDictionary<string, JsonNode> _definitions = new(StringComparer.Ordinal);
        private readonly NullabilityInfoContext _nullability = new();
        private readonly Docs _docs = new();

        public JsonObject Definitions()
        {
            var defs = new JsonObject();
            foreach (var (name, node) in _definitions)
            {
                defs[name] = node;
            }

            return defs;
        }

        /// <summary>Adds the type (and what it refers to) and returns its definition's name.</summary>
        public string Define(Type type)
        {
            var name = type.Name;
            if (_definitions.ContainsKey(name))
            {
                return name;
            }

            if (type.IsEnum)
            {
                _definitions[name] = Describe(new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray([.. Enum.GetNames(type).Select(n => (JsonNode)JsonNamingPolicy.CamelCase.ConvertName(n))]),
                }, _docs.Type(type));
                return name;
            }

            // Placeholder first, for types that refer back to themselves.
            _definitions[name] = new JsonObject();
            var info = options.GetTypeInfo(type);
            var properties = new JsonObject();
            var required = new JsonArray();

            // A record's parameters with defaults may be left out of a request: those are optional.
            var parameters = type.GetConstructors().MaxBy(c => c.GetParameters().Length)?.GetParameters() ?? [];
            foreach (var property in info.Properties)
            {
                var member = property.AttributeProvider as PropertyInfo;
                var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
                    || (member is not null && !property.PropertyType.IsValueType
                        && _nullability.Create(member).ReadState == NullabilityState.Nullable);
                var schema = Schema(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, member);

                // A string with fixed values (the model a familiar is drawn as): those values.
                if (member?.GetCustomAttribute<System.ComponentModel.DataAnnotations.AllowedValuesAttribute>() is { } allowed)
                {
                    schema["enum"] = new JsonArray([.. allowed.Values.Select(v => (JsonNode?)v?.ToString())]);
                }
                if (nullable)
                {
                    schema = OrNull(schema);
                }

                var parameter = parameters.FirstOrDefault(p => string.Equals(p.Name, member?.Name, StringComparison.OrdinalIgnoreCase));
                if (parameter is { HasDefaultValue: true })
                {
                    schema["default"] = parameter.DefaultValue switch
                    {
                        null => null,
                        bool b => b,
                        string s => s,
                        int i => i,
                        double d => d,
                        var other => other.ToString(),
                    };
                }
                else
                {
                    required.Add((JsonNode)property.Name);
                }

                properties[property.Name] = Describe(schema, _docs.Property(type, member?.Name ?? property.Name));
            }

            _definitions[name] = Describe(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
            }, _docs.Type(type));
            return name;
        }

        private JsonObject Schema(Type type, PropertyInfo? member)
        {
            if (type == typeof(string))
            {
                return new() { ["type"] = "string" };
            }

            if (type == typeof(Guid))
            {
                return new() { ["type"] = "string", ["format"] = "uuid" };
            }

            if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
            {
                return new() { ["type"] = "string", ["format"] = "date-time" };
            }

            if (type == typeof(bool))
            {
                return new() { ["type"] = "boolean" };
            }

            if (type == typeof(int) || type == typeof(long))
            {
                return new() { ["type"] = "integer" };
            }

            if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            {
                return new() { ["type"] = "number" };
            }

            if (type == typeof(JsonObject))
            {
                // Free-form: a tool's arguments or result.
                return new() { ["type"] = "object" };
            }

            if (type.IsEnum || IsRecord(type))
            {
                return new() { ["$ref"] = $"#/$defs/{Define(type)}" };
            }

            if (ElementType(type) is { } element)
            {
                var items = Schema(Nullable.GetUnderlyingType(element) ?? element, null);
                return new() { ["type"] = "array", ["items"] = items };
            }

            throw new NotSupportedException($"{type} has no protocol schema (member {member?.DeclaringType?.Name}.{member?.Name}).");
        }

        private static bool IsRecord(Type type) => type.IsClass && type.Namespace?.StartsWith("Familiar", StringComparison.Ordinal) == true;

        private static Type? ElementType(Type type)
        {
            if (type.IsArray)
            {
                return type.GetElementType();
            }

            return type.IsGenericType && type.GetGenericArguments() is [var element]
                && typeof(IEnumerable<>).MakeGenericType(element).IsAssignableFrom(type)
                ? element
                : null;
        }

        private static JsonObject OrNull(JsonObject schema)
        {
            if (schema["$ref"] is not null)
            {
                return new() { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
            }

            schema["type"] = new JsonArray(schema["type"]!.GetValue<string>(), "null");
            return schema;
        }

        private static JsonObject Describe(JsonObject schema, string? description)
        {
            if (description is { Length: > 0 })
            {
                var described = new JsonObject { ["description"] = description };
                foreach (var (key, value) in schema.ToList())
                {
                    schema.Remove(key);
                    described[key] = value;
                }

                return described;
            }

            return schema;
        }
    }

    /// <summary>The XML documentation of the server's (and domain's) types: summaries, and params for records.</summary>
    private sealed class Docs
    {
        private readonly Dictionary<string, XElement> _members = [];

        public Docs()
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Familiar.*.xml"))
            {
                foreach (var member in XDocument.Load(file).Descendants("member"))
                {
                    if (member.Attribute("name")?.Value is { } name)
                    {
                        _members[name] = member;
                    }
                }
            }
        }

        public string? Type(Type type) => Text(Member($"T:{type.FullName}")?.Element("summary"));

        public string? Property(Type type, string name) =>
            Text(Member($"P:{type.FullName}.{name}")?.Element("summary"))
            ?? Text(Member($"T:{type.FullName}")?.Elements("param").FirstOrDefault(p => p.Attribute("name")?.Value == name));

        private XElement? Member(string name) => _members.GetValueOrDefault(name);

        /// <summary>The text, on one line: <c>see cref</c>s become the name they point to.</summary>
        private static string? Text(XElement? element)
        {
            if (element is null)
            {
                return null;
            }

            var parts = element.Nodes().Select(node => node switch
            {
                XText text => text.Value,
                XElement { Name.LocalName: "see" or "seealso" } see =>
                    (see.Attribute("cref")?.Value ?? see.Attribute("langword")?.Value ?? "").Split('.', ':')[^1],
                XElement other => other.Value,
                _ => "",
            });
            var text = string.Join("", parts);
            text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length == 0 ? null : text;
        }
    }
}
