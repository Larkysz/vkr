using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.Errors;

namespace TransactionalWindows.Core.State;

public static class TransactionStateMachine
{
    private static readonly IReadOnlyDictionary<TransactionState, IReadOnlySet<TransactionState>> Allowed =
        new Dictionary<TransactionState, IReadOnlySet<TransactionState>>
        {
            [TransactionState.Created] = new HashSet<TransactionState> { TransactionState.Starting, TransactionState.Failed, TransactionState.Aborted },
            [TransactionState.Starting] = new HashSet<TransactionState> { TransactionState.Running, TransactionState.Failed, TransactionState.Aborted },
            [TransactionState.Running] = new HashSet<TransactionState> { TransactionState.Quiescing, TransactionState.Failed, TransactionState.Aborted },
            [TransactionState.Quiescing] = new HashSet<TransactionState> { TransactionState.DiffReady, TransactionState.Failed, TransactionState.Aborted },
            [TransactionState.DiffReady] = new HashSet<TransactionState> { TransactionState.Committing, TransactionState.Discarding, TransactionState.Failed, TransactionState.Aborted },
            [TransactionState.Committing] = new HashSet<TransactionState> { TransactionState.Completed, TransactionState.Failed },
            [TransactionState.Discarding] = new HashSet<TransactionState> { TransactionState.Completed, TransactionState.Failed },
            [TransactionState.Completed] = new HashSet<TransactionState>(),
            [TransactionState.Failed] = new HashSet<TransactionState>(),
            [TransactionState.Aborted] = new HashSet<TransactionState>()
        };

    public static bool CanTransition(TransactionState from, TransactionState to) => Allowed[from].Contains(to);

    public static Transaction Transition(Transaction transaction, TransactionState target, DateTimeOffset at)
    {
        if (!CanTransition(transaction.CurrentState, target))
            throw new DomainException(new(DomainErrorCode.InvalidStateTransition, $"Transition {transaction.CurrentState} -> {target} is not allowed."));
        return transaction with
        {
            CurrentState = target,
            StartedAt = target == TransactionState.Running ? at : transaction.StartedAt,
            QuiescingAt = target == TransactionState.Quiescing ? at : transaction.QuiescingAt,
            DiffReadyAt = target == TransactionState.DiffReady ? at : transaction.DiffReadyAt,
            CompletedAt = target == TransactionState.Completed ? at : transaction.CompletedAt,
            Version = transaction.Version + 1
        };
    }
}

