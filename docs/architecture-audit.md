# Architecture Audit

## 1. Executive Summary

Аудит выполнен по фактическому содержимому рабочего каталога. Найдены следующие документы:

- [transaction-model.md](transaction-model.md);
- [filesystem-overlay.md](filesystem-overlay.md);
- [registry-overlay.md](registry-overlay.md);
- [process-model.md](process-model.md);
- [diff-model.md](diff-model.md);
- [commit-model.md](commit-model.md).

Файл [architecture.md](architecture.md) отсутствует. Поэтому текущий audit сопоставляет модели между собой и не может проверить их соответствие единому верхнеуровневому baseline.

Архитектурное направление корректно: Windows Service владеет Transaction, WPF является клиентом, minifilter и registry callback выполняют kernel-side interception, состояние хранится в user mode, а Commit выполняется после Quiescing. Эти решения не требуют VM или Windows Sandbox.

Текущая документация ещё не является implementation-ready. Основные причины:

1. Прозрачная Registry virtualization через описанный только callback/cache механизм не доказана. Registry callbacks позволяют наблюдать и блокировать операции, но документы не задают поддержанный механизм подмены key handles, query results и enumeration для конкретного процесса.
2. Для файлового overlay не описан IRP_MN_QUERY_DIRECTORY и алгоритм слияния host/overlay directory entries. Без него приложение не увидит корректную effective namespace.
3. Регистрация PID после CreateProcess создаёт гонку: процесс может выполнить файловую или registry операцию до появления Transaction mapping.
4. Переход Failed → Committing противоречит правилу Commit only in DiffReady, а восстановление после падения Service/Driver не имеет детерминированного протокола.
5. IPC channels названы правильно, но не определены framing, request/response correlation, deadlines, backpressure, reconnect и поведение при переполнении очереди.

Итоговый статус: NEEDS CHANGES. Базовую архитектуру можно сохранить для Transaction Core, Process monitoring, Diff и offline Commit. Для файлового overlay нужны дополнительные kernel design details. Для Registry необходим отдельный feasibility gate и явное решение: добавить механизм user-mode interception/broker либо сузить/перенести прозрачную Registry virtualization.

## 2. Current Architecture

По текущим документам поток выглядит так:

~~~text
WPF UI
  -> ACL-protected Named Pipe
Windows Service
  -> Transaction state machine
  -> Process launcher + Job Object
  -> Diff builder + Commit Engine
  -> Filter Manager communication port
Kernel Driver / Minifilter / Registry callback
  -> overlay lookup, enforcement, bounded events
Overlay Store
  -> content, metadata, tombstones, events, journal
Diff
  -> Commit, Selective Commit или Discard
~~~

Каноническим источником состояния считается Service. Kernel mode должен принимать быстрые решения по уже загруженной policy и context; user mode должен хранить metadata, координировать процессы, строить Diff и применять изменения.

Границы MVP документированы как один локальный NTFS-том, regular files и каталоги, HKCU, ограниченный allowlist HKLM\\Software, один root process с дочерними процессами в Job Object и offline Commit. Сетевые пути, ReFS, removable volumes, hard links, ADS, сложные reparse points, memory-mapped writes, другие hives, Windows Services, drivers, boot settings, live commit, VM/Sandbox и distributed transactions указаны вне области.

## 3. Verified Decisions

Ниже перечислены решения, которые согласованно повторяются в моделях и могут быть зафиксированы как принятые ADR:

| Решение | Проверка | Статус |
| --- | --- | --- |
| Service owns Transaction | transaction-model, process-model и commit-model описывают Service как единственного владельца state. | Подтверждено на уровне архитектуры. |
| WPF is a client | UI не хранит каноническое состояние. | Подтверждено. |
| Minifilter and Service separation | Callback не должен ждать UI; user-mode выполняет Diff и Commit. | Подтверждено, но нужны fault rules. |
| One local NTFS volume | filesystem-overlay и commit-model ограничивают scope. | Подтверждено. |
| HKCU plus limited HKLM\\Software | registry-overlay и Transaction registryScope совпадают. | Подтверждено как область, но механизм ещё не доказан. |
| Offline Commit | Commit доступен после остановки дерева и DiffReady. | Подтверждено. |
| Diff-level Selective Commit | Выбор item и dependency closure описаны в diff/commit models. | Подтверждено, детали требуют нормализации. |
| Fail-closed conflicts | Last-writer-wins и automatic merge запрещены. | Подтверждено. |
| No VM/Sandbox foundation | Все документы исключают VM и Windows Sandbox. | Подтверждено. |

Эти решения фиксируют направление, но не заменяют технические контракты. Подтверждение решения не означает, что его текущая формулировка уже достаточна для реализации.

## 4. Contradictions

### C-001 — отсутствует верхнеуровневый architecture.md

Severity: HIGH

Problem: Ожидаемый документ architecture.md отсутствует, хотя шесть моделей ссылаются друг на друга и задают собственные границы.

Why it matters: Нельзя проверить единый component boundary, IPC contract и список гарантий. Разные модели могут эволюционировать независимо и расходиться.

Recommendation: Создать architecture.md как утверждённый baseline после принятия настоящего audit. До этого считать audit единственным местом фиксации обнаруженных расхождений.

MVP impact: Не блокирует отдельные domain prototypes, но блокирует уверенное утверждение, что архитектура согласована целиком.

### C-002 — policy states не сведены к единому контракту

