using System.Net;
using System.Runtime.CompilerServices;
using AgentForge.Api.Chat;
using AgentForge.IntegrationTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentForge.IntegrationTests.Api;

/// <summary>
/// Runs the real BFF host in-process against the real QA OpenEMR deployment and the real
/// Anthropic API - the only tier that can prove <see cref="global::Program"/>'s actual DI wiring,
/// session handling, and SignalR hub work end to end (CONVENTIONS.md §8.2 - nothing here is mocked).
/// Reuses <see cref="OpenEmrQaFixture"/> and the <c>LlmQa__*</c> variables already configured for
/// Epic 4/6's QA tiers, mapped onto the BFF's own (differently-named) configuration sections via
/// in-memory config rather than requiring a second, parallel set of CI variables.
/// </summary>
public sealed class BffQaFixture : WebApplicationFactory<global::Program>
{
    /// <summary>The underlying OpenEMR QA connection (base URL, site, test patient id/token).</summary>
    public OpenEmrQaFixture OpenEmr { get; }

    // The page key each seeded jar's session was rendered with, so a hub connection presents it as index.html does.
    private readonly ConditionalWeakTable<CookieContainer, string> _contextKeys = new();

    /// <summary>Every log line the running host has written, for the no-token-in-logs assertion.</summary>
    public CapturingLoggerProvider CapturedLogs { get; } = new();

    private readonly string _llmApiKey;
    private readonly string _llmModel;
    private readonly string _agendaClientId;
    private readonly string _agendaClientSecret;

