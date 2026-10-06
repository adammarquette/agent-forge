using System.Globalization;

namespace AgentForge.MintSessionCookie;

/// <summary>Which sidecar launch entry point the login runs through.</summary>
internal enum LaunchKind
{
    /// <summary><c>/agenda/launch</c>: roster session, no patient picker.</summary>
    Agenda,

    /// <summary><c>/launch</c>: single-patient session; OpenEMR shows a patient picker.</summary>
    Patient,
}

/// <summary>
/// Everything the tool is told on its command line or in its environment - deliberately nothing secret.
/// Credentials never pass through here: <see cref="Parse"/> refuses them, because an argument lands in
/// shell history and in any process listing.
/// </summary>
internal sealed record MintOptions
{
    public const string BaseUrlVariable = "MintSession__BaseUrl";
    public const string DefaultCookieName = ".AspNetCore.Session";

    public required Uri SidecarBaseUrl { get; init; }

    /// <summary>The only origin the tool will type credentials into.</summary>
    public required Uri OpenEmrOrigin { get; init; }

    public LaunchKind Launch { get; init; } = LaunchKind.Agenda;
    public string? PatientId { get; init; }
    public string? OutputPath { get; init; }
    public string CookieName { get; init; } = DefaultCookieName;
    public string? BrowserChannel { get; init; }
    public bool Headed { get; init; }
    public bool Verify { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public const string Usage = """
        Mints a sidecar session cookie by driving a real SMART login in a headless browser.

        Usage: dotnet run --project tools/MintSessionCookie -- --base-url <sidecar base url> [options]

          --base-url <url>           Sidecar base URL including its path base, e.g.
                                     https://<front door>/agentforge  (or env MintSession__BaseUrl)
          --launch agenda|patient    Launch entry point (default: agenda)
          --patient-id <uuid>        Patient to pick on OpenEMR's patient-select page (--launch patient)
          --openemr-base-url <url>   The OpenEMR the login page must be served from. Default: the
                                     sidecar's own origin (one front door). Credentials are never
                                     typed into a page on any other origin.
          --out <file>               Write the Name=Value pair to this file instead of stdout
          --cookie-name <name>       Session cookie name (default: .AspNetCore.Session)
          --browser-channel <name>   Use an installed browser (chrome, msedge) instead of Playwright's
          --headed                   Show the browser window
          --timeout-seconds <n>      Per-step timeout (default: 60)
          --no-verify                Skip the GET /patient check after a patient launch

        Credentials are read from MintSession__Username / MintSession__Password, or prompted for
        on the terminal (password not echoed). They are never accepted as arguments.
        """;

    private static readonly string[] CredentialFlagStems = ["--user", "--pass", "--pwd", "--secret", "--cred", "-u", "-p"];

    /// <summary>Parses arguments. Returns null options plus an error message on any usage error.</summary>
    public static (MintOptions? Options, string? Error) Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        string? baseUrl = environment(BaseUrlVariable);
        string? openEmrUrl = null;
        var launch = LaunchKind.Agenda;
        string? patientId = null;
        string? output = null;
        var cookieName = DefaultCookieName;
        string? channel = null;
        var headed = false;
        var verify = true;
        var timeoutSeconds = 60;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var flag = arg.Split('=', 2)[0];
            if (CredentialFlagStems.Any(stem => flag.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
            {
                return (null,
                    $"'{flag}': credentials are never accepted as arguments - they would land in shell history. " +
                    "Set MintSession__Username / MintSession__Password, or run interactively to be prompted.");
            }

            string? Value()
            {
                var inline = arg.Split('=', 2);
                if (inline.Length == 2)
                {
                    return inline[1];
                }

                return i + 1 < args.Count ? args[++i] : null;
            }

            switch (flag)
            {
                case "--help" or "-h":
                    return (null, Usage);
                case "--base-url":
                    baseUrl = Value();
                    break;
                case "--openemr-base-url":
                    openEmrUrl = Value();
                    break;
                case "--launch":
                    var kind = Value();
                    if (!Enum.TryParse(kind, ignoreCase: true, out launch) || !Enum.IsDefined(launch))
                    {
                        return (null, $"--launch must be 'agenda' or 'patient', not '{kind}'.");
                    }

                    break;
                case "--patient-id":
                    patientId = Value();
                    break;
                case "--out":
                    output = Value();
                    break;
                case "--cookie-name":
                    cookieName = Value() ?? string.Empty;
                    break;
                case "--browser-channel":
                    channel = Value();
                    break;
                case "--headed":
                    headed = true;
                    break;
                case "--no-verify":
                    verify = false;
                    break;
                case "--timeout-seconds":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds) || timeoutSeconds < 1)
                    {
                        return (null, "--timeout-seconds must be a positive whole number.");
                    }

                    break;
                default:
                    return (null, $"Unknown argument '{arg}'.\n\n{Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (null, $"--base-url (or {BaseUrlVariable}) is required.\n\n{Usage}");
        }

        if (!TryAbsoluteHttpUri(baseUrl, out var sidecar))
        {
            return (null, $"--base-url must be an absolute http(s) URL, not '{baseUrl}'.");
        }

        Uri openEmrOrigin;
        if (openEmrUrl is null)
        {
            openEmrOrigin = OriginOf(sidecar);
        }
        else if (TryAbsoluteHttpUri(openEmrUrl, out var openEmr))
        {
            openEmrOrigin = OriginOf(openEmr);
        }
        else
        {
            return (null, $"--openemr-base-url must be an absolute http(s) URL, not '{openEmrUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(cookieName))
        {
            return (null, "--cookie-name must not be empty.");
        }

        if (launch == LaunchKind.Agenda && patientId is not null)
        {
            return (null, "--patient-id applies only to --launch patient; an agenda launch has no patient picker.");
        }

        // A .bru environment is checked in; the collection README forbids a cookie in one.
        if (output is not null && output.EndsWith(".bru", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "--out must not be a Bruno .bru file - environments are checked in. Pass the cookie with --env-var instead.");
        }

        return (new MintOptions
        {
            SidecarBaseUrl = new Uri(sidecar.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"),
            OpenEmrOrigin = openEmrOrigin,
            Launch = launch,
            PatientId = patientId,
            OutputPath = output,
            CookieName = cookieName,
            BrowserChannel = channel,
            Headed = headed,
            Verify = verify,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        }, null);
    }

    /// <summary>Scheme, host and port only - the unit a browser's same-origin policy compares.</summary>
    public static Uri OriginOf(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    private static bool TryAbsoluteHttpUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