Severity: HIGH

Problem: В исходном требовании обязательны SUPPORTED, UNSUPPORTED, BLOCKED и PASSTHROUGH. Текущие документы используют Unsupported, fail-closed и passthrough в разных местах, но не определяют enum, precedence или результат при queue/service/driver failure.

Why it matters: Неявный passthrough может нарушить изоляцию. Один компонент может пропустить операцию, а другой считать её частью Diff.

Recommendation: Ввести единый OperationDisposition и таблицу policy по file, directory, Registry, process и IPC failure. Для каждого случая определить, кто принимает решение и какое событие публикуется.

MVP impact: Критично для изоляции. До этого нельзя считать unsupported operation безопасной.

### C-003 — Failed → Committing противоречит DiffReady-only Commit

Severity: HIGH

Problem: transaction-model допускает переход Failed → Committing для recoverable retry, но его инвариант и commit-model требуют Commit только из DiffReady. Не определено, существует ли при этом валидный Diff и какая часть journal уже применена.

Why it matters: Service может принять Commit над неполным overlay или повторно применить уже записанный item. Это создаёт риск повреждения host state.

Recommendation: Разделить Transaction state и Recovery operation state. Обычный Commit разрешать только из DiffReady; recovery resume разрешать из Failed лишь после проверки journal, overlay и generation с отдельным operation mode.

MVP impact: Блокирует безопасное восстановление после сбоя Commit.

### C-004 — направление dependency graph различается

Severity: HIGH

Problem: diff-model описывает dependency как item → prerequisite, а Mermaid graph показывает ParentDir → File и RenameKey → ChildValue. Неясно, направлена ли стрелка от prerequisite к dependent или наоборот.

Why it matters: Topological sort для Selective Commit может применить дочерний item раньше родителя или удалить объект в неверном порядке.

Recommendation: Зафиксировать одно направление, например item → prerequisite, и отдельно определить порядок Apply и обратный порядок Delete. Добавить проверку acyclic graph.

MVP impact: Нужна до первой реализации Selective Commit.

### C-005 — status и decision flags смешаны в одной модели Diff item

Severity: MEDIUM

Problem: В diff-model Pending, Selected, Applicable, Conflicted и Applied перечислены как status, но selected и applicable одновременно описаны как отдельные boolean-поля. commit-model использует ещё skipped, failed и unknown journal states.

Why it matters: UI и Commit Engine не смогут однозначно понять, является ли item выбранным, разрешённым policy, конфликтным или уже применённым.

Recommendation: Разделить поля: selectionState, policyState, conflictState и applyState. Не использовать одно поле status для разных осей.

MVP impact: Средний для полного Commit, высокий для Selective Commit и recovery UI.

### C-006 — расположение overlayRoot относительно volumeScope не определено

Severity: HIGH

Problem: Transaction DTO содержит overlayRoot на D: и volumeScope в виде отдельного volume identity, при этом MVP ограничен одним локальным NTFS-том. Не сказано, должен ли overlay находиться на том же томе, может ли он находиться на другом, и какие atomicity guarantees тогда сохраняются.

Why it matters: Cross-volume copy не даёт atomic replace, меняет file identity и усложняет redirect, cleanup и recovery. Для minifilter также важны исключение собственного overlay из interception и доступность тома.

Recommendation: Явно выбрать same-volume или cross-volume overlay policy. Для MVP предпочтительно зафиксировать отдельный service-owned store с документированными cross-volume limits либо same-volume storage, если нужны атомарные операции. Проверить выбор на уровне Commit Engine.

MVP impact: Влияет на файловый COW и надёжность Commit.

## 5. Filesystem Overlay Audit

### 5.1 Проверка основного пути

Требуемая семантика должна быть такой:

~~~text
Application Create/Open
  -> minifilter obtains process context
  -> canonicalize path and volume
  -> check tombstone and overlay index
  -> select host object, overlay object or synthesized directory view
  -> return a handle whose subsequent I/O has stable per-handle context
Application Read/Write/Close
  -> operate on the selected object and update bounded event/journal
~~~

Текущая документация описывает lookup и copy-on-write концептуально, но не задаёт механизм, который обеспечивает эту семантику для всех IRP paths. Особенно отсутствуют directory control, per-handle state, cache invalidation и handle lifetime rules.

### 5.2 Операции и сценарии

| Сценарий | Что заявлено | Аудит |
| --- | --- | --- |
| File exists only in overlay | Новый объект должен быть виден приложению. | HIGH: нет алгоритма directory lookup/enumeration и правил для parent directory. |
| Host and overlay both exist | Read должен видеть overlay, baseline хранится для conflict check. | HIGH: не определено, как Create/Open выбирает объект и как поддерживаются share modes, oplocks и cached handles. |
| File deleted in transaction | Tombstone скрывает объект и старый open получает not found для новых opens. | HIGH: не определено поведение уже открытых handles, directory enumeration и recreation того же path. |
| File renamed | New path виден, old path скрыт. | HIGH: нет полной семантики open старого пути, rename через открытый handle, directory rename и concurrent opens. |
| Directory created only in transaction | Каталог должен участвовать в effective namespace. | HIGH: metadata directory описана, но нет обязательного synthetic directory index. |
| Directory enumeration | Приложение должно видеть host entries плюс новые overlay entries без удалённых и без дублей. | CRITICAL: IRP_MN_QUERY_DIRECTORY, restart scan, pattern matching, case handling и merge state отсутствуют. |
| Repeated opens | Каждый open должен получать согласованный effective object. | HIGH: нет per-file/per-handle context и generation rules. |
| Two processes use one file | Обе операции должны видеть одну transaction view и согласованный порядок writes. | HIGH: нет locking, versioning, append semantics, writer arbitration или event ordering. |

