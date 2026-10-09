# Phase 0 Report

## Implemented

Создан минимальный project foundation без production integration:

- solution TransactionalWindows.sln;
- Core project TransactionalWindows.Core;
- Service application skeleton TransactionalWindows.Service;
- self-contained Core test runner TransactionalWindows.Core.Tests;
- каталоги-заглушки Driver, Registry и desktop без реализации.

Не создавались kernel driver, WPF UI, Windows Service host, filesystem interception, Registry interception, Named Pipe, Filter Manager Communication Port, SQLite или EF Core.

## Project Structure

~~~text
src/
  Core/TransactionalWindows.Core/
  Service/TransactionalWindows.Service/
  Driver/README.md
  Registry/README.md
desktop/README.md
tests/
  TransactionalWindows.Core.Tests/
docs/
~~~

Core не имеет ссылок на Service, WPF, WDK, Driver или конкретный IPC. Service ссылается только на Core. Tests ссылается на Core.

## Domain Models

Добавлены immutable records/value objects для TransactionId, DiffId, FileChangeId, RegistryChangeId, ProcessNodeId и DiffItemId. Реализованы Transaction, FileChange, RegistryChange, ProcessNode, DiffItem, Diff и Dependency. PID сопровождается ProcessCreationIdentity и не используется как единственная identity.

RegistrySubtree в DiffItemType обозначает концептуальный Registry key subtree; Core не использует Windows RegistryKey API или другие platform-specific Registry types.

## State Machine

TransactionStateMachine реализует детерминированные переходы из transaction-model.md:

Created → Starting → Running → Quiescing → DiffReady → Committing/Discarding → Completed.

Failure/Aborted transitions разрешены только на этапах, указанных в модели; Completed, Failed и Aborted terminal. Недопустимый переход выбрасывает DomainException с InvalidStateTransition.

## Interfaces

Добавлены минимальные контракты ITransactionManager, ITransactionRepository, IProcessManager, IOverlayStore, IDiffEngine, IConflictDetector, ICommitEngine, IRecoveryJournal и IEventPublisher. Добавлена IDiagnostics abstraction без конкретного logging backend. Реализаций storage, driver, overlay, IPC, Registry или Commit нет.

Добавлены domain event contracts с EventId, OccurredAt и TransactionId, а также declarative filesystem, Registry, conflict и unsupported-operation policies.

## Tests

Добавлен self-contained executable test runner с assertions для обязательных state transitions, commit/discard completion, terminal states, invalid transitions, stable/unique TransactionId, PID reuse identity, независимости file operation/status, Registry identity, допустимой dependency и dependency на отсутствующий/self item.

## Build Result

BUILD: PASS

Фактический текущий запуск: .NET SDK 8.0.425 и MSBuild 17.11.48 через dotnet SDK. Restore завершён успешно. Build завершён успешно: 0 warnings, 0 errors. Core test runner вывел: Core tests passed.

## Known Limitations

- Service skeleton использует только transition orchestration и не является настоящей Windows Service.
- TransactionApplication пока не подключает repository, process manager, overlay, diff, commit или recovery implementations.
- Domain records пока не являются wire DTO; сериализация и IPC отложены.
- Registry remains gated by REG-GATE-01 из architecture-audit.md.
- Filesystem minifilter и QueryDirectory/overlay behavior не реализованы.
- Standalone MSBuild.exe, Windows SDK, WDK и cl.exe отсутствуют; они необходимы только для следующих native/Driver phases.

## Deviations From Blueprint

- Blueprint предусматривал дополнительные Protocol/Commit projects; Phase 0 оставляет их до стабилизации contracts, чтобы не создавать пустые implementation projects.
- Blueprint target был предложен как net10.0; Phase 0 использует net8.0 для консервативной совместимости, но фактическая сборка требует установленного SDK и может потребовать согласования target framework.
- В solution включены Core, Service и Core.Tests; Driver, Registry feasibility и UI представлены README placeholders согласно запрету создавать их реализации.

## Next Phase

Phase 0 review завершена после успешных restore/build/test. Следующий отдельный этап — Phase 1; до написания Driver или Registry code команда должна провести FS-GATE-01 и REG-GATE-01 feasibility spikes в изолированном тестовом окружении.

## Final Status

PHASE 0 STATUS: READY

TOOLCHAIN:

- dotnet host: AVAILABLE, 10.0.7.
- .NET SDK: PASS, 8.0.425.
- .NET runtimes: AVAILABLE.
- MSBuild через .NET SDK: PASS, 17.11.48.
- Standalone MSBuild.exe: MISSING, не требуется для Phase 0.
- Visual Studio Build Tools: MISSING/UNKNOWN, требуется для будущих native/Driver phases.
- Windows SDK: MISSING.
- WDK: MISSING.
- C++ compiler cl.exe: MISSING.
- CMake: MISSING.

Restore: PASS

Build: PASS — 0 warnings, 0 errors

Tests: PASS — Core tests passed

FILES CREATED:

- TransactionalWindows.sln
- src/Core/TransactionalWindows.Core/**
- src/Service/TransactionalWindows.Service/**
- tests/TransactionalWindows.Core.Tests/**
- tools/check-environment.ps1
- docs/development-environment.md
- docs/phase-0-checklist.md
- docs/phase-0-report.md
- placeholders для src/Driver, src/Registry и desktop

FILES MODIFIED:

- Existing architecture documents were not modified.

ARCHITECTURE DEVIATIONS:

- architecture.md отсутствует в фактическом workspace; это зафиксировано в checklist и audit.
- Target framework остаётся net8.0; автоматически не изменялся.
- Driver, WPF, filesystem/Registry interception, production IPC и настоящий Windows Service не создавались.

BLOCKERS:

1. Для будущих native phases установить Visual Studio Build Tools/MSBuild, C++ toolset, Windows SDK и WDK.
2. Не начинать Phase 1 в рамках этой фиксации.

NEXT ACTION:

Phase 0 закрыта реальными PASS всех трёх команд. Следующая задача — отдельный запрос на Phase 1; native toolchain установить перед соответствующими Driver phases.

