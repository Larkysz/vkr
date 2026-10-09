# Phase 2: сохранение состояния и восстановление после перезапуска

**PHASE 2 STATUS: IMPLEMENTED AND TESTED — DURABLE METADATA CORE**

Дата проверки: 2026-10-09. Этап выполнен поверх Phase 1 без замены работающего файлового слоя и без изменений состояний Core. Нумерация относится к фактическим отчётам проекта: Phase 0 — основа, Phase 1 — файловый сценарий, Phase 2 — сохранение метаданных. Старый [implementation-blueprint.md](implementation-blueprint.md) содержит отдельную предварительную нумерацию.

## Результат

Состояние Transaction, исходный ответ принятой команды, последовательность событий и готовый Diff сохраняются на диск. При повторении команды с тем же OperationId возвращается её первоначальный ответ; новая запись и новый побочный эффект не создаются. Это проверено после повторного открытия хранилища и после принудительного завершения отдельного тестового процесса.

В файловом workflow состояние Committing или Discarding записывается до соответствующих операций с файлами. DiffReady записывается вместе с Diff. Completed записывается после завершения операций и очистки overlay. История и Diff сохраняются отдельно от overlay и остаются доступны после cleanup.

Этот этап не реализует запуск обычных приложений, прозрачное перенаправление файловых операций или восстановление прерванного Commit по каждому файлу. Сохранение метаданных не объявляется полноценным crash-safe Commit.

## Реализованные компоненты

| Компонент | Назначение |
| --- | --- |
| DurableTransactionStore | Реализация существующего ITransactionRepository; сохранение, чтение, version check, архивирование, Diff, журнал результатов команд и событий. |
| DurableTransactionApplication | Создание Transaction с выдачей ID только при первом выполнении; проверка владельца, чтение состояния и Diff, запись фактов orchestration, replay событий и startup scan. |
| StoredOperation | OperationId, request digest, оригинальный Transaction response, sequence. |
| StoredEvent | Sequence, EventId, TransactionId, OperationId, имя события, From/To и UTC timestamp. |
| RestartEntry | Результат startup scan с disposition и диагностикой. |
| UtcTimestampConverter | UTC ISO 8601 с семью знаками дробной части и суффиксом Z; сохранение точности .NET timestamps. |
| FileTransactionWorkflow | Необязательное подключение durable store; запись фактов lifecycle до файловых эффектов; проверка расположения хранилища; фиксация ошибок cleanup. |

Core остаётся библиотекой без файловых API и без новых зависимостей. Durable repository и сериализация находятся в Service, target framework остаётся net8.0. Legacy TransactionApplication остаётся основой Phase 0; для сохранения используется DurableTransactionApplication или durable FileTransactionWorkflow.

## Формат и порядок записи

Выбран локальный JSONL write-ahead journal без нового NuGet-пакета или базы данных. Единственный файл transactions.jsonl содержит записи, а writer.lock запрещает второму экземпляру открывать то же хранилище для записи. Один экземпляр сериализует параллельные команды своим lock.

Каждая строка имеет SchemaVersion = 1, непрерывный глобальный Sequence, сериализованный Payload и SHA-256 Payload в lowercase hex. Payload содержит Transaction, необязательный Diff, признак архивирования, StoredOperation и StoredEvent. Domain IDs сохраняются как объекты с UUID в Value; wire protocol этим форматом не определяется. Максимальная запись — 4 MiB.

Правила записи:

1. Проверить владельца, OperationId, expected version и допустимый переход состояния.
2. Для повторного запроса вернуть сохранённый ответ; reuse OperationId с другим содержимым отклонить. Время обработки не входит в request digest и не мешает replay.
3. Проверить целостность будущей записи; для Diff проверить идентичность, item IDs и dependency graph, включая циклы.
4. Записать одну строку, заканчивающуюся LF, и выполнить FileStream.Flush(flushToDisk: true).
5. Установить новую проекцию в памяти и только затем вернуть результат.

Результат команды, состояние и событие не записываются в независимые файлы: между ними нет отдельного окна рассогласования checkpoint. Если запись/flush заканчивается ошибкой, экземпляр помечается непригодным для дальнейших операций. Перед retry требуется закрыть и повторно открыть хранилище, чтобы проверить фактическую границу записи.

Состояния сохраняются через тот же TransactionStateMachine, что и в Core. Raw RecordState/RecordTransition — внутренние факты orchestration; они не запускают приложения и не применяют файлы. Эти методы нельзя напрямую публиковать для произвольного изменения состояния из UI.

ITransactionRepository.Save принимает только следующий канонический state snapshot с Version = previous + 1. Изменение launch parameters, владельца и overlay root через Save запрещено. Для DiffReady требуется отдельная RecordDiff, сохраняющая Diff вместе с переходом. Архивирование не удаляет историю и допускается только для terminal states.

## Восстановление

При открытии проверяются schema version, sequence, checksum, IDs, соответствие ответа команды и события Transaction, порядок переходов и присутствие Diff. Неизвестная версия или повреждённая завершённая запись блокирует открытие: файл не переписывается и не превращается в пустое хранилище.

Незавершённый JSON suffix без LF сохраняется отдельно как torn-tail-UUID.bin, затем журнал обрезается только до последней полной границы. TornTailRemoved сообщает об этом вызывающему коду. Если suffix является полным JSON без LF, открытие блокируется: невозможно отличить прерванную запись от повреждения ранее сохранённого разделителя. Checksum обнаруживает случайные повреждения; это не защита от намеренной подмены данных.

RecoverAfterRestart вызывается один раз перед приёмом фактов от производителей событий. Открытие хранилища само по себе не означает, что startup scan уже выполнен.

