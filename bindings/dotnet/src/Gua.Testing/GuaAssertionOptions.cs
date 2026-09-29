namespace Gua.Testing;

public sealed class GuaAssertionOptions
{
    public static GuaAssertionOptions Default { get; } = new();

    public Action<string> Fail { get; init; } = message => throw new GuaAssertionException(message);

    public GuaDiagnosticOptions? Diagnostics { get; init; }
    public GuaDiagnosticsSession? DiagnosticsSession { get; init; }
    public GuaTraceSession? Trace { get; init; }
}
