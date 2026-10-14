using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sellora.AuditService.Domain.Audit;

namespace Sellora.AuditService.Application.Ingestion;

public enum RejectionReason
{
    EmptyPayload,
    InvalidUtf8,
    ContainsNulCharacter,
    NotJson,
    NotJsonObject,
    MissingCompanyId,
    CompanyMismatch
}

public sealed record NormalisationResult(AuditEventContent? Content, RejectionReason? Rejection, string? Detail)
{
    public bool IsAccepted => Content is not null;

    public static NormalisationResult Accepted(AuditEventContent content) => new(content, null, null);

    public static NormalisationResult Rejected(RejectionReason reason, string detail) => new(null, reason, detail);
}

/// <summary>
/// US-E7-1-BE3: turns a record from any of the five producers into one
/// audit shape. Headers are read first, the payload second, because the
/// producers differ: Organization sends event-id as 16 raw bytes, Order as
/// a UTF-8 string, Catalog has no correlation header, Inventory sends no
/// headers at all, and Delivery prefixes its headers with "x-".
/// </summary>
public static class AuditEnvelopeNormaliser
{
    public const string UnknownActor = "unknown";
    public const string SystemActor = "system";
    public const string UnknownEventType = "Unknown";

    /// <summary>
    /// Derived columns are indexed, so they are capped; the payload itself is
    /// always stored in full. The capped value is also the hashed value.
    /// </summary>
    public const int MaxFieldLength = 200;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] OccurredAtFields =
    {
        "occurredAt", "effectiveAt", "changedAt", "detectedAt", "acceptedAt", "createdAt"
    };

    private static readonly string[] ActorObjectFields = { "actor", "approvedBy", "cancelledBy", "acceptedBy" };

    public static NormalisationResult Normalise(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Value is not { Length: > 0 } bytes)
        {
            return NormalisationResult.Rejected(RejectionReason.EmptyPayload, "The message has no value.");
        }

        string payload;
        try
        {
            payload = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return NormalisationResult.Rejected(RejectionReason.InvalidUtf8, "The message value is not valid UTF-8.");
        }

        // PostgreSQL text cannot hold U+0000, and the payload must be stored exactly.
        if (payload.Contains('\0'))
        {
            return NormalisationResult.Rejected(RejectionReason.ContainsNulCharacter, "The message value contains a NUL character.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            return NormalisationResult.Rejected(RejectionReason.NotJson, $"The message value is not JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return NormalisationResult.Rejected(RejectionReason.NotJsonObject, "The message value must be a JSON object.");
            }

            var headerCompany = HeaderGuid(message, "company-id");
            var payloadCompany = JsonFields.Guid(root, "companyId");

            if (headerCompany is { } fromHeader && payloadCompany is { } fromPayload && fromHeader != fromPayload)
            {
                return NormalisationResult.Rejected(
                    RejectionReason.CompanyMismatch,
                    $"The company-id header ({fromHeader}) and the payload companyId ({fromPayload}) differ.");
            }

            if ((headerCompany ?? payloadCompany) is not { } companyId)
            {
                return NormalisationResult.Rejected(RejectionReason.MissingCompanyId, "No company ID in the headers or the payload.");
            }

            var eventType = Limit(HeaderText(message, "event-type")
                ?? NonBlank(JsonFields.String(root, "eventType"))
                ?? UnknownEventType)!;

            var rule = AggregateRules.For(message.Topic, eventType);
            var aggregateType = rule.AggregateType;

            if (eventType == "HierarchyEntityDeactivated" && JsonFields.String(root, "entityType") is { Length: > 0 } entityType)
            {
                aggregateType = Limit(entityType)!;
            }

            var aggregateId = JsonFields.FirstGuid(root, rule.IdFields);
            var (actorUserId, actorRole) = ResolveActor(root, eventType);

            return NormalisationResult.Accepted(new AuditEventContent(
                CompanyId: companyId,
                EventId: HeaderGuid(message, "event-id") ?? JsonFields.Guid(root, "eventId") ?? DerivedEventId(message),
                EventType: eventType,
                SchemaVersion: Limit(HeaderText(message, "schema-version") ?? NonBlank(JsonFields.String(root, "schemaVersion"))),
                SourceTopic: message.Topic,
                Partition: message.Partition,
                Offset: message.Offset,
                AggregateType: aggregateType,
                AggregateId: aggregateId,
                OrderReference: Limit(NonBlank(JsonFields.String(root, "orderReference"))),
                ProvinceId: JsonFields.Guid(root, "provinceId") ?? (aggregateType == "Province" ? aggregateId : null),
                ActorUserId: Limit(actorUserId)!,
                ActorRole: Limit(actorRole),
                OccurredAt: AuditTime.Normalise(OccurredAt(root, message)),
                CorrelationId: Limit(HeaderText(message, "correlation-id") ?? NonBlank(JsonFields.String(root, "correlationId"))),
                Payload: payload));
        }
    }

    private static string? Limit(string? value) =>
        value is { Length: > MaxFieldLength } ? value[..MaxFieldLength] : value;

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTimeOffset OccurredAt(JsonElement root, IncomingMessage message)
    {
        foreach (var field in OccurredAtFields)
        {
            if (JsonFields.Timestamp(root, field) is { } value)
            {
                return value;
            }
        }

        return message.Timestamp;
    }

    private static (string UserId, string? Role) ResolveActor(JsonElement root, string eventType)
    {
        foreach (var field in ActorObjectFields)
        {
            if (JsonFields.Object(root, field) is { } actor && JsonFields.String(actor, "userId") is { Length: > 0 } userId)
            {
                return (userId, JsonFields.String(actor, "role"));
            }
        }

        // Order: cancelledBy is null when the system cancelled (stock hold expiry).
        if (eventType == "OrderCancelled")
        {
            return (SystemActor, "System");
        }

        // The rep places every order and takes cash at the counter.
        var cashSale = string.Equals(JsonFields.String(root, "fulfilmentType"), "ImmediateCashSale", StringComparison.Ordinal);
        if (eventType == "OrderPlaced" || (cashSale && eventType is "OrderConfirmed" or "PaymentRecorded"))
        {
            if (JsonFields.Object(root, "salesRep") is { } rep && JsonFields.Guid(rep, "salesRepId") is { } repId)
            {
                return (repId.ToString("D"), "SalesRep");
            }
        }

        if (JsonFields.String(root, "changedBy") is { Length: > 0 } changedBy)
        {
            return (changedBy, null);
        }

        if (JsonFields.Guid(root, "operatorId") is { } operatorId)
        {
            return (operatorId.ToString("D"), null);
        }

        return (UnknownActor, null);
    }

    private static byte[]? Header(IncomingMessage message, string name)
    {
        foreach (var candidate in new[] { name, "x-" + name })
        {
            foreach (var header in message.Headers)
            {
                if (string.Equals(header.Key, candidate, StringComparison.OrdinalIgnoreCase) && header.Value is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? HeaderText(IncomingMessage message, string name)
    {
        if (Header(message, name) is not { } bytes)
        {
            return null;
        }

        try
        {
            var text = StrictUtf8.GetString(bytes).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>16 bytes are a Guid.ToByteArray(); anything else is parsed as text.</summary>
    private static Guid? HeaderGuid(IncomingMessage message, string name)
    {
        if (Header(message, name) is not { } bytes)
        {
            return null;
        }

        if (bytes.Length == 16)
        {
            var raw = new Guid(bytes);
            return raw == Guid.Empty ? null : raw;
        }

        return Guid.TryParse(HeaderText(message, name), out var parsed) && parsed != Guid.Empty ? parsed : null;
    }

    /// <summary>
    /// A record with no usable event ID is still recorded. The ID is derived
    /// from its position in the topic, so a redelivery maps to the same ID.
    /// </summary>
    private static Guid DerivedEventId(IncomingMessage message)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{message.Topic}|{message.Partition}|{message.Offset}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
