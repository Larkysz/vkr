# Implementation Blueprint

## 1. Purpose and Audit Gate

Этот документ переводит утверждённую архитектуру в проектные границы, модели, контракты и проверяемые этапы разработки. Это blueprint, а не production implementation: на этом этапе не создаются C/C++, C#, WPF, Service или Driver исходники.

Перед разработкой команда должна учитывать [architecture-audit.md](architecture-audit.md). Файл [architecture.md](architecture.md) отсутствует в репозитории и не считается доступным архитектурным источником. До появления утверждённого architecture baseline решения и ограничения из audit имеют приоритет.

Audit содержит CRITICAL проблему для прозрачной Registry virtualization callback-only способом. Поэтому Registry overlay здесь определяется как интерфейс и feasibility phase, но не объявляется реализуемой частью MVP до закрытия gate REG-GATE-01. Остальные части можно проектировать и прототипировать независимо.

Решение REG-GATE-01: registry callbacks сами по себе не предоставляют достаточного per-process replacement для OpenKey, QueryValue и enumeration. До разработки Registry overlay нужно доказать конкретный дополнительный interception mechanism для поддержанного API набора. Если spike не проходит, зафиксировать пересмотр MVP отдельным ADR; не отправлять операции в host как будто они изолированы.

## 2. Repository Structure

~~~text
src/
  Core/
    TransactionalWindows.Core/
    TransactionalWindows.Protocol/
  Service/
    TransactionalWindows.Service/
    TransactionalWindows.ServiceHost/
    TransactionalWindows.Commit/
  Driver/
    TransactionalWindows.Minifilter/
    TransactionalWindows.DriverProtocol/
  Registry/
    TransactionalWindows.Registry.Contracts/
    TransactionalWindows.Registry.Feasibility/
  Shared/
    TransactionalWindows.Contracts.Schema/
desktop/
  TransactionalWindows.UI/
tests/
  Unit/
  Integration/
  Driver/
  Filesystem/
  Registry/
  Process/
  Commit/
  Recovery/
  Stress/
docs/
~~~

| Каталог | Назначение и ответственность | Зависимости | Запрещено |
| --- | --- | --- | --- |
| src/Core | Чистые domain models, state rules, IDs, dispositions, errors, dependency algorithms. | BCL only. | WPF, Windows APIs, Driver, IPC transports, file/Registry effects. |
| src/Service | Application use cases, repositories, process orchestration, event coordination. | Core, Protocol, Commit contracts, Driver channel abstractions. | WPF references, direct kernel API, domain rule duplication. |
| src/Driver | Minifilter interception, kernel context, policy cache, bounded event queue. | WDK, C-compatible protocol schema. | WPF, C# user-mode assembly, business state machine, synchronous RPC to Service. |
| src/Registry | Feasibility spike and only later a selected interception adapter. | Approved ADR, Protocol contracts where applicable. | Pretending CM callback alone replaces Registry handles/results; dependency on WPF. |
| src/Shared | Versioned language-neutral schema inputs and generated protocol constants. | Schema toolchain only. | Runtime business logic or platform APIs. |
| desktop | Thin WPF client and presentation models. | Protocol client DTOs only. | Direct Driver/IOCTL/Filter Manager access, canonical Transaction state. |
| tests | Domain, component, driver, Windows integration and stress validation. | Project under test and test harness. | Production behavior hidden in tests; silent cleanup of user data. |
| docs | Architecture baseline, ADRs, model and test documentation. | None. | Undocumented policy changes. |

Boundary:

~~~text
UI --Named Pipe/DTO--> Service Host --> Core use cases
                                   --> Process Manager
                                   --> Commit Engine
                                   --> Driver Channel --Flt Port--> Minifilter
                                   --> Repositories / Overlay Store
~~~

Kernel Driver enforces fast-path policy only from a registered immutable context/cache. Core owns domain rules but must not be linked into kernel mode. Service owns durable state and coordination. UI sends commands and renders snapshots/events.

## 3. Logical Projects and Build Targets

Targets below are proposed defaults for a new repository. Pin exact SDK, WDK and Windows SDK versions in build configuration after checking lab machines and signing setup.

| Project | Language/type/target | References | Public surface | Forbidden references |
| --- | --- | --- | --- | --- |
| TransactionalWindows.Core | C#, class library, net10.0 | BCL | IDs, immutable records, enums, state transition policy, dependency closure and domain errors. | WPF, System.ServiceProcess, registry/filesystem APIs, Driver. |
| TransactionalWindows.Protocol | C#, class library, net10.0 | Core IDs or primitive wire types only | UI-Service envelopes, DTOs, version constants, validation limits. | WPF, kernel APIs, repository implementation. |
| TransactionalWindows.Service | C#, class library, net10.0-windows | Core, Protocol, Commit abstractions | Application handlers and orchestration. | WPF, Driver C headers as runtime dependency. |
| TransactionalWindows.ServiceHost | C#, Worker/Windows Service host, net10.0-windows | Service, Protocol | Service lifetime, Named Pipe server, Windows identity setup. | Domain rules duplicated in host. |
| TransactionalWindows.Commit | C#, class library, net10.0-windows | Core | ICommitEngine, journal implementation, file/Registry apply adapters. | UI, Driver RPC from apply loop. |
| TransactionalWindows.Minifilter | C/C++, WDK minifilter .sys, Windows kernel target | WDK + generated C protocol header | Filter callbacks, contexts, communication port handlers. | C# assemblies, WPF, Core user-mode library, Service RPC in callback. |
| TransactionalWindows.DriverProtocol | C-compatible generated header/schema | Shared schema | Protocol version, bounded message layouts and opcodes. | Business logic, C++ runtime allocation patterns unsuitable for kernel. |
| TransactionalWindows.Registry.Contracts | C#, class library, net10.0 | Core primitives | Registry scope and backend abstraction contracts. | Concrete production virtualization assertion before gate. |
| TransactionalWindows.Registry.Feasibility | C#, test harness, net10.0-windows | Contracts, selected prototype adapter | Evidence for REG-GATE-01. | Shipping as a transparent production backend absent ADR. |
| TransactionalWindows.UI | C#, WPF, net10.0-windows | Protocol client | Views, view models, command submission and event presentation. | Core state mutation, Service implementation, Driver access. |
| TransactionalWindows.Tests.* | C# test projects, net10.0 or net10.0-windows | Only tested project(s) | Unit/integration/test harness. | Shared hidden production implementation. |

No project references a WPF assembly from Core. DriverProtocol is generated/shared as a language-neutral wire contract; the Driver never consumes a managed assembly.

## 4. Domain Model

All IDs are opaque UUIDs. Domain records are immutable snapshots; mutable lifecycle is represented by a new aggregate version, not public setters. Wire DTOs are versioned separately from domain types. UTC timestamps use ISO 8601 with Z; content hashes use SHA-256 hex. Process ID alone is never identity.