    public BffQaFixture()
    {
        OpenEmr = new OpenEmrQaFixture();
        if (string.IsNullOrEmpty(OpenEmr.Options.TestAccessToken) || string.IsNullOrEmpty(OpenEmr.Options.TestPatientId))
        {
            throw new InvalidOperationException(
                $"BFF integration tests require {QaOpenEmrOptions.SectionName}__TestAccessToken and " +
                $"{QaOpenEmrOptions.SectionName}__TestPatientId - the token-custody and delivery tests need a " +
                "real authenticated session end to end (CONVENTIONS.md §8.2 - nothing here is mocked).");
        }

        var llmConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("LlmQa");
        var apiKey = llmConfig["ApiKey"];
        var model = llmConfig["Model"];
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "BFF integration tests require LlmQa__ApiKey and LlmQa__Model environment variables - " +
                "the pre-visit brief exercised here calls the real Anthropic API, not a mock.");
        }

        _llmApiKey = apiKey;
        _llmModel = model;

        // Daily Agenda's own registered confidential client (ARCHITECTURE.md §19.2) - a
        // separately registered client, not OpenEmr__ClientId, per ScopeRepository::finalizeScopes
        // silently dropping scopes outside a client's own registration (INTERFACES.md A.4).
        // Registered against the QA staging server 2026-07-11.
        var agendaConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("OpenEmrAgenda");
        var agendaClientId = agendaConfig["ClientId"];
        var agendaClientSecret = agendaConfig["ClientSecret"];
        if (string.IsNullOrWhiteSpace(agendaClientId) || string.IsNullOrWhiteSpace(agendaClientSecret))
        {
            throw new InvalidOperationException(
                "BFF Daily Agenda integration tests require OpenEmrAgenda__ClientId and " +
                "OpenEmrAgenda__ClientSecret environment variables - the agenda launch flow " +
                "exercised here needs a real registered confidential client, not a mock.");
        }

        _agendaClientId = agendaClientId;
        _agendaClientSecret = agendaClientSecret;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenEmr:BaseUrl"] = OpenEmr.Options.BaseUrl,
            ["OpenEmr:Site"] = OpenEmr.Options.Site,
            ["OpenEmr:ClientId"] = "qa-integration-test-client",
            ["OpenEmr:Scopes:0"] = "patient/patient.read",
            ["Bff:PublicBaseUrl"] = "https://bff-integration-test.invalid",
            // Daily Agenda (ARCHITECTURE.md §19) - its own registered confidential client, per
            // ScopeRepository::finalizeScopes silently dropping scopes outside a client's own
            // registration (INTERFACES.md A.4). Scope list matches exactly what was
            // registered against the QA staging server - keep this list
            // and the registered client's own scope list in sync if either ever changes.
            ["OpenEmrAgenda:ClientId"] = _agendaClientId,
            ["OpenEmrAgenda:ClientSecret"] = _agendaClientSecret,
            ["OpenEmrAgenda:Scopes:0"] = "openid",
            ["OpenEmrAgenda:Scopes:1"] = "fhirUser",
            ["OpenEmrAgenda:Scopes:2"] = "launch",
            ["OpenEmrAgenda:Scopes:3"] = "api:fhir",
            ["OpenEmrAgenda:Scopes:4"] = "user/Patient.read",
            ["OpenEmrAgenda:Scopes:5"] = "user/encounter.read",
            ["OpenEmrAgenda:Scopes:6"] = "user/medication.read",
            ["OpenEmrAgenda:Scopes:7"] = "user/prescription.read",
            ["OpenEmrAgenda:Scopes:8"] = "user/drug.read",
            ["OpenEmrAgenda:Scopes:9"] = "user/list.read",
            ["OpenEmrAgenda:Scopes:10"] = "user/allergy.read",
            ["OpenEmrAgenda:Scopes:11"] = "user/vital.read",
            ["OpenEmrAgenda:Scopes:12"] = "user/procedure.read",
            ["OpenEmrAgenda:Scopes:13"] = "user/surgery.read",
            ["OpenEmrAgenda:Scopes:14"] = "user/document.read",
            ["OpenEmrAgenda:Scopes:15"] = "user/Appointment.read",
            ["Llm:ApiKey"] = _llmApiKey,
            ["Llm:Model"] = _llmModel,
            ["Llm:InputPricePerMillionTokensUsd"] = "0",
            ["Llm:OutputPricePerMillionTokensUsd"] = "0",
        }));

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, SeedSessionStartupFilter>();
            services.AddSingleton<ILoggerProvider>(CapturedLogs);

            // Program.cs requires Cookie.SecurePolicy = Always (the cross-origin iframe embedding
            // needs SameSite=None+Secure - see its own comment on why). The in-memory TestServer
            // this fixture runs on serves everything over http://, and CookieContainer correctly
            // (RFC 6265) refuses to re-attach a Secure cookie to an http request - the session
            // cookie set by SeedAuthenticatedSessionAsync would silently never reach the SignalR
            // hub's own requests, and neither would the WebSocket upgrade's hand-copied Cookie
            // header, which reads the same jar. Relaxing just SecurePolicy here (production is
            // untouched) is the surgical fix; forcing the TestServer's base address to https
            // instead hung the connection, and was never re-tried after the transport changed.
            services.PostConfigure<SessionOptions>(options => options.Cookie.SecurePolicy = CookieSecurePolicy.None);
        });
    }

    /// <summary>
    /// An <see cref="HttpClient"/> against the real in-memory host, carrying <paramref name="cookies"/>
    /// on every request and capturing every <c>Set-Cookie</c> it receives back into the same jar -
    /// shared with a <c>HubConnection</c>'s handler so both see the same session.
    /// </summary>
    public HttpClient CreateHttpClient(CookieContainer cookies) =>
        new(new CookieContainerHandler(cookies) { InnerHandler = Server.CreateHandler() }) { BaseAddress = Server.BaseAddress };

    /// <summary>
    /// Seeds a real, authenticated <c>PatientSessionContext</c> (the pre-obtained QA test token -
    /// see <see cref="Support.QaOpenEmrOptions.TestAccessToken"/>) via the test-only route, bypassing
    /// the interactive SMART login this automated test cannot drive. Returns the cookie jar carrying
    /// the resulting session cookie.
    /// </summary>
    public async Task<CookieContainer> SeedAuthenticatedSessionAsync(CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        using var client = CreateHttpClient(cookies);

        var query = $"?accessToken={Uri.EscapeDataString(OpenEmr.Options.TestAccessToken!)}" +
            $"&site={Uri.EscapeDataString(OpenEmr.Options.Site)}" +
            $"&patientId={Uri.EscapeDataString(OpenEmr.Options.TestPatientId!)}" +
            "&clinicianIdentity=qa-test-clinician";
        var response = await client.PostAsync(SeedSessionStartupFilter.SeedSessionPath + query, content: null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _contextKeys.AddOrUpdate(cookies, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        return cookies;
    }

    /// <summary>
    /// Seeds a real, authenticated <c>AgendaSessionContext</c> (and, if provided, its roster) via
    /// the test-only route - the agenda-flow counterpart to <see cref="SeedAuthenticatedSessionAsync"/>.
    /// Unlike that method, this does not require a real registered agenda OAuth client: the
    /// authorization-boundary behavior it exists to test (401 with no session, 403 for a patient
    /// outside the seeded roster) never depends on the token being real.
    /// </summary>
    public async Task<CookieContainer> SeedAuthenticatedAgendaSessionAsync(
        IReadOnlyList<string>? rosterPatientIds, CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        using var client = CreateHttpClient(cookies);

        var query = "?accessToken=test-agenda-token" +
            $"&site={Uri.EscapeDataString(OpenEmr.Options.Site)}" +
            "&clinicianIdentity=qa-test-clinician" +
            (rosterPatientIds is { Count: > 0 }
                ? $"&rosterPatientIds={Uri.EscapeDataString(string.Join(',', rosterPatientIds))}"
                : string.Empty);
        var response = await client.PostAsync(SeedSessionStartupFilter.SeedAgendaSessionPath + query, content: null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return cookies;
    }

    /// <summary>
    /// Builds a <see cref="HubConnection"/> to the real chat hub, carrying <paramref name="cookies"/>
    /// so the hub sees whatever session they hold. The transport choice lives in
    /// <see cref="TestServerHubConnection"/>, because it decides whether the hub can read a session
    /// at all - and nothing here would notice if it changed. Presents the page key the jar's patient session was
    /// seeded with, as <c>index.html</c> presents the one <c>GET /patient</c> gave it. A separate change
    /// </summary>
    public HubConnection BuildHubConnection(CookieContainer cookies) =>
        TestServerHubConnection.Build(
            Server, cookies, ChatHub.Route, _contextKeys.TryGetValue(cookies, out var contextKey) ? contextKey : null);
}
