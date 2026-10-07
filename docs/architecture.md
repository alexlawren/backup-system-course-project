# Архитектура системы резервного копирования

## 1. Общий обзор системы (Change-set #2: PostgreSQL + Metadata Service)

Курсовой проект по дисциплине «Программирование сетевых приложений» посвящен разработке распределенной клиент-серверной системы резервного копирования файлов с автоматической синхронизацией и ведением журнала изменений.

Вся межсервисная сетевая коммуникация между клиентом и серверными компонентами строится на базе **собственного бинарного TCP-протокола (Protocol v1)**, использующего асинхронный ввод-вывод (`System.Net.Sockets`) и потоковую передачу данных (streaming).

```
Browser (Пользователь)
    |
    | HTTP / Blazor (http://localhost:5100)
    v
Backup.Client.Web (Локальный UI клиента)
    |
    | TCP (127.0.0.1:5000) [Protocol v1]
    v
Backup.SyncGateway (Шлюз синхронизации и оркестратор)
    |
    +---- TCP (127.0.0.1:5001) [Protocol v1] ----> Backup.MetadataService
    |                                                |
    |                                                | EF Core / Npgsql
    |                                                v
    |                                              PostgreSQL (backup_system)
    |
    +---- TCP (127.0.0.1:5002) [Protocol v1] ----> Backup.StorageService
                                                     (в будущем -> Файловое хранилище)
```

---

## 2. Архитектурная изоляция базы данных

> [!IMPORTANT]
> **PostgreSQL принадлежит ТОЛЬКО `Backup.MetadataService`**.
> Проекты `Backup.Client.Web`, `Backup.SyncGateway` и `Backup.StorageService` не имеют и не должны иметь прямого доступа к базе данных или строкам подключения к ней. Вся работа с метаданными осуществляется исключительно через сетевые вызовы TCP Protocol v1.

---

## 3. Схема базы данных и сущности (MetadataService)

База данных построена с использованием **Entity Framework Core 9** и провайдера **Npgsql**.

### Реляционные сущности:

1. **`clients` (`ClientEntity`)**:
   - `Id` (PK, Guid): уникальный идентификатор зарегистрированного клиента;
   - `Name` (varchar 255): отображаемое имя рабочей станции;
   - `MachineName` (varchar 255): сетевое имя хоста;
   - `CreatedAtUtc` (timestamptz): время первичной регистрации;
   - `LastSeenAtUtc` (timestamptz): время последней активности клиента.

2. **`backup_sets` (`BackupSetEntity`)**:
   - `Id` (PK, Guid): идентификатор набора резервного копирования;
   - `ClientId` (FK -> `clients`, Guid): владелец набора (Cascade Delete);
   - `Name` (varchar 255): название набора (например, "Рабочие документы");
   - `LocalRootPath` (varchar 1024): локальный корневой путь на клиенте;
   - `CreatedAtUtc` (timestamptz): дата создания;
   - `IsActive` (bool): признак активности набора.
   - *Индексы:* `ClientId`.

3. **`files` (`FileEntity`)**:
   - `Id` (PK, Guid): идентификатор логического файла;
   - `BackupSetId` (FK -> `backup_sets`, Guid): родительский набор (Cascade Delete);
   - `RelativePath` (varchar 2048): нормализованный относительный путь (разделитель `/`);
   - `CurrentVersionNumber` (int, default 0): номер актуальной зафиксированной версии;
   - `IsDeleted` (bool, default false): признак логического удаления;
   - `CreatedAtUtc` (timestamptz);
   - `UpdatedAtUtc` (timestamptz).
   - *Ограничения:* `UNIQUE (BackupSetId, RelativePath)`.

4. **`file_versions` (`FileVersionEntity`)**:
   - `Id` (PK, Guid): идентификатор версии;
   - `FileId` (FK -> `files`, Guid): родительский файл (Cascade Delete);
   - `VersionNumber` (int): порядковый номер ревизии файла (1, 2, ...);
   - `Size` (bigint): точный размер файла в байтах;
   - `Sha256` (varchar 64): контрольная сумма содержимого;
   - `StorageKey` (varchar 1024, nullable): уникальный ключ файла в хранилище (заполняется при Commit);
   - `CreatedAtUtc` (timestamptz).
   - *Ограничения:* `UNIQUE (FileId, VersionNumber)`, индекс на `FileId`.