| Model | Required fields and ID | Mutability/lifecycle | Create/change/read authority | Serialization |
| --- | --- | --- | --- | --- |
| Transaction | TransactionId, owner SID, session ID, root image/args, working directory, volume scope, registry scope, overlay root reference, state, version, timestamps, root ProcessNodeId, DiffId, last error. | Immutable versioned snapshots; state changes only through manager. Created → Starting → Running → Quiescing → DiffReady → Committing/Discarding → Completed, with Failed/Aborted. | Manager creates; Transaction Manager changes; Service/UI read. | Durable repository schema and UI DTO with independent versions. |
| TransactionId | UUID value object. | Immutable for life. | Transaction Manager generates before resource allocation. | UUID string/16-byte wire form. |
| TransactionState | Canonical enum from Transaction model. | Immutable enum; legal transitions are a Core policy. | Core defines; Manager applies; all layers read. | Stable numeric wire value plus display name. |
| ProcessNode | ProcessNodeId, TransactionId, PID, PID generation/creation time, PPID, parent node, image, command line redaction, SID/session/token digest, Job membership, status, start/end, exit code. | Append lifecycle facts; terminal fields set once. | Process Manager creates from suspended root or notification; Service updates; Driver reads context mapping. | Durable process snapshot; sensitive args redacted in UI. |
| ProcessNodeId | UUID. | Immutable. | Process Manager. | UUID. |
| FileChange | FileChangeId, TransactionId, volume identity, canonical host path, old path, object type, operation, baseline fingerprint, overlay object ID, metadata/hash, source node, disposition, dependencies. | Immutable normalized Diff input; apply result elsewhere. | Overlay index/event reducer creates; Diff Engine normalizes; Commit reads. | Overlay metadata schema and Diff DTO. |
| FileChangeId | UUID. | Immutable. | Overlay reducer. | UUID. |
| RegistryChange | RegistryChangeId, TransactionId, SID, hive, view, key/value path, operation, baseline digest, overlay ref, source node, disposition, dependencies. | Immutable normalized input; apply result elsewhere. | Only approved Registry backend may create; otherwise feasibility data is not a production change. | Versioned Registry metadata and Diff DTO. |
| RegistryChangeId | UUID. | Immutable. | Registry reducer. | UUID. |
| Diff | DiffId, TransactionId, generation, builtAt, overlay event watermark, baseline generation, immutable items, status. | Immutable per generation; rebuild creates new DiffId/generation. | Diff Engine creates; Service persists/reads. | Durable snapshot and UI DTO. |
| DiffItem | ItemId, kind, ChangeId, operation, snapshots, selection state, policy disposition, conflict state, apply state, dependencies. | Selection creates plan; apply state belongs to plan result, not mutable source item. | Diff Engine creates; Commit Planner reads; UI may request selection. | Versioned Diff DTO. |
| Dependency | dependent item ID, prerequisite item ID, reason, required/optional flag. | Immutable for a Diff generation. | Diff Engine derives; Planner validates acyclic graph. | Included in Diff snapshot. |
| Conflict | Change/item ID, expected baseline, observed current fingerprint, kind, detection time, resolution. | Detection immutable; explicit resolution is a separate command record. | Conflict Detector creates; user may request Skip/Overwrite if allowed; Commit validates. | Diff/Commit result DTO; no secret content. |
| CommitPlan | OperationId, TransactionId, DiffId/generation, requested selection, expanded closure, ordered items, conflict decisions, owner SID, created time, plan hash. | Immutable once prepared. | Commit Planner creates; Commit Engine executes. | Durable plan record and audit. |
| RecoveryEntry | OperationId, TransactionId, item/change ID, operation, canonical target identity, previous/new fingerprints, backup/temp refs, phase, status, checksum, sequence. | Append-only state transitions Pending → Applied → Verified or Failed; unknown outcome represented explicitly as Unknown. | Recovery Journal appends; Recovery Manager reads/reconciles. | Durable append-only journal with schema version. |

Identity distinction: DiffItem uses a stable itemId derived/assigned for that Diff snapshot; FileChangeId and RegistryChangeId remain domain identities. OperationId identifies one command attempt and its idempotent result.

## 5. Interfaces

Interfaces live in Core/Application or narrow adapter contracts; do not create one interface per class by convention. The following are the minimum useful boundaries.

| Interface | Methods and results | Caller → implementation | Errors and execution |
| --- | --- | --- | --- |
| ITransactionManager | CreateAsync(CreateTransactionRequest) → TransactionSnapshot; StartAsync(TransactionId, OperationId) → snapshot; GetAsync; RequestQuiesceAsync; CompleteDiffAsync. | Protocol handlers → Service application. | DomainError, Conflict, DriverUnavailable, PersistenceFailure; async user mode, cancellation/deadline. |
| ITransactionRepository | Insert; Get; TryTransition(expectedVersion, newState, operationId); ListRecoverable. | Transaction Manager → durable Service adapter. | NotFound, VersionConflict, StorageFailure; async and transactional persistence. |
| IProcessManager | StartSuspendedAsync; AssociateAsync; ResumeAsync; GetTreeAsync; QuiesceAsync; TerminateAsync. | Transaction Manager → Windows process adapter. | LaunchFailed, AccessDenied, EscapeDetected, Timeout; async, process notifications delivered as events. |
| IDriverChannel | RegisterTransaction; AssociateProcess; UpdatePolicy; BeginQuiesce; EndTransaction; GetDiagnostics; SubscribeEvents. | Service → Filter Manager port adapter. | DriverUnavailable, ProtocolMismatch, QueueFull, Timeout; control calls async; callback path never invokes RPC. |
| IOverlayStore | CreateTransactionArea; ReadManifest; AppendEvent; PutObject; PutMetadata; AddTombstone; FlushWatermark; Cleanup. | Driver adapter for fast indexed records where supported, Service reducer for durable orchestration. | QuotaExceeded, Corrupt, AccessDenied, StorageFailure; bounded local I/O. Ownership of kernel-visible index must be decided in filesystem spike. |
| IFileOverlay | ResolveOpen; PrepareWrite/Cow; ApplyDelete; ApplyRename; QueryDirectorySnapshot; CloseHandle. | Minifilter fast path via kernel-local index; worker updates durable model. | Unsupported/Blocked/QueueFull/ObjectChanged; nonblocking callback, no Service RPC. |
| IRegistryOverlay | Open/Create/Query/Set/DeleteValue/DeleteKey/EnumerateKeys/EnumerateValues. | No production caller until REG-GATE-01 closes; then selected adapter. | PolicyDenied, Unsupported, Conflict, BackendUnavailable; no blocking callback to Service. |
| IDiffEngine | BuildAsync(TransactionId, event watermark) → Diff; Normalize(changes) → changes; dependencies and statuses. | Transaction Manager → Core/domain service. | EventGap, OverlayCorrupt, UnsupportedInput; async worker. |
| IConflictDetector | CheckAsync(DiffItem, current fingerprint) → ConflictResult. | Commit Planner → file/Registry adapters. | AccessDenied, ObjectChanged, ReadFailure; user-mode bounded operations. |
| ICommitEngine | PrepareAsync(plan); ExecuteAsync(plan); VerifyAsync(plan); FinalizeAsync(plan). | Transaction Manager → Commit project. | Conflict, PermissionDenied, ApplyFailure, JournalFailure, RecoveryRequired. User mode, each step journaled. |
| IRecoveryJournal | AppendAsync(entry); FlushAsync; ReadAsync(OperationId); CheckpointAsync. | Commit/Recovery → durable adapter. | JournalCorrupt, DiskFull, FlushFailure; serialized append writer. |
| IEventPublisher | PublishAsync(event envelope); ReplayFrom(sequence). | Service handlers/managers → Named Pipe broker. | SubscriberDisconnected, SlowConsumer; asynchronous bounded subscriber queues. |

