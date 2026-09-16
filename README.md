# BasicXmlDatabase

A lightweight, fully asynchronous, thread-safe XML-based database library for C#. It stores data as rows of dynamic key-value pairs (`Dictionary<string, object>`) and provides a simple, LINQ-like API for CRUD operations, bulk updates, and Time-To-Live (TTL) management.

## ✨ Features

- **True Async/Await**: Uses `SemaphoreSlim` and asynchronous file I/O (`FileStream` with `useAsync: true`) to ensure zero thread blocking during disk operations.
- **Thread-Safe**: Safe for concurrent access from multiple threads or async tasks.
- **Dynamic Schema**: No predefined schema required. Columns are created on the fly when you insert or update data.
- **System-Managed Metadata**: 
  - `id__`: Auto-incrementing `Int64` identity column (strictly controlled by the system).
  - `chkDate__`: Auto-updated `DateTime` (UTC) timestamp used for TTL tracking.
- **TTL (Time-To-Live)**: Built-in support for expiring and purging old rows based on their last update/creation time.
- **Strict Case-Sensitivity**: All dictionary keys and string value queries are strictly case-sensitive (`StringComparison.Ordinal`).
- **Single File**: The entire library is contained in a single `.cs` file for easy integration.

## 🚀 Quick Start

### 1. Initialization
Because constructors cannot be asynchronous, use the static factory method `CreateAsync` to initialize the database.

```csharp
using BasicXmlDatabase;

// Initialize and load the database asynchronously
await using var db = await XmlDatabase.CreateAsync("my_data.xml");
```

### 2. Inserting Data
Pass a `Dictionary<string, object>` to insert a new row. The system will automatically assign the `id__` and `chkDate__`.

```csharp
var newUser = new Dictionary<string, object>
{
    { "FirstName", "Alice" },
    { "Age", 28 },
    { "IsActive", true }
};

long newId = await db.InsertAsync(newUser);
Console.WriteLine($"Inserted row with ID: {newId}");
```

### 3. Querying Data
Retrieve single or multiple rows using exact, case-sensitive matches.

```csharp
// Get a single row by ID
var alice = await db.GetByIdAsync(1);

// Get multiple rows matching ALL criteria
var criteria = new Dictionary<string, object> 
{ 
    { "IsActive", true }, 
    { "Age", 28 } 
};
List<Dictionary<string, object>> results = await db.GetManyRowsAsync(criteria);
```

### 4. Updating Data
Update existing columns or add brand new columns on the fly. The system automatically refreshes the `chkDate__`.

```csharp
// Update a single row
await db.UpdateByIdAsync(1, new Dictionary<string, object> 
{ 
    { "Age", 29 }, 
    { "Email", "alice@example.com" } // Adds a new column!
});

// Bulk update all matching rows
var criteria = new Dictionary<string, object> { { "IsActive", true } };
var updates = new Dictionary<string, object> { { "Status", "Verified" } };

List<long> updatedIds = await db.UpdateManyRowsAsync(criteria, updates);
```

### 5. Deleting & TTL Purging
Delete specific rows or bulk delete by criteria. You can also purge expired rows based on the TTL.

```csharp
// Delete by ID
await db.DeleteByIdAsync(1);

// Bulk delete
var deletedIds = await db.DeleteManyRowsAsync(new Dictionary<string, object> { { "Status", "Banned" } });

// Purge rows that haven't been updated in the last 30 days
int purgedCount = await db.PurgeExpiredAsync(ttlDays: 30);
Console.WriteLine($"Purged {purgedCount} expired rows.");
```

## 📖 API Reference

| Method | Description |
| :--- | :--- |
| `CreateAsync(string filePath)` | Factory method to initialize and load the database. |
| `InsertAsync(Dictionary)` | Inserts a row. Returns the new `id__` (`long`). |
| `SelectAsync()` | Returns all rows as `List<Dictionary<string, object>>`. |
| `GetByIdAsync(long id)` | Returns a single row matching the `id__`, or `null`. |
| `GetManyRowsAsync(Dictionary)` | Returns all rows matching the exact key/value criteria. |
| `UpdateByIdAsync(long id, Dictionary)` | Updates a row by ID. Adds new keys if they don't exist. Returns `bool`. |
| `UpdateManyRowsAsync(Dict, Dict)` | Bulk updates rows matching criteria. Returns `List<long>` of updated IDs. |
| `DeleteByIdAsync(long id)` | Deletes a row by ID. Returns `bool`. |
| `DeleteManyRowsAsync(Dictionary)` | Bulk deletes rows matching criteria. Returns `List<long>` of deleted IDs. |
| `PurgeExpiredAsync(int days)` | Deletes rows where `chkDate__` is older than the specified days. Returns count. |

## 📄 Underlying XML Structure

The library uses a robust `<Field>` element structure. This allows any string (including spaces and special characters) to be used as a column name, and preserves the exact .NET data type for accurate deserialization.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Table>
  <Row>
    <Field name="id__" type="System.Int64">1</Field>
    <Field name="FirstName" type="System.String">Alice</Field>
    <Field name="Age" type="System.Int32">29</Field>
    <Field name="IsActive" type="System.Boolean">True</Field>
    <Field name="Email" type="System.String">alice@example.com</Field>
    <Field name="chkDate__" type="System.DateTime">2023-10-27T14:32:01.1234567Z</Field>
  </Row>
</Table>
```

## ⚠️ Important Notes & Limitations

- **In-Memory Processing**: The entire XML file is loaded into memory (`XDocument`) upon initialization. This is perfect for small-to-medium datasets (thousands of rows) but not recommended for massive datasets (gigabytes of XML).
- **No SQL/Query Language**: Queries are strictly exact-match dictionary lookups. There is no support for `LIKE`, `>`, `<`, or `OR` conditions natively. (You can achieve this by using `SelectAsync()` and filtering in memory with LINQ).
- **File Locking**: While the in-memory operations are thread-safe via `SemaphoreSlim`, if multiple *processes* (e.g., two separate running applications) try to write to the same XML file simultaneously, OS-level file locking will cause exceptions. This library is designed for single-process concurrency.

## 📜 License

Free to use and modify for personal and commercial projects.
