using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BasicXmlDatabase
{
    /// <summary>
    /// Implements IAsyncDisposable for proper async cleanup
    /// </summary>
    public class XmlDatabase : IAsyncDisposable
    {
        /// <summary>
        /// The file path where the XML database is stored.
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// The in-memory representation of the XML database.
        /// </summary>
        private XDocument _doc = null!;

        /// <summary>
        /// The next unique ID to assign to a new row. This is incremented with each insert.
        /// </summary>
        private long _nextId;

        /// <summary>
        /// A semaphore to ensure only one async operation can modify the database at a time.
        /// </summary>
        private readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Flag to indicate whether the object has been disposed. This is used to prevent multiple disposals and to ensure that resources are released properly.
        /// </summary>
        private bool _disposed = false;

        /// <summary>
        /// The name of the column used to store unique IDs for each row. This is a system column and should not be modified by users.
        /// </summary>
        private const string IdColumn = "id__";

        /// <summary>
        /// The name of the column used to store the last checked date for each row. This is a system column and should not be modified by users.
        /// </summary>
        private const string ChkDateColumn = "chkDate__";

        /// <summary>
        /// The name of the column used to store the table name for each row. This is a system column and defaults to "NotSet" if omitted or whitespace.
        /// </summary>
        private const string TableColumn = "table___";

        /// <summary>
        /// The default table name assigned when no valid table name is provided.
        /// </summary>
        public const string DefaultTableName = "NotSet";

        /// <summary>
        /// The header marker prepended to cell values that were serialized to JSON because they could not be stored as plain XML text.
        /// </summary>
        private const string JsonHeader = "[JSON]";

        /// <summary>
        /// The marker written into a cell when a value can neither be stored as plain XML text nor serialized to JSON.
        /// </summary>
        private const string SerializeErrorTag = "[SERIALIZE_ERROR]";

        /// <summary>
        /// Prefix used in the "type" attribute for cells stored as JSON.
        /// </summary>
        private const string JsonTypePrefix = "JSON:";

        /// <summary>
        /// Serializer options used for the JSON fallback: indented ("clean") formatting.
        /// </summary>
        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

        /// <summary>
        /// Private constructor; use CreateDBAsync to instantiate
        /// </summary>
        private XmlDatabase(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));
            _filePath = filePath;
        }

        /// <summary>
        /// Factory method to create and asynchronously load the database.
        /// </summary>
        public static async Task<XmlDatabase> CreateDBAsync(string filePath)
        {
            var db = new XmlDatabase(filePath);
            await db.InitializeAsync();
            return db;
        }

        /// <summary>
        /// Asynchronously initializes the database by loading the XML file if it exists, or creating a new XML structure if it does not.
        /// Scans for missing or invalid table names (name="table___") on existing rows and normalizes them to "NotSet".
        /// Also determines the next unique ID to assign for new rows based on existing data.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private async Task InitializeAsync()
        {
            if (File.Exists(_filePath))
            {
                // True async file read
                using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                {
                    _doc = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
                }

                _nextId = _doc.Descendants("Field")
                              .Where(f => f.Attribute("name")?.Value == IdColumn)
                              .Select(f => long.TryParse(f.Value, out long id) ? id : 0)
                              .DefaultIfEmpty(0)
                              .Max() + 1;

                // Startup scan for missing or invalid table names
                bool modified = false;
                if (_doc.Root != null)
                {
                    foreach (var rowElem in _doc.Root.Elements("Row"))
                    {
                        var tableField = rowElem.Elements("Field")
                            .FirstOrDefault(f => f.Attribute("name")?.Value == TableColumn);

                        if (tableField == null || string.IsNullOrWhiteSpace(tableField.Value))
                        {
                            if (tableField != null)
                            {
                                tableField.ReplaceWith(CreateFieldElement(TableColumn, DefaultTableName));
                            }
                            else
                            {
                                var chkDateField = rowElem.Elements("Field")
                                    .FirstOrDefault(f => f.Attribute("name")?.Value == ChkDateColumn);
                                if (chkDateField != null)
                                {
                                    chkDateField.AddAfterSelf(CreateFieldElement(TableColumn, DefaultTableName));
                                }
                                else
                                {
                                    rowElem.AddFirst(CreateFieldElement(TableColumn, DefaultTableName));
                                }
                            }
                            modified = true;
                        }
                    }
                }

                if (modified)
                {
                    await SaveAsync();
                }
            }
            else
            {
                _doc = new XDocument(new XElement("Table"));
                _nextId = 1;
                await SaveAsync();
            }
        }

        /// <summary>
        /// Normalizes a table name: if null, empty, or whitespace, returns DefaultTableName ("NotSet"); otherwise returns trimmed name.
        /// </summary>
        private static string NormalizeTableName(string? tableName)
        {
            return string.IsNullOrWhiteSpace(tableName) ? DefaultTableName : tableName.Trim();
        }

        /// <summary>
        /// Resolves the table name for a row. If tableName is specified (and not null/whitespace), it is used.
        /// Otherwise, if the row dictionary contains "table___" (and not null/whitespace), that is used.
        /// Otherwise, defaults to "NotSet".
        /// </summary>
        private static string ResolveTableName(string? tableName, Dictionary<string, object>? row)
        {
            if (!string.IsNullOrWhiteSpace(tableName))
                return tableName.Trim();

            if (row != null && row.TryGetValue(TableColumn, out object? val) && val != null)
            {
                string s = val.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }

            return DefaultTableName;
        }

        /// <summary>
        /// Checks if a parsed row matches a requested table name. If tableName is null or whitespace,
        /// returns true (matches all tables). Otherwise performs ordinal comparison with the row's table___ field.
        /// </summary>
        private static bool MatchesTable(Dictionary<string, object> row, string? tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName)) return true;
            if (row.TryGetValue(TableColumn, out object? val) && val != null)
            {
                return string.Equals(val.ToString(), tableName.Trim(), StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>
        /// Asynchronously inserts a new row into the database. The provided dictionary represents the column names and their corresponding values for the new row.
        /// Automatically assigns a unique ID, sets the current UTC date for "chkDate__", and assigns the table name ("table___", defaulting to "NotSet").
        /// </summary>
        /// <param name="row">A dictionary containing the column-value pairs for the new row.</param>
        /// <param name="tableName">Optional table name for the new row. If null or whitespace, reads from row["table___"] or defaults to "NotSet".</param>
        /// <returns>The unique ID of the newly inserted row.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the row dictionary is null.</exception>
        public async Task<long> InsertAsync(Dictionary<string, object> row, string? tableName = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            await _asyncLock.WaitAsync();
            try
            {
                long newId = _nextId++;
                string finalTable = ResolveTableName(tableName, row);
                var rowElem = new XElement("Row");

                rowElem.Add(CreateFieldElement(IdColumn, newId));
                rowElem.Add(CreateFieldElement(ChkDateColumn, DateTime.UtcNow));
                rowElem.Add(CreateFieldElement(TableColumn, finalTable));

                foreach (var kvp in row)
                {
                    if (kvp.Key == IdColumn || kvp.Key == ChkDateColumn || kvp.Key == TableColumn) continue;
                    rowElem.Add(CreateFieldElement(kvp.Key, kvp.Value));
                }

                _doc.Root!.Add(rowElem);
                await SaveAsync();
                return newId;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously inserts multiple rows in a single batch. Each row is handled like a single
        /// InsertAsync call (unique ID, current UTC "chkDate__", and "table___" are assigned automatically,
        /// and system columns are protected), but the XML file is saved only once at the end of the batch.
        /// </summary>
        /// <param name="rows">The rows to insert.</param>
        /// <param name="tableName">Optional default table name for the inserted rows. Can be overridden per row if row["table___"] is set.</param>
        /// <returns>The unique IDs of the newly inserted rows, in input order.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the rows sequence is null.</exception>
        public async Task<List<long>> InsertManyAsync(IEnumerable<Dictionary<string, object>> rows, string? tableName = null)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            await _asyncLock.WaitAsync();
            try
            {
                var newIds = new List<long>();
                foreach (var row in rows)
                {
                    if (row == null) continue;
                    long newId = _nextId++;
                    string finalTable = ResolveTableName(tableName, row);
                    var rowElem = new XElement("Row");

                    rowElem.Add(CreateFieldElement(IdColumn, newId));
                    rowElem.Add(CreateFieldElement(ChkDateColumn, DateTime.UtcNow));
                    rowElem.Add(CreateFieldElement(TableColumn, finalTable));

                    foreach (var kvp in row)
                    {
                        if (kvp.Key == IdColumn || kvp.Key == ChkDateColumn || kvp.Key == TableColumn) continue;
                        rowElem.Add(CreateFieldElement(kvp.Key, kvp.Value));
                    }

                    _doc.Root!.Add(rowElem);
                    newIds.Add(newId);
                }
                if (newIds.Count > 0) await SaveAsync();
                return newIds;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves a single row from the database by its unique ID. If tableName is specified,
        /// only returns the row if it belongs to that table.
        /// </summary>
        /// <param name="id">The unique ID of the row to retrieve.</param>
        /// <param name="tableName">Optional table name to restrict the lookup to.</param>
        /// <returns>A dictionary representing the row with the specified ID, or null if no such row exists.</returns>
        public async Task<Dictionary<string, object>?> GetByIdAsync(long id, string? tableName = null)
        {
            await _asyncLock.WaitAsync();
            try
            {
                var rowElem = FindRowById(id);
                if (rowElem == null) return null;
                var rowDict = ParseRow(rowElem);
                return MatchesTable(rowDict, tableName) ? rowDict : null;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves multiple rows from the database that match the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <param name="tableName">Optional table name to restrict the query to. If null/whitespace, searches all tables.</param>
        /// <returns>A list of dictionaries representing the rows that match the criteria.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        public async Task<List<Dictionary<string, object>>> GetManyRowsAsync(Dictionary<string, object> criteria, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var result = new List<Dictionary<string, object>>();
                if (_doc.Root == null) return result;

                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesTable(rowDict, tableName) && MatchesCriteria(rowDict, criteria))
                        result.Add(rowDict);
                }
                return result;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves a single page of rows from the database that match the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">Dictionary containing filter criteria.</param>
        /// <param name="page">1-based page number.</param>
        /// <param name="pageSize">Maximum number of rows to return.</param>
        /// <param name="orderBy">Optional column name to sort by before paging.</param>
        /// <param name="ascending">Sort direction; only used when orderBy is provided.</param>
        /// <param name="tableName">Optional table name to restrict the query to. If null/whitespace, searches all tables.</param>
        /// <returns>A list of dictionaries representing the rows on the requested page.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the page or pageSize is less than 1.</exception>
        public async Task<List<Dictionary<string, object>>> GetPageAsync(Dictionary<string, object> criteria, int page, int pageSize, string? orderBy = null, bool ascending = true, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), "Page number is 1-based and must be positive.");
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be positive.");

            await _asyncLock.WaitAsync();
            try
            {
                var list = new List<Dictionary<string, object>>();
                if (_doc.Root == null) return list;

                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesTable(rowDict, tableName) && MatchesCriteria(rowDict, criteria))
                        list.Add(rowDict);
                }

                IEnumerable<Dictionary<string, object>> matched = list;
                if (orderBy != null)
                {
                    matched = ascending
                        ? list.OrderBy(r => r.TryGetValue(orderBy, out object? v) ? v : null, RowValueComparer.Instance)
                        : list.OrderByDescending(r => r.TryGetValue(orderBy, out object? v) ? v : null, RowValueComparer.Instance);
                }

                long skip = ((long)page - 1) * pageSize;
                return matched.Skip((int)Math.Min(skip, int.MaxValue)).Take(pageSize).ToList();
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves all rows that satisfy a complex predicate and optional table name filter.
        /// </summary>
        /// <param name="predicate">A function that evaluates a parsed row and returns true to include it.</param>
        /// <param name="tableName">Optional table name to restrict the query to. If null/whitespace, searches all tables.</param>
        /// <returns>A list of dictionaries representing the rows accepted by the predicate.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the predicate is null.</exception>
        public async Task<List<Dictionary<string, object>>> GetWhereAsync(Func<Dictionary<string, object>, bool> predicate, string? tableName = null)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            await _asyncLock.WaitAsync();
            try
            {
                var result = new List<Dictionary<string, object>>();
                if (_doc.Root == null) return result;

                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesTable(rowDict, tableName) && predicate(rowDict))
                        result.Add(rowDict);
                }
                return result;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves every row in the database, or restricted to a specific table name if provided.
        /// </summary>
        /// <param name="tableName">Optional table name to restrict rows to.</param>
        /// <returns>A list of dictionaries representing all matching rows.</returns>
        public async Task<List<Dictionary<string, object>>> GetAllAsync(string? tableName = null)
        {
            return await GetManyRowsAsync(new Dictionary<string, object>(), tableName);
        }

        /// <summary>
        /// Asynchronously counts the rows that match the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <param name="tableName">Optional table name to restrict the count to.</param>
        /// <returns>The number of rows matching the criteria.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        public async Task<long> CountAsync(Dictionary<string, object> criteria, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                long count = 0;
                if (_doc.Root == null) return 0;

                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesTable(rowDict, tableName) && MatchesCriteria(rowDict, criteria)) count++;
                }
                return count;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously determines whether at least one row matches the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <param name="tableName">Optional table name to restrict the check to.</param>
        /// <returns>True if at least one row matches the criteria; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        public async Task<bool> ExistsAsync(Dictionary<string, object> criteria, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                if (_doc.Root == null) return false;

                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesTable(rowDict, tableName) && MatchesCriteria(rowDict, criteria)) return true;
                }
                return false;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously generates an HTML report of all rows matching the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <param name="tableName">Optional table name to restrict the report to.</param>
        /// <returns>An HTML string containing a table of the matching rows.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        public async Task<string> GetHTMLReportAsync(Dictionary<string, object> criteria, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var rows = new List<Dictionary<string, object>>();
                var columns = new List<string>();

                if (_doc.Root != null)
                {
                    foreach (var rowElem in _doc.Root.Elements("Row"))
                    {
                        var rowDict = ParseRow(rowElem);
                        if (!MatchesTable(rowDict, tableName) || !MatchesCriteria(rowDict, criteria)) continue;
                        rows.Add(rowDict);
                        foreach (var key in rowDict.Keys)
                            if (!columns.Contains(key)) columns.Add(key);
                    }
                }

                var sb = new System.Text.StringBuilder();
                sb.Append("<table class=\"xml-db-report\">");
                sb.Append("<thead><tr>");
                foreach (var col in columns) sb.Append($"<th>{HtmlEncode(col)}</th>");
                sb.Append("</tr></thead>");
                sb.Append("<tbody>");
                foreach (var row in rows)
                {
                    sb.Append("<tr>");
                    foreach (var col in columns)
                        sb.Append($"<td>{HtmlEncode(FormatCell(row.TryGetValue(col, out object? v) ? v : null))}</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
                return sb.ToString();
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Formats a cell value for display in an HTML report.
        /// </summary>
        private static string FormatCell(object? value)
        {
            if (value == null) return "";
            if (value is DateTime dt) return dt.ToString("o");
            return value.ToString() ?? "";
        }

        /// <summary>
        /// HTML-encodes a string for safe inclusion in an HTML report.
        /// </summary>
        private static string HtmlEncode(string? text)
        {
            return string.IsNullOrEmpty(text) ? "" : System.Net.WebUtility.HtmlEncode(text);
        }

        /// <summary>
        /// Asynchronously generates an XML report of all rows matching the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <param name="tableName">Optional table name to restrict the report to.</param>
        /// <returns>An XML string containing the matching rows.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        public async Task<string> GetXMLReportAsync(Dictionary<string, object> criteria, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var root = new XElement("Report");
                if (_doc.Root != null)
                {
                    foreach (var rowElem in _doc.Root.Elements("Row"))
                    {
                        var rowDict = ParseRow(rowElem);
                        if (!MatchesTable(rowDict, tableName) || !MatchesCriteria(rowDict, criteria)) continue;

                        var reportRow = new XElement("Row");
                        foreach (var kvp in rowDict)
                            reportRow.Add(CreateFieldElement(kvp.Key, kvp.Value));
                        root.Add(reportRow);
                    }
                }

                var report = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
                return report.ToString();
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously generates a JSON report of all rows matching the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <param name="tableName">Optional table name to restrict the report to.</param>
        /// <returns>A JSON string containing an array of the matching rows.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        public async Task<string> GetJSONReportAsync(Dictionary<string, object> criteria, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var rows = new List<Dictionary<string, object>>();
                if (_doc.Root != null)
                {
                    foreach (var rowElem in _doc.Root.Elements("Row"))
                    {
                        var rowDict = ParseRow(rowElem);
                        if (MatchesTable(rowDict, tableName) && MatchesCriteria(rowDict, criteria))
                            rows.Add(rowDict);
                    }
                }

                return System.Text.Json.JsonSerializer.Serialize(rows, JsonOptions);
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Shared implementation for the delimited export methods (CSV/TSV/PSV).
        /// </summary>
        private async Task ExportDelimitedAsync(Dictionary<string, object> criteria, string filePath, char delimiter, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));
            await _asyncLock.WaitAsync();
            try
            {
                var rows = new List<Dictionary<string, object>>();
                var columns = new List<string>();

                if (_doc.Root != null)
                {
                    foreach (var rowElem in _doc.Root.Elements("Row"))
                    {
                        var rowDict = ParseRow(rowElem);
                        if (!MatchesTable(rowDict, tableName) || !MatchesCriteria(rowDict, criteria)) continue;
                        rows.Add(rowDict);
                        foreach (var key in rowDict.Keys)
                            if (!columns.Contains(key)) columns.Add(key);
                    }
                }

                var sb = new System.Text.StringBuilder();
                sb.Append(string.Join(delimiter.ToString(), columns.Select(col => DelimitedEscape(col, delimiter))));
                sb.Append("\r\n");
                foreach (var row in rows)
                {
                    sb.Append(string.Join(delimiter.ToString(), columns.Select(col => DelimitedEscape(FormatCell(row.TryGetValue(col, out object? v) ? v : null), delimiter))));
                    sb.Append("\r\n");
                }

                await File.WriteAllTextAsync(filePath, sb.ToString());
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously exports all rows matching the specified criteria and optional table name to a CSV file.
        /// </summary>
        public async Task ExportCsvAsync(Dictionary<string, object> criteria, string csvFilePath, string? tableName = null)
        {
            await ExportDelimitedAsync(criteria, csvFilePath, ',', tableName);
        }

        /// <summary>
        /// Asynchronously exports all rows matching the specified criteria and optional table name to a tab-separated values (TSV) file.
        /// </summary>
        public async Task ExportTsvAsync(Dictionary<string, object> criteria, string tsvFilePath, string? tableName = null)
        {
            await ExportDelimitedAsync(criteria, tsvFilePath, '\t', tableName);
        }

        /// <summary>
        /// Asynchronously exports all rows matching the specified criteria and optional table name to a pipe-delimited (PSV) file.
        /// </summary>
        public async Task ExportPsvAsync(Dictionary<string, object> criteria, string psvFilePath, string? tableName = null)
        {
            await ExportDelimitedAsync(criteria, psvFilePath, '|', tableName);
        }

        /// <summary>
        /// Shared implementation for the delimited import methods (CSV/TSV/PSV).
        /// If targetTable is specified (or table___ exists in the header), rows are assigned to that table.
        /// </summary>
        private async Task<int> ImportDelimitedAsync(string filePath, char delimiter, string? targetTable = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));
            if (!File.Exists(filePath)) throw new FileNotFoundException("Delimited file not found.", filePath);

            string content;
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            using (var reader = new StreamReader(stream))
            {
                content = await reader.ReadToEndAsync();
            }

            var records = ParseDelimitedRecords(content, delimiter);
            if (records.Count == 0) return 0;

            var header = records[0];
            string defaultRowTable = NormalizeTableName(targetTable);

            await _asyncLock.WaitAsync();
            try
            {
                int imported = 0;
                for (int i = 1; i < records.Count; i++)
                {
                    var fields = records[i];
                    if (fields.Count == 1 && fields[0].Length == 0) continue; // skip blank line

                    var row = new Dictionary<string, object>();
                    string? rowTableFromCsv = null;

                    for (int c = 0; c < header.Count && c < fields.Count; c++)
                    {
                        if (header[c] == IdColumn || header[c] == ChkDateColumn) continue;
                        if (header[c] == TableColumn)
                        {
                            rowTableFromCsv = fields[c];
                            continue;
                        }
                        row[header[c]] = fields[c].Length == 0 ? null! : fields[c];
                    }

                    string finalTable = !string.IsNullOrWhiteSpace(rowTableFromCsv)
                        ? rowTableFromCsv.Trim()
                        : defaultRowTable;

                    long newId = _nextId++;
                    var rowElem = new XElement("Row");
                    rowElem.Add(CreateFieldElement(IdColumn, newId));
                    rowElem.Add(CreateFieldElement(ChkDateColumn, DateTime.UtcNow));
                    rowElem.Add(CreateFieldElement(TableColumn, finalTable));

                    foreach (var kvp in row)
                        rowElem.Add(CreateFieldElement(kvp.Key, kvp.Value));

                    _doc.Root!.Add(rowElem);
                    imported++;
                }
                if (imported > 0) await SaveAsync();
                return imported;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously imports rows from a CSV file.
        /// </summary>
        public async Task<int> ImportCsvAsync(string csvFilePath, string? targetTable = null)
        {
            return await ImportDelimitedAsync(csvFilePath, ',', targetTable);
        }

        /// <summary>
        /// Asynchronously imports rows from a tab-separated values (TSV) file.
        /// </summary>
        public async Task<int> ImportTsvAsync(string tsvFilePath, string? targetTable = null)
        {
            return await ImportDelimitedAsync(tsvFilePath, '\t', targetTable);
        }

        /// <summary>
        /// Asynchronously imports rows from a pipe-delimited (PSV) file.
        /// </summary>
        public async Task<int> ImportPsvAsync(string psvFilePath, string? targetTable = null)
        {
            return await ImportDelimitedAsync(psvFilePath, '|', targetTable);
        }

        /// <summary>
        /// Asynchronously updates a single row in the database identified by its unique ID.
        /// If tableName is specified, the row must belong to that table for the update to succeed.
        /// </summary>
        /// <param name="id">The unique ID of the row to update.</param>
        /// <param name="updates">A dictionary containing the column-value pairs to update in the row.</param>
        /// <param name="tableName">Optional table name to verify the row belongs to.</param>
        /// <returns>True if the update was successful; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public async Task<bool> UpdateByIdAsync(long id, Dictionary<string, object> updates, string? tableName = null)
        {
            if (updates == null) throw new ArgumentNullException(nameof(updates));
            await _asyncLock.WaitAsync();
            try
            {
                var rowElem = FindRowById(id);
                if (rowElem == null) return false;

                var rowDict = ParseRow(rowElem);
                if (!MatchesTable(rowDict, tableName)) return false;

                UpdateSystemField(rowElem, ChkDateColumn, DateTime.UtcNow);

                if (updates.TryGetValue(TableColumn, out object? newTableObj))
                {
                    UpdateSystemField(rowElem, TableColumn, NormalizeTableName(newTableObj?.ToString()));
                }

                ApplyUpdates(rowElem, updates);

                await SaveAsync();
                return true;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously updates multiple rows in the database that match the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">Dictionary containing the criteria for selecting rows to update.</param>
        /// <param name="values">Dictionary containing the column-value pairs to update in the matching rows.</param>
        /// <param name="tableName">Optional table name to restrict updates to.</param>
        /// <returns>A list of unique IDs of the rows that were successfully updated.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public async Task<List<long>> UpdateManyRowsAsync(Dictionary<string, object> criteria, Dictionary<string, object> values, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            if (values == null) throw new ArgumentNullException(nameof(values));

            await _asyncLock.WaitAsync();
            try
            {
                var updatedIds = new List<long>();
                bool anyUpdated = false;

                if (_doc.Root != null)
                {
                    foreach (var rowElem in _doc.Root.Elements("Row"))
                    {
                        var rowDict = ParseRow(rowElem);
                        if (MatchesTable(rowDict, tableName) && MatchesCriteria(rowDict, criteria))
                        {
                            if (rowDict.TryGetValue(IdColumn, out object? idObj) && idObj is long idVal)
                                updatedIds.Add(idVal);

                            UpdateSystemField(rowElem, ChkDateColumn, DateTime.UtcNow);

                            if (values.TryGetValue(TableColumn, out object? newTableObj))
                            {
                                UpdateSystemField(rowElem, TableColumn, NormalizeTableName(newTableObj?.ToString()));
                            }

                            ApplyUpdates(rowElem, values);
                            anyUpdated = true;
                        }
                    }
                }
                if (anyUpdated) await SaveAsync();
                return updatedIds;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously replaces a single row in the database identified by its unique ID.
        /// If tableName is specified, the row must belong to that table.
        /// </summary>
        /// <param name="id">The unique ID of the row to replace.</param>
        /// <param name="row">A dictionary containing the complete column-value pairs for the replacement row.</param>
        /// <param name="tableName">Optional table name to verify the row belongs to.</param>
        /// <returns>True if the row was replaced; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the row dictionary is null.</exception>
        public async Task<bool> ReplaceAsync(long id, Dictionary<string, object> row, string? tableName = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            await _asyncLock.WaitAsync();
            try
            {
                var oldRow = FindRowById(id);
                if (oldRow == null) return false;

                var oldDict = ParseRow(oldRow);
                if (!MatchesTable(oldDict, tableName)) return false;

                // Determine replacement table name
                string finalTable;
                if (row.TryGetValue(TableColumn, out object? tVal) && !string.IsNullOrWhiteSpace(tVal?.ToString()))
                {
                    finalTable = tVal.ToString()!.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(tableName))
                {
                    finalTable = tableName.Trim();
                }
                else if (oldDict.TryGetValue(TableColumn, out object? oldT) && !string.IsNullOrWhiteSpace(oldT?.ToString()))
                {
                    finalTable = oldT.ToString()!.Trim();
                }
                else
                {
                    finalTable = DefaultTableName;
                }

                var newRow = new XElement("Row");
                newRow.Add(CreateFieldElement(IdColumn, id));
                newRow.Add(CreateFieldElement(ChkDateColumn, DateTime.UtcNow));
                newRow.Add(CreateFieldElement(TableColumn, finalTable));

                foreach (var kvp in row)
                {
                    if (kvp.Key == IdColumn || kvp.Key == ChkDateColumn || kvp.Key == TableColumn) continue;
                    newRow.Add(CreateFieldElement(kvp.Key, kvp.Value));
                }

                oldRow.ReplaceWith(newRow);
                await SaveAsync();
                return true;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously deletes a single row from the database identified by its unique ID.
        /// If tableName is specified, only deletes the row if it belongs to that table.
        /// </summary>
        /// <param name="id">The unique ID of the row to delete.</param>
        /// <param name="tableName">Optional table name to verify before deletion.</param>
        /// <returns>True if the row was successfully deleted; otherwise, false.</returns>
        public async Task<bool> DeleteByIdAsync(long id, string? tableName = null)
        {
            await _asyncLock.WaitAsync();
            try
            {
                var rowElem = FindRowById(id);
                if (rowElem == null) return false;

                var rowDict = ParseRow(rowElem);
                if (!MatchesTable(rowDict, tableName)) return false;

                rowElem.Remove();
                await SaveAsync();
                return true;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously deletes multiple rows from the database that match the specified criteria and optional table name.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <param name="tableName">Optional table name to restrict deletions to.</param>
        /// <returns>A list of unique IDs of the rows that were successfully deleted.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        public async Task<List<long>> DeleteManyRowsAsync(Dictionary<string, object> criteria, string? tableName = null)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var deletedIds = new List<long>();
                var rowsToRemove = new List<XElement>();

                if (_doc.Root != null)
                {
                    foreach (var rowElem in _doc.Root.Elements("Row"))
                    {
                        var rowDict = ParseRow(rowElem);
                        if (MatchesTable(rowDict, tableName) && MatchesCriteria(rowDict, criteria))
                        {
                            if (rowDict.TryGetValue(IdColumn, out object? idObj) && idObj is long idVal)
                                deletedIds.Add(idVal);
                            rowsToRemove.Add(rowElem);
                        }
                    }
                }
                foreach (var row in rowsToRemove) row.Remove();
                if (rowsToRemove.Count > 0) await SaveAsync();
                return deletedIds;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously purges rows from the database that have a "chkDate__" value older than 
        /// the specified number of days (ttlDays), optionally filtered by table name.
        /// </summary>
        /// <param name="ttlDays">The number of days to use as the time-to-live (TTL) for rows.</param>
        /// <param name="tableName">Optional table name to restrict the purge to.</param>
        /// <returns>The count of rows that were removed.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if ttlDays is negative.</exception>
        public async Task<int> PurgeExpiredAsync(int ttlDays, string? tableName = null)
        {
            if (ttlDays < 0) throw new ArgumentOutOfRangeException(nameof(ttlDays));
            await _asyncLock.WaitAsync();
            try
            {
                DateTime cutoffDate = DateTime.UtcNow.AddDays(-ttlDays);
                var rowsToRemove = new List<XElement>();

                if (_doc.Root != null)
                {
                    foreach (var rowElem in _doc.Root.Elements("Row"))
                    {
                        var rowDict = ParseRow(rowElem);
                        if (!MatchesTable(rowDict, tableName)) continue;

                        var chkDateField = rowElem.Elements("Field")
                            .FirstOrDefault(f => f.Attribute("name")?.Value == ChkDateColumn);

                        DateTime rowDate = DateTime.MinValue;
                        if (chkDateField != null && ParseValue(chkDateField.Value, chkDateField.Attribute("type")?.Value) is DateTime dt)
                            rowDate = dt;

                        if (rowDate < cutoffDate) rowsToRemove.Add(rowElem);
                    }
                }

                foreach (var row in rowsToRemove) row.Remove();
                if (rowsToRemove.Count > 0) await SaveAsync();
                return rowsToRemove.Count;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously creates a backup copy of the database file.
        /// </summary>
        /// <param name="backupFilePath">The path of the backup file to create.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="ArgumentException">Thrown if the backup path is null, whitespace, or identical to the live database path.</exception>
        public async Task BackupAsync(string backupFilePath)
        {
            if (string.IsNullOrWhiteSpace(backupFilePath)) throw new ArgumentException("Backup file path cannot be null or whitespace.", nameof(backupFilePath));
            if (string.Equals(Path.GetFullPath(backupFilePath), Path.GetFullPath(_filePath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Backup path must differ from the live database path.", nameof(backupFilePath));

            await _asyncLock.WaitAsync();
            try
            {
                await SaveAsync();
                using (var source = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                using (var dest = new FileStream(backupFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await source.CopyToAsync(dest);
                }
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously disposes of the resources used by the XmlDatabase instance.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _asyncLock.Dispose();
            }
            await Task.CompletedTask;
        }

        #region Private Async Helpers

        /// <summary>
        /// Asynchronously saves the in-memory XML document to the file specified by _filePath atomically.
        /// </summary>
        private async Task SaveAsync()
        {
            var settings = new System.Xml.XmlWriterSettings { Indent = true, Async = true };
            string tempPath = _filePath + "." + Environment.ProcessId + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                using (var writer = System.Xml.XmlWriter.Create(stream, settings))
                {
                    await _doc.SaveAsync(writer, CancellationToken.None);
                }
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        #endregion

        #region Private Sync Helpers (In-Memory Operations)

        /// <summary>
        /// Applies the specified updates to the given row element in the XML document.
        /// System columns (id__, chkDate__, table___) are handled separately.
        /// </summary>
        private void ApplyUpdates(XElement rowElem, Dictionary<string, object> updates)
        {
            foreach (var kvp in updates)
            {
                if (kvp.Key == IdColumn || kvp.Key == ChkDateColumn || kvp.Key == TableColumn) continue;
                var existingField = rowElem.Elements("Field").FirstOrDefault(f => f.Attribute("name")?.Value == kvp.Key);
                if (existingField != null) existingField.ReplaceWith(CreateFieldElement(kvp.Key, kvp.Value));
                else rowElem.Add(CreateFieldElement(kvp.Key, kvp.Value));
            }
        }

        /// <summary>
        /// Finds a row in the XML document by its unique ID.
        /// </summary>
        private XElement? FindRowById(long id)
        {
            return _doc.Root?.Elements("Row")
                .FirstOrDefault(row => row.Elements("Field")
                    .Any(f => f.Attribute("name")?.Value == IdColumn && f.Value == id.ToString()));
        }

        /// <summary>
        /// Determines whether a given row matches the specified criteria.
        /// </summary>
        private bool MatchesCriteria(Dictionary<string, object> row, Dictionary<string, object> criteria)
        {
            foreach (var kvp in criteria)
            {
                bool keyFound = false;
                foreach (var rowKvp in row)
                {
                    if (string.Equals(rowKvp.Key, kvp.Key, StringComparison.Ordinal))
                    {
                        keyFound = true;
                        if (!object.Equals(rowKvp.Value, kvp.Value)) return false;
                        break;
                    }
                }
                if (!keyFound) return false;
            }
            return true;
        }

        /// <summary>
        /// Updates a system field in the specified row element.
        /// </summary>
        private void UpdateSystemField(XElement rowElem, string fieldName, object value)
        {
            var existingField = rowElem.Elements("Field").FirstOrDefault(f => f.Attribute("name")?.Value == fieldName);
            if (existingField != null) existingField.ReplaceWith(CreateFieldElement(fieldName, value));
            else rowElem.Add(CreateFieldElement(fieldName, value));
        }

        /// <summary>
        /// Parses a row XElement into a dictionary representation.
        /// </summary>
        private Dictionary<string, object> ParseRow(XElement rowElem)
        {
            var dict = new Dictionary<string, object>();
            foreach (var field in rowElem.Elements("Field"))
            {
                var name = field.Attribute("name")?.Value;
                if (name != null) dict[name] = ParseValue(field.Value, field.Attribute("type")?.Value)!;
            }
            return dict;
        }

        /// <summary>
        /// Creates an XElement representing a field in the XML document.
        /// </summary>
        private XElement CreateFieldElement(string name, object? value)
        {
            if (value == null || IsXmlSafeValue(value))
            {
                string typeName = value?.GetType().FullName ?? "Null";
                string textValue = value?.ToString() ?? "";
                if (value is DateTime dt) textValue = dt.ToString("o");
                return new XElement("Field", new XAttribute("name", name), new XAttribute("type", typeName), textValue);
            }

            // Value cannot be stored as plain XML text: try JSON with clean formatting
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(value, JsonOptions);
                return new XElement("Field",
                    new XAttribute("name", name),
                    new XAttribute("type", JsonTypePrefix + value.GetType().FullName),
                    JsonHeader + "\n" + json);
            }
            catch
            {
                return new XElement("Field",
                    new XAttribute("name", name),
                    new XAttribute("type", "System.String"),
                    SerializeErrorTag);
            }
        }

        /// <summary>
        /// Determines whether a value can be stored directly as XML text.
        /// </summary>
        private static bool IsXmlSafeValue(object value)
        {
            switch (value)
            {
                case string s: return IsValidXmlText(s);
                case DateTime _: case DateTimeOffset _: case decimal _: case Guid _: case TimeSpan _: case Uri _: return true;
                default:
                    var type = value.GetType();
                    return type.IsPrimitive || type.IsEnum;
            }
        }

        /// <summary>
        /// Checks whether every character in the string is legal in XML 1.0 text content.
        /// </summary>
        private static bool IsValidXmlText(string text)
        {
            foreach (char c in text)
            {
                if (c == 0x9 || c == 0xA || c == 0xD) continue;
                if (c < 0x20 || c == 0xFFFE || c == 0xFFFF) return false;
            }
            return true;
        }

        /// <summary>
        /// Parses a string value into its appropriate type based on the provided type name.
        /// </summary>
        private object? ParseValue(string text, string? typeName)
        {
            if (typeName == "Null" || (string.IsNullOrEmpty(text) && typeName == "System.String")) return null;
            Type targetType = typeof(string);
            switch (typeName)
            {
                case "System.Int32": targetType = typeof(int); break;
                case "System.Int64": targetType = typeof(long); break;
                case "System.Double": targetType = typeof(double); break;
                case "System.Boolean": targetType = typeof(bool); break;
                case "System.DateTime": targetType = typeof(DateTime); break;
                case "System.Decimal": targetType = typeof(decimal); break;
                case "System.Single": targetType = typeof(float); break;
            }
            if (targetType == typeof(string)) return text;
            try
            {
                if (targetType == typeof(DateTime)) return DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind);
                return Convert.ChangeType(text, targetType);
            }
            catch { return text; }
        }

        /// <summary>
        /// Escapes a single delimited field per RFC 4180.
        /// </summary>
        private static string DelimitedEscape(string text, char delimiter)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.IndexOfAny(new[] { delimiter, '"', '\r', '\n' }) < 0) return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// Parses delimited text into records of fields, following RFC 4180.
        /// </summary>
        private static List<List<string>> ParseDelimitedRecords(string content, char delimiter)
        {
            var records = new List<List<string>>();
            if (string.IsNullOrEmpty(content)) return records;

            var fields = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            bool fieldStarted = false;

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < content.Length && content[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"' && !fieldStarted)
                {
                    inQuotes = true;
                    fieldStarted = true;
                }
                else if (c == delimiter)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                    fieldStarted = false;
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                    fields.Add(sb.ToString());
                    sb.Clear();
                    fieldStarted = false;
                    records.Add(fields);
                    fields = new List<string>();
                }
                else
                {
                    sb.Append(c);
                    fieldStarted = true;
                }
            }

            if (fieldStarted || fields.Count > 0)
            {
                fields.Add(sb.ToString());
                records.Add(fields);
            }
            return records;
        }

        /// <summary>
        /// Compares two cell values for ordering.
        /// </summary>
        private sealed class RowValueComparer : IComparer<object?>
        {
            public static readonly RowValueComparer Instance = new RowValueComparer();

            public int Compare(object? x, object? y)
            {
                if (x == null) return y == null ? 0 : -1;
                if (y == null) return 1;
                if (x.GetType() == y.GetType() && x is IComparable comparable) return comparable.CompareTo(y);
                return string.CompareOrdinal(x.ToString(), y.ToString());
            }
        }

        #endregion
    }
}
