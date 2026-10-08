# Материалы к расчетно-пояснительной записке курсового проекта

**Тема:** «Клиент-серверная система резервного копирования файлов с автоматической синхронизацией и ведением журнала изменений»  
**Дисциплина:** Программирование сетевых приложений (ПСП)  
**Год / Семестр:** 4 курс, 1 семестр  

---

## 1. Технологический стек и окружение

- **Язык программирования:** C# 13
- **Платформа / Runtime:** .NET 9.0 SDK (`net9.0`)
- **База данных:** PostgreSQL 17 (размещение: Docker Compose или локальный сервис)
- **ORM / Доступ к данным:** Entity Framework Core 9.0 (`Npgsql.EntityFrameworkCore.PostgreSQL`)
- **Сетевой транспорт:** Сырые TCP-сокеты (`System.Net.Sockets.TcpClient`, `TcpListener`, `NetworkStream`)
- **Формат заголовков кадра:** JSON (`System.Text.Json`), длина в сетевом порядке байт (Big-Endian)
- **Пользовательский интерфейс клиента:** Blazor Server Interactive (`Microsoft.AspNetCore.App`)
- **Файловая подсистема:** `System.IO.FileSystemWatcher`, асинхронные потоки `FileStream`
- **Криптографическое хэширование:** `System.Security.Cryptography.IncrementalHash` (алгоритм SHA-256)
- **Тестирование:** xUnit 2.8, FluentAssertions, Microsoft.NET.Test.Sdk

---

## 2. Структура Solution и назначение проектов

Solution: `BackupSystem.sln`

| Проект | Тип | Назначение и зона ответственности |
| :--- | :--- | :--- |
| **`Backup.Shared`** | Class Library | Общие протокольные типы (`ProtocolHeader`, `MessageType`), кадры (`ProtocolFrameReader`, `ProtocolFrameWriter`), контракты DTO, валидация путей (`ProtocolPathValidator`), хелперы ключей хранения (`StorageKeyHelper`), верификатор консистентности (`StorageConsistencyVerifier`). |
| **`Backup.MetadataService`** | Worker Service / Console | Микросервис управления метаданными. Единственный сервис с прямым доступом к СУБД PostgreSQL (`MetadataDbContext`). Реализует регистрацию клиентов, наборов, версионирование, резервирование (`pending_file_operations`), журнал изменений (`change_logs`), фоновую очистку просроченных резервирований. |
| **`Backup.StorageService`** | Worker Service / Console | Микросервис физического блочного хранения файлов. Принимает бинарные потоки, производит потоковое вычисление SHA-256, пишет во временные файлы `.tmp/*.part`, выполняет атомарный коммит в неизменяемые пути `clients/{cId}/backup-sets/{bsId}/files/{fId}/versions/{ver}.bin`, очищает устаревшие временные файлы. |
| **`Backup.SyncGateway`** | Worker Service / Console | Пограничный TCP-шлюз и оркестратор. Единственная точка входа для клиентов (127.0.0.1:5000). Ретранслирует потоки между клиентом, хранилищем и сервисом метаданных без накопления файлов на своем диске. Предотвращает гонки и защищает топологию бэкенда. |
| **`Backup.Client.Web`** | ASP.NET Core (Blazor Server) | Локальное клиентское приложение. Включает графический веб-интерфейс (`localhost:5100`), движок синхронизации (`SyncEngine`), фоновое наблюдение (`FileSystemWatcherService`), дебаунсинг событий (750 мс), локальный индекс кэша хэшей (`LocalFileIndexService`), селективное подавление эхо-событий восстановления (`RestoreSuppressionService`), и клиента шлюза с автоматическим переподключением (`BackupGatewayClient`). |
| **`tests/Backup.Tests`** | xUnit Test Project | Модульные, компонентные, интеграционные и сквозные (E2E) тесты протокола, сериализации, валидации путей, устойчивости к сбоям downstream, многопоточных клиентов, потоковой передачи больших файлов и сценариев восстановления. |

---

## 3. Сетевой протокол TCP Protocol v1

- **Маркер начала (Magic):** `BKP1` (ASCII)
- **Версия протокола:** `1`
- **Структура физического кадра:**
  ```text
  [4 байта: HeaderLength (Big-Endian UInt32)]
  [HeaderLength байт: UTF-8 JSON заголовок ProtocolHeader (макс. 64 KiB)]
  [BodyLength байт: Полезная нагрузка (JSON либо бинарный поток)]
  ```
