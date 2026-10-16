# Спецификация TCP Protocol v1 (Change-set #2)

Сетевой протокол прикладного уровня, разработанный для системы резервного копирования файлов. Протокол функционирует поверх TCP-сокетов с гарантированной доставкой и ориентацией на непрерывный поток байт.

---

## 1. Физическая структура кадра (Physical Frame)

Каждый пакет данных передается в виде кадра следующего формата:

```
+------------------------------------+--------------------------+-----------------------+
|  4 байта (UInt32 Big-Endian)       |  HeaderLength байт       |  BodyLength байт      |
|  Длина заголовка (HeaderLength)    |  UTF-8 JSON Заголовок    |  Тело (Body stream)   |
+------------------------------------+--------------------------+-----------------------+
```

### Поля кадра:
1. **HeaderLength (4 байта):** Беззнаковое 32-битное целое число в сетевом порядке байт (**Big-Endian**), определяющее точный размер JSON-заголовка в байтах.
2. **JSON Header (`HeaderLength` байт):** Сериализованный в кодировке UTF-8 объект метаданных кадра.
3. **Body (`BodyLength` байт):** Произвольные бинарные данные или JSON-полезная нагрузка. Длина определяется полем `BodyLength` из заголовка. Если `BodyLength == 0`, тело в кадре отсутствует.

---

## 2. Ключевые протокольные правила

> [!IMPORTANT]
> **Отсутствие мультиплексирования (No Multiplexing / Interleaving):**
> Protocol v1 **не поддерживает** мультиплексирование кадров внутри одного TCP-соединения. Кадры передаются строго последовательно (FIFO). Во время передачи потокового тела (Streaming Body) никакой другой кадр не может быть вставлен в тот же `NetworkStream`. Параллельная работа нескольких клиентов или одновременная передача независимых файлов осуществляется за счет создания **отдельных параллельных TCP-соединений**.

### Правила валидации:
- **Максимальный размер заголовка:** строго **не более 64 KiB** (65 536 байт). При получении `HeaderLength > 65536` или `HeaderLength == 0` генерируется `ProtocolException`.
- **Порядок байт:** Длина заголовка всегда кодируется в Big-Endian (`BinaryPrimitives.WriteUInt32BigEndian`).
- **Фрагментация TCP:** Прием данных выполняется через циклическое накопление (`ProtocolFrameReader.ReadExactlyAsync`), исключающее предположение о том, что один вызов `ReadAsync` вернет весь кадр целиком.
- **Потоковая передача (Streaming):** Для `BodyLength > 0` чтение и запись могут осуществляться блоками (чанками по 64 KiB) напрямую в/из файлового потока, защищая от переполнения оперативной памяти.

### Безопасность путей (`RelativePath` Validation):
Все относительные пути клиентов строго валидируются через `ProtocolPathValidator`:
- Запрещены абсолютные пути (начинающиеся с `/` или UNC `\\`);
- Запрещены пути с дисковой буквой Windows (`C:\...`);
- Запрещен переход по каталогам (`..` или `.`);
- Запрещены нулевые байты (`\0`) и недопустимые спецсимволы;
- Все разделители нормализуются к символу `/`.

---

## 3. Структура JSON-заголовка (`ProtocolHeader`)

```json
{
  "magic": "BKP1",
  "protocolVersion": 1,
  "messageType": "PrepareVersion",
  "messageId": "c5f59045-8fd1-477c-a496-e131d575792d",
  "correlationId": null,
  "contentType": "application/json",
  "bodyLength": 128,
  "sentAtUtc": "2026-10-07T16:30:00.1234567Z"
}
```

| Поле | Тип | Описание | Обязательность |
| :--- | :--- | :--- | :--- |
| `magic` | String | Константа `"BKP1"`. | Обязательно |
| `protocolVersion` | Int32 | Версия протокола (`1`). | Обязательно |
| `messageType` | String / Enum | Имя типа сообщения из каталога протокола. | Обязательно |
| `messageId` | Guid | Уникальный GUID сообщения. | Обязательно |
| `correlationId` | Guid? (nullable) | ID запроса, на который отправляется ответ. | Опционально |
| `contentType` | String | `"application/json"`, `"application/octet-stream"`. | Обязательно |
| `bodyLength` | Int64 | Длина тела в байтах ($\ge 0$). | Обязательно |
| `sentAtUtc` | DateTime (ISO-8601) | Время отправки сообщения в UTC. | Обязательно |