Для Create/Open/Read/Write/Delete/Rename отдельно не зафиксированы pre-operation/post-operation правила и допустимые fallback dispositions. Поэтому утверждение «приложение видит обычную Windows-систему» пока не доказано.

### 5.3 Directory enumeration — критический пробел

Для каждого directory handle нужен state, включающий canonical directory path, search pattern, requested information class, enumeration generation и уже выданные identities. Minifilter или отдельный namespace layer должен:

1. получить host entries;
2. получить overlay-created entries;
3. удалить entries, скрытые tombstone/whiteout;
4. заменить старое имя на новое при rename;
5. устранить дубли host/overlay по canonical identity;
6. сохранить порядок и semantics, достаточные для FindFirst/FindNext и restart scan.

Current docs не определяют, кто реализует этот merge: minifilter, user-mode service или предварительно построенный kernel cache. Синхронный вызов Service из QueryDirectory недопустим, поэтому всё необходимое для fast path должно быть локально доступно driver-у.

### 5.4 Copy-on-write, identity и path handling

Заявленного набора данных недостаточно для надёжного MVP без уточнений. Необходимы:

- canonical volume identity и путь, включая case policy, trailing dots/spaces, DOS/NT path, stream suffix и reparse policy;
- baseline file identity с volume serial и file ID, но не только path;
- отдельный immutable overlay object ID;
- per-handle context, который фиксирует выбранный object и generation;
- tombstone/whiteout для file и directory с правилами subtree hide;
- parent directory index для synthetic entries;
- event sequence, lock/version и durability state;
- исключение overlay root из собственного interception для всех эквивалентных path forms;
- правила для oplocks, Cc cache, section objects и rename invalidation.

Текущий текст упоминает copy-on-write, tombstone, volume identity и opaque object ID, но не фиксирует эти invariants в виде контракта.

### 5.5 Cache и concurrency

MVP допускает типичные synchronous и overlapped операции, но это не снимает риски Cache Manager. Обычный overlapped write может использовать cached I/O. Нужны решения по:

- кто является владельцем cached data и когда вычисляется hash;
- как инвалидируется cache после rename/delete;
- что происходит с oplock break и share violation;
- как сериализуются два writer-а одной Transaction;
- как событие считается durable до ответа приложению;
- что делает driver при bounded queue full.

Memory-mapped writes правильно вынесены за MVP, но должны получать BLOCKED или UNSUPPORTED disposition, а не неявный passthrough.

### 5.6 Оценка

Статус: HIGH RISK. File System Minifilter overlay реалистичен для ограниченного MVP, но текущего описания недостаточно. До реализации нужны namespace/enumeration spike и контракт cache/concurrency. Это design completion, а не необходимость VM или другой фундаментальной платформы.

## 6. Registry Overlay Audit

### 6.1 Главный feasibility issue

Текущий registry-overlay.md предполагает, что Registry callback получает transaction context, использует kernel cache и виртуализирует read/write. Документ не показывает supported mechanism, который для конкретного процесса заменяет:

- handle, возвращённый OpenKey/CreateKey;
- результат QueryValue;
- перечисление subkeys и values;
- semantics DeleteKey/RenameKey для уже открытых handles;
- WOW64 view и security checks в effective namespace.

Windows registry callbacks предоставляют pre/post notifications для наблюдения, policy и блокирования операций. Они не заменяют автоматически произвольный registry namespace на per-process overlay. Service не может синхронно отвечать из callback, а только bounded event/cache не даёт полного содержимого и enumeration semantics.

Severity: CRITICAL

Problem: Прозрачная Registry virtualization для HKCU и allowlisted HKLM\\Software не доказана текущим механизмом callback + service cache.

Why it matters: Можно получить только журналирование или блокирование, тогда как требование требует, чтобы приложение читало обычные пути и видело transaction-local values/keys. Неполная подмена приведёт к чтению host Registry или к несогласованным handles.

Recommendation: Провести отдельный feasibility spike до фиксации Registry MVP. Если callback-only не может обеспечить required semantics, выбрать одно из двух явно: добавить user-mode interception/broker для согласованного набора APIs либо ограничить MVP Registry наблюдением/блокированием и перенести transparent overlay. Не объявлять Registry ready на основании cache.

MVP impact: Текущий Registry overlay нельзя считать готовым к MVP. Это единственное место, где потребуется дополнительный interception mechanism или изменение границы функциональности.

### 6.2 Операции

| Операция | Текущее описание | Аудит |
| --- | --- | --- |
| OpenKey | Scope check и effective lookup подразумеваются. | HIGH: не определено, как заменить returned handle на overlay key. |
| CreateKey | Overlay key должен быть виден процессу. | HIGH: нет handle/object mapping и parent creation semantics. |
| QueryValue | Overlay value возвращается вместо host value. | CRITICAL: механизм подмены output buffer для всех API paths не зафиксирован. |
| SetValue | Сохраняется baseline и пишется overlay. | HIGH: capture/block возможны, но caller должен получить согласованный result и последующий read. |
| DeleteValue | Tombstone возвращает not found. | HIGH: нет enumeration и handle consistency. |
| DeleteKey | Скрывает subtree. | HIGH: нет subtree tombstone, child handles и inheritance rules. |
| RenameKey | Старый путь скрывается, новый виден. | CRITICAL: нет доказанного transparent key handle/enum mechanism. |
| EnumKey/EnumValue | Требуются effective entries. | CRITICAL: в документе отсутствует алгоритм merged enumeration. |

