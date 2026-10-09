using AgentForge;
using AgentForge.Agent;
using AgentForge.Api.Agenda;
using AgentForge.Api.Chat;
using AgentForge.Api.Health;
using AgentForge.Api.Launch;
using AgentForge.Api.Observability;
using AgentForge.Api.OpenApi;
using AgentForge.Api.Patient;
using AgentForge.Api.Security;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Auth;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Api.LlmProviders;
using AgentForge.Mcp;
using AgentForge.Mcp.Authorization;
using AgentForge.Observability;
using AgentForge.Verification;
using AgentForge.Agents;
using AgentForge.Api.Evidence;
using AgentForge.Data;
using AgentForge.Documents;
using AgentForge.Retrieval;
using AgentForge.Agents.Ingestion;
using AgentForge.Api.Ingestion;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Refit;
using AgentForge.Api.Contracts;

// Rendered from the contract types alone, so it exits before any configuration is validated.
if (EvidenceGraphSchemaExport.OutputPathFrom(args) is { } graphSchemaExportPath)
{
    await EvidenceGraphSchemaExport.WriteAsync(graphSchemaExportPath);
    return;
}

// Same shape: the tool catalog is static, so no configuration is needed.
if (McpToolSchemaExport.OutputPathFrom(args) is { } toolSchemaExportPath)
{
    await McpToolSchemaExport.WriteAsync(toolSchemaExportPath);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Validate the composition in EVERY environment, not only the Development boot the host turns this
// on for by default. What it catches is a service that captures a shorter-lived one - and the case
// that matters is FR-AUTH-2's: a singleton IPatientRelationshipAuthorizer holding the request-scoped
// IOpenEmrFhirClient, which makes the per-turn memo of authorization decisions process-wide and
// hands one requester's permit to the next. Off, that composition boots and serves (verified);
// on, it refuses to start. A one-word AddSingleton anywhere after
// AddPatientRelationshipAuthorization() wins at resolution. Four unit-test classes build the real
// host (FrameworkLogLevelTests, AccessAuditOtlpExclusionTests, TracerPipelineOrderTests,
// and the OpenAPI contract test) but none pins this authorizer's lifetime; the
// integration-tier Production boot test proposed in a separate change covers it once
// merged - until then this startup check is the only thing standing where the change would
// actually ship.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

builder.Services.AddOptions<OpenEmrOptions>()
    .Bind(builder.Configuration.GetSection(OpenEmrOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<BffOptions>()
    .Bind(builder.Configuration.GetSection(BffOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// Daily Agenda (ARCHITECTURE.md §19) is an optional, additive feature - not every environment
// configures it yet (confirmed live: ValidateOnStart here crashed the whole host at startup for
// tests/deployments with no reason to set OpenEmrAgenda config, e.g. HealthEndpointReadinessTests).
// No ValidateOnStart, matching ObservabilityOptions' precedent below: the app must still boot and
// serve the existing single-patient flow without this configured. Validation still runs lazily
// the first time these options are actually resolved (AgendaLaunchService/AgendaRosterService),
// producing a clean error there rather than an unguarded NullReferenceException
// (AgendaOpenEmrOptions.Validate).
builder.Services.AddOptions<AgendaOpenEmrOptions>()
    .Bind(builder.Configuration.GetSection(AgendaOpenEmrOptions.SectionName))
    .ValidateDataAnnotations();
builder.Services.AddOptions<AgendaOptions>()
    .Bind(builder.Configuration.GetSection(AgendaOptions.SectionName))
    .ValidateDataAnnotations();
// The clinic's wall clock. Bound with a working default (ClinicOptions.DefaultTimeZone), so the
// app boots without the section - but validated, because an unresolvable zone would otherwise
// surface as a blanket FR-AUTH-2 refusal that reads like a policy decision.
builder.Services.AddOptions<ClinicOptions>()
    .Bind(builder.Configuration.GetSection(ClinicOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DataProtectionKeyRingOptions>()
    .Bind(builder.Configuration.GetSection(DataProtectionKeyRingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// AgentOptions.TurnDeadline has a built-in default (Epic 10), so binding is optional - the app
// must still boot and use the default when this section is absent from configuration.
builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// Optional self-hosted infra (observability/docker-compose.yml) - not required/ValidateOnStart,
// unlike OpenEmr above and Llm (AddLlmProvider), since the app must still boot and serve traffic without it.
builder.Services.AddOptions<ObservabilityOptions>()
    .Bind(builder.Configuration.GetSection(ObservabilityOptions.SectionName));
// The budget every /ready dependency probe is bounded by. Has a safe built-in default so the app
// boots without the section - but validated on start, because a zero or negative budget would
// cancel every probe before it began and leave readiness unable to answer at all.
builder.Services.AddOptions<ReadinessOptions>()
    .Bind(builder.Configuration.GetSection(ReadinessOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Per-request/per-hub-invocation scope: a fresh instance carries exactly one call's token and
// correlation id, so nothing here can leak between two different sessions or hub calls
// (ARCHITECTURE.md D11, §11).
builder.Services.AddScoped<ScopedAccessTokenProvider>();
builder.Services.AddScoped<IScopedAccessTokenProvider>(sp => sp.GetRequiredService<ScopedAccessTokenProvider>());
builder.Services.AddScoped<IAccessTokenProvider>(sp => sp.GetRequiredService<ScopedAccessTokenProvider>());
// Concrete type registered too, so CorrelationIdMiddleware can *set* the id it establishes at
// ingress while every consumer keeps the read-only interface.
builder.Services.AddScoped<MutableCorrelationIdAccessor>();
builder.Services.AddScoped<ICorrelationIdAccessor>(sp => sp.GetRequiredService<MutableCorrelationIdAccessor>());
builder.Services.AddScoped<ScopedClinicianIdentityAccessor>();
builder.Services.AddScoped<IScopedClinicianIdentityAccessor>(sp => sp.GetRequiredService<ScopedClinicianIdentityAccessor>());
builder.Services.AddScoped<IClinicianIdentityAccessor>(sp => sp.GetRequiredService<ScopedClinicianIdentityAccessor>());

// Single-instance in-memory stores (v1 deployment assumption - see their own doc comments).
builder.Services.AddSingleton<IConversationStateStore, InMemoryConversationStateStore>();
builder.Services.AddSingleton<IChatMessageOutbox, InMemoryChatMessageOutbox>();
// Per-session LLM turn budget - chat, evidence asks, agenda summaries. Built-in defaults, so
// the section is optional. Shared with the hand-built ChatHubHost so the two cannot drift.
builder.Services.AddConversationTurnBudget(builder.Configuration);

// The framework's HttpClient loggers open an "HTTP {HttpMethod} {Uri}" scope around every outbound call at any
// level, and OTel exports it on every line logged inside (Polly's attempts): /fhir/Patient/{id}.
builder.Services.ConfigureHttpClientDefaults(http => http.RemoveAllLoggers());

builder.Services.AddTransient<AuthHandler>();
builder.Services.AddTransient<CorrelationIdHandler>();

// AuthHandler is Transient and injects IAccessTokenProvider (Scoped by registration) - but
// IHttpClientFactory constructs a named client's DelegatingHandler chain using its OWN internal
// handler-building scope, never the calling hub invocation's DI scope, so no HandlerLifetime value
// makes AuthHandler observe the token ChatSessionCoordinator set (confirmed live 2026-07-10: every
// real FHIR tool call failed FR-AUTH-1's "no token" check even after forcing frequent handler
// rebuilds). The actual fix lives in ScopedAccessTokenProvider itself: its storage is a
// static AsyncLocal, not a per-instance field, so it flows correctly through the real async call
// chain regardless of which DI scope constructed which object along the way. Nothing special is
// needed here as a result - plain AddRefitClient, default handler pooling and its connection-reuse
// benefit both intact.
builder.Services.AddRefitClient<IOpenEmrAuthApi>()
    .ConfigureHttpClient((sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OpenEmrOptions>>().Value.BaseUrl))
    .AddHttpMessageHandler<CorrelationIdHandler>()
    .AddStandardResilienceHandler();

// Staging OpenEMR routinely takes 4-8s per FHIR call; under the Daily Agenda's parallel fan-out the
// framework's 10s attempt default was tripping, forcing retries/cancellations and "Summary
// unavailable" rows. Give the FHIR client a wider budget from the same config
// the validated OpenEmrOptions binds to (invalid values still fail fast via that options validation).
var openEmrSection = builder.Configuration.GetSection(OpenEmrOptions.SectionName);
var fhirAttemptTimeout = TimeSpan.FromSeconds(
    openEmrSection.GetValue<int?>(nameof(OpenEmrOptions.FhirAttemptTimeoutSeconds))
        ?? OpenEmrOptions.DefaultFhirAttemptTimeoutSeconds);
var fhirTotalTimeout = TimeSpan.FromSeconds(
    openEmrSection.GetValue<int?>(nameof(OpenEmrOptions.FhirTotalRequestTimeoutSeconds))
        ?? OpenEmrOptions.DefaultFhirTotalRequestTimeoutSeconds);

builder.Services.AddRefitClient<IOpenEmrFhirApi>()
    .ConfigureHttpClient((sp, client) =>
    {
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OpenEmrOptions>>().Value.BaseUrl);
        // HttpClient's outer timeout must exceed the pipeline's total, or it cancels first.
        client.Timeout = fhirTotalTimeout + TimeSpan.FromSeconds(30);
    })
    .AddHttpMessageHandler<AuthHandler>()
    .AddHttpMessageHandler<CorrelationIdHandler>()
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = fhirAttemptTimeout;
        options.TotalRequestTimeout.Timeout = fhirTotalTimeout;
        // Handler invariant: SamplingDuration must be >= 2x AttemptTimeout.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(fhirAttemptTimeout.TotalSeconds * 2);
        options.Retry.MaxRetryAttempts = 2;
    });

// The model tier: validated LlmProviderOptions, the Anthropic and Gemini clients under one resilience budget,
// and the ILlmProvider Llm__Provider selects.
builder.Services.AddLlmProvider(builder.Configuration);

builder.Services.AddScoped<IOpenEmrAuthClient, OpenEmrAuthClient>();
builder.Services.AddScoped<IOpenEmrFhirClient, OpenEmrFhirClient>();

// AuditingMcpToolServer decorates the real tool server with the access-audit trail (FR-AUTH-4) -
// registered as IMcpToolServer so every consumer gets the audited version without knowing it.
builder.Services.AddScoped<McpToolServer>();
builder.Services.AddScoped<IMcpToolServer>(sp => new AuditingMcpToolServer(
    sp.GetRequiredService<McpToolServer>(),
    sp.GetRequiredService<IClinicianIdentityAccessor>(),
    sp.GetRequiredService<ICorrelationIdAccessor>(),
    sp.GetRequiredService<ILogger<AccessAudit>>()));

// Scoped, so its per-turn memo of relationship decisions lives exactly one chat turn or one
// agenda fan-out branch (FR-AUTH-2 - see PatientRelationshipAuthorizer's remarks). The lifetime
// is pinned by PatientRelationshipAuthorizationRegistrationTests, which is why it lives behind a
// named method rather than inline here.
builder.Services.AddPatientRelationshipAuthorization();

builder.Services.AddScoped<IMcpToolDispatcher, McpToolDispatcher>();

// Stateless (no per-request data of their own), so a single shared instance is fine. Missing since
// Epic 7 first wired the verifier into AgentOrchestrator - the app never actually booted with that
// change in place until this was added; caught by a Program.cs smoke test during Epic 8's work.
builder.Services.AddSingleton<ISourceAttributionEngine, SourceAttributionEngine>();
builder.Services.AddSingleton(sp => new CardiologyConstraintEngine(
    CardiologyConstraintRules.Default, sp.GetRequiredService<ILogger<CardiologyConstraintEngine>>()));
builder.Services.AddSingleton<IClinicalResponseVerifier, ClinicalResponseVerifier>();

builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();

// Week 2 (Multimodal Evidence Agent, ARCHITECTURE-DOCUMENTS.md) - additive and OPTIONAL: only wired when a
// database connection string is configured, so the host still boots for the Week 1 flows / tests without
// a database (matching the AgendaOptions "optional feature" precedent above, and the smoke-test rule that
// the app must boot with whatever config an environment actually sets).
var weekTwoEnabled = !string.IsNullOrWhiteSpace(
    builder.Configuration.GetSection("AgentForgeData")["ConnectionString"]);
// Bound unconditionally, unlike AddAgentForgeData's own copy below: VectorIndexHealthCheck is registered
// in every environment and has to be able to tell "not configured" (Degraded) from "configured and not
// answering" (503), which it cannot do from options that only exist where Week 2 is wired
builder.Services.AddOptions<AgentForgeDataOptions>()
    .Bind(builder.Configuration.GetSection(AgentForgeDataOptions.SectionName));
// Bound unconditionally, same reason as AgentForgeDataOptions above: RerankerHealthCheck is registered
// in every environment and has to read the real Cohere:ApiKey value to tell "not configured" from
// "configured and not answering," which AddAgentForgeRetrieval only binds when Week 2 is wired
builder.Services.AddOptions<CohereOptions>()
    .Bind(builder.Configuration.GetSection(CohereOptions.SectionName));
// Always registered: VectorIndexHealthCheck reads it in every environment.
builder.Services.AddSingleton<DataStoreStartupState>();
builder.Services.AddOptions<DataStoreStartupOptions>()
    .Bind(builder.Configuration.GetSection(DataStoreStartupOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// Export mode renders the document and exits, so it must not touch a database it only needs in order
// to serve traffic - the committed spec is generated with a placeholder connection string.
var openApiExportPath = OpenApiSpecExport.OutputPathFrom(args);
if (weekTwoEnabled)
{
    builder.Services.AddAgentForgeData(builder.Configuration);
    builder.Services.AddAgentForgeDocuments();
    builder.Services.AddAgentForgeRetrieval(builder.Configuration);
    builder.Services.AddAgentForgeEvidenceAgent();

    // The chat's Week 2 tools (get_document_facts / retrieve_evidence): registered only with Week 2, since
    // they depend on the derived-fact store and the evidence retriever. The dispatcher references them
    // optionally, so the Week 1 chat still boots (degraded to no document/guideline citations) without them.
    builder.Services.AddScoped<IDocumentFactsTool, DocumentFactsTool>();
    builder.Services.AddScoped<IEvidenceTool, EvidenceTool>();

    // Week 2 ingestion (E2): the front desk uploads through OpenEMR's own Documents; the oe-module-agentforge
    // upload hook then calls POST /documents/ingest with the content, and the sidecar extracts + persists the
    // derived facts citing the OpenEMR DocumentReference. No write-back - OpenEMR is authoritative for the
    // source document, so there is no document write client, resolver, or category config.
    builder.Services.AddSingleton<IDerivedFactMapper, DerivedFactMapper>();
    builder.Services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();

    // Deploy-time schema management (W2-D14): migrations, then the guideline seed, once per process - after
    // the host is listening and retried until the store answers, so a store down at boot is a /ready 503
    // naming it rather than a host that never starts (maintainer ruling).
    if (openApiExportPath is null)
    {
        builder.Services.AddSingleton<IDataStoreStartupWork, MigrateAndSeedStartupWork>();
        builder.Services.AddHostedService<DataStoreStartupService>();
    }
}

builder.Services.AddScoped<SmartLaunchService>();
builder.Services.AddScoped<AgendaLaunchService>();
builder.Services.AddScoped<ChatSessionCoordinator>();

// TimeProvider.System, not DateTimeOffset.UtcNow directly: gives AgendaRosterServiceTests a fake
// clock seam instead of a bespoke IClock (ARCHITECTURE.md §19.1's single-captured-"now" design).
builder.Services.AddSingleton(TimeProvider.System);
// One definition of "the current clinic day" for everything that reads OpenEMR's calendar - the
// Daily Agenda roster and the FR-AUTH-2 relationship gate. Singleton: it holds only the resolved
// zone.
builder.Services.AddSingleton<ClinicClock>();
builder.Services.AddScoped<IAgendaPatientSummaryRunner, AgendaPatientSummaryRunner>();
// Singleton: it outlives the request so a roster reload re-serves summaries uncharged.
builder.Services.AddSingleton<IAgendaSummaryCache, InMemoryAgendaSummaryCache>();
builder.Services.AddScoped<AgendaRosterService>();
builder.Services.AddScoped<PatientContextService>();

// Epic 9 (Observability): the app-side metrics/tracing that feed the self-hosted dashboard, and
// the readiness checks NFR-HEALTH-1 requires against OpenEMR, the LLM provider and that dashboard's
// own backend - plus the vector index NFR-HEALTH-W2-1 adds. A single
// AgentForgeMetrics instance so every Counter/Histogram it owns aggregates across the whole
// process, not per-request.
builder.Services.AddSingleton<AgentForgeMetrics>();
builder.Services.AddSingleton<IAgentForgeMetrics>(sp => sp.GetRequiredService<AgentForgeMetrics>());
// Scoped, not singleton: it reads the correlation id of the flow it is recording against
builder.Services.AddScoped<ExpiredSessionSignal>();
// The eval run the image build made against this build's own source (Dockerfile stage `evals`),
// published as the series AgentForgeEvalCategoryRegression evaluates. Optional: a missing run is
// logged by the loader and the sidecar boots without the series.
builder.Services.AddOptions<EvalResultsOptions>()
    .Bind(builder.Configuration.GetSection(EvalResultsOptions.SectionName));
builder.Services.AddSingleton<EvalResultsSnapshotLoader>();
builder.Services.AddSingleton(sp => new EvalResultsMetrics(
    sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
    sp.GetRequiredService<EvalResultsSnapshotLoader>().Load(
        sp.GetRequiredService<IOptions<EvalResultsOptions>>().Value,
        sp.GetRequiredService<IHostEnvironment>().ContentRootPath)));

// Registered outside the weekTwoEnabled block on purpose - see the health check's own remarks: readiness
// has to report the vector index as unconfigured where Week 2 is not wired, rather than say nothing.
// The probe opens its own short-lived connection, so it needs no DbContext and no Week 2 registration.
builder.Services.AddSingleton<IVectorIndexProbe, NpgsqlVectorIndexProbe>();
// The five /ready checks and their typed clients, the three external ones behind a result cache
builder.Services.AddReadinessChecks();

// Resolved from configuration before the host is built, like the Loki endpoint below.
var traceExport = TraceExportPlan.From(
    builder.Configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>()
    ?? new ObservabilityOptions());

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("agentforge-api"))
    .WithMetrics(metrics => metrics
        .AddMeter(AgentForgeMetrics.MeterName)
        // Explicit buckets for the turn-duration histogram, WITHOUT which
        // AgentForgeHighTurnLatencyP95's 26s threshold is unresolvable: OTel .NET's defaults leave
        // 25 as the only boundary between 10 and 50, so histogram_quantile interpolates across a
        // 25-second-wide band exactly where NFR-PERF-1's budget sits. The boundaries live on the
        // instrument's own class so AlertRuleThresholdTests can pin them against the threshold it
        // parses out of the rule file itself - the pin is two-way. AgentTurnBriefHistogramExportTests
        // pins that the boundaries reach /metrics under the {turn_type="brief"} label the alert reads.
        .AddView(
            instrumentName: "agentforge.agent_turn.duration",
            metricStreamConfiguration: new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [.. AgentForgeMetrics.AgentTurnDurationBucketBoundariesSeconds],
            })
        // The same fix for the two Week 2 SLO histograms, whose 6s and 11s targets otherwise sit inside the
        // 5-10s and 10-25s default buckets. A name typo here is silent, so Week2HistogramExportTests scrapes
        // the host's /metrics and requires exactly these boundaries.
        .AddView(
            instrumentName: "agentforge.evidence_retrieval.duration",
            metricStreamConfiguration: new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [.. AgentForgeMetrics.EvidenceRetrievalDurationBucketBoundariesSeconds],
            })
        .AddView(
            instrumentName: "agentforge.document_ingestion.duration",
            metricStreamConfiguration: new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [.. AgentForgeMetrics.DocumentIngestionDurationBucketBoundariesSeconds],
            })
        // Microsoft.Extensions.Http.Resilience's AddStandardResilienceHandler (the OpenEMR/LLM
        // clients above) already emits retry/circuit-breaker telemetry under this meter name - no
        // custom retry-counting code needed for the dashboard's "tool-call + retry counts" panel.
        .AddMeter("Polly")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        // Runtime above is GC/heap only - Process gives real process CPU%/RSS for Epic 12's
        // load-test baselines (REQUIREMENTS.md NFR-PERF-2/4), also useful ongoing since none of the other
        // instrumentation here surfaces true OS-level resource usage.
        .AddProcessInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(AgentForgeActivitySource.Name)
            // Exception events carry ex.Message, which the scrubber cannot rewrite after the fact.
            .AddAspNetCoreInstrumentation(o => o.RecordException = false)
            .AddHttpClientInstrumentation(o => o.RecordException = false)
            // First processor, so every exporter below reads the span only after it is scrubbed.
            .AddProcessor(new SpanPhiScrubber());
        // Local debugging only (Observability:TraceConsoleExporter): console spans land in the platform's
        // stdout retention, so no deployed environment sets it.
        if (traceExport.ConsoleExporter)
        {
            tracing.AddConsoleExporter();
        }

        // Only where an environment names a trace backend - staging's Tempo, never production ('s
        // disposition, .railway/railway.ts TEMPO_IMAGE_BY_ENV).
        if (traceExport.OtlpEndpoint is { } traceEndpoint)
        {
            tracing.AddOtlpExporter(otlp =>
            {
                // Full /v1/traces path, used as-is (HttpProtobuf does not append the signal path).
                otlp.Endpoint = traceEndpoint;
                otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
        }
    });

// Correlation id (a logging scope - ChatSessionCoordinator, CONVENTIONS.md §7) actually
// rendered somewhere real; OTel's own log exporter, not a specific provider like Serilog/NLog, so
// the sidecar stays provider-agnostic per CONVENTIONS.md §7's sample-configs note.
builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeScopes = true;
    options.IncludeFormattedMessage = true;
    // Same service label the metrics/traces resource uses, so Loki tags these logs `service_name=agentforge-api`.
    options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("agentforge-api"));
    options.AddConsoleExporter();

    // Optional: also ship logs to a self-hosted Loki via its native OTLP/HTTP endpoint (Epic 107), so
    // sidecar logs are searchable in Grafana next to the metrics dashboards. Read from configuration
    // directly (DI isn't built yet here), matching bffPathBase below. Fail open: unset -> console only;
    // a wrong/unreachable endpoint never blocks the app (the exporter batches and drops on failure).
    // DEPLOYMENT.md
    var lokiOtlpEndpoint = builder.Configuration
        .GetSection(ObservabilityOptions.SectionName)[nameof(ObservabilityOptions.LokiOtlpEndpoint)];
    if (!string.IsNullOrWhiteSpace(lokiOtlpEndpoint))
    {
        options.AddOtlpExporter(otlp =>
        {
            // Full /otlp/v1/logs path is used as-is (HttpProtobuf does not append the signal path).
            otlp.Endpoint = new Uri(lokiOtlpEndpoint);
            otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
        });
    }
});

// The access-audit trail names the patient: console only, never an OTel exporter.
builder.Logging.KeepAccessAuditOffOpenTelemetry();

// IncludeScopes exports the hosting scope's RequestPath, which can carry a document id.
builder.Logging.ScrubRequestPathFromLogScopes();

// Read directly from configuration (not IOptions<BffOptions>) - this runs before
// builder.Build(), so the DI container isn't available to resolve options from yet.
var bffPathBase = builder.Configuration.GetSection(BffOptions.SectionName)[nameof(BffOptions.PathBase)]
    ?? string.Empty;

// Persist the DataProtection key ring to durable, shared storage. It protects the pending-launch cookie
// (state + PKCE verifier, PendingLaunchCookie) and the session cookie below; the default in-memory ring
// is regenerated per process, so a redeploy or a second replica cannot decrypt a cookie an earlier
// process wrote - the launch callback then fails with "No pending SMART launch for this session".
// Sidecar DataProtection persistence. Empty KeyRingPath keeps the in-memory
// default for local dev / unit tests; every deployed environment must set it to a mounted volume.
var dataProtectionOptions = builder.Configuration.GetSection(DataProtectionKeyRingOptions.SectionName)
    .Get<DataProtectionKeyRingOptions>() ?? new DataProtectionKeyRingOptions();
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName(dataProtectionOptions.ApplicationName);
if (dataProtectionOptions.KeyRingPath.Length > 0)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionOptions.KeyRingPath));
}

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    if (bffPathBase.Length > 0)
    {
        // Behind the reverse proxy, the sidecar is first-party with OpenEMR *as long
        // as the whole SMART launch stays on the proxy host*. Lax then suffices and keeps its CSRF
        // protection. This requires the OpenEMR module's launch URL (agentforge_launch_uri) to point
        // at the proxy front door, not the sidecar's own Railway host - otherwise /launch and
        // /callback land on different domains and the session cookie is lost.
        options.Cookie.Path = bffPathBase;
        options.Cookie.SameSite = SameSiteMode.Lax;
    }
    else
    {
        // Root-hosted fallback (no reverse-proxy PathBase). Since the top-level-launch decision, the
        // OpenEMR module opens the sidecar as a top-level tab (window.open), not a cross-origin iframe,
        // so this session cookie is first-party to the sidecar's own origin and SameSite=Lax would
        // suffice - None here is now temporary permissiveness, not a requirement. Kept at None until the
        // None->Lax tightening lands; the same-origin reverse-proxy path above (Lax) is the end state.
        // a separate change (None->Lax tightening), a separate change (top-level launch), a separate change (reverse proxy)
        options.Cookie.SameSite = SameSiteMode.None;
    }
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.IdleTimeout = TimeSpan.FromMinutes(30);
});

