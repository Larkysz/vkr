namespace TransactionalWindows.Core.Diagnostics;

public enum DiagnosticLevel { Trace, Debug, Information, Warning, Error, Critical }

public interface IDiagnostics
{
    void Write(DiagnosticLevel level, string category, string message, IReadOnlyDictionary<string, string>? properties = null);
}

