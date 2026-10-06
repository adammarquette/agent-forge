using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentForge.MintSessionCookie.SelfTest;

/// <summary>
/// A fake front door on loopback: the sidecar's launch/callback/patient routes under <c>/agentforge</c> and a
/// fake OpenEMR OAuth2 login, patient picker and consent page under <c>/oauth2/default</c>. The pages copy the
/// fork's own markup where the tool depends on it (field names, button values, <c>data-patient-id</c>, and the
/// script-driven submits of <c>patient-select.html.twig</c> and <c>scope-authorize.html.twig</c>). It listens
/// on two ports so a login page can be served from a second, "foreign" origin. Nothing here is real: the
/// username, password, patients and cookies are made up per run.
/// </summary>
internal sealed class StubFrontDoor : IAsyncDisposable
{
    public const string SessionCookie = ".AspNetCore.Session";
    private const string OpenEmrCookie = "OpenEMR";

    private readonly WebApplication _app;

    private StubFrontDoor(WebApplication app, Uri primary, Uri secondary)
    {
        _app = app;
        Primary = primary;
        Secondary = secondary;
    }

    /// <summary>The one front door: sidecar under /agentforge, OpenEMR at the root.</summary>
    public Uri Primary { get; }

    /// <summary>The same app on another port - a different origin.</summary>
    public Uri Secondary { get; }

    public Uri SidecarBase => new(Primary, "/agentforge/");

    public StubState State { get; private set; } = new();

    public static readonly string[] PatientIds = ["00000000-0000-4000-8000-00000000a001", "00000000-0000-4000-8000-00000000a002"];

    public void Reset(StubState state) => State = state;

