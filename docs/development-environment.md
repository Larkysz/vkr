# Development Environment

Проверка выполнена 2026-10-07 в рабочем каталоге проекта. ОС: Microsoft Windows 11 Домашняя, version 10.0.26200, build 26200. Версии не подменялись и не устанавливались автоматически.

| Компонент | Статус | Найдено | Требование Phase 0 |
| --- | --- | --- | --- |
| .NET CLI | AVAILABLE | C:\\Program Files\\dotnet\\dotnet.exe, host 10.0.7 | Используется для restore/build/test. |
| .NET SDK | AVAILABLE | 8.0.425 | Текущий net8.0 target поддержан. |
| .NET runtimes | AVAILABLE | Microsoft.NETCore.App 6.0.36, 8.0.14/8.0.17, 9.0.7, 10.0.1/10.0.5/10.0.7; WindowsDesktop 8.0.17, 9.0.7, 10.0.1/10.0.5/10.0.7 | Runtime не заменяет SDK. |
| MSBuild через .NET SDK | AVAILABLE | MSBuild 17.11.48 из dotnet SDK; restore/build прошли | Достаточно для Phase 0. |
| Standalone MSBuild.exe | MISSING | Команда msbuild не найдена в PATH | Не блокирует Phase 0; нужен для отдельных Visual Studio/native workflows. |
| Visual Studio Build Tools | MISSING/UNKNOWN | Visual Studio locator и стандартные roots не обнаружены | Требуется для будущих native/Driver phases. |
| C++ compiler | MISSING | cl не найден | REQUIRED для следующих Driver phases, не нужен для Phase 0 Core C#. |
| CMake | MISSING | Команда cmake не найдена | OPTIONAL для текущего Phase 0; может потребоваться для native tooling. |
| Visual Studio locator | MISSING | vswhere не найден | REQUIRED/OPTIONAL для обнаружения VS, если установка будет добавлена позже. |
| Windows SDK | MISSING | Windows Kits directories отсутствуют | REQUIRED для native Windows/Driver phases. |
| WDK | MISSING | WDK path не обнаружен вместе с Windows Kits | REQUIRED для minifilter phase. |

Phase 0 toolchain достаточен: .NET SDK 8.0.425 и входящий MSBuild 17.11.48 доступны. Для следующих native phases дополнительно нужны Visual Studio Build Tools/standalone MSBuild, совместимые Windows SDK, WDK, C++ toolset и test-signing setup. Их отсутствие не блокирует Phase 0, потому что Phase 0 не содержит native driver implementation.

