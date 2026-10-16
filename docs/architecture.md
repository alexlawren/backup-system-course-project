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

---

## 7. Сквозная оркестрация резервного копирования (Change-set #4: End-to-End Orchestration)

### 7.1. Роль и архитектурные границы SyncGateway
`Backup.SyncGateway` выступает центральным координирующим шлюзом и оркестратором процессов синхронизации:
- **Клиент (`Backup.Client.Web`) подключается ИСКЛЮЧИТЕЛЬНО к `SyncGateway`**. Клиент не знает сетевых адресов и портов `MetadataService` и `StorageService`, не имеет прямого доступа к PostgreSQL.
- **`MetadataService` никогда не пишет физические файлы резервных копий.**
- **`StorageService` никогда не обращается к PostgreSQL и базе данных метаданных.**
- **Никаких распределенных транзакций (2PC):** система использует надежный поэтапный рабочий процесс (**Staged Workflow**):
  `PrepareVersion` $\rightarrow$ `StoreFile` (физическая запись) $\rightarrow$ `CommitVersion` (фиксация в БД).

---

### 7.2. Последовательная диаграмма сквозного резервного копирования (Sequence Diagram)

```
[Client.Web]                     [SyncGateway]                 [MetadataService]              [StorageService]
     |                                |                                |                             |
     |--- 1. FileChanged (Meta) ----->|                                |                             |
     |    (BackupSetId, RelPath,      |--- 2. PrepareVersion --------->|                             |
     |     Size, Sha256)              |                                |-- Проверка committed Sha -->|
     |                                |                                |-- Создание PendingOp ------>|
     |                                |<-- 3. PrepareVersionResult ----|                             |
     |                                |    (UploadRequired=true,       |                             |
     |                                |     OpId, ExpVersion)          |                             |
     |<-- 4. UploadRequired ----------|                                |                             |
     |                                |                                |                             |
     |--- 5. Binary UploadFile ------>|                                |                             |
     |    (Socket Stream)             |--- 6. StoreFileDescriptor ---------------------------------->|
     |                                |<-- 7. StoreFileReady ----------------------------------------|
     |                                |--- 8. Relay Binary Stream (Streaming Relay, 64 KiB buffer) ->|
     |                                |                                |                             |-- Streaming write to .tmp
     |                                |                                |                             |-- SHA-256 incremental verify
     |                                |                                |                             |-- Atomic File.Move
     |                                |<-- 9. StoreFileResult (StorageKey, ActualSize, ActualSha) ---|
     |                                |                                |                             |
     |                                |--- 10. CommitVersion --------->|                             |
     |                                |    (OpId, StorageKey,          |== BEGIN DB TRANSACTION ====>|
     |                                |     ActualSize, ActualSha)     |-- Валидация PendingOp ------>|
     |                                |                                |-- Создание FileVersion ----->|
     |                                |                                |-- Update File.CurrentVer --->|
     |                                |                                |-- Запись ChangeLog --------->|
     |                                |                                |== COMMIT DB TRANSACTION ===>|
     |                                |<-- 11. CommitVersionResult ----|                             |
     |<-- 12. UploadCompleted --------|    (Success=true, VerNum)      |                             |
     |    (Success, Version, Key)     |                                |                             |
```

---

### 7.3. Почему Storage выполняется ДО Commit метаданных
Если бы коммит метаданных выполнялся до физического сохранения файла на диске:
1. База данных объявила бы версию $N$ актуальной (`CurrentVersionNumber = N`).
2. При сбое сети, переполнении диска или аварии `StorageService` физический файл не был бы записан.
3. Система оказалась бы в критически некорректном состоянии: метаданные ссылаются на отсутствующий блоб, а восстановление файла невозможно.

Выполнение `StorageService.StoreFile` до `CommitVersion` гарантирует, что **к моменту фиксации версии в PostgreSQL физические байты уже надежно записаны на диск, их размер и SHA-256 проверены**.

---

### 7.4. Поведение при сбое и неопределенности коммита (Uncertain Commit & Orphan Blobs)
1. **При сбое StorageService:**
   - Gateway прерывает процесс и **НЕ вызывает** `CommitVersion`.
   - Клиент получает `UploadCompletedResponse` с `Success = false` и кодом ошибки хранилища.
   - Метаданные не изменяются, номер версии в БД не увеличивается.
2. **При тайм-ауте или сетевом сбое во время вызова `CommitVersion`:**
   - Gateway **НЕ удаляет** физический файл из хранилища.
   - Причина: при сетевом тайм-ауте невозможно достоверно знать, зафиксировал ли PostgreSQL транзакцию до разрыва TCP-сокета или нет. Удаление блоба может привести к безвозвратной потере данных.
   - Gateway предпринимает **до 3 повторных попыток (Retry)** с контролируемой задержкой через `Task.Delay`.
   - Если статус коммита остается неопределенным, клиенту возвращается ошибка `METADATA_COMMIT_UNCERTAIN`.
   - Поскольку путь `StorageKey` детерминирован, а версии неизменяемы, следующий сеанс синхронизации восстановит согласованность.

---

### 7.5. Идемпотентность CommitVersion в MetadataService
Для надежного повтора коммита сущность `FileVersionEntity` дополнена полем:
```csharp
public Guid OperationId { get; set; }
```
На уровне PostgreSQL создан **уникальный индекс** `IX_file_versions_OperationId`.

**Алгоритм обработки `CommitVersion`:**
1. Поиск существующей записи в `file_versions` по `OperationId`.
2. Если версия уже существует:
   - Проверяется совпадение `StorageKey`, `Size` и `Sha256`.
   - Если данные совпадают: возвращается `Success = true`, `AlreadyCommitted = true` (вторая версия не создается).
   - Если переданные параметры конфликтуют с уже зафиксированной версией: возвращается ошибка `COMMIT_CONFLICT`.
3. Если версия с таким `OperationId` отсутствует: выполняется стандартная фиксация транзакции с удалением записи из `pending_file_operations`.

---

### 7.6. Идентификация рабочей станции (Client Identity)
- Сервис `ClientIdentityService` управляет локальным состоянием рабочей станции в файле:
  `data/client/client-state.json`.
- Запись файла выполняется атомарно: запись во временный файл $\rightarrow$ атомарная замена `File.Move(..., overwrite: true)`.
- При первом запуске `ClientId` отсутствует. Клиент отправляет запрос `RegisterClient` в `SyncGateway`, который маршрутизирует его в `MetadataService`. Полученный GUID сохраняется локально.
- При последующих запусках сохраненный `ClientId` передается в заголовках и обновляет поле `LastSeenAtUtc` в базе данных.

---

