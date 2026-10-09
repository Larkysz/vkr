# Phase 0 Checklist

Проверка выполнена 2026-10-07. BLOCKED используется только для реально невыполненных проверок; native toolchain отмечен N/A, поскольку Phase 0 managed-only.

| Проверка | Статус | Основание |
| --- | --- | --- |
| Architecture reviewed | PASS | architecture-audit.md, implementation-blueprint.md и model docs проверены; architecture.md отсутствует. |
| Solution exists | PASS | TransactionalWindows.sln присутствует. |
| Core exists | PASS | src/Core/TransactionalWindows.Core присутствует. |
| Service skeleton exists | PASS | src/Service/TransactionalWindows.Service присутствует. |
| Tests exist | PASS | tests/TransactionalWindows.Core.Tests присутствует. |
| Core dependency isolation verified | PASS | Core csproj не содержит ProjectReference. |
| State machine verified | PASS | State transition table и unit runner покрывают legal commit/discard, terminal и invalid paths. |
| Domain contracts verified | PASS | IDs, Transaction, changes, ProcessNode, Diff, Dependency, Conflict, CommitPlan, RecoveryEntry присутствуют. |
| Forbidden dependencies verified | PASS | В Core нет WPF/WDK/kernel/IPC implementation references. |
| .NET SDK available | PASS | SDK 8.0.425 найден. |
| MSBuild available | PASS | Build выполнен через MSBuild 17.11.48, входящий в .NET SDK; standalone msbuild.exe отсутствует. |
| Visual Studio Build Tools available | N/A | Не требуется для Phase 0 managed-only foundation; требуется для будущих native phases. |
| Windows SDK available | N/A | Не требуется для Phase 0; требуется для Driver/native phases. |
| WDK available | N/A | Не требуется для Phase 0; требуется для minifilter phase. |
| C++ compiler available | N/A | Не требуется для Phase 0; требуется для Driver phase. |
| Restore successful | PASS | dotnet restore TransactionalWindows.sln завершён успешно. |
| Build successful | PASS | dotnet build TransactionalWindows.sln: 0 warnings, 0 errors. |
| Tests successful | PASS | Core test runner вывел Core tests passed. |

## Required manual setup

Для Phase 0 дополнительных установок не требуется. Для будущих native phases установить Visual Studio Build Tools с C++/MSBuild workload, совместимый Windows SDK и WDK. После установки или обновления открыть новое PowerShell и выполнить следующие команды:

dotnet --info
dotnet --list-sdks
dotnet restore TransactionalWindows.sln
dotnet build TransactionalWindows.sln
dotnet run --project tests/TransactionalWindows.Core.Tests/TransactionalWindows.Core.Tests.csproj

## Forbidden Phase 0 implementations

В Phase 0 не создавались kernel driver, WPF, Filter Manager port, Named Pipe, filesystem interception, Registry interception или настоящий Windows Service host.