| Сохранённое состояние | Startup disposition и действия |
| --- | --- |
| Created | InspectCreated: сохраняется; требуется проверка ресурсов до запуска. |
| Starting, Running, Quiescing | Failed + RecoveryRequired; живые процессы и полнота file journal пока не подтверждены. |
| DiffReady | ReviewDiff: Diff доступен для просмотра; перед будущим применением нужно заново подтвердить overlay, процессы и baseline. Workflow в этой фазе нельзя возобновить из DiffReady после рестарта. |
| Committing, Discarding | Failed + RecoveryRequired; автоматическое повторение файловых операций запрещено. |
| Completed, Aborted | InspectTerminal; сохраняется для аудита. |
| Failed | RecoveryRequired; повторный startup scan не создаёт ещё одну failure-запись. |

Failed остаётся terminal согласно Core. RecoveryRequired — диагностический результат отдельного scan, а не новое состояние Transaction. Этот этап не добавляет переход Failed → Committing.

Startup scan не удаляет overlay, не меняет baseline и не считает отсутствие процесса доказательством успешного Commit. После прерванной файловой операции исход/backup необходимо выяснить отдельно. В файловом workflow durable metadata root должен не пересекаться с baseline или overlay; сохранённые roots обязаны совпадать с реальной сессией.

## Проверки

Текущий запуск в рабочем каталоге:

| Проверка | Результат |
| --- | --- |
| dotnet restore TransactionalWindows.sln | PASS |
| dotnet build TransactionalWindows.sln | PASS — 0 warnings, 0 errors |
| Core test runner | PASS — Core tests passed. |
| Service file overlay test runner | PASS — Service file overlay tests passed. |
| Service persistence test runner | PASS — Service persistence tests passed. |
| .NET SDK | PASS — 8.0.425 |
| MSBuild через .NET SDK | PASS — 17.11.48 |

Новые интеграционные проверки входят в существующий TransactionalWindows.Service.Tests. Проверены:

- duplicate create и transition до и после reopen; первоначальный ответ при более новом текущем состоянии;
- parallel duplicate commands, optimistic version conflict и запрет второго writer;
- owner mismatch и owner-filtered event replay с sequence cursor;
- transaction и Diff round trip, неизменность сохранённого Diff при изменении исходного массива;
- отсутствующие, повторяющиеся и циклические dependency IDs; отклонение неверного Diff до записи;
- архивирование с сохранением истории и legacy repository methods;
- startup scan всех десяти Transaction states и отсутствие повторной failure-записи;
- оборванный suffix, checksum mismatch, неверные sequence/schema, malformed JSON и утраченный LF;
- ограничение размера записи и отсутствие durable mutations у отклонённых команд;
- durable Commit/Discard файлового workflow, очистка overlay и audit после reopen;
- прерванный workflow: baseline не изменяется, overlay остаётся, возобновление как новой сессии запрещено;
- отдельный writer process принудительно завершается без Dispose: ранее подтверждённые состояние и события сохраняются, startup scan требует recovery.

Тесты используют отдельные временные каталоги. Принудительно завершается только созданный тестом дочерний процесс. Не проверялись аппаратный power loss, повреждение NTFS, все возможные точки обрыва write/flush, реальный Windows Service или native driver.

## Изменённые файлы

- src/Service/TransactionalWindows.Service/Persistence/StoredModels.cs
- src/Service/TransactionalWindows.Service/Persistence/DurableTransactionStore.cs
- src/Service/TransactionalWindows.Service/Persistence/UtcTimestampConverter.cs
- src/Service/TransactionalWindows.Service/DurableTransactionApplication.cs
- src/Service/TransactionalWindows.Service/FileTransactionWorkflow.cs
- src/Service/README.md
- tests/TransactionalWindows.Service.Tests/PersistenceTests.cs
- tests/TransactionalWindows.Service.Tests/Program.cs
- docs/phase-2-report.md

## Остаточные ограничения

- Один trusted writer, локальная файловая система. Автоматическая compaction, quotas и bounded event pagination ещё не реализованы; журнал и memory projections растут с историей.
- Хранилище предназначено для доверенного Service слоя в приватном каталоге. ACL, filesystem impersonation и проверка SID по токену будущего IPC не реализованы. Переданный SID сам по себе не является аутентификацией.
- Persistent transaction record содержит launch arguments; хранилище нельзя публиковать клиенту как сырой журнал. Redaction/ACL должны быть добавлены перед реальным Service/IPC deployment.
- Сохраняются Transaction/Diff и события lifecycle. FileOverlaySession пока не реконструируется из своего file-changes.jsonl; восстановление активного overlay и watermark остаётся отдельной задачей.
- Command replay гарантируется для метаданных. Файловые Commit/Discard не получают durable per-item journal или глобальную атомарность от этой интеграции. Прерванный Commit автоматически не возобновляется.
- Один локальный каталог с regular files в user-mode остаётся границей файловой реализации Phase 1. Нет minifilter, прозрачной изоляции, Registry backend, process tree, Job Object, Named Pipe, Windows Service host и WPF UI.

## Следующий этап и окружение

Следующий логический этап — Phase 3: контролируемый запуск и ожидание завершения дерева процессов через Windows Job Objects. Это будет process-management этап; без minifilter такой запуск не должен выдаваться за изолированный запуск обычного приложения.

FS-GATE-01 остаётся открытым. Проверка tools/check-environment.ps1 на этом запуске обнаружила отсутствие standalone MSBuild, cl.exe, Visual Studio Build Tools, Windows SDK и WDK. Эти компоненты нужны до native/minifilter этапов, но не блокируют Phase 2 на .NET. REG-GATE-01 также не закрыт: callback-only Registry virtualization не объявляется реализованной.
