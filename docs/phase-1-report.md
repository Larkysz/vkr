# Phase 1: user-mode file transaction slice

## Статус

**PHASE 1 FILE FOUNDATION: IMPLEMENTED AND TESTED**

Этот этап добавляет проверяемый user-mode слой для одного локального каталога. Он не является Windows Service host, minifilter или прозрачной виртуализацией уже запущенного приложения.

## Что реализовано

- FileOverlaySession создаёт отдельный overlay с каталогами content и tombstones.
- Чтение сначала смотрит overlay, затем baseline.
- Первая запись существующего файла делает copy-on-write; новый файл сразу создаётся только в overlay.
- Удаление сохраняется через tombstone, а переименование хранит пару OldPath/Path.
- Относительные пути нормализуются; абсолютные пути и выход через .. отклоняются.
- Каждая операция записывается в JSONL-журнал и получает FileChangeId.
- FileDiffEngine сворачивает текущие изменения в материализованный Diff.
- FileCommitEngine поддерживает полный и выборочный файловый Commit, вычисляет dependency closure и проверяет baseline digest.
- Commit использует временную замену файла и резервные копии для восстановления при ошибке. Глобальная атомарность нескольких файлов не заявляется.
- FileTransactionWorkflow связывает overlay с состояниями Starting, Running, Quiescing, DiffReady, Committing, Completed, Failed.

## Проверенный сценарий

Тест создаёт A.txt и C.txt, изменяет A.txt, создаёт B.txt и удаляет C.txt внутри overlay. До Commit исходная папка остаётся без изменений. Selective Commit применяет A.txt и B.txt, но оставляет C.txt; Discard удаляет overlay без изменения baseline. Внешнее изменение baseline между Diff и Commit приводит к отказу и не перезаписывается.

## Проверка

- dotnet restore TransactionalWindows.sln: PASS
- dotnet build TransactionalWindows.sln: PASS, 0 warnings, 0 errors
- dotnet run --project tests/TransactionalWindows.Core.Tests/TransactionalWindows.Core.Tests.csproj: PASS, Core tests passed.

## Ограничения

- Minifilter и Filter Manager communication port ещё не реализованы.
- Системные вызовы обычного Windows-приложения пока не перенаправляются в FileOverlaySession автоматически.
- Поддерживаются regular files в пределах одного локального каталога; каталоги, ADS, memory-mapped writes, hard links, reparse points, сетевые пути и другие тома остаются вне этого этапа.
- Registry overlay, Process Tree, Job Object, Named Pipe, persistence и WPF UI не реализованы.

Следующий технический этап должен закрыть filesystem feasibility gate для minifilter и IRP_MN_QUERY_DIRECTORY, сохранив этот user-mode слой как тестовый oracle для семантики overlay и Commit.