### 7.7. Потоковый ретранслятор Gateway (Streaming Relay without Full-File Buffering)
Шлюз `SyncGateway` не буферизует файл целиком в оперативную память и не создает промежуточных копий на жестком диске шлюза.
Используется обертка `BoundedReadStream`, ограничивающая поток чтения ровно `header.BodyLength` байтами. Передача между клиентом и `StorageService` выполняется в потоковом режиме с ограниченным буфером в пространстве пользователя (64 KiB чанки), без сохранения файла на диск шлюза (streaming relay without full-file buffering or intermediate Gateway file). Это не является аппаратным OS zero-copy (так как данные проходят через ограниченный пользовательский буфер процесса), но обеспечивает минимальный и константный $O(1)$ расход оперативной памяти независимо от размера файла.

---

### 7.8. Сессии клиентов на шлюзе и отсутствие мультиплексирования
- Каждое входящее TCP-соединение обрабатывается отдельным экземпляром `GatewaySessionHandler` с изолированным состоянием `GatewayClientSession`.
- Состояние отслеживает активный контекст загрузки (`ActiveUploadContext`).
- Попытка отправить второй запрос `FileChanged` до завершения активной загрузки отклоняется с кодом `UPLOAD_IN_PROGRESS`.
- Попытка передать бинарный кадр `UploadFile` без предварительного `UploadRequired` отклоняется с кодом `UPLOAD_NOT_EXPECTED`.
- Параллельные клиенты обслуживаются независимо без взаимного влияния.

---

## 8. Change-set #5: Автоматическая синхронизация каталогов (Directory Sync & Watcher)

### 8.1. Клиентский конвейер синхронизации (Client Pipeline)
В Change-set #5 фокус смещен с ручной загрузки одиночного файла на непрерывное резервное копирование выбранного каталога:

```
[ Локальный каталог ]
         |
         v
[ FileSystemWatcherService ]
         | (легковесный захват событий без блокировок)
         v
[ Channel<InternalFileChangeEvent> ] (потокобезопасная асинхронная очередь)
         |
         v
[ Debounce & Coalescing Engine ] (~750 ms окно схлопывания событий)
         |
         v
[ FileStabilityHelper ] (проверка снятия файловых блокировок)
         |
         v
[ SyncEngine ] <====================> [ LocalFileIndexService ]
         |                                  (локальный кеш SHA-256)
         v
[ BackupGatewayClient ]
         | (TCP Protocol v1)
         v
   [ SyncGateway ]
```

### 8.2. Один активный профиль синхронизации в v1 (Single Active Profile)
Клиентская часть `Backup.Client.Web` поддерживает работу с **одним активно отслеживаемым BackupSet** одновременно.
- Профиль сохраняется в файле: `data/client/sync-profile.json`.
- Модель: `BackupSetId`, `Name`, `LocalRootPath`, `AutomaticSyncEnabled`.
- Персистентность: строго атомарная запись через запись во временный файл (`.tmp.{Guid}`) и атомарную замену `File.Move(temp, target, overwrite: true)`.
- Состояние профиля **не хранится** в `localStorage` браузера.

### 8.3. Локальный кеш индексов (Local File Index Cache)
Хранится в `data/client/index/{BackupSetId}.json`.
- Запись для каждого файла: `RelativePath`, `Size`, `LastWriteTimeUtc`, `Sha256`.
- Роль кеша: исключительно оптимизация клиентского сканирования; кеш **не является** авторитетным состоянием сервера.
- При сканировании директории:
  - Если `Size == cached.Size` и `LastWriteTimeUtc == cached.LastWriteTimeUtc`: повторно используется вычисленный ранее `Sha256`.
  - Иначе: выполняется потоковый пересчет `Sha256` через `FileStreamingHelper`.
- Устойчивость к сбоям: если кеш отсутствует или поврежден (битый JSON), он безопасно перестраивается с нуля без падения приложения.
- При удалении локального файла запись автоматически удаляется из индекса.

### 8.4. Сканер директорий и безопасность путей (Directory Scanner & Path Safety)
- Рекурсивно обходит `LocalRootPath` в потоковом режиме без загрузки тел файлов в память.
- Пустые директории в Protocol v1 не резервируются (резервируются только файлы).
- **Безопасность путей:**
  - Каждый относительный путь валидируется через `ProtocolPathValidator` (запрет `..`, обратных слешей, ведущих слешей, двоеточий, зарезервированных имен Windows).
  - Проверка принадлежности: канонический `Path.GetFullPath(file)` обязан начинаться с канонического `LocalRootPath`.
  - Защита от симлинков/reparse points: сканер не углубляется в поддиректории с атрибутом `FileAttributes.ReparsePoint` (junctions/symlinks), что предотвращает зацикливание и выход за пределы корня набора.

### 8.5. Согласование манифеста и построение плана (Manifest -> SyncPlan)
1. Клиент строит и отправляет кадр `ManifestRequest` (`MessageType.Manifest`):
   - `ClientId`, `BackupSetId`, `Files` (`RelativePath`, `Size`, `LastWriteTimeUtc`, `Sha256`).
2. Gateway запрашивает снимок сервера у `MetadataService` единым SQL-запросом (`GetFilesAsync` с `IncludeDeleted = true`), возвращающим текущую версию, размер и SHA-256 каждого файла без N+1 запросов.
3. Gateway сравнивает локальный манифест со снимком сервера по правилам:
   - LOCAL exists, SERVER missing $\rightarrow$ `UPLOAD NEW`
   - LOCAL exists, SERVER `IsDeleted == true` $\rightarrow$ `UPLOAD SERVER_DELETED`
   - LOCAL exists, SERVER active, hash differs $\rightarrow$ `UPLOAD CHANGED`
   - LOCAL exists, SERVER active, hash same $\rightarrow$ `UNCHANGED`
   - LOCAL missing, SERVER active $\rightarrow$ `DELETE`
   - LOCAL missing, SERVER already deleted $\rightarrow$ ничего не делать
4. Gateway возвращает клиенту кадр `SyncPlanResponse` (`MessageType.SyncPlan`), содержащий детерминированно отсортированные списки `Upload` и `Delete`, а также счетчик `UnchangedCount`.
5. Клиент последовательно исполняет план.

### 8.6. Алгоритм начальной реконсиляции (Initial Reconciliation)
Для предотвращения race condition между сканированием и стартом отслеживания:
1. Проверка существования `LocalRootPath`.
2. Запуск `FileSystemWatcher` в режиме накопления очереди (события, происходящие во время сканирования, уже попадают в канал).
3. Сканирование каталога и построение манифеста.
4. Отправка манифеста на Gateway и получение `SyncPlan`.
5. Последовательное выполнение Upload операций через `BackupGatewayClient.BackupFileAsync`.
6. Последовательное выполнение Delete операций через `BackupGatewayClient.DeleteFileAsync`.
7. Сохранение обновленного `LocalFileIndex`.
8. Переход в нормальный режим обработки наблюдателя: background loop вычитывает и обрабатывает события, накопившиеся во время начального сканирования.