Core contains policy and records, not Windows implementations. Adapter interfaces may use domain result types but protocol DTOs do not cross into Core.

## 6. Transaction Service Lifecycle

| Operation/transition | Initiator | Checks and work | Event | Failure behavior / idempotency |
| --- | --- | --- | --- | --- |
| CreateTransaction | UI command through pipe | Authenticate pipe caller; allocate TransactionId; persist Created before creating overlay resources; validate volume/Registry scope; create ACL-protected transaction area. | TransactionCreated | Same OperationId returns same result. Partial setup is compensated or left recoverable in Created/Failed; never orphan silently. |
| StartTransaction | UI command | Verify Created/version; register driver context/policy; create Job; create root suspended; associate process identity; resume only after all registrations ack. | TransactionStarting, ProcessAttached, TransactionStarted | Failure terminates suspended process, removes registrations, records Failed. Retry only with explicit new/recovery operation. |
| Running | Service/driver facts | Accept events for registered context; persist event sequence/watermark; process membership checked. | ProcessCreated/Exited, File/Registry event | Queue/driver loss produces Failed or Quiescing according to policy, never implicit host passthrough. |
| Quiescing | Stop command or root exit | Close child admission; request driver quiesce; wait Job empty; terminate per policy on deadline; drain event queue and flush overlay watermark. | QuiescingStarted, ProcessTerminated, QuiescingCompleted | Timeout is visible failure; no DiffReady while processes or callbacks are unaccounted. Stop command is idempotent by OperationId. |
| DiffReady | Internal transition | Build and persist immutable Diff after final watermark; run initial baseline check; store DiffId/generation. | DiffBuilt | Event gap/corruption means Failed; rebuild produces new generation/ID. |
| Commit/Selective Commit | UI command | Authenticate owner; require DiffReady and matching generation; compute closure; reject unresolved dependency conflicts; persist CommitPlan before Apply. | CommitStarted, item outcomes, CommitCompleted/Failed | OperationId unique; same ID replays result; another commit for terminal operation rejected. |
| Discard | UI command or explicit recovery action | Stop processes if needed; close handles; persist cleanup intent; remove overlay idempotently. | DiscardStarted/Completed | Cleanup failure leaves Failed and preserves enough metadata for retry. |
| Completed | Internal Finalize | Durable operation result, terminal per-item outcomes, overlay cleanup complete or explicit retained recovery artifact. | TransactionCompleted | Terminal state is immutable; subsequent Commit is InvalidState. |

### Service restart persistence

Persist before acknowledging externally visible transitions:

- transaction manifest: IDs, owner/session, root launch specification, scopes, state/version, timestamps, schema, component epochs;
- process tree: ProcessNodeId, PID plus creation identity, parent, Job identifier, status and exit code;
- overlay manifest/index: object IDs, host identity, tombstones, rename map, event watermark and queue loss marker;
- Diff snapshot and generation;
- command idempotency table: OperationId, request hash, expected version, phase and response;
- commit plans, recovery journal, backups and temporary-object references;
- audit/event sequence and driver/service epoch.

On service startup, enumerate nonterminal transactions. Verify store ACL and checksums; query Job/process identities and driver epoch; reconnect/re-register contexts; drain/replay events after last durable watermark. Never infer success from missing process alone. If driver mapping or event continuity cannot be proved, set Failed/RecoveryRequired, stop the tree if still controlled, and disable Commit. If a commit journal is active, reconcile each item against before/after fingerprints and mark Unknown when neither can be proven. Do not discard unknown data automatically.

## 7. IPC Contracts

### Common envelope

Two separate transports have separate schemas. All request/response/event messages have protocolVersion, requestId (events use eventId), transactionId when applicable, sentAtUtc, deadlineMs, payload and correlation ID. Responses include responseTo=requestId, result or structured error. Maximum message size and per-method payload limits are mandatory. JSON is acceptable for initial UI pipe only if strict length framing and size limits are implemented; Driver port uses fixed-layout versioned C-compatible messages.

Error envelope fields: code, category, severity, retryable, fatalTransaction, userMessageKey, diagnosticId, retryAfterMs. Raw paths/arguments/value data are redacted by policy.

### UI ↔ Service Named Pipe

Use one duplex message-mode Named Pipe with explicit envelope framing, per-client authenticated SID/session, SDDL restricted to intended interactive owner and service identity, impersonation only to establish caller identity, and no unauthenticated cross-user access. Pipe reconnect starts with protocol negotiation and GetTransaction snapshot; events replay after sequence where retained.

| Message | Request payload | Response |
| --- | --- | --- |
| CreateTransaction | launch target, args, working directory, selected volume scope, allowed Registry scope, client operation ID. | Transaction snapshot in Created. |
| StartTransaction | TransactionId, expected version, OperationId. | updated snapshot and root ProcessNodeId. |
| GetTransaction | TransactionId. | current versioned snapshot. |
| GetProcessTree | TransactionId, optional sequence cursor. | ProcessNode snapshot/list. |
| GetDiff | TransactionId, DiffId/generation optional. | Diff snapshot or not-ready/stale error. |
| SelectDiffItems | TransactionId, DiffId/generation, item IDs, conflict resolutions. | validated selection and dependency closure preview; no apply. |
| CommitTransaction | TransactionId, DiffId/generation, OperationId, selection optional. | CommitPlan ID/status and asynchronous completion events. |
| DiscardTransaction | TransactionId, OperationId, expected version. | accepted operation and completion status. |
| CancelOperation | TransactionId, target OperationId. | cancellation accepted only before irreversible phase; otherwise too-late result. |
| SubscribeEvents | TransactionId, last sequence, event filters. | subscription ID and replay/live stream. |

All long operations return accepted + operation ID, then publish events; requests have finite deadlines and cancellation. Slow UI clients have bounded event queues; overflow returns a gap marker and requires snapshot/replay. No Service worker blocks on a client.

### Driver ↔ Service Filter Manager Communication Port

Control messages use fixed-size headers: protocolVersion, messageSize, opcode, requestId, TransactionId, componentEpoch, sequence, flags. Variable payloads have explicit byte lengths and maximums. Messages are validated before allocation/copy. Driver-originated event buffers are bounded.

