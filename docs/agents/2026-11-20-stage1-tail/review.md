PASS: все требования спеки по коду подтверждены (сборка 0 предупреждений, тесты 122/34, `has-pending` чистый, `ef-script` без пересоздания таблиц, `narrow-0600` пережил прогрев одного профиля). Два пункта приемки (`has-pending` и `migrations script`) сверены только с файлами-доказательствами.

# Проверка: хвосты этапа 1, шестой заход (профильная изоляция, обновлённая спека, ef-script)

Дата: 2026-11-21. Репозиторий `VR.RoutePlanner`, ветка `ai/stage1-graph`, HEAD `d613b3c` (2026-11-21 19:24 +0300). Дерево чистое.
Спека: `C:\Projects\VRouter\doc\agents\2026-11-20-stage1-tail\spec.md` (изменена 19:26, после коммита).
Доказательства: `C:\Projects\VRouter\doc\agents\2026-11-20-stage1-tail\evidence\ef-has-pending.txt` + `ef-script.txt` (заход 5, не переснимались).

## Вердикт
PASS. Блокер прошлого раза (стирание строк `narrow-0600`) закрыт: `ResourceRepository.SaveAsync` теперь удаляет только профили набора, новый тест `SaveAsync_OneProfile_KeepsRowsOfOtherProfile` подтверждает.

## Что проверено самостоятельно
- `git status --porcelain` → пусто; HEAD `d613b3c`, ветка `ai/stage1-graph`.
- `dotnet build VR.RoutePlanner.sln --no-incremental -nologo -clp:Summary` → 0 предупреждений, 0 ошибок.
- `.\scripts\test.ps1 -NoBuild` → exit 0; Unit 122/122, Integration 34/34, Load 1/1, Simulation 1/1, пропусков 0.
- `git diff 4acbb04 d613b3c -- ResourceRepository.cs` → удаление ограничено `profiles.Contains(row.AgvProfile)`.
- Новый тест: `ResourceRepositoryTests.SaveAsync_OneProfile_KeepsRowsOfOtherProfile` (строка 91) — вызывает `SaveAsync` только с `WideProfile` и проверяет, что `NarrowProfile` не изменился.
- Прямой запрос к `rms_vrp_test`, только SELECT:
  - `vrt_TrafficResource` по профилям: `narrow-0900` 594, `tusk-forklift-1102` 584.
  - `vrt_EdgePassability`: `narrow-0900` True 307/False 58, `tusk-forklift-1102` True 298/False 67.
  - `vrt_ResourceConflict`: 4944.
  - `vrt_EdgeWidth`: `Raster` 365, диапазон 0,8–22,41 м.
  - `vrt_WarmupState`: `ResourceCount`/`ImpassableCount` содержат оба профиля в JSON.
  - `__EFMigrationsHistory_RoutePlanner`: 5 строк, от `Stage1_Graph` до `Stage1_EdgeWidthIndex`.

## Доказательства (файлы захода 5, перечитаны)
- `ef-has-pending.txt`: «No changes have been made to the model since the last migration.»
- `ef-script.txt`: 5 `INSERT INTO "__EFMigrationsHistory_RoutePlanner"`; `CREATE TABLE` `vrt_*` только в первой миграции; `vrt_WarmupState` и `IX_vrt_EdgeWidth_EdgeId` разрешены частью 4; `Maps`/`Nodes`/`Edges` только в `REFERENCES` (4 места), без `CREATE`.
- `ef-script.txt` перечитан сфокусированно: содержит `CREATE TABLE "vrt_WarmupState"` и `CREATE INDEX "IX_vrt_EdgeWidth_EdgeId"`; спека, часть 1, п. 5 в новой редакции явно разрешает эти два оператора после baseline.

## Обновления спеки (дифф с заходом 4)
- Часть 1, п. 5: разрешает `CREATE TABLE public."vrt_WarmupState"` и `CREATE INDEX "IX_vrt_EdgeWidth_EdgeId"` после baseline.
- Часть 2, п. 51: требование профильной изоляции — удаление только профилей набора.

## Замечания
Нет.

## Не покрыто проверкой
- `has-pending-model-changes` и `migrations script`: подтверждены только файлами-доказательствами; в моей роли `dotnet ef` запрещён.
- `POST /api/graph/warmup` и `GET /api/graph/status` через HTTP: не перезапускались после исправления изоляции; состояние БД после тестов содержит оба профиля и совпадает с `WarmupState`.
- В `appsettings.json` API и Workers профиль `narrow-*` отсутствует; тесты кладут `narrow-0600`/`narrow-0900` программно. При реальном запуске API с текущим конфигом `tusk-forklift-1102` строки `narrow-*` переживут прогрев (подтверждено тестом), но сам `narrow` не пересчитается. Решение о составе профилей в конфигах — за владельцем спеки.