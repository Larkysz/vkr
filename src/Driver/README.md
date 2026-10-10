# Windows driver work

MinifilterProbe is a test-only WDK minifilter package for filesystem observation in an isolated, recoverable Windows test environment. It is not part of the transactional overlay implementation.

The probe counts create/open, write, set-information and directory-control callbacks on NTFS volumes, emitting a bounded trace. It does not redirect, modify, block or persist file operations. Its INF uses temporary laboratory altitude 370500, which is not a Microsoft-issued product altitude. Do not install or load this driver on the development Windows. Runtime use requires an isolated Windows and an assigned, collision-checked test altitude.

Build with the Visual Studio 2026 x64 WDK toolset:

    & 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' .\src\Driver\MinifilterProbe\MinifilterProbe.vcxproj /m /p:Configuration=Debug /p:Platform=x64

A successful package build confirms compilation, INF validation and local test signing only. Runtime attachment, unload, callback evidence and filesystem overlay semantics remain unverified. Use tools/run-filesystem-probe.ps1 only in an isolated test Windows after loading the test package and assigning a unique test altitude. The transactional overlay must not be implemented until FS-GATE-01 namespace tests and failure semantics are approved.