| Operation | Direction | Purpose/result |
| --- | --- | --- |
| Connect/Negotiate | Service → Driver | Protocol version, capabilities, driver epoch; reject incompatible protocol. |
| RegisterTransaction | Service → Driver | TransactionId, scope IDs, policy generation, overlay index reference/capability; ack before process resume. |
| AssociateProcess | Service → Driver | PID + creation identity, ProcessNodeId, SID/session/token digest, TransactionId; ack before resume/allow. |
| RemoveProcess | Service → Driver | Invalidate mapping on exit; idempotent ack. |
| UpdatePolicy | Service → Driver | Atomic generation replacement; stale generations rejected. |
| BeginQuiesce | Service → Driver | Stop admission and flush event watermark; response includes final sequence or loss marker. |
| EndTransaction | Service → Driver | Remove context only after handles and pending work are resolved. |
| OverlayEvent | Driver → Service | Nonblocking queued event with transaction/process IDs, sequence and operation. |
| QueryDiagnostics | Service → Driver | Queue depth, contexts, dropped event count, active callbacks and epoch. |

Driver performs registered-context lookup, allowlist checks, file namespace fast-path decisions, COW preparation supported by kernel-local storage/index, process mapping lookup, bounded event enqueue and enforcement. Service performs transaction state, durable metadata, policy compilation, process orchestration, Diff, conflicts and Commit. If a fast-path decision needs Service, the operation is not supported by that fast path; no blocking RPC is allowed from filesystem or Registry callback.

On disconnect: Driver marks channel down, stops accepting new transactions and applies per-operation fail-closed disposition for existing ones; it must not silently pass through a supported path. On reconnect, Service negotiates epoch, re-registers transaction/process/policy snapshot, then resumes only after ack. Bounded queue full sets persistent loss marker and fails/quiesces affected transaction; no waiting for capacity in callback.

IOCTL is not part of normal event/control path. Add it only after a documented Filter Manager port limitation and a separate ACL/version review.

## 8. Filesystem Driver Contract

The following is an architectural contract, not a driver implementation recipe. Minifilter callbacks must not wait on user-mode Service. Fast-path metadata/index must be available locally and consistent with the acknowledged policy generation.

| Operation | Intercept/lookup/COW/event | Blocking rule | Context and error behavior |
| --- | --- | --- | --- |
| Create/Open | Intercept create/open; canonicalize path; lookup overlay/tombstone; choose host or overlay object; COW before first modifying open; enqueue event for mutation. | No Service RPC. Local bounded work only; any storage wait must be a documented filesystem-safe operation with strict bounds and deadlock review. | Transaction context + per-handle object/generation. Unknown context outside scope follows explicit PASSTHROUGH; missing context inside registered scope is BLOCKED and audited. |
| Read | Resolve effective object at open; subsequent reads use same handle context; overlay-only object read from overlay. | No Service RPC. | Host/overlay object and generation captured in handle context; missing object/tombstone returns not found. |
| Write | Ensure COW before modifying host-backed object; write overlay object; update size/metadata generation; enqueue change event. | No Service RPC; queue enqueue cannot wait. Queue full marks transaction data-loss/fatal and blocks additional writes. | Per-file synchronization and transaction context; failure must not fall back to host write. |
| Cleanup | Record delete-on-close disposition, flush per-handle metadata/event, resolve share/delete semantics. | No RPC; bounded local flush. | Handle context; durable failure recorded and prevents DiffReady. |
| Close | Release per-handle and file contexts after final cleanup, decrement references. | No RPC. | Idempotent; no state may be deleted while other handles refer to it. |
| SetInformation | Intercept rename, disposition/delete, basic metadata and supported size changes; represent rename as one logical old/new mapping. | No Service RPC. | Validate same supported volume and policy; unsupported info classes receive UNSUPPORTED or BLOCKED, never accidental passthrough within supported scope. |
| DirectoryControl | Intercept QueryDirectory; merge host enumeration with overlay-created entries, tombstones and rename map; maintain restart/pattern/cursor state. | No Service RPC; merged index must be local. | Per-directory handle snapshot/generation. If merge/index is unavailable, fail closed and flag transaction; do not report host-only results as isolated namespace. |

### Required namespace outcomes

| Case | Required effective result |
| --- | --- |
| Create over existing host file | Open/create disposition is evaluated against effective namespace and Win32 create semantics; modifying open gets overlay copy; create-new fails if effective object exists. |
| Create overlay-only file | File and parent directory metadata become visible to Open and QueryDirectory in the same Transaction. |
| Open deleted file | Tombstone hides baseline and prior overlay object; new open returns not found. Existing handles retain documented Windows-like handle semantics. |
| Open old path after rename | Old path is hidden in new lookup; new path resolves to same logical overlay object. Existing handles remain bound to the opened object. |
| Directory enumeration | Merge host and overlay; suppress tombstones/old rename names; include new names; deduplicate by canonical identity; honor search pattern/restart semantics. |
| Rename | Source identity and destination effective-existence rules checked; one logical mapping and recovery unit; cross-volume rename is BLOCKED. |
| Multiple processes | Same Transaction shares one object identity and lock/version domain; writes serialize at object level; append/byte-range semantics and share modes are tested. |

Path identity uses volume identity + canonical relative name plus observed file identity where available. File ID alone is not stable across replacement; path alone is insufficient across rename/recreate. Store host path separately from overlay object path. Overlay physical paths use opaque object IDs. Overlay root and all aliases must be excluded from interception before normal policy lookup to prevent recursion.

Required overlay metadata: manifest schema/generation; transaction/volume ID; overlay object ID; canonical host path; optional host file ID; object type; operation; old/new names; baseline fingerprint; overlay fingerprint/content reference; tombstone/subtree flags; disposition; event watermark; open reference count or equivalent lifetime tracking.

Directory enumeration and cache/concurrency semantics are a pre-implementation design gate from the audit. Do not claim filesystem MVP complete until it passes FS-GATE-01 tests.

## 9. Overlay Storage

Proposed service-owned physical layout (not a user-visible mount):

~~~text
<store-root>/
  transactions/<TransactionId>/
    manifest.bin
    index/objects.bin
    index/directories.bin
    files/<opaque-object-id>
    registry/<opaque-object-id>
    events/<sequence-segment>
    journal/<OperationId>.log
    recovery/<checkpoint>
~~~

Store root ACL permits only Service identity and required SYSTEM/administrators recovery access. Per-transaction directory is non-user-browsable. A normal app process has no direct permission to overlay content. Commit access is performed by Service under explicit user identity rules.

HOST PATH is the canonical identity in the application namespace and conflict target. OVERLAY PATH is an internal opaque content location. No client-supplied host path is concatenated into the physical overlay path. Collision prevention uses UUID/object ID plus create-new semantics and manifest uniqueness constraints.

Tombstone records identify exact object or subtree, transaction generation, baseline identity and deletion operation. Rename records identify old path, new path and object ID; old name is a tombstone in the effective namespace. Directory index is required for enumeration merge.