---

## 4. Каталог типов сообщений (`MessageType`)

### Системные:
- `Hello`: Рукопожатие клиента с сервером.
- `HelloAck`: Подтверждение рукопожатия.
- `Ping` / `Pong`: Проверка доступности (heartbeat).
- `Error`: Протокольная или сервисная ошибка.

### Регистрация клиента и наборов:
- `RegisterClient`: Запрос регистрации рабочей станции.
- `RegisterClientResult`: Результат регистрации (ClientId, Created, LastSeen).
- `CreateBackupSet`: Создание набора резервного копирования.
- `CreateBackupSetResult`: Подтверждение создания набора.

### Управление метаданными версий:
- `PrepareVersion`: Подготовка загрузки новой версии файла.
- `PrepareVersionResult`: Решение о необходимости загрузки (`UploadRequired`, `OperationId`, `ExpectedVersionNumber`).
- `CommitVersion`: Фиксация загруженной версии в базе данных.
- `CommitVersionResult`: Результат фиксации версии.
- `DeleteFileMetadata`: Логическое удаление файла.
- `DeleteFileMetadataResult`: Подтверждение удаления.
- `RenameFileMetadata`: Переименование логического пути файла.
- `RenameFileMetadataResult`: Результат переименования.

### Запросы списков и ревизий:
- `GetFiles`: Получение списка файлов набора.
- `FilesResponse`: Ответ со списком файлов (`FileMetadataDto`).
- `GetVersions`: Получение истории ревизий файла.
- `VersionsResponse`: Список версий (`FileVersionDto`), отсортированный по убыванию номера версии.
- `ResolveVersion`: Запрос метаданных конкретной версии по номеру.
- `ResolveVersionResult`: Ответ с метаданными и ключом хранилища `StorageKey`.

### Физическое файловое хранилище (StorageService):
- `StoreFile`: Запрос дескриптора на загрузку физического файла (`OperationId`, `ClientId`, `BackupSetId`, `FileId`, `VersionNumber`, `ExpectedSize`, `ExpectedSha256`).
- `StoreFileReady`: Подтверждение готовности сервера принять бинарный поток (`Accepted: bool`, `ErrorCode`, `ErrorMessage`).
- `UploadFile`: Бинарный поток байт файла (`BodyLength = ExpectedSize`, `CorrelationId = storeFileMessageId`, `ContentType = application/octet-stream`).
- `StoreFileResult`: Финальный результат сохранения (`Success`, `StorageKey`, `ActualSize`, `ActualSha256`, `ErrorCode`, `ErrorMessage`).
- `GetStoredFile`: Запрос на чтение сохраненного файла (`StorageKey`).
- `GetStoredFileResult`: Дескриптор файла перед передачей потока (`Success`, `StorageKey`, `Size`, `Sha256`, `ErrorCode`, `ErrorMessage`).
- `StoredFile`: Бинарный поток байт запрошенного файла (`BodyLength = Size`, `CorrelationId = getStoredFileMessageId`).

### Внешний рабочий процесс резервного копирования (Client <-> Gateway):
- `FileChanged`: Первичное уведомление клиента шлюзу о создании или изменении локального файла (`BackupSetId`, `RelativePath`, `Size`, `Sha256`).
- `UploadRequired`: Ответ шлюза о необходимости физической загрузки (`UploadRequired: bool`, `OperationId`, `FileId`, `VersionNumber`).
- `UploadCompleted`: Финальный ответ клиенту о завершении резервного копирования (`Success: bool`, `FileId`, `VersionNumber`, `StorageKey`, `AlreadyCommitted: bool`, `ErrorCode`, `ErrorMessage`).

