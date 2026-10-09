using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;

#pragma warning disable CA1416
using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.State;
using TransactionalWindows.Service.Processes;

internal static class ProcessManagementTests
{
    public static void RunChild(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("A process child command is required.");
        switch (args[0])
        {
            case "process-child":
                if (args.Length != 4) throw new ArgumentException("process-child requires marker, parent lifetime and child lifetime.");
                var childMarker = Unquote(args[1]);
                var parentMilliseconds = ParseMilliseconds(args[2]);
                var childMilliseconds = ParseMilliseconds(args[3]);
                using (StartSelf("process-leaf", childMarker, childMilliseconds.ToString(CultureInfo.InvariantCulture)))
                {
                    Thread.Sleep(parentMilliseconds);
                }
                return;
            case "process-leaf":
                if (args.Length != 3) throw new ArgumentException("process-leaf requires marker and lifetime.");
                File.WriteAllText(Unquote(args[1]), "leaf");
                Thread.Sleep(ParseMilliseconds(args[2]));
                return;
            default:
                throw new ArgumentException($"Unknown process child command: {args[0]}");
        }
    }

    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Process management tests skipped: Windows is required.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "tw-process-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            JobTracksChildAndWaits(Path.Combine(root, "tree"));
            TimeoutAndTermination(Path.Combine(root, "timeout"));
            PolicyAndInputValidation(Path.Combine(root, "policy"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        Console.WriteLine("Service process management tests passed.");
    }

    private static void JobTracksChildAndWaits(string root)
    {
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "leaf.marker");
        var transaction = StartingTransaction(root, "process-child " + Quote(marker) + " 1800 350");
        var changes = new List<ProcessNode>();
        using var manager = new WindowsProcessManager();
        var rootNode = manager.StartSuspended(transaction, ProcessLaunchMode.ControlledUnisolated, node =>
        {
            lock (changes) changes.Add(node);
        });
        Assert.True(rootNode.IsRoot && rootNode.JobMembershipConfirmed, "TEST-PROC-001 root is assigned before resume");
        Assert.Equal(ProcessNodeStatus.Starting, rootNode.Status, "TEST-PROC-002 root starts suspended");
        manager.Resume(transaction.Id);
        var active = manager.GetTreeAsync(transaction.Id, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(active.Any(n => n.Id == rootNode.Id && n.Status == ProcessNodeStatus.Running), "TEST-PROC-003 root becomes running");
        var started = Stopwatch.StartNew();
        manager.QuiesceAsync(transaction.Id, TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(250), "TEST-PROC-004 wait includes child lifetime");
        Assert.True(File.Exists(marker), "TEST-PROC-005 child executed inside Job");
        var tree = manager.GetTreeAsync(transaction.Id, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(tree.Count >= 2, "TEST-PROC-006 root and child are observed");
        Assert.Equal(1, tree.Count(n => n.IsRoot), "TEST-PROC-007 exactly one root");
        Assert.True(tree.Count(n => !n.IsRoot && n.ParentNodeId is not null) >= 1, "TEST-PROC-008 child links to observed parent");
        Assert.True(tree.All(n => n.TransactionId == transaction.Id && n.JobMembershipConfirmed), "TEST-PROC-009 all nodes share context");
        Assert.True(tree.All(n => n.Status is ProcessNodeStatus.Exited or ProcessNodeStatus.Terminated), "TEST-PROC-010 all nodes are terminal");
        Assert.True(changes.Any(n => n.Status == ProcessNodeStatus.Exited), "TEST-PROC-011 exit observation is emitted");
        Assert.Equal(rootNode.Id, manager.Resolve(transaction.Id, rootNode.Pid, rootNode.ProcessCreationIdentity), "TEST-PROC-012 root identity resolves while active or retained");
    }

    private static void TimeoutAndTermination(string root)
    {
        Directory.CreateDirectory(root);
        var transaction = StartingTransaction(root, "process-child " + Quote(Path.Combine(root, "leaf.marker")) + " 5000 5000");
        using var manager = new WindowsProcessManager();
        manager.StartSuspended(transaction, ProcessLaunchMode.ControlledUnisolated);
        manager.Resume(transaction.Id);
        var childDeadline = Stopwatch.StartNew();
        while (manager.GetTreeAsync(transaction.Id, CancellationToken.None).GetAwaiter().GetResult().Count < 2
            && childDeadline.Elapsed < TimeSpan.FromSeconds(2))
            Thread.Sleep(20);
        Assert.True(manager.GetTreeAsync(transaction.Id, CancellationToken.None).GetAwaiter().GetResult().Count >= 2,
            "TEST-PROC-013 child is observed before timeout scenario");
        Assert.Throws<ProcessManagementException>(() => manager.QuiesceAsync(transaction.Id, TimeSpan.FromMilliseconds(80), CancellationToken.None).GetAwaiter().GetResult(),
            "TEST-PROC-014 active child causes quiesce timeout");
        manager.TerminateAsync(transaction.Id, TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
        var tree = manager.GetTreeAsync(transaction.Id, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(tree.Count >= 2, "TEST-PROC-015 terminated tree retains process nodes");
        Assert.True(tree.All(n => n.Status == ProcessNodeStatus.Terminated), "TEST-PROC-016 forced termination is explicit");
    }

    private static void PolicyAndInputValidation(string root)
    {
        Directory.CreateDirectory(root);
        var transaction = StartingTransaction(root, "");
        using var manager = new WindowsProcessManager();
        Assert.Throws<ProcessManagementException>(() => manager.StartSuspended(transaction, ProcessLaunchMode.RequireIsolation),
            "TEST-PROC-017 isolation policy fails closed without driver");
        var invalid = transaction with { ApplicationPath = Path.Combine(root, "missing.exe") };
        Assert.Throws<ProcessManagementException>(() => manager.StartSuspended(invalid, ProcessLaunchMode.ControlledUnisolated),
            "TEST-PROC-018 missing application is rejected");
        var invalidOwner = transaction with { OwnerSid = "S-1-5-18" };
        Assert.Throws<ProcessManagementException>(() => manager.StartSuspended(invalidOwner, ProcessLaunchMode.ControlledUnisolated),
            "TEST-PROC-019 owner mismatch is rejected");
    }

    private static Transaction StartingTransaction(string root, string arguments)
    {
        var owner = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current SID unavailable.");
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path unavailable.");
        var working = AppContext.BaseDirectory;
        var transaction = Transaction.Create(TransactionId.New(), owner, path, arguments, working, Path.Combine(root, "overlay"), DateTimeOffset.UtcNow);
        return TransactionStateMachine.Transition(transaction, TransactionState.Starting, DateTimeOffset.UtcNow);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"");
        return value;
    }

    private static int ParseMilliseconds(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : throw new ArgumentException("Lifetime must be a non-negative integer number of milliseconds.");

    private static Process StartSelf(params string[] arguments)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path unavailable.");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        var commandLine = Environment.GetCommandLineArgs();
        if (Path.GetFileName(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (commandLine.Length == 0 || !commandLine[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test assembly path is unavailable for a dotnet-hosted child.");
            start.ArgumentList.Add(commandLine[0]);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start process child.");
    }

}

#pragma warning restore CA1416
