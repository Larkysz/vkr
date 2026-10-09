using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.State;
using TransactionalWindows.Service.Persistence;
using TransactionalWindows.Service.Processes;
using System.Runtime.Versioning;

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
    private WindowsProcessManager? _processManager;

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

    /// <summary>Controlled current-user launch. Ordinary filesystem calls are NOT redirected.</summary>
    [SupportedOSPlatform("windows")]
    public ProcessNode StartProcess(WindowsProcessManager processManager, ProcessLaunchMode mode, DateTimeOffset at)
    {
        if (mode != ProcessLaunchMode.ControlledUnisolated)
            throw new ProcessManagementException(ProcessErrorCode.UnsupportedIsolation, "Transparent isolation is not implemented.");
        Move(TransactionState.Starting, at);
        _processManager = processManager;
        try
        {
            var root = processManager.StartSuspended(Transaction, mode, _store is null ? null : _store.RecordProcess);
            if (_store is null) Transaction = Transaction with { RootProcessNodeId = root.Id };
            Move(TransactionState.Running, at);
            processManager.Resume(Transaction.Id);
            SyncProcessVersion();
            return root;
        }
        catch (Exception ex)
        {
            try { processManager.TerminateAsync(Transaction.Id, TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception cleanup) when (cleanup is KeyNotFoundException or ProcessManagementException) { }
            Move(TransactionState.Failed, DateTimeOffset.UtcNow, "LaunchFailed: " + ex.Message);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public async Task<Diff> WaitForProcessesAndBuildDiffAsync(long generation, TimeSpan timeout, bool terminate,
        CancellationToken cancellationToken)
    {
        var manager = _processManager ?? throw new InvalidOperationException("No controlled process launch in this workflow.");
        Move(TransactionState.Quiescing, DateTimeOffset.UtcNow);
        try
        {
            if (terminate) await manager.TerminateAsync(Transaction.Id, timeout, cancellationToken).ConfigureAwait(false);
            else await manager.QuiesceAsync(Transaction.Id, timeout, cancellationToken).ConfigureAwait(false);
            return BuildDiff(generation, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            try { await manager.TerminateAsync(Transaction.Id, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is ProcessManagementException) { }
            Move(TransactionState.Failed, DateTimeOffset.UtcNow, "ProcessQuiesceFailed: " + ex.Message);
            throw;
        }
    }

    public Diff StopAndBuildDiff(long generation, DateTimeOffset at)
    {
        EnsureProcessesStopped();
        Move(TransactionState.Quiescing, at);
        return BuildDiff(generation, at);
    }

    private Diff BuildDiff(long generation, DateTimeOffset at)
    {
        EnsureProcessesStopped();
        SyncProcessVersion();
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
        EnsureProcessesStopped();
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
        EnsureProcessesStopped();
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
    {
        SyncProcessVersion();
        Transaction = _store is null
            ? TransactionStateMachine.Transition(Transaction, target, at) with { LastError = error ?? Transaction.LastError }
            : _store.RecordTransition(Transaction.Id, Transaction.OwnerSid, Transaction.Version,
                target, Guid.NewGuid(), at, error).Transaction;
    }

    private void SyncProcessVersion()
    {
        if (_store is null) return;
        var current = _store.Get(Transaction.Id)!;
        if (current.CurrentState != Transaction.CurrentState)
            throw new InvalidOperationException("Another owner changed the workflow state.");
        Transaction = current;
    }

    private void EnsureProcessesStopped()
    {
        if (_processManager is null) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!_processManager.IsEmptyAndObserved(Transaction.Id))
            throw new InvalidOperationException("The controlled process group is still active; Diff/Commit/Discard are blocked.");
    }

    private static bool PathsOverlap(string left, string right)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

}