Transaction cleanup: quiesce; verify no process/driver handles; persist cleanup intent; remove content and indexes by manifest; retain minimal audit/recovery record; mark terminal only after verified cleanup. Disk quota and orphan cleanup policy are required before broad testing. Cleanup failure retains manifest and sets Failed.

## 10. Registry Contract and Gate

### Conditional API

If REG-GATE-01 passes, the logical API is:

| Method | Input/result | Semantics |
| --- | --- | --- |
| Open | TransactionId, process identity, hive/view/path → virtual handle/result | Return effective key identity; mechanism must be proven. |
| Create | scope/key/options/security context → virtual handle/result | Overlay-only key with parent/inheritance semantics. |
| Query | virtual handle, value name/type/output capacity → type/required size/data/result | Overlay value, tombstone not-found, otherwise baseline. Query path must never call Service synchronously. |
| Set | virtual handle, value name/type/data → result | Persist overlay before reporting success; preserve first baseline fingerprint. |
| DeleteValue | handle/name → result | Tombstone and hide from query and enumeration. |
| DeleteKey | handle/path → result | Subtree semantics, child handles and dependency are explicit. |
| EnumerateKeys | handle/index/name buffer → result | Stable merged view including overlay children and excluding tombstones. |
| EnumerateValues | handle/index/name/type/data buffer → result | Stable merged view including overlay values and excluding tombstones. |

The service owns allowlist and durable policy; an approved backend must have a nonblocking callback/API path and local cache/index sufficient for Query and enumeration. Cache miss cannot block in kernel. If complete effective value and enumeration data are not locally available, operation is BLOCKED/UNSUPPORTED and the transaction is marked; it must not silently read/write host state as if virtualized.

HKCU is resolved against the Transaction owner SID, not the Service account hive. HKLM\\Software uses exact allowlist by canonical key path and separate 32/64 views. WOW64 default view follows process bitness; explicit view remains distinct. Deleted values/keys remain tombstoned across open handles according to documented handle snapshot semantics. Two transaction processes see shared transaction overlay with serialized updates.

REG-GATE-01 acceptance: prove Open/Create/Query/Set/Delete/Enum for a normal Win32 app in HKCU and both WOW64 views; host values stay unchanged before Commit; deleted entries disappear from query and enumeration; callbacks never wait for Service; restart and cache loss fail closed. If mechanism cannot satisfy this API set, Registry transparent overlay is removed from MVP through ADR before production implementation.

## 11. Process Model

Service creates the Job Object and configures kill-on-close/limits according to explicit product policy. Launcher creates root suspended. Sequence:

1. Persist Transaction Created/Starting and Job identity.
2. Create Job Object and register it with process monitor.
3. Create root suspended with intended token/session and no unintended inherited handles.
4. Assign root to Job; obtain PID plus creation identity and ProcessNodeId.
5. Register ProcessNode in repository and Driver mapping; await acknowledgements.
6. Resume root only after Transaction and Driver policy are active.
7. Process monitor observes creation notifications; child must be confirmed in Job and associated before allowed operations.
8. On stop/root exit, close child admission, request graceful close, wait until deadline, terminate remaining controlled Job members if policy allows, flush mappings/events, then build Diff.

Process creation notification and ParentPid are evidence, not sufficient identity alone. Association requires PID generation/creation time, Job membership and expected SID/session/token policy. Remove mapping on process exit before PID reuse.

| Scenario | Disposition | Rule |
| --- | --- | --- |
| Helper/child/grandchild in same Job and accepted SID/session/token policy | SUPPORTED | Same TransactionId after association ack. |
| Shell-launched child that remains in Job | SUPPORTED | Membership verified; parent chain logged. |
| Updater in Job, same identity | WARNING | Included in transaction; long-running behavior and external effects remain unsupported. |
| Detached/breakaway child | ESCAPE | Block breakaway at creation where possible; otherwise mark escape, quiesce/fail transaction, never claim its writes isolated. |
| Child with different SID/token/session | BLOCKED by default | Separate explicit policy/ADR required. |
| Windows service/driver/system process | UNSUPPORTED/BLOCKED | Outside MVP. |
| Process created before Transaction | UNSUPPORTED | Never attach retroactively based only on image/PPID. |

On process creation race, create notification must suspend/gate the child or the launch path must provide a handshake before user execution; if the OS mechanism cannot guarantee association before first user operation, that child creation is BLOCKED/ESCAPE. This must be experimentally validated on supported Windows versions.

## 12. Diff Engine

Pipeline:

~~~text
final overlay watermark
 -> verify event continuity and object store integrity
 -> canonicalize identities
 -> fold operations per logical object
 -> create FileChange/RegistryChange records
 -> derive dependency graph (dependent -> prerequisite)
 -> detect baseline conflicts and policy dispositions
 -> persist immutable DiffId + generation
 -> transition Transaction to DiffReady
~~~

Dependencies are directed from dependent item to prerequisite. Graph must be acyclic. Planner applies prerequisite-first (reverse topological order under this edge convention); deletes use dependent-first. Rename is one atomic logical item depending on source identity and destination parent/absence constraints.

| Event sequence | Final change |
| --- | --- |
| Create + Write | Create with final content/metadata. |
| Create + Delete | No user-visible item; remove unreachable overlay object after durable fold. |
| Modify + Modify | One Modify with final content and initial baseline fingerprint. |
| Rename + Modify | One Rename with final content/metadata at new path. |
| Rename + Delete | Delete original logical baseline object; no surviving new path. |
| Create + Rename | Create at final path; original temporary path omitted. |
| Rename + Rename | One Rename from first old path to last new path. |

Every item records source ProcessNodeId where reliable, otherwise an explicit unknown source. Lost event sequence, event queue overflow or ambiguous identity prevents DiffReady.

## 13. Selective Commit

Algorithm:

1. Accept requested item IDs bound to DiffId/generation and OperationId.
2. Validate IDs, item policy disposition and user authorization.
3. Compute prerequisite closure; show automatically added items in a preview.
4. Reject missing, unsupported, cyclic or conflicted prerequisites. A dependency may not be silently omitted.
5. Re-check current conflicts for the entire closure.
6. Build immutable CommitPlan with requested and required sets, item order, conflict decisions and plan hash.
7. Persist plan before Apply; execute per-item journal transitions.
8. Mark all unselected changes Skipped/Discarded only after selected plan reaches a terminal outcome and cleanup policy is known.

Dependency rules:

| Selected item | Required dependency |
| --- | --- |
| File under transaction-created parent directory | Parent directory create. |
| File rename | Source identity, old-path tombstone, destination parent and destination conflict check; rename itself indivisible. |
| Registry value create/set | Parent key create if absent in baseline/effective state. |
| Registry key delete | Complete selected subtree delete set; an unsupported child blocks parent deletion. |
| Registry key rename | Old key identity, new parent and all required child key/value moves. |
| Child item under deleted directory/key | Parent/subtree delete is ordered after child operations and cannot contradict a selected child keep. |

Conflict resolution Skip removes the conflicted item and all dependent items from closure or rejects the plan. Overwrite requires explicit user decision and a second identity/permission check. No implicit cascade is allowed.