### 6.3 WOW64, inheritance и concurrency

Документ правильно различает 32-bit и 64-bit view, но не определяет:

- как определяется view для каждого API call;
- как ведут себя Default view и process bitness;
- как исключается смешивание одинаковых paths в двух views;
- как применяются security descriptor и inheritance при создании overlay key;
- что происходит при изменении host key другим процессом;
- как синхронизируются два transaction process, работающие с одним value;
- как идентифицируются и инвалидируются уже выданные handles.

### 6.4 Kernel/user-mode boundary

В kernel callback допустимы только быстрые операции: проверка process context, hive/view/path policy, lookup небольшого immutable cache, блокирование или redirect decision, enqueue bounded event. Нельзя ждать Service, читать произвольный overlay storage через IPC или выполнять тяжёлую сериализацию.

В Service должны находиться policy management, durable value storage, event folding, Diff, conflict check и Commit. Но этого разделения недостаточно для прозрачного Registry API surface: нужен конкретный механизм, который даёт процессу effective key/value semantics без synchronous Service call.

Статус: NOT FEASIBLE FOR MVP в текущей callback-only формулировке; HIGH RISK даже после добавления user-mode interception. Это не означает, что весь проект невозможен, но Registry MVP нельзя считать закрытым.

## 7. Process Model Audit

### 7.1 Race при запуске

Current process-model.md говорит, что Service регистрирует PID после CreateProcess, а окно до registration обозначает Starting. Сам процесс при обычном CreateProcess уже может исполнять user code и открыть файл или Registry key до завершения registration.

Severity: HIGH

Problem: PID → TransactionId mapping не гарантирован до первой операции процесса.

Why it matters: Первая запись может пройти host path, получить неверный policy disposition или потеряться из Diff. Это нарушает основную гарантию transaction context.

Recommendation: Запускать root suspended, создать и закрепить Job Object, зарегистрировать ProcessNode/PID generation в Service и Driver, подготовить overlay policy, затем resume. Для child creation нужен такой же race-free handshake через process notification и заранее готовую Transaction mapping.

MVP impact: Обязательное изменение launcher flow до файловой интеграции.

### 7.2 Сценарии процессов

| Сценарий | Рекомендуемый disposition | Текущее состояние |
| --- | --- | --- |
| Обычный helper, созданный root и оставшийся в Job Object | SUPPORTED | Направление описано, но race mapping не закрыт. |
| Child/grandchild в том же SID/session/token family | SUPPORTED | Job Object и notifications предусмотрены. |
| Updater без breakaway, тот же token | WARNING | Нужны правила для долгоживущего процесса и его файловых writes. |
| Shell-launched child, унаследовавший Job Object | SUPPORTED/WARNING | Нужно проверять реальное членство, а не только PPID. |
| Detached process вне Job Object | ESCAPE | Документ упоминает EscapeDetected, но нет обязательного disposition для его изменений. |
| CREATE_BREAKAWAY_FROM_JOB или другой Job Object | BLOCKED или ESCAPE | Не определено, когда создание запрещается и как фиксируется факт. |
| Другой user token или SID | BLOCKED по умолчанию | SID/session проверка упомянута, но policy не зафиксирована. |
| Elevation token того же пользователя | WARNING до отдельной проверки | Не определены integrity level, registry scope и access rules. |
| Windows Service, driver или system process | BLOCKED/OUT OF SCOPE | Соответствует границам, но disposition не оформлен. |

### 7.3 PID identity и restart

PID → TransactionId недостаточно из-за PID reuse и restart gap. Mapping должен включать ProcessNodeId, PID, process creation time или generation, owner SID/session/token digest и Job membership. После Service restart необходимо перечислить процессы Job Object, сверить creation identity и восстановить mapping; одного persisted PID недостаточно.

Падение Driver не имеет определённой реакции. Нужно выбрать: запретить новые операции и перевести Transaction в Failed, либо Quiesce и попытаться восстановить mapping. Молчаливый passthrough недопустим.

Статус: HIGH RISK; после suspended launch, identity mapping и failure policy модель Job Object пригодна для MVP.

## 8. IPC Audit

### 8.1 Выбор транспорта

Named Pipe для UI ↔ Service и Filter Manager Communication Port для Driver ↔ Service — подходящий выбор. IOCTL как дополнительный control channel также допустим, если operation не укладывается в communication port. Транспортный выбор можно считать READY; wire contract ещё не готов.

### 8.2 Недостающие обязательные поля и правила

