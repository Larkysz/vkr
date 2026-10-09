using TransactionalWindows.Core.Errors;

namespace TransactionalWindows.Core.Domain;

public sealed record ProcessNode
{
    public required ProcessNodeId Id { get; init; }
    public required TransactionId TransactionId { get; init; }
    public required int Pid { get; init; }
    public required long ProcessCreationIdentity { get; init; }
    public int? ParentPid { get; init; }
    public string? ImagePath { get; init; }
    public string? CommandLine { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? ExitedAt { get; init; }
    public int? ExitCode { get; init; }
    public required ProcessNodeStatus Status { get; init; }
}

public sealed record Dependency(DiffItemId Dependent, DiffItemId Prerequisite, string Reason);
public sealed record DiffItem
{
    public required DiffItemId Id { get; init; }
    public required DiffItemType Type { get; init; }
    public FileChangeId? FileChangeId { get; init; }
    public RegistryChangeId? RegistryChangeId { get; init; }
    public required DiffItemStatus Status { get; init; }
    public SelectionState Selection { get; init; }
    public ApplyState ApplyState { get; init; }
}

public sealed record Diff
{
    public required DiffId Id { get; init; }
    public required TransactionId TransactionId { get; init; }
    public required long Generation { get; init; }
    public required IReadOnlyList<DiffItem> Items { get; init; }
    public required IReadOnlyList<Dependency> Dependencies { get; init; }

    public void ValidateDependencies()
    {
        var itemIds = Items.Select(item => item.Id).ToHashSet();
        foreach (var dependency in Dependencies)
        {
            if (!itemIds.Contains(dependency.Dependent) || !itemIds.Contains(dependency.Prerequisite))
                throw new DomainException(new(DomainErrorCode.DependencyViolation, "Diff dependency references an item outside the Diff."));
            if (dependency.Dependent == dependency.Prerequisite)
                throw new DomainException(new(DomainErrorCode.DependencyViolation, "Diff dependency cannot be self-referential."));
        }
    }
}