builder.Services.AddSignalR();

// OpenAPI document for the HTTP surface (INTERFACES.md §D). Registration is
// harmless in every environment; the document is only *served* in non-prod (see MapOpenApi below),
// and the same document is what --export-openapi renders to the committed spec.
builder.Services.AddAgentForgeOpenApi();

var app = builder.Build();

// Observable gauges exist only once their owner is built; nothing else asks for it.
app.Services.GetRequiredService<EvalResultsMetrics>();

if (traceExport.EndpointRejected)
{
    TraceExportPlanLog.EndpointRejected(app.Logger);
}

if (bffPathBase.Length > 0)
{
    // Must run before UseSession/UseStaticFiles/routing - nginx terminates TLS
    // and proxies a subpath, so both need to happen before anything downstream reads the request
    // path or scheme.
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    });
    app.UsePathBase(bffPathBase);
}

// Before anything that logs: every request runs inside its own correlation scope, adopting a
// well-formed inbound X-Correlation-Id so a caller's trace continues rather than restarting here
// (FR-OBS-1). Hub traffic is left alone - ChatSessionCoordinator scopes each turn itself.
app.UseCorrelationId();

// Serve the OpenAPI document at /openapi/v1.json in non-prod only: the spec carries real endpoint
// and config detail, and CONVENTIONS.md §6 forbids unauthenticated doc exposure in prod.
if (!app.Environment.IsProduction())
{
    app.MapOpenApi();
}

