namespace Astm.Abstractions.Diagnostics;

public enum AstmSeverity { Information, Warning, Error }

/// <summary>
/// Stable, machine-readable finding. <see cref="Code"/> never changes meaning between versions;
/// <see cref="Message"/> is invariant English for logs and is translated by the host when shown to users.
/// </summary>
public sealed record AstmDiagnostic(string Code, AstmSeverity Severity, string Message, string? Location = null)
{
    public override string ToString() => Location is null ? $"{Code}: {Message}" : $"{Code} [{Location}]: {Message}";
}
