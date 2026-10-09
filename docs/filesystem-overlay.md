# Файловый overlay и модель FileChange

## Связанные модели

- [transaction-model.md](transaction-model.md) — scope и TransactionId.
- [process-model.md](process-model.md) — связь PID с транзакцией.
- [diff-model.md](diff-model.md) — свёртка файловых событий.
- [commit-model.md](commit-model.md) — применение FileChange.

## Границы и пространства имён

File System Minifilter перенаправляет поддерживаемые операции так, чтобы приложение видело исходные пути. MVP ограничен одним локальным NTFS-том. UNC, сеть, ReFS, сменные носители и другие тома имеют статус Unsupported или явный passthrough и не считаются изолированными.

Overlay — служебное хранилище, а не смонтированный диск. Его root исключён из повторного перехвата, закрыт ACL и не раскрывается приложению.

Для каждой транзакции есть baseline namespace (состояние до запуска), overlay namespace (копии, новые объекты, metadata и tombstone) и effective namespace (overlay, если он существует, иначе baseline). Путь канонизируется из DOS в volume-relative форму, с разрешением точек, нормализацией разделителей и проверкой volume identity. Device paths, UNC и неразрешённые reparse-переходы отклоняются.

## Overlay Store

| Раздел | Назначение |
| --- | --- |
| objects/ | Содержимое созданных и скопированных файлов. |
| metadata/ | Путь, тип, attributes, timestamps, file ID и content reference. |
| tombstones/ | Скрытые объекты и старые имена после rename. |
| events/ | Append-only события driver/service. |
| journal/ | Cleanup и Commit recovery records. |

Имена физических файлов строятся по opaque object ID, а не по пользовательскому пути.

## FileChange

FileChange — логическая запись Diff, а не каждое IRP-событие.

| Поле | Описание |
| --- | --- |
| fileChangeId, transactionId | UUID записи и владельца. |
| path, oldPath | Новый и старый путь; oldPath нужен для rename. |
| volumeIdentity, fileId | Том и file ID, если доступен. |
| objectType, operation | File/Directory; Create, Modify, Delete, Rename. |
| baseline, overlay | Снимки существования, metadata, size и hash. |
| contentRef | Opaque ссылка на данные overlay. |
| size, timestamps, attributes | Итоговые metadata. |
| baselineHash, overlayHash | SHA-256, допускается отложенный расчёт. |
| sourceProcessNodeId | Процесс первого значимого события. |
| status, dependencies | Статус Diff и обязательные связи. |

Baseline и overlay сравниваются с current state при построении Diff и перед Commit.

## Семантика операций

| Операция | Effective namespace | Overlay |
| --- | --- | --- |
| Create | Новый объект виден сразу. | Content/metadata без baseline. |
| Modify | Read возвращает изменённое содержимое. | Copy-on-write при первом изменении. |
| Delete | Следующий open получает not found. | Tombstone. |
| Rename | Новый путь виден, старый скрыт. | oldPath + path и tombstone старого имени. |
| Create + Delete | Baseline не изменён. | Запись удаляется из Diff. |
| Modify + Delete | Итог — delete baseline. | Сохраняется tombstone. |

Каталог не содержит копии содержимого. Для дочерних объектов строится dependency edge на родителя.

## Copy-on-write и read path

При первом write/open-for-modify driver проверяет context. Если baseline regular file не скопирован, сохраняются baseline metadata и создаётся overlay copy; handle направляется в overlay. Повторные writes идут туда же. Read сначала проверяет tombstone и overlay metadata, затем baseline.

Cache Manager и overlapped I/O требуют отдельных тестов. Memory-mapped write считается ограничением, пока корректность dirty-page фиксации не доказана.

~~~mermaid
sequenceDiagram
    participant P as Приложение
    participant F as Minifilter
    participant O as Overlay Store
    participant S as Service
    P->>F: open(path, write)
    F->>F: lookup TransactionId and canonical path
    F->>O: metadata/copy-on-write
    O-->>F: baseline not in overlay
    F->>O: save baseline and create copy
    F-->>P: overlay handle
    P->>F: write/close
    F->>O: content and metadata update
    F-->>S: FileOverlayChanged
    S->>O: append event and fold logical record
~~~

Callback не ждёт UI. При недоступном сервисе используются policy/cache и bounded queue; если context нельзя определить, операция fail-closed либо получает Unsupported.

## Дедупликация и rename

Сырые события имеют eventId, sequence, PID, operation, path и timestamp. Worker сворачивает их по TransactionId, canonical path и file identity:

- несколько writes → один Modify с финальным size/hash;
- create + modify → Create;
- create + delete → нет Diff item;
- rename + modify → одна rename/modify запись;
- rename + delete → удаление логического объекта со связью old/new.

Потеря sequence переводит Diff в Stale/Failed. Rename каталога связывается с дочерними путями dependency graph.

## Ответственность и ограничения

| Компонент | Ответственность |
| --- | --- |
| Minifilter | Context lookup, callbacks, redirect и bounded enqueue. |
| Service | Policy, association с процессом, folding, baseline snapshot и Diff. |
| Overlay Store | Durable content/metadata/tombstone/event storage и ACL. |
| Commit Engine | Re-check и user-mode применение с journal. |

MVP включает regular files, каталоги и обычные Win32 create/read/write/delete/rename на одном NTFS-томе. Hard links, ADS, сложные reparse points, device paths, paging/boot-critical файлы, security descriptors и неподдерживаемые kernel API маркируются Unsupported.

Cleanup начинается после завершения дерева и освобождения handles. Каждая запись имеет pending/deleted/failed; повторный cleanup продолжает с последнего подтверждённого шага.

