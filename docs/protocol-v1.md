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

---

## 5. Примеры полезной нагрузки сообщений

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

### 3. `CommitVersion`
```json
// Request: MessageType.CommitVersion
{
  "operationId": "f7d8c9e0-1234-5678-9abc-def012345678",
  "storageKey": "storage/blobs/2026/10/f7d8c9e0.bin",
  "actualSize": 1048576,
  "actualSha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
}

// Response: MessageType.CommitVersionResult
{
  "fileId": "b2c3d4e5-f6a7-8901-bcde-f0123456789a",
  "versionNumber": 1,
  "storageKey": "storage/blobs/2026/10/f7d8c9e0.bin",
  "success": true,
  "errorMessage": null
}
```
