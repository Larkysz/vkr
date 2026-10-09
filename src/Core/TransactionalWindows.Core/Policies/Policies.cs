namespace TransactionalWindows.Core.Policies;

public sealed record FilesystemScopePolicy
{
    public required string VolumeIdentity { get; init; }
    public bool IsOneLocalNtfsVolume => true;
}

public sealed record RegistryScopePolicy
{
    public bool AllowCurrentUser { get; init; } = true;
    public IReadOnlyList<string> AllowedLocalMachineSoftwarePaths { get; init; } = Array.Empty<string>();
}

public sealed record ConflictPolicyDefinition
{
    public Domain.ConflictPolicy Policy { get; init; } = Domain.ConflictPolicy.FailClosed;
}

public sealed record UnsupportedOperationPolicy
{
    public Domain.OperationDisposition DefaultDisposition { get; init; } = Domain.OperationDisposition.Blocked;
}

