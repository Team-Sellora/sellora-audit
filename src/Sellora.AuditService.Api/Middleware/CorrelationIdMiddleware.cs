using Sellora.AuditService.Application.Identity;
using Serilog.Context;

namespace Sellora.AuditService.Api.Middleware;

/// <summary>
/// Copied from sellora-order. Uses the incoming X-Correlation-ID or a new
/// one, returns it on the response and pushes it into every log line.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>The one key every reader uses; a second spelling would silently read nothing.</summary>
    public const string ItemKey = "Sellora.CorrelationId";
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();

        var correlationId = !string.IsNullOrWhiteSpace(incoming) &&
                            incoming.Length <= MaxLength &&
                            incoming.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            ? incoming
            : Guid.NewGuid().ToString();

        context.Response.Headers[HeaderName] = correlationId;
        context.Items[ItemKey] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}

public sealed class HttpCorrelationIdAccessor(IHttpContextAccessor accessor) : ICorrelationIdAccessor
{
    public string GetCorrelationId() =>
        accessor.HttpContext?.Items[CorrelationIdMiddleware.ItemKey] is string { Length: > 0 } correlationId
            ? correlationId
            : Guid.NewGuid().ToString();
}
