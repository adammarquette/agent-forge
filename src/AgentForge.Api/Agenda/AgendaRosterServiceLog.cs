using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Agenda;

/// <summary>
/// Source-generated log messages for <see cref="AgendaRosterService"/> (CA1848). Diagnostic stream: no patient
/// id and no exception message, which can echo one from a FHIR path - the roster row and the exception type
/// instead. Which patient a row was is the access-audit trail's to say (CONVENTIONS.md §7).
/// </summary>
internal static partial class AgendaRosterServiceLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Agenda summary failed for roster row {RosterRow} of {RosterSize}: {ExceptionType}")]
    public static partial void PatientSummaryFailed(ILogger logger, int rosterRow, int rosterSize, Type exceptionType);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Agenda patient name unavailable for roster row {RosterRow} of {RosterSize}, falling back to id: {ExceptionType}")]
    public static partial void PatientNameUnavailable(ILogger logger, int rosterRow, int rosterSize, Type exceptionType);
}
