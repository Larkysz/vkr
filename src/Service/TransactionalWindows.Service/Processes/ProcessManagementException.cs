namespace TransactionalWindows.Service.Processes;

public enum ProcessErrorCode
{
    LaunchFailed,
    JobAssignmentFailed,
    OwnerMismatch,
    ObservationIncomplete,
    QuiesceTimeout,
    NativeFailure,
    UnsupportedIsolation,
    AlreadyLaunched
}

public sealed class ProcessManagementException(ProcessErrorCode code, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public ProcessErrorCode Code { get; } = code;
}