### Общие наборы и управление доступом (Shared Storage & Membership):
- `JoinSharedBackupSet`: Запрос на присоединение рабочей станции к общему набору по коду доступа (`ShareCode`).
- `JoinSharedBackupSetResult`: Результат присоединения (`Success`, `BackupSetId`, `Name`, `AccessMode`, `OwnerClientId`, `ShareCode`, `ErrorCode`, `ErrorMessage`).
- `GetBackupSetMembers`: Запрос списка участников набора резервного копирования (`BackupSetId`).
- `GetBackupSetMembersResult`: Список участников с их ролями (`BackupSetId`, `Members: List<BackupSetMemberDto>`, `Success`, `ErrorCode`, `ErrorMessage`).
- `GetBackupSetManifest`: Запрос серверного манифеста состояния набора (`BackupSetId`).
- `GetBackupSetManifestResult`: Ответ с манифестом файлов набора (`BackupSetId`, `Entries: List<BackupSetManifestEntryDto>`, `GeneratedAtUtc`, `Success`, `ErrorCode`, `ErrorMessage`).
- `DownloadFileVersion`: Запрос на потоковое скачивание конкретной ревизии файла (`BackupSetId`, `FileId`, `VersionNumber`).
- `DownloadFileVersionReady`: Дескриптор готовности сервера перед передачей бинарного потока (`Success`, `FileId`, `VersionNumber`, `RelativePath`, `Size`, `Sha256`, `ErrorCode`, `ErrorMessage`).
- `DownloadFileVersionData`: Бинарный поток байт ревизии файла (`BodyLength = Size`, `ContentType = application/octet-stream`).

---

## 5. Протокольный цикл сохранения и получения файлов

### Протокол сохранения файла (Store Flow):
```
Gateway (Client)                                          StorageService
      |                                                         |
      |-- 1. StoreFile [JSON] --------------------------------->|
      |      (OpId, ClientId, SetId, FileId, Ver, Size, Hash)   |-- Валидация параметров
      |                                                         |-- Подготовка .tmp/{OpId}.part
      |<-- 2. StoreFileReady [JSON] ----------------------------|
      |      (Accepted=true/false, ErrorCode, ErrorMessage)     |
      |                                                         |
      |-- 3. UploadFile [Binary Stream] ----------------------->|
      |      (BodyLength=ExpectedSize, Raw Bytes)               |-- Инкрементальный SHA-256
      |                                                         |-- Потоковая запись в .tmp
      |                                                         |-- Сверка размера и хэша
      |                                                         |-- Проверка идемпотентности
      |                                                         |-- Атомарный File.Move()
      |<-- 4. StoreFileResult [JSON] ---------------------------|
      |      (Success, StorageKey, ActualSize, ActualSha256)    |
```

### Протокол получения файла (Get Flow):
```
Gateway (Client)                                          StorageService
      |                                                         |
      |-- 1. GetStoredFile [JSON] ----------------------------->|
      |      (StorageKey)                                       |-- Валидация StorageKey
      |                                                         |-- Проверка существования
      |<-- 2. GetStoredFileResult [JSON] -----------------------|
      |      (Success, StorageKey, Size, Sha256)                |
      |                                                         |
      |<-- 3. StoredFile [Binary Stream] -----------------------| (если Success=true)
      |      (BodyLength=Size, Raw Bytes)                       |
```

---

## 6. Примеры полезной нагрузки сообщений

### 1. `RegisterClient`
```json
// Request: MessageType.RegisterClient
{
  "clientId": null,
  "name": "Dev-Workstation",
  "machineName": "DESKTOP-DEV"
}

// Response: MessageType.RegisterClientResult
{
  "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "created": true,
  "lastSeenAtUtc": "2026-10-07T16:30:00Z",
  "success": true,
  "errorMessage": null
}
```

### 2. `PrepareVersion`
```json
// Request: MessageType.PrepareVersion
{
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "relativePath": "docs/project_plan.docx",
  "size": 1048576,
  "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
}

// Response: MessageType.PrepareVersionResult
{
  "uploadRequired": true,
  "operationId": "f7d8c9e0-1234-5678-9abc-def012345678",
  "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
  "expectedVersionNumber": 1,
  "success": true,
  "errorMessage": null
}
```

### 3. `StoreFile` и `StoreFileReady`
```json
// Request: MessageType.StoreFile
{
  "operationId": "f7d8c9e0-1234-5678-9abc-def012345678",
  "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
  "versionNumber": 1,
  "expectedSize": 1048576,
  "expectedSha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
}

// Response: MessageType.StoreFileReady
{
  "operationId": "f7d8c9e0-1234-5678-9abc-def012345678",
  "accepted": true,
  "errorCode": null,
  "errorMessage": null
}
```

### 4. `StoreFileResult`
```json
// Response: MessageType.StoreFileResult
{
  "operationId": "f7d8c9e0-1234-5678-9abc-def012345678",
  "success": true,
  "storageKey": "clients/3fa85f64-5717-4562-b3fc-2c963f66afa6/backup-sets/a1b2c3d4-e5f6-7890-abcd-ef1234567890/files/b2c3d4e5-f6a7-8901-bcde-f0123456789a/versions/0000000001.bin",
  "actualSize": 1048576,
  "actualSha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  "errorCode": null,
  "errorMessage": null
}
```

