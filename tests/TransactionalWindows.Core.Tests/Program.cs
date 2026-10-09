using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.Errors;
using TransactionalWindows.Core.State;

static class Assert
{
    public static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }

    public static void True(bool value, string name)
    {
        if (!value) throw new InvalidOperationException($"{name}: expected true");
    }

    public static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{name}: expected {typeof(T).Name}");
    }
}

public static class Program
{
    private static Transaction NewTransaction() => Transaction.Create(
        TransactionId.New(), "S-1-5-21-test", "C:\\Apps\\Editor.exe", "", "C:\\Apps", "C:\\Overlay", DateTimeOffset.UtcNow);

    private static Transaction Move(Transaction transaction, TransactionState target)
        => TransactionStateMachine.Transition(transaction, target, DateTimeOffset.UtcNow);

    public static void Main()
    {
        var transaction = NewTransaction();
        Assert.Equal(TransactionState.Created, transaction.CurrentState, "TEST-CORE-001 initial state");
        transaction = Move(transaction, TransactionState.Starting);
        Assert.Equal(TransactionState.Starting, transaction.CurrentState, "TEST-CORE-001 Created -> Starting");
        transaction = Move(transaction, TransactionState.Running);
        Assert.Equal(TransactionState.Running, transaction.CurrentState, "TEST-CORE-002 Starting -> Running");
        transaction = Move(transaction, TransactionState.Quiescing);
        Assert.Equal(TransactionState.Quiescing, transaction.CurrentState, "TEST-CORE-003 Running -> Quiescing");
        transaction = Move(transaction, TransactionState.DiffReady);
        Assert.Equal(TransactionState.DiffReady, transaction.CurrentState, "TEST-CORE-004 Quiescing -> DiffReady");
        transaction = Move(transaction, TransactionState.Committing);
        Assert.Equal(TransactionState.Committing, transaction.CurrentState, "TEST-CORE-005 DiffReady -> Committing");
        transaction = Move(transaction, TransactionState.Completed);
        Assert.Equal(TransactionState.Completed, transaction.CurrentState, "Completed result");
        Assert.Throws<DomainException>(() => Move(transaction, TransactionState.Failed), "TEST-CORE-008 Completed terminal");

        var discardTransaction = Move(Move(Move(Move(NewTransaction(), TransactionState.Starting), TransactionState.Running), TransactionState.Quiescing), TransactionState.DiffReady);
        discardTransaction = Move(discardTransaction, TransactionState.Discarding);
        Assert.Equal(TransactionState.Discarding, discardTransaction.CurrentState, "TEST-CORE-006 DiffReady -> Discarding");
        discardTransaction = Move(discardTransaction, TransactionState.Completed);
        Assert.Equal(TransactionState.Completed, discardTransaction.CurrentState, "Discarding -> Completed");

        var failed = Move(Move(NewTransaction(), TransactionState.Starting), TransactionState.Failed);
        Assert.Throws<DomainException>(() => Move(failed, TransactionState.Aborted), "TEST-CORE-009 Failed terminal");
        var aborted = Move(Move(NewTransaction(), TransactionState.Starting), TransactionState.Aborted);
        Assert.Throws<DomainException>(() => Move(aborted, TransactionState.Running), "TEST-CORE-010 Aborted terminal");
        Assert.Throws<DomainException>(() => Move(NewTransaction(), TransactionState.Running), "TEST-CORE-007 invalid transition");

        var stableId = transaction.Id;
        Assert.Equal(stableId, transaction.Id, "TEST-CORE-011 TransactionId stable");
        Assert.True(stableId != TransactionId.New(), "ID uniqueness");
        var p1 = new ProcessNode { Id = ProcessNodeId.New(), TransactionId = stableId, Pid = 42, ProcessCreationIdentity = 1, StartedAt = DateTimeOffset.UtcNow, Status = ProcessNodeStatus.Running };
        var p2 = p1 with { Id = ProcessNodeId.New(), ProcessCreationIdentity = 2 };
        Assert.True(p1.Id != p2.Id && p1.Pid == p2.Pid, "TEST-CORE-012 PID is not identity");

        var file = new FileChange { Id = FileChangeId.New(), TransactionId = stableId, Path = "C:\\data.txt", ObjectType = FileObjectType.File, Operation = FileChangeOperation.Modify };
        var item = new DiffItem { Id = DiffItemId.New(), Type = DiffItemType.File, FileChangeId = file.Id, Status = DiffItemStatus.Applied, ApplyState = ApplyState.Applied };
        Assert.Equal(FileChangeOperation.Modify, file.Operation, "TEST-CORE-013 operation");
        Assert.Equal(DiffItemStatus.Applied, item.Status, "TEST-CORE-013 status independent");

        var registry = new RegistryChange { Id = RegistryChangeId.New(), TransactionId = stableId, Hive = RegistryHive.CurrentUser, View = RegistryView.View64, KeyPath = "Software\\Vendor", Operation = RegistryChangeOperation.Set };
        Assert.True(registry.Id.Value != Guid.Empty, "TEST-CORE-014 Registry identity");

        var nonexistent = DiffItemId.New();
        var dependency = new Dependency(item.Id, nonexistent, "missing prerequisite");
        var diff = new Diff { Id = DiffId.New(), TransactionId = stableId, Generation = 1, Items = new[] { item }, Dependencies = new[] { dependency } };
        Assert.Throws<DomainException>(diff.ValidateDependencies, "TEST-CORE-015 dependency cannot reference nonexistent item");
        var validDependency = new Dependency(item.Id, item.Id, "self dependency should be rejected");
        var validDiff = diff with { Dependencies = new[] { validDependency } };
        Assert.Throws<DomainException>(validDiff.ValidateDependencies, "dependency self-reference rejected");

        Console.WriteLine("Core tests passed.");
    }
}