    public static async Task<StubFrontDoor> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0", "http://127.0.0.1:0");
        var app = builder.Build();
        StubFrontDoor? door = null;
        Map(app, () => door!.State);
        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Select(a => new Uri(a)).ToArray();
        door = new StubFrontDoor(app, addresses[0], addresses[1]);
        return door;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);

    private static void Map(WebApplication app, Func<StubState> state)
    {
        // ---- the sidecar ----
        app.MapGet("/agentforge/launch", (HttpContext http) => BeginLaunch(http, state(), patientLaunch: true));
        app.MapGet("/agentforge/agenda/launch", (HttpContext http) => BeginLaunch(http, state(), patientLaunch: false));
        app.MapGet("/agentforge/callback", (HttpContext http) => CompleteLaunch(http, state(), "/agentforge/index.html"));
        app.MapGet("/agentforge/agenda/callback", (HttpContext http) => CompleteLaunch(http, state(), "/agentforge/agenda"));
        app.MapGet("/agentforge/index.html", () => Html("<h1>Copilot</h1>"));
        app.MapGet("/agentforge/agenda", () => Html("<h1>Agenda</h1>"));
        app.MapGet("/agentforge/patient", (HttpContext http) =>
        {
            var s = state();
            if (s.DropPatientCheck)
            {
                http.Abort();
                return Results.Empty;
            }

            return http.Request.Cookies.TryGetValue(SessionCookie, out var id)
                && s.SidecarSessions.TryGetValue(id, out var session) && session.Authenticated && session.PatientLaunch
                ? Results.Json(new { patientId = session.PatientId })
                : Results.StatusCode(StatusCodes.Status401Unauthorized);
        });

        // ---- OpenEMR ----
        app.MapGet("/oauth2/default/authorize", (HttpContext http) =>
        {
            var q = http.Request.Query;
            var id = Token();
            state().OpenEmrSessions[id] = new OpenEmrSession
            {
                RedirectUri = q["redirect_uri"].ToString(),
                State = q["state"].ToString(),
                PatientLaunch = q["client_id"] == "patient-client",
                Csrf = Token(),
            };
            http.Response.Cookies.Append(OpenEmrCookie, id, new CookieOptions { Path = "/", HttpOnly = true });
            return Results.Redirect("/oauth2/default/provider/login");
        });

        app.MapGet("/oauth2/default/provider/login", (HttpContext http) =>
            WithOpenEmrSession(http, state(), s => LoginPage(s.Csrf, invalid: null, state())));

        app.MapPost("/oauth2/default/typed", () =>
        {
            Interlocked.Increment(ref state().PasswordTypedCount);
            return Results.NoContent();
        });

        app.MapPost("/oauth2/default/provider/login", async (HttpContext http) =>
        {
            var st = state();
            var form = await http.Request.ReadFormAsync().ConfigureAwait(false);
            return WithOpenEmrSession(http, st, s =>
            {
                Interlocked.Increment(ref st.LoginPostCount);
                var ok = form["csrf_token_form"] == s.Csrf && form["user_role"] == "api"
                    && form["username"] == st.Username && form["password"] == st.Password;
                if (!ok)
                {
                    return LoginPage(s.Csrf, invalid: "Invalid username or password", st);
                }

                // The fork's two MFA branches (oauth2-login.html.twig): TOTP adds mfa_token, U2F does not.
                if (st.Mfa != MfaKind.None)
                {
                    var factor = st.Mfa == MfaKind.Totp
                        ? """
                          <input id="totp_token" type="text" name="mfa_token">
                          <button type="submit" name="user_role" value="api">Authenticate TOTP</button>
                          """
                        : """
                          <button type="button" id="authutf" onclick="doAuth()">Authenticate U2F</button>
                          <input type="hidden" name="form_requests" value="[]" />
                          <input type="hidden" name="user_role" value="api">
                          """;
                    return Html($$"""
                        <h4>MFA Verification</h4>
                        <form method="post" name="userLogin" id="userLogin" action="/oauth2/default/provider/login">
                          <input type="hidden" name="csrf_token_form" value="{{s.Csrf}}" />
                          {{factor}}
                          <input type="hidden" name="username" value="{{st.Username}}">
                          <input type="hidden" name="password" value="(echoed by the fork's MFA page)">
                          <input type="hidden" name="mfa_type" value="TOTP">
                        </form>
                        """);
                }

                s.LoggedIn = true;
                return Results.Redirect(s.PatientLaunch ? "/oauth2/default/patient-select" : "/oauth2/default/scope-authorize");
            });
        });

        app.MapGet("/oauth2/default/patient-select", (HttpContext http) =>
            WithOpenEmrSession(http, state(), s => s.LoggedIn ? PatientSelectPage(s.Csrf) : Results.StatusCode(StatusCodes.Status403Forbidden)));

        app.MapPost("/oauth2/default/patient-select", async (HttpContext http) =>
        {
            var st = state();
            var form = await http.Request.ReadFormAsync().ConfigureAwait(false);
            return WithOpenEmrSession(http, st, s =>
            {
                if (!s.LoggedIn || form["csrf_token"] != s.Csrf || !PatientIds.Contains(form["patient_id"].ToString()))
                {
                    return Results.StatusCode(StatusCodes.Status400BadRequest);
                }

                s.PatientId = form["patient_id"];
                return Results.Redirect("/oauth2/default/scope-authorize");
            });
        });

        app.MapGet("/oauth2/default/scope-authorize", (HttpContext http) =>
            WithOpenEmrSession(http, state(), s =>
                s.LoggedIn && (!s.PatientLaunch || s.PatientId is not null) ? ConsentPage(s.Csrf) : Results.StatusCode(StatusCodes.Status403Forbidden)));

        app.MapPost("/oauth2/default/scope-authorize", async (HttpContext http) =>
        {
            var st = state();
            var form = await http.Request.ReadFormAsync().ConfigureAwait(false);
            return WithOpenEmrSession(http, st, s =>
            {
                if (form["proceed"] != "1" || form["csrf_token_form"] != s.Csrf)
                {
                    return Results.StatusCode(StatusCodes.Status400BadRequest);
                }

                var code = Token();
                st.Codes[code] = s;
                var separator = s.RedirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                return Results.Redirect($"{s.RedirectUri}{separator}code={code}&state={WebUtility.UrlEncode(s.State)}");
            });
        });
    }

    private static IResult BeginLaunch(HttpContext http, StubState s, bool patientLaunch)
    {
        // Like the real sidecar: every /launch mints a fresh, unauthenticated session.
        var id = Token();
        var session = new SidecarSession { State = Token(), PatientLaunch = patientLaunch };
        s.SidecarSessions[id] = session;
        http.Response.Cookies.Append(SessionCookie, id, new CookieOptions { Path = "/agentforge", HttpOnly = true, SameSite = SameSiteMode.Lax });

        var self = $"{http.Request.Scheme}://{http.Request.Host}";
        var authorizeOrigin = s.AuthorizeOrigin?.GetLeftPart(UriPartial.Authority) ?? self;
        var callback = self + (patientLaunch ? "/agentforge/callback" : "/agentforge/agenda/callback");
        return Results.Redirect(
            $"{authorizeOrigin}/oauth2/default/authorize?response_type=code&client_id={(patientLaunch ? "patient-client" : "agenda-client")}" +
            $"&redirect_uri={WebUtility.UrlEncode(callback)}&state={session.State}");
    }

    private static IResult CompleteLaunch(HttpContext http, StubState s, string landing)
    {
        var code = http.Request.Query["code"].ToString();
        if (!http.Request.Cookies.TryGetValue(SessionCookie, out var id)
            || !s.SidecarSessions.TryGetValue(id, out var session)
            || !s.Codes.TryRemove(code, out var grant)
            || grant.State != http.Request.Query["state"] || session.State != grant.State)
        {
            return Results.Text("No pending SMART launch for this session.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (s.RefuseCallback)
        {
            return Results.Text("You are not authorized to open this patient's chart.", statusCode: StatusCodes.Status403Forbidden);
        }

        session.Authenticated = true;
        session.PatientId = grant.PatientId;
        return Results.Redirect(landing);
    }

    private static IResult WithOpenEmrSession(HttpContext http, StubState s, Func<OpenEmrSession, IResult> handle) =>
        http.Request.Cookies.TryGetValue(OpenEmrCookie, out var id) && s.OpenEmrSessions.TryGetValue(id, out var session)
            ? handle(session)
            : Results.StatusCode(StatusCodes.Status401Unauthorized);

    private static IResult LoginPage(string csrf, string? invalid, StubState st)
    {
        const string loginPath = "/oauth2/default/provider/login";
        var action = st.LoginFormPostsTo is null ? loginPath : new Uri(st.LoginFormPostsTo, loginPath).ToString();

        // What a hostile or compromised page could do while it is being filled: repoint the form elsewhere.
        var retarget = st.RetargetTo is null || st.RetargetWhenTyped is null
            ? string.Empty
            : $$"""
                document.querySelector('input[name={{st.RetargetWhenTyped}}]').addEventListener('input', function () {
                  document.getElementById('userLogin').setAttribute('action', '{{new Uri(st.RetargetTo, loginPath)}}');
                });
                """;

        // Synchronous, so the stub has counted it before Playwright's fill returns.
        return Html($$"""
            <h4>Sign In</h4>
            <form method="post" name="userLogin" id="userLogin" action="{{action}}">
              {{(invalid is null ? string.Empty : $"<div class=\"alert alert-danger\"><p>{invalid}</p></div>")}}
              <input type="hidden" name="csrf_token_form" value="{{csrf}}" />
              <input type="text" name="username" value="">
              <input type="password" name="password" value="">
              <button type="submit" name="user_role" value="api">OpenEMR Login</button>
              <button type="submit" name="user_role" value="portal-api">Patient Login</button>
              <input type="checkbox" name="persist_login" value="1">
            </form>
            <script>
              document.querySelector('input[name=password]').addEventListener('input', function () {
                var x = new XMLHttpRequest(); x.open('POST', '/oauth2/default/typed', false); x.send();
              });
              {{retarget}}
            </script>
            """);
    }

    private static IResult PatientSelectPage(string csrf) => Html($$"""
        <h4>Patient Selection</h4>
        <table><tbody>
          {{string.Join('\n', PatientIds.Select((p, i) => $"<tr><td>Stub Patient {i + 1}</td><td><button data-patient-id=\"{p}\" class=\"btn patient-btn\">Select patient</button></td></tr>"))}}
        </tbody></table>
        <form method="post" name="patientForm" id="patientForm" action="/oauth2/default/patient-select">
          <input type="hidden" name="csrf_token" value="{{csrf}}" />
          <input id="patient_id" type="hidden" name="patient_id" value="" />
        </form>
        <script>
          window.addEventListener('load', function () {
            document.querySelectorAll('.patient-btn').forEach(function (b) {
              b.addEventListener('click', function (evt) {
                document.getElementById('patient_id').value = evt.target.dataset.patientId;
                document.getElementById('patientForm').submit();
              });
            });
          });
        </script>
        """);

    private static IResult ConsentPage(string csrf) => Html($$"""
        <form method="post" name="userLogin" id="userLogin" action="/oauth2/default/scope-authorize">
          <input type="checkbox" class="app-scope" name="scope[openid]" value="openid" checked>
          <input type="hidden" class="app-scope" name="scope[launch]" value="launch">
          <div id="dynamic-scopes-container"></div>
          <input type="hidden" name="csrf_token_form" value="{{csrf}}" />
          <button type="submit" name="proceed" value="1" id="authorize-btn">Authorize</button>
          <button type="button" onclick="window.history.back();">Cancel</button>
        </form>
        <script>
          document.addEventListener('DOMContentLoaded', function () {
            var form = document.getElementById('userLogin');
            var button = document.getElementById('authorize-btn');
            button.addEventListener('click', function (event) {
              event.preventDefault();
              var proceed = document.createElement('input');
              proceed.type = 'hidden'; proceed.name = 'proceed'; proceed.value = '1';
              form.appendChild(proceed);
              button.disabled = true;
              form.submit();
            });
          });
        </script>
        """);

    private static IResult Html(string body) =>
        Results.Content($"<!doctype html><html><head><title>stub</title></head><body>{body}</body></html>", "text/html");

    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

internal sealed class StubState
{
    public string Username { get; init; } = "stub-clinician";

    // Random per run, so no test value can be mistaken for - or become - a real password.
    public string Password { get; init; } = "stub-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    public Uri? AuthorizeOrigin { get; init; }
    public bool RefuseCallback { get; init; }
    public MfaKind Mfa { get; init; }

    /// <summary>An origin the login form's action names instead of the page's own.</summary>
    public Uri? LoginFormPostsTo { get; init; }

    /// <summary>The login form is repointed at <see cref="RetargetTo"/> once this field ("username" or "password") is typed into.</summary>
    public string? RetargetWhenTyped { get; init; }

    public Uri? RetargetTo { get; init; }

    /// <summary>The sidecar's GET /patient drops the connection.</summary>
    public bool DropPatientCheck { get; init; }

    public int LoginPostCount;
    public int PasswordTypedCount;
    public ConcurrentDictionary<string, SidecarSession> SidecarSessions { get; } = new();
    public ConcurrentDictionary<string, OpenEmrSession> OpenEmrSessions { get; } = new();
    public ConcurrentDictionary<string, OpenEmrSession> Codes { get; } = new();

    public IEnumerable<string> AuthenticatedSessionIds => SidecarSessions.Where(kv => kv.Value.Authenticated).Select(kv => kv.Key);
}

internal enum MfaKind
{
    None,
    Totp,
    U2f,
}

internal sealed class SidecarSession
{
    public required string State { get; init; }
    public required bool PatientLaunch { get; init; }
    public bool Authenticated { get; set; }
    public string? PatientId { get; set; }
}

internal sealed class OpenEmrSession
{
    public required string RedirectUri { get; init; }
    public required string State { get; init; }
    public required bool PatientLaunch { get; init; }
    public required string Csrf { get; init; }
    public bool LoggedIn { get; set; }
    public string? PatientId { get; set; }
}
