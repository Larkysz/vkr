namespace TransactionalWindows.Core.Domain;

public sealed record FileChange
{
    public required FileChangeId Id { get; init; }
    public required TransactionId TransactionId { get; init; }
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required FileObjectType ObjectType { get; init; }
    public required FileChangeOperation Operation { get; init; }
    public string? BaselineDigest { get; init; }
    public string? OverlayDigest { get; init; }
    public string? ContentReference { get; init; }
    public bool BaselineExists { get; init; }
    public bool OverlayExists { get; init; }
    public long? BaselineLength { get; init; }
    public long? OverlayLength { get; init; }
    public DateTimeOffset? BaselineLastWriteTimeUtc { get; init; }
    public DateTimeOffset? OverlayLastWriteTimeUtc { get; init; }
    public ProcessNodeId? SourceProcessNodeId { get; init; }
    public OperationDisposition Disposition { get; init; } = OperationDisposition.Supported;
}

public sealed record RegistryChange
{
    public required RegistryChangeId Id { get; init; }
    public required TransactionId TransactionId { get; init; }
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string KeyPath { get; init; }
    public string? ValueName { get; init; }
    public required RegistryChangeOperation Operation { get; init; }
    public string? ValueType { get; init; }
    public string? BaselineDigest { get; init; }
    public string? OverlayDigest { get; init; }
    public OperationDisposition Disposition { get; init; } = OperationDisposition.Unsupported;
}

