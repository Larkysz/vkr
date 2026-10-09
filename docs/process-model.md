# Модель процессов и transaction context

## Связанные модели

- [transaction-model.md](transaction-model.md) — состояние сессии.
- [filesystem-overlay.md](filesystem-overlay.md) — источник FileChange.
- [registry-overlay.md](registry-overlay.md) — источник RegistryChange.
- [diff-model.md](diff-model.md) — группировка по процессу.

## Цель

Process Tree связывает root application и его потомков с одним TransactionId. Windows Service создаёт Job Object, запускает root через launcher и регистрирует PID mapping до пользовательской работы. Job Object обеспечивает группировку и наблюдение, но не виртуализирует ресурсы.

## ProcessNode

| Поле | Описание |
| --- | --- |
| processNodeId | UUID, стабильный в рамках Transaction. |
| transactionId | Владелец context. |
| pid, parentPid | Текущие PID/PPID; PID не является идентичностью. |
| imagePath, commandLine | Канонический image и аргументы с redaction в UI. |
| sessionId, ownerSid, tokenDigest | Проверка сессии и владельца без хранения секрета. |
| startedAt, exitedAt, exitCode | Lifecycle процесса. |
| status | Starting, Running, Exited, EscapeDetected, Terminated или Unknown. |
| jobMembership | Подтверждение членства в Job Object. |
| parentNodeId | Логическая связь с предком. |
| fileChangeIds, registryChangeIds | Ссылки на замеченные изменения. |

## Запуск и наследование

Launcher создаёт Job Object, назначает root process и передаёт service-issued launch context. После CreateProcess сервис регистрирует PID → TransactionId и ProcessNodeId. Окно до registration имеет состояние Starting и не принимает пользовательские операции.

Дочерний процесс обнаруживается через kernel process notification, PPID и Job Object notification. Context наследуется по членству и проверке SID/session; совпадение имени или PPID недостаточно. Detached процесс получает EscapeDetected.

~~~mermaid
graph TD
    T[TransactionId]
    T --> R[Root ProcessNode]
    R --> A[Child: editor helper]
    R --> B[Child: updater]
    A --> C[Grandchild: converter]
    B -.-> E[EscapeDetected]
~~~

## События и контроль

Сервис обрабатывает ProcessCreated, ProcessAttached, ProcessExited, JobMembershipChanged, ProcessEscapeDetected и ProcessTerminated. Каждое событие содержит transaction/process IDs, sequence и timestamp. Workers записывают sourceProcessNodeId, полученный из context на момент callback.

Политика MVP:

- child в Job Object получает тот же context;
- процесс вне Job Object не получает молчащую изоляцию;
- escape отображается в UI и по умолчанию переводит Transaction в Failed при значимом доступе;
- уже запущенные процессы не включаются задним числом;
- Windows services, drivers и системные процессы вне Job Object не считаются дочерними.

## Quiescing

При stop request или выходе root сервис запрещает новый launch, переводит Transaction в Quiescing, закрывает admission новых PID и ждёт Job Object empty. Сначала выполняется graceful close, затем policy-controlled termination дерева. Таймаут фиксируется как ProcessQuiesceTimeout и не маскируется успешным DiffReady.

Diff строится только после отсутствия активных handles, drain callback queue и завершения значимых nodes.

~~~mermaid
sequenceDiagram
    participant U as UI
    participant S as Service
    participant J as Job Object
    participant D as Driver
    participant W as Diff worker
    U->>S: StopTransaction
    S->>J: close admission and wait empty
    S->>D: Quiesce(TransactionId)
    D-->>S: callbacks drained
    J-->>S: process tree exited
    S->>W: materialize Diff
    W-->>S: DiffReady or error
~~~

## Service restart и безопасность

Service хранит nodes и mapping до завершения Transaction. После restart сверяются живые PID, Job Object, owner SID/session и driver mapping. Живое дерево без подтверждённого mapping переводится в Failed; произвольный PID не присоединяется по одному image path.

Named pipe и launch API проверяют SID, session и integrity. Access token не сохраняется в документах и логах. Job Object и process handles получают минимально необходимые права.

## Границы и MVP

MVP покрывает один root, обычных Win32 children, Job Object и user-mode lifecycle. Не входят полная контейнеризация, процессы с заранее созданными наследуемыми handles, driver-level isolation, службы/драйверы, kernel processes, уже работающие приложения и гарантированное удержание detached children.

