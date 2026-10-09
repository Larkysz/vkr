using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.State;
using TransactionalWindows.Service.Persistence;

namespace TransactionalWindows.Service;

/// <summary>
/// Coordinates the first user-mode transaction slice. A future Service host will
/// call the same boundaries after it has attached a process and minifilter.
/// </summary>
public sealed class FileTransactionWorkflow
{
    public Transaction Transaction { get; private set; }
    public FileOverlaySession Overlay { get; }
    public Diff? CurrentDiff { get; private set; }
    private readonly DurableTransactionStore? _store;

    public FileTransactionWorkflow(Transaction transaction, FileOverlaySession overlay, DurableTransactionStore? store = null)
    {
        if (transaction.Id != overlay.TransactionId)
            throw new ArgumentException("Transaction and overlay identifiers do not match.", nameof(overlay));
        if (store is not null)
        {
            if (transaction.CurrentState != TransactionState.Created || store.Get(transaction.Id) != transaction)
                throw new ArgumentException("A durable workflow requires an already persisted Created transaction.", nameof(transaction));
            if (!string.Equals(Path.GetFullPath(transaction.OverlayRoot), overlay.OverlayRoot, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFullPath(transaction.WorkingDirectory), overlay.BaselineRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Persisted roots do not match the overlay session.", nameof(transaction));
            if (PathsOverlap(store.Root, overlay.OverlayRoot) || PathsOverlap(store.Root, overlay.BaselineRoot))
                throw new ArgumentException("Metadata must be stored separately from baseline and overlay.", nameof(store));
        }
        Transaction = transaction;
        Overlay = overlay;
        _store = store;
    }

    public void Start(DateTimeOffset at)
    {
        Move(TransactionState.Starting, at);
        // The real launcher/driver handshake is a later phase. This workflow
        // explicitly records that the user-mode admission point is ready.
        Move(TransactionState.Running, at);
    }

    public Diff StopAndBuildDiff(long generation, DateTimeOffset at)
    {
        Move(TransactionState.Quiescing, at);
        var diff = FileDiffEngine.Build(Overlay, generation);
        if (_store is null)
        {
            Transaction = Transaction with { DiffId = diff.Id };
            Move(TransactionState.DiffReady, at);
        }
        else
        {
            Transaction = _store.RecordDiff(Transaction.Id, Transaction.OwnerSid, Transaction.Version,
                diff, Guid.NewGuid(), at).Transaction;
        }
        CurrentDiff = diff;
        return diff;
    }

    public FileCommitResult Commit(IReadOnlySet<DiffItemId> selected, DateTimeOffset at)
    {
        if (CurrentDiff is null) throw new InvalidOperationException("A Diff must be built before Commit.");
        Move(TransactionState.Committing, at);
        var result = new FileCommitEngine().Commit(Overlay, CurrentDiff, selected);
        if (result.Succeeded)
        {
            try { Overlay.Discard(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Move(TransactionState.Failed, at, "RecoveryRequired: overlay cleanup failed: " + ex.Message);
                return result with { Succeeded = false, Error = Transaction.LastError };
            }
            Move(TransactionState.Completed, at);
        }
        else
        {
            Move(TransactionState.Failed, at, result.Error);
        }
        return result;
    }

    public void Discard(DateTimeOffset at)
    {
        Move(TransactionState.Discarding, at);
        try { Overlay.Discard(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Move(TransactionState.Failed, at, "RecoveryRequired: overlay cleanup failed: " + ex.Message);
            throw;
        }
        Move(TransactionState.Completed, at);
    }

    private void Move(TransactionState target, DateTimeOffset at, string? error = null)
        => Transaction = _store is null
            ? TransactionStateMachine.Transition(Transaction, target, at) with { LastError = error ?? Transaction.LastError }
            : _store.RecordTransition(Transaction.Id, Transaction.OwnerSid, Transaction.Version,
                target, Guid.NewGuid(), at, error).Transaction;

    private static bool PathsOverlap(string left, string right)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

}
