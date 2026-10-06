using System.Net;
using Microsoft.Playwright;

namespace AgentForge.MintSessionCookie;

/// <summary>A login that did not produce a usable session. The message is safe to print: no secret, no query string.</summary>
internal sealed class MintException(string message) : Exception(message);

/// <summary>
/// Drives the sidecar's real <c>/launch</c> (or <c>/agenda/launch</c>) -&gt; OpenEMR login -&gt; patient
/// picker -&gt; consent -&gt; <c>/callback</c> flow in a headless browser, then reads the session cookie the
/// sidecar set. It automates the browser step a human does with DevTools and nothing else: no endpoint,
/// no bypass, the same authorization code a person's login would mint.
/// </summary>
internal static class SessionMinter
{
    private const string LoginPage = "input[name=password]";
    private const string UsernameField = "input[name=username]";

    // TOTP renders mfa_token; U2F renders only mfa_type (and the echoed password), so both are needed.
    private const string MfaPage = "input[name=mfa_token], input[name=mfa_type]";
    private const string PatientPicker = "button[data-patient-id]";
    private const string ConsentButton = "button[name=proceed]";
    private const string LoginButton = "button[name=user_role][value=api]";
    private const int MaxSteps = 4;

    /// <summary>Completes one login and returns the session cookie as a <c>Name=Value</c> pair.</summary>
    public static async Task<string> MintAsync(
        MintOptions options, LoginCredentials credentials, TextWriter log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(log);

        log.WriteLine("Starting the browser ...");
        var playwright = await StartPlaywrightAsync().ConfigureAwait(false);
        IBrowser? browser = null;
        IPage? page = null;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = !options.Headed,
                Channel = options.BrowserChannel,
            }).ConfigureAwait(false);
            var context = await browser.NewContextAsync().ConfigureAwait(false);
            page = await context.NewPageAsync().ConfigureAwait(false);
            page.SetDefaultTimeout((float)options.Timeout.TotalMilliseconds);
            return await DriveAsync(options, credentials, context, page, log, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new MintException($"A browser step timed out after {options.Timeout.TotalSeconds:0}s; stopped at {Redact(page?.Url)}.");
        }
        catch (PlaywrightException ex)
        {
            throw new MintException($"The browser failed at {Redact(page?.Url)}: {ScrubBrowserError(ex.Message, credentials.Password)}");
        }
        finally
        {
            await CloseAsync(browser, log).ConfigureAwait(false);
            playwright.Dispose();
        }
    }

    /// <summary>
    /// Starts the Playwright driver after removing the password variable from this process's environment:
    /// the driver, and the browser it starts, inherit that environment for the whole run.
    /// </summary>
    internal static Task<IPlaywright> StartPlaywrightAsync()
    {
        Environment.SetEnvironmentVariable(LoginCredentials.PasswordVariable, null);
        return Playwright.CreateAsync();
    }

    /// <summary>
    /// The first line of a Playwright error, with the password masked. The call log on the lines after it
    /// can quote what an action was given.
    /// </summary>
    internal static string ScrubBrowserError(string message, string password)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(password);
        return message.Split('\n', 2)[0].TrimEnd('\r').Replace(password, "***", StringComparison.Ordinal);
    }

    // A graceful close can stall under load; disposing Playwright afterwards stops the driver and its browser regardless.
    private static async Task CloseAsync(IBrowser? browser, TextWriter log)
    {
        if (browser is null)
        {
            return;
        }

        var close = browser.CloseAsync();
        if (await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != close)
        {
            log.WriteLine("The browser did not close within 10s; stopping it.");
        }

        _ = close.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    private static async Task<string> DriveAsync(
        MintOptions options, LoginCredentials credentials, IBrowserContext context, IPage page, TextWriter log, CancellationToken cancellationToken)
    {
        var callback = new TaskCompletionSource<IResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Response += (_, response) =>
        {
            if (IsSidecarCallback(options, response.Url))
            {
                callback.TrySetResult(response);
            }
        };

        var navigations = 0;
        page.FrameNavigated += (_, frame) =>
        {
            if (frame == page.MainFrame)
            {
                Interlocked.Increment(ref navigations);
            }
        };

        // A click on these pages submits a form, sometimes from script; wait for the document to change
        // so the next look does not find the page just left.
        async Task ClickAndAwaitNavigationAsync(string what, Func<Task> click)
        {
            var before = Volatile.Read(ref navigations);
            await click().ConfigureAwait(false);
            var deadline = DateTime.UtcNow + options.Timeout;
            while (Volatile.Read(ref navigations) == before && !callback.Task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new MintException($"{what} did not lead anywhere within {options.Timeout.TotalSeconds:0}s; stopped at {Redact(page.Url)}.");
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The next selector wait carries its own timeout and a better message.
            }
        }

        var launchUrl = new Uri(options.SidecarBaseUrl, options.Launch == LaunchKind.Patient ? "launch" : "agenda/launch");
        log.WriteLine($"Launching {Redact(launchUrl)} ...");
        await page.GotoAsync(launchUrl.ToString()).ConfigureAwait(false);

        var loggedIn = false;
        var pickedPatient = false;
        var consented = false;
        for (var step = 0; !callback.Task.IsCompleted; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Login, picker and consent each happen at most once; anything longer is a loop, not a login.
            if (step >= MaxSteps)
            {
                throw new MintException($"No callback after {MaxSteps} pages; stopped at {Redact(page.Url)}.");
            }

            var nextPage = page.WaitForSelectorAsync(
                $"{LoginPage}, {MfaPage}, {PatientPicker}, {ConsentButton}",
                new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached });
            var first = await Task.WhenAny(callback.Task, nextPage).ConfigureAwait(false);
            if (first == callback.Task)
            {
                ObserveQuietly(nextPage);
                break;
            }

            try
            {
                await nextPage.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new MintException(
                    $"Neither a login, patient-select or consent page nor the sidecar callback appeared within " +
                    $"{options.Timeout.TotalSeconds:0}s; stopped at {Redact(page.Url)}.");
            }

            if (await page.Locator(MfaPage).CountAsync().ConfigureAwait(false) > 0)
            {
                throw new MintException("OpenEMR asked for a second factor (MFA). This tool does not handle MFA; use an account without it or the manual DevTools recipe.");
            }

            if (await page.Locator(LoginPage).CountAsync().ConfigureAwait(false) > 0)
            {
                if (loggedIn)
                {
                    // Never retry: a second attempt with the same password only moves the account towards lockout.
                    var alert = await FirstTextAsync(page, ".alert-danger").ConfigureAwait(false);
                    throw new MintException($"OpenEMR rejected the login{(alert is null ? string.Empty : $": {alert}")}.");
                }

                // Checked again before each step: the page can navigate or retarget its form while it is filled.
                async Task EnsureStillSafeAsync() =>
                    EnsureCredentialsMayGoTo(options, page.Url, await LoginFormTargetAsync(page).ConfigureAwait(false));

                await EnsureStillSafeAsync().ConfigureAwait(false);
                log.WriteLine($"Signing in at {Redact(page.Url)} ...");
                await page.FillAsync(UsernameField, credentials.Username).ConfigureAwait(false);
                await EnsureStillSafeAsync().ConfigureAwait(false);
                await page.FillAsync(LoginPage, credentials.Password).ConfigureAwait(false);
                loggedIn = true;
                await EnsureStillSafeAsync().ConfigureAwait(false);
                await ClickAndAwaitNavigationAsync("Signing in", () => page.ClickAsync(LoginButton)).ConfigureAwait(false);
            }
            else if (await page.Locator(PatientPicker).CountAsync().ConfigureAwait(false) > 0)
            {
                if (pickedPatient)
                {
                    throw new MintException("OpenEMR showed the patient picker again after a patient was chosen.");
                }

                if (options.PatientId is null)
                {
                    throw new MintException("OpenEMR is asking for a patient. Pass --patient-id <uuid>, or use --launch agenda.");
                }

                var button = page.Locator($"{PatientPicker}[data-patient-id=\"{CssString(options.PatientId)}\"]");
                if (await button.CountAsync().ConfigureAwait(false) == 0)
                {
                    throw new MintException(
                        $"Patient '{options.PatientId}' is not among the patients OpenEMR offered (it shows a limited set).");
                }

                log.WriteLine("Selecting the patient ...");
                pickedPatient = true;
                await ClickAndAwaitNavigationAsync("Selecting the patient", () => button.First.ClickAsync()).ConfigureAwait(false);
            }
            else
            {
                if (consented)
                {
                    throw new MintException("OpenEMR showed the consent page again after it was approved.");
                }

                log.WriteLine("Approving the requested scopes ...");
                consented = true;
                await ClickAndAwaitNavigationAsync("Approving the scopes", () => page.ClickAsync(ConsentButton)).ConfigureAwait(false);
            }
        }

        var callbackResponse = await callback.Task.ConfigureAwait(false);
        if (callbackResponse.Status is < 300 or >= 400)
        {
            var body = await SafeBodyAsync(callbackResponse).ConfigureAwait(false);
            throw new MintException($"The sidecar callback answered {callbackResponse.Status} rather than redirecting into the app{(body.Length == 0 ? string.Empty : $": {body}")}.");
        }

        try
        {
            await page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The session is committed before /callback redirects; a slow SPA does not change the cookie.
        }

        var cookies = await context.CookiesAsync([options.SidecarBaseUrl.ToString()]).ConfigureAwait(false);
        var session = cookies.FirstOrDefault(c => c.Name == options.CookieName)
            ?? throw new MintException($"The callback succeeded but the browser holds no '{options.CookieName}' cookie for {Redact(options.SidecarBaseUrl)}.");
        var pair = $"{session.Name}={session.Value}";

        if (options.Launch == LaunchKind.Patient && options.Verify)
        {
            await VerifyAsync(options, pair, log, cancellationToken).ConfigureAwait(false);
        }
        else if (options.Launch == LaunchKind.Agenda)
        {
            // GET /agenda would prove it, but runs an agent turn per roster patient - real model spend.
            log.WriteLine("Session established (agenda launch; not re-checked).");
        }

        return pair;
    }

    /// <summary>
    /// Refuses to type credentials unless the page and the URL its login form submits to are both on the named
    /// OpenEMR origin, and neither is plain http off loopback. A null <paramref name="formTarget"/> means the
    /// password field is in no form, so where it would go cannot be checked; that is refused too.
    /// </summary>
    internal static void EnsureCredentialsMayGoTo(MintOptions options, string pageUrl, string? formTarget)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureMayReceiveCredentials(options, pageUrl, "the login page is served from");
        if (formTarget is null)
        {
            throw new MintException("Refusing to enter credentials: the password field is not inside a form, so where it would be sent cannot be checked.");
        }

        EnsureMayReceiveCredentials(options, formTarget, "the login form submits to");
    }

    private static void EnsureMayReceiveCredentials(MintOptions options, string url, string what)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new MintException($"Refusing to enter credentials: {what} no absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new MintException($"Refusing to enter credentials: {what} a {uri.Scheme}: URL, not the expected OpenEMR origin {options.OpenEmrOrigin}.");
        }

        if (MintOptions.OriginOf(uri) != options.OpenEmrOrigin)
        {
            throw new MintException(
                $"Refusing to enter credentials: {what} {MintOptions.OriginOf(uri)}, " +
                $"not the expected OpenEMR origin {options.OpenEmrOrigin}. If that host really is your OpenEMR, pass --openemr-base-url.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new MintException($"Refusing to send credentials over plain http to {MintOptions.OriginOf(uri)}; only loopback may use http.");
        }
    }

    // Resolved in the page as the browser would submit it: the login button's formaction if it has one, else the
    // form's action attribute (read as an attribute, because a field named "action" shadows the property).
    private static Task<string?> LoginFormTargetAsync(IPage page) => page.EvaluateAsync<string?>(
        """
        ([field, button]) => {
          const password = document.querySelector(field);
          const form = password ? password.form : null;
          if (!form) return null;
          const submit = document.querySelector(button);
          const raw = submit && submit.form === form && submit.hasAttribute('formaction')
            ? submit.getAttribute('formaction')
            : form.getAttribute('action');
          return new URL(raw ? raw : document.URL, document.baseURI).href;
        }
        """,
        new[] { LoginPage, LoginButton });

    /// <summary>Scheme, host and path only - a query string may carry a state, a code or a launch token.</summary>
    internal static string Redact(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    private static string Redact(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Redact(uri) : "(no page)";

    private static bool IsSidecarCallback(MintOptions options, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || MintOptions.OriginOf(uri) != MintOptions.OriginOf(options.SidecarBaseUrl))
        {
            return false;
        }

        var basePath = options.SidecarBaseUrl.AbsolutePath;
        return uri.AbsolutePath == basePath + "callback" || uri.AbsolutePath == basePath + "agenda/callback";
    }

    private static async Task VerifyAsync(MintOptions options, string pair, TextWriter log, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = options.Timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.SidecarBaseUrl, "patient"));
        request.Headers.Add("Cookie", pair);
        using var response = await SendVerificationAsync(http, request, options.Timeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new MintException(
                $"The minted cookie did not authenticate GET {Redact(request.RequestUri!)}: {(int)response.StatusCode}. Nothing was written.");
        }

        log.WriteLine("Session verified: GET /patient answered 200 with the minted cookie as a plain header.");
    }

    // A network failure here is a session that could not be checked (exit 1), not a crash.
    private static async Task<HttpResponseMessage> SendVerificationAsync(
        HttpClient http, HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MintException($"GET {Redact(request.RequestUri!)} failed, so the minted cookie could not be checked: {OneLine(ex.Message)} Nothing was written.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MintException(
                $"GET {Redact(request.RequestUri!)} did not answer within {timeout.TotalSeconds:0}s, so the minted cookie could not be checked. Nothing was written.");
        }
    }

    private static async Task<string?> FirstTextAsync(IPage page, string selector)
    {
        var locator = page.Locator(selector);
        if (await locator.CountAsync().ConfigureAwait(false) == 0)
        {
            return null;
        }

        var text = (await locator.First.InnerTextAsync().ConfigureAwait(false)).Trim();
        return text.Length == 0 ? null : OneLine(text);
    }

    private static async Task<string> SafeBodyAsync(IResponse response)
    {
        try
        {
            return OneLine(await response.TextAsync().ConfigureAwait(false));
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    private static string OneLine(string text)
    {
        var flat = string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "...";
    }

    private static string CssString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void ObserveQuietly(Task task) => _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
}
