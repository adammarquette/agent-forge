using System.Diagnostics.CodeAnalysis;
using AgentForge.Api.Chat;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Observability;

/// <summary>
/// Establishes one correlation id per HTTP request and opens the logging scope every line written
/// while that request is handled inherits (FR-OBS-1, NFR-TRACE-1, CONVENTIONS.md §7).
/// A well-formed inbound <c>X-Correlation-Id</c> is adopted so a caller's trace continues instead
/// of restarting here; anything else is replaced with a freshly minted id.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Longest inbound id accepted - an unbounded one would be a log-cardinality lever for the caller.</summary>
    internal const int MaxInboundLength = 128;

    /// <summary>Runs the request inside its correlation scope.</summary>
    public async Task InvokeAsync(HttpContext context, MutableCorrelationIdAccessor correlationIdAccessor)
    {
        // A hub connection is one long-lived request whose execution context every later invocation
        // inherits, while ChatSessionCoordinator opens a scope per turn - a scope here would nest a
        // second, connection-wide id under every turn's own.
        if (context.Request.Path.StartsWithSegments(ChatHub.Route))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        correlationIdAccessor.CorrelationId = TryReadInbound(context.Request, out var inbound)
            ? inbound
            : MutableCorrelationIdAccessor.NewCorrelationId();

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationIdAccessor.CorrelationId,
        });

        await next(context).ConfigureAwait(false);
    }

    // Caller-supplied and therefore untrusted: whitespace or a control character would forge a
    // second record inside one log line, so only an unpadded id of safe characters is adopted.
    private static bool TryReadInbound(HttpRequest request, [NotNullWhen(true)] out string? correlationId)
    {
        correlationId = null;
        if (!request.Headers.TryGetValue(CorrelationIdHandler.HeaderName, out var values))
        {
            return false;
        }

        var candidate = values.ToString();
        if (candidate.Length is 0 or > MaxInboundLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        correlationId = candidate;
        return true;
    }
}
