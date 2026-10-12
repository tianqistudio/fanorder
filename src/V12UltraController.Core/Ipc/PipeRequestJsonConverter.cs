using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12UltraController.Core.Ipc;

/// <summary>
/// Keeps the wire envelope unambiguous. System.Text.Json's web defaults accept
/// case-insensitive names, ignore unknown members, and resolve duplicate members
/// by taking the last value; none of those behaviors are appropriate for a local
/// control boundary containing attended-operation flags.
/// </summary>
internal sealed class PipeRequestJsonConverter : JsonConverter<PipeRequest>
{
    private static readonly string[] ExpectedProperties =
    [
        "apiVersion",
        "requestId",
        "operation",
        "payload",
    ];

    public override PipeRequest Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A named-pipe request must be a JSON object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? requestId = null;
        string? operation = null;
        JsonElement payload = default;
        var apiVersionPresent = false;
        var apiVersion = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                RequireAllProperties(seen);
                return new PipeRequest(
                    apiVersion,
                    requestId!,
                    operation!,
                    payload);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A named-pipe request contains an invalid object member.");
            }

            var propertyName = reader.GetString()
                ?? throw new JsonException("A named-pipe request contains an invalid property name.");
            if (!seen.Add(propertyName))
            {
                throw new JsonException($"A named-pipe request contains duplicate '{propertyName}' properties.");
            }

            EnsureCanonicalPropertyName(propertyName);
            if (!reader.Read())
            {
                throw new JsonException($"The named-pipe request property '{propertyName}' has no value.");
            }

            switch (propertyName)
            {
                case "apiVersion":
                    if (!TryReadExactInteger(ref reader, out apiVersion))
                    {
                        throw new JsonException(
                            "apiVersion must be a JSON integer literal within the Int32 range.");
                    }

                    apiVersionPresent = true;
                    break;

                case "requestId":
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("requestId must be a JSON string.");
                    }

                    requestId = reader.GetString();
                    break;

                case "operation":
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("operation must be a JSON string.");
                    }

                    operation = reader.GetString();
                    break;

                case "payload":
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        throw new JsonException("payload must be a JSON object.");
                    }

                    using (var document = JsonDocument.ParseValue(ref reader))
                    {
                        payload = document.RootElement.Clone();
                    }

                    break;
            }
        }

        throw new JsonException("A named-pipe request object was not terminated.");

        void RequireAllProperties(HashSet<string> properties)
        {
            foreach (var expected in ExpectedProperties)
            {
                if (!properties.Contains(expected))
                {
                    throw new JsonException($"A named-pipe request is missing '{expected}'.");
                }
            }

            if (!apiVersionPresent)
            {
                throw new JsonException("A named-pipe request is missing 'apiVersion'.");
            }
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        PipeRequest value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("apiVersion", value.ApiVersion);
        writer.WriteString("requestId", value.RequestId);
        writer.WriteString("operation", value.Operation);
        writer.WritePropertyName("payload");
        value.Payload.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static void EnsureCanonicalPropertyName(string propertyName)
    {
        if (ExpectedProperties.Contains(propertyName, StringComparer.Ordinal))
        {
            return;
        }

        if (ExpectedProperties.Contains(propertyName, StringComparer.OrdinalIgnoreCase))
        {
            throw new JsonException(
                $"The named-pipe request property '{propertyName}' uses non-canonical casing.");
        }

        throw new JsonException($"Unknown named-pipe request property '{propertyName}'.");
    }

    private static bool TryReadExactInteger(ref Utf8JsonReader reader, out int value)
    {
        value = default;
        if (reader.TokenType != JsonTokenType.Number)
        {
            return false;
        }

        byte[]? rented = null;
        ReadOnlySpan<byte> literal;
        if (reader.HasValueSequence)
        {
            rented = reader.ValueSequence.ToArray();
            literal = rented;
        }
        else
        {
            literal = reader.ValueSpan;
        }

        if (literal.IsEmpty)
        {
            return false;
        }

        var start = literal[0] == (byte)'-' ? 1 : 0;
        if (start == literal.Length)
        {
            return false;
        }

        for (var index = start; index < literal.Length; index++)
        {
            if (literal[index] is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return reader.TryGetInt32(out value);
    }
}