- **Ограничения:**
  - Максимальный размер заголовка: 64 KiB (защита от переполнения буфера).
  - Строгое отсутствие мультиплексирования (No multiplexing / interleaving): одно соединение обслуживает строго последовательные кадры (FIFO). Для параллельных операций используются отдельные TCP-соединения.
  - Потоковая передача данных (Streaming): для бинарных тел передачи (`UploadFile`, `StoreFile`, `RestoreFile`) полезная нагрузка считывается и записывается чанками по 64 KiB без буферизации файла целиком в RAM (`File.ReadAllBytes` запрещен на контуре передачи).
- **Валидация относительных путей:**
  - Запрещены абсолютные пути (`/`, `C:\`, `\\unc`).
  - Запрещены переходы по каталогам (`..`, `.`).
  - Запрещены спецсимволы (`\0`, `*`, `?`, `<`, `>`, `|`, `"`).
  - Нормализация к единому разделителю `/`.

---

## 4. Схема реляционной базы данных (PostgreSQL)

База данных: `backup_system` (доступ через `MetadataDbContext`).

1. **`clients`**:
   - `Id` (UUID, PK)
   - `Name` (varchar 255), `MachineName` (varchar 255)
   - `CreatedAtUtc` (timestamptz), `LastSeenAtUtc` (timestamptz)
2. **`backup_sets`**:
   - `Id` (UUID, PK)
   - `ClientId` (UUID, FK -> `clients.Id`, ON DELETE CASCADE)
   - `Name` (varchar 255), `LocalRootPath` (varchar 1024)
   - `CreatedAtUtc` (timestamptz), `IsActive` (boolean)
3. **`files`**:
   - `Id` (UUID, PK)
   - `BackupSetId` (UUID, FK -> `backup_sets.Id`, ON DELETE CASCADE)
   - `RelativePath` (varchar 2048)
   - `CurrentVersionNumber` (integer, default 0)
   - `IsDeleted` (boolean, default false)
   - `CreatedAtUtc` (timestamptz), `UpdatedAtUtc` (timestamptz)
   - **Constraint:** `UNIQUE (BackupSetId, RelativePath)`
4. **`file_versions`**:
   - `Id` (UUID, PK)
   - `OperationId` (UUID, UNIQUE) — токен транзакции для идемпотентного коммита
   - `FileId` (UUID, FK -> `files.Id`, ON DELETE CASCADE)
   - `VersionNumber` (integer)
   - `Size` (bigint), `Sha256` (varchar 64)
   - `StorageKey` (varchar 1024, nullable)
   - `CreatedAtUtc` (timestamptz)
   - **Constraint:** `UNIQUE (FileId, VersionNumber)`
5. **`change_logs`**:
   - `Id` (UUID, PK)
   - `BackupSetId` (UUID, FK -> `backup_sets.Id`)
   - `FileId` (UUID, nullable)
   - `RelativePath` (varchar 2048, nullable)
   - `Action` (varchar 50): `CREATE`, `MODIFY`, `DELETE`, `RENAME`, `RESTORE`
   - `Status` (varchar 50): `SUCCESS`, `FAILED`
   - `Details` (text, nullable)
   - `CreatedAtUtc` (timestamptz)
6. **`pending_file_operations`**:
   - `OperationId` (UUID, PK)
   - `FileId` (UUID, FK -> `files.Id`, ON DELETE CASCADE)
   - `ExpectedVersionNumber` (integer)
   - `ExpectedSize` (bigint), `ExpectedSha256` (varchar 64)
   - `OperationKind` (varchar 50): `NORMAL`, `RESTORE`
   - `CreatedAtUtc` (timestamptz), `ExpiresAtUtc` (timestamptz)

---

## 5. Алгоритмы системы

### 5.1. Алгоритм резервного копирования файла (Upload Pipeline)
1. Клиент вычисляет инкрементальный SHA-256 и размер файла.
2. Клиент отправляет запрос `FileChanged` (или `PrepareVersion`) на Gateway.
3. Gateway обращается к MetadataService:
   - Если хэш совпадает с актуальной версией, возвращается `UploadRequired = false` (дедупликация идентичного содержимого).
   - Если хэш изменился, создается запись в `pending_file_operations` со сроком действия (`ExpiresAtUtc`) и инкрементированным номером версии. Возвращается `UploadRequired = true` и `OperationId`.
4. Клиент шлет кадр `UploadFile` с `OperationId` и начинает потоковую отправку байт.
5. Gateway в потоковом режиме ретранслирует поток в `StorageService` (`StoreFile`).
6. StorageService сохраняет байты в `data/storage/.tmp/{OperationId}.part`, проверяя хэш на лету. При совпадении атомарно перемещает файл в детерминированный путь:
   `clients/{cId}/backup-sets/{bsId}/files/{fId}/versions/{ver:D10}.bin`
7. Gateway фиксирует операцию в MetadataService (`CommitVersion`). MetadataService в одной транзакции переносит запись из `pending_file_operations` в `file_versions`, обновляет `CurrentVersionNumber` и записывает аудит в `change_logs`.

### 5.2. Алгоритм первичного согласования (Initial Manifest Reconciliation)
1. Клиент загружает локальный кэш хэшей `LocalFileIndex` (`*.local-index.json`).
2. Клиент сканирует директорию на диске (`DirectoryScanner`). Если `Length` и `LastWriteTimeUtc` совпадают с кэшем, хэш берется из кэша (быстрый пропуск); иначе хэш вычисляется заново.
3. Формируется пакетный запрос `ManifestRequest` со списком локальных файлов и отправляется на Gateway.
4. Gateway опрашивает MetadataService снимком (один запрос к БД, без N+1).
5. MetadataService строит план согласования `SyncPlan`:
   - `Upload`: новые или изменившиеся файлы;
   - `Delete`: файлы, отсутствующие локально, но не помеченные удаленными на сервере;
   - `Unchanged`: идентичные файлы.
6. Клиент последовательно выполняет загрузку необходимых файлов и удаление отсутствующих, после чего обновляет и сохраняет локальный кэш.

### 5.3. Алгоритм восстановления (Restore Pipeline)
1. Пользователь выбирает файл и версию в UI (`/files/{fileId}`).
2. Клиент проверяет, есть ли локальные несохраненные правки. Если есть, делает их упреждающий бэкап.
3. Клиент регистрирует целевой хэш в `RestoreSuppressionService` (для подавления ложного срабатывания FileSystemWatcher).
4. Клиент отправляет `RestoreRequest` на Gateway.
5. Gateway проверяет версию через MetadataService (`ResolveVersion`), получает `StorageKey` и запрашивает бинарный поток из StorageService (`GetStoredFile`).
6. Gateway в потоковом режиме отдает байты клиенту (`RestoreReady` + `RestoreFile`).
7. Клиент записывает байты во временный файл в целевой директории `.restore.{guid}.tmp`, проверяет SHA-256 и атомарно перемещает (`File.Move`) на место целевого файла.
8. Создается новая ревизия со статусом `RESTORE`, чтобы история оставалась строго линейной и неизменяемой.

---

## 6. Ключевые архитектурные решения (Design Decisions)

1. **Разделение метаданных и физических файлов:** База данных хранит только структурированные реляционные данные и ссылки (`StorageKey`). Физические блобы лежат на диске, что предотвращает раздувание СУБД (bloat) и исключает узкое место транзакционного лога при передаче гигабайтных файлов.
2. **Атомарность сохранения:** Все входящие файлы пишутся во временные файлы (`.tmp/*.part` на сервере и `.restore.*.tmp` на клиенте) и перемещаются в постоянное хранилище только после полной проверки размера и SHA-256.
3. **Неизменяемость версий (Append-Only):** Ни одна зафиксированная версия файла никогда не перезаписывается на диске. Восстановление старой версии создает новую последующую версию с указанием типа операции `RESTORE`.
4. **Защита от сетевых сбоев и reconnect lifecycle:** Клиент `BackupGatewayClient` автоматически распознает разрыв TCP-соединения, выполняет повторное подключение с экспоненциальной задержкой (backoff 500ms, 1s, 2s, 5s), автоматически восстанавливает логическую сессию (`ClientId`) и инициирует сверку состояния (`ReconciliationRequired = true`).
5. **Безопасная очистка мусора:** Удаляются исключительно просроченные временные резервирования (`ExpiresAtUtc < Now`) и временные файлы старше порога (`.tmp/*.part`). Committed-блобы в каталоге `clients/.../versions/` никогда не удаляются автоматически во избежание потери данных.

---

## 7. Конфигурация портов и путей по умолчанию

- **MetadataService TCP:** `127.0.0.1:5001`
- **StorageService TCP:** `127.0.0.1:5002`
- **SyncGateway TCP:** `127.0.0.1:5000`
- **Client Web UI (HTTP):** `http://localhost:5100`
- **PostgreSQL:** `127.0.0.1:5432`
- **Storage Root:** `data/storage`
- **Client State File:** `data/client/client-state.json`
