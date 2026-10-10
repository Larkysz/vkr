# Minifilter build probe

This is a compile-only feasibility probe for the installed Visual Studio, MSVC, Windows SDK and WDK toolchain. It is not part of the transactional overlay implementation.

The filter registers no-op callbacks for create, write, set-information and directory-control operations, and requests attachment only to NTFS volumes. It does not redirect, modify, block or persist file operations. The project intentionally has no installable INF or service configuration. Do not install or load this driver on a development machine.

Build from a Visual Studio Developer PowerShell with the x64 driver target:

```powershell
MSBuild.exe .\MinifilterProbe.vcxproj /m /p:Configuration=Debug /p:Platform=x64
```

A successful build confirms only that the WDK project compiles and links. Runtime attachment, signing, installation, filesystem semantics and the FS-GATE-01 namespace tests remain unverified.
