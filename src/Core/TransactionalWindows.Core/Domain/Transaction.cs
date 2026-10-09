using TransactionalWindows.Core.Errors;

namespace TransactionalWindows.Core.Domain;

public sealed record Transaction
{
    public required TransactionId Id { get; init; }
    public required string OwnerSid { get; init; }
    public required string ApplicationPath { get; init; }
    public required string Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string OverlayRoot { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required TransactionState CurrentState { get; init; }
    public ProcessNodeId? RootProcessNodeId { get; init; }
    public required int SchemaVersion { get; init; }
    public long Version { get; init; }

    public static Transaction Create(TransactionId id, string ownerSid, string applicationPath, string arguments, string workingDirectory, string overlayRoot, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(ownerSid) || string.IsNullOrWhiteSpace(applicationPath))
            throw new DomainException(new(DomainErrorCode.InvalidArgument, "Owner SID and application path are required."));
        return new()
        {
            Id = id, OwnerSid = ownerSid, ApplicationPath = applicationPath, Arguments = arguments,
            WorkingDirectory = workingDirectory, OverlayRoot = overlayRoot, CreatedAt = createdAt,
            CurrentState = TransactionState.Created, SchemaVersion = 1, Version = 0
        };
    }
}