5. **`change_logs` (`ChangeLogEntity`)**:
   - `Id` (PK, Guid): запись аудита;
   - `ClientId` (FK -> `clients`);
   - `BackupSetId` (FK -> `backup_sets`);
   - `FileId` (nullable FK -> `files`, SetNull);
   - `Action` (varchar 64): тип действия (`CREATE`, `MODIFY`, `DELETE`, `RENAME`);
   - `Status` (varchar 64): статус фиксации (`COMMITTED`);
   - `Details` (varchar 2048, nullable): текстовое описание события;
   - `CreatedAtUtc` (timestamptz).
   - *Индексы:* `CreatedAtUtc`.

6. **`pending_file_operations` (`PendingFileOperationEntity`)**:
   - `OperationId` (PK, Guid): уникальный идентификатор незавершенной операции;
   - `FileId` (FK -> `files`, Cascade Delete);
   - `ExpectedVersionNumber` (int): ожидаемый номер следующей версии;
   - `ExpectedSize` (bigint): заявленный размер загрузки;
   - `ExpectedSha256` (varchar 64): ожидаемый хэш содержимого;
   - `CreatedAtUtc` (timestamptz);
   - `ExpiresAtUtc` (timestamptz): срок действия операции (по умолчанию +30 минут).
   - *Индексы:* `ExpiresAtUtc`.

---

## 4. Жизненный цикл версионирования (Prepare -> Commit)

Для предотвращения несогласованности данных и защиты от частичных загрузок операция создания новой версии разбита на две фазы:

```
[Клиент / Gateway]                                      [MetadataService]                      [PostgreSQL]
        |                                                       |                                   |
        |--- 1. PrepareVersionRequest (Path, Size, Sha256) ---->|                                   |
        |                                                       |-- Поиск файла по RelativePath --->|
        |                                                       |   (если нет, создает File v0)     |
        |                                                       |-- Проверка committed Sha256 ----->|
        |                                                       |   (если совпал: UploadReq=false)  |
        |                                                       |-- Создание PendingOperation ----->|
        |<-- 2. PrepareVersionResponse (OpId, ExpVersion) ------|                                   |
        |                                                       |                                   |
   [Фаза передачи данных в StorageService на Change-set #3]     |                                   |
        |                                                       |                                   |
        |--- 3. CommitVersionRequest (OpId, Key, Size, Hash) -->|                                   |
        |                                                       |== BEGIN TRANSACTION =============>|
        |                                                       |-- Валидация PendingOperation ---->|
        |                                                       |-- Создание FileVersion ---------->|
        |                                                       |-- Обновление File.CurrentVersion->|
        |                                                       |-- Запись в ChangeLog ------------>|
        |                                                       |-- Удаление PendingOperation ----->|
        |                                                       |== COMMIT TRANSACTION ============>|
        |<-- 4. CommitVersionResponse (FileId, Version) --------|                                   |
```

### Важнейшие инварианты:
- **`PrepareVersion` НЕ создает `FileVersion`** и не увеличивает `CurrentVersionNumber`.
- **`CommitVersion` выполняется в единой ACID-транзакции**. В случае несовпадения хэша/размера или повторного вызова транзакция откатывается (`Rollback`), дублирующие версии не создаются.
- **Удаление (`DeleteFileMetadata`)** является логическим (`IsDeleted = true`), исторические версии в `file_versions` сохраняются.
- **Переименование (`RenameFileMetadata`)** меняет логический путь в `files`, сохраняя всю историю ревизий без физического перемещения блоков.

---

## 5. Внутренняя архитектура MetadataService

Сервис организован по уровням ответственности:

1. **`MetadataServerWorker`**: сетевой уровень. Принимает TCP-клиентов, поддерживает сессии и потоковое чтение кадров.
2. **`MetadataMessageDispatcher`**: протокольный диспетчер. Маршрутизирует `MessageType` в строго типизированные вызовы сервиса приложения и формирует ответы с корректными `MessageId` и `CorrelationId`.
3. **`MetadataApplicationService`**: слой бизнес-логики. Валидирует входящие параметры и пути (`ProtocolPathValidator`), управляет транзакциями и сущностями.
4. **`IDbContextFactory<MetadataDbContext>`**: потокобезопасная фабрика контекстов EF Core. Каждый запрос создает изолированный экземпляр контекста через `await using var dbContext = await _dbContextFactory.CreateDbContextAsync()`.

