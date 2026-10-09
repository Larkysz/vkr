# Service: user-mode transaction foundation

TransactionalWindows.Service contains the application-layer skeleton and a tested user-mode file transaction slice. FileOverlaySession implements copy-on-write, tombstones, path checks and an append-only JSONL change journal for one local baseline directory. FileDiffEngine materializes a file Diff and FileCommitEngine applies selected file changes offline with baseline digest checks and best-effort per-file recovery.

FileTransactionWorkflow coordinates the overlay with the canonical transaction states. This is a controlled proof of concept and not a Windows Service host: Named Pipe, Filter Manager Communication Port, process launcher, Job Object, persistence, Registry interception, minifilter and WPF UI remain future work. Ordinary applications are not transparently redirected yet; the minifilter feasibility gate must be closed before claiming filesystem MVP support.

