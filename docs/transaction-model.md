# Модель Transaction

## Связанные модели

- [filesystem-overlay.md](filesystem-overlay.md) — файловая изоляция и FileChange.
- [registry-overlay.md](registry-overlay.md) — виртуализация Registry и RegistryChange.
- [process-model.md](process-model.md) — дерево процессов.
- [diff-model.md](diff-model.md) — материализованный Diff.
- [commit-model.md](commit-model.md) — Commit, Discard и Selective Commit.

## Назначение и границы

Transaction — каноническая запись одной сессии запуска приложения. Она связывает root process, потомков, file overlay, registry overlay и Diff. Windows Service владеет жизненным циклом; WPF UI является клиентом; minifilter использует TransactionId в kernel callbacks.

MVP поддерживает один локальный NTFS-том, HKCU и ограниченный allowlist под HKLM\\Software. Commit выполняется после остановки дерева. Транзакция не является границей безопасности ядра и не обещает полную виртуализацию Windows.

## Общие идентификаторы и время

Все идентификаторы — UUID в канонической строке. Время — UTC ISO 8601 с миллисекундами и суффиксом Z. Хэши содержимого — SHA-256 в hex.

| Идентификатор | Назначение |
| --- | --- |
| TransactionId | Одна сессия и её overlay. |
| DiffId | Материализованный набор изменений. |
| FileChangeId | Логическая файловая запись. |
| RegistryChangeId | Логическая запись Registry. |
| ProcessNodeId | Узел дерева независимо от повторного использования PID. |

PID и file ID не являются глобальными идентификаторами и сопровождаются контекстом хоста или тома.

## Состав Transaction

| Поле | Описание |
| --- | --- |
| transactionId, schemaVersion | UUID и версия модели. |
| ownerSid, sessionId | Владелец и Windows session. |
| applicationPath, commandLine, workingDirectory | Параметры запуска; чувствительные аргументы маскируются. |
| overlayRoot, volumeScope, registryScope | Границы и хранилище. |
| jobObjectId, rootProcessNodeId | Контроль дерева процессов. |
| createdAt, startedAt, quiescingAt, diffReadyAt, completedAt | Временные отметки переходов. |
| baselineGeneration | Снимок состояния до запуска. |
| state, diffId, lastError | Текущее состояние, готовый Diff и ошибка. |

Access token и содержимое overlay в DTO не включаются; сервис хранит их отдельно и применяет ACL.

## Жизненный цикл

~~~mermaid
stateDiagram-v2
    [*] --> Created
    Created --> Starting: start
    Starting --> Running: root attached
    Starting --> Failed: launch error
    Running --> Quiescing: stop or root exit
    Running --> Failed: runtime error
    Quiescing --> DiffReady: tree stopped and diff built
    Quiescing --> Failed: timeout or diff error
    DiffReady --> Committing: commit or selective commit
    DiffReady --> Discarding: discard
    Committing --> Completed: finalized
    Committing --> Failed: apply or recovery error
    Discarding --> Completed: overlay removed
    Discarding --> Failed: cleanup error
    Failed --> Committing: recoverable retry
    Failed --> Discarding: discard failed run
    Failed --> Aborted: abort
    Completed --> [*]
    Aborted --> [*]
~~~

| Состояние | Смысл и действия |
| --- | --- |
| Created | Метаданные созданы; подготовка scope и overlay. |
| Starting | Создаются Job Object, PID mapping и root. |
| Running | Дерево работает, принимаются изменения. |
| Quiescing | Новые процессы запрещены, очереди drain-ятся. |
| DiffReady | Дерево остановлено, Diff сохранён; доступны решения. |
| Committing | Выполняется план применения или recovery. |
| Discarding | Освобождаются handles и удаляется overlay. |
| Completed | Итог зафиксирован; только аудит. |
| Failed | Операция остановлена с диагностикой; возможна recovery. |
| Aborted | Продолжение невозможно; только диагностика. |

## Инварианты

1. Только Service изменяет state; UI отправляет команды, driver публикует факты.
2. В Starting существует ровно один root process node.
3. Известные дочерние процессы имеют тот же TransactionId или статус EscapeDetected.
4. Quiescing закрывает admission новых процессов и ждёт bounded drain.
5. Commit доступен только в DiffReady; аварийный Discard разрешён для failed run.
6. Повтор завершённого Commit/Discard возвращает сохранённый результат и не применяет данные повторно.
7. Команда содержит expected state и operation ID.

## Компоненты и IPC

| Компонент | Ответственность |
| --- | --- |
| WPF UI | Команды, состояние, ошибки и выбор элементов Diff. |
| Windows Service | State machine, launcher, Job Object, policy, Diff и commit orchestration. |
| Minifilter | Быстрый context lookup и file/registry callbacks. |
| Overlay Store | Durable content, metadata, tombstone и journal. |
| Commit Engine | User-mode checks, применение и recovery. |

UI и Service используют ACL-защищённый named pipe с версионируемыми DTO. Driver и Service используют Filter Manager communication port. Каждое сообщение содержит TransactionId, protocol version и sequence. Callback не ждёт UI.

## События и восстановление

Событие содержит eventId, transactionId, occurredAt, sequence, state и payload. Публикуются TransactionCreated, TransactionStarted, TransactionStateChanged, ProcessAttached, ProcessExited, ProcessEscapeDetected, FileOverlayChanged, RegistryOverlayChanged, QuiescingStarted, DiffBuilt, DiffStale, CommitStarted, CommitItemApplied, CommitFailed, CommitCompleted, DiscardStarted, DiscardCompleted и RecoveryRequired.

Сервис сохраняет запись до запуска, при каждом переходе и перед каждой фазой Commit через временный файл и atomic replace. После рестарта проверяются overlay root, Job Object, живые PID, driver mapping и recovery journal. Недоказуемое состояние блокирует автоматический Commit и переводит сессию в Failed.

Ошибки имеют стабильные коды LaunchFailed, DriverUnavailable, ProcessQuiesceTimeout, OverlayCorrupted, DiffBuildFailed, ConflictDetected, CommitApplyFailed и RecoveryRequired. Повтор операции с тем же ID идемпотентен.

~~~json
{
  "schemaVersion": 1,
  "transactionId": "4a0e5c0d-6a0f-4b29-9f8c-0a1f3c8f9d21",
  "state": "Running",
  "ownerSid": "S-1-5-21-...-1001",
  "sessionId": 1,
  "applicationPath": "C:\\Apps\\Editor\\Editor.exe",
  "overlayRoot": "D:\\TransactionalWindows\\overlays\\4a0e...",
  "volumeScope": "\\\\?\\Volume{...}",
  "registryScope": ["HKCU", "HKLM\\Software\\VendorAllowlist"],
  "jobObjectId": "job-4a0e5c0d",
  "rootProcessNodeId": "c17fd7f9-2f93-4df1-b6ef-4d6ed0d7e4f3",
  "diffId": null,
  "lastError": null
}
~~~

## MVP и вне области

MVP: один NTFS-том, regular files/каталоги, HKCU, allowlist HKLM\\Software, один root с Job Object, offline Diff и Commit/Discard/Selective Commit.

Вне области: полная виртуализация Windows, VM/Sandbox, сеть и сменные тома, kernel security boundary, все registry hive, драйверы и boot-компоненты, live commit и произвольное слияние бинарных файлов.