| Область | Что должно быть зафиксировано | Текущее состояние |
| --- | --- | --- |
| Framing | Length prefix или message envelope, maximum size, encoding и checksum при необходимости. | Не определено. |
| Correlation | protocolVersion, requestId, responseTo/requestId, TransactionId и sequence. | TransactionId/version/sequence упомянуты; request/response IDs отсутствуют. |
| Deadlines | Per-request timeout, cancellation и absolute deadline. | Не определено. |
| Bounded queues | Размер, ownership, enqueue result и overflow disposition. | Bounded queue упомянут, политика переполнения отсутствует. |
| Disconnect | In-flight request result, reconnect handshake и stale connection invalidation. | Не определено. |
| Service restart | Driver reconnect, resubscribe, state snapshot и replay sequence. | Частично упомянуто только через recovery. |
| Driver restart | Re-register contexts, transaction epoch и fail-closed behavior. | Не определено. |
| Backpressure | Как не задерживать filesystem/registry callback и не терять durable fact. | Не определено. |
| Errors | Stable code, retryability, operator action и sensitive-data redaction. | Error codes есть в Transaction/Commit, но нет IPC envelope. |

### 8.3 Опасное ожидание

Документы правильно запрещают ждать UI и Service внутри callback. Это правило необходимо усилить: callback не должен выполнять бесконечный wait, synchronously call Service, ждать свободную очередь без deadline или переходить в passthrough при переполнении. При queue full допустимы только заранее определённые dispositions: BLOCKED, UNSUPPORTED или перевод Transaction в Failed/Quiescing. Операция не должна молча терять событие.

Статус: NEEDS DESIGN CHANGE для протокола; transport selection READY.

## 9. Transaction State Machine Audit

### 9.1 Проверенные состояния

Канонический набор состояний присутствует: Created, Starting, Running, Quiescing, DiffReady, Committing, Discarding, Completed, Failed, Aborted.

Базовые переходы Created → Starting → Running → Quiescing → DiffReady → Committing/Discarding → Completed описаны однозначно для штатного пути. Starting, Quiescing, Commit и cleanup имеют диагностические Failed branches.

### 9.2 Проблемы переходов

Severity: HIGH

Problem: Failed → Committing разрешён в state diagram, хотя Commit должен начинаться только из DiffReady.

Why it matters: Неясно, является ли это обычным Commit, resume journal или повторным применением. State machine не различает эти режимы.

Recommendation: Оставить обычный Commit только DiffReady → Committing. Recovery operation хранить отдельно и разрешать только при проверенном journal, overlay generation и operation record. При невозможности доказать состояние выбирать Discard или Aborted, не blind Commit.

MVP impact: Блокирует recovery correctness.

Severity: HIGH

Problem: Не зафиксировано, когда создаётся TransactionId относительно overlay creation, driver registration и root launch.

Why it matters: При ошибке между этими шагами Service не знает, кто владеет orphan overlay и какие операции разрешены.

Recommendation: Создавать TransactionId и durable Created record до overlay/driver setup. Каждый следующий шаг иметь compensating action и operation record.

MVP impact: Нужен до launcher и restart tests.

Severity: HIGH

Problem: Падение Service или Driver не имеет единой state transition policy для Created, Starting, Running, Quiescing и Committing.

Why it matters: Возможен тихий passthrough, потеря mapping или удаление overlay при неизвестном результате.

Recommendation: Определить component epoch, heartbeat/reconnect и fail-closed matrix. При неизвестном состоянии не считать Transaction Completed.

MVP impact: Обязателен для заявленного service restart/recovery.

Severity: MEDIUM

Problem: Идемпотентность заявлена, но нет durable schema operationId → terminal result → affected items.

Why it matters: Повтор команды после disconnect может повторить Commit или очистку.

Recommendation: Хранить request/operation ID, expected state, generation, command hash, phase и terminal result.

MVP impact: Требуется для UI reconnect и retry.

### 9.3 Завершение

Transaction считается Completed только после durable Finalize и подтверждённого terminal cleanup. Документ не определяет, допустим ли Completed при оставшихся Unsupported/Conflicted items. Это должно быть отдельным итогом: CompletedWithSkipped, Failed или Completed с audit result, но не скрытым статусом.

## 10. Diff Audit

Сильные стороны: Diff строится после Quiescing, использует DiffId, TransactionId, FileChangeId, RegistryChangeId и ProcessNodeId, предусматривает baseline/current/overlay snapshots, conflict state и dependency closure.

### Проблемы

Severity: HIGH

Problem: Направление dependency graph противоречиво между текстом и Mermaid diagram.

Why it matters: Неверная topological order ломает parent-before-child creation и child-before-parent deletion.

Recommendation: Зафиксировать направление item → prerequisite, правила циклов и два порядка обхода: Apply и Delete.

MVP impact: Нужен до Selective Commit.

Severity: MEDIUM

Problem: Selection, policy applicability, conflict и apply result смешаны в status list.

Why it matters: Нельзя надёжно вычислить Selected ∪ Required или отобразить пользователю причину пропуска.

Recommendation: Разделить selectionState, policyDisposition, conflictState и applyState; хранить immutable input snapshots и mutable apply result отдельно.

MVP impact: Нужен до UI и commit plan.

Severity: MEDIUM

Problem: Не определено, что означает выбор каталога, Registry key с children и rename subtree.

Why it matters: Выбор может неожиданно включить или исключить дочерние entries.

Recommendation: Для каждого item kind определить selection closure: directory create, directory delete, key delete, key rename, old/new paths и child values.

MVP impact: Влияет на предсказуемость Selective Commit.

Severity: MEDIUM

Problem: Diff Stale при новых events упомянут, но не определено, кто увеличивает generation и как отменяется уже выбранный Commit plan.

Why it matters: Старый plan может примениться к новому overlay или baseline.

Recommendation: Включать generation в every request, selection и journal precondition; при mismatch возвращать DiffStale без Apply.

## 11. Commit / Recovery Audit

### 11.1 Что спроектировано корректно

Prepare → Validate → ConflictCheck → Apply → Finalize и отдельный Recovery flow подходят для offline engine. Документ прямо не обещает полноценный rollback: rollback указан как best-effort для backup операций, а глобальная atomicity file+Registry исключена. Это корректное ограничение.

### 11.2 Проблемы

Severity: HIGH

Problem: Atomic replace и backup описаны как «где возможно», но не определены capability matrix и результат при cross-volume или reparse-sensitive path.

Why it matters: Commit может заменить не тот object или оставить частично применённый host state.

Recommendation: Для каждого file operation хранить precondition (volume, file ID, size, timestamps, hash policy), apply capability и recovery action. Открывать target с no-follow-reparse policy и повторно проверять identity перед replace/delete.

MVP impact: Обязателен для безопасного file Commit.

Severity: HIGH

Problem: Baseline ConflictCheck отделён от Apply, поэтому host может измениться между проверкой и записью.

Why it matters: Есть TOCTOU окно даже при fail-closed policy.

Recommendation: Использовать открытый handle/identity validation непосредственно перед операцией, минимизировать окно, повторять precondition после write и записывать unknown при неопределённом результате.

MVP impact: Нужен для любой фиксации существующего файла или Registry value.

Severity: HIGH

Problem: Journal упомянут, но не определены durable commit boundary, checksum/replay format, fsync/flush requirement и crash points.

Why it matters: После падения Service нельзя отличить not applied, applied и partially written.

Recommendation: Определить append record, atomic checkpoint, operation/item IDs, before/after fingerprints и правила unknown. Тестировать crash после каждого externally visible step.

MVP impact: Recovery иначе будет только предположением.

Severity: HIGH

Problem: Registry application и file application не атомарны между собой, а partial failure semantics для Selective Commit не формализованы.

Why it matters: Пользователь может получить часть выбранного набора и не понять, какие dependencies остались в overlay.

Recommendation: Commit plan должен иметь per-item terminal result, plan result и explicit Partial/RecoveryRequired status. Overlay cleanup разрешать только после terminal state каждого item.

MVP impact: Требуется до первого destructive Commit test.

Severity: MEDIUM

Problem: Повторный Commit после Failed разрешён концептуально, но не указан порядок проверки уже Applied items и конфликтов с их текущим состоянием.

Why it matters: Blind retry может повторно удалить или перезаписать объект.

Recommendation: Resume только по journal precondition; неизвестный item требует operator decision, а не автоматического retry.

## 12. Security Risks

### S-001 — обход пути и reparse

Severity: HIGH

Problem: Path normalization описана общо, но no-follow semantics, volume aliases, junctions/reparse и final component handling не закреплены во всех Commit paths.

Why it matters: Overlay или Commit могут выйти за scope или изменить неожиданный host object.

Recommendation: Единый canonical path resolver для Driver и Commit Engine, проверка volume/file identity, отдельная policy для reparse и no-follow open.

MVP impact: Блокирует безопасный путь к одному NTFS-том.

### S-002 — ACL и impersonation

Severity: HIGH

Problem: Named Pipe и overlay ACL упомянуты, но не определены impersonation boundary, service account, same-user elevated client и права на HKLM Commit.

Why it matters: Пользователь может выбрать чужой Transaction или Service может применить изменение с неверным token.

Recommendation: Зафиксировать pipe SDDL, owner SID check, session/integrity check, impersonation only around target API, Service-owned overlay ACL и audit identity.

MVP impact: Нужен до UI/Service integration.

### S-003 — PID reuse/context spoofing

Severity: HIGH

Problem: PID mapping упомянуто, но не закреплены creation time/generation и invalidation после exit.

Why it matters: Новый процесс может унаследовать старый PID mapping и получить чужой Transaction context.

Recommendation: Использовать ProcessNodeId, PID generation/creation timestamp, process handle validation и remove-on-exit.

MVP impact: Нужен до interception tests.

### S-004 — silent passthrough

Severity: CRITICAL

Problem: При callback, queue, Service или Driver failure отсутствует единая disposition matrix.

Why it matters: Молчаливый passthrough превращает заявленную изоляцию в частичную и неочевидную.

Recommendation: Ввести SUPPORTED/UNSUPPORTED/BLOCKED/PASSTHROUGH с audit event, default fail-closed для supported scope и явным пользовательским предупреждением для passthrough.

MVP impact: Критично для доверия к продукту.

### S-005 — данные Registry и command line

Severity: MEDIUM

Problem: Redaction упомянут, но правила для binary values, command line, hashes и диагностических dumps не определены.

Why it matters: Audit и recovery logs могут раскрыть секретные настройки или аргументы.

Recommendation: Определить redaction policy и TTL для чувствительных данных; хранить только digest/content reference в UI.

MVP impact: Нужен до production-like demo.

### S-006 — driver trust и installation

Severity: HIGH

Problem: Не описаны signing, test mode, service-driver version compatibility и rollback при unload/update.

Why it matters: Неподходящий driver может нарушить I/O или оставить active context.

Recommendation: Зафиксировать test signing assumptions для ВКР и отдельные production constraints; проверять protocol/driver epoch.

MVP impact: Не блокирует лабораторный spike, но блокирует безопасную установку.

## 13. Performance Risks