### 8.7. Обработка событий наблюдателя и дебаунс (Debounce & Coalescing)
- FileSystemWatcher генерирует всплески событий на одно физическое сохранение файла.
- Background processor объединяет события по относительному пути с конфигурируемым окном ожидания (по умолчанию 750 мс):
  - Множественные `Changed` $\rightarrow$ один логический `Changed`.
  - `Created` + `Changed` $\rightarrow$ один логический `Created`.
  - `Renamed(old -> new)` + `Changed(new)` $\rightarrow$ сохранение как `Renamed`.
  - `Created` затем сразу `Deleted` до истечения таймера $\rightarrow$ полное удаление из очереди (upsert отменяется).
- **Файловые блокировки (Locks):** перед чтением файла `FileStabilityHelper` выполняет неблокирующие асинхронные попытки открытия потока (`FileAccess.Read`, `FileShare.Read`) с задержкой через `Task.Delay`. Если файл заблокирован внешним процессом/редактором дольше допустимого, операция откладывается и выставляется флаг `ReconciliationRequired = true` без падения сервиса.

### 8.8. Исправление семантики повторно созданных удаленных файлов (Reappearing Deleted File)
В `MetadataApplicationService.PrepareVersionAsync` исправлена критическая ошибка:
- Если файл имеет `IsDeleted == true` и локально создается заново с тем же содержимым (совпадающим SHA-256):
  - Сервис **обязан** вернуть `UploadRequired = true` (а не `false`).
  - Новая версия получает номер `CurrentVersionNumber + 1`.
  - При `CommitVersion` флаг сбрасывается в `IsDeleted = false`, а в `ChangeLog` фиксируется создание/модификация версии.

### 8.9. Потоки удаления и переименования (Delete & Rename Flows)
- **Удаление:** Клиент отправляет `FileDeletedRequest` $\rightarrow$ Gateway вызывает `DeleteFileMetadata` $\rightarrow$ файл помечается `IsDeleted = true`, в `ChangeLog` пишется `DELETE`. Исторические блобы в `StorageService` не удаляются. Запись удаляется из `LocalFileIndex`. Повторный DELETE обрабатывается идемпотентно.
- **Переименование:** Клиент отправляет `FileRenamedRequest` $\rightarrow$ Gateway вызывает `RenameFileMetadata`.
  - Физические файлы версий не перемещаются; `FileId` и история версий сохраняются.
  - Если `OldRelativePath` известен в локальном индексе: выполняется `RenameFileMetadata`.
  - Если старый путь неизвестен (например, временный файл редактора переименован в целевой файл): новый путь обрабатывается как обычный upsert.

### 8.10. Переполнение буфера наблюдателя и сбои сети
- При получении события `FileSystemWatcher.Error` (включая `InternalBufferOverflowException`) или сетевом сбое во время live-синхронизации:
  - Выставляется флаг `ReconciliationRequired = true`.
  - Приложение не падает.
  - При стабилизации соединения или по расписанию автоматически запускается полная реконсиляция по манифесту.

### 8.11. Ограничения Protocol v1
1. **Офлайн-переименование:** Если файл был переименован, пока клиент `Backup.Client.Web` был выключен, при последующем запуске начальная реконсиляция манифеста зафиксирует это как `DELETE old` + `UPLOAD new` (будет создан новый `FileId`). Живое переименование через работающий FileSystemWatcher сохраняет `FileId` и всю историю.
2. **Пустые директории:** Пустые каталоги не пересылаются и не резервируются в v1 (отслеживаются только файлы).

---

## 9. Архитектура восстановления версий и браузера файлов (Change-set #6)

### 9.1. Принцип неизменяемости версий (Version Immutability)
- В системе реализовано фундаментальное архитектурное правило: **версии файлов строго неизменяемы**.
- Операция Restore **НЕ откатывает** значение `CurrentVersionNumber` назад во времени.
- *Пример:* если файл имел версии v1, v2, v3 (текущая — v3), восстановление версии v1 приводит к созданию новой версии **v4**, физическое содержимое и SHA-256 которой идентичны версии v1. Поле `CurrentVersionNumber` становится равным 4.
- Все предшествующие записи в `file_versions` и физические блобы в `StorageService` сохраняются без изменений.

### 9.2. Фиксация источника изменений в БД (`PendingFileOperationEntity.OperationKind` и миграция)
- Чтобы признак операции восстановления не терялся между фазами `PrepareVersion` и `CommitVersion`, в таблицу `pending_file_operations` добавлена колонка `OperationKind` (`varchar(32)`, по умолчанию `"NORMAL"`).
- Создана миграция EF Core: `20261008111059_AddOperationKindToPendingOperations`.
- При вызове `PrepareVersionAsync` с признаком `ChangeOrigin == BackupChangeOrigin.Restore` сохраняется `OperationKind = "RESTORE"`.
- При последующем вызове `CommitVersionAsync`:
  - Если `pending.OperationKind == "RESTORE"`, в аудит-журнал `change_logs` записывается действие `Action = "RESTORE"` (вместо `"MODIFY"`).

### 9.3. Потоковая ретрансляция на SyncGateway (Streaming Relay with Bounded Buffer)
- SyncGateway выступает посредником между клиентом и микросервисами, полностью изолируя PostgreSQL и физическое файловое хранилище.
- При скачивании восстанавливаемой версии Gateway:
  - Разрешает ключ хранилища `StorageKey` через `MetadataService.ResolveVersion`;
  - Запрашивает поток у `StorageService.GetStoredFile`;
  - Отправляет клиенту дескриптор `RestoreReady` и бинарный заголовок `RestoreFile`;
  - Ретранслирует бинарное тело напрямую из сокета хранилища в сокет клиента с использованием ограниченного буфера пользователя (64 KiB).
- **Инварианты шлюза:**
  - Gateway **НЕ** читает весь файл в `byte[]`;
  - Gateway **НЕ** создает полный `MemoryStream`;
  - Gateway **НЕ** создает промежуточных временных файлов на своем диске.

### 9.4. Безопасность на стороне клиента (Safety Invariants)
1. **Безопасность целевого пути:**
   - Серверный `RelativePath` валидируется через `ProtocolPathValidator.TryNormalizeAndValidate`;
   - Проверяется канонический путь `Path.GetFullPath`: целевой путь обязан строго находиться внутри `LocalRootPath` (защита от path traversal, абсолютных путей, UNC и дисковых префиксов).
2. **Воссоздание родительских каталогов:**
   - Если промежуточные родительские папки файла были удалены, клиент безопасно воссоздает их перед записью файла.
3. **Временный файл в том же каталоге:**
   - Поток пишется в `<path>.restore.<Guid>.tmp` в той же директории, что гарантирует нахождение временного и целевого файла на одной файловой системе для атомарной замены.