### 5. `GetStoredFile` и `GetStoredFileResult`
```json
// Request: MessageType.GetStoredFile
{
  "storageKey": "clients/3fa85f64-5717-4562-b3fc-2c963f66afa6/backup-sets/a1b2c3d4-e5f6-7890-abcd-ef1234567890/files/b2c3d4e5-f6a7-8901-bcde-f0123456789a/versions/0000000001.bin"
}

// Response: MessageType.GetStoredFileResult
{
  "storageKey": "clients/3fa85f64-5717-4562-b3fc-2c963f66afa6/backup-sets/a1b2c3d4-e5f6-7890-abcd-ef1234567890/files/b2c3d4e5-f6a7-8901-bcde-f0123456789a/versions/0000000001.bin",
  "success": true,
  "size": 1048576,
  "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  "errorCode": null,
  "errorMessage": null
}
```

---

## 6. Форматы сообщений сквозного резервного копирования (Change-set #4)

### 6.1. `FileChanged` (Клиент -> Gateway)
```json
{
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "relativePath": "documents/report.pdf",
  "size": 1048576,
  "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
}
```

### 6.2. `UploadRequired` (Gateway -> Клиент)
```json
{
  "uploadRequired": true,
  "operationId": "f7d8c9e0-1234-5678-9abc-def012345678",
  "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
  "versionNumber": 1
}
```
*Примечание:* Если хэш содержимого совпадает с уже зафиксированной версией в базе данных, Gateway немедленно возвращает `UploadCompleted` с флагом `AlreadyCommitted = true` без запроса физической передачи файла.

### 6.3. `UploadCompleted` (Gateway -> Клиент)
```json
{
  "success": true,
  "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
  "versionNumber": 1,
  "storageKey": "clients/3fa85f64-5717-4562-b3fc-2c963f66afa6/backup-sets/a1b2c3d4-e5f6-7890-abcd-ef1234567890/files/b2c3d4e5-f6a7-8901-bcde-f0123456789a/versions/0000000001.bin",
  "alreadyCommitted": false,
  "errorCode": null,
  "errorMessage": null
}
```

### 6.4. Коды ошибок прикладного уровня:
| Код ошибки | Описание |
| :--- | :--- |
| `CLIENT_NOT_REGISTERED` | Клиент попытался выполнить операцию до успешной регистрации в Gateway/Metadata. |
| `BACKUP_SET_NOT_FOUND` | Указанный `BackupSetId` не найден в базе данных метаданных. |
| `INVALID_LOCAL_FILE` | Локальный файл отсутствует, поврежден или заблокирован другим процессом ОС. |
| `FILE_CHANGED_DURING_PREPARE` | Файл был изменен на клиенте между этапом расчета SHA-256 и началом передачи. |
| `UPLOAD_NOT_EXPECTED` | Получен бинарный кадр `UploadFile` без предварительного контекста `UploadRequired`. |
| `UPLOAD_IN_PROGRESS` | Клиент попытался запустить новую операцию в рамках соединения, где уже идет загрузка. |
| `METADATA_UNAVAILABLE` | Микросервис метаданных недоступен по сети. |
| `STORAGE_UNAVAILABLE` | Микросервис физического хранилища недоступен по сети. |
| `METADATA_COMMIT_UNCERTAIN` | Тайм-аут или разрыв связи при коммите; статус фиксации неизвестен, блоб сохранен. |
| `INVALID_RELATIVE_PATH` | Относительный путь не прошел строгую проверку безопасности (`ProtocolPathValidator`). |
| `SIZE_MISMATCH` | Количество переданных байт не совпало с `header.BodyLength` или заявленным размером. |
| `HASH_MISMATCH` | Контрольная сумма SHA-256 записанных данных не совпала с заявленным хэшем. |
| `STORAGE_CONFLICT` | В хранилище уже существует версия с другим содержимым под данным ключом. |

---

## 7. Контракты автоматической синхронизации и манифестов (Change-set #5)

