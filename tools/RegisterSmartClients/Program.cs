using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Integration.OpenEmr;

// Registers the two AgentForge SMART launch clients on a fresh/reseeded OpenEMR with the correct scope
// lists - INCLUDING patient/Binary.read (patient) and user/Binary.read (agenda), which OpenEMR's
// finalizeScopes drops when the client isn't registered for them, breaking click-to-source,
// and patient/Appointment.read, without which the FR-AUTH-2 relationship gate cannot resolve and every
// per-patient launch is refused 403.
//
// The OAuth clients are DB-only and vanish on an OpenEMR reseed (DEPLOYMENT.md §4 "OAuth clients").
// This makes their re-creation reproducible instead of a manual Admin-GUI dance: it POSTs the registrations,
// then prints the new client ids/secrets, the sidecar environment variables to set, and the two ways to
// enable the clients + skip the per-launch authorization prompt (SQL, or the two header buttons on the
// client's Admin -> System -> API Clients page - the checkboxes there are readonly and save nothing).
//
// Usage:
//   dotnet run --project tools/RegisterSmartClients -- <frontDoorBaseUrl> [site]
//   e.g. dotnet run --project tools/RegisterSmartClients -- http://localhost:8080
// The base URL MUST be the reverse-proxy front door (never a service's own host), so the redirect_uri and the
// OAuth aud match the launch (DEPLOYMENT.md §2). No secrets are read or written by this tool.

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: RegisterSmartClients <frontDoorBaseUrl> [site]");
    return 1;
}

var baseUrl = args[0].TrimEnd('/');
var site = args.Length > 1 ? args[1] : "default";

// The section names the sidecar binds on: OpenEmrOptions.SectionName (src/AgentForge.Integration.OpenEmr/
// OpenEmrOptions.cs) and AgendaOpenEmrOptions.SectionName (src/AgentForge.Api/Launch/AgendaOpenEmrOptions.cs).
// Held here as two constants rather than four literals: the agenda one printed "OPenEmrAgenda__ClientId" for
// two months, and a key nothing binds to raises no error - it surfaces as a 401 at the token exchange that
// reads exactly like a wrong secret. A separate change.
const string PatientSection = "OpenEmr";
const string AgendaSection = "OpenEmrAgenda";

// Registration scope lists are the SUPERSET the launch flow requests (the sidecar's OpenEmr__Scopes /
// OpenEmrAgenda__Scopes - DEPLOYMENT.md §3, .railway/railway.ts). finalizeScopes only grants a
// requested scope the client is registered for, so these must stay a superset - keep them in sync when a flow
// adds a scope. They live in AgentForge.Integration.OpenEmr.SmartLaunchScopes, so a unit test can assert that
// every FHIR resource the sidecar reads has a read scope on the client that reads it.
ClientSpec[] clients =
[
    new(
        Name: "AgentForge Copilot (patient launch)",
        RedirectPath: SmartLaunchScopes.PatientRedirectPath,
        ConfigIdVar: $"{PatientSection}__ClientId",
        ConfigSecretVar: $"{PatientSection}__ClientSecret",
        Scope: string.Join(' ', SmartLaunchScopes.PatientLaunch)),
    new(
        Name: "AgentForge Copilot (roster/agenda launch)",
        RedirectPath: SmartLaunchScopes.AgendaRedirectPath,
        ConfigIdVar: $"{AgendaSection}__ClientId",
        ConfigSecretVar: $"{AgendaSection}__ClientSecret",
        Scope: string.Join(' ', SmartLaunchScopes.AgendaLaunch)),
];

using var http = new HttpClient();
var registered = new List<(ClientSpec Spec, string ClientId, string? ClientSecret)>();

