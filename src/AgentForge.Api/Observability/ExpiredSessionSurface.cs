namespace AgentForge.Api.Observability;

/// <summary>
/// The closed set of surfaces that may reach <see cref="ExpiredSessionSignal"/>'s exported
/// <c>surface</c> label — one value per place the app converts an aged-out SMART session into a
/// refusal (<c>ARCHITECTURE.md</c> §5.7).
/// </summary>
/// <remarks>
/// Enum-like for the same reason <c>AuthorizationDecisionReason</c> is: a label multiplies the
/// counter's series, survives for the retention of the metrics store and leaves the process, so it
/// is derived from where the refusal happened and never from the patient, the requester, the
/// session or the correlation id (CONVENTIONS.md §7's no-PHI rule, which covers telemetry
/// as well as logs). The launch gate has no value here because it does not refuse — it 500s, which
/// <c>ARCHITECTURE.md</c> §5.7 records as an open gap rather than a choke point. A separate change
/// </remarks>
public static class ExpiredSessionSurface
{
    /// <summary>The hub refused before the turn opened: the session was already dead when it was read.</summary>
    public const string ChatPreTurn = "chat-pre-turn";

    /// <summary>
    /// The hub refused mid-turn: live when the turn started, dead by the time a call went out. Kept
    /// distinct from <see cref="ChatPreTurn"/> because the two answer different questions — a
    /// pre-turn refusal is the wall being hit between turns, a mid-turn one is a turn long enough to
    /// straddle it, which is what a BFF timeout drifting from the token lifetime looks like.
    /// </summary>
    public const string ChatTurn = "chat-turn";

    /// <summary><c>POST /evidence/ask</c> — §5.7's other metered choke point.</summary>
    public const string EvidenceAsk = "evidence-ask";

    /// <summary><c>GET /evidence/document/{id}</c> — the click-to-source fetch.</summary>
    public const string EvidenceDocument = "evidence-document";

    /// <summary><c>GET /agenda</c> — the roster fan-out, the longest-running flow in the product.</summary>
    public const string Agenda = "agenda";

    /// <summary>Every surface that may ever reach the label, so the cardinality claim is checkable.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ChatPreTurn, ChatTurn, EvidenceAsk, EvidenceDocument, Agenda];
}
