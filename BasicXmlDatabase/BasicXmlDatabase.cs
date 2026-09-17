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
        /// Asynchronously initializes the database by loading the XML file if it exists, or creating a new XML structure if it does not. It also determines the next unique ID to assign for new rows based on existing data.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private async Task InitializeAsync()
        {
            if (File.Exists(_filePath))
            {
                // True async file read
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
        /// Asynchronously inserts a new row into the database. The provided dictionary represents the column names and their corresponding values for the new row. The method automatically assigns a unique ID and sets the current UTC date for the "chkDate__" system column. It returns the unique ID of the newly inserted row.
        /// </summary>
        /// <param name="row">A dictionary containing the column-value pairs for the new row.</param>
        /// <returns>The unique ID of the newly inserted row.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the row dictionary is null.</exception>
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
        /// Asynchronously retrieves a single row from the database by its unique ID. If a row with the specified ID exists, it returns a dictionary representing that row; otherwise, it returns null. The dictionary contains column names as keys and their corresponding values.
        /// </summary>
        /// <param name="id">The unique ID of the row to retrieve.</param>
        /// <returns>A dictionary representing the row with the specified ID, or null if no such row exists.</returns>
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
        /// Asynchronously retrieves multiple rows from the database that match the specified criteria. The criteria are provided as a dictionary where keys are column names and values are the expected values for those columns. The method returns a list of dictionaries, each representing a row that meets the criteria. If no rows match, an empty list is returned.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <returns>A list of dictionaries representing the rows that match the criteria.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
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
        /// Asynchronously retrieves a single page of rows from the database that match the specified criteria. This is the bounded way to page through larger result sets: at most pageSize rows are materialized per call. The criteria are provided as a dictionary where keys are column names and values are the expected values for those columns. The page number is 1-based; page and pageSize must both be positive. An optional orderBy column name and ascending flag control the sort order before paging is applied. Values are compared using their natural type order when possible; rows where the orderBy column is missing or null sort first, and mixed types fall back to ordinal string comparison of their text representation. The method returns a list of dictionaries representing the rows on the requested page. If no rows match, an empty list is returned.
        /// </summary>
        /// <param name="criteria"></param>
        /// <param name="page">1-based page number.</param>
        /// <param name="pageSize">Maximum number of rows to return.</param>
        /// <param name="orderBy">Optional column name to sort by before paging.</param>
        /// <param name="ascending">Sort direction; only used when orderBy is provided.</param>
        /// <returns>A list of dictionaries representing the rows on the requested page.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the page or pageSize is less than 1.</exception>
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
        /// Asynchronously generates an HTML report of all rows in the database that match the specified criteria. The report consists of a single HTML table: the columns are the union of all column names across the matching rows (in order of first appearance, with the system columns first since they exist on every row), and each matching row becomes one table row. All cell content is HTML-encoded, null values render as empty cells, and DateTime values are formatted in ISO 8601 format. No rows match, an empty table body is produced. The method returns the complete HTML table markup as a string; the caller is responsible for embedding it into a full HTML document if needed.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <returns>An HTML string containing a table of the matching rows.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
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
        /// Formats a cell value for display in an HTML report. Null values become an empty string and DateTime values are formatted in ISO 8601 format; all other values use their default string representation.
        /// </summary>
        /// <param name="value">The value to format for display in an HTML report.</param>
        /// <returns>The formatted string representation of the value.</returns>
        private static string FormatCell(object value)
        {
            if (value == null) return "";
            if (value is DateTime dt) return dt.ToString("o");
            return value.ToString();
        }

        /// <summary>
        /// HTML-encodes a string for safe inclusion in an HTML report, converting characters such as &lt;, &gt;, &amp;, and quotes to their HTML entities. Null strings become empty strings.
        /// </summary>
        /// <param name="text">The text to HTML-encode.</param>
        /// <returns>The HTML-encoded string.</returns>
        private static string HtmlEncode(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : System.Net.WebUtility.HtmlEncode(text);
        }

        /// <summary>
        /// Asynchronously generates an XML report of all rows in the database that match the specified criteria. The report uses the same structure as the underlying database storage: a Report root element containing one Row element per matching row, with each column stored as a Field element carrying the column name and .NET type as attributes. Because the report is built through the same field-creation logic used for storage, values that cannot be represented as plain XML text are handled by the same JSON serialization fallback. The method returns the complete XML document (including an XML declaration) as a string.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <returns>An XML string containing the matching rows.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
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
        /// Asynchronously generates a JSON report of all rows in the database that match the specified criteria. Each matching row becomes one JSON object with the column names as property names and the cell values as property values; nulls become JSON null and DateTime values use their ISO 8601 representation. The output uses clean (indented) formatting. The method returns the JSON text as a string.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <returns>A JSON string containing an array of the matching rows.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
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
        /// Asynchronously updates a single row in the database identified by its unique ID. The updates are provided as a dictionary where keys are column names and values are the new values to be set for those columns. The method automatically updates the "chkDate__" system column to the current UTC date. It returns true if the update was successful (i.e., the row with the specified ID exists), or false if no such row was found.
        /// </summary>
        /// <param name="id">The unique ID of the row to update.</param>
        /// <param name="updates">A dictionary containing the column-value pairs to update in the row.</param>
        /// <returns>True if the update was successful; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"></exception>
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
        /// Asynchronously updates multiple rows in the database that match the specified criteria. 
        /// The criteria are provided as a dictionary where keys are column names and values are 
        /// the expected values for those columns. The updates to be applied are also provided 
        /// as a dictionary. The method automatically updates the "chkDate__" system column to 
        /// the current UTC date for each updated row. It returns a list of unique IDs of the 
        /// rows that were successfully updated. If no rows match the criteria, an empty list is returned.
        /// </summary>
        /// <param name="criteria">Dictionary containing the criteria for selecting rows to update.</param>
        /// <param name="values">Dictionary containing the column-value pairs to update in the matching rows.</param>
        /// <returns>A list of unique IDs of the rows that were successfully updated.</returns>
        /// <exception cref="ArgumentNullException"></exception>
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
        /// Asynchronously deletes a single row from the database identified by its unique ID. If a row with the specified ID exists, it is removed from the database and the method returns true. If no such row is found, the method returns false. The deletion is followed by saving the updated XML structure to the file.
        /// </summary>
        /// <param name="id">The unique ID of the row to delete.</param>
        /// <returns>True if the row was successfully deleted; otherwise, false.</returns>
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
        /// Asynchronously deletes multiple rows from the database that match the specified criteria. The criteria are provided as a dictionary where keys are column names and values are the expected values for those columns. The method returns a list of unique IDs of the rows that were successfully deleted. If no rows match the criteria, an empty list is returned. The deletion is followed by saving the updated XML structure to the file.
        /// </summary>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <returns>A list of unique IDs of the rows that were successfully deleted.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the criteria dictionary is null.</exception>
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
        /// Asynchronously purges rows from the database that have a "chkDate__" value older than the specified number of days (ttlDays). The method calculates a cutoff date based on the current UTC date minus ttlDays and removes any rows with a "chkDate__" value earlier than this cutoff. It returns the count of rows that were removed. If ttlDays is negative, an ArgumentOutOfRangeException is thrown.
        /// </summary>
        /// <param name="ttlDays">The number of days to use as the time-to-live (TTL) for rows.</param>
        /// <returns>The count of rows that were removed.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if ttlDays is negative.</exception>
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
        /// Asynchronously disposes of the resources used by the XmlDatabase instance. This method ensures that the semaphore used for locking is properly released and disposed of, preventing resource leaks. It is safe to call this method multiple times; subsequent calls after the first will have no effect.
        /// </summary>
        /// <returns>A task that represents the asynchronous dispose operation.</returns>
        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _asyncLock.Dispose();
            }
        }

        #region Private Async Helpers

        /// <summary>
        /// Asynchronously saves the in-memory XML document to the file specified by _filePath. This method uses true asynchronous file writing to ensure that the operation does not block the calling thread, allowing for better performance in applications that require responsiveness. The XML is saved with indentation for readability.
        /// </summary>
        /// <returns></returns>
        private async Task SaveAsync()
        {
            // Atomic save: write to a temp file first, then move it over the original.
            // If the process crashes mid-write, the real database file is left untouched.
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
                // Best-effort cleanup of the orphaned temp file; never mask the original exception
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        #endregion

        #region Private Sync Helpers (In-Memory Operations)

        /// <summary>
        /// Applies the specified updates to the given row element in the XML document. This method iterates through the provided dictionary of updates, checking each key-value pair. If the key corresponds to a system column (IdColumn or ChkDateColumn), it is skipped. For other keys, the method checks if a field with that name already exists in the row; if it does, the existing field is replaced with a new field element containing the updated value. If the field does not exist, a new field element is added to the row. This ensures that the row reflects the latest data as specified by the updates.
        /// </summary>
        /// <param name="rowElem">The XElement representing the row to update.</param>
        /// <param name="updates">A dictionary containing the column-value pairs to update in the row.</param>
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

        /// <summary>
        /// Finds a row in the XML document by its unique ID. This method searches through the "Row" elements in the root of the XML document, looking for a "Field" element with a "name" attribute matching the IdColumn and a value equal to the specified ID. If such a row is found, it returns the corresponding XElement; otherwise, it returns null. This is used internally to locate specific rows for operations like updates or deletions.
        /// </summary>
        /// <param name="id">The unique ID of the row to find.</param>
        /// <returns>The XElement representing the row if found; otherwise, null.</returns>
        private XElement FindRowById(long id)
        {
            return _doc.Root.Elements("Row")
                .FirstOrDefault(row => row.Elements("Field")
                    .Any(f => f.Attribute("name")?.Value == IdColumn && f.Value == id.ToString()));
        }

        /// <summary>
        /// Determines whether a given row matches the specified criteria. This method takes a dictionary representing a row and another dictionary representing the criteria. It checks if all key-value pairs in the criteria are present in the row with matching values. If any key from the criteria is not found in the row or if any value does not match, the method returns false. If all criteria are satisfied, it returns true. This is used for filtering rows based on user-defined conditions.
        /// </summary>
        /// <param name="row">A dictionary representing the row to check.</param>
        /// <param name="criteria">A dictionary containing the column-value pairs to filter the rows.</param>
        /// <returns>True if the row matches the criteria; otherwise, false.</returns>
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
        /// Updates a system field in the specified row element. This method checks if a field with the given name already exists in the row; if it does, the existing field is replaced with a new field element containing the updated value. If the field does not exist, a new field element is added to the row.
        /// </summary>
        /// <param name="rowElem">The XElement representing the row to update.</param>
        /// <param name="fieldName">The name of the system field to update.</param>
        /// <param name="value">The new value to set for the system field.</param>
        private void UpdateSystemField(XElement rowElem, string fieldName, object value)
        {
            var existingField = rowElem.Elements("Field").FirstOrDefault(f => f.Attribute("name")?.Value == fieldName);
            if (existingField != null) existingField.ReplaceWith(CreateFieldElement(fieldName, value));
            else rowElem.Add(CreateFieldElement(fieldName, value));
        }

        /// <summary>
        /// Parses a row XElement into a dictionary representation. This method iterates through the "Field" elements of the given row element, extracting the "name" attribute and the value of each field. It uses the ParseValue method to convert the string representation of the value into its appropriate type based on the "type" attribute. The resulting key-value pairs are added to a dictionary, which is then returned. This allows for easy manipulation and access to row data in a structured format.
        /// </summary>
        /// <param name="rowElem">The XElement representing the row to parse.</param>
        /// <returns>A dictionary containing the column-value pairs from the row.</returns>
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

        /// <summary>
        /// Creates an XElement representing a field in the XML document. The value is checked to determine whether it can be stored directly as XML text (simple, XML-safe values such as primitives, enums, strings, DateTime, decimal, Guid, TimeSpan, DateTimeOffset, and Uri). Such values are stored as before, with the type attribute set to the value's .NET type and DateTime values formatted in ISO 8601 format. If the value is a complex object, or a string containing characters that are illegal in XML, the method falls back to JSON serialization using clean (indented) formatting; the stored text is then prefixed with a "[JSON]" header and the type attribute is prefixed with "JSON:". If JSON serialization also fails (for example due to circular references), the cell is stored with the type "Null" and the text "[SERIALIZE_ERROR]". The resulting XElement can then be added to a row in the XML document.
        /// </summary>
        /// <param name="name">The name of the field.</param>
        /// <param name="value">The value of the field.</param>
        /// <returns>An XElement representing the field.</returns>
        private XElement CreateFieldElement(string name, object value)
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
                // JSON serialization failed as well: tag the cell as a serialization error.
                // Stored as a string (not "Null") so the tag survives when the row is read back.
                return new XElement("Field",
                    new XAttribute("name", name),
                    new XAttribute("type", "System.String"),
                    SerializeErrorTag);
            }
        }

        /// <summary>
        /// Determines whether a value can be stored directly as XML text, without a JSON fallback. Returns true for simple, self-describing values (primitive types, enums, DateTime, DateTimeOffset, decimal, Guid, TimeSpan, and Uri). For strings, it additionally verifies that every character is legal in XML 1.0 text content. Returns false for complex objects and for strings containing illegal XML characters.
        /// </summary>
        /// <param name="value">The value to check.</param>
        /// <returns>True if the value can be stored directly as XML text; otherwise, false.</returns>
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
        /// Checks whether every character in the string is legal in XML 1.0 text content (tab, line feed, carriage return, and the ranges #x20-#xD7FF, #xE000-#xFFFD, excluding the non-characters #xFFFE and #xFFFF). Strings failing this check cannot be written to an XML document directly.
        /// </summary>
        /// <param name="text">The string to check.</param>
        /// <returns>True if the string is valid XML text; otherwise, false.</returns>
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
        /// Parses a string value into its appropriate type based on the provided type name. This method checks the type name and converts the string into the corresponding .NET type (e.g., int, long, double, bool, DateTime, decimal, float). If the type name is "Null" or if the text is empty and the type is "System.String", it returns null. If the conversion fails for any reason, it returns the original string value. This allows for dynamic handling of different data types stored in the XML database.
        /// </summary>
        /// <param name="text">The string representation of the value to parse.</param>
        /// <param name="typeName">The name of the type to convert the string to.</param>
        /// <returns>The parsed value as an object of the appropriate type, or the original string if conversion fails.</returns>
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

        /// <summary>
        /// Compares two cell values for ordering. Null values sort first. When both values share the same type and implement IComparable, their natural order is used; otherwise the comparison falls back to ordinal string comparison of their text representations, so mixed-type columns still sort deterministically instead of throwing.
        /// </summary>
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