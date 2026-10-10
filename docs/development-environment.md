# Development Environment

Проверка выполнена на Windows 11 Home x64, build 26200. В проекте есть managed-компоненты .NET 8 и отдельный WDK minifilter feasibility probe.

| Компонент | Статус | Найдено |
| --- | --- | --- |
| .NET SDK | AVAILABLE | 8.0.425 |
| MSBuild через .NET SDK | AVAILABLE | 17.11.48 |
| Visual Studio | AVAILABLE | Community 2026, 18.10.12224.181 |
| MSVC x64 | AVAILABLE | 14.51.36231 |
| Visual Studio MSBuild | AVAILABLE | 18.10.1, включая x64 host |
| Windows SDK | AVAILABLE | 10.0.28000.0 |
| WDK | AVAILABLE | 10.0.28000.0; fltKernel.h, fltMgr.lib and kernel-mode rules |
| CMake | OPTIONAL | Не требуется текущему vcxproj probe |
| cl.exe и MSBuild.exe в PATH | NOT REQUIRED | Используются найденные установки Visual Studio |

tools/check-environment.ps1 обнаруживает Visual Studio через vswhere.exe и проверяет установленные compiler, SDK и WDK files.

## Проверка filesystem probe

src/Driver/MinifilterProbe/MinifilterProbe.vcxproj собирается отдельно от TransactionalWindows.sln, с x64 и WindowsKernelModeDriver10.0. Успешно проверены C compile/link, source INF средствами x64 InfVerif /w и /h, создание каталога через Inf2Cat и локальная WDK test signature. SignTool подтверждает наличие подписи, но тестовый корневой сертификат не доверен этой Windows. Это ожидаемо для локальной тестовой подписи, не production signature.

Обычный 32-разрядный MSBuild host не может загрузить x86/InfVerif.dll: такой DLL нет в текущей установке WDK. Сборка 64-разрядным MSBuild host проходит package verification без ошибок. Драйвер не устанавливался и не загружался.

tools/run-filesystem-probe.ps1 был запущен на отдельной scratch-папке и успешно проверил создание каталога, создание/чтение файла, перезапись, переименование, перечисление и удаление. Это только проверка сценария обычных файловых операций; она не подтверждает работу driver callbacks или overlay.

| Проверка | Статус |
| --- | --- |
| Phase 0–3 .NET restore/build/tests | PASS по предыдущим отчётам |
| Visual Studio/MSVC/SDK/WDK discovery | PASS |
| Minifilter C build, INF, catalog, local test signing | PASS with x64 MSBuild |
| Harness scratch operations | PASS, six operations |
| Runtime load/attach/detach | NOT RUN |
| Driver callback evidence | NOT RUN |
| FS-GATE-01 namespace semantics | OPEN |
| Registry feasibility gate | NOT RUN |

## Требование для runtime испытаний

Для загрузки драйвера нужна отдельная Windows, которую можно восстановить после сбоя. В текущей среде зарегистрированная VM и доступные VMware/VirtualBox/QEMU инструменты не обнаружены. Право чтения списка фильтров через fltmc в текущей non-elevated сессии отсутствует. Поэтому ничего не устанавливалось, привилегии не повышались, test signing и boot configuration основной Windows не менялись.

INF содержит временную лабораторную высоту фильтра 370500. Это значение не назначено Microsoft проекту и не проверено на конфликт с фильтрами тестовой Windows. Перед runtime-тестом в изолированной среде необходимо выбрать и проверить уникальную лабораторную высоту. Подробности приведены в [Phase 4 filesystem preflight](phase-4-filesystem-preflight.md).
