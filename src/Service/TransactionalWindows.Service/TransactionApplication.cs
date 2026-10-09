using TransactionalWindows.Core.Contracts;
using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.State;

namespace TransactionalWindows.Service;

public sealed class TransactionApplication : ITransactionManager
{
    public Transaction Create(string ownerSid, string applicationPath, string arguments, string workingDirectory, string overlayRoot, DateTimeOffset now)
        => Transaction.Create(TransactionId.New(), ownerSid, applicationPath, arguments, workingDirectory, overlayRoot, now);

    public Transaction Transition(Transaction transaction, TransactionState target, DateTimeOffset now)
        => TransactionStateMachine.Transition(transaction, target, now);

    public Task<Transaction> StartTransactionAsync(Transaction transaction, CancellationToken cancellationToken)
        => Task.FromResult(Transition(transaction, TransactionState.Starting, DateTimeOffset.UtcNow));

    public Task<Transaction> QuiesceAsync(Transaction transaction, CancellationToken cancellationToken)
        => Task.FromResult(Transition(transaction, TransactionState.Quiescing, DateTimeOffset.UtcNow));

    public Task<Transaction> MarkDiffReadyAsync(Transaction transaction, CancellationToken cancellationToken)
        => Task.FromResult(Transition(transaction, TransactionState.DiffReady, DateTimeOffset.UtcNow));

    public Task<Transaction> CommitAsync(Transaction transaction, CancellationToken cancellationToken)
        => Task.FromResult(Transition(transaction, TransactionState.Committing, DateTimeOffset.UtcNow));

    public Task<Transaction> SelectiveCommitAsync(Transaction transaction, CancellationToken cancellationToken)
        => CommitAsync(transaction, cancellationToken);

    public Task<Transaction> DiscardAsync(Transaction transaction, CancellationToken cancellationToken)
        => Task.FromResult(Transition(transaction, TransactionState.Discarding, DateTimeOffset.UtcNow));

    public Task<Diff?> GetDiffAsync(Transaction transaction, CancellationToken cancellationToken)
        => Task.FromResult<Diff?>(null);
}

