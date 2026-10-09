# Service: user-mode transaction foundation

TransactionalWindows.Service contains the application-layer skeleton and a tested user-mode file transaction slice. FileOverlaySession implements copy-on-write, tombstones, path checks and an append-only JSONL change journal for one local baseline directory. FileDiffEngine materializes a file Diff and FileCommitEngine applies selected file changes offline with baseline digest checks and best-effort per-file recovery.

FileTransactionWorkflow coordinates the overlay with the canonical transaction states. It can optionally use DurableTransactionStore to persist lifecycle facts before filesystem effects and retain the Diff and audit after cleanup. Existing in-memory callers continue to work.

DurableTransactionStore implements ITransactionRepository using a single-writer, checksummed JSONL write-ahead journal. Each flushed record contains the transaction snapshot, operation result and event. DurableTransactionApplication provides owner checks for trusted callers, command replay and startup scanning. Interrupted active states become Failed with RecoveryRequired; no process or file effects are automatically resumed. The metadata root must be separate from the baseline and overlay. See [Phase 2 report](../../docs/phase-2-report.md) for schema, tests and limitations.

This is a controlled proof of concept and not a Windows Service host: Named Pipe, Filter Manager Communication Port, process launcher, Job Object, Registry interception, minifilter and WPF UI remain future work. Durable metadata is implemented; overlay reconstruction and durable recovery of individual Commit operations are not. Ordinary applications are not transparently redirected yet; the minifilter feasibility gate must be closed before claiming filesystem MVP support. The repository/application APIs are internal service boundaries and must not be directly exposed as arbitrary state-change commands to a future UI or pipe client.