4. **Инкрементальный SHA-256 и размер:**
   - В процессе скачивания подсчитывается контрольная сумма. Если итоговый размер или хэш не совпали с дескриптором `RestoreReady`: temp немедленно удаляется, исходный файл пользователя не изменяется, возвращается ошибка `RESTORE_SIZE_MISMATCH` или `HASH_MISMATCH`.
5. **Атомарная замена (Atomic Replace):**
   - Файл заменяется через `File.Move(temp, target, overwrite: true)`. При занятости файла сторонним процессом выполняются ограниченные асинхронные попытки с паузой; при исчерпании лимита возвращается `RESTORE_TARGET_LOCKED` без повреждения целевого файла.
6. **Защита несохраненных пользовательских правок (Pre-Backup Safety):**
   - Если локальный файл существует перед восстановлением, клиент рассчитывает его текущий SHA-256 и запускает обычный backup-поток;
   - Если локальный файл содержал несохраненные правки, сервер сначала фиксирует их как нормальную версию `v_edit` (например, v4);
   - Если пред-бэкап завершился ошибкой, локальная перезапись **НЕ выполняется**;
   - После успешного пред-бэкапа скачивается историческая версия и коммитится как `v_restore` (например, v5).
7. **Восстановление логически удаленного файла:**
   - Поддерживается восстановление файлов с `FileEntity.IsDeleted == true`: файл создается на диске, в БД сбрасывается флаг `IsDeleted = false`, инкрементируется версия и пишется `RESTORE` в `ChangeLog`.

### 9.5. Подавление эхо-событий наблюдателя (`RestoreSuppressionService`)
- Чтобы FileSystemWatcher не создавал паразитных дублирующих версий при атомарной замене файла, разработан `RestoreSuppressionService`.
- Супрессия связывается с тройкой: `(RelativePath, ExpectedSha256, ExpiresAtUtc)`.
- **Алгоритм фильтрации:**
  - При возникновении события от наблюдателя на подавляемый путь событие ожидает дебаунса;
  - Рассчитывается фактический SHA-256 текущего состояния файла;
  - Если хэш совпадает с `ExpectedSha256` восстановленной версии, событие квалифицируется как эхо восстановления и подавляется;
  - Если хэш отличается (пользователь отредактировал файл сразу после восстановления), событие **НЕ подавляется** и обрабатывается как новое пользовательское изменение.

### 9.6. Координация потоков и сетевой сокет
- **Сериализация TCP-сокета (`_connectionLock`):**
  - Поскольку Protocol v1 не поддерживает мультиплексирование, в `BackupGatewayClient` внедрен `SemaphoreSlim(1, 1)`, гарантирующий атомарность циклов запрос-ответ при одновременных обращениях фонового цикла наблюдателя и пользовательского интерфейса восстановления.
- **Пофайловая блокировка (`AsyncPathLock`):**
  - Операция восстановления и обработка событий наблюдателя по одному относительному пути взаимно исключают друг друга.

### 9.7. Веб-интерфейс пользователя (Client.Web UI)
- Клиентский интерфейс работает на Blazor Server (`http://localhost:5100`) и обращается к серверу исключительно через TCP Protocol v1 шлюза `Backup.SyncGateway`:
  1. `/`: Интерактивный дашборд синхронизации с мониторингом статуса шлюза (`Online` / `Offline`), движка синхронизации (`Running` / `Stopped`), реконсиляции (`Synced` / `Required` / `Running`), общего числа файлов на сервере и последних событий;
  2. `/files`: Браузер файлов активного набора резервного копирования (`RelativePath`, `CurrentVersionNumber`, статус `Active` / `Deleted`, `Size`, `UpdatedAtUtc`);
  3. `/files/{fileId:guid}`: Просмотр ревизий файла (`VersionNumber`, `Size`, `Sha256`, `CreatedAtUtc`, бейдж `Current`), кнопка `[Restore]`, модальное окно подтверждения с предупреждением о сохранении локальных правок и карточка результата восстановления;
  4. `/history`: Журнал аудита с интерактивным фильтром по типу действий (`ALL`, `CREATE`, `MODIFY`, `DELETE`, `RENAME`, `RESTORE`), статусом `COMMITTED`, метками времени и деталями.

---

## 10. Эксплуатационная надежность и отказоустойчивость (Change-set #7: Final Hardening)

### 10.1. Жизненный цикл переподключения клиента (Client -> Gateway Reconnect Lifecycle)
- **Обнаружение потери соединения:**
  Любой сбой сокета (`SocketException`, `IOException`, `EndOfStreamException` при нулевом возврате `ReadAsync`) переводит клиентский компонент `BackupGatewayClient` в отключенное состояние через вызов `MarkDisconnected()`.
- **Очистка ресурсов:**
  Поврежденный `TcpClient` и связанный `NetworkStream` немедленно закрываются и освобождаются (`Dispose`).
- **Восстановление соединения с Backoff:**
  При следующей пользовательской операции либо по таймеру фонового опроса вызывается `EnsureConnectedAsync`. Механизм применяет ограниченный экспоненциальный backoff:
  ```
  500 ms -> 1 000 ms -> 2 000 ms -> 5 000 ms (bounded retry)
  ```
- **Восстановление логической сессии:**
  После успешного открытия сокета клиент отправляет `MessageType.Hello` с сохранением активного `ClientId`.
- **Запуск реконсиляции:**
  Событие `BackupGatewayClient.Reconnected` переводит `SyncEngine` в состояние:
  `ReconciliationRequired = true`.
  При следующей итерации или вызове `ReconcileAsync()` локальное состояние папки полностью синхронизируется со снимком сервера.

---

### 10.2. Изоляция сбоев подчиненных сервисов на шлюзе (Downstream Failure Isolation)
Шлюз `SyncGateway` спроектирован так, чтобы временная недоступность микросервисов метаданных или хранилища не приводила к аварийному завершению процесса Gateway:
- Клиенты `MetadataServiceClient` и `StorageServiceClient` реализуют метод `Disconnect()`, сбрасывающий TCP-подключение при возникновении сбоя сетевого потока;
- При ошибке связи с `MetadataService` клиент получает структурированный ответ с кодом `METADATA_UNAVAILABLE`;
- При ошибке связи со `StorageService` клиент получает структурированный ответ с кодом `STORAGE_UNAVAILABLE`;
- При поступлении следующего запроса шлюз автоматически пытается переустановить downstream TCP-соединение без перезапуска процесса.

---

