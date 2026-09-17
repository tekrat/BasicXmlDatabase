using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BasicXmlDatabase
{
    // Implements IAsyncDisposable for proper async cleanup
    public class XmlDatabase : IAsyncDisposable
    {
        /// <summary>
        /// The file path where the XML database is stored.
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// The in-memory representation of the XML database.
        /// </summary>
        private XDocument _doc;

        /// <summary>
        /// The next unique ID to assign to a new row. This is incremented with each insert.
        /// </summary>
        private long _nextId;

        /// <summary>
        /// A semaphore to ensure only one async operation can modify the database at a time.
        /// </summary>
        private readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Flag to indicate whether the object has been disposed.
        /// </summary>
        private bool _disposed = false;

        /// <summary>
        /// The name of the column used to store unique IDs for each row.
        /// </summary>
        private const string IdColumn = "id__";

        /// <summary>
        /// The name of the column used to store the last checked date for each row.
        /// </summary>
        private const string ChkDateColumn = "chkDate__";

        /// <summary>
        /// The header marker prepended to cell values that were serialized to JSON.
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
        /// Private constructor; use CreateDBAsync to instantiate.
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
        /// Asynchronously initializes the database by loading the XML file or creating a new structure.
        /// </summary>
        private async Task InitializeAsync()
        {
            if (File.Exists(_filePath))
            {
                using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                _doc = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
                _nextId = _doc.Descendants("Field")
                              .Where(f => f.Attribute("name")?.Value == IdColumn)
                              .Select(f => long.TryParse(f.Value, out long id) ? id : 0)
                              .DefaultIfEmpty(0)
                              .Max() + 1;
            }
            else
            {
                _doc = new XDocument(new XElement("Table"));
                _nextId = 1;
                await SaveAsync();
            }
        }

        /// <summary>
        /// Asynchronously inserts a new row into the database.
        /// </summary>
        public async Task<long> InsertAsync(Dictionary<string, object> row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            await _asyncLock.WaitAsync();
            try
            {
                long newId = _nextId++;
                var rowElem = new XElement("Row");
                rowElem.Add(CreateFieldElement(IdColumn, newId));
                rowElem.Add(CreateFieldElement(ChkDateColumn, DateTime.UtcNow));
                foreach (var kvp in row)
                {
                    if (kvp.Key == IdColumn || kvp.Key == ChkDateColumn) continue;
                    rowElem.Add(CreateFieldElement(kvp.Key, kvp.Value));
                }
                _doc.Root.Add(rowElem);
                await SaveAsync();
                return newId;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously inserts multiple rows into the database in a single atomic operation.
        /// </summary>
        public async Task<List<long>> InsertManyAsync(IEnumerable<Dictionary<string, object>> rows)
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
                    var rowElem = new XElement("Row");
                    rowElem.Add(CreateFieldElement(IdColumn, newId));
                    rowElem.Add(CreateFieldElement(ChkDateColumn, DateTime.UtcNow));
                    foreach (var kvp in row)
                    {
                        if (kvp.Key == IdColumn || kvp.Key == ChkDateColumn) continue;
                        rowElem.Add(CreateFieldElement(kvp.Key, kvp.Value));
                    }
                    _doc.Root.Add(rowElem);
                    newIds.Add(newId);
                }
                if (newIds.Count > 0) await SaveAsync();
                return newIds;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves a single row from the database by its unique ID.
        /// </summary>
        public async Task<Dictionary<string, object>> GetByIdAsync(long id)
        {
            await _asyncLock.WaitAsync();
            try
            {
                var rowElem = FindRowById(id);
                return rowElem != null ? ParseRow(rowElem) : null;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves all rows from the database.
        /// </summary>
        public async Task<List<Dictionary<string, object>>> GetAllAsync()
        {
            await _asyncLock.WaitAsync();
            try
            {
                var result = new List<Dictionary<string, object>>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    result.Add(ParseRow(rowElem));
                }
                return result;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves multiple rows from the database that match the specified criteria.
        /// </summary>
        public async Task<List<Dictionary<string, object>>> GetManyRowsAsync(Dictionary<string, object> criteria)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var result = new List<Dictionary<string, object>>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesCriteria(rowDict, criteria)) result.Add(rowDict);
                }
                return result;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves rows matching a custom predicate function, enabling complex queries (>, <, OR, Contains, etc.).
        /// </summary>
        public async Task<List<Dictionary<string, object>>> GetWhereAsync(Func<Dictionary<string, object>, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            await _asyncLock.WaitAsync();
            try
            {
                var result = new List<Dictionary<string, object>>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (predicate(rowDict)) result.Add(rowDict);
                }
                return result;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves a single page of rows from the database that match the specified criteria.
        /// </summary>
        public async Task<List<Dictionary<string, object>>> GetPageAsync(Dictionary<string, object> criteria, int page, int pageSize, string orderBy = null, bool ascending = true)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), "Page number is 1-based and must be positive.");
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be positive.");
            await _asyncLock.WaitAsync();
            try
            {
                var list = new List<Dictionary<string, object>>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesCriteria(rowDict, criteria)) list.Add(rowDict);
                }
                IEnumerable<Dictionary<string, object>> matched = list;
                if (orderBy != null)
                {
                    matched = ascending
                        ? list.OrderBy(r => r.TryGetValue(orderBy, out object v) ? v : null, RowValueComparer.Instance)
                        : list.OrderByDescending(r => r.TryGetValue(orderBy, out object v) ? v : null, RowValueComparer.Instance);
                }
                long skip = ((long)page - 1) * pageSize;
                return matched.Skip((int)Math.Min(skip, int.MaxValue)).Take(pageSize).ToList();
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously returns the number of rows matching the specified criteria.
        /// </summary>
        public async Task<int> CountAsync(Dictionary<string, object> criteria)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                int count = 0;
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesCriteria(rowDict, criteria)) count++;
                }
                return count;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously checks if a row with the specified ID exists.
        /// </summary>
        public async Task<bool> ExistsAsync(long id)
        {
            await _asyncLock.WaitAsync();
            try
            {
                return FindRowById(id) != null;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously generates an HTML report of all rows matching the specified criteria.
        /// </summary>
        public async Task<string> GetHTMLReportAsync(Dictionary<string, object> criteria)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var rows = new List<Dictionary<string, object>>();
                var columns = new List<string>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (!MatchesCriteria(rowDict, criteria)) continue;
                    rows.Add(rowDict);
                    foreach (var key in rowDict.Keys)
                        if (!columns.Contains(key)) columns.Add(key);
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
                        sb.Append($"<td>{HtmlEncode(FormatCell(row.TryGetValue(col, out object v) ? v : null))}</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
                return sb.ToString();
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously generates an XML report of all rows matching the specified criteria.
        /// </summary>
        public async Task<string> GetXMLReportAsync(Dictionary<string, object> criteria)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var root = new XElement("Report");
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (!MatchesCriteria(rowDict, criteria)) continue;
                    var reportRow = new XElement("Row");
                    foreach (var kvp in rowDict)
                        reportRow.Add(CreateFieldElement(kvp.Key, kvp.Value));
                    root.Add(reportRow);
                }
                var report = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
                return report.ToString();
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously generates a JSON report of all rows matching the specified criteria.
        /// </summary>
        public async Task<string> GetJSONReportAsync(Dictionary<string, object> criteria)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var rows = new List<Dictionary<string, object>>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesCriteria(rowDict, criteria)) rows.Add(rowDict);
                }
                return System.Text.Json.JsonSerializer.Serialize(rows, JsonOptions);
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously generates a CSV report of all rows matching the specified criteria.
        /// </summary>
        public async Task<string> GetCSVReportAsync(Dictionary<string, object> criteria)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var rows = new List<Dictionary<string, object>>();
                var columns = new List<string>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (!MatchesCriteria(rowDict, criteria)) continue;
                    rows.Add(rowDict);
                    foreach (var key in rowDict.Keys)
                        if (!columns.Contains(key)) columns.Add(key);
                }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Join(",", columns.Select(CsvEscape)));
                foreach (var row in rows)
                {
                    var values = columns.Select(col => CsvEscape(FormatCell(row.TryGetValue(col, out object v) ? v : null)));
                    sb.AppendLine(string.Join(",", values));
                }
                return sb.ToString();
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously parses and imports rows from a CSV string. First row is treated as headers.
        /// </summary>
        public async Task<int> ImportCSVAsync(string csvContent)
        {
            if (string.IsNullOrWhiteSpace(csvContent)) throw new ArgumentException("CSV content cannot be null or whitespace.", nameof(csvContent));
            await _asyncLock.WaitAsync();
            try
            {
                var lines = new List<string>();
                using (var reader = new StringReader(csvContent))
                {
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        lines.Add(line);
                    }
                }
                if (lines.Count < 2) return 0;

                var headers = ParseCsvLine(lines[0]);
                int importedCount = 0;

                for (int i = 1; i < lines.Count; i++)
                {
                    var values = ParseCsvLine(lines[i]);
                    if (values.Count == 0) continue;

                    long newId = _nextId++;
                    var rowElem = new XElement("Row");
                    rowElem.Add(CreateFieldElement(IdColumn, newId));
                    rowElem.Add(CreateFieldElement(ChkDateColumn, DateTime.UtcNow));

                    for (int j = 0; j < headers.Count; j++)
                    {
                        string header = headers[j];
                        if (header == IdColumn || header == ChkDateColumn) continue;
                        string val = j < values.Count ? values[j] : "";
                        rowElem.Add(CreateFieldElement(header, ParseCsvValue(val)));
                    }
                    _doc.Root.Add(rowElem);
                    importedCount++;
                }
                if (importedCount > 0) await SaveAsync();
                return importedCount;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously updates a single row in the database identified by its unique ID.
        /// </summary>
        public async Task<bool> UpdateByIdAsync(long id, Dictionary<string, object> updates)
        {
            if (updates == null) throw new ArgumentNullException(nameof(updates));
            await _asyncLock.WaitAsync();
            try
            {
                var rowElem = FindRowById(id);
                if (rowElem == null) return false;
                UpdateSystemField(rowElem, ChkDateColumn, DateTime.UtcNow);
                ApplyUpdates(rowElem, updates);
                await SaveAsync();
                return true;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously replaces all user data in a row, preserving only the system ID and updating the check date.
        /// </summary>
        public async Task<bool> ReplaceAsync(long id, Dictionary<string, object> newRow)
        {
            if (newRow == null) throw new ArgumentNullException(nameof(newRow));
            await _asyncLock.WaitAsync();
            try
            {
                var rowElem = FindRowById(id);
                if (rowElem == null) return false;

                var idField = rowElem.Elements("Field").FirstOrDefault(f => f.Attribute("name")?.Value == IdColumn);
                rowElem.RemoveNodes(); 
                
                if (idField != null) rowElem.Add(idField);
                rowElem.Add(CreateFieldElement(ChkDateColumn, DateTime.UtcNow));

                foreach (var kvp in newRow)
                {
                    if (kvp.Key == IdColumn || kvp.Key == ChkDateColumn) continue;
                    rowElem.Add(CreateFieldElement(kvp.Key, kvp.Value));
                }
                await SaveAsync();
                return true;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously updates multiple rows in the database that match the specified criteria.
        /// </summary>
        public async Task<List<long>> UpdateManyRowsAsync(Dictionary<string, object> criteria, Dictionary<string, object> values)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            if (values == null) throw new ArgumentNullException(nameof(values));
            await _asyncLock.WaitAsync();
            try
            {
                var updatedIds = new List<long>();
                bool anyUpdated = false;
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesCriteria(rowDict, criteria))
                    {
                        if (rowDict.TryGetValue(IdColumn, out object idObj) && idObj is long idVal)
                            updatedIds.Add(idVal);
                        UpdateSystemField(rowElem, ChkDateColumn, DateTime.UtcNow);
                        ApplyUpdates(rowElem, values);
                        anyUpdated = true;
                    }
                }
                if (anyUpdated) await SaveAsync();
                return updatedIds;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously deletes a single row from the database identified by its unique ID.
        /// </summary>
        public async Task<bool> DeleteByIdAsync(long id)
        {
            await _asyncLock.WaitAsync();
            try
            {
                var rowElem = FindRowById(id);
                if (rowElem == null) return false;
                rowElem.Remove();
                await SaveAsync();
                return true;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously deletes multiple rows from the database that match the specified criteria.
        /// </summary>
        public async Task<List<long>> DeleteManyRowsAsync(Dictionary<string, object> criteria)
        {
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            await _asyncLock.WaitAsync();
            try
            {
                var deletedIds = new List<long>();
                var rowsToRemove = new List<XElement>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var rowDict = ParseRow(rowElem);
                    if (MatchesCriteria(rowDict, criteria))
                    {
                        if (rowDict.TryGetValue(IdColumn, out object idObj) && idObj is long idVal)
                            deletedIds.Add(idVal);
                        rowsToRemove.Add(rowElem);
                    }
                }
                foreach (var row in rowsToRemove) row.Remove();
                if (rowsToRemove.Count > 0) await SaveAsync();
                return deletedIds;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously purges rows from the database that have a "chkDate__" value older than the specified number of days.
        /// </summary>
        public async Task<int> PurgeExpiredAsync(int ttlDays)
        {
            if (ttlDays < 0) throw new ArgumentOutOfRangeException(nameof(ttlDays));
            await _asyncLock.WaitAsync();
            try
            {
                DateTime cutoffDate = DateTime.UtcNow.AddDays(-ttlDays);
                var rowsToRemove = new List<XElement>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                {
                    var chkDateField = rowElem.Elements("Field")
                        .FirstOrDefault(f => f.Attribute("name")?.Value == ChkDateColumn);
                    DateTime rowDate = DateTime.MinValue;
                    if (chkDateField != null && ParseValue(chkDateField.Value, chkDateField.Attribute("type")?.Value) is DateTime dt)
                        rowDate = dt;
                    if (rowDate < cutoffDate) rowsToRemove.Add(rowElem);
                }
                foreach (var row in rowsToRemove) row.Remove();
                if (rowsToRemove.Count > 0) await SaveAsync();
                return rowsToRemove.Count;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously creates a safe, atomic backup copy of the current database file.
        /// </summary>
        public async Task BackupAsync(string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path cannot be null or whitespace.", nameof(destinationPath));
            await _asyncLock.WaitAsync();
            try
            {
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempPath = destinationPath + "." + Environment.ProcessId + ".tmp";
                try
                {
                    var settings = new System.Xml.XmlWriterSettings { Indent = true, Async = true };
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                    using (var writer = System.Xml.XmlWriter.Create(stream, settings))
                    {
                        await _doc.SaveAsync(writer, CancellationToken.None);
                    }
                    File.Move(tempPath, destinationPath, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw;
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
        }

        #region Private Async Helpers

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

        private void ApplyUpdates(XElement rowElem, Dictionary<string, object> updates)
        {
            foreach (var kvp in updates)
            {
                if (kvp.Key == IdColumn || kvp.Key == ChkDateColumn) continue;
                var existingField = rowElem.Elements("Field").FirstOrDefault(f => f.Attribute("name")?.Value == kvp.Key);
                if (existingField != null) existingField.ReplaceWith(CreateFieldElement(kvp.Key, kvp.Value));
                else rowElem.Add(CreateFieldElement(kvp.Key, kvp.Value));
            }
        }

        private XElement FindRowById(long id)
        {
            return _doc.Root.Elements("Row")
                .FirstOrDefault(row => row.Elements("Field")
                    .Any(f => f.Attribute("name")?.Value == IdColumn && f.Value == id.ToString()));
        }

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

        private void UpdateSystemField(XElement rowElem, string fieldName, object value)
        {
            var existingField = rowElem.Elements("Field").FirstOrDefault(f => f.Attribute("name")?.Value == fieldName);
            if (existingField != null) existingField.ReplaceWith(CreateFieldElement(fieldName, value));
            else rowElem.Add(CreateFieldElement(fieldName, value));
        }

        private Dictionary<string, object> ParseRow(XElement rowElem)
        {
            var dict = new Dictionary<string, object>();
            foreach (var field in rowElem.Elements("Field"))
            {
                var name = field.Attribute("name")?.Value;
                if (name != null) dict[name] = ParseValue(field.Value, field.Attribute("type")?.Value);
            }
            return dict;
        }

        private XElement CreateFieldElement(string name, object value)
        {
            if (value == null || IsXmlSafeValue(value))
            {
                string typeName = value?.GetType().FullName ?? "Null";
                string textValue = value?.ToString() ?? "";
                if (value is DateTime dt) textValue = dt.ToString("o");
                return new XElement("Field", new XAttribute("name", name), new XAttribute("type", typeName), textValue);
            }
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

        private static bool IsValidXmlText(string text)
        {
            foreach (char c in text)
            {
                if (c == 0x9 || c == 0xA || c == 0xD) continue;
                if (c < 0x20 || c == 0xFFFE || c == 0xFFFF) return false;
            }
            return true;
        }

        private object ParseValue(string text, string typeName)
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

        private static string FormatCell(object value)
        {
            if (value == null) return "";
            if (value is DateTime dt) return dt.ToString("o");
            return value.ToString();
        }

        private static string HtmlEncode(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : System.Net.WebUtility.HtmlEncode(text);
        }

        private static string CsvEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\"\"";
            if (text.Contains(",") || text.Contains("\"") || text.Contains("\n") || text.Contains("\r"))
            {
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            return text;
        }

        private List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(line)) return result;

            bool inQuotes = false;
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            result.Add(current.ToString());
            return result;
        }

        private object ParseCsvValue(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (bool.TryParse(text, out bool b)) return b;
            if (int.TryParse(text, out int i)) return i;
            if (long.TryParse(text, out long l)) return l;
            if (double.TryParse(text, out double d)) return d;
            if (DateTime.TryParse(text, out DateTime dt)) return dt;
            return text;
        }

        private sealed class RowValueComparer : IComparer<object>
        {
            public static readonly RowValueComparer Instance = new RowValueComparer();
            public int Compare(object x, object y)
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
