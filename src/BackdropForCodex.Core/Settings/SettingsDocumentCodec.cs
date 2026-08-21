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

        // Materialization alone does not enforce schema invariants. Snapshot validates the
        // document and detaches collection values before they escape the codec.
        return settings.Snapshot();
    }

    internal SettingsV2 DeserializeVersion2(byte[] documentBytes)
    {
        RejectVersion3MediaFieldsInVersion2(documentBytes);
        var settings = JsonSerializer.Deserialize<SettingsV2>(
            documentBytes,
            _serializerOptions);
        if (settings is null)
        {
            throw new JsonException("The settings document is empty.");
        }

        // V2 snapshots additionally isolate nested profiles, media references, and mappings that
        // a caller-supplied converter could otherwise back with mutable collections.
        return settings.Snapshot();
    }

    internal SettingsV3 DeserializeVersion3(byte[] documentBytes)
    {
        var settings = JsonSerializer.Deserialize<SettingsV3>(
            documentBytes,
            _serializerOptions);
        if (settings is null)
        {
            throw new JsonException("The settings document is empty.");
        }

        return settings.Snapshot();
    }

    internal byte[] SerializeVersion3(SettingsV3 settings)
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

        // Enforce the limit against the exact UTF-8 payload that the repository will publish.
        if (documentBytes.LongLength > SettingsRepository.MaximumDocumentBytes)
        {
            throw new SettingsRepositoryException(
                "The settings document exceeds the size limit.");
        }

        return documentBytes;
    }

    private static void RejectVersion3MediaFieldsInVersion2(byte[] documentBytes)
    {
        using var document = JsonDocument.Parse(
            documentBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        JsonElement? mediaCatalog = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    "mediaCatalog",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (mediaCatalog is not null)
            {
                throw new JsonException(
                    "The version-two media catalog is duplicated.");
            }

            mediaCatalog = property.Value;
        }

        if (mediaCatalog is null || mediaCatalog.Value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var media in mediaCatalog.Value.EnumerateArray())
        {
            if (media.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in media.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        "lastKnownContentKind",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        property.Name,
                        "lastKnownDisplayName",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new JsonException(
                        "Version-two media entries cannot contain version-three metadata.");
                }
            }
        }
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
        // Enumerate case variants explicitly so schema dispatch cannot depend on property order or
        // on the caller's later property-name matching policy.
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
            // Documents predating explicit versioning belong to the original V1 contract.
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
        // Never mutate caller-owned options. After cloning, override the strict settings this codec
        // owns; custom converters and other caller-selected behaviors remain in effect.
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