foreach (var spec in clients)
{
    Console.WriteLine($"Registering '{spec.Name}' ...");
    var body = new
    {
        application_type = "private",
        client_name = spec.Name,
        redirect_uris = new[] { baseUrl + spec.RedirectPath },
        grant_types = new[] { "authorization_code", "refresh_token" },
        response_types = new[] { "code" },
        token_endpoint_auth_method = "client_secret_post",
        scope = spec.Scope,
    };

    using var response = await http.PostAsJsonAsync($"{baseUrl}/oauth2/{site}/registration", body);
    var payload = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"  FAILED: {(int)response.StatusCode} {response.StatusCode} - {payload}");
        return 1;
    }

    using var doc = JsonDocument.Parse(payload);
    var root = doc.RootElement;
    var clientId = root.GetProperty("client_id").GetString()
        ?? throw new InvalidOperationException("Registration response carried no client_id.");
    var clientSecret = root.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
    var grantedScope = root.TryGetProperty("scope", out var sc) ? sc.GetString() ?? string.Empty : string.Empty;

    // finalizeScopes may still trim at registration; say which scope went, since a silent drop is exactly the
    // failure this tool exists to prevent - a separate change for Binary (click-to-source 404s) and a separate change for
    // Appointment (the FR-AUTH-2 gate cannot resolve, so every per-patient launch is refused 403).
    var granted = grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var dropped = spec.Scope
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(requested => !granted.Contains(requested, StringComparer.Ordinal))
        .ToArray();
    foreach (var scope in dropped)
    {
        var consequence = scope.Split('/') is [_, var resource] ? resource switch
        {
            "Binary.read" => " - click-to-source will 404",
            "Appointment.read" => " - every per-patient launch will be refused 403 (FR-AUTH-2)",
            _ => string.Empty,
        } : string.Empty;
        Console.Error.WriteLine($"  WARNING: registration dropped '{scope}'{consequence}.");
    }

    if (dropped.Length > 0)
    {
        Console.Error.WriteLine($"  granted scope: {grantedScope}");
    }

    registered.Add((spec, clientId, clientSecret));
    Console.WriteLine($"  client_id: {clientId}");
}

Console.WriteLine();
Console.WriteLine("=== Next steps (reference: DEPLOYMENT.md §4) ===");
Console.WriteLine();
Console.WriteLine("1) Set these on the sidecar - .env for compose, service variables for a hosted environment:");
foreach (var (spec, clientId, clientSecret) in registered)
{
    Console.WriteLine($"   {spec.ConfigIdVar} = {clientId}");
    Console.WriteLine($"   {spec.ConfigSecretVar} = {clientSecret ?? "(none returned - public client)"}");
}

Console.WriteLine();
Console.WriteLine("   Copy the names exactly. Configuration binding does not warn on a key nothing binds to,");
Console.WriteLine("   so a misspelled one fails much later, as a 401 from /oauth2/*/token that looks like a");
Console.WriteLine("   wrong secret. On compose, OPENEMR_CLIENT_ID / OPENEMR_CLIENT_SECRET in .env carry the");
Console.WriteLine("   patient pair; the agenda pair has nowhere to go there (DEPLOYMENT.md §3).");

Console.WriteLine();
Console.WriteLine("2) Enable each client + skip the per-launch authorization prompt. Either the SQL:");
var ids = string.Join(",", registered.Select(r => $"'{r.ClientId}'"));
Console.WriteLine($"   UPDATE oauth_clients SET is_enabled = 1, skip_ehr_launch_authorization_flow = 1 WHERE client_id IN ({ids});");
Console.WriteLine();
Console.WriteLine("   ...or, where the database is not reachable from here (a hosted environment on a private");
Console.WriteLine("   network has no route to MySQL from a workstation), by hand in the Admin GUI.");
Console.WriteLine();
Console.WriteLine("   FIRST, as a prerequisite: the global 'OAuth2 EHR-Launch Authorization Flow Skip' must be");
Console.WriteLine("   enabled (Admin -> Config -> Connectors). With it off, the per-client skip control is not");
Console.WriteLine("   rendered at all and the page shows the flag as unset whatever the database says.");
Console.WriteLine();
Console.WriteLine("   THEN, on Admin -> System -> API Clients -> the client, the two HEADER BUTTONS:");
Console.WriteLine("     'Enable Client'                          -> is_enabled = 1");
Console.WriteLine("     'Disable EHR Launch Authorization Flow'  -> skip_ehr_launch_authorization_flow = 1");
Console.WriteLine("   The second label is counter-intuitive but correct: it is named after the authorization");
Console.WriteLine("   flow, and skipping that flow is what the launch needs. Each button shows the ACTION, so");
Console.WriteLine("   it disappears once taken. The checkboxes on that page are NOT controls - they render");
Console.WriteLine("   readonly, their form has no action/method/submit, and the controller has no save route;");
Console.WriteLine("   ticking them changes nothing and says nothing.");
Console.WriteLine();
Console.WriteLine("   BootstrapOpenEmr does the same UPDATE for every AgentForge% client when it can reach MySQL.");

return 0;

internal sealed record ClientSpec(string Name, string RedirectPath, string ConfigIdVar, string ConfigSecretVar, string Scope);
