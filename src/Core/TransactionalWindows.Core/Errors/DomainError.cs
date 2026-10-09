namespace TransactionalWindows.Core.Errors;

public enum DomainErrorCode
{
    InvalidStateTransition,
    TransactionNotFound,
    TransactionAlreadyCompleted,
    InvalidSelection,
    DependencyViolation,
    ConflictDetected,
    UnsupportedOperation,
    SecurityViolation,
    RecoveryRequired,
    InvalidArgument,
    VersionConflict
}

public enum ErrorSeverity { Info, Warning, Error, Critical }

public sealed record DomainError(
    DomainErrorCode Code,
    string Message,
    ErrorSeverity Severity = ErrorSeverity.Error,
    bool Retryable = false,
    bool FatalTransaction = false);

public sealed class DomainException : InvalidOperationException
{
    public DomainException(DomainError error) : base(error.Message) => Error = error;
    public DomainError Error { get; }
}

