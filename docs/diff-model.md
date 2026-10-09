# Модель Diff

## Связанные модели

- [transaction-model.md](transaction-model.md) — момент построения и DiffId.
- [filesystem-overlay.md](filesystem-overlay.md) — FileChange.
- [registry-overlay.md](registry-overlay.md) — RegistryChange.
- [process-model.md](process-model.md) — ProcessNodeId.
- [commit-model.md](commit-model.md) — применение и статусы.

## Назначение и момент построения

Diff — материализованный версионируемый снимок после Quiescing, остановки дерева и drain callback queues. Он строится при целостном event sequence; время — UTC. DiffId уникален, generation монотонен при rebuild.

## Состав

| Поле | Описание |
| --- | --- |
| diffId, transactionId | UUID Diff и владельца. |
| schemaVersion, generation, builtAt | Совместимость, поколение и время. |
| fileChanges | Список логических FileChange. |
| registryChanges | Список логических RegistryChange. |
| groups | Группы по path/key и source process. |
| dependencies | Граф itemId → prerequisite itemId. |
| baselineGeneration | Снимок, относительно которого строился Diff. |
| status | Ready, Stale, Conflicted, Failed, Applied или PartiallyApplied. |

Каждый item имеет стабильный itemId, operation, baseline snapshot, overlay/current snapshot, source ProcessNodeId, selected, applicable, conflictStatus и applyStatus.

## Статусы item

| Значение | Смысл |
| --- | --- |
| Pending | Материализован, не выбран. |
| Selected | Выбран пользователем или dependency closure. |
| Applicable | Прошёл policy и baseline checks. |
| Unsupported | Не покрывается MVP. |
| Conflicted | Baseline изменился или нарушена зависимость. |
| Applied | Успешно применён и записан в journal. |
| Skipped | Не выбран или пропущен по решению. |
| Failed | Применение завершилось ошибкой. |

selected и applicable — разные признаки: выбор не снимает policy/conflict failure.

## Свёртка событий

Builder группирует события по canonical identity:

- writes → финальный Modify с size/hash/metadata;
- create + modify → Create;
- create + delete → удаление item;
- rename → единый item с oldPath и path;
- rename + modify → rename item с финальным content;
- delete → dependencies на дочерние записи.

Стабильная сортировка: namespace kind, canonical path/key, view, operation и item ID. Потеря sequence, повреждение overlay или неоднозначная identity дают Stale/Failed.

## Selective Commit

UI выбирает regular file, каталог, registry value или registry key целиком для delete/rename. Выбор расширяется dependency closure: родительский каталог, target key и связанные old/new rename paths добавляются автоматически и показываются как Required. Невыбранные items получают Skipped после завершения операции.

## Пример

~~~json
{
  "schemaVersion": 1,
  "diffId": "d6c3f6cf-6bfb-4a88-92a8-31de9cb9f020",
  "transactionId": "4a0e5c0d-6a0f-4b29-9f8c-0a1f3c8f9d21",
  "generation": 1,
  "builtAt": "2026-10-06T10:12:04.111Z",
  "status": "Ready",
  "items": [
    {"itemId":"file-1","kind":"File","operation":"Modify","path":"C:\\Apps\\data.db","selected":true,"status":"Applicable","dependencies":[]},
    {"itemId":"reg-1","kind":"RegistryValue","operation":"Set","keyPath":"HKCU\\Software\\Vendor","valueName":"Theme","selected":false,"status":"Pending","dependencies":[]}
  ]
}
~~~

UI показывает baseline/current/overlay snapshot, hash, size, source process, conflict reason и dependencies. Binary values и большие файлы отображаются через summary/content reference.

Пример dependency graph для Selective Commit:

~~~mermaid
graph LR
    ParentDir[Parent directory] --> File[Created file]
    OldKey[Old registry key] --> RenameKey[Renamed registry key]
    RenameKey --> ChildValue[Child registry value]
    File --> ApplyFile[Apply file item]
    ChildValue --> ApplyRegistry[Apply registry item]
~~~

## Устаревание и совместимость

Diff становится Stale, если после построения есть новые overlay events, изменился baseline, нарушен sequence, потеряно содержимое или изменилась policy. Service обязан rebuild или запретить Commit; UI не применяет устаревший generation.

schemaVersion повышается при несовместимом изменении. Неизвестные поля игнорируются, неизвестные enum values переводят item в Unsupported.

## MVP и вне области

MVP включает file/registry items, dependency graph, stable IDs, conflict status и выбор по item. Вне области: неподдерживаемые тома, binary merge, live Diff во время записи и automatic last-writer-wins.

