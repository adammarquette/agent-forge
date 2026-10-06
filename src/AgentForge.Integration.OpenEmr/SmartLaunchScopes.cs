namespace AgentForge.Integration.OpenEmr;

/// <summary>
/// The scope supersets the two AgentForge SMART clients are registered for, and the rule that
/// decides what belongs in them: <b>a FHIR resource the sidecar reads needs a read scope on the
/// client that reads it</b> (INTERFACES.md §A.4).
/// </summary>
/// <remarks>
/// <para>
/// Held in code rather than only in the deployment manifests because OpenEMR's
/// <c>ScopeRepository::finalizeScopes()</c> intersects the requested scopes against the registered
/// client's stored list and <b>silently drops</b> the difference - a missing scope surfaces as a
/// 401 on one FHIR call, arbitrarily far from the registration that omitted it. These lists are the
/// superset <c>tools/RegisterSmartClients</c> registers; the sidecar's own
/// <c>OpenEmr__Scopes__*</c> / <c>OpenEmrAgenda__Scopes__*</c> (docker-compose.yml,
/// .railway/railway.ts) are what each launch requests and must stay within them.
/// reference: DEPLOYMENT.md §3.
/// </para>
/// <para>
/// Every resource scope here is a V1 <c>&lt;context&gt;/&lt;Resource&gt;.read</c> from the fork's
/// <c>ServerScopeListEntity::fhirResourceScopesV1()</c>; a string outside that catalog is rejected
/// at <c>/authorize</c> and <c>/registration</c> as <c>invalid_scope</c>, and the resource names are
/// PascalCase for the same reason.
/// </para>
/// </remarks>
public static class SmartLaunchScopes
{
    /// <summary>SMART scope context prefix for the single-patient launch (a patient is in context).</summary>
    public const string PatientContext = "patient";

    /// <summary>SMART scope context prefix for the roster/agenda launch (no patient in context).</summary>
    public const string UserContext = "user";

    /// <summary>
    /// The single-patient launch client (redirect <c>/agentforge/callback</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> PatientLaunch =
    [
        "openid", "fhirUser", "launch", "launch/patient", "api:fhir",
        ReadScopeFor(PatientContext, "Patient"),
        ReadScopeFor(PatientContext, "Encounter"),
        ReadScopeFor(PatientContext, "Observation"),
        ReadScopeFor(PatientContext, "DocumentReference"),
        ReadScopeFor(PatientContext, "Binary"),
        ReadScopeFor(PatientContext, "Condition"),
        ReadScopeFor(PatientContext, "AllergyIntolerance"),
        ReadScopeFor(PatientContext, "MedicationRequest"),
        ReadScopeFor(PatientContext, "Procedure"),
        ReadScopeFor(PatientContext, "DiagnosticReport"),
        // Not a data need of any tool: the FR-AUTH-2 relationship gate
        // (PatientRelationshipGate) resolves "is this requester clinically related to this
        // patient" from the clinic day's Appointment participants, and runs on every launch.
        // Without it that search 401s, the gate cannot resolve and fails closed, and every
        // per-patient launch is refused 403. patient/ rather than user/: OpenEMR's
        // _rest_routes_fhir_r4_us_core_3_1_0.inc.php binds a patient-context Appointment search
        // to the launch patient, which is all the gate reads and strictly less than user/ grants.
        ReadScopeFor(PatientContext, "Appointment"),
        "api:oemr",
    ];

    /// <summary>
    /// The roster/agenda launch client (redirect <c>/agentforge/agenda/callback</c>). It has no
    /// patient in context, so every resource scope is <c>user/</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> AgendaLaunch =
    [
        "openid", "fhirUser", "launch", "api:fhir",
        ReadScopeFor(UserContext, "Appointment"),
        ReadScopeFor(UserContext, "Patient"),
        ReadScopeFor(UserContext, "Encounter"),
        ReadScopeFor(UserContext, "Observation"),
        ReadScopeFor(UserContext, "DocumentReference"),
        ReadScopeFor(UserContext, "Binary"),
        ReadScopeFor(UserContext, "Condition"),
        ReadScopeFor(UserContext, "AllergyIntolerance"),
        ReadScopeFor(UserContext, "MedicationRequest"),
        ReadScopeFor(UserContext, "Procedure"),
        ReadScopeFor(UserContext, "DiagnosticReport"),
    ];

    /// <summary>The patient launch client's redirect path behind the front door.</summary>
    public const string PatientRedirectPath = "/agentforge/callback";

    /// <summary>The roster/agenda launch client's redirect path behind the front door.</summary>
    public const string AgendaRedirectPath = "/agentforge/agenda/callback";

    /// <summary>
    /// The V1 read scope for <paramref name="fhirResource"/> in <paramref name="scopeContext"/>,
    /// e.g. <c>patient/Appointment.read</c>.
    /// </summary>
    public static string ReadScopeFor(string scopeContext, string fhirResource) =>
        $"{scopeContext}/{fhirResource}.read";

    /// <summary>
    /// True for a SMART resource scope (<c>patient/…</c> or <c>user/…</c> with a permission
    /// suffix) - the kind a FHIR read needs; false for <c>openid</c>, <c>launch/patient</c> and
    /// <c>api:*</c>.
    /// </summary>
    public static bool IsResourceScope(string scope) =>
        (scope.StartsWith(PatientContext + "/", StringComparison.Ordinal)
            || scope.StartsWith(UserContext + "/", StringComparison.Ordinal))
        && scope.Contains('.', StringComparison.Ordinal);

    /// <summary>
    /// The scopes in <paramref name="expected"/> that <paramref name="scopeString"/> does not
    /// carry, in <paramref name="expected"/>'s order. <paramref name="scopeString"/> is split on a
    /// single ASCII space and compared ordinally - how OpenEMR itself reads a client's stored list
    /// and a token's granted one - so a scope joined by any other separator counts as missing.
    /// </summary>
    public static IReadOnlyList<string> MissingFrom(IEnumerable<string> expected, string? scopeString)
    {
        var present = new HashSet<string>((scopeString ?? string.Empty).Split(' '), StringComparer.Ordinal);
        return [.. expected.Where(scope => !present.Contains(scope))];
    }

    /// <summary>
    /// The scope superset an OpenEMR client with these redirect URIs (OpenEMR's <c>|</c>-joined
    /// <c>oauth_clients.redirect_uri</c>) should be registered for: <see cref="PatientLaunch"/> for
    /// the patient callback, <see cref="AgendaLaunch"/> for the agenda one. Null when the client
    /// is neither, or claims both - a client that cannot be identified is left alone.
    /// </summary>
    public static IReadOnlyList<string>? RegisteredSupersetFor(string redirectUris)
    {
        var lists = redirectUris
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(uri => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath.TrimEnd('/') : null)
            .Select(path => path switch
            {
                not null when path.EndsWith(AgendaRedirectPath, StringComparison.Ordinal) => AgendaLaunch,
                not null when path.EndsWith(PatientRedirectPath, StringComparison.Ordinal) => PatientLaunch,
                _ => null,
            })
            .Distinct()
            .ToList();

        return lists is [{ } only] ? only : null;
    }
}