## 14. Commit Engine

Global atomicity across filesystem and Registry is not claimed. Filesystem and Registry operations are individually journaled; the plan may finish partially and report RecoveryRequired/Partial. No fully automatic rollback is promised.

| Phase | Input | Output | Failure and recovery |
| --- | --- | --- | --- |
| Prepare | Transaction, Diff generation, owner, selection, OperationId. | Durable immutable CommitPlan and journal header. | Invalid state/stale generation: reject before side effects. Journal failure: no Apply. |
| Validate | Plan, dependencies, supported dispositions, paths/scopes, permissions. | Ordered validated item set. | Unsupported or missing dependency: reject or explicit Skip plan; no silent drop. |
| ConflictCheck | Baseline fingerprints and current objects. | Per-item conflict results. | Conflict: fail closed; explicit Skip/Overwrite resolution then re-plan. |
| Plan | Validated selected closure. | Per-namespace order and preconditions. | Cycle or contradictory selection: reject. |
| Apply | Immutable plan and journal. | Per-item Pending/Applied entries, backups/temp refs. | Stop on first failure by default; preserve already-applied facts; no blind retry. |
| Verify | Expected new fingerprint and target identity. | Verified item status. | Mismatch becomes Failed/Unknown and requires recovery decision. |
| Finalize | All selected items terminal, unselected disposition known. | Durable operation result and cleanup intent. | Cannot mark Completed until journal/result durable and cleanup criterion met. |

Filesystem and Registry order is not semantically interchangeable for dependencies within either namespace, but cross-namespace order has no guaranteed atomicity and no general dependency. MVP order: apply filesystem dependency closure first, then Registry closure, with a durable boundary and user-visible partial status between them. This deterministic order simplifies recovery; it does not imply cross-resource transactionality. If later an application-level dependency across namespaces is modeled, planner must add an explicit dependency and may choose another order.

File apply uses same-volume temp/backup where available, no-follow path resolution, current identity check immediately before replacement, content hash verification, and post-apply verification. If atomic replace is unsupported, item capability becomes BLOCKED or requires explicit risk acceptance in a later ADR.

Registry apply remains gated. If gate passes, user-mode apply uses owner SID context for HKCU and explicit privilege validation for HKLM allowlist, rechecks view/type/digest immediately before mutation, and journals each value/key operation.

## 15. Recovery Journal

RecoveryEntry fields:

| Field | Meaning |
| --- | --- |
| OperationId, TransactionId | Command and owning transaction. |
| ChangeId, ItemId | Source change and Diff item. |
| Operation, Target | Action and canonical target identity, never untrusted raw overlay path. |
| PreviousState, NewState | Fingerprints or existence/type/metadata snapshots. |
| BackupRef, TemporaryRef | Durable recovery objects, if available. |
| Phase, Sequence, SchemaVersion | Replay order and format. |
| Status | Pending, Applied, Verified, Failed, or Unknown. |
| Checksum | Detect torn/corrupt records. |

Journal append must be flushed before an externally visible irreversible step; applied/verified record is flushed after verifying the target. Unknown means recovery cannot infer whether the operation took effect; it is not retried automatically.

| Crash scenario | Recovery behavior |
| --- | --- |
| Service crash before Commit | Reload Transaction/overlay; validate event watermark, reconnect Driver and Job; if consistent return to DiffReady, otherwise Failed/RecoveryRequired. |
| Service crash during Commit | Load plan/journal; compare target with PreviousState and NewState; mark Verified if new state proven, retry only if previous state proven and operation is idempotent, otherwise Unknown and require operator action. |
| Service crash after Commit | If Finalize record and terminal result are durable, return prior result idempotently; if not, reconcile journal and do not report Completed prematurely. |
| Machine restart during Commit | Same reconciliation after boot; validate store/ACL/volume availability. Preserve backups and overlay until every entry is terminal. Never claim global rollback. |

Retry of Commit uses same OperationId to return/reconcile same plan. A new operation cannot commit a Transaction already Completed. Discard after partial apply removes only unapplied overlay data; it does not undo already applied host changes. UI must distinguish this from rollback.

## 16. Unified Error Model

| Category | Example codes | Severity/retry/fatal guidance |
| --- | --- | --- |
| Domain | InvalidState, VersionConflict, ScopeDenied | Error; retry only with refreshed state; invalid state is not transaction-fatal. |
| IPC | ProtocolMismatch, Timeout, Disconnected, MessageTooLarge, QueueOverflow | Warning/error; retryable only for idempotent OperationId; disconnect may make result unknown. |
| Driver | DriverUnavailable, ContextMissing, PolicyStale, EventGap, QueueFull | High; supported-scope loss is fatal to transaction and must fail closed. |
| Filesystem | PathOutsideScope, UnsupportedOperation, ObjectChanged, AccessDenied, StorageFull | Per-item or fatal; never fallback to host write inside supported scope. |
| Registry | RegistryGateClosed, PolicyDenied, ViewMismatch, ValueChanged | Registry gate closed blocks feature; no silent host fallback. |
| Process | LaunchFailed, AssociationFailed, EscapeDetected, QuiesceTimeout | Escape/association failure is transaction-fatal; timeout can be retried/terminated by policy. |
| Commit | DiffStale, ConflictDetected, ApplyFailed, VerifyFailed | Preserve journal; conflict blocks item/closure; apply failure may make operation partial. |
| Recovery | JournalCorrupt, UnknownOutcome, BackupMissing, RecoveryRequired | Critical; do not auto retry or cleanup; require explicit recovery action. |
| Security | CallerNotOwner, InvalidSid, PathTraversal, IntegrityMismatch | Security audit; reject; repeated attack patterns may terminate client/transaction. |

Common error record: code, category, severity, retryable, fatalTransaction, user-visible message key, diagnostic ID, operation/request ID, TransactionId, ChangeId if applicable, timestamp and redacted detail. Logging level: informational success, warning retryable issue, error operation failure, critical unknown/corrupt/security boundary failure.

## 17. Security Model

| Surface | Required rule |
| --- | --- |
| Service identity | Dedicated Windows Service identity with only required privileges; avoid LocalSystem unless a concrete kernel/commit requirement is documented. Separate privileged apply from user-visible UI. |
| UI identity | Interactive user token; UI is untrusted client and cannot choose another owner’s transaction. |
| Named Pipe ACL | Explicit SDDL for Service SID/SYSTEM and intended user SID/session; authenticate caller with impersonation/query token, then revert before unrelated work. |
| Driver port | Restrict communication port connect ACL to Service identity; validate every message size/version/opcode/epoch and transaction ownership. |
| Transaction ownership | Every command checks owner SID and permitted session; admin elevation alone does not silently transfer ownership. |
| Overlay permissions | Service-owned root; app token has no direct access; per-user/transaction separation. Ordinary user cannot read another user’s overlay. |
| Path traversal | Canonicalize final volume/path, reject escaping components and unauthorized reparse, open without following unexpected reparse targets. Never concatenate raw client strings into storage paths. |
| Impersonation | Use only around a specific target operation; revert in finally/RAII; never hold impersonation across async waits or IPC. |
| Handles | Minimize inherited handles; duplicate only to intended process; validate process identity and access mask. |
| Logs | Redact command-line secrets, Registry data and user file content; correlate by IDs and fingerprints. |