app.UseSession();
// The hub cannot read the session itself on every transport, so it is resolved here.
app.UseChatHubSession();
app.UseDefaultFiles();
// Serve .mjs as a JS MIME so browsers execute the vendored pdf.js ES modules (evidence.html); the default
// provider doesn't reliably map it, and a module served as octet-stream is rejected outright.
var staticContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".mjs"] = "text/javascript";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypes });

app.MapLaunchEndpoints();
app.MapAgendaLaunchEndpoints();
app.MapAgendaEndpoints();
app.MapPatientEndpoints();
if (weekTwoEnabled)
{
    app.MapEvidenceEndpoints();
    app.MapIngestionEndpoints();
}

app.MapHub<ChatHub>(ChatHub.Route);

// /health: liveness only (the process is up and serving) - no dependency checks, so it can't flap
// on a transient OpenEMR/LLM blip. /ready: the real NFR-HEALTH-1/NFR-HEALTH-W2-1 checks
// (OpenEmrHealthCheck, LlmProviderHealthCheck, ObservabilityHealthCheck, VectorIndexHealthCheck,
// RerankerHealthCheck),
// tagged "ready" above, each bounded by ReadinessOptions.ProbeTimeout so no one silent dependency can
// hold this endpoint open. /ready answers JSON naming each check, since the default
// writer's one word named no dependency at all; /health keeps that default, having
// no entries to name.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = ReadinessResponse.WriteAsync,
});
app.MapPrometheusScrapingEndpoint();

if (openApiExportPath is not null)
{
    // Last, so the document describes every route mapped above. Start() rather than Run() because the
    // document provider reads the routes off the built pipeline, which only exists once the host starts;
    // port 0 keeps that start from claiming a fixed one.
    app.Urls.Clear();
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();
    await OpenApiSpecExport.WriteAsync(app.Services, openApiExportPath);
    await app.StopAsync();
    return;
}

app.Run();

/// <summary>
/// Top-level statements generate an internal Program class; this partial declaration makes it
/// accessible to <c>WebApplicationFactory&lt;Program&gt;</c> from the integration test assembly
/// (standard ASP.NET Core testability convention - no behavior change).
/// </summary>
public partial class Program;
