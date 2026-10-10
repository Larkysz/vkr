# Minifilter filesystem observation spike

This is a test-only WDK minifilter package for an isolated, recoverable Windows test installation. The callbacks observe create/open, write, set-information and directory-control requests on NTFS volumes. They keep in-memory operation counters and emit at most the first 32 request summaries through DbgPrintEx; totals are emitted during unload.

The probe never changes, blocks, redirects or persists a filesystem request. It is not an overlay and proves none of the COW, tombstone, rename-merge or QueryDirectory semantics required by FS-GATE-01. Do not install or load it on the development Windows. The INF altitude 370500 is a temporary, unassigned laboratory value, not a Microsoft-issued product altitude. Runtime use requires an isolated Windows and an assigned, collision-checked test altitude.

Build from the repository root with the 64-bit Visual Studio MSBuild and x64 WDK target:

    & 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' .\src\Driver\MinifilterProbe\MinifilterProbe.vcxproj /m /p:Configuration=Debug /p:Platform=x64

The build creates a locally test-signed .sys and catalog under src/Driver/MinifilterProbe/bin/x64/Debug. This local signature is not a production signature. The x86 MSBuild host currently cannot load the installed InfVerif.dll; the x64 MSBuild host works. Validate the source INF separately with the installed x64 InfVerif.exe if needed.

tools/run-filesystem-probe.ps1 -Root <dedicated-test-directory> runs create, read, overwrite, rename, enumerate and delete checks under one unique scratch directory on a fixed local NTFS volume. Use it only in the isolated test Windows, after the probe has been installed and loaded there. Collect [TWProbe] kernel debug output and verify all four operation counters increase. The script deletes only its own generated scratch directory after checking its parent path and refusing reparse points.

Runtime load/unload and callback evidence have not yet been recorded. FS-GATE-01 remains open until those tests and the overlay namespace cases in the architecture audit pass in a disposable test environment.