### 10.3. Корректное мягкое завершение процессов (Graceful Shutdown)
Все четыре процесса системы поддерживают мягкое завершение по сигналам ОС (Ctrl+C, `SIGINT`, `SIGTERM`):
- `CancellationTokenSource` переводится в отмененное состояние;
- `TcpListener.Stop()` прекращает прием новых входящих подключений;
- Фоновые циклы приема (`AcceptTcpClientAsync`) корректно завершают работу без необработанных исключений;
- Активные клиентские сокеты закрываются с корректным сбросом сетевых буферов;
- Очереди событий наблюдателя (`Channel<InternalFileChangeEvent>`) завершаются вызовом `Writer.Complete()`;
- Фоновые процессоры завершают обработку текущих элементов очереди;
- Открытые файловые потоки `FileStream` сбрасывают данные (`FlushAsync`) и освобождаются;
- Фабрики контекстов EF Core `MetadataDbContext` гарантируют отсутствие зависших транзакций.

---

### 10.4. Очистка устаревших транзакций метаданных (`PendingOperationsCleanupWorker`)
В `Backup.MetadataService` внедрен фоновый сервис `PendingOperationsCleanupWorker`:
- Запускается с настраиваемым интервалом (по умолчанию 5 минут);
- Находит и удаляет записи из таблицы `pending_file_operations`, для которых:
  `ExpiresAtUtc < DateTime.UtcNow`;
- **Критический инвариант:**
  Воркер удаляет **исключительно** устаревшие бронирования транзакций;
  Воркер **НИКОГДА** не удаляет записи из таблицы `file_versions` и не изменяет значение `CurrentVersionNumber`.

---

### 10.5. Очистка временных файлов физического хранилища (`StorageTempCleanupWorker`)
В `Backup.StorageService` внедрен фоновый сервис `StorageTempCleanupWorker`:
- Выполняет очистку при старте сервиса и периодически в фоновом режиме (по умолчанию раз в 1 час);
- Удаляет временные файлы из каталога:
  `data/storage/.tmp/*.part`,
  время последней модификации которых старше настраиваемого порога (по умолчанию 1 час);
- **Критический инвариант:**
  Воркер сканирует исключительно директорию `.tmp`;
  Воркер **НИКОГДА** не затрагивает каталог зафиксированных неизменяемых версий `clients/.../versions/`.

---

### 10.6. Неразрушающий контроль согласованности (`StorageConsistencyVerifier`)
Для демонстрации целостности системы разработан диагностический верификатор `StorageConsistencyVerifier`:
- Запрашивает все зафиксированные сущности `FileVersionEntity` из PostgreSQL;
- Проверяет физическое существование каждого соответствующего блоба на диске по ключу `StorageKey`;
- Формирует структурированный отчет:
  `TotalCommittedVersions`, `ValidBlobsCount`, `MissingBlobsCount`, список путей отсутствующих блобов;
- **Инвариант безопасности:**
  Верификатор носит исключительно диагностический характер и не производит никаких деструктивных действий (удалений или перезаписи).

---

### 10.7. Изоляция нескольких рабочих станций (Multiple Clients)
Архитектура v1.1 полностью поддерживает одновременное подключение нескольких независимых клиентов к единому экземпляру `SyncGateway`:
- Каждое TCP-подключение изолировано в собственном контексте `GatewaySessionHandler`;
- Состояния сессий (`ActiveUploadContext`, `ActiveClientId`) не смешиваются;
- Метаданные изолированы внешними ключами `ClientId` и `BackupSetId`;
- Физические файлы версий изолированы уникальными путями `StorageKey` вида:
  `clients/{clientId}/backup-sets/{backupSetId}/files/{fileId}/versions/{version:D10}.bin`;
- Сетевой или аппаратный сбой Клиента А не влияет на непрерывность синхронизации Клиента Б.

---

## 11. Стабилизация сессий и локализация интерфейса (Release Fix #1)

### 11.1. Анализ корневой причины сбоя кадрирования (Framing Corruption Bug)
В ходе интеграционного тестирования был зафиксирован runtime-сбой:
```text
Invalid frame framing: HeaderLength (2065855593 bytes) exceeds maximum allowed limit of 65536 bytes (64 KiB).
```
**Точная причина (Root Cause):**
1. Число `2065855593` в 16-ричном представлении равно `0x7B227469`.
2. В кодировке ASCII/UTF-8 эти байты соответствуют строке `{"ti`, то есть началу JSON-полезной нагрузки `{"timestamp":...}`.
3. Метод `SyncGateway.HandlePingAsync` отправлял в ответ кадр `MessageType.Pong`, содержащий сериализованный JSON-объект `PingMessage` (длина тела `BodyLength > 0`).
4. Клиентский метод `BackupGatewayClient.PingAsync` вызывал `ReadHeaderAsync`, проверял тип `MessageType.Pong`, но **не вычитывал** полезную нагрузку из сокета, если `respHeader.BodyLength > 0`.
5. Тело ответа `Pong` оставалось в буфере `NetworkStream`.
6. При последующем запросе (например, `RegisterClient`) метод `ReadHeaderAsync` читал первые 4 байта из сокета, которыми оказывались оставшиеся байты `{"ti`, и интерпретировал их как длину заголовка `HeaderLength = 2065855593`.
7. Срабатывала строгая защита протокола `HeaderLength <= 64 KiB`.

### 11.2. Соблюдаемые инварианты параллелизма (Concurrency Invariants)
В соответствии с ограничениями Protocol v1 (отсутствие мультиплексирования в рамках одного TCP-сокета):
1. **Строгая последовательность запросов (Strict Request-Response Serialization):**
   - Все операции с сокетом `_stream` внутри `BackupGatewayClient` защищены общим `SemaphoreSlim _connectionLock = new(1, 1)`;
   - Захват блокировки охватывает **весь цикл** от отправки кадра запроса до полного считывания/дренирования ответа;
2. **Гарантированное дренирование потока (Body Drain Invariant):**
   - Каждый обработчик ответа (включая `PingAsync` и `ConnectInternalAsync`) обязан вычитать ровно `respHeader.BodyLength` байт, если `BodyLength > 0`;
3. **Безопасная обработка сбоев сокета (Fail-Safe Disconnect):**
   - При возникновении любого исключения во время чтения или записи сокет помечается как разорванный (`MarkDisconnected()`), закрывая сетевой поток и предотвращая повторное использование сокета с нарушенными границами кадра;
   - Следующий запрос автоматически выполняет новое TCP-подключение и рукопожатие `Hello`.

### 11.3. Единый запуск и оркестрация (One-Command Startup)
Для удобства локального запуска разработаны сценарии PowerShell:
- **`start.ps1`**: проверяет .NET 9 SDK, доступность PostgreSQL 127.0.0.1:5432, занятость портов (5000, 5001, 5002, 5100), компилирует решение, запускает 4 независимых процесса с перенаправлением вывода в `data/logs/current/`, проверяет readiness каждого порта по TCP и сохраняет манифест PID в `data/run/processes.json`.
- **`stop.ps1`**: читает манифест PID, останавливает ровно те процессы, которые были запущены `start.ps1`, и очищает манифест, не затрагивая внешнюю базу данных.
- **`test.ps1`**: запускает полный цикл `dotnet restore -> dotnet build -> dotnet test` (181 тест в зеленой зоне).

