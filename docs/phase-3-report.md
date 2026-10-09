# Phase 3: управление деревом процессов

**PHASE 3 STATUS: IMPLEMENTED AND TESTED — USER-MODE PROCESS MANAGEMENT**

Дата проверки: 2026-10-09. Этап добавляет контролируемый запуск одного корневого процесса и наблюдение его дочерних процессов через Windows Job Object. Он не является прозрачной изоляцией файлов или Registry: для этого нужны последующие native/minifilter этапы.

## Что реализовано

`WindowsProcessManager` выполняет следующие операции:

- проверяет владельца транзакции: текущий SID, отсутствие impersonation и текущую Windows session;
- принимает только абсолютный существующий `.exe`, абсолютный рабочий каталог и корректную командную строку;
- создаёт root process в suspended состоянии;
- создаёт Job Object с `KILL_ON_JOB_CLOSE` без breakaway-флагов;
- назначает root в Job до его возобновления;
- связывает PID с устойчивым `ProcessNodeId` и временем создания процесса;
- подтверждает SID, session и членство каждого наблюдаемого процесса;
- получает дочерние процессы через Job notifications и периодический список членов Job;
- сохраняет PPID и `ParentNodeId`, чтобы построить дерево;
- использует retained process handles и creation time, поэтому повторно использованный PID не принимается за старый процесс;
- ждёт естественного завершения всего Job, отдельно фиксирует timeout и умеет завершить всё дерево;
- передаёт изменения узлов в `DurableTransactionStore`, если workflow подключён к durable storage.

`FileTransactionWorkflow` связывает lifecycle с состояниями Transaction: `Starting` записывается до запуска; root сохраняется suspended; после сохранения root transaction переходит в `Running`; после `Quiescing` и пустого Job разрешено строить Diff; Commit и Discard проверяют тот же барьер.

## Режимы и границы

Поддержан режим `ControlledUnisolated`: процессы сгруппированы и контролируются, но их обычные файловые и Registry вызовы пока не перенаправляются. `RequireIsolation` завершается ошибкой `UnsupportedIsolation`, потому что minifilter и Registry interception ещё не реализованы.

Один Transaction имеет один root. Уже запущенные процессы, Windows services, kernel drivers, процессы вне Job, detached children и breakaway-сценарии не становятся частью изоляции задним числом. Полная виртуализация Windows, VM и Windows Sandbox в Phase 3 не используются.

При штатном завершении ожидаются `Exited` и exit code. При аварийном `TerminateAsync` весь Job завершается, активные узлы получают `Terminated`; если наблюдение неполно, возвращается `ObservationIncomplete`, а успешное quiescence не подменяется.

## Тесты

| Проверка | Результат |
| --- | --- |
| `dotnet restore TransactionalWindows.sln` | PASS |
| `dotnet build TransactionalWindows.sln --no-restore` | PASS — 0 warnings, 0 errors |
| Core test runner | PASS — `Core tests passed.` |
| Service test runner | PASS — file overlay, persistence и process management tests passed |

Process tests проверяют suspended root, назначение в Job, запуск child/leaf, общий TransactionId, SID/session, ожидание всего дерева, timeout, принудительное завершение Job, защиту от неверного SID и отказ `RequireIsolation`.

## Изменённые файлы

- `src/Core/TransactionalWindows.Core/Domain/ProcessAndDiff.cs`;
- `src/Service/TransactionalWindows.Service/Processes/WindowsProcessManager.cs`;
- `src/Service/TransactionalWindows.Service/Processes/WindowsProcessNative.cs`;
- `src/Service/TransactionalWindows.Service/Processes/ProcessManagementException.cs`;
- `src/Service/TransactionalWindows.Service/FileTransactionWorkflow.cs`;
- `src/Service/TransactionalWindows.Service/Persistence/DurableTransactionStore.cs`;
- `src/Service/TransactionalWindows.Service/Persistence/StoredModels.cs`;
- `tests/TransactionalWindows.Service.Tests/ProcessManagementTests.cs`;
- `tests/TransactionalWindows.Service.Tests/Program.cs`;
- `src/Service/README.md`;
- `docs/process-model.md`;
- `docs/phase-3-report.md`.

## Остаточные риски и следующий этап

Completion port и polling дают контролируемое user-mode наблюдение, но не заменяют kernel process notifications. Сервис не восстанавливает активный Job после собственного перезапуска: durable tree сохраняется для аудита, а незавершённые состояния требуют recovery. Долгоживущие detached children и системные процессы требуют отдельной политики.

Следующая работа может закрывать native feasibility gate для File System Minifilter и Registry callbacks. До наличия Windows SDK, WDK и MSVC этот native этап не объявляется выполненным.
