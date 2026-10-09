# Registry overlay и модель RegistryChange

## Связанные модели

- [transaction-model.md](transaction-model.md) — registryScope и lifecycle.
- [process-model.md](process-model.md) — источник изменения.
- [diff-model.md](diff-model.md) — группировка RegistryChange.
- [commit-model.md](commit-model.md) — применение Registry.

## Границы MVP

Виртуализируются HKCU и ограниченный allowlist под HKLM\\Software. HKLM\\SYSTEM, HKLM\\SECURITY, HKLM\\SAM, драйверные/boot-настройки и произвольные kernel consumers не входят в MVP. Изменение вне allowlist получает Unsupported или явный passthrough и не включается в изолированный Diff.

Каждая запись различает hive, 32/64-bit view, canonical key path и SID владельца. Сервис хранит policy, Registry callback использует transaction context и ограниченный cache.

## RegistryChange

| Поле | Описание |
| --- | --- |
| registryChangeId, transactionId | UUID записи и транзакции. |
| hive, view | HKCU/allowlisted HKLM; 32, 64 или Default. |
| keyPath, valueName | Canonical key path и имя value; default value — пустое имя. |
| operation | Create, Set, Delete, Rename. |
| valueType, baselineSize, overlaySize | REG type и размеры. |
| baselineDigest, overlayDigest | SHA-256 представления значения. |
| baselineValue, overlayValue | Снимки; binary values допускают content reference. |
| sourceProcessNodeId | Процесс-источник. |
| status, dependencies | Статус и связи key/parent/rename. |

Секретные значения не попадают в UI-журнал без redaction policy.

## Lookup и операции

Effective read сначала проверяет transaction overlay: tombstone даёт not found, overlay value возвращает сохранённое значение, иначе читается baseline. Create создаёт объект только в overlay; Set сохраняет baseline snapshot при первом изменении; Delete создаёт tombstone; Rename хранит old/new key path как одну операцию.

| Операция | Итог |
| --- | --- |
| Create | Новый key/value виден процессу, отсутствует в baseline. |
| Set | Изменённое значение видно только transaction processes. |
| Delete | Объект скрыт. |
| Rename | Новый key path виден, старый скрыт. |

Create + Delete без baseline сворачивается в отсутствие Diff item. Set + Delete становится Delete baseline value. Удаление key включает child values/keys либо фиксируется как subtree operation.

## Callback, cache и service

Registry callback получает PID/context и проверяет canonical scope в pre-operation. Read использует компактный cache overlay metadata; write создаёт bounded event для user-mode worker. Долгие операции, большие значения, Diff и Commit выполняются сервисом. Callback не ждёт named pipe или WPF UI.

При cache miss допускается короткий kernel-safe lookup policy. Если решение невозможно, поддерживаемая запись отклоняется с RegistryPolicyUnavailable; прозрачный passthrough не используется без явного разрешения.

~~~mermaid
sequenceDiagram
    participant P as Процесс
    participant R as Registry callback
    participant C as Policy/cache
    participant O as Registry overlay
    participant S as Service
    P->>R: RegOpen/RegSetValue
    R->>C: check TransactionId, hive, view, path
    C-->>R: allow and overlay metadata
    R->>O: read effective value or save baseline
    O-->>R: value or tombstone
    R-->>P: virtual result
    R->>S: RegistryOverlayChanged
    S->>O: fold change and persist digest
~~~

## Policy и согласованность

Allowlist сопоставляется с полным canonical path, hive, view и SID. 32-bit и 64-bit view являются отдельными namespace entries. Перед Diff и Commit сервис повторно читает baseline через Win32 APIs и сверяет digest, type, существование и view. Несовпадение получает Conflicted; fail-closed запрещает молчаливую перезапись.

## Ответственность, тесты и ограничения

| Компонент | Ответственность |
| --- | --- |
| Registry callback | Context, scope check, virtual read/write и bounded event. |
| Service | Policy, folding, baseline snapshot, Diff и orchestration. |
| Overlay Store | Value/key data, tombstone, digest и journal. |
| Commit Engine | User-mode Win32 Registry API и recovery. |

Тесты: HKCU create/set/read/delete; allowlisted HKLM; раздельные 32/64 views; rename key с child values; create+delete cancellation; baseline conflict; restart с неполной event queue.

Вне MVP: полный HKLM, ACL merge, service/driver registration, boot settings, kernel-only consumers, cross-hive atomicity и автоматическое binary merge.

