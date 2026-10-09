using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using TransactionalWindows.Core.Contracts;
using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.Errors;
using TransactionalWindows.Service;
using TransactionalWindows.Service.Persistence;

internal static class PersistenceTests
{
    private const string Owner = "S-1-5-21-test";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-09T10:00:00Z");

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "tw-persistence-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RestartAndReplay(Path.Combine(root, "replay"));
            ConcurrentCommands(Path.Combine(root, "concurrent"));
            DiffPersistence(Path.Combine(root, "diff"));
            RepositoryContract(Path.Combine(root, "contract"));
            RestartStates(Path.Combine(root, "states"));
            TornTail(Path.Combine(root, "tail"));
            Corruption(Path.Combine(root, "corrupt"));
            DurableWorkflow(Path.Combine(root, "workflow"));
            RejectedCommands(Path.Combine(root, "rejected"));
            InterruptedWorkflow(Path.Combine(root, "interrupted-workflow"));
            AbruptProcessExit(Path.Combine(root, "killed"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Service persistence tests passed.");
    }

    private static void RejectedCommands(string root)
    {
        using var store = new DurableTransactionStore(root);
        var app = new DurableTransactionApplication(store);
        Assert.Throws<DomainException>(() => Create(app, Guid.Empty), "TEST-PERSIST-052 empty operation ID rejected");
        var id = Guid.NewGuid();
        var at = At.AddTicks(1234567).ToOffset(TimeSpan.FromHours(7));
        var transaction = app.Create(id, Owner, "editor.exe", "", "baseline", "overlay", at).Transaction;
        Assert.Equal(at, transaction.CreatedAt, "TEST-PERSIST-053 timestamps preserve original instant");
        transaction = Move(store, transaction, TransactionState.Starting);
        transaction = Move(store, transaction, TransactionState.Running);
        transaction = Move(store, transaction, TransactionState.Quiescing);
        var a = new DiffItem { Id = DiffItemId.New(), Type = DiffItemType.File, Path = "A.txt", Status = DiffItemStatus.Applicable };
        var b = a with { Id = DiffItemId.New(), Path = "B.txt" };
        var diff = new Diff { Id = DiffId.New(), TransactionId = transaction.Id, Generation = 1, Items = new[] { a, b },
            Dependencies = new[] { new Dependency(a.Id, b.Id, "a requires b"), new Dependency(b.Id, a.Id, "b requires a") } };
        var before = store.ReplayEvents().Count;
        Assert.Throws<DomainException>(() => store.RecordDiff(transaction.Id, Owner, transaction.Version, diff, Guid.NewGuid(), at),
            "TEST-PERSIST-054 cyclic dependencies rejected");
        Assert.Throws<DomainException>(() => store.RecordDiff(transaction.Id, Owner, transaction.Version,
            diff with { Items = new[] { a, a }, Dependencies = Array.Empty<Dependency>() }, Guid.NewGuid(), at),
            "TEST-PERSIST-055 duplicate item IDs rejected");
        Assert.Throws<DomainException>(() => store.RecordDiff(transaction.Id, Owner, transaction.Version,
            diff with { Dependencies = new[] { new Dependency(a.Id, DiffItemId.New(), "missing") } }, Guid.NewGuid(), at),
            "TEST-PERSIST-056 missing dependency rejected");
        Assert.Throws<DomainException>(() => store.Archive(transaction.Id), "TEST-PERSIST-057 active transaction cannot be archived");
        Assert.Throws<DomainException>(() => app.Create(Guid.NewGuid(), Owner, "editor.exe", new string('x', 5 * 1024 * 1024),
            "baseline", "overlay", at), "TEST-PERSIST-058 oversized command rejected before write");
        Assert.Equal(before, store.ReplayEvents().Count, "TEST-PERSIST-059 rejected commands produce no durable mutation");
        Assert.Equal(TransactionState.Quiescing, store.Get(transaction.Id)!.CurrentState, "TEST-PERSIST-060 rejected Diff preserves state");
    }

    private static void InterruptedWorkflow(string root)
    {
        var baseline = Path.Combine(root, "baseline");
        var overlay = Path.Combine(root, "overlay");
        var metadata = Path.Combine(root, "metadata");
        Directory.CreateDirectory(baseline);
        File.WriteAllText(Path.Combine(baseline, "A.txt"), "before");
        TransactionId id;
        using (var store = new DurableTransactionStore(metadata))
        {
            var transaction = store.Create(Owner, "editor.exe", "", baseline, overlay, Guid.NewGuid(), At).Transaction;
            id = transaction.Id;
            var workflow = new FileTransactionWorkflow(transaction, new FileOverlaySession(id, baseline, overlay), store);
            workflow.Start(At);
            workflow.Overlay.WriteAllBytes("A.txt", Encoding.UTF8.GetBytes("after"));
            // The owner leaves without completing the workflow. Restart must preserve the overlay for inspection.
        }
        using var reopened = new DurableTransactionStore(metadata);
        var app = new DurableTransactionApplication(reopened);
        Assert.Equal(RestartDisposition.RecoveryRequired, app.RecoverAfterRestart(At).Single().Disposition,
            "TEST-PERSIST-061 interrupted workflow needs inspection");
        Assert.Equal("before", File.ReadAllText(Path.Combine(baseline, "A.txt")), "TEST-PERSIST-062 recovery does not apply files");
        Assert.Equal("after", File.ReadAllText(Path.Combine(overlay, "content", "A.txt")), "TEST-PERSIST-063 recovery retains overlay evidence");
        Assert.Throws<ArgumentException>(() => new FileTransactionWorkflow(reopened.Get(id)!,
            new FileOverlaySession(id, baseline, overlay), reopened), "TEST-PERSIST-064 failed workflow cannot be resumed as fresh");
    }

    private static DurableCommandResult Create(DurableTransactionApplication app, Guid operationId)
        => app.Create(operationId, Owner, "C:\\Apps\\Editor.exe", "", "C:\\Apps", "C:\\Overlay", At);

    private static Transaction Move(DurableTransactionStore store, Transaction transaction, TransactionState state)
        => store.RecordTransition(transaction.Id, Owner, transaction.Version, state, Guid.NewGuid(), At).Transaction;

    private static void RestartAndReplay(string root)
    {
        var createId = Guid.NewGuid();
        var startId = Guid.NewGuid();
        Transaction created;
        Transaction running;
        using (var store = new DurableTransactionStore(root))
        {
            var app = new DurableTransactionApplication(store);
            created = Create(app, createId).Transaction;
            var duplicate = Create(app, createId);
            Assert.True(duplicate.Replayed, "TEST-PERSIST-001 duplicate create is replayed");
            Assert.Equal(created, duplicate.Transaction, "TEST-PERSIST-002 duplicate keeps original ID");
            var starting = app.RecordState(created.Id, Owner, 0, TransactionState.Starting, startId, At);
            running = Move(store, starting.Transaction, TransactionState.Running);
            Assert.Equal(3, store.ReplayEvents().Count, "TEST-PERSIST-003 exactly one event per accepted command");
            Assert.Throws<IOException>(() => { using var other = new DurableTransactionStore(root); }, "TEST-PERSIST-004 second writer rejected");
        }

        using var reopened = new DurableTransactionStore(root);
        var recovered = new DurableTransactionApplication(reopened);
        Assert.Equal(running, recovered.Get(created.Id, Owner), "TEST-PERSIST-005 state round trip");
        var replay = recovered.RecordState(created.Id, Owner, 0, TransactionState.Starting, startId, At.AddDays(1));
        Assert.True(replay.Replayed, "TEST-PERSIST-006 duplicate after restart is replayed");
        Assert.Equal(TransactionState.Starting, replay.Transaction.CurrentState, "TEST-PERSIST-007 original response returned");
        Assert.Equal(TransactionState.Running, recovered.Get(created.Id, Owner).CurrentState, "TEST-PERSIST-008 replay does not regress current state");
        Assert.Equal(1, recovered.ReadEvents(Owner, 2).Count, "TEST-PERSIST-009 replay cursor");
        Assert.Equal(0, recovered.ReadEvents("other-owner", 0).Count, "TEST-PERSIST-010 event owner filter");
        Assert.Throws<DomainException>(() => recovered.Get(created.Id, "other-owner"), "TEST-PERSIST-011 owner check");
        Assert.Throws<DomainException>(() => recovered.RecordState(created.Id, Owner, 0, TransactionState.Failed, startId, At), "TEST-PERSIST-012 changed request rejects reused operation ID");
        Assert.Throws<DomainException>(() => recovered.RecordState(created.Id, Owner, 0, TransactionState.Quiescing, Guid.NewGuid(), At), "TEST-PERSIST-013 stale version rejected");
        Assert.Throws<DomainException>(() => recovered.RecordState(created.Id, Owner, running.Version, TransactionState.Completed, Guid.NewGuid(), At), "TEST-PERSIST-014 invalid transition rejected");
        var scan = recovered.RecoverAfterRestart(At.AddMinutes(1));
        Assert.Equal(RestartDisposition.RecoveryRequired, scan.Single().Disposition, "TEST-PERSIST-015 interrupted running needs recovery");
        Assert.Equal(TransactionState.Failed, recovered.Get(created.Id, Owner).CurrentState, "TEST-PERSIST-016 interrupted running becomes Failed");
        var before = reopened.ReplayEvents().Count;
        recovered.RecoverAfterRestart(At.AddMinutes(2));
        Assert.Equal(before, reopened.ReplayEvents().Count, "TEST-PERSIST-017 repeated scan adds no duplicate failure");
        Assert.Throws<DomainException>(() => recovered.RecordState(created.Id, Owner, running.Version + 1, TransactionState.Committing, Guid.NewGuid(), At), "TEST-PERSIST-018 recovery cannot blindly commit");
    }

    private static void ConcurrentCommands(string root)
    {
        using var store = new DurableTransactionStore(root);
        var app = new DurableTransactionApplication(store);
        var operationId = Guid.NewGuid();
        var results = Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Create(app, operationId)))).GetAwaiter().GetResult();
        Assert.Equal(1, results.Count(r => !r.Replayed), "TEST-PERSIST-019 concurrent duplicate runs once");
        Assert.Equal(1, store.List().Count, "TEST-PERSIST-020 one transaction after duplicates");
        var created = results[0].Transaction;
        var attempts = Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { app.RecordState(created.Id, Owner, 0, TransactionState.Starting, Guid.NewGuid(), At); return true; }
            catch (DomainException ex) when (ex.Error.Code == DomainErrorCode.VersionConflict) { return false; }
        }))).GetAwaiter().GetResult();
        Assert.Equal(1, attempts.Count(s => s), "TEST-PERSIST-021 concurrent versions have one winner");
    }

    private static void DiffPersistence(string root)
    {
        Transaction transaction;
        Diff diff;
        using (var store = new DurableTransactionStore(root))
        {
            transaction = Create(new DurableTransactionApplication(store), Guid.NewGuid()).Transaction;
            transaction = Move(store, transaction, TransactionState.Starting);
            transaction = Move(store, transaction, TransactionState.Running);
            transaction = Move(store, transaction, TransactionState.Quiescing);
            Assert.Throws<DomainException>(() => Move(store, transaction, TransactionState.DiffReady), "TEST-PERSIST-022 DiffReady requires snapshot");
            var items = new[] { new DiffItem { Id = DiffItemId.New(), FileChangeId = FileChangeId.New(), Path = "A.txt",
                Type = DiffItemType.File, FileOperation = FileChangeOperation.Modify, Status = DiffItemStatus.Applicable } };
            diff = new Diff { Id = DiffId.New(), TransactionId = transaction.Id, Generation = 1,
                Items = items, Dependencies = Array.Empty<Dependency>() };
            Assert.Throws<DomainException>(() => store.RecordDiff(transaction.Id, Owner, transaction.Version,
                diff with { TransactionId = TransactionId.New() }, Guid.NewGuid(), At), "TEST-PERSIST-023 foreign Diff rejected");
            transaction = store.RecordDiff(transaction.Id, Owner, transaction.Version, diff, Guid.NewGuid(), At).Transaction;
            items[0] = items[0] with { Path = "mutated.txt" };
            Assert.Equal("A.txt", store.GetDiff(transaction.Id)!.Items[0].Path, "TEST-PERSIST-024 stored Diff is detached from caller");
        }
        using var reopened = new DurableTransactionStore(root);
        Assert.Equal(transaction, reopened.Get(transaction.Id), "TEST-PERSIST-025 DiffReady snapshot round trip");
        Assert.Equal(diff.Id, reopened.GetDiff(transaction.Id)!.Id, "TEST-PERSIST-026 Diff ID stable after restart");
        Assert.Equal("A.txt", reopened.GetDiff(transaction.Id)!.Items[0].Path, "TEST-PERSIST-027 Diff contents round trip");
        Assert.Equal(RestartDisposition.ReviewDiff, reopened.ScanAfterRestart(At).Single().Disposition, "TEST-PERSIST-028 ready Diff requires review");
    }

    private static void RepositoryContract(string root)
    {
        Transaction transaction;
        using (var store = new DurableTransactionStore(root))
        {
            ITransactionRepository repository = store;
            transaction = Transaction.Create(TransactionId.New(), Owner, "editor.exe", "", "baseline", "overlay", At);
            repository.Create(transaction);
            transaction = repository.UpdateState(transaction.Id, TransactionState.Created, TransactionState.Starting, At);
            transaction = TransactionalWindows.Core.State.TransactionStateMachine.Transition(transaction, TransactionState.Failed, At);
            repository.Save(transaction);
            Assert.Throws<DomainException>(() => repository.Save(transaction), "TEST-PERSIST-029 stale Save rejected");
            repository.Archive(transaction.Id);
            Assert.Equal(0, store.List().Count, "TEST-PERSIST-030 archive hides from active list");
        }
        using var reopened = new DurableTransactionStore(root);
        Assert.Equal(transaction, reopened.Get(transaction.Id), "TEST-PERSIST-031 archived audit retained");
        Assert.Equal(0, reopened.List().Count, "TEST-PERSIST-032 archive round trip");
        Assert.Equal(1, reopened.List(includeArchived: true).Count, "TEST-PERSIST-033 archived transaction available for audit");
    }

    private static void RestartStates(string root)
    {
        var states = new[] { TransactionState.Created, TransactionState.Starting, TransactionState.Running,
            TransactionState.Quiescing, TransactionState.DiffReady, TransactionState.Committing, TransactionState.Discarding,
            TransactionState.Completed, TransactionState.Failed, TransactionState.Aborted };
        foreach (var state in states)
        {
            var directory = Path.Combine(root, state.ToString());
            Transaction transaction;
            using (var store = new DurableTransactionStore(directory))
            {
                transaction = Create(new DurableTransactionApplication(store), Guid.NewGuid()).Transaction;
                if (state == TransactionState.Aborted) transaction = Move(store, transaction, state);
                else if (state == TransactionState.Failed) transaction = Move(store, transaction, state);
                else if (state != TransactionState.Created)
                {
                    transaction = Move(store, transaction, TransactionState.Starting);
                    if (state != TransactionState.Starting)
                    {
                        transaction = Move(store, transaction, TransactionState.Running);
                        if (state != TransactionState.Running)
                        {
                            transaction = Move(store, transaction, TransactionState.Quiescing);
                            if (state != TransactionState.Quiescing)
                            {
                                var diff = new Diff { Id = DiffId.New(), TransactionId = transaction.Id, Generation = 1,
                                    Items = Array.Empty<DiffItem>(), Dependencies = Array.Empty<Dependency>() };
                                transaction = store.RecordDiff(transaction.Id, Owner, transaction.Version, diff, Guid.NewGuid(), At).Transaction;
                                if (state != TransactionState.DiffReady)
                                {
                                    transaction = Move(store, transaction, state == TransactionState.Discarding ? state : TransactionState.Committing);
                                    if (state == TransactionState.Completed) transaction = Move(store, transaction, state);
                                }
                            }
                        }
                    }
                }
            }
            using var reopened = new DurableTransactionStore(directory);
            var result = reopened.ScanAfterRestart(At).Single();
            var interrupted = state is TransactionState.Starting or TransactionState.Running or TransactionState.Quiescing
                or TransactionState.Committing or TransactionState.Discarding;
            Assert.Equal(interrupted ? TransactionState.Failed : state, reopened.Get(transaction.Id)!.CurrentState,
                "TEST-PERSIST-034 restart state " + state);
            if (interrupted) Assert.Equal(RestartDisposition.RecoveryRequired, result.Disposition, "interrupted state " + state);
        }
    }

    private static void TornTail(string root)
    {
        Transaction transaction;
        using (var store = new DurableTransactionStore(root))
            transaction = Create(new DurableTransactionApplication(store), Guid.NewGuid()).Transaction;
        var journal = Path.Combine(root, "transactions.jsonl");
        var priorLength = new FileInfo(journal).Length;
        File.AppendAllText(journal, "{partial-record", new UTF8Encoding(false));
        using var reopened = new DurableTransactionStore(root);
        Assert.True(reopened.TornTailRemoved, "TEST-PERSIST-035 incomplete tail recognized");
        Assert.Equal(priorLength, new FileInfo(journal).Length, "TEST-PERSIST-036 only incomplete suffix removed");
        Assert.Equal(transaction, reopened.Get(transaction.Id), "TEST-PERSIST-037 acknowledged state retained");
        Assert.Equal(1, Directory.GetFiles(root, "torn-tail-*.bin").Length, "TEST-PERSIST-038 tail retained as evidence");
        Move(reopened, transaction, TransactionState.Starting);
    }

    private static void Corruption(string root)
    {
        foreach (var kind in new[] { "checksum", "sequence", "schema", "json", "missing-newline" })
        {
            var directory = Path.Combine(root, kind);
            using (var store = new DurableTransactionStore(directory))
                Create(new DurableTransactionApplication(store), Guid.NewGuid());
            var journal = Path.Combine(directory, "transactions.jsonl");
            var envelope = JsonNode.Parse(File.ReadAllText(journal))!;
            if (kind == "checksum") envelope["Sha256"] = new string('0', 64);
            if (kind == "sequence") envelope["Sequence"] = 9;
            if (kind == "schema") envelope["SchemaVersion"] = 999;
            var content = kind == "json" ? "broken\n" : envelope.ToJsonString() + (kind == "missing-newline" ? "" : "\n");
            File.WriteAllText(journal, content, new UTF8Encoding(false));
            Assert.Throws<InvalidDataException>(() => { using var reopened = new DurableTransactionStore(directory); },
                "TEST-PERSIST-039 corrupt journal fails closed " + kind);
            Assert.Equal(content, File.ReadAllText(journal), "TEST-PERSIST-040 corruption is not silently rewritten " + kind);
        }
    }

    private static void DurableWorkflow(string root)
    {
        foreach (var commit in new[] { true, false })
        {
            var baseline = Path.Combine(root, commit ? "commit-baseline" : "discard-baseline");
            var overlay = Path.Combine(root, commit ? "commit-overlay" : "discard-overlay");
            var metadata = Path.Combine(root, commit ? "commit-metadata" : "discard-metadata");
            Directory.CreateDirectory(baseline);
            File.WriteAllText(Path.Combine(baseline, "A.txt"), "before");
            TransactionId id;
            DiffId diffId;
            using (var store = new DurableTransactionStore(metadata))
            {
                var transaction = store.Create(Owner, "editor.exe", "", baseline, overlay, Guid.NewGuid(), At).Transaction;
                id = transaction.Id;
                var workflow = new FileTransactionWorkflow(transaction, new FileOverlaySession(id, baseline, overlay), store);
                workflow.Start(At);
                workflow.Overlay.WriteAllBytes("A.txt", Encoding.UTF8.GetBytes("after"));
                var diff = workflow.StopAndBuildDiff(1, At);
                diffId = diff.Id;
                Assert.Equal(diffId, store.GetDiff(id)!.Id, "TEST-PERSIST-044 workflow saves Diff with state");
                if (commit)
                {
                    var result = workflow.Commit(diff.Items.Select(i => i.Id).ToHashSet(), At);
                    Assert.True(result.Succeeded, "TEST-PERSIST-045 durable workflow commit");
                }
                else workflow.Discard(At);
                Assert.Equal(TransactionState.Completed, store.Get(id)!.CurrentState, "TEST-PERSIST-046 completion persisted");
            }
            using var reopened = new DurableTransactionStore(metadata);
            Assert.Equal(TransactionState.Completed, reopened.Get(id)!.CurrentState, "TEST-PERSIST-047 workflow completion after restart");
            Assert.Equal(diffId, reopened.GetDiff(id)!.Id, "TEST-PERSIST-048 Diff retained for audit after overlay cleanup");
            Assert.Equal(commit ? "after" : "before", File.ReadAllText(Path.Combine(baseline, "A.txt")), "TEST-PERSIST-049 correct baseline outcome");
            Assert.False(Directory.Exists(overlay), "TEST-PERSIST-050 overlay cleanup done before Completed");
            Assert.Equal(7, reopened.ReplayEvents().Count, "TEST-PERSIST-051 all lifecycle events retained");
        }
    }

    private static void AbruptProcessExit(string root)
    {
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("persistence-crash-child");
        start.ArgumentList.Add(root);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start crash test child.");
        var marker = Path.Combine(root, "acknowledged.txt");
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker))
            {
                if (child.HasExited || timer.Elapsed > TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException("Crash test child did not acknowledge its durable write.");
                Thread.Sleep(20);
            }
            // Opening the marker succeeds only after the child's write handle has closed.
            string idText;
            var markerTimer = Stopwatch.StartNew();
            while (true)
            {
                try { idText = File.ReadAllText(marker); if (Guid.TryParse(idText, out _)) break; }
                catch (IOException) { }
                if (markerTimer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("Incomplete child marker.");
                Thread.Sleep(20);
            }
            child.Kill(entireProcessTree: true);
            if (!child.WaitForExit(10000)) throw new TimeoutException("Crash test child did not stop.");
            var id = new TransactionId(Guid.Parse(idText));
            using var reopened = new DurableTransactionStore(root);
            Assert.Equal(TransactionState.Running, reopened.Get(id)!.CurrentState, "TEST-PERSIST-041 killed writer retains acknowledged state");
            Assert.Equal(3, reopened.ReplayEvents().Count, "TEST-PERSIST-042 killed writer retains complete event log");
            Assert.Equal(RestartDisposition.RecoveryRequired, reopened.ScanAfterRestart(At).Single().Disposition,
                "TEST-PERSIST-043 killed writer requires recovery");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(10000); }
        }
    }

    public static void RunChild(string[] args)
    {
        if (args.Length != 2 || args[0] != "persistence-crash-child") throw new ArgumentException("Unknown test child command.");
        using var store = new DurableTransactionStore(args[1]);
        var transaction = Create(new DurableTransactionApplication(store), Guid.NewGuid()).Transaction;
        transaction = Move(store, transaction, TransactionState.Starting);
        transaction = Move(store, transaction, TransactionState.Running);
        File.WriteAllText(Path.Combine(args[1], "acknowledged.txt"), transaction.Id.ToString());
        // Parent terminates us without Dispose to prove process-level restart behavior.
        Thread.Sleep(Timeout.Infinite);
    }
}