### 7.1. Кадр `Manifest` (Клиент -> Gateway)
Кадр с управляющим JSON-телом (`MessageType.Manifest`):
```json
{
  "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "files": [
    {
      "relativePath": "docs/readme.txt",
      "size": 1024,
      "lastWriteTimeUtc": "2026-10-08T10:00:00Z",
      "sha256": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    },
    {
      "relativePath": "images/logo.png",
      "size": 20480,
      "lastWriteTimeUtc": "2026-10-08T10:15:00Z",
      "sha256": "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
    }
  ]
}
```

### 7.2. Кадр `SyncPlan` (Gateway -> Клиент)
Кадр ответа со сформированным планом действий (`MessageType.SyncPlan`):
```json
{
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "upload": [
    {
      "relativePath": "docs/readme.txt",
      "reason": "CHANGED"
    },
    {
      "relativePath": "images/logo.png",
      "reason": "NEW"
    }
  ],
  "delete": [
    "docs/old_obsolete_draft.txt"
  ],
  "unchangedCount": 42,
  "success": true,
  "errorMessage": null
}
```
Значения `reason` для элементов загрузки:
- `"NEW"`: Файл присутствует локально, но отсутствует на сервере.
- `"CHANGED"`: Файл присутствует на клиенте и сервере, но хэш содержимого различается.
- `"SERVER_DELETED"`: Файл числился на сервере удаленным (`IsDeleted == true`), но локально вновь создан или существует.

### 7.3. Кадр `FileDeleted` (Клиент -> Gateway)
Кадр уведомления об удалении файла в реальном времени (`MessageType.FileDeleted`):
**Запрос (`FileDeletedRequest`):**
```json
{
  "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "relativePath": "docs/obsolete.docx"
}
```
**Ответ (`FileDeletedResponse`):**
```json
{
  "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
  "success": true,
  "errorMessage": null
}
```

### 7.4. Кадр `FileRenamed` (Клиент -> Gateway)
Кадр уведомления о переименовании файла в реальном времени (`MessageType.FileRenamed`):
**Запрос (`FileRenamedRequest`):**
```json
{
  "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "oldRelativePath": "docs/draft.docx",
  "newRelativePath": "docs/final_report.docx"
}
```
**Ответ (`FileRenamedResponse`):**
```json
{
  "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
  "success": true,
  "errorMessage": null
}
```

---

## 8. Протокольный цикл восстановления версий и запросов браузера (Change-set #6)

### 8.1. Сообщения просмотра файлов и версий

#### 8.1.1. `GetFiles` (Клиент -> Gateway -> Metadata)
Запрос списка файлов набора резервного копирования (`MessageType.GetFiles`):
```json
{
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "includeDeleted": true
}
```
Ответ `MessageType.FilesResponse`:
```json
{
  "files": [
    {
      "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
      "relativePath": "docs/report.docx",
      "currentVersionNumber": 3,
      "size": 1048576,
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "isDeleted": false,
      "updatedAtUtc": "2026-10-08T12:00:00Z"
    }
  ],
  "success": true,
  "errorMessage": null
}
```

#### 8.1.2. `GetVersions` (Клиент -> Gateway -> Metadata)
Запрос списка исторических версий файла (`MessageType.GetVersions`):
```json
{
  "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a"
}
```
Ответ `MessageType.VersionsResponse` (упорядочен по убыванию `VersionNumber`):
```json
{
  "versions": [
    {
      "versionNumber": 3,
      "size": 1048576,
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "storageKey": "clients/.../versions/0000000003.bin",
      "createdAtUtc": "2026-10-08T12:00:00Z"
    },
    {
      "versionNumber": 1,
      "size": 1024,
      "sha256": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
      "storageKey": "clients/.../versions/0000000001.bin",
      "createdAtUtc": "2026-10-08T10:00:00Z"
    }
  ],
  "success": true,
  "errorMessage": null
}
```

#### 8.1.3. `GetChangeLog` (Клиент -> Gateway -> Metadata)
Запрос аудиторского журнала изменений (`MessageType.GetChangeLog`):
```json
{
  "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "limit": 100
}
```
Ответ `MessageType.ChangeLogResponse`:
```json
{
  "entries": [
    {
      "id": "11111111-2222-3333-4444-555555555555",
      "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "backupSetId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
      "relativePath": "docs/report.docx",
      "action": "RESTORE",
      "status": "COMMITTED",
      "details": "Restored version 4 with Sha256 e3b0c4...",
      "timestampUtc": "2026-10-08T12:05:00Z"
    }
  ],
  "success": true,
  "errorMessage": null
}
```

