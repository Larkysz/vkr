# Phase 4 preflight: проверка native toolchain

**PREFLIGHT STATUS: BUILD PASS; FS-GATE-01 RUNTIME TESTS NOT STARTED**

Проверка выполнена 2026-10-10 на Windows 11 Home x64, build 26200. Инструменты обнаружены в каталогах установки, хотя `cl.exe` и `MSBuild.exe` не находятся в `PATH`.

| Компонент | Найдено | Статус |
| --- | --- | --- |
| Visual Studio | Community 2026, 18.10.12224.181 | PASS |
| MSVC | 14.51.36231, `Hostx64\x64\cl.exe` | PASS |
| MSBuild | Visual Studio 18.10.1 | PASS |
| Windows SDK | 10.0.28000.0 | PASS |
| WDK | 10.0.28000.0; `fltKernel.h`, `fltMgr.lib` и kernel-mode MSBuild rules | PASS |
| Probe minifilter | `MinifilterProbe.sys` собран и подписан локальной test-signing конфигурацией WDK | PASS, compile/link only |

## Проверочный драйвер

`src/Driver/MinifilterProbe` содержит небольшой minifilter для проверки сборки: регистрирует no-op callbacks на create, write, set-information и directory-control; запрашивает attach только к NTFS. Он не меняет файловые операции, не является overlay, не имеет INF/service installation manifest и не включён в основную solution. `.sys` создаётся в локальной build-папке проекта, исключённой Git через `bin/`.

Сборка выполнена Visual Studio MSBuild 18.10.1 с `WindowsKernelModeDriver10.0`, x64, WDK 10.0.28000.0. Результат: compile/link успешны, WDK локально подписал бинарный файл; INF2Cat пропущен, потому что INF намеренно отсутствует. В проект не добавлялись команды установки, загрузки, фильтр-подключения, test-signing режима Windows или изменения boot configuration.

## Что ещё нужно для FS-GATE-01

Этот preflight подтверждает инструментарий, но не работу filesystem overlay и не закрывает FS-GATE-01. Для следующего runtime spike нужен отдельный тестовый компьютер или отдельная тестовая Windows, которую можно восстановить после сбоя. Там отдельно проверяются загрузка/выгрузка фильтра, порядок attach/detach, безопасный fail-closed behavior и тестовые случаи namespace из [architecture-audit.md](architecture-audit.md) и [filesystem-overlay.md](filesystem-overlay.md).

Текущий probe сам по себе эти сценарии не реализует. Перед выдачей тестовой VM/машины настраиваются её владелец и recovery procedure; режим test signing и установка любого драйвера выполняются только в этой тестовой среде.

## Изменения preflight

- `src/Driver/MinifilterProbe/MinifilterProbe.vcxproj`
- `src/Driver/MinifilterProbe/MinifilterProbe.c`
- `src/Driver/MinifilterProbe/README.md`
- `src/Driver/README.md`
- `tools/check-environment.ps1`
- `docs/development-environment.md`
- `docs/phase-4-filesystem-preflight.md`

Это не завершённая Phase 4 и не начало production overlay. Следующая кодовая задача остаётся FS feasibility harness, только после подготовки отдельной восстанавливаемой тестовой Windows и уточнения критериев загрузочного испытания.
