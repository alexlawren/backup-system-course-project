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

