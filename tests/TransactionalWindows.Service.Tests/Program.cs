using System.Text;
using TransactionalWindows.Core.Domain;
using TransactionalWindows.Service;

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

    public static void False(bool value, string name) => True(!value, name);

    public static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{name}: expected {typeof(T).Name}");
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            PersistenceTests.RunChild(args);
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "tw-service-tests-" + Guid.NewGuid().ToString("N"));
        var baseline = Path.Combine(root, "baseline");
        var overlay = Path.Combine(root, "overlay");
        Directory.CreateDirectory(baseline);
        File.WriteAllText(Path.Combine(baseline, "A.txt"), "before", Encoding.UTF8);
        File.WriteAllText(Path.Combine(baseline, "C.txt"), "remove-me", Encoding.UTF8);
        var transactionId = TransactionId.New();

        try
        {
            var session = new FileOverlaySession(transactionId, baseline, overlay);
            session.WriteAllBytes("A.txt", Encoding.UTF8.GetBytes("after"));
            session.CreateFile("B.txt", Encoding.UTF8.GetBytes("created"));
            session.DeleteFile("C.txt");

            Assert.Equal("after", Encoding.UTF8.GetString(session.ReadAllBytes("A.txt")), "TEST-FS-001 overlay read after write");
            Assert.True(session.ExistsEffective("B.txt"), "TEST-FS-002 created file visible in overlay");
            Assert.False(session.ExistsEffective("C.txt"), "TEST-FS-003 tombstone hides deleted file");
            Assert.Equal("before", File.ReadAllText(Path.Combine(baseline, "A.txt")), "TEST-FS-004 baseline unchanged before commit");
            Assert.False(File.Exists(Path.Combine(baseline, "B.txt")), "TEST-FS-005 create not visible in baseline");

            var diff = FileDiffEngine.Build(session, 1);
            Assert.Equal(transactionId, diff.TransactionId, "TEST-FS-006 diff transaction identity");
            Assert.Equal(3, diff.Items.Count, "TEST-FS-007 diff has three logical changes");
            var selected = diff.Items.Where(item => item.Path is "A.txt" or "B.txt").Select(item => item.Id).ToHashSet();
            var result = new FileCommitEngine().Commit(session, diff, selected);
            Assert.True(result.Succeeded, "TEST-FS-008 selective commit succeeds");
            Assert.Equal("after", File.ReadAllText(Path.Combine(baseline, "A.txt")), "TEST-FS-009 selected modification committed");
            Assert.Equal("created", File.ReadAllText(Path.Combine(baseline, "B.txt")), "TEST-FS-010 selected creation committed");
            Assert.Equal("remove-me", File.ReadAllText(Path.Combine(baseline, "C.txt")), "TEST-FS-011 unselected deletion skipped");

            var discardRoot = Path.Combine(root, "discard");
            var discardOverlay = Path.Combine(root, "discard-overlay");
            Directory.CreateDirectory(discardRoot);
            File.WriteAllText(Path.Combine(discardRoot, "keep.txt"), "keep", Encoding.UTF8);
            var discardSession = new FileOverlaySession(TransactionId.New(), discardRoot, discardOverlay);
            discardSession.WriteAllBytes("keep.txt", Encoding.UTF8.GetBytes("temporary"));
            discardSession.CreateFile("new.txt", Encoding.UTF8.GetBytes("temporary"));
            discardSession.Discard();
            Assert.Equal("keep", File.ReadAllText(Path.Combine(discardRoot, "keep.txt")), "TEST-FS-012 discard preserves baseline");
            Assert.False(File.Exists(Path.Combine(discardRoot, "new.txt")), "TEST-FS-013 discard removes uncommitted creation");
            Assert.Throws<ArgumentException>(() => session.ReadAllBytes("..\\outside.txt"), "TEST-FS-014 path traversal rejected");
            Assert.Throws<ArgumentException>(() => new FileOverlaySession(TransactionId.New(), baseline, Path.Combine(baseline, "overlay")), "TEST-FS-015 overlay cannot be inside baseline");

            var recreate = new FileOverlaySession(TransactionId.New(), baseline, Path.Combine(root, "recreate-overlay"));
            recreate.CreateFile("temporary.txt", Encoding.UTF8.GetBytes("one"));
            recreate.DeleteFile("temporary.txt");
            var createDeleteDiff = FileDiffEngine.Build(recreate, 1);
            Assert.Equal(0, createDeleteDiff.Items.Count, "TEST-FS-017 create-delete is folded out");
            recreate.CreateFile("temporary.txt", Encoding.UTF8.GetBytes("two"));
            Assert.Equal("two", Encoding.UTF8.GetString(recreate.ReadAllBytes("temporary.txt")), "TEST-FS-016 recreate clears tombstone");
            var recreateDiff = FileDiffEngine.Build(recreate, 1);
            Assert.Equal(1, recreateDiff.Items.Count, "TEST-FS-018 recreate is a create");
            Assert.Equal(FileChangeOperation.Create, recreateDiff.Items[0].FileOperation, "TEST-FS-019 recreate operation retained");

            var rename = new FileOverlaySession(TransactionId.New(), baseline, Path.Combine(root, "rename-overlay"));
            rename.RenameFile("A.txt", "renamed.txt");
            var renameDiff = FileDiffEngine.Build(rename, 1);
            Assert.Equal(1, renameDiff.Items.Count, "TEST-FS-020 rename is one logical item");
            Assert.Equal(FileChangeOperation.Rename, renameDiff.Items[0].FileOperation, "TEST-FS-021 rename operation retained");
            var renameResult = new FileCommitEngine().Commit(rename, renameDiff, renameDiff.Items.Select(x => x.Id).ToHashSet());
            Assert.True(renameResult.Succeeded, "TEST-FS-022 rename commit succeeds");
            Assert.False(File.Exists(Path.Combine(baseline, "A.txt")), "TEST-FS-023 rename source removed");
            Assert.Equal("after", File.ReadAllText(Path.Combine(baseline, "renamed.txt")), "TEST-FS-024 rename destination contains baseline");

            var workflowRoot = Path.Combine(root, "workflow");
            var workflowOverlay = Path.Combine(root, "workflow-overlay");
            Directory.CreateDirectory(workflowRoot);
            File.WriteAllText(Path.Combine(workflowRoot, "value.txt"), "one", Encoding.UTF8);
            var workflowTransaction = Transaction.Create(TransactionId.New(), "S-1-5-21-test", "C:\\Apps\\Editor.exe", "", workflowRoot, workflowOverlay, DateTimeOffset.UtcNow);
            var workflow = new FileTransactionWorkflow(workflowTransaction, new FileOverlaySession(workflowTransaction.Id, workflowRoot, workflowOverlay));
            workflow.Start(DateTimeOffset.UtcNow);
            workflow.Overlay.WriteAllBytes("value.txt", Encoding.UTF8.GetBytes("two"));
            var workflowDiff = workflow.StopAndBuildDiff(1, DateTimeOffset.UtcNow);
            var workflowResult = workflow.Commit(workflowDiff.Items.Select(x => x.Id).ToHashSet(), DateTimeOffset.UtcNow);
            Assert.True(workflowResult.Succeeded, "TEST-FS-025 workflow commit succeeds");
            Assert.Equal(TransactionState.Completed, workflow.Transaction.CurrentState, "TEST-FS-026 workflow reaches completed");
            Assert.Equal("two", File.ReadAllText(Path.Combine(workflowRoot, "value.txt")), "TEST-FS-027 workflow applies file");

            var conflictRoot = Path.Combine(root, "conflict");
            var conflictOverlay = Path.Combine(root, "conflict-overlay");
            Directory.CreateDirectory(conflictRoot);
            File.WriteAllText(Path.Combine(conflictRoot, "conflict.txt"), "baseline", Encoding.UTF8);
            var conflictSession = new FileOverlaySession(TransactionId.New(), conflictRoot, conflictOverlay);
            conflictSession.WriteAllBytes("conflict.txt", Encoding.UTF8.GetBytes("transaction"));
            var conflictDiff = FileDiffEngine.Build(conflictSession, 1);
            File.WriteAllText(Path.Combine(conflictRoot, "conflict.txt"), "external", Encoding.UTF8);
            var conflictResult = new FileCommitEngine().Commit(conflictSession, conflictDiff, conflictDiff.Items.Select(x => x.Id).ToHashSet());
            Assert.False(conflictResult.Succeeded, "TEST-FS-028 external baseline change fails closed");
            Assert.Equal("external", File.ReadAllText(Path.Combine(conflictRoot, "conflict.txt")), "TEST-FS-029 conflict does not overwrite host");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("Service file overlay tests passed.");
        PersistenceTests.Run();
    }
}
