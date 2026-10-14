using System.Globalization;
using System.Text.Json;

namespace Sellora.AuditService.Application.Ingestion;

/// <summary>
/// Reads payload fields whatever their casing: Order and Catalog publish
/// camelCase, while Inventory and Delivery serialise with the default
/// (PascalCase) options.
/// </summary>
public static class JsonFields
{
    public static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    public static string? String(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static Guid? Guid(JsonElement element, string name) =>
        TryGet(element, name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        System.Guid.TryParse(value.GetString(), out var parsed) &&
        parsed != System.Guid.Empty
            ? parsed
            : null;

    public static Guid? FirstGuid(JsonElement element, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (Guid(element, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public static DateTimeOffset? Timestamp(JsonElement element, string name) =>
        TryGet(element, name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    public static JsonElement? Object(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
}