| Риск | Причина | Что измерять |
| --- | --- | --- |
| File I/O overhead | Lookup, COW, metadata и event enqueue на каждом open/write. | p50/p95/p99 latency, throughput, CPU per MB. |
| Large file COW | Полное копирование при первом write. | First-write latency, temporary disk, cancellation. |
| Directory enumeration | Merge host и overlay, dedup и pattern matching. | Entries/sec, memory per open directory, repeated scans. |
| Hashing | Baseline/overlay SHA-256 может блокировать Diff/Commit. | Hash throughput и peak memory. |
| Registry callbacks | Context/cache lookup и high-frequency queries. | Callback latency, queue depth, rejected operations. |
| Queue backpressure | Driver cannot wait Service, events may accumulate. | Queue occupancy, overflow count, fail-closed rate. |
| Commit | Backup, replace, Registry API и journal flush. | Items/sec, recovery time, disk amplification. |
| Process monitoring | Notifications, Job Object and restart rebuild. | Attach latency and missed-event rate. |

До реализации должны быть заданы измеримые MVP budgets: максимально допустимый callback latency, queue capacity, overlay disk quota и timeout quiesce/commit. Сейчас таких budgets нет.

## 14. MVP Feasibility

| Область | Вердикт | Обоснование |
| --- | --- | --- |
| Service-owned Transaction Core | READY | State/data model есть, но recovery transitions требуют уточнения. |
| WPF as Service client | READY | Каноническое состояние остаётся в Service. |
| Named Pipe / Filter Manager transport | READY for transport | Нужен полный protocol/fault contract. |
| File System Minifilter overlay | HIGH RISK | Реализуемо для узкого scope, но отсутствуют enumeration, handle, cache и concurrency design. |
| Registry transparent overlay as currently described | NOT FEASIBLE FOR MVP | Callback + cache не доказывают подмену handles/query/enumeration. Нужен механизм или изменение scope. |
| Process → Transaction association | HIGH RISK | Job Object полезен, но нужен suspended launch и race-free PID generation mapping. |
| Diff engine | NEEDS DESIGN CHANGE | Dependency direction и status dimensions противоречивы. |
| Offline Commit | HIGH RISK | Возможен в user mode, но нужны TOCTOU controls, capability matrix и durable journal. |
| Service restart/recovery | NEEDS DESIGN CHANGE | Нет детерминированной policy для Service/Driver crash и reconnect. |
| Full stated MVP without changes | NEEDS DESIGN CHANGE | Core feasible, Registry and filesystem details не закрыты. |

Главный вопрос «можно ли создать рабочий MVP без изменения фундаментальной архитектуры?» имеет ответ: не в текущей спецификации. Фундамент Service + minifilter + user-mode Commit сохраняется, но Registry interception mechanism, process launch handshake, filesystem namespace contract и recovery protocol должны быть уточнены или ограничены.

## 15. Required Design Changes

Ниже обязательные изменения проектирования. Они не применяются автоматически к остальным документам этим audit-ом.

1. **Architecture baseline.** Создать architecture.md после принятия настоящего отчёта и связать component boundaries, IPC и guarantees в одном документе.
2. **Disposition contract.** Зафиксировать SUPPORTED, UNSUPPORTED, BLOCKED, PASSTHROUGH, precedence и default для каждого failure path. Для supported scope default должен быть fail-closed.
3. **Transaction/recovery state.** Разделить обычный Transaction state и Recovery operation state; убрать неоднозначный Failed → Committing или формально определить journal-resume transition.
4. **Creation ordering.** Ввести suspended root launch: CreateProcess suspended → Job assignment → ProcessNode/PID generation registration → Driver policy registration → resume. Определить аналогичный child notification handshake.
5. **Filesystem namespace contract.** Спроектировать Create/Open/Read/Write/Delete/Rename и QueryDirectory, включая host/overlay merge, tombstone subtree, old-path behavior, per-handle context, cache invalidation и concurrency.
6. **Overlay placement.** Принять ADR о same-volume/cross-volume overlayRoot и его atomicity/cleanup implications.
7. **Registry feasibility gate.** До основной реализации провести spike OpenKey/CreateKey/QueryValue/Enum/Delete/Rename для HKCU и двух WOW64 views. Если callback-only не подходит, выбрать user-mode interception/broker или исключить transparent Registry overlay из MVP.
8. **IPC envelope.** Определить framing, protocolVersion, requestId, responseTo, TransactionId, sequence, deadline, cancellation, bounded queue behavior, disconnect/reconnect и component epoch.
9. **Diff contract.** Нормализовать dependency direction, item status axes, directory/key selection closure и generation checks.
10. **Commit journal.** Определить durable record/checkpoint, precondition fingerprints, crash points, unknown state, partial result и cleanup gate. Не обещать rollback сверх реально сохранённых backups.
11. **Security contract.** Зафиксировать SDDL, impersonation, canonical path/no-follow policy, PID identity, overlay ACL, redaction и driver version/signing assumptions.
12. **Acceptance matrix.** Добавить integration scenarios для all four dispositions, file enumeration/rename, registry enumeration/WOW64, child process escape, service/driver restart и crash-at-each-commit-step.

## 16. Recommended Implementation Order

Предлагаемый порядок отличается от исходного тем, что feasibility spikes и failure contracts выполняются до тяжёлой интеграции driver/UI:

