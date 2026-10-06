using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace AgentForge.Api.Launch;

/// <summary>
/// Holds a flow's pending SMART launches (state and PKCE verifier) in a data-protected cookie on the browser, so
/// starting a launch writes nothing to the server's session store. The launch routes are unauthenticated, and a
/// session entry per launch let anyone fill the store that established sessions live in; a session entry is
/// now written only by a callback that completed the token exchange.
/// </summary>
/// <remarks>
/// The cookie keeps the <see cref="MaxPending"/> most recent launches of its flow, each for <see cref="Lifetime"/>,
/// so a second launch before the first completes does not strand either - the double launch when the first one
/// lands on OpenEMR's login page. Each entry's issue time is inside the protected payload, so neither the lifetime
/// nor the entries can be altered by the browser.
/// </remarks>
public static class PendingLaunchCookie
{
    /// <summary>The most pending launches one browser holds per flow; the oldest is dropped first.</summary>
    public const int MaxPending = 3;

    /// <summary>How long a pending launch stays redeemable - long enough to sign in to OpenEMR on the way.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const string CookiePrefix = ".AgentForge.PendingLaunch.";
    private const string Purpose = "AgentForge.Api.Launch.PendingLaunchCookie";

    /// <summary>The cookie name for <paramref name="flow"/>.</summary>
    public static string CookieName(string flow) => CookiePrefix + flow;

    /// <summary>Records <paramref name="pending"/> for <paramref name="flow"/>, dropping expired and surplus entries.</summary>
    public static void Add(
        HttpContext httpContext, string flow, PendingLaunchContext pending,
        IDataProtectionProvider dataProtection, TimeProvider timeProvider, BffOptions bffOptions)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(pending);
        var now = timeProvider.GetUtcNow();
        var entries = Read(httpContext, flow, dataProtection, now);
        entries.Add(new Entry(pending.State, pending.CodeVerifier, now.ToUnixTimeSeconds()));
        Write(httpContext, flow, entries.TakeLast(MaxPending).ToList(), dataProtection, bffOptions);
    }

    /// <summary>
    /// Removes and returns the pending launch that issued <paramref name="state"/>, or null when this browser holds
    /// none (never issued, expired, or already redeemed).
    /// </summary>
    public static PendingLaunchContext? Take(
        HttpContext httpContext, string flow, string state,
        IDataProtectionProvider dataProtection, TimeProvider timeProvider, BffOptions bffOptions)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var entries = Read(httpContext, flow, dataProtection, timeProvider.GetUtcNow());
        var match = entries.Find(e => string.Equals(e.State, state, StringComparison.Ordinal));
        if (match is null)
        {
            return null;
        }

        entries.Remove(match);
        Write(httpContext, flow, entries, dataProtection, bffOptions);
        return new PendingLaunchContext(match.State, match.CodeVerifier);
    }

    private static List<Entry> Read(
        HttpContext httpContext, string flow, IDataProtectionProvider dataProtection, DateTimeOffset now)
    {
        if (!httpContext.Request.Cookies.TryGetValue(CookieName(flow), out var protectedValue)
            || string.IsNullOrEmpty(protectedValue))
        {
            return [];
        }

        try
        {
            var json = Protector(dataProtection, flow).Unprotect(protectedValue);
            var entries = JsonSerializer.Deserialize<List<Entry>>(json) ?? [];
            var oldest = now.Subtract(Lifetime).ToUnixTimeSeconds();
            return entries.Where(e => e.IssuedAtUnixSeconds >= oldest).ToList();
        }
        // A rotated key ring or a tampered cookie reads as no pending launch, as a lost session did before.
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return [];
        }
    }

    private static void Write(
        HttpContext httpContext, string flow, List<Entry> entries,
        IDataProtectionProvider dataProtection, BffOptions bffOptions)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            IsEssential = true,
            // Mirrors the session cookie: the callback is a top-level redirect back from OpenEMR.
            SameSite = bffOptions.PathBase.Length > 0 ? SameSiteMode.Lax : SameSiteMode.None,
            Path = bffOptions.PathBase.Length > 0 ? bffOptions.PathBase : "/",
        };

        if (entries.Count == 0)
        {
            httpContext.Response.Cookies.Delete(CookieName(flow), options);
            return;
        }

        options.MaxAge = Lifetime;
        var json = JsonSerializer.Serialize(entries);
        httpContext.Response.Cookies.Append(CookieName(flow), Protector(dataProtection, flow).Protect(json), options);
    }

    private static IDataProtector Protector(IDataProtectionProvider dataProtection, string flow) =>
        dataProtection.CreateProtector(Purpose, flow);

    private sealed record Entry(string State, string CodeVerifier, long IssuedAtUnixSeconds);
}
