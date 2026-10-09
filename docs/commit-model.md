# Модель Commit, Discard и Selective Commit

## Связанные модели

- [transaction-model.md](transaction-model.md) — состояния DiffReady и Committing.
- [filesystem-overlay.md](filesystem-overlay.md) — файловые изменения.
- [registry-overlay.md](registry-overlay.md) — Registry изменения.
- [process-model.md](process-model.md) — offline boundary.
- [diff-model.md](diff-model.md) — items и dependencies.

## Предпосылки и режимы

Commit выполняется только offline: дерево завершено, Job Object empty, callback/event queues drain-нуты, overlay закрыт для записи, Diff имеет текущий generation. UI не фиксирует изменения во время работы приложения.

- Commit — применить все items, прошедшие policy/conflict checks.
- Discard — не менять baseline и удалить overlay.
- Selective Commit — применить выбранные items и dependencies, остальные discard.

## Фазы

| Фаза | Действия |
| --- | --- |
| Prepare | Проверить state, owner SID, Diff generation, overlay и journal. |
| Validate | Проверить policy, unsupported items, dependencies и порядок. |
| ConflictCheck | Сверить baseline с текущими file metadata/hash и Registry digest. |
| Apply | Применять выбранные items с journal и backups. |
| Finalize | Зафиксировать результаты, обновить Transaction и закрыть journal. |
| Recover | После сбоя продолжить или откатить доказуемые steps. |

Transaction переходит DiffReady → Committing перед Prepare. После Finalize — Completed; ошибка оставляет Failed и RecoveryRequired.

~~~mermaid
sequenceDiagram
    participant U as UI
    participant S as Service
    participant D as Diff
    participant C as Commit Engine
    participant F as File system
    participant R as Registry
    U->>S: Commit or SelectiveCommit
    S->>D: validate generation and selection
    S->>C: Prepare
    C->>C: Validate and ConflictCheck
    C->>F: ordered file items
    C->>R: ordered registry items
    C->>C: Finalize journal and cleanup
    C-->>S: Completed or RecoveryRequired
    S-->>U: item results and audit
~~~

## Recovery journal и порядок

Journal append-only содержит operation ID, item ID, expected baseline, temporary/backup refs, phase, precondition, result и checksum. Статусы записи: pending, applied, rolledBack или unknown. Повтор сначала проверяет фактическое состояние и не выполняет blind write.

Порядок:

1. Создать отсутствующие parent directories/keys.
2. Применить renames и creates в dependency order.
3. Записать file content и metadata через temporary object и atomic replace, где возможно.
4. Применить Registry values и key operations через штатные Win32 APIs.
5. Выполнить deletes после зависимых children.
6. Обновить journal и audit.

Между file и Registry не заявляется глобальная атомарность Windows.

## Конфликты

Если baseline изменился после старта или построения Diff, item получает Conflicted, а операция останавливается до решения. Политика fail-closed:

- Skip — item не применяется;
- явное Overwrite — только после подтверждения и повторной проверки прав/типа;
- Merge автоматически не выполняется.

Last-writer-wins запрещён. Нельзя применить dependency item при нерешённом конфликте closure.

## Файлы и Registry

Для файлов Commit Engine проверяет volume/path canonicalization, создаёт parent, пишет временный content, проверяет hash и выполняет replace/rename с backup. Delete проверяет expected baseline и не удаляет объект другой identity.

Для Registry Engine открывает нужные hive/view через Win32 APIs, проверяет type и digest, затем выполняет Set/Create/Rename/Delete. HKCU выполняется от владельца Transaction или согласованного impersonation token; HKLM allowlist требует подходящих прав. Scope нельзя расширить во время Commit.

## Discard и Selective Commit

Discard переводит Transaction в Discarding, закрывает handles, удаляет overlay content/metadata/tombstones и journal, не меняя baseline. Cleanup идемпотентен; ошибка оставляет recovery state.

Selective Commit принимает itemId, вычисляет dependency closure и применяет Selected ∪ Required. Остальные получают Skipped и удаляются после успешного применения. Rename выбирается целиком по old/new paths; delete key выбирается вместе с child entries.

## Частичные сбои и восстановление

После ошибки новые items не применяются; результаты сохраняются, Transaction получает Failed. Resume разрешён только для journal entries с проверенным precondition. При unknown сервис требует ручного решения и запрещает blind Commit. Rollback — best-effort для backup операций; полной атомарности произвольных Windows API нет.

После рестарта сервис проверяет journal, ACL/owner, handles и hashes. UI получает RecoveryRequired и список applied/failed/unknown. Overlay удаляется только после terminal status всех записей; audit сохраняется.

## Безопасность и ошибки

Команда проверяет owner SID, interactive session, integrity и право на Transaction. Named pipe не позволяет выбрать чужой TransactionId. Access token и секретные Registry values не логируются.

Ошибки: InvalidState, DiffStale, PolicyDenied, ConflictDetected, PermissionDenied, ObjectChanged, IoFailure, RegistryFailure, JournalCorrupted, RecoveryRequired, CleanupFailed и QuiesceTimeout.

## Критерии успеха, MVP и вне области

Успех означает: выбранные applicable items имеют Applied, journal закрыт, baseline соответствует результату, overlay cleanup завершён, Transaction получил Completed, а UI показывает audit. Unsupported/conflicted items остаются явно перечисленными.

MVP: offline Commit/Discard/Selective Commit для одного NTFS-тома, regular files/каталогов, HKCU и allowlisted HKLM\\Software. Вне области: live commit, TxF, VM/Sandbox, глобальная атомарность file+Registry, все hives, merge, сетевые/сменные тома, драйверы и boot-компоненты.