1. Утвердить настоящий audit, architecture baseline и ADR-008–ADR-012.
2. Зафиксировать domain schemas: Transaction, ProcessNode, FileChange, RegistryChange, Diff item, dispositions и error envelope.
3. Реализовать только в user mode state machine, operation idempotency и durable metadata/recovery records.
4. Зафиксировать IPC protocol и fault-injection contract; проверить reconnect, timeout, queue full и restart без driver/UI.
5. Реализовать suspended launcher, Job Object, process identity mapping и policy classification для helper/shell/breakaway/token scenarios.
6. Провести filesystem feasibility spike на одном NTFS-томе: host-only, overlay-only, COW, delete/tombstone, rename, directory create и QueryDirectory merge.
7. Реализовать минимальный overlay metadata/index и driver↔Service event path после успешного spike; отдельно измерить cache/overlapped behavior.
8. Реализовать Diff builder на synthetic events, dependency graph и generation invalidation.
9. Реализовать Commit Engine на synthetic Diff: baseline preconditions, backups, journal, partial failure и crash recovery.
10. Провести Registry feasibility spike для callback capabilities, key/value enumeration, deleted entries и 32/64 views.
11. Только при положительном результате Registry spike добавить выбранный interception mechanism; иначе зафиксировать revised MVP boundary.
12. Соединить process association, filesystem overlay и Diff в один vertical slice без WPF.
13. Подключить WPF как thin client через уже стабилизированный Named Pipe protocol.
14. Добавить Selective Commit, recovery UI и audit export.
15. Выполнить integration tests: service restart, driver disconnect/reconnect, process escape, host conflict, queue overflow и crash points.
16. Выполнить stress tests: concurrent writers, large files, directory enumeration, high event rate, registry enumeration and cleanup quota.

Ключевой порядок: сначала доказать registry и filesystem mechanisms на малых spikes, затем расширять Service и UI. Это снижает риск написать большую user-mode модель поверх неподдержанного interception behavior.

## 17. Architecture Decision Records

### ADR-001 — Windows Service owns Transaction

Статус: ACCEPTED.

Service является единственным владельцем state, policy, lifecycle, Diff и Commit. UI и driver не являются каноническим источником состояния.

### ADR-002 — One local NTFS volume

Статус: ACCEPTED.

MVP ограничен одним локальным NTFS-том. Сетевые пути, ReFS и removable volumes исключены.

### ADR-003 — HKCU plus limited HKLM\\Software

Статус: ACCEPTED AS SCOPE; MECHANISM PENDING.

Allowlist Registry scope принят, но прозрачный interception mechanism должен быть подтверждён отдельным feasibility spike.

### ADR-004 — Offline Commit

Статус: ACCEPTED.

Commit, Discard и Selective Commit доступны после завершения дерева, Quiescing и построения Diff.

### ADR-005 — Diff-level Selective Commit

Статус: ACCEPTED; DEPENDENCY SEMANTICS PENDING.

Пользователь выбирает логические Diff items, а Service вычисляет dependency closure. Направление графа и selection closure необходимо формально закрепить.

### ADR-006 — Fail-closed conflict policy

Статус: ACCEPTED.

Конфликт не приводит к silent overwrite. Разрешены Skip или явное Overwrite после повторной проверки; automatic merge и last-writer-wins запрещены.

### ADR-007 — Minifilter plus user-mode Service separation

Статус: ACCEPTED.

Kernel mode отвечает за interception/enforcement и bounded fast path; Service отвечает за state, policy, coordination, Diff и Commit. Callback не ждёт Service.

### ADR-008 — Unified operation dispositions

Статус: REQUIRED.

Ввести SUPPORTED, UNSUPPORTED, BLOCKED и PASSTHROUGH как единый policy contract с fail-closed default и audit event.

### ADR-009 — Registry virtualization mechanism

Статус: REQUIRED DECISION.

До реализации выбрать callback-compatible supported mechanism, user-mode interception/broker или пересмотр Registry MVP. Callback-only описания недостаточно.

### ADR-010 — Race-free process attach

Статус: PROPOSED.

Root запускается suspended; Job, ProcessNode, PID generation и Driver context регистрируются до resume. Breakaway и token changes получают явную policy.

### ADR-011 — Recovery is resume/partial recovery, not global rollback

Статус: PROPOSED.

Система гарантирует durable journal, проверяемый resume и best-effort backup recovery. Глобальная атомарность file+Registry и полноценный rollback не обещаются.

### ADR-012 — Diff status axes and dependency direction

Статус: REQUIRED.

Разделить selection, policy, conflict и apply states; зафиксировать dependency item → prerequisite и отдельные orders для Apply/Delete.

## Final Summary

ARCHITECTURE STATUS:

NEEDS CHANGES

TOP 5 RISKS:

1. Прозрачная Registry virtualization через текущий callback-only механизм не доказана и сейчас не готова для MVP.
2. Directory enumeration и полная effective namespace семантика файлового minifilter overlay не спроектированы.
3. PID mapping создаётся после запуска процесса, что допускает операции до присоединения к Transaction.
4. State/recovery model допускает противоречивый Failed → Committing и не определяет Driver/Service crash protocol.
5. Commit journal и baseline validation не закрывают TOCTOU, crash points и partial application на уровне implementation contract.

NEXT STEP:

Утвердить этот audit как входной документ, затем провести два коротких feasibility spike до написания production code: filesystem namespace spike с QueryDirectory/rename/tombstone и Registry spike с Query/Enum/WOW64 semantics. После их результатов принять ADR-009 и обновить architecture baseline.