Acceptance test SEC-001: User A cannot enumerate/read User B transaction manifest, file content, Registry overlay or journal through filesystem ACL, pipe API or predictable IDs. Direct access to overlay as the ordinary app user is denied.

## 18. Logging and Observability

Use structured event logs with UTC timestamp, level, category, event name, service/driver epoch, TransactionId, ProcessNodeId, ChangeId, DiffId, OperationId/RequestId, sequence, duration, disposition and error code. Never log file content, raw Registry data, access tokens or unredacted arguments by default.

Categories: Transaction, Process, Filesystem, Registry, Driver, IPC, Diff, Commit, Recovery, Security. Metrics include queue depth/drops, callback latency, active transactions, overlay bytes, quiesce duration, Diff duration, commit item duration and recovery unknown count. Correlation IDs must cross UI → Service → Driver event → Diff → Commit.

## 19. Test Architecture

| Test layer | Project area | What it proves |
| --- | --- | --- |
| Unit | Core | State transitions, disposition, fold rules, dependency closure, error mapping. |
| Integration | Service/repository/pipe | Authentication, idempotency, persistence, reconnect and event replay. |
| Driver | Disposable test VM/machine with test-signed driver | Registration, callback policy, queue full, reconnect, unload/restart. |
| Filesystem | Isolated NTFS test volume | COW, host/overlay resolution, tombstone, rename, QueryDirectory merge, concurrency/cache. |
| Registry | Disposable user hive/test keys | Only after REG-GATE-01; HKCU, allowlist, WOW64 views and enumeration. |
| Process | Job/process harness | Suspended root attach, helper/shell, breakaway, token mismatch, PID reuse and quiesce. |
| Commit | Temporary target tree/test hive | Baseline conflicts, backup/replace, dependency order and partial apply. |
| Recovery | Fault injection | Crash at every journal boundary, restart and unknown outcome handling. |
| Stress | NTFS test volume and process swarm | Concurrent writes, directory scans, event pressure, quota and latency. |

Never run destructive tests on user data. Driver tests require an isolated disposable environment and signed test build; this is a test prerequisite, not a VM/Sandbox product architecture.

### Required scenarios

| Test | Scenario and pass condition |
| --- | --- |
| TEST-001 | Create overlay file → Discard; host unchanged and overlay removed. |
| TEST-002 | Modify host file → Commit; content matches overlay and journal is Verified. |
| TEST-003 | Delete host file → Discard; host file still exists. |
| TEST-004 | Delete host file → Commit; host file absent after identity/precondition check. |
| TEST-005 | Registry SetValue → Discard; baseline unchanged. Run only after REG-GATE-01 passes. |
| TEST-006 | Registry SetValue → Commit; new value visible in correct hive/view; gate required. |
| TEST-007 | Root starts child; child operation is associated with same TransactionId before first allowed I/O. |
| TEST-008 | Selective Commit one file; only selected file and explicit dependencies apply. |
| TEST-009 | Host conflict before Commit; item is blocked until Skip or explicit Overwrite. |
| TEST-010 | Service crash with active transaction; restart restores or marks RecoveryRequired without passthrough. |
| TEST-011 | Host-only + overlay-only directory entries enumerate as one merged view; tombstone suppresses host entry. |
| TEST-012 | Rename then open old/new path and enumerate parent; old hidden, new resolves to same logical object. |
| TEST-013 | Two processes write same transaction file; no lost event, duplicate object or host leakage. |
| TEST-014 | Driver disconnect and bounded queue overflow; supported operations do not silently pass through. |
| TEST-015 | Child breakaway/different token; disposition is ESCAPE/BLOCKED and transaction reports it. |
| TEST-016 | Crash after each Commit journal boundary; restart classifies every item Applied/Verified/Failed/Unknown correctly. |
| TEST-017 | 32-bit and 64-bit Registry view enumeration and query; no cross-view leakage. Gate required. |
| TEST-018 | User A attempts to read User B overlay/pipe transaction; access denied. |

## 20. Agent Ownership

Ownership means edit responsibility after interfaces and ADRs are approved. No agent changes a cross-owner contract without Lead approval and an ADR. Parallel work must not create duplicate source of truth.

