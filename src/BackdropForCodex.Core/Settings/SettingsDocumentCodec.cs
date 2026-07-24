using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackdropForCodex.Core.Settings;

internal sealed class SettingsDocumentCodec
{
    private readonly JsonSerializerOptions _serializerOptions;

    internal SettingsDocumentCodec(JsonSerializerOptions? serializerOptions)
    {
        _serializerOptions = CreateSerializerOptions(serializerOptions);
    }

    internal SettingsV1 DeserializeVersion1(byte[] documentBytes)
    {
        var settings = JsonSerializer.Deserialize<SettingsV1>(
            documentBytes,
            _serializerOptions);
        if (settings is null)
        {
            throw new JsonException("The settings document is empty.");
        }

        return settings.Snapshot();
    }

    internal SettingsV2 DeserializeVersion2(byte[] documentBytes)
    {
        var settings = JsonSerializer.Deserialize<SettingsV2>(
            documentBytes,
            _serializerOptions);
        if (settings is null)
        {
            throw new JsonException("The settings document is empty.");
        }

        return settings.Snapshot();
    }

    internal byte[] SerializeVersion2(SettingsV2 settings)
    {
        byte[] documentBytes;
        try
        {
            documentBytes = JsonSerializer.SerializeToUtf8Bytes(
                settings,
                _serializerOptions);
        }
        catch (JsonException exception)
        {
            throw new SettingsRepositoryException(
                "Settings could not be serialized.",
                exception);
        }

        if (documentBytes.LongLength > SettingsRepository.MaximumDocumentBytes)
        {
            throw new SettingsRepositoryException(
                "The settings document exceeds the size limit.");
        }

        return documentBytes;
    }

    internal static int ReadSchemaVersion(byte[] documentBytes)
    {
        using var document = JsonDocument.Parse(
            documentBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The settings document must be an object.");
        }

        JsonElement? versionElement = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    "schemaVersion",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (versionElement is not null)
            {
                throw new JsonException("The settings schema version is duplicated.");
            }

            versionElement = property.Value;
        }

        if (versionElement is null)
        {
            return SettingsV1.CurrentSchemaVersion;
        }

        if (versionElement.Value.ValueKind != JsonValueKind.Number ||
            !versionElement.Value.TryGetInt32(out var schemaVersion) ||
            schemaVersion < SettingsV1.CurrentSchemaVersion)
        {
            throw new JsonException("The settings schema version is invalid.");
        }

        return schemaVersion;
    }

    private static JsonSerializerOptions CreateSerializerOptions(
        JsonSerializerOptions? serializerOptions)
    {
        var options = serializerOptions is null
            ? new JsonSerializerOptions(JsonSerializerDefaults.Web)
            : new JsonSerializerOptions(serializerOptions);

        options.WriteIndented = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.MaxDepth = 64;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
