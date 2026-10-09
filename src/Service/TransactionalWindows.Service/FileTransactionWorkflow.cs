using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.State;

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

    public FileTransactionWorkflow(Transaction transaction, FileOverlaySession overlay)
    {
        if (transaction.Id != overlay.TransactionId)
            throw new ArgumentException("Transaction and overlay identifiers do not match.", nameof(overlay));
        Transaction = transaction;
        Overlay = overlay;
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
        CurrentDiff = diff;
        Transaction = Transaction with { DiffId = diff.Id };
        Move(TransactionState.DiffReady, at);
        return diff;
    }

    public FileCommitResult Commit(IReadOnlySet<DiffItemId> selected, DateTimeOffset at)
    {
        if (CurrentDiff is null) throw new InvalidOperationException("A Diff must be built before Commit.");
        Move(TransactionState.Committing, at);
        var result = new FileCommitEngine().Commit(Overlay, CurrentDiff, selected);
        if (result.Succeeded)
        {
            Overlay.Discard();
            Move(TransactionState.Completed, at);
        }
        else
        {
            Transaction = Transaction with { LastError = result.Error };
            Move(TransactionState.Failed, at);
        }
        return result;
    }

    public void Discard(DateTimeOffset at)
    {
        Move(TransactionState.Discarding, at);
        Overlay.Discard();
        Move(TransactionState.Completed, at);
    }

    private void Move(TransactionState target, DateTimeOffset at)
        => Transaction = TransactionStateMachine.Transition(Transaction, target, at);

}
