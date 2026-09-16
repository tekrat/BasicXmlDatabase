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
        /// Private constructor; use CreateAsync to instantiate
        /// </summary>
        private XmlDatabase(string filePath)
        {
            if(string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));
            _filePath = filePath;
        }

        /// <summary>
        /// Factory method to create and asynchronously load the database.
        /// </summary>
        public static async Task<XmlDatabase> CreateAsync(string filePath)
        {
            var db = new XmlDatabase(filePath);
            await db.InitializeAsync();
            return db;
        }


        /// <summary>
        /// Asynchronously initializes the database by loading the XML file if it exists, or creating a new XML structure if it does not. It also determines the next unique ID to assign for new rows based on existing data.
        /// </summary>
        /// <returns></returns>
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
        /// <param name="row"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
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
        /// Asynchronously retrieves all rows from the database. Each row is represented as a dictionary where the keys are column names and the values are the corresponding data. The method returns a list of these dictionaries, allowing for easy access to all stored data.
        /// </summary>
        /// <returns></returns>
        public async Task<List<Dictionary<string, object>>> SelectAsync()
        {
            await _asyncLock.WaitAsync();
            try
            {
                var result = new List<Dictionary<string, object>>();
                foreach (var rowElem in _doc.Root.Elements("Row"))
                    result.Add(ParseRow(rowElem));
                return result;
            }
            finally { _asyncLock.Release(); }
        }

        /// <summary>
        /// Asynchronously retrieves a single row from the database by its unique ID. If a row with the specified ID exists, it returns a dictionary representing that row; otherwise, it returns null. The dictionary contains column names as keys and their corresponding values.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
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
        /// <param name="criteria"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
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
        /// Asynchronously updates a single row in the database identified by its unique ID. The updates are provided as a dictionary where keys are column names and values are the new values to be set for those columns. The method automatically updates the "chkDate__" system column to the current UTC date. It returns true if the update was successful (i.e., the row with the specified ID exists), or false if no such row was found.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="updates"></param>
        /// <returns></returns>
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
        /// Asynchronously updates multiple rows in the database that match the specified criteria. The criteria are provided as a dictionary where keys are column names and values are the expected values for those columns. The updates to be applied are also provided as a dictionary. The method automatically updates the "chkDate__" system column to the current UTC date for each updated row. It returns a list of unique IDs of the rows that were successfully updated. If no rows match the criteria, an empty list is returned.
        /// </summary>
        /// <param name="criteria"></param>
        /// <param name="values"></param>
        /// <returns></returns>
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
        /// <param name="id"></param>
        /// <returns></returns>
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
        /// <param name="criteria"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
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
        /// <param name="ttlDays"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
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
        /// <returns></returns>
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
            // True async file write
            var settings = new System.Xml.XmlWriterSettings { Indent = true, Async = true };
            using var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            using var writer = System.Xml.XmlWriter.Create(stream, settings);
            await _doc.SaveAsync(writer, CancellationToken.None);
        }

        #endregion

        #region Private Sync Helpers (In-Memory Operations)

        /// <summary>
        /// Applies the specified updates to the given row element in the XML document. This method iterates through the provided dictionary of updates, checking each key-value pair. If the key corresponds to a system column (IdColumn or ChkDateColumn), it is skipped. For other keys, the method checks if a field with that name already exists in the row; if it does, the existing field is replaced with a new field element containing the updated value. If the field does not exist, a new field element is added to the row. This ensures that the row reflects the latest data as specified by the updates.
        /// </summary>
        /// <param name="rowElem"></param>
        /// <param name="updates"></param>
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
        /// <param name="id"></param>
        /// <returns></returns>
        private XElement FindRowById(long id)
        {
            return _doc.Root.Elements("Row")
                .FirstOrDefault(row => row.Elements("Field")
                    .Any(f => f.Attribute("name")?.Value == IdColumn && f.Value == id.ToString()));
        }

        /// <summary>
        /// Determines whether a given row matches the specified criteria. This method takes a dictionary representing a row and another dictionary representing the criteria. It checks if all key-value pairs in the criteria are present in the row with matching values. If any key from the criteria is not found in the row or if any value does not match, the method returns false. If all criteria are satisfied, it returns true. This is used for filtering rows based on user-defined conditions.
        /// </summary>
        /// <param name="row"></param>
        /// <param name="criteria"></param>
        /// <returns></returns>
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
        /// <param name="rowElem"></param>
        /// <param name="fieldName"></param>
        /// <param name="value"></param>
        private void UpdateSystemField(XElement rowElem, string fieldName, object value)
        {
            var existingField = rowElem.Elements("Field").FirstOrDefault(f => f.Attribute("name")?.Value == fieldName);
            if (existingField != null) existingField.ReplaceWith(CreateFieldElement(fieldName, value));
            else rowElem.Add(CreateFieldElement(fieldName, value));
        }

        /// <summary>
        /// Parses a row XElement into a dictionary representation. This method iterates through the "Field" elements of the given row element, extracting the "name" attribute and the value of each field. It uses the ParseValue method to convert the string representation of the value into its appropriate type based on the "type" attribute. The resulting key-value pairs are added to a dictionary, which is then returned. This allows for easy manipulation and access to row data in a structured format.
        /// </summary>
        /// <param name="rowElem"></param>
        /// <returns></returns>
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
        /// Creates an XElement representing a field in the XML document. This method takes a field name and its corresponding value, determines the type of the value, and constructs an XElement with the appropriate attributes and text content. If the value is null, it sets the type to "Null" and the text content to an empty string. For DateTime values, it formats them in ISO 8601 format. The resulting XElement can then be added to a row in the XML document.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        private XElement CreateFieldElement(string name, object value)
        {
            string typeName = value?.GetType().FullName ?? "Null";
            string textValue = value?.ToString() ?? "";
            if (value is DateTime dt) textValue = dt.ToString("o");
            return new XElement("Field", new XAttribute("name", name), new XAttribute("type", typeName), textValue);
        }

        /// <summary>
        /// Parses a string value into its appropriate type based on the provided type name. This method checks the type name and converts the string into the corresponding .NET type (e.g., int, long, double, bool, DateTime, decimal, float). If the type name is "Null" or if the text is empty and the type is "System.String", it returns null. If the conversion fails for any reason, it returns the original string value. This allows for dynamic handling of different data types stored in the XML database.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="typeName"></param>
        /// <returns></returns>
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

        #endregion
    }
}