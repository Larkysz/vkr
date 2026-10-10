# Phase 4 filesystem preflight

**STATUS: PACKAGE BUILD PASS; DRIVER RUNTIME TEST BLOCKED ON ISOLATED WINDOWS**

Проверка выполнена 2026-10-10 на Windows 11 Home x64, build 26200. WDK package собран; основной Windows не менялась: драйвер не устанавливался и не загружался, test-signing и boot configuration не менялись.

| Проверка | Результат |
| --- | --- |
| Visual Studio Community 2026 / MSVC x64 | PASS; WDK toolset 10.0.28000.0 |
| Windows SDK и WDK | PASS; версия 10.0.28000.0 |
| 64-bit Visual Studio MSBuild | PASS; 18.10.1 |
| MinifilterProbe C compile/link | PASS; 0 warnings, 0 errors |
| INF check with x64 InfVerif /w and /h | PASS; INF is valid |
| INF2Cat package signability | PASS; no errors, no warnings |
| Local WDK test signing | PASS; current machine does not trust the generated test root |
| Filesystem harness syntax | PASS |
| Filesystem harness operations on a scratch folder | PASS: create, read, overwrite, rename, enumerate, delete |
| Driver install/load/unload on isolated Windows | NOT RUN; no separate recoverable Windows/VM was found |
| Driver callback evidence | NOT RUN |
| FS-GATE-01 overlay semantics | OPEN |

## Package contents

The MinifilterProbe directory contains an observation-only minifilter and an installable test package. It requests attachment only to NTFS and registers callbacks for create/open, write, set-information and directory-control. The callback increments in-memory counters and emits at most 32 short messages with DbgPrintEx; the probe never blocks, redirects or changes an I/O request. It is not an overlay and does not prove COW, tombstones, rename behavior or QueryDirectory merging.

The INF specifies PnpLockdown=1, uses the isolated driver store (DIRID 13), declares the FltMgr dependency and writes minifilter instance settings under the service Parameters key. InfVerif.exe from the installed x64 WDK validated the source INF with both /w and /h. Inf2Cat.exe generated the catalog with no errors or warnings. WDK locally test-signed the driver and catalog; SignTool confirms the signatures are present but cannot build trust because their test root is not trusted on this machine. This is expected for a local test certificate and is not production signing.

The ordinary MSBuild host is 32-bit and its WDK package verification task fails to load x86/InfVerif.dll, which is absent from this WDK installation. Building the same project with the installed amd64/MSBuild.exe completes successfully and runs the package tasks. This is an environment/tool-host issue; the x64 INF verifier and package checks themselves pass.

## Altitude and test safety

The package currently contains altitude 370500 only as a temporary laboratory placeholder. Microsoft assigns filter altitudes to products and load-order groups; this value has not been assigned to this project or checked for collision with filters on a test machine. Do not install the package while the placeholder remains unreviewed. Before runtime testing, use a disposable Windows with a recovery point, inspect existing filter altitudes there, and choose a unique test altitude for that environment. Never enable test signing or change boot settings on the development Windows for this probe.

The current machine has no available Hyper-V PowerShell module or registered local VM, and no VMware/VirtualBox/QEMU executable was found. Access to fltmc filters was denied in the current non-elevated session. Therefore actual filter installation, load, attach, unload and callback collection remain pending. No attempt was made to elevate or make system changes.

## Filesystem harness

The script tools/run-filesystem-probe.ps1 with parameter -Root and an existing dedicated test directory creates a unique scratch child under the supplied directory, requires a fixed local NTFS volume, rejects a UNC path, drive root and reparse-point root, and checks before deleting only that scratch child. It tests ordinary host filesystem operations. It must only be used on the isolated Windows after the observation driver is loaded; it creates no overlay and does not exercise the transaction system.

The harness was run locally against the repository directory, where it created and removed its uniquely named scratch folder. All six checks passed. The filter was not loaded, so these results validate the harness only, not driver callbacks.

## Next gate

FS-GATE-01 remains open. The next work requires a disposable/recoverable Windows test installation and a non-colliding laboratory altitude. In that environment, first record install/load/attach/unload evidence and confirm callback counters change during the filesystem harness. Only after that should the separate namespace feasibility cases be run for copy-on-write, delete/tombstone, rename and QueryDirectory behavior. Do not implement a production overlay, Service-driver IPC, Registry interception, WPF or commit engine as part of this spike.

Microsoft reference: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/minifilter-altitude-request
