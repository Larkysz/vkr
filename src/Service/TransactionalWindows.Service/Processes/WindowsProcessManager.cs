using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using TransactionalWindows.Core.Contracts;
using TransactionalWindows.Core.Domain;
using N = TransactionalWindows.Service.Processes.WindowsProcessNative;

namespace TransactionalWindows.Service.Processes;

public enum ProcessLaunchMode { RequireIsolation, ControlledUnisolated }

/// <summary>Current-user Job Object launcher. No file/Registry redirection is provided.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessManager : IProcessManager, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<TransactionId, Session> _sessions = new();
    private bool _disposed;

    public ProcessNode StartSuspended(Transaction transaction, ProcessLaunchMode mode, Action<ProcessNode>? onChanged = null)
    {
        if (mode != ProcessLaunchMode.ControlledUnisolated)
            throw new ProcessManagementException(ProcessErrorCode.UnsupportedIsolation, "An isolation driver is not available. ControlledUnisolated must be requested explicitly.");
        if (transaction.CurrentState != TransactionState.Starting || transaction.Id.Value == Guid.Empty)
            throw new ArgumentException("A process requires a Starting transaction with a valid identity.", nameof(transaction));
        using var identity = WindowsIdentity.GetCurrent();
        using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);
        if (impersonated is not null || identity.User?.Value != transaction.OwnerSid)
            throw new ProcessManagementException(ProcessErrorCode.OwnerMismatch, "Only a non-impersonating launch as the current user is supported.");
        if (!Path.IsPathFullyQualified(transaction.ApplicationPath) || !File.Exists(transaction.ApplicationPath)
            || !string.Equals(Path.GetExtension(transaction.ApplicationPath), ".exe", StringComparison.OrdinalIgnoreCase)
            || transaction.ApplicationPath.Contains('"') || transaction.ApplicationPath.Contains('\0')
            || transaction.Arguments.Contains('\0') || !Path.IsPathFullyQualified(transaction.WorkingDirectory)
            || !Directory.Exists(transaction.WorkingDirectory))
            throw new ProcessManagementException(ProcessErrorCode.LaunchFailed, "An existing absolute .exe path and working directory are required.");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.ContainsKey(transaction.Id))
                throw new ProcessManagementException(ProcessErrorCode.AlreadyLaunched, "A transaction can launch only one root process.");
            Session? session = null;
            try
            {
                session = new Session(transaction, onChanged);
                _sessions.Add(transaction.Id, session);
                return session.Root;
            }
            catch (Exception ex)
            {
                session?.Dispose();
                _sessions.Remove(transaction.Id);
                if (ex is ProcessManagementException) throw;
                throw new ProcessManagementException(ProcessErrorCode.LaunchFailed, "Suspended launch failed.", ex);
            }
        }
    }

    public void Resume(TransactionId id) => Get(id).Resume();

    public Task<IReadOnlyList<ProcessNode>> GetTreeAsync(TransactionId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Get(id).Snapshot());
    }

    public bool IsEmptyAndObserved(TransactionId id) => Get(id).IsEmptyAndObserved();

    public ProcessNodeId? Resolve(TransactionId id, int pid, long creationIdentity)
        => Get(id).Snapshot().FirstOrDefault(n => n.Pid == pid && n.ProcessCreationIdentity == creationIdentity
            && n.Status != ProcessNodeStatus.Unknown)?.Id;

    /// <summary>Waits for natural exit. Timeout does not pretend quiescence or silently kill.</summary>
    public Task QuiesceAsync(TransactionId id, TimeSpan timeout, CancellationToken cancellationToken)
        => Get(id).WaitEmptyAsync(timeout, cancellationToken);

    public async Task TerminateAsync(TransactionId id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Get(id).Terminate();
        await Get(id).WaitEmptyAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private Session Get(TransactionId id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sessions.TryGetValue(id, out var session) ? session
                : throw new KeyNotFoundException("No controlled process group for this transaction.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _sessions.Values) session.Dispose();
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly object _gate = new();
        private readonly Transaction _transaction;
        private readonly Action<ProcessNode>? _onChanged;
        private readonly KernelHandle _job;
        private readonly KernelHandle _port;
        private readonly Dictionary<ProcessNodeId, Entry> _entries = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
        private KernelHandle? _thread;
        private Task? _monitor;
        private string? _error;
        private bool _resumed, _terminating, _disposed;
        internal ProcessNode Root { get; private set; } = null!;

        private sealed record Entry(KernelHandle Handle, ProcessNode Node)
        {
            internal ProcessNode Snapshot { get; set; } = Node;
        }

        internal Session(Transaction transaction, Action<ProcessNode>? onChanged)
        {
            _transaction = transaction;
            _onChanged = onChanged;
            _job = N.CreateJobObjectW(IntPtr.Zero, null);
            _port = N.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
            KernelHandle? process = null;
            try
            {
                if (_job.IsInvalid || _port.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Create Job/IO completion port");
                N.Configure(_job, 9, new N.ExtendedLimitInformation { Basic = new N.BasicLimitInformation { LimitFlags = N.KillOnJobClose } });
                // Breakaway and silent-breakaway flags are intentionally absent.
                N.Configure(_job, 7, new N.CompletionPortAssociation { Key = new IntPtr(1), Port = _port.DangerousGetHandle() });
                var startup = new N.StartupInfo { Size = (uint)Marshal.SizeOf<N.StartupInfo>() };
                var command = new StringBuilder('"' + transaction.ApplicationPath + "\" " + transaction.Arguments);
                if (command.Length >= 32767) throw new ArgumentException("Command line exceeds the Windows limit.");
                N.Check(N.CreateProcessW(transaction.ApplicationPath, command, IntPtr.Zero, IntPtr.Zero, false,
                    N.CreateSuspended | N.CreateNoWindow, IntPtr.Zero, transaction.WorkingDirectory, ref startup, out var info), "CreateProcess suspended");
                process = N.Own(info.Process);
                _thread = N.Own(info.Thread);
                if (!N.AssignProcessToJobObject(_job, process))
                    throw new ProcessManagementException(ProcessErrorCode.JobAssignmentFailed, "The suspended root could not be assigned to the Job Object.", new Win32Exception(Marshal.GetLastWin32Error()));
                Root = Inspect(process, info.ProcessId, true, ProcessNodeStatus.Starting);
                _entries.Add(Root.Id, new Entry(process, Root));
                _onChanged?.Invoke(Root); // Must finish (including durable storage) before Resume.
                process = null;
                _monitor = Task.Factory.StartNew(Monitor, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                if (process is not null && !process.IsInvalid) { N.TerminateProcess(process, 1); N.WaitForSingleObject(process, 5000); }
                process?.Dispose();
                _thread?.Dispose();
                _job.Dispose();
                foreach (var entry in _entries.Values) entry.Handle.Dispose();
                _port.Dispose();
                _stop.Dispose();
                throw;
            }
        }

        internal void Resume()
        {
            lock (_gate)
            {
                EnsureHealthy();
                if (_resumed) throw new InvalidOperationException("Root has already been resumed.");
                var entry = _entries[Root.Id];
                var running = entry.Snapshot with { Status = ProcessNodeStatus.Running };
                _onChanged?.Invoke(running);
                if (N.ResumeThread(_thread!) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread");
                entry.Snapshot = Root = running;
                _resumed = true;
                _thread!.Dispose();
                _thread = null;
            }
        }

        internal IReadOnlyList<ProcessNode> Snapshot()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Refresh();
                return _entries.Values.Select(e => e.Snapshot).OrderBy(n => n.StartedAt).ThenBy(n => n.Id.Value).ToArray();
            }
        }

        internal bool IsEmptyAndObserved()
        {
            lock (_gate)
            {
                EnsureHealthy();
                Refresh();
                EnsureHealthy();
                var accounting = N.Accounting(_job);
                if (accounting.ActiveProcesses != 0) return false;
                if (_terminating)
                {
                    // TerminateJobObject can signal the accounting barrier before
                    // every retained handle is observed as signalled. Once the Job
                    // reports zero active members, the remaining nodes are known to
                    // have been force-terminated and can be closed as terminal.
                    foreach (var entry in _entries.Values.Where(e => e.Snapshot.Status is ProcessNodeStatus.Starting or ProcessNodeStatus.Running).ToArray())
                    {
                        var ended = entry.Snapshot with
                        {
                            Status = ProcessNodeStatus.Terminated,
                            ExitedAt = DateTimeOffset.UtcNow
                        };
                        _onChanged?.Invoke(ended);
                        entry.Snapshot = ended;
                    }
                }
                if (accounting.TotalProcesses != _entries.Count || _entries.Values.Any(e => e.Snapshot.Status is ProcessNodeStatus.Starting or ProcessNodeStatus.Running))
                    throw new ProcessManagementException(ProcessErrorCode.ObservationIncomplete, $"Job is empty but its process history is incomplete (total={accounting.TotalProcesses}, observed={_entries.Count}, active={accounting.ActiveProcesses}).");
                return true;
            }
        }

        internal async Task WaitEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsEmptyAndObserved()) return;
                if (clock.Elapsed >= timeout)
                    throw new ProcessManagementException(ProcessErrorCode.QuiesceTimeout, "ProcessQuiesceTimeout: the Job still contains active processes.");
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }

        internal void Terminate()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _terminating = true;
                // Capture every currently active member before the Job is terminated.
                // This keeps the durable process tree complete even when a child
                // starts and the completion-port notification races with timeout.
                Refresh();
                N.Check(N.TerminateJobObject(_job, 0xE0000001), "TerminateJobObject");
            }
        }

        private void Monitor()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var received = N.GetQueuedCompletionStatus(_port, out var message, out _, out var value, 50);
                    if (!received && Marshal.GetLastWin32Error() != (int)N.WaitTimeout)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Job completion notification");
                    lock (_gate)
                    {
                        if (_disposed || _stop.IsCancellationRequested) return;
                        if (received && message == N.NewProcess) Capture(checked((uint)value.ToInt64()));
                        Refresh();
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _error = "Process observation failed: " + ex.Message;
                    if (!_disposed) N.TerminateJobObject(_job, 0xE0000002);
                }
            }
        }

        private void Refresh()
        {
            foreach (var pid in N.ActivePids(_job)) Capture(pid);
            foreach (var entry in _entries.Values)
            {
                if (entry.Snapshot.Status is not (ProcessNodeStatus.Starting or ProcessNodeStatus.Running)) continue;
                var wait = N.WaitForSingleObject(entry.Handle, 0);
                if (wait == N.WaitTimeout) continue;
                if (wait != N.WaitObject0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Wait for process");
                N.Check(N.GetProcessTimes(entry.Handle, out _, out var exit, out _, out _), "Read exit time");
                N.Check(N.GetExitCodeProcess(entry.Handle, out var code), "Read exit code");
                var ended = entry.Snapshot with
                {
                    Status = _terminating ? ProcessNodeStatus.Terminated : ProcessNodeStatus.Exited,
                    ExitedAt = DateTimeOffset.FromFileTime(exit).ToUniversalTime(), ExitCode = unchecked((int)code)
                };
                _onChanged?.Invoke(ended);
                entry.Snapshot = ended;
            }
        }

        private void Capture(uint pid)
        {
            // Retained handles + creation time protect against PID reuse. A late
            // notification for an already retained process needs no second handle.
            if (_entries.Values.Any(e => e.Snapshot.Pid == pid && N.WaitForSingleObject(e.Handle, 0) == N.WaitTimeout)) return;
            using var candidate = N.OpenProcess(N.ProcessQueryLimitedInformation | N.Synchronize, false, pid);
            if (candidate.IsInvalid)
            {
                if (_entries.Values.Any(e => e.Snapshot.Pid == pid)) return;
                // Short-lived missed children are detected by TotalProcesses at the final barrier.
                return;
            }
            N.Check(N.GetProcessTimes(candidate, out var creation, out _, out _, out _), "Read process creation identity");
            if (_entries.Values.Any(e => e.Snapshot.Pid == pid && e.Snapshot.ProcessCreationIdentity == creation)) return;
            N.Check(N.IsProcessInJob(candidate, _job, out var belongs), "Check Job membership");
            if (!belongs) return; // PID was reused outside this Job; never attach it.
            // Open and retain a separate handle; inspect its identity again before insertion.
            var handle = N.OpenProcess(N.ProcessQueryLimitedInformation | N.Synchronize, false, pid);
            try
            {
                if (handle.IsInvalid) return;
                var node = Inspect(handle, pid, false, ProcessNodeStatus.Running);
                if (node.ProcessCreationIdentity != creation) return;
                _onChanged?.Invoke(node);
                _entries.Add(node.Id, new Entry(handle, node));
                handle = null!;
            }
            finally { handle?.Dispose(); }
        }

        private ProcessNode Inspect(KernelHandle handle, uint pid, bool root, ProcessNodeStatus status)
        {
            N.Check(N.IsProcessInJob(handle, _job, out var belongs), "Validate Job membership");
            if (!belongs) throw new ProcessManagementException(ProcessErrorCode.ObservationIncomplete, "Process does not belong to its transaction Job.");
            N.Check(N.GetProcessTimes(handle, out var creation, out _, out _, out _), "Read process identity");
            N.Check(N.OpenProcessToken(handle, N.TokenQuery, out var token), "Read process token");
            string? sid;
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle())) sid = identity.User?.Value;
            N.Check(N.ProcessIdToSessionId(pid, out var session), "Read process session");
            if (sid != _transaction.OwnerSid || session != _sessionId)
                throw new ProcessManagementException(ProcessErrorCode.OwnerMismatch, "Process SID/session differs from its transaction owner.");
            var image = new StringBuilder(32768);
            var size = (uint)image.Capacity;
            N.Check(N.QueryFullProcessImageNameW(handle, 0, image, ref size), "Read process image");
            int? parentPid = null;
            if (N.NtQueryInformationProcess(handle, 0, out var basic, Marshal.SizeOf<N.ProcessBasicInformation>(), IntPtr.Zero) == 0)
                parentPid = checked((int)basic.InheritedFromProcessId.ToInt64());
            var parent = root ? null : _entries.Values.Select(e => e.Snapshot).Where(n => n.Pid == parentPid && n.ProcessCreationIdentity <= creation)
                .OrderByDescending(n => n.ProcessCreationIdentity).FirstOrDefault();
            return new ProcessNode
            {
                Id = ProcessNodeId.New(), TransactionId = _transaction.Id, Pid = checked((int)pid),
                ProcessCreationIdentity = creation, ParentPid = parentPid, ParentNodeId = parent?.Id, IsRoot = root,
                OwnerSid = sid, SessionId = checked((int)session), JobMembershipConfirmed = true,
                ImagePath = image.ToString(), CommandLine = null, // Do not expose potentially secret arguments.
                StartedAt = DateTimeOffset.FromFileTime(creation).ToUniversalTime(), Status = status
            };
        }

        private void EnsureHealthy()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_error is not null) throw new ProcessManagementException(ProcessErrorCode.ObservationIncomplete, _error);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Cancel();
                // Job handle is not inheritable; closing the last handle kills remaining members.
                _job.Dispose();
            }
            _monitor?.GetAwaiter().GetResult();
            _thread?.Dispose();
            foreach (var entry in _entries.Values) entry.Handle.Dispose();
            _port.Dispose();
            _stop.Dispose();
        }
    }
}