### 11.4. Русификация пользовательского интерфейса
Все видимые элементы пользовательского интерфейса Blazor переведены на русский язык:
- Панели мониторинга, карточки статусов, навигация;
- Блок пошаговых инструкций «Быстрый старт»;
- Модальные окна подтверждения восстановления версий;
- Представление журнала аудита (`Создание`, `Изменение`, `Переименование`, `Удаление`, `Восстановление`);
- Дружественные сообщения об ошибках без отображения сырых системных исключений.

---

## 12. Архитектура общего хранилища (Shared Storage Architecture v1.2)

### 12.1. Концепция и модель общего набора резервного копирования
В рамках Change-set #1 архитектура системы расширена поддержкой общих наборов резервного копирования (`Shared BackupSet`), к которым могут подключаться несколько рабочих станций:
- **`AccessMode`**: режим доступа к набору (`PRIVATE` или `SHARED`). По умолчанию все наборы создаются в режиме `PRIVATE`.
- **`ShareCode`**: уникальный 8-значный буквенно-цифровой код доступа в формате `XXXX-XXXX`, генерируемый исключительно для наборов в режиме `SHARED`.
  - Алфавит состоит из 31 однозначного символа (`ABCDEFGHJKMNPQRSTUVWXYZ23456789`), исключая визуально похожие знаки (`0`, `O`, `1`, `I`, `L`);
  - Генерация осуществляется с помощью криптографически стойкого генератора `System.Security.Cryptography.RandomNumberGenerator`;
  - Валидация и нормализация кода (`ShareCodeHelper`) устойчива к регистру, пробелам и дефисам (например, `"7h4k 92qx"` нормализуется в `"7H4K-92QX"`).

### 12.2. Членство и роли (`backup_set_members`)
Таблица `backup_set_members` связывает наборы с рабочими станциями участников:
- Составной первичный ключ: `(BackupSetId, ClientId)`;
- Внешние ключи с каскадным удалением к `backup_sets` и `clients`;
- Роли:
  - `OWNER`: создатель набора (автоматически создается при вызове `CreateBackupSet`);
  - `MEMBER`: рабочая станция, подключившаяся по коду `ShareCode` (через операцию `JoinSharedBackupSet`).

### 12.3. Инвариант пространства имен хранилища (Storage Namespace Invariant)
Ключевым архитектурным решением является изоляция и неизменность физического пути хранения блобов:
- Физические файлы версий всегда размещаются в пространстве имен **владельца** (`OwnerClientId`):
  ```text
  clients/{OwnerClientId}/backup-sets/{backupSetId}/files/{fileId}/versions/{version:D10}.bin
  ```
- Даже если новую версию файла загружает участник B (`MEMBER`), физический ключ хранения формируется от идентификатора владельца A (`OWNER`). Это исключает дублирование и фрагментацию пространства имен между участниками.

### 12.4. Аудит авторства в журнале изменений (`ActorClientId`)
Журнал изменений `ChangeLogEntity` дополнен атрибутами авторства:
- `ActorClientId` (FK -> `clients`): идентификатор фактической рабочей станции, выполнившей операцию (`CREATE`, `MODIFY`, `DELETE`, `RENAME`, `RESTORE`);
- `ActorClient`: навигационное свойство для получения сетевого имени клиента без дополнительных запросов;
- `PendingFileOperationEntity` также сохраняет `ActorClientId` между фазами `PrepareVersion` и `CommitVersion`.

### 12.5. Серверный контроль доступа (`EnsureBackupSetAccessAsync`)
В `MetadataApplicationService` реализован централизованный шлюз авторизации:
- Запросы на подготовку версий, фиксацию, переименование, удаление и чтение метаданных проверяют наличие прав вызывающей рабочей станции (`ClientId` / `ActorClientId`);
- Доступ предоставляется только в случае, если клиент является `OWNER` или активным `MEMBER` набора;
- При попытке доступа неавторизованного клиента возвращается стандартная протокольная ошибка `ACCESS_DENIED`.

---

## 13. Двусторонняя синхронизация общего хранилища (Shared Bidirectional Synchronization v1.3)

### 13.1. Архитектурный принцип
В режиме `SHARED` топология синхронизации расширяется от однонаправленной `LOCAL → SERVER` до двунаправленной `LOCAL ↔ SERVER`. Сервер выступает единственным авторитетным источником единой истории версий набора. Для предотвращения ложного удаления серверных файлов при подключении нового клиента reconciliation для `SHARED` набора всегда начинается с получения актуального серверного манифеста.

### 13.2. Фазы двусторонней сверки (Reconciliation Pipeline)
Алгоритм `ExecuteSharedReconciliationInternalAsync` состоит из 7 строго последовательных фаз:

1. **PHASE 1: Fetch Server Manifest (`GetBackupSetManifest`)**
   - Клиент запрашивает актуальный снимок файлов набора на сервере;
   - Запрос защищен авторизацией (только `OWNER` и `MEMBER`, неавторизованным клиентам возвращается `ACCESS_DENIED`);
   - Ответ содержит список записей `BackupSetManifestEntryDto`, включая активные файлы и надгробия (`IsDeleted = true`).

2. **PHASE 2: Scan Local Directory**
   - Локальный сканер обходит рабочий каталог набора;
   - Сопоставляет файлы с локальным индексом `LocalFileIndex`, вычисляет хэши измененных файлов и формирует снимок `currentLocalFiles`.

3. **PHASE 3: Classify Changes**
   - Сопоставление серверного манифеста и локального снимка:
     - **Remote Delete**: файл помечен удаленным на сервере, локальный хэш совпадает с `LastSyncedSha256` -> запланировано локальное удаление; если файл локально модифицирован -> `RemoteDeleteLocalModify` (`ConflictPending`);
     - **Remote Rename**: `FileId` перемещен на сервере, локальный файл не модифицирован и целевой путь свободен -> запланировано локальное перемещение; если целевой путь занят -> `RemoteRenameDestinationOccupied` (`ConflictPending`);
     - **Remote Create / Download**: файл отсутствует локально. Если локальный индекс не содержит файл или серверная версия новее `LastSyncedServerVersion` (а локальный файл не менялся) -> запланировано скачивание;
     - **Local Delete**: файл отсутствует на диске, но локальный индекс помнит его при совпадающей версии сервера -> откладывается до фазы 6 для безопасной отправки на сервер.

4. **PHASE 4: Apply Safe Remote Changes**
   - **4a. Renames**: локальный `File.Move`, регистрация подавления эхо в `RestoreSuppressionService`, обновление индекса;
   - **4b. Deletes**: локальное удаление `File.Delete`, регистрация подавления эхо в `RestoreSuppressionService`, удаление из индекса;
   - **4c. Downloads**: потоковое скачивание через `DownloadSharedFileVersionAsync` во временный файл `filename.sharedsync.{Guid}.tmp`, валидация фактического размера и SHA-256, атомарная замена `File.Move`, регистрация подавления эхо.

