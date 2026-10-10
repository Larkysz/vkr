# Development Environment

Проверка выполнена 2026-10-10 на рабочем компьютере. ОС: Windows 11 Home, version 10.0.26200, x64. Проект теперь включает managed-компоненты на .NET 8 и отдельный compile-only WDK minifilter probe.

| Компонент | Статус | Найдено |
| --- | --- | --- |
| .NET SDK | AVAILABLE | 8.0.425 |
| MSBuild через .NET SDK | AVAILABLE | 17.11.48 |
| Visual Studio | AVAILABLE | Community 2026, 18.10.12224.181 |
| MSVC x64 | AVAILABLE | 14.51.36231, `Hostx64\x64\cl.exe` |
| Visual Studio MSBuild | AVAILABLE | 18.10.1; полный путь обнаружен через Visual Studio locator |
| Windows SDK | AVAILABLE | 10.0.28000.0 |
| WDK | AVAILABLE | 10.0.28000.0; minifilter header, `fltMgr.lib`, kernel-mode toolset and MSBuild rules found |
| CMake | OPTIONAL | Не требуется для текущего `.vcxproj` probe |
| `cl.exe` и `MSBuild.exe` в PATH | NOT REQUIRED | Используются по найденным полным путям/Developer environment |

`tools/check-environment.ps1` обнаруживает Visual Studio через `vswhere.exe` и проверяет реальные файлы компилятора, SDK и WDK вместо одного наличия Windows Kits directory.

## Проверка сборки драйвера

Пробный проект `src/Driver/MinifilterProbe/MinifilterProbe.vcxproj` собран отдельно от `TransactionalWindows.sln` с x64 и `WindowsKernelModeDriver10.0`. Сборка `.sys` прошла успешно; WDK выполнил локальную тестовую подпись. Это только compile/link smoke check. Probe не устанавливался и не загружался; файловые операции не перехватываются. Подробности — [Phase 4 filesystem preflight](phase-4-filesystem-preflight.md).

| Этап проверки | Статус |
| --- | --- |
| Phase 0–3 .NET restore/build/tests | PASS по последним отчётам |
| Обнаружение Visual Studio/MSVC/SDK/WDK | PASS |
| Minifilter probe compile/link | PASS |
| Runtime load/attach/detach | NOT RUN |
| FS-GATE-01 namespace semantics | NOT RUN |
| Registry feasibility gate | NOT RUN |

## Требование для runtime испытаний

До установки/загрузки драйвера подготовить отдельную тестовую Windows, восстанавливаемую после сбоя. Не включать test signing и не менять boot configuration на основной рабочей системе ради этого probe. Тестовая среда и процедура восстановления должны быть подтверждены владельцем тестовой машины.
