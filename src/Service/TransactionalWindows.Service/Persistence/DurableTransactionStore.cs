using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TransactionalWindows.Core.Contracts;
using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.Errors;
using TransactionalWindows.Core.State;

namespace TransactionalWindows.Service.Persistence;

/// <summary>
/// Single-writer metadata repository. Each flushed journal record contains the
/// transaction, command result and event together. It does not recover file effects.
/// </summary>
public sealed class DurableTransactionStore : ITransactionRepository, IDisposable
{
    private const int MaxRecordBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new UtcTimestampConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly object _gate = new();
    private readonly Dictionary<TransactionId, Transaction> _transactions = new();
    private readonly Dictionary<TransactionId, Diff> _diffs = new();
    private readonly Dictionary<ProcessNodeId, ProcessNode> _processes = new();
    private readonly Dictionary<Guid, StoredOperation> _operations = new();
    private readonly List<StoredEvent> _events = new();
    private readonly HashSet<TransactionId> _archived = new();
    private readonly FileStream _writerLock;
    private readonly FileStream _journal;
    private bool _disposed;
    private bool _faulted;
    private long _sequence;

    public DurableTransactionStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        _writerLock = new FileStream(Path.Combine(Root, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            _journal = new FileStream(Path.Combine(Root, "transactions.jsonl"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Read);
            try { Replay(); }
            catch { _journal.Dispose(); throw; }
        }
        catch { _writerLock.Dispose(); throw; }
    }

    public string Root { get; }
    public bool TornTailRemoved { get; private set; }

    public Transaction Create(Transaction transaction)
        => Create(transaction, Guid.NewGuid()).Transaction;

    public DurableCommandResult Create(Transaction transaction, Guid operationId)
    {
        lock (_gate)
        {
            EnsureUsable();
            var request = RequestDigest("CreateSnapshot", transaction);
            if (TryReplay(operationId, request, out var replay)) return replay!;
            ValidateTransaction(transaction);
            if (transaction.CurrentState != TransactionState.Created || transaction.Version != 0 || transaction.DiffId is not null)
                throw Error(DomainErrorCode.InvalidArgument, "Create requires a new Created transaction.");
            if (_transactions.ContainsKey(transaction.Id))
                throw Error(DomainErrorCode.VersionConflict, "Transaction already exists.");
            return Append(transaction, null, false, operationId, request, "TransactionCreated", null, transaction.CreatedAt);
        }
    }

    public DurableCommandResult Create(string ownerSid, string applicationPath, string arguments,
        string workingDirectory, string overlayRoot, Guid operationId, DateTimeOffset at)
    {
        lock (_gate)
        {
            EnsureUsable();
            var request = RequestDigest("Create", new { ownerSid, applicationPath, arguments, workingDirectory, overlayRoot });
            if (TryReplay(operationId, request, out var replay)) return replay!;
            var transaction = Transaction.Create(TransactionId.New(), ownerSid, applicationPath, arguments,
                workingDirectory, overlayRoot, at.ToUniversalTime());
            return Append(transaction, null, false, operationId, request, "TransactionCreated", null, at);
        }
    }

    public Transaction? Get(TransactionId id)
    {
        lock (_gate)
        {
            EnsureUsable();
            return _transactions.TryGetValue(id, out var transaction) ? transaction : null;
        }
    }

    public IReadOnlyList<Transaction> List(bool includeArchived = false)
    {
        lock (_gate)
        {
            EnsureUsable();
            return _transactions.Values.Where(t => includeArchived || !_archived.Contains(t.Id))
                .OrderBy(t => t.Id.Value).ToArray();
        }
    }

    public Diff? GetDiff(TransactionId id)
    {
        lock (_gate)
        {
            EnsureUsable();
            return _diffs.TryGetValue(id, out var diff) ? Clone(diff) : null;
        }
    }

    public IReadOnlyList<StoredEvent> ReplayEvents(long afterSequence = 0)
    {
        lock (_gate)
        {
            EnsureUsable();
            if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
            return _events.Where(e => e.Sequence > afterSequence).ToArray();
        }
    }

    public IReadOnlyList<ProcessNode> GetProcessTree(TransactionId id)
    {
        lock (_gate)
        {
            Require(id);
            return _processes.Values.Where(n => n.TransactionId == id).OrderBy(n => n.StartedAt).ThenBy(n => n.Id.Value).ToArray();
        }
    }

    /// <summary>Trusted observer fact. Process state survives restart for audit, never for blind PID attachment.</summary>
    public void RecordProcess(ProcessNode node)
    {
        lock (_gate)
        {
            var transaction = Require(node.TransactionId);
            if (_archived.Contains(transaction.Id)) throw Error(DomainErrorCode.VersionConflict, "Transaction is archived.");
            if (_processes.TryGetValue(node.Id, out var existing) && existing == node) return;
            var next = transaction with
            {
                Version = transaction.Version + 1,
                RootProcessNodeId = node.IsRoot ? node.Id : transaction.RootProcessNodeId
            };
            Append(next, null, false, Guid.NewGuid(), RequestDigest("Process", node),
                node.Status is ProcessNodeStatus.Exited or ProcessNodeStatus.Terminated ? "ProcessExited" : "ProcessAttached",
                transaction.CurrentState, DateTimeOffset.UtcNow, node);
        }
    }

    public Transaction UpdateState(TransactionId id, TransactionState expected, TransactionState target, DateTimeOffset at)
    {
        lock (_gate)
        {
            var transaction = Require(id);
            if (transaction.CurrentState != expected)
                throw Error(DomainErrorCode.VersionConflict, "Expected transaction state does not match.");
            return RecordTransition(id, transaction.OwnerSid, transaction.Version, target, Guid.NewGuid(), at).Transaction;
        }
    }

    /// <summary>Records an internal orchestration fact; does not launch, stop, commit or discard anything.</summary>
    public DurableCommandResult RecordTransition(TransactionId id, string ownerSid, long expectedVersion,
        TransactionState target, Guid operationId, DateTimeOffset at, string? error = null)
    {
        lock (_gate)
        {
            EnsureUsable();
            VerifyOwner(id, ownerSid);
            var request = RequestDigest("Transition", new { id, ownerSid, expectedVersion, target, error });
            if (TryReplay(operationId, request, out var replay)) return replay!;
            var current = RequireVersion(id, expectedVersion);
            if (target == TransactionState.DiffReady)
                throw Error(DomainErrorCode.InvalidArgument, "DiffReady must be recorded with the Diff snapshot.");
            var next = TransactionStateMachine.Transition(current, target, at.ToUniversalTime()) with { LastError = error ?? current.LastError };
            return Append(next, null, false, operationId, request, "TransactionStateChanged", current.CurrentState, at);
        }
    }

    public DurableCommandResult RecordDiff(TransactionId id, string ownerSid, long expectedVersion,
        Diff diff, Guid operationId, DateTimeOffset at)
    {
        lock (_gate)
        {
            EnsureUsable();
            VerifyOwner(id, ownerSid);
            diff = Clone(diff);
            var request = RequestDigest("Diff", new { id, ownerSid, expectedVersion, diff });
            if (TryReplay(operationId, request, out var replay)) return replay!;
            var current = RequireVersion(id, expectedVersion);
            ValidateDiff(diff, id);
            var next = TransactionStateMachine.Transition(current, TransactionState.DiffReady, at.ToUniversalTime()) with { DiffId = diff.Id };
            return Append(next, diff, false, operationId, request, "DiffReady", current.CurrentState, at);
        }
    }

    public void Save(Transaction transaction)
    {
        lock (_gate)
        {
            var current = RequireVersion(transaction.Id, transaction.Version - 1);
            // Legacy Save still validates one state transition. DiffReady has its own atomic command.
            var at = TransitionTime(transaction).ToUniversalTime();
            var expected = TransactionStateMachine.Transition(current, transaction.CurrentState,
                at) with { LastError = transaction.LastError };
            if (transaction != expected || transaction.CurrentState == TransactionState.DiffReady)
                throw Error(DomainErrorCode.VersionConflict, "Save requires the next canonical state snapshot.");
            Append(transaction, null, false, Guid.NewGuid(), RequestDigest("Save", transaction),
                "TransactionStateChanged", current.CurrentState, at);
        }
    }

    public void Archive(TransactionId id)
    {
        lock (_gate)
        {
            var current = Require(id);
            if (!IsTerminal(current.CurrentState))
                throw Error(DomainErrorCode.InvalidStateTransition, "Only terminal transactions can be archived.");
            if (_archived.Contains(id)) return;
            Append(current, null, true, Guid.NewGuid(), RequestDigest("Archive", id), "TransactionArchived",
                current.CurrentState, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Preserves evidence; never resumes or reapplies effects after restart.</summary>
    public IReadOnlyList<RestartEntry> ScanAfterRestart(DateTimeOffset at)
    {
        lock (_gate)
        {
            EnsureUsable();
            var result = new List<RestartEntry>();
            foreach (var transaction in List())
            {
                var state = transaction.CurrentState;
                if (state is TransactionState.Starting or TransactionState.Running or TransactionState.Quiescing
                    or TransactionState.Committing or TransactionState.Discarding)
                {
                    const string reason = "RecoveryRequired: interrupted orchestration; process and filesystem effects are unverified.";
                    RecordTransition(transaction.Id, transaction.OwnerSid, transaction.Version, TransactionState.Failed,
                        Guid.NewGuid(), at, reason);
                    result.Add(new(transaction.Id, TransactionState.Failed, RestartDisposition.RecoveryRequired, reason));
                }
                else
                {
                    var disposition = state switch
                    {
                        TransactionState.Created => RestartDisposition.InspectCreated,
                        TransactionState.DiffReady => RestartDisposition.ReviewDiff,
                        TransactionState.Failed => RestartDisposition.RecoveryRequired,
                        _ => RestartDisposition.InspectTerminal
                    };
                    result.Add(new(transaction.Id, state, disposition, transaction.LastError));
                }
            }
            return result;
        }
    }

    private DurableCommandResult Append(Transaction transaction, Diff? diff, bool archived, Guid operationId,
        string requestDigest, string eventName, TransactionState? from, DateTimeOffset at, ProcessNode? process = null)
    {
        var sequence = _sequence + 1;
        var operation = new StoredOperation(operationId, requestDigest, transaction, sequence);
        var domainEvent = new StoredEvent(sequence, Guid.NewGuid(), transaction.Id, operationId,
            eventName, from, transaction.CurrentState, at.ToUniversalTime());
        var mutation = new JournalMutation(transaction, diff, archived, operation, domainEvent, process);
        // Validate before any disk write; replay uses exactly the same consistency checks.
        ValidateMutation(mutation, sequence);
        var payload = JsonSerializer.Serialize(mutation, Json);
        var envelope = new JournalEnvelope(process is null ? 1 : 2, sequence, payload, Hash(payload));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, Json) + "\n");
        if (bytes.Length > MaxRecordBytes) throw Error(DomainErrorCode.InvalidArgument, "Metadata record exceeds 4 MiB.");
        try
        {
            _journal.Write(bytes);
            _journal.Flush(flushToDisk: true);
            Install(mutation);
        }
        catch { _faulted = true; throw; }
        return new(transaction, sequence, false);
    }

    private void Replay()
    {
        _journal.Position = 0;
        using var line = new MemoryStream();
        long lastComplete = 0;
        var buffer = new byte[8192];
        int count;
        while ((count = _journal.Read(buffer)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    ReplayLine(line.ToArray());
                    line.SetLength(0);
                    lastComplete = _journal.Position - count + i + 1;
                }
                else
                {
                    if (line.Length >= MaxRecordBytes) throw Corrupt("Journal record exceeds the supported limit.");
                    line.WriteByte(buffer[i]);
                }
            }
        }
        if (line.Length > 0)
        {
            try
            {
                using var completeJson = JsonDocument.Parse(line.ToArray());
                // A complete record missing its delimiter might be corrupted acknowledged data.
                // Its outcome is uncertain, so do not truncate or silently accept it.
                throw Corrupt("Complete JSON record has no journal delimiter; manual inspection required.");
            }
            catch (JsonException) { }
            // An incomplete suffix has no committed record boundary. Completed malformed records are never removed.
            using (var evidence = new FileStream(Path.Combine(Root, "torn-tail-" + Guid.NewGuid().ToString("N") + ".bin"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                evidence.Write(line.ToArray());
                evidence.Flush(flushToDisk: true);
            }
            _journal.SetLength(lastComplete);
            _journal.Flush(flushToDisk: true);
            TornTailRemoved = true;
        }
        _journal.Position = _journal.Length;
    }

    private void ReplayLine(byte[] bytes)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<JournalEnvelope>(bytes, Json) ?? throw Corrupt("Empty envelope.");
            if (envelope.SchemaVersion is not (1 or 2) || envelope.Sequence != _sequence + 1 ||
                envelope.Sha256 != Hash(envelope.Payload)) throw Corrupt("Journal schema, sequence or checksum is invalid.");
            var mutation = JsonSerializer.Deserialize<JournalMutation>(envelope.Payload, Json) ?? throw Corrupt("Empty mutation.");
            if ((mutation.Process is not null) != (envelope.SchemaVersion == 2)) throw Corrupt("Process journal schema does not match its payload.");
            ValidateMutation(mutation, envelope.Sequence);
            Install(mutation);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException or DomainException)
        {
            throw Corrupt("Malformed or inconsistent journal record.", ex);
        }
    }

    private void ValidateMutation(JournalMutation mutation, long sequence)
    {
        ValidateTransaction(mutation.Transaction);
        var transaction = mutation.Transaction;
        if (mutation.Operation.OperationId == Guid.Empty || mutation.Operation.Sequence != sequence ||
            mutation.Event.Sequence != sequence || mutation.Event.EventId == Guid.Empty ||
            mutation.Event.TransactionId != transaction.Id || mutation.Event.OperationId != mutation.Operation.OperationId ||
            mutation.Event.To != transaction.CurrentState || mutation.Operation.Response != transaction ||
            _operations.ContainsKey(mutation.Operation.OperationId) || mutation.Operation.RequestDigest.Length != 64 ||
            mutation.Operation.RequestDigest.Any(c => !char.IsAsciiHexDigit(c))) throw Corrupt("Inconsistent command/event record.");
        if (_transactions.TryGetValue(transaction.Id, out var prior))
        {
            if (_archived.Contains(transaction.Id) || prior.OwnerSid != transaction.OwnerSid ||
                prior.CreatedAt != transaction.CreatedAt || prior.ApplicationPath != transaction.ApplicationPath ||
                prior.Arguments != transaction.Arguments || prior.WorkingDirectory != transaction.WorkingDirectory ||
                prior.OverlayRoot != transaction.OverlayRoot || mutation.Event.From != prior.CurrentState)
                throw Corrupt("Transaction identity or event history changed.");
            if (mutation.Process is not null)
            {
                ValidateProcess(mutation.Process, prior);
                var expected = prior with { Version = prior.Version + 1,
                    RootProcessNodeId = mutation.Process.IsRoot ? mutation.Process.Id : prior.RootProcessNodeId };
                if (transaction != expected || mutation.Archived || mutation.Diff is not null)
                    throw Corrupt("Invalid process observation mutation.");
            }
            else if (mutation.Archived)
            {
                if (!IsTerminal(prior.CurrentState) || prior != transaction) throw Corrupt("Invalid archive record.");
            }
            else if (transaction.Version != prior.Version + 1 ||
                     !TransactionStateMachine.CanTransition(prior.CurrentState, transaction.CurrentState))
                throw Corrupt("Invalid transaction version or transition.");
            else
            {
                var expected = TransactionStateMachine.Transition(prior, transaction.CurrentState, mutation.Event.OccurredAt)
                    with { DiffId = mutation.Diff?.Id ?? prior.DiffId, LastError = transaction.LastError };
                if (transaction != expected) throw Corrupt("Transaction metadata does not match its state transition.");
            }
        }
        else if (transaction.CurrentState != TransactionState.Created || transaction.Version != 0 || mutation.Archived || mutation.Event.From is not null)
            throw Corrupt("First transaction record must be Created.");
        if (mutation.Diff is not null)
        {
            ValidateDiff(mutation.Diff, transaction.Id);
            if (transaction.CurrentState != TransactionState.DiffReady || transaction.DiffId != mutation.Diff.Id)
                throw Corrupt("Diff does not match its transaction.");
        }
        if (transaction.DiffId is not null && mutation.Diff is null &&
            (!_diffs.TryGetValue(transaction.Id, out var diff) || diff.Id != transaction.DiffId))
            throw Corrupt("Referenced Diff is missing.");
        if (transaction.CurrentState == TransactionState.DiffReady && transaction.DiffId is null)
            throw Corrupt("DiffReady has no Diff.");
        if (!_transactions.ContainsKey(transaction.Id) && mutation.Process is not null)
            throw Corrupt("Process observation has no existing transaction.");
    }

    private void Install(JournalMutation mutation)
    {
        _transactions[mutation.Transaction.Id] = mutation.Transaction;
        if (mutation.Diff is not null) _diffs[mutation.Transaction.Id] = mutation.Diff;
        if (mutation.Process is not null) _processes[mutation.Process.Id] = mutation.Process;
        if (mutation.Archived) _archived.Add(mutation.Transaction.Id);
        _operations.Add(mutation.Operation.OperationId, mutation.Operation);
        _events.Add(mutation.Event);
        _sequence = mutation.Event.Sequence;
    }

    private bool TryReplay(Guid operationId, string digest, out DurableCommandResult? result)
    {
        if (operationId == Guid.Empty) throw Error(DomainErrorCode.InvalidArgument, "OperationId is required.");
        result = null;
        if (!_operations.TryGetValue(operationId, out var operation)) return false;
        if (operation.RequestDigest != digest)
            throw Error(DomainErrorCode.InvalidArgument, "OperationId was already used for a different request.");
        result = new(operation.Response, operation.Sequence, true);
        return true;
    }

    private Transaction Require(TransactionId id)
    {
        EnsureUsable();
        return _transactions.TryGetValue(id, out var transaction) ? transaction :
            throw Error(DomainErrorCode.TransactionNotFound, "Transaction was not found.");
    }

    private Transaction RequireVersion(TransactionId id, long version)
    {
        var transaction = Require(id);
        if (_archived.Contains(id) || transaction.Version != version)
            throw Error(DomainErrorCode.VersionConflict, "Transaction version changed or is archived.");
        return transaction;
    }

    private void VerifyOwner(TransactionId id, string ownerSid)
    {
        if (!string.Equals(Require(id).OwnerSid, ownerSid, StringComparison.Ordinal))
            throw Error(DomainErrorCode.SecurityViolation, "Transaction owner does not match.");
    }

    private static void ValidateTransaction(Transaction transaction)
    {
        if (transaction.Id.Value == Guid.Empty || transaction.SchemaVersion != 1 || transaction.Version < 0 ||
            !Enum.IsDefined(transaction.CurrentState) || string.IsNullOrWhiteSpace(transaction.OwnerSid) ||
            string.IsNullOrWhiteSpace(transaction.ApplicationPath)) throw Corrupt("Invalid transaction schema or identity.");
    }

    private void ValidateProcess(ProcessNode node, Transaction transaction)
    {
        if (node.Id.Value == Guid.Empty || node.TransactionId != transaction.Id || node.Pid <= 0 ||
            node.ProcessCreationIdentity <= 0 || node.OwnerSid != transaction.OwnerSid || node.SessionId is null ||
            !node.JobMembershipConfirmed || !Enum.IsDefined(node.Status) ||
            node.Status is ProcessNodeStatus.Unknown or ProcessNodeStatus.EscapeDetected)
            throw Corrupt("Invalid process identity, ownership or membership.");
        if (transaction.CurrentState is not (TransactionState.Starting or TransactionState.Running or TransactionState.Quiescing))
            throw Corrupt("Process facts require an active transaction.");
        if (node.IsRoot && transaction.RootProcessNodeId is not null && transaction.RootProcessNodeId != node.Id)
            throw Corrupt("Transaction already has a different root.");
        if (node.ParentNodeId is not null && (!_processes.TryGetValue(node.ParentNodeId.Value, out var parent) || parent.TransactionId != node.TransactionId))
            throw Corrupt("Process parent is outside this transaction.");
        if (node.Status is ProcessNodeStatus.Exited or ProcessNodeStatus.Terminated)
        {
            if (node.ExitedAt is null || node.ExitCode is null || node.ExitedAt < node.StartedAt)
                throw Corrupt("Terminal process lacks exit facts.");
        }
        else if (node.ExitedAt is not null || node.ExitCode is not null) throw Corrupt("Active process has exit facts.");
        if (_processes.TryGetValue(node.Id, out var prior))
        {
            if ((node with { Status = prior.Status, ExitedAt = prior.ExitedAt, ExitCode = prior.ExitCode }) != prior
                || prior.Status is ProcessNodeStatus.Exited or ProcessNodeStatus.Terminated
                || (prior.Status == ProcessNodeStatus.Running && node.Status == ProcessNodeStatus.Starting))
                throw Corrupt("Process identity or terminal state changed.");
        }
        else if (node.Status is not (ProcessNodeStatus.Starting or ProcessNodeStatus.Running))
            throw Corrupt("First process observation must be active.");
    }

    private static void ValidateDiff(Diff diff, TransactionId id)
    {
        if (diff.TransactionId != id || diff.Id.Value == Guid.Empty || diff.Generation <= 0 ||
            diff.Items.Any(i => i.Id.Value == Guid.Empty) || diff.Items.Select(i => i.Id).Distinct().Count() != diff.Items.Count)
            throw Error(DomainErrorCode.InvalidArgument, "Invalid Diff identity, generation or item IDs.");
        diff.ValidateDependencies();
        var pending = diff.Items.Select(i => i.Id).ToHashSet();
        while (pending.Count > 0)
        {
            var ready = pending.Where(i => diff.Dependencies.All(d => d.Dependent != i || !pending.Contains(d.Prerequisite))).ToArray();
            if (ready.Length == 0) throw Error(DomainErrorCode.DependencyViolation, "Diff dependencies contain a cycle.");
            pending.ExceptWith(ready);
        }
    }

    private static DateTimeOffset TransitionTime(Transaction transaction) => transaction.CurrentState switch
    {
        TransactionState.Running => transaction.StartedAt ?? throw Error(DomainErrorCode.InvalidArgument, "StartedAt required."),
        TransactionState.Quiescing => transaction.QuiescingAt ?? throw Error(DomainErrorCode.InvalidArgument, "QuiescingAt required."),
        TransactionState.Completed => transaction.CompletedAt ?? throw Error(DomainErrorCode.InvalidArgument, "CompletedAt required."),
        _ => DateTimeOffset.UtcNow
    };

    private static bool IsTerminal(TransactionState state)
        => state is TransactionState.Completed or TransactionState.Failed or TransactionState.Aborted;
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static string RequestDigest<T>(string kind, T request) => Hash(kind + ":" + JsonSerializer.Serialize(request, Json));
    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    private static DomainException Error(DomainErrorCode code, string message) => new(new(code, message));
    private static InvalidDataException Corrupt(string message, Exception? inner = null) => new(message, inner);

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new IOException("Store has an uncertain write result; close and reopen it before retrying.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _journal.Dispose(); }
            finally { _writerLock.Dispose(); }
        }
    }
}