---

### 8.2. Протокольный процесс восстановления (Restore Flow)

```
Client (Web)                     SyncGateway                 MetadataService         StorageService
     |                                |                             |                       |
     |-- 1. RestoreRequest ---------->|                             |                       |
     |      (FileId, Version)         |-- 2. ResolveVersion ------->|                       |
     |                                |<-- ResolveVersionResponse --|                       |
     |                                |    (StorageKey, Size, SHA)  |                       |
     |                                |                                                     |
     |                                |-- 3. GetStoredFile -------------------------------->|
     |                                |<-- GetStoredFileResult (Size, SHA) -----------------|
     |                                |                                                     |
     |<-- 4. RestoreReady ------------|                                                     |
     |      (Size, SHA, RelativePath) |                                                     |
     |<-- 5. RestoreFile (Header) ----|                                                     |
     |<-- 6. Binary Stream Relay -----|<-- StoredFile Binary Stream ------------------------|
     |                                |                                                     |
[Verify SHA-256]                      |                                                     |
[Atomic Replace]                      |                                                     |
[Register Suppression]                |                                                     |
     |                                |                                                     |
     |-- 7. BackupFileAsync --------->| (Re-backup restored content with origin=RESTORE)    |
     |      (Origin: RESTORE)         |-- PrepareVersion (RESTORE)->|                       |
     |                                |-- StoreFile / Commit ------>|                       |
     |<-- UploadCompleted ------------|                             |                       |
     |                                |                             |                       |
     |-- 8. RestoreCompleted -------->|                             |                       |
     |<-- RestoreCompletedResponse ---|                             |                       |
```

#### Порядок взаимодействия:
1. **RestoreRequest:** Клиент отправляет JSON-кадр `MessageType.RestoreRequest` со ссылкой на `FileId` и восстанавливаемый `VersionNumber`.
2. **Разрешение версии:** Gateway обращается к `MetadataService.ResolveVersion` для проверки существования версии и получения `StorageKey`.
3. **Потоковая ретрансляция (Streaming Relay):**
   - Gateway открывает поток к `StorageService.GetStoredFile`;
   - Получив дескриптор из хранилища, Gateway отправляет клиенту `RestoreReady` (`Size`, `Sha256`, `RelativePath`);
   - Следом Gateway отправляет заголовок `RestoreFile` (`BodyLength = Size`, `ContentType = application/octet-stream`);
   - Бинарные данные пересылаются блоками по 64 KiB без сохранения на диске Gateway и без загрузки файла целиком в память.
4. **Безопасная временная запись и валидация на клиенте:**
   - Клиент формирует целевой путь `LocalRootPath + RelativePath` и валидирует его через `ProtocolPathValidator` на отсутствие path traversal;
   - Клиент создает временный файл в том же каталоге: `<file>.restore.<Guid>.tmp`;
   - В процессе потоковой записи инкрементально рассчитывается SHA-256;
   - После завершения проверяются условия: `ActualSize == ExpectedSize` и `ActualSha256 == ExpectedSha256`. При несовпадении temp удаляется, пользовательский файл не изменяется, возвращается ошибка.
5. **Подавление эхо-событий наблюдателя (`RestoreSuppressionService`):**
   - Перед заменой файла клиент регистрирует супрессию `(RelativePath, RestoredSha256, TTL = 30s)`;
   - После атомарного перемещения `File.Move(temp, target, overwrite: true)` возникающие события FileSystemWatcher дебаунсятся, вычисляется хэш файла: если он совпадает с `RestoredSha256`, событие подавляется без создания дублирующей версии; если пользователь внес новое изменение (хэш изменился), событие синхронизируется в обычном порядке.
6. **Неизменяемость версий (Version Immutability):**
   - Restore **НЕ откатывает** `CurrentVersionNumber` назад;
   - После локальной замены клиент выполняет `BackupFileAsync` с флагом `ChangeOrigin = BackupChangeOrigin.Restore`;
   - В метаданных создается новая ревизия `v_next = CurrentVersionNumber + 1`;
   - В `ChangeLog` фиксируется действие `RESTORE` (вместо `MODIFY`);
   - Старые версии и физические блобы сохраняются полностью.