5. **PHASE 5: Rescan / Update Local Index**
   - Повторное сканирование каталога для учета примененных удаленных файлов и обновленного локального состояния.

6. **PHASE 6: Push Safe Local Changes**
   - **Local Uploads**: файлы, созданные или измененные локально без конфликтов, загружаются через стандартный конвейер `BackupFileAsync` (`Prepare -> Store -> Commit`);
   - **Local Deletes**: файлы, отсутствующие на диске при неизменной версии сервера, отправляются на сервер через `DeleteFileAsync`.

7. **PHASE 7: Persist Final LocalFileIndex & Conflict Status**
   - Атомарное сохранение `LocalFileIndex` на диск;
   - Обновление статуса: при наличии `ActiveConflicts` выставляется `ConflictPending`, блокируя ложный статус «Синхронизировано».

### 13.3. Потоковое чтение версий (Read-Only Streaming Download)
Для удаленной синхронизации используется выделенный конвейер чтения, полностью отделенный от бизнес-логики `Restore`:
- Сообщения: `DownloadFileVersion` -> `DownloadFileVersionReady` -> `DownloadFileVersionData` (бинарный фрейм);
- Шлюз `SyncGateway` выполняет проверку членства клиента и принадлежности `FileId` указанному `BackupSetId`;
- Передача осуществляется сквозным релеем `StorageService -> SyncGateway -> Client` без буферизации файла целиком в RAM;
- Операция не увеличивает счетчик версий и не создает записей в `ChangeLog`.

---

## 14. Стратегия разрешения конфликтов (Change-set #3: Conflict Copies + Hardening)

### 14.1. Основной принцип разрешения конфликтов
Для совместного хранилища зафиксирована строгая детерминированная стратегия без слияния содержимого (content merge), без Last-Writer-Wins и без распределенных блокировок:

```
SERVER VERSION WINS CANONICAL PATH
LOCAL DIVERGENT VERSION BECOMES CONFLICT COPY
```

Если клиент обнаруживает расхождение локального файла с серверным манифестом (divergence):
1. Текущая серверная версия скачивается и занимает **канонический путь** файла (`report.txt`);
2. Локальная расходящаяся редакция клиента физически сохраняется как отдельный **файл конфликтной копии** (`report (conflict CLIENT-B 2026-10-16 142355).txt`);
3. Конфликтная копия загружается на сервер как **новый независимый логический файл** (`FileId != canonical FileId`, начальная версия `v1`);
4. Все участники общего набора (`SHARED`) при последующей сверке получают конфликтную копию;
5. Обе редакции остаются сохранены в полном объеме, исключая любую потерю данных;
6. После разрешения конфликта статус сверки обоих клиентов сходится к **`Synced`** (без вечного `ConflictPending`).

### 14.2. Именование конфликтных копий (`ConflictFileNameHelper`)
Формирование имен конфликтных копий централизовано в хелпере `ConflictFileNameHelper`:
- Шаблон: `{ИмяБезРасширения} (conflict {MachineName} {yyyy-MM-dd HHmmss}){Расширение}`;
- Имя хоста (`MachineName`) очищается от недопустимых символов путей Windows/POSIX, пробелы заменяются на `_`;
- Временная метка формируется в фиксированном формате `yyyy-MM-dd HHmmss` (UTC);
- Поддерживаются составные расширения (например, `archive.tar.gz` -> `archive (conflict DESKTOP-B 2026-10-16 142355).tar.gz`) и файлы без расширения (`README` -> `README (conflict DESKTOP-B 2026-10-16 142355)`);
- **Коллизии имен:** при наличии существующего файла с аналогичным именем автоматически добавляется инкрементальный суффикс: `(2)`, `(3)`, ... без перезаписи существующих файлов;
- **Идемпотентность:** если в каталоге уже присутствует конфликтная копия с идентичной контрольной суммой SHA-256 (например, после сбоя сетевой отправки), генератор повторно использует существующий файл, предотвращая размножение копий при ретраях.

### 14.3. Классификация и сценарии разрешения

| Сценарий расхождения | Состояние сервера | Локальное состояние клиента | Алгоритм разрешения |
| :--- | :--- | :--- | :--- |
| **Remote Modify + Local Modify** | Файл обновлен до v(N+1) | Файл изменен локально от базы v(N) | Локальный файл переносится в conflict copy; канонический путь обновляется версией сервера v(N+1); conflict copy загружается как новый `FileId` v1. |
| **Server Delete + Local Modify** | Файл удален (`IsDeleted = true`) | Файл изменен локально | Локальный файл переносится в conflict copy; канонический путь локально удаляется (надгробие сервера сохраняется); conflict copy загружается на сервер. |
| **Remote Create Collision** | Сервер создал файл с `FileId X` | Локальный несинхронизированный файл с тем же путем и иным хэшем | Локальный файл сохраняется как conflict copy; серверная версия скачивается в канонический путь; conflict copy загружается как отдельный файл. |
| **Remote Rename Destination Occupied** | Сервер переименовал файл в `target` | У клиента путь `target` уже занят независимым файлом | Занятый локальный `target` переносится в conflict copy; серверное переименование применяется к каноническому файлу; conflict copy загружается. |
| **Remote Rename Source Modified** | Сервер переименовал `old` -> `new` | У клиента локальный `old` был модифицирован | Локальный модифицированный `old` сохраняется как conflict copy в исходном каталоге; серверная версия скачивается в `new`; conflict copy загружается. |
| **Remote Modify + Local Delete** | Сервер обновил файл до новой версии | Локальный файл удален клиентом | Серверная версия выигрывает канонический путь; файл скачивается с сервера; устаревшая запись локального индекса удаляется. |

### 14.4. Отказоустойчивость и порядок операций (Failure Safety)
Порядок операций исключает потерю локальных данных при любых сетевых сбоях:
1. Ожидание стабильности локального файла (`WaitForFileStabilityAsync`);
2. Вычисление SHA-256 и размера локального файла;
3. Подавление эхо-событий в `RestoreSuppressionService` для путей канонического файла и конфликтной копии;
4. **Физическое перемещение** (`File.Move`) расходящегося файла в путь конфликтной копии и подтверждение наличия файла на диске;
5. Замена канонического пути актуальным состоянием сервера и сохранение `LocalFileIndex`;
6. Загрузка конфликтной копии на сервер через `BackupFileAsync` (`ChangeOrigin = BackupChangeOrigin.ConflictCopy`);
7. При успешной загрузке — регистрация нового `FileId` и версии 1 в `LocalFileIndex`, удаление конфликта из `_activeConflicts` и перемещение в `_resolvedConflicts`;
8. При ошибке сети во время шага 6 — конфликтная копия **остается на диске**, флаг `ReconciliationRequired = true`. При следующей сверке файл распознается как локальный `CREATE` и досылается на сервер.

