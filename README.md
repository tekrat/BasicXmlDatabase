

[![NuGet Version](https://img.shields.io/nuget/v/BasicXmlDatabase.svg?style=flat-square&color=blue)](https://www.nuget.org/packages/BasicXmlDatabase/)
[![Target Framework](https://img.shields.io/badge/.NET-8.0-512bd4.svg?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](LICENSE)
[![Zero Dependencies](https://img.shields.io/badge/Dependencies-Zero-success.svg?style=flat-square)](#)

**A lightweight, fully asynchronous, thread-safe, and schema-less XML-based database library for .NET 8.**

`BasicXmlDatabase` stores data as rows of dynamic key-value pairs (`Dictionary<string, object>`) and provides a simple, intuitive API for CRUD operations, bulk processing, advanced querying, pagination, Time-To-Live (TTL) management, and multi-format reporting (CSV, TSV, PSV, JSON, XML, and HTML).

Designed for simplicity and zero external dependencies, the entire library is contained within a **single `.cs` file**, making it effortless to install via NuGet or drop directly into any project.

---

## 📑 Table of Contents

- [Features](#-features)
- [Installation](#-installation)
- [Quick Start](#-quick-start)
- [How It Works](#-how-it-works)
  - [System Columns](#system-columns)
  - [Atomic Writes & Safety](#atomic-writes--safety)
  - [Automatic Serialization Fallback](#automatic-serialization-fallback)
  - [Storage Format on Disk](#storage-format-on-disk)
- [API Reference & Usage](#-api-reference--usage)
  - [Database Lifecycle](#database-lifecycle)
  - [Inserting Data](#inserting-data)
  - [Querying & Reading](#querying--reading)
  - [Updating & Replacing](#updating--replacing)
  - [Deleting & Purging](#deleting--purging)
  - [Importing & Exporting](#importing--exporting)
  - [Backup & Maintenance](#backup--maintenance)
- [Advanced Scenarios](#-advanced-scenarios)
- [When to Use](#-when-to-use)
- [License](#-license)

---

## ✨ Features

### 🚀 Performance & Safety

- **True Async/Await**: Uses `SemaphoreSlim` and asynchronous file I/O (`FileStream` with `useAsync: true`) to ensure non-blocking disk operations across all reads, writes, imports, exports, and backups.
- **Thread-Safe**: Fully safe for concurrent access from multiple threads and async tasks within your application.
- **Atomic Writes**: Writes to a process-unique temporary file first and moves it over the original file (`File.Move` with overwrite). A crash or power outage mid-save will never corrupt your database file.
- **`IAsyncDisposable` Support**: Enables modern C# `await using` syntax for clean, deterministic resource disposal.

### 🧩 Flexibility & Schema

- **Schema-Less**: No predefined schema, tables, or migrations required. Columns are dynamically added and adapted on the fly.
- **System-Managed Metadata**:
  - `id__`: Auto-incrementing `Int64` primary key assigned and managed automatically.
  - `chkDate__`: Auto-updated UTC `DateTime` timestamp tracking record creation and modification times.
  - `table___`: Logical table partition (`System.String`, defaults to `"NotSet"`). Validated against null, blank, and whitespace, with automatic startup scanning and self-healing.
- **Built-in TTL (Time-To-Live)**: Purge stale or expired records with a single method call based on row age.

### 🔍 Advanced Querying

- **Exact Criteria Lookups**: Filter rows by dictionary key-value matching (`StringComparison.Ordinal`).
- **Arbitrary Predicate Queries**: Use any custom C# `Func<Dictionary<string, object>, bool>` expression for complex conditions, regex, numerical ranges, and cross-column logic.
- **Bounded Pagination & Sorting**: Efficiently page through large datasets with natural type ordering and fallback string comparison.
- **Aggregations**: Built-in `CountAsync` and `ExistsAsync` operations.

### 🔄 Multi-Format Import & Export

- **Delimited Formats**: Full RFC 4180 compliant import and export for **CSV** (comma), **TSV** (tab), and **PSV** (pipe).
- **Web & API Reports**: Generate formatted **HTML tables**, **indented JSON arrays**, and structured **XML reports** directly from query criteria.

### 📦 Smart Data Serialization

- **XML-Safe Primitive Storage**: Primitives, strings, `DateTime`, `decimal`, `Guid`, `TimeSpan`, `Uri`, and enums are stored as native typed XML text.
- **Automatic JSON Fallback**: Complex objects, nested structures, or strings with illegal XML 1.0 characters are automatically serialized to indented JSON with a `[JSON]` header.
- **Fail-Safe Cell Tagging**: If an object cannot be serialized (e.g., circular references), the cell is safely recorded as `[SERIALIZE_ERROR]` rather than throwing an unhandled exception.

---

## 📦 Installation

### Option 1: NuGet Package Manager

Install via the .NET CLI:

```bash
dotnet add package BasicXmlDatabase
```

Or via the Visual Studio Package Manager Console:

```powershell
Install-Package BasicXmlDatabase
```

Or reference it directly in your `.csproj`:

```xml
<PackageReference Include="BasicXmlDatabase" Version="1.1.0" />
```

### Option 2: Direct Source File (Zero-Dependency Drop-In)

Because `BasicXmlDatabase` is self-contained in a single file with no third-party dependencies, you can simply copy [`BasicXmlDatabase.cs`](BasicXmlDatabase.cs) into your .NET 8 project.

---

## ⚡ Quick Start

Here is a complete, runnable example demonstrating initialization, insertion, querying, updating, and exporting:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BasicXmlDatabase;

class Program
{
    static async Task Main(string[] args)
    {
        string dbPath = "app_data.xml";

        // 1. Create or open database (asynchronous factory)
        await using var db = await XmlDatabase.CreateDBAsync(dbPath);

        // 2. Insert rows
        long userId = await db.InsertAsync(new Dictionary<string, object>
        {
            ["Username"] = "johndoe",
            ["Email"] = "john@example.com",
            ["Role"] = "Admin",
            ["IsActive"] = true,
            ["Points"] = 150
        });
        Console.WriteLine($"Inserted User with ID: {userId}");

        // 3. Batch insert multiple rows
        var newUsers = new List<Dictionary<string, object>>
        {
            new() { ["Username"] = "alice", ["Email"] = "alice@example.com", ["Role"] = "Member", ["Points"] = 200 },
            new() { ["Username"] = "bob", ["Email"] = "bob@example.com", ["Role"] = "Member", ["Points"] = 75 }
        };
        List<long> batchIds = await db.InsertManyAsync(newUsers);

        // 4. Query by ID
        var user = await db.GetByIdAsync(userId);
        Console.WriteLine($"Found user: {user["Username"]} (Created: {user["chkDate__"]})");

        // 5. Query using arbitrary predicates (LINQ)
        var topUsers = await db.GetWhereAsync(r => 
            r.TryGetValue("Points", out var p) && Convert.ToInt32(p) >= 100);
        Console.WriteLine($"Users with >= 100 points: {topUsers.Count}");

        // 6. Update row
        await db.UpdateByIdAsync(userId, new Dictionary<string, object>
        {
            ["Points"] = 250,
            ["LastLogin"] = DateTime.UtcNow
        });

        // 7. Export to CSV and JSON
        await db.ExportCsvAsync(new Dictionary<string, object>(), "users_export.csv");
        string jsonReport = await db.GetJSONReportAsync(new Dictionary<string, object> { ["Role"] = "Member" });
        Console.WriteLine(jsonReport);
    }
}
```

---

## 🔬 How It Works

### System Columns

Every row in `BasicXmlDatabase` is automatically provisioned with three protected system columns:

| Column | Type | Description |
| :--- | :--- | :--- |
| `id__` | `Int64` (`long`) | Unique auto-incrementing identity column starting at 1. Cannot be overwritten by user inserts, updates, or imports. |
| `chkDate__` | `DateTime` (UTC) | High-precision UTC timestamp (`o` format) refreshed automatically whenever a row is inserted, updated, or replaced. Used for audit trails and TTL purging. |
| `table___` | `System.String` | Logical table name (defaults to `"NotSet"`). Cannot be null, blank, or whitespace. Automatically validated and self-healed on startup. |

### Atomic Writes & Safety

When `BasicXmlDatabase` saves changes:

1. Data is written asynchronously using `XmlWriterSettings { Async = true, Indent = true }` to a temporary file:
   `{filePath}.{ProcessId}.tmp`
2. Once the write succeeds and is flushed, the temporary file replaces the target file via atomic `File.Move(tempPath, filePath, overwrite: true)`.
3. If an error occurs during write, the temporary file is deleted and the live database file remains unharmed.
4. Internal mutations are guarded by an asynchronous re-entrant lock (`SemaphoreSlim(1, 1)`), making instances thread-safe.

### Automatic Serialization Fallback

`BasicXmlDatabase` safely handles heterogeneous and complex values:

- **Direct XML**: Standard primitives (`int`, `long`, `double`, `bool`, etc.), `string`, `DateTime`, `decimal`, `Guid`, `TimeSpan`, `DateTimeOffset`, and enums are written directly with their type attribute.
- **Illegal XML Characters**: If a string contains invalid XML 1.0 control characters (e.g., `\0` or non-characters), the library redirects the value to JSON storage to prevent XML parsing exceptions.
- **Complex Objects**: Objects or collections are converted to formatted JSON strings prefixed with `[JSON]` and tagged with `type="JSON:<FullName>"`.
- **Serialization Error Handling**: If an object cannot be serialized to JSON (e.g., recursive references), the cell is saved with `type="System.String"` and the value `[SERIALIZE_ERROR]`.

### Storage Format on Disk

Here is how data looks in the underlying XML file:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Table>
  <Row>
    <Field name="id__" type="System.Int64">1</Field>
    <Field name="chkDate__" type="System.DateTime">2026-09-20T15:00:00.0000000Z</Field>
    <Field name="table___" type="System.String">NotSet</Field>
    <Field name="Username" type="System.String">johndoe</Field>
    <Field name="IsActive" type="System.Boolean">True</Field>
    <Field name="Metadata" type="JSON:MyNamespace.CustomSettings">
[JSON]
{
  "Theme": "Dark",
  "NotificationsEnabled": true
}
    </Field>
  </Row>
</Table>
```

---

## 📖 API Reference & Usage

### Database Lifecycle

#### `CreateDBAsync`

Asynchronously initializes or opens an existing XML database file.

```csharp
public static async Task<XmlDatabase> CreateDBAsync(string filePath)
```

#### `DisposeAsync`

Disposes internal resources and releases the synchronization semaphore.

```csharp
await using var db = await XmlDatabase.CreateDBAsync("database.xml");
```

---

### Inserting Data

#### `InsertAsync`

Inserts a single row and returns its newly assigned unique ID. Automatically assigns `id__`, `chkDate__`, and `table___` (defaulting to `"NotSet"` if omitted).

```csharp
public async Task<long> InsertAsync(Dictionary<string, object> row, string? tableName = null)
```

```csharp
// Insert with explicit table parameter
long id = await db.InsertAsync(new Dictionary<string, object>
{
    ["Title"] = "Task 1",
    ["Completed"] = false
}, tableName: "Tasks");

// Or specify table___ directly in the dictionary
long id2 = await db.InsertAsync(new Dictionary<string, object>
{
    ["table___"] = "Tasks",
    ["Title"] = "Task 2"
});
```

#### `InsertManyAsync`

Inserts multiple rows in a batch, saving to disk only once at the end.

```csharp
public async Task<List<long>> InsertManyAsync(IEnumerable<Dictionary<string, object>> rows, string? tableName = null)
```

```csharp
var items = new List<Dictionary<string, object>>
{
    new() { ["Item"] = "Apples", ["Qty"] = 10 },
    new() { ["Item"] = "Oranges", ["Qty"] = 5 }
};
List<long> ids = await db.InsertManyAsync(items, tableName: "Inventory");
```

---

### Querying & Reading

#### `GetByIdAsync`

Retrieves a row by its primary `id__`. When `tableName` is specified, returns `null` if the row belongs to a different table.

```csharp
public async Task<Dictionary<string, object>?> GetByIdAsync(long id, string? tableName = null)
```

#### `GetAllAsync`

Returns all rows stored in the database, or restricted to a specific table when `tableName` is provided.

```csharp
public async Task<List<Dictionary<string, object>>> GetAllAsync(string? tableName = null)
```

#### `GetManyRowsAsync`

Returns all rows matching specific criteria using exact ordinal equality, optionally filtered by table.

```csharp
public async Task<List<Dictionary<string, object>>> GetManyRowsAsync(Dictionary<string, object> criteria, string? tableName = null)
```

```csharp
var activeUsers = await db.GetManyRowsAsync(new Dictionary<string, object>
{
    ["Status"] = "Active",
    ["Department"] = "Engineering"
}, tableName: "Users");
```

#### `GetWhereAsync`

Queries rows using a custom C# predicate function for flexible and complex filtering, optionally scoped to a table.

```csharp
public async Task<List<Dictionary<string, object>>> GetWhereAsync(Func<Dictionary<string, object>, bool> predicate, string? tableName = null)
```

```csharp
// Find high-value orders in the "Orders" table
var bigOrders = await db.GetWhereAsync(row =>
    row.TryGetValue("Total", out var t) && Convert.ToDecimal(t) >= 1000m,
    tableName: "Orders"
);
```

#### `GetPageAsync`

Paginates through matching rows with optional ordering and table scoping. Page numbers are 1-based.

```csharp
public async Task<List<Dictionary<string, object>>> GetPageAsync(
    Dictionary<string, object> criteria, 
    int page, 
    int pageSize, 
    string? orderBy = null, 
    bool ascending = true,
    string? tableName = null)
```

```csharp
// Page 2 of "Books" table, 10 items per page, sorted by "CreatedDate" descending
var pageData = await db.GetPageAsync(
    criteria: new Dictionary<string, object> { ["InStock"] = true },
    page: 2,
    pageSize: 10,
    orderBy: "chkDate__",
    ascending: false,
    tableName: "Books"
);
```

#### `CountAsync` & `ExistsAsync`

Efficiently count matching rows or test for existence without materializing full rows.

```csharp
public async Task<long> CountAsync(Dictionary<string, object> criteria, string? tableName = null)
public async Task<bool> ExistsAsync(Dictionary<string, object> criteria, string? tableName = null)
```

```csharp
long pendingOrders = await db.CountAsync(new Dictionary<string, object> { ["Status"] = "Pending" }, tableName: "Orders");
bool hasAdmin = await db.ExistsAsync(new Dictionary<string, object> { ["Role"] = "Admin" }, tableName: "Users");
```

---

### Updating & Replacing

#### `UpdateByIdAsync`

Applies partial updates to a row by ID. If `tableName` is specified, the row must belong to that table.

```csharp
public async Task<bool> UpdateByIdAsync(long id, Dictionary<string, object> updates, string? tableName = null)
```

```csharp
bool updated = await db.UpdateByIdAsync(1, new Dictionary<string, object>
{
    ["Status"] = "Completed",
    ["CompletedAt"] = DateTime.UtcNow
}, tableName: "Orders");
```

#### `UpdateManyRowsAsync`

Updates all rows matching the criteria (and optional table) with the specified values. Returns a list of updated IDs.

```csharp
public async Task<List<long>> UpdateManyRowsAsync(
    Dictionary<string, object> criteria, 
    Dictionary<string, object> values,
    string? tableName = null)
```

```csharp
List<long> modifiedIds = await db.UpdateManyRowsAsync(
    criteria: new Dictionary<string, object> { ["Expired"] = true },
    values: new Dictionary<string, object> { ["Status"] = "Archived" },
    tableName: "Sessions"
);
```

#### `ReplaceAsync`

Replaces the entire row (retaining its `id__`, updating `chkDate__`, and preserving or updating `table___`).

```csharp
public async Task<bool> ReplaceAsync(long id, Dictionary<string, object> row, string? tableName = null)
```

---

### Deleting & Purging

#### `DeleteByIdAsync`

Deletes a single row by ID, optionally scoped to a table. Returns `true` if found and deleted.

```csharp
public async Task<bool> DeleteByIdAsync(long id, string? tableName = null)
```

#### `DeleteManyRowsAsync`

Deletes all rows matching specified criteria and optional table. Returns list of deleted IDs.

```csharp
public async Task<List<long>> DeleteManyRowsAsync(Dictionary<string, object> criteria, string? tableName = null)
```

#### `PurgeExpiredAsync`

Purges records whose `chkDate__` timestamp is older than `ttlDays`, optionally targeted to a specific table.

```csharp
public async Task<int> PurgeExpiredAsync(int ttlDays, string? tableName = null)
```

```csharp
// Remove logs older than 30 days only in the "Logs" table
int purgedCount = await db.PurgeExpiredAsync(ttlDays: 30, tableName: "Logs");
Console.WriteLine($"Purged {purgedCount} expired log records.");
```

---

### Importing & Exporting

#### Delimited Files (CSV / TSV / PSV)

Fully RFC 4180 compliant. System columns (`id__` and `chkDate__`) are automatically refreshed on import, and rows are assigned to `targetTable` (or read from the CSV's `table___` column).

```csharp
// Export (optionally filtered by table)
await db.ExportCsvAsync(new Dictionary<string, object>(), "users_export.csv", tableName: "Users");
await db.ExportTsvAsync(new Dictionary<string, object>(), "export.tsv");
await db.ExportPsvAsync(new Dictionary<string, object>(), "export.psv");

// Import (returns number of imported rows, assigns targetTable)
int countCsv = await db.ImportCsvAsync("users.csv", targetTable: "Users");
int countTsv = await db.ImportTsvAsync("import.tsv");
int countPsv = await db.ImportPsvAsync("import.psv");
```

#### Reports (HTML, JSON, XML)

Generate strings for web display, API responses, or backups (optionally filtered by table):

```csharp
// HTML Table with class "xml-db-report"
string htmlTable = await db.GetHTMLReportAsync(new Dictionary<string, object>(), tableName: "Products");

// Clean, indented JSON array
string json = await db.GetJSONReportAsync(new Dictionary<string, object>(), tableName: "Users");

// Well-formed XML document string (<Report><Row><Field .../>...</Row></Report>)
string xml = await db.GetXMLReportAsync(new Dictionary<string, object>());
```

---

### Backup & Maintenance

#### `BackupAsync`

Saves any in-memory state to disk and asynchronously copies the database file to a destination path.

```csharp
public async Task BackupAsync(string backupFilePath)
```

```csharp
await db.BackupAsync("backups/database_backup_20260920.xml");
```

---

## 💡 Advanced Scenarios

### Working with Complex Objects & JSON

You can store arbitrary C# objects or collections directly in your row dictionary. `BasicXmlDatabase` serializes them automatically:

```csharp
public class UserSettings
{
    public string Theme { get; set; } = "Dark";
    public List<string> FavoriteTags { get; set; } = new();
}

// Inserting an object
await db.InsertAsync(new Dictionary<string, object>
{
    ["Username"] = "alex",
    ["Settings"] = new UserSettings 
    { 
        Theme = "Solarized", 
        FavoriteTags = new() { "csharp", "dotnet", "xml" } 
    }
});
```

### Building Scheduled Cleanup Tasks

Use `PurgeExpiredAsync` alongside a background timer or recurring job:

```csharp
public class CleanupService
{
    public static async Task RunDailyCleanup(XmlDatabase db)
    {
        // Purge records older than 14 days
        int removed = await db.PurgeExpiredAsync(ttlDays: 14);
        Console.WriteLine($"[Cleanup] {DateTime.UtcNow}: Purged {removed} old records.");
    }
}
```

---

## ⚖️ When to Use BasicXmlDatabase

`BasicXmlDatabase` is ideal for:

- ✅ **Local applications & CLI tools**: Configuration, local history, session caches, and user preferences.
- ✅ **Microservices & prototypes**: Fast, zero-setup storage without provisioning SQL Server, PostgreSQL, or SQLite binaries.
- ✅ **Human-readable storage**: Databases that need to be inspectable, diffable with Git, or editable by humans in standard text editors.
- ✅ **Embedded systems / desktop apps**: Zero external native C++ dependencies or architecture-specific DLLs.
- ✅ **Lightweight document stores**: Projects requiring schema-less document storage with simple LINQ querying.

*For high-concurrency enterprise workloads with millions of records, consider a traditional relational database (PostgreSQL, SQL Server) or distributed NoSQL database.*

---

## 📄 License

This project is licensed under the [MIT License](LICENSE) - see the LICENSE file for details.