7. **Защита несохраненных локальных правок (Pre-Backup):**
   - Перед заменой локального файла клиент выполняет `BackupFileAsync` текущего состояния;
   - Если локальный файл содержал несохраненные правки, сервер сначала фиксирует их как нормальную версию `v_edit`, и лишь затем восстанавливаемый файл коммитится как `v_restore`. Данные пользователя не теряются.
8. **Восстановление логически удаленного файла:**
   - Если файл был помечен `IsDeleted == true`, восстанавливаемая версия создает файл на диске, создает родительские каталоги, снимает флаг удаления (`IsDeleted = false`) и инкрементирует `CurrentVersionNumber`.
9. **Обработка сбоев:**
   - *Сбой до замены:* temp-файл удаляется, пользовательский файл не трогается, возвращается ошибка.
   - *Сбой после замены (например, разрыв сети при ре-бэкапе):* файл остается восстановленным на диске, клиенту выставляется флаг `ReconciliationRequired = true` (`LOCAL_RESTORED_SYNC_PENDING`), следующий sync коммитит версию на сервере.

---

## 9. Диагностика, проверка доступности и каталог ошибок (Change-set #7)

### 9.1. Механизм проверки связи (Ping / Pong Heartbeat)
Для быстрого определения сетевой доступности сервисов и визуализации статуса шлюза в UI (`Gateway: Online / Offline`) протокол использует легковесные кадры нулевой длины:
- **Ping (`MessageType.Ping`):**
  - Заголовок с `bodyLength = 0`, `contentType = application/json`;
  - Отправляется клиентом для проверки живучести TCP-соединения;