### 14.5. Аудит и авторство в ChangeLog
При фиксации конфликтной копии в `MetadataService` формируется запись в таблице `change_logs`:
- **`Action`**: `"CONFLICT_COPY"`;
- **`ActorClientId`**: идентификатор клиента, чья локальная редакция была сохранена;
- **`Details`**: `Conflict with {originalPath} v{remoteVersion}; local content preserved as '{conflictCopyPath}'`;
- Исходный канонический файл сохраняет свою непрерывную историю версий (`v1, v2, ...`), а конфликтная копия начинает собственную историю с `v1`.

### 14.6. Изоляция приватных наборов (PRIVATE)
Логика создания конфликтных копий действует исключительно для профилей с `AccessMode == BackupSetAccessMode.Shared`. Приватные наборы (`PRIVATE`) обслуживаются стандартным конвейером однопользовательской сверки без накладных расходов.

---

## 15. Автоматическая фоновая сверка общих наборов (Change-set #4: Automatic Shared Synchronization + Final UX)

### 15.1. Архитектура фонового опроса (Polling Loop)
В распределенной среде с несколькими клиентами изменения, вносимые одним клиентом, должны автоматически отражаться на остальных клиентах без необходимости ручного нажатия «Синхронизировать сейчас».

В соответствии с архитектурными ограничениями курсового проекта система не использует сторонние шины сообщений (RabbitMQ, Kafka) или WebSocket/SignalR, а реализует периодический легковесный опрос серверного манифеста на уровне `SyncEngine`:

```
+---------------------------------------------------------------------------+
|                              SyncEngine                                   |
|                                                                           |
|   LOCAL CHANGES:                                                          |
|   FileSystemWatcher ---> Debounce (750ms) ---> Immediate Safe Upload     |
|                                                                           |
|   REMOTE CHANGES:                                                         |
|   Periodic Loop ---> Delay(SharedSyncInterval) ---> Manifest Poll --->    |
|                      Safe Remote Apply & Index Update                     |
+---------------------------------------------------------------------------+
```

### 15.2. Параметры конфигурации (`SharedSyncIntervalSeconds`)
Интервал проверки задается через конфигурационный класс `SyncEngineOptions`:
- **По умолчанию (Production):** `5` секунд;
- **Демонстрационный скрипт (`start-shared-demo.ps1`):** `3` секунды (для быстрой наглядной реакции интерфейсов);
- **Автоматические тесты (`Backup.Tests`):** `100–300` мс (через свойство `SharedSyncInterval` в `ISyncEngine`), что обеспечивает быстрое прохождение тестов без блокировок.

### 15.3. Жизненный цикл фонового цикла (Lifecycle Management)
Фоновый цикл управляется строго атомарно через `CancellationTokenSource`:
1. **Запуск цикла (`EnsureSharedPollingLoopStarted`):**
   - При вызове `StartAsync` для профиля с `AccessMode == Shared`;
   - После успешного переключения на общий профиль (`SwitchProfileAsync`);
   - После успешного восстановления связи с шлюзом (`OnGatewayReconnected`), если синхронизация находилась в состоянии `Running`.
2. **Остановка цикла (`StopSharedPollingLoopAsync`):**
   - При вызове `StopAsync`;
   - При переключении на приватный профиль (`PRIVATE`);
   - При смене активного профиля;
   - При завершении работы приложения / освобождении ресурсов (`Dispose`, `DisposeAsync`).
3. **Предотвращение дублирования (Idempotency):**
   - Внутренний объект блокировки `_sharedPollingLoopLock` и проверка существования незавершенной `Task` гарантируют, что повторные вызовы `StartAsync` не создают параллельных циклов-сирот.

### 15.4. Предотвращение наложения сверок (Non-Overlapping Execution)
Каждая итерация фонового цикла выполняет неблокирующий захват общего семафора синхронизации `_syncLock.WaitAsync(0, cancellationToken)`:
- Если в данный момент выполняется локальная выгрузка по событию `FileSystemWatcher` или пользователь нажал кнопку «Синхронизировать сейчас», семафор занят;
- Фоновый цикл **пропускает данную итерацию (skip tick)** без блокировки и без создания очереди отложенных задач;
- Наложение параллельных сверок исключено.

### 15.5. Оптимизация пустого цикла (Zero-Change Poll / No-op)
Если серверный манифест идентичен текущему локальному индексу `LocalFileIndex` и состоянию диска:
- Файлы не скачиваются и не загружаются;
- Новые версии (`FileVersion`) не создаются;
- Журнал изменений (`change_logs`) не пополняется;
- Временные метки физических файлов не изменяются;
- Опрос является сверхлегкой проверкой по метаданным (`FileId`, `CurrentVersionNumber`, `RelativePath`, `IsDeleted`, `Sha256`).

### 15.6. Устойчивость к сетевым сбоям (Resilience & Auto Catch-up)
При временной недоступности шлюза (`SyncGateway` offline):
- Фоновый воркер перехватывает сетевые исключения и **не завершается аварийно**;
- Клиент переходит в режим ожидания переподключения (backoff reconnect);
- В пользовательском интерфейсе отображается статус `Нет соединения` / `Требуется синхронизация`;
- После восстановления шлюза фоновый цикл или событие реконнекта автоматически догоняет накопленные изменения и переводит систему в статус `Синхронизировано`.

### 15.7. Распространение конфликтных копий
При возникновении конфликта клиент-инициатор физически сохраняет локальную версию в файл конфликтной копии и загружает её на сервер как независимый файл. Все остальные клиенты общего набора автоматически получают файл конфликтной копии в ходе следующей итерации фонового опроса без каких-либо ручных действий.

### 15.8. Финальные доработки Web UI
- **Идентификация клиента:** компактный блок «Текущий клиент» на главной странице с отображением имени компьютера (`MachineName`), сокращенного ClientId (`7f19c840...`), всплывающей подсказки и кнопки копирования полного GUID;
- **Индикация общего набора:** статус автосинхронизации, заданный интервал проверки (в секундах), отметка времени последней успешной сверки (`LastSuccessfulReconciliationUtc`), код подключения `ShareCode` с кнопкой копирования, список участников с ролями и сетевыми именами хостов;
- **Журнал изменений (`/history`):** колонка «Клиент» с отображением `ActorMachineName` (или `ActorClientId` / «—»), локализованные русскоязычные наименования всех операций, включая «Конфликтная копия» (`CONFLICT_COPY`);
- **История версий файлов:** отображение полного 64-символьного хэша SHA-256 через подсказку и кнопку копирования в буфер обмена.
