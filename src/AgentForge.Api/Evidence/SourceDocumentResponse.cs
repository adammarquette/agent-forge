using Microsoft.AspNetCore.Http;

namespace AgentForge.Api.Evidence;

/// <summary>
/// Serves a source document so that no browser can run it as markup: the media type is pinned to what the
/// bytes' signature proves (PDF or a raster image), never taken from the upload's declared type, and every
/// response carries <c>X-Content-Type-Options: nosniff</c> and a sandboxing <c>Content-Security-Policy</c>.
/// Anything the signature does not prove is an opaque attachment. The click-to-source overlay reads the bytes
/// with <c>fetch</c> and renders them with pdf.js, which none of these headers affect.
/// </summary>
internal static class SourceDocumentResponse
{
    /// <summary>Applied to the response itself; a fetch of it as data is unaffected.</summary>
    internal const string ContentSecurityPolicy = "default-src 'none'; sandbox";

    private const string OpaqueMediaType = "application/octet-stream";
    private const string OpaqueDownloadName = "source-document.bin";

    /// <summary>Writes the hardening headers and returns the file result for <paramref name="content"/>.</summary>
    internal static IResult Create(HttpResponse response, byte[] content)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;

        return MediaTypeOf(content) is { } proven
            ? Results.File(content, proven)
            : Results.File(content, OpaqueMediaType, OpaqueDownloadName);
    }

    /// <summary>The media type the file signature proves, or null when it is not an allowed type.</summary>
    internal static string? MediaTypeOf(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }
}
