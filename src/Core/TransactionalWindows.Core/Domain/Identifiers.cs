namespace TransactionalWindows.Core.Domain;

public readonly record struct TransactionId(Guid Value)
{
    public static TransactionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct DiffId(Guid Value)
{
    public static DiffId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct FileChangeId(Guid Value)
{
    public static FileChangeId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct RegistryChangeId(Guid Value)
{
    public static RegistryChangeId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ProcessNodeId(Guid Value)
{
    public static ProcessNodeId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct DiffItemId(Guid Value)
{
    public static DiffItemId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