- **Pong (`MessageType.Pong`):**
  - Ответ сервера с `correlationId = pingMessageId`;
  - Может содержать `PingMessage` (метка времени) в теле кадра (`bodyLength >= 0`);
  - **Критический инвариант дренирования (Release Fix #1):** Клиент обязан вычитать или сдренировать ровно `bodyLength` байт при получении ответа Pong. Пропуск чтения тела приводит к тому, что байты тела `{"ti` считываются следующим запросом как 4-байтный `HeaderLength = 2065855593`, вызывая ошибку фрейминга.

### 9.2. Стандартизированные коды ошибок (Protocol Error Codes)

| Код ошибки | Описание | Источник |
| :--- | :--- | :--- |
| `METADATA_UNAVAILABLE` | Сервис метаданных временно недоступен или сокет разорван | SyncGateway |
| `STORAGE_UNAVAILABLE` | Сервис физического хранилища временно недоступен или сокет разорван | SyncGateway |
| `UPLOAD_IN_PROGRESS` | Попытка отправки второго файла в то же соединение до завершения текущего | SyncGateway |
| `UPLOAD_NOT_EXPECTED` | Получен бинарный кадр `UploadFile` без предварительного `UploadRequired` | SyncGateway |
| `RESTORE_TARGET_LOCKED` | Локальный целевой файл заблокирован внешним процессом | Client.Web |
| `RESTORE_HASH_MISMATCH` | Контрольная сумма скачанного файла не совпала с дескриптором | Client.Web |
| `RESTORE_SIZE_MISMATCH` | Размер скачанного файла не совпал с заявленным | Client.Web |
| `PATH_TRAVERSAL` | Попытка выхода за пределы корневого каталога или некорректный относительный путь | Shared Validator |
| `HEADER_TOO_LARGE` | Длина JSON-заголовка превышает допустимый лимит 64 KiB | Shared Reader |
| `CORRUPTED_MAGIC` | В начале кадра передан некорректный маркер, отличный от `BKP1` | Shared Reader |
| `ACCESS_DENIED` | Клиент не является владельцем или участником набора | MetadataService |
| `INVALID_SHARE_CODE` | Некорректный формат или символы в коде доступа | Shared Validator |
| `NOT_SHARED` | Попытка подключения по коду к набору в режиме PRIVATE | MetadataService |

### 9.3. Неизменяемость физического фрейминга
В рамках Release Fix #1 физический фрейминг Protocol v1 (сигнатура `BKP1`, порядок байт Big-Endian, 32-битный префикс `HeaderLength`, ограничение 64 KiB, потоковое тело `BodyLength`) **полностью сохранен без модификаций**. Ошибка рассинхронизации устранена на уровне управления сессией и жизненным циклом потока, без искусственного увеличения лимита заголовка и без расслабления проверок кадрирования. Protocol v1 остается строго последовательным протоколом без мультиплексирования.

---

## 10. Протокольные операции общего хранилища (Shared Storage Operations)

### 10.1. Присоединение к общему набору (`JoinSharedBackupSet`)
- **Запрос (`JoinSharedBackupSetRequest`):**
  ```json
  {
    "shareCode": "7H4K-92QX",
    "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
  }
  ```
- **Ответ (`JoinSharedBackupSetResponse`):**
  ```json
  {
    "success": true,
    "backupSetId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "name": "SharedTeamVault",
    "accessMode": "Shared",
    "ownerClientId": "f0e1d2c3-b4a5-9687-8e7d-6c5b4a3f2e1d",
    "shareCode": "7H4K-92QX",
    "errorCode": null,
    "errorMessage": null
  }
  ```
- **Идемпотентность:** Повторный вызов `JoinSharedBackupSet` для клиента, уже являющегося участником, завершается успехом без дублирования записей в БД.

### 10.2. Получение участников набора (`GetBackupSetMembers`)
- **Запрос (`GetBackupSetMembersRequest`):**
  ```json
  {
    "backupSetId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
  }
  ```
- **Ответ (`GetBackupSetMembersResponse`):**
  ```json
  {
    "backupSetId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "members": [
      {
        "clientId": "f0e1d2c3-b4a5-9687-8e7d-6c5b4a3f2e1d",
        "clientName": "Workstation-A",
        "machineName": "DESKTOP-LEAD",
        "role": "OWNER",
        "joinedAtUtc": "2026-10-16T10:00:00Z",
        "lastSeenAtUtc": "2026-10-16T10:30:00Z"
      },
      {
        "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
        "clientName": "Workstation-B",
        "machineName": "DESKTOP-DEV",
        "role": "MEMBER",
        "joinedAtUtc": "2026-10-16T10:15:00Z",
        "lastSeenAtUtc": "2026-10-16T10:35:00Z"
      }
    ],
    "success": true,
    "errorCode": null,
    "errorMessage": null
  }
  ```

### 10.3. Получение серверного манифеста (`GetBackupSetManifest`)
- **Запрос (`GetBackupSetManifestRequest`):**
  ```json
  {
    "backupSetId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "clientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
  }
  ```
  *Примечание:* Поле `clientId` из DTO не используется для авторизации; шлюз авторизует сессию исключительно по `GatewaySession.ClientId`.

- **Ответ (`GetBackupSetManifestResponse`):**
  ```json
  {
    "backupSetId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "generatedAtUtc": "2026-10-16T10:40:00Z",
    "entries": [
      {
        "fileId": "612b9add-00b8-497f-b318-1053cc554c4d",
        "relativePath": "docs/report.txt",
        "currentVersionNumber": 2,
        "isDeleted": false,
        "size": 1024,
        "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        "updatedAtUtc": "2026-10-16T10:35:00Z"
      },
      {
        "fileId": "8f4a21cd-43a9-467f-98c1-1253bb554e12",
        "relativePath": "old_draft.txt",
        "currentVersionNumber": 1,
        "isDeleted": true,
        "size": 512,
        "sha256": "a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e",
        "updatedAtUtc": "2026-10-16T10:30:00Z"
      }
    ],
    "success": true,
    "errorCode": null,
    "errorMessage": null
  }
  ```

### 10.4. Потоковое скачивание версии файла (`DownloadFileVersion`)
Read-only потоковый конвейер скачивания существующей ревизии файла (не затрагивает бизнес-логику Restore и не порождает новых записей аудита):

1. **Запрос дескриптора (`DownloadFileVersionRequest`):**
   ```json
   {
     "backupSetId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
     "fileId": "612b9add-00b8-497f-b318-1053cc554c4d",
     "versionNumber": 2
   }
   ```
2. **Ответ готовности (`DownloadFileVersionReadyResponse`):**
   ```json
   {
     "success": true,
     "fileId": "612b9add-00b8-497f-b318-1053cc554c4d",
     "versionNumber": 2,
     "relativePath": "docs/report.txt",
     "size": 1024,
     "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
     "errorCode": null,
     "errorMessage": null
   }
   ```
3. **Бинарный фрейм полезной нагрузки (`DownloadFileVersionData`):**
   - `Header.MessageType`: `DownloadFileVersionData`
   - `Header.ContentType`: `application/octet-stream`
   - `Header.BodyLength`: `1024`
   - `Body`: Сквозной бинарный поток байт файла из хранилища.
