using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentForge.Api.Health;

/// <summary>
/// The <c>/ready</c> body. ASP.NET Core's default writer prints the aggregate status and nothing else, so
/// a 503 named no dependency and a 200 could not be told from a `Degraded` one without reading the logs -
/// NFR-HEALTH-W2-1 asks for the opposite.
/// </summary>
/// <remarks>
/// <c>/ready</c> is unauthenticated, so the body carries only what each check chose to say: its name, its
/// status and its own description. Never <see cref="HealthReportEntry.Exception"/> - an Npgsql or
/// <see cref="HttpClient"/> failure can carry the host, port and connection-string keywords it was
/// configured with.
/// </remarks>
internal static class ReadinessResponse
{
    /// <summary>Writes the report as the <c>/ready</c> response body.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(Serialize(report));
    }

    /// <summary>
    /// Renders the report. Checks are ordered by name: <see cref="HealthReport.Entries"/> is a dictionary,
    /// and an unordered body makes two probes impossible to diff.
    /// </summary>
    public static string Serialize(HealthReport report)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("status", report.Status.ToString());
            json.WriteStartArray("checks");
            foreach (var (name, entry) in report.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteString("status", entry.Status.ToString());
                json.WriteString("description", entry.Description);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