---

## 6. Физическое файловое хранилище (Change-set #3: StorageService)

### Архитектурная изоляция:
- **`Backup.StorageService` отвечает исключительно за физические файлы**.
- Сервис ничего не знает о PostgreSQL, EF Core, наборах таблиц или журналах изменений.
- Сервер принимает сырой бинарный поток байтов через Protocol v1, проверяет целостность, сохраняет файл и выдает детерминированный `StorageKey`.

### Детерминированная конвенция StorageKey:
Ключ формируется строго из валидированных GUID и номера версии:
```
clients/{clientId}/backup-sets/{backupSetId}/files/{fileId}/versions/{versionNumber:D10}.bin
```
Пример:
```
clients/3fa85f64-5717-4562-b3fc-2c963f66afa6/backup-sets/a1b2c3d4-e5f6-7890-abcd-ef1234567890/files/b2c3d4e5-f6a7-8901-bcde-f0123456789a/versions/0000000001.bin
```
- Ключ всегда относительный;
- Использует стандартный протокольный разделитель `/`;
- Никогда не принимает произвольный путь извне от клиента.

### Корневая файловая структура (Storage Root):
По умолчанию настраивается в `appsettings.json`:
```
data/
└── storage/
    ├── .tmp/                               <-- Временная запись незавершенных загрузок ({OperationId}.part)
    └── clients/
        └── {clientId}/
            └── backup-sets/
                └── {backupSetId}/
                    └── files/
                        └── {fileId}/
                            └── versions/
                                └── 0000000001.bin  <-- Неизменяемые зафиксированные версии
```
Каталог `data/` добавлен в `.gitignore` для предотвращения попадания реальных резервных копий в Git.

### Потоковая передача данных и проверка целостности:
- Передача осуществляется порциями по **64 KiB** (`FileStream` с флагом `FileOptions.Asynchronous`).
- Исключены небезопасные методы (`File.ReadAllBytes`, аллокации гигабайтных массивов в RAM).
- В процессе записи файла во временный файл `.tmp/{OperationId}.part` инкрементально рассчитывается **SHA-256** контрольная сумма (`IncrementalHash.CreateHash(HashAlgorithmName.SHA256)`).
- Проверяется точное соответствие заявленному размеру (`ActualSize == ExpectedSize`) и хэшу (`ActualSha256 == ExpectedSha256`).
- При разрыве соединения или несоответствии хэша/размера временный файл немедленно удаляется, а финальная версия не создается.

### Атомарность, неизменяемость и идемпотентность:
1. **Атомарная фиксация:** Зафиксированный файл перемещается из `.tmp/{OpId}.part` в постоянный путь с помощью атомарной операции файловой системы `File.Move(tempPath, finalPath, overwrite: false)`.
2. **Неизменяемость (Immutability):** Существующий файл версии никогда молча не перезаписывается.
3. **Идемпотентность (Idempotency):**
   - Если файл версии уже существует и его размер и SHA-256 идентичны ожидаемым, операция завершается успешно с возвратом существующего `StorageKey` (временный файл удаляется).
   - Если файл версии существует, но его содержимое отличается, возвращается ошибка `STORAGE_CONFLICT`.

### Защита от Path Traversal:
Класс `StorageKeyHelper` гарантирует, что вычисленный абсолютный физический путь строго находится внутри разрешенного корня `Storage:RootPath`, блокируя любые попытки передачи `..`, абсолютных путей, дисковых букв Windows, UNC-путей и нулевых байтов `\0`.

### Внутренняя архитектура StorageService:
1. **`StorageServerWorker`**: Слушает порт 5002, принимает подключения TCP-клиентов и управляет жизненным циклом сессий.
2. **`StorageMessageDispatcher`**: Изолированный диспетчер конкретного TCP-соединения, отслеживающий состояние двухфазного рукопожатия (`StoreFile` -> `UploadFile`), защищающий от параллельного чередования кадров (no interleaving).
3. **`FileStorageService`**: Низкоуровневый сервис работы с дисковым хранилищем, временными файлами, хэшированием и атомарным перемещением.
4. **`StorageServiceClient`** (в `Backup.SyncGateway`): Клиентская абстракция для выполнения вызовов `Hello`, `StoreFileAsync` (streaming) и `GetStoredFileAsync` (streaming).