| Role | May change | Must read | Implements | Must not change / dependencies forbidden |
| --- | --- | --- | --- | --- |
| Lead / Architect | docs/architecture*.md, ADRs, Core contracts after review. | All docs and audit. | Cross-component decisions, schemas, integration acceptance. | No unreviewed compatibility-breaking contract; do not add UI/Driver dependency to Core. |
| Filesystem Agent | Driver filesystem modules, filesystem tests, filesystem-overlay.md only via approved ADR. | architecture-audit, transaction, filesystem, diff, commit, protocol. | IFileOverlay adapter, FS fast-path/index and FS integration tests. | Must not change Core domain model without ADR; no Service RPC from callbacks; no WPF dependency. |
| Process Agent | Service process adapter and process tests; process-model.md by review. | audit, transaction, process, IPC, security. | IProcessManager, suspended launch, Job and PID generation mapping. | Must not change Driver policy schema alone; no UI dependency in Service. |
| Registry Agent | Registry feasibility project/tests and later approved backend. | audit, registry, transaction, diff, commit, security. | REG-GATE-01 evidence and only approved IRegistryOverlay backend. | Must not change Transaction model; no assertion callback-only is transparent; no passthrough on gate failure. |
| Transaction Agent | Core and Service transaction modules/tests. | All model docs, audit, IPC and recovery. | Transaction aggregate, state machine, repository, idempotency and orchestration. | No WPF/WDK dependency in Core; no hidden implementation of Registry semantics. |
| Desktop Agent | desktop/ UI tests and UI IPC client. | transaction, diff, commit, protocol, security. | Thin WPF client, view models, owner-safe commands and event replay. | Must not change Driver or call it directly; no canonical state in UI. |
| Security Agent | Security docs, threat tests, pipe/overlay ACL adapters with owners. | audit, process, transaction, IPC, commit. | SDDL review, identity/impersonation checks, isolation tests. | No policy broadening without ADR; no token/content logging. |
| QA / Reviewer Agent | tests/** and review comments/reports. | All docs and relevant source contracts. | Acceptance matrix, fault injection and traceability review. | QA Agent НЕ исправляет код без отдельного разрешения; no changes to production implementation. |

## 21. Implementation Phases

| Phase | Goal and changes | Dependencies | Exit criteria and tests | Main risk |
| --- | --- | --- | --- | --- |
| 0. Audit decisions | Approve audit; create architecture baseline and close dispositions, recovery transition, overlay placement ADR. Docs only. | Current docs/audit. | No unresolved contradictions in states, dispositions and dependency direction. | Premature coding over unstable contracts. |
| 1. Core contracts | Create solution/project skeleton, Core IDs/records/errors/state policy, protocol schema. | Phase 0. | Unit tests for legal transitions, immutable IDs, schema limits. | Contract churn. |
| 2. Durable Service core | Repository, operation idempotency, event log, restart scan, no Driver. | Phase 1. | Restart/replay and duplicate request tests. | Persistence/checkpoint mismatch. |
| 3. Process vertical slice | Suspended launcher, Job, process identity, association ack, quiesce. | Service core + security review. | TEST-007, escape/token/breakaway tests. | Child notification race. |
| 4. IPC contract | Named Pipe host/client; driver port schema and simulator/fake endpoint. | Phase 1; process/service identity. | Protocol version, reconnect, timeout, backpressure and owner ACL tests. | Driver/service epoch mismatch. |
| 5. FS feasibility spike | One NTFS test volume: open/create/COW, overlay-only, delete, rename, QueryDirectory merge. | Phase 0 contracts, isolated test setup. | FS-GATE-01 passes all namespace tests; measured callback/cache behavior. | Minifilter namespace and cache semantics. |
| 6. FS overlay MVP | Driver fast path, overlay index/store integration, event watermark and COW. | Phase 5 pass. | TEST-001..004, 011..014, queue fault tests. | Deadlocks, missed events, recursion. |
| 7. Diff | Normalization/folding/dependencies/conflicts over durable synthetic and real events. | Phase 2 and FS event contract. | All fold combinations, DAG and stale generation tests. | Incorrect identity/rename collapse. |
| 8. Commit/recovery | File commit plan, journal, verification, partial result and restart recovery. | Phase 7; security review. | TEST-002/004/008/009/016 fault injection. | TOCTOU and unknown outcomes. |
| 9. Registry feasibility gate | Prototype only the candidate mechanism; query/open/enum/WOW64 and no Service wait. | Audit and security review. | REG-GATE-01 pass or ADR revises MVP. | Current callback-only approach insufficient. |
| 10. Registry backend | Only if phase 9 passes and ADR-009 approves mechanism. | Phase 9 pass. | TEST-005/006/017, host unchanged before Commit. | API coverage and view/security mismatch. |
| 11. UI and Selective Commit | WPF thin client, snapshots, selection preview, operation/events/recovery UI. | Stable Protocol, Diff and Commit plan. | UI integration and owner isolation tests. | UI/state disagreement. |
| 12. Hardening | Integration/stress, crash/restart, signing/install docs, performance budgets. | Previous phases. | Acceptance suite, no silent passthrough, resource quotas, reviewed logs. | Driver stability and lab configuration. |

Каждая фаза оставляет работающий проверяемый результат; Registry production work не начинается до gate. WPF можно прототипировать только как pipe client после стабилизации протокола; он не должен блокировать доменные и driver feasibility spikes.

## 22. Architecture → Implementation Traceability

| Architecture requirement | Design component | Interface | Project | Test |
| --- | --- | --- | --- | --- |
| Service owns Transaction state | Transaction Manager + repository | ITransactionManager, ITransactionRepository | Core, Service | Unit state transitions; TEST-010 restart. |
| UI is client only | Pipe handlers and WPF client | UI-Service request/response/event envelope | Protocol, ServiceHost, UI | Owner ACL, reconnect and stale-version integration. |
| Launch root in one Transaction | Suspended launcher + Job association | IProcessManager, IDriverChannel.AssociateProcess | Service | TEST-007, process race test. |
| Child uses same context | Process monitor and PID generation map | IProcessManager events, AssociateProcess | Service, Driver | TEST-007, TEST-015. |
| File Create/Write isolated | Minifilter + file overlay index | IFileOverlay | Driver | TEST-001, TEST-002, TEST-013. |
| Delete/rename visible in namespace | Tombstone/rename map + directory index | IFileOverlay.QueryDirectorySnapshot | Driver | TEST-003, TEST-004, TEST-011, TEST-012. |
| Directory enumeration reflects effective view | QueryDirectory merger | IFileOverlay | Driver, Filesystem tests | TEST-011/012 + search/restart scan suite. |
| Unsupported operation has explicit outcome | Disposition policy | OperationDisposition | Core, Driver, Service | Queue full, unsupported path and no-passthrough tests. |
| Registry value isolation | Gated Registry interception backend | IRegistryOverlay | Registry | TEST-005/006/017 only after REG-GATE-01. |
| Diff built after quiesce | Diff builder and event watermark | IDiffEngine | Core, Service | Event gap, stale generation, fold rule unit tests. |
| Selective Commit includes dependencies | Commit Planner | CommitPlan, Dependency | Core, Commit | TEST-008 and dependency closure matrix. |
| Conflict fails closed | Conflict Detector | IConflictDetector | Commit | TEST-009. |
| Offline apply and verify | Commit Engine | ICommitEngine | Commit | Per-item apply/verify and partial failure tests. |
| Service crash recoverable | Journal/recovery manager | IRecoveryJournal | Commit, Service | TEST-010, TEST-016. |
| Cross-user overlay isolation | Store ACL + pipe identity | Security adapter, pipe auth | ServiceHost | TEST-018. |
| IPC no blocking callback | Bounded driver queue and async control plane | IDriverChannel | Driver, Service | TEST-014 and callback latency assertions. |

## Final Status

BLUEPRINT STATUS:

NEEDS CHANGES

ARCHITECTURE CHANGES REQUIRED:

- Create and approve architecture.md baseline; it is absent in the inspected repository.
- Close REG-GATE-01. Registry transparent overlay is not implementable as a production feature from the current callback-only specification; retain the scope only conditionally until an interception mechanism is proven or revise MVP by ADR.
- Specify filesystem QueryDirectory/effective namespace, cache, per-handle and concurrency rules before the driver implementation phase.
- Resolve transaction recovery transitions, operation dispositions, dependency direction, status dimensions and overlay placement as ADRs.
- Define exact IPC framing, request correlation, deadlines, queue-full and restart behavior before integration.

TOP IMPLEMENTATION RISKS:

1. Registry APIs do not have a proven per-process virtual result/handle mechanism; callback-only is insufficiently specified.
2. Minifilter namespace is incomplete until directory enumeration merges host/overlay entries and handles tombstones/rename consistently.
3. Process association must be acknowledged before root/child code can perform I/O; post-CreateProcess registration is too late.
4. Driver/service disconnection and queue overflow must never silently become passthrough for supported scope.
5. Commit has per-item partial outcomes and TOCTOU/crash windows; journal/recovery is resume/reconciliation, not global rollback.

FIRST IMPLEMENTATION TASK:

Documentation/feasibility task, not production code: approve architecture-audit.md, write architecture.md baseline and ADRs for dispositions/process launch/recovery/overlay placement, then run REG-GATE-01 and FS-GATE-01 design spikes in isolated test harnesses. Registry gate must be resolved before claiming the full MVP is implementation-ready.

