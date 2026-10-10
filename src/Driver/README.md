# Windows driver work

`MinifilterProbe` is a compile-only probe for the installed MSVC, Windows SDK and WDK. It is not part of the transactional overlay implementation.

The probe registers no-op callbacks for create, write, set-information and directory-control operations, and requests attachment only to NTFS volumes. It does not redirect, modify, block or persist file operations. It has no INF or install configuration. Do not install or load this driver on a development machine.

Build with the Visual Studio 2026 x64 WDK toolset:

```powershell
MSBuild.exe .\src\Driver\MinifilterProbe\MinifilterProbe.vcxproj /m /p:Configuration=Debug /p:Platform=x64
```

A successful build confirms only that this minimal minifilter compiles and links. Runtime attachment, signing suitability, installation and filesystem semantics remain unverified. The transactional overlay must not be implemented until FS-GATE-01 namespace tests and failure semantics are approved.
