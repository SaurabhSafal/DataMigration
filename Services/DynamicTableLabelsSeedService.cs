using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using Npgsql;

namespace DataMigration.Services
{
    public class DynamicTableLabelsSeedService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DynamicTableLabelsSeedService> _logger;
        private const string CsvPath = "SeedData/Constant/dynamic_table_labels.csv";

        public DynamicTableLabelsSeedService(IConfiguration configuration, ILogger<DynamicTableLabelsSeedService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }
        public List<object> GetMappings() => new List<object>
        {
            new { source = "id", target = "id", logic = "Primary key, identity", type = "int" },
            new { source = "company_id", target = "company_id", logic = "Foreign key to company", type = "int" },
            new { source = "page_name", target = "page_name", logic = "Page identifier", type = "string" },
            new { source = "column_name", target = "column_name", logic = "Column identifier", type = "string" },
            new { source = "label_text", target = "label_text", logic = "Label to display", type = "string" },
            new { source = "sequence_id", target = "sequence_id", logic = "Order of label", type = "int?" },
            new { source = "is_non_listing_page", target = "is_non_listing_page", logic = "True if not a listing page", type = "bool" },
            new { source = "created_by", target = "created_by", logic = "User who created", type = "int?" },
            new { source = "created_date", target = "created_date", logic = "Creation timestamp", type = "DateTimeOffset?" },
            new { source = "modified_by", target = "modified_by", logic = "User who modified", type = "int?" },
            new { source = "modified_date", target = "modified_date", logic = "Modification timestamp", type = "DateTimeOffset?" },
            new { source = "is_deleted", target = "is_deleted", logic = "Soft delete flag", type = "bool?" },
            new { source = "deleted_by", target = "deleted_by", logic = "User who deleted", type = "int?" },
            new { source = "deleted_date", target = "deleted_date", logic = "Deletion timestamp", type = "DateTimeOffset?" },
            new { source = "is_mandatory", target = "is_mandatory", logic = "True if field is mandatory", type = "bool" }
        };

        public async Task<int> SeedAsync()
        {
            var pgConnString = _configuration.GetConnectionString("PostgreSql");
            if (string.IsNullOrEmpty(pgConnString))
            {
                _logger.LogError("PostgreSQL connection string not found in configuration");
                return 0;
            }

            if (!File.Exists(CsvPath))
            {
                _logger.LogError($"CSV file not found: {CsvPath}");
                return 0;
            }

            int recordsInserted = 0;
            using var pgConn = new NpgsqlConnection(pgConnString);
            await pgConn.OpenAsync();

            // Get all valid company_ids (not deleted)
            var companyIds = new List<int>();
            using (var cmd = new NpgsqlCommand("SELECT company_id FROM company_master WHERE is_deleted IS NULL OR is_deleted = false", pgConn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    companyIds.Add(reader.GetInt32(0));
                }
            }
            if (companyIds.Count == 0)
            {
                _logger.LogWarning("No valid company_id found in company_master");
                return 0;
            }

            // Read all CSV rows into memory (excluding header)
            var csvRows = new List<string[]>();
            using (var csvReader = new Microsoft.VisualBasic.FileIO.TextFieldParser(CsvPath))
            {
                csvReader.SetDelimiters(",");
                csvReader.HasFieldsEnclosedInQuotes = true;
                if (csvReader.EndOfData)
                {
                    _logger.LogError("CSV file is empty");
                    return 0;
                }
                var headers = csvReader.ReadFields();
                if (headers == null)
                {
                    _logger.LogError("CSV header is missing");
                    return 0;
                }
                while (!csvReader.EndOfData)
                {
                    var fields = csvReader.ReadFields();
                    if (fields == null || fields.Length < headers.Length) continue;
                    csvRows.Add(fields);
                }
            }

            // Get the current max id from dynamic_table_labels
            int nextId = 1;
            using (var cmd = new NpgsqlCommand("SELECT COALESCE(MAX(id), 0) + 1 FROM dynamic_table_labels", pgConn))
            {
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && int.TryParse(result.ToString(), out int maxId))
                    nextId = maxId;
            }

            foreach (var companyId in companyIds)
            {
                foreach (var fields in csvRows)
                {
                    // Parse fields, but override company_id and id
                    int id = nextId++;
                    // int company_id = int.Parse(fields[1]); // replaced
                    string page_name = fields[2];
                    string column_name = fields[3];
                    string label_text = fields[4];
                    int? sequence_id = string.IsNullOrWhiteSpace(fields[5]) ? (int?)null : int.Parse(fields[5]);
                    bool is_non_listing_page = ParseBool(fields[6]);
                    int? created_by = ParseNullableInt(fields[7]);
                    DateTimeOffset? created_date = ParseNullableDateTime(fields[8]);
                    int? modified_by = ParseNullableInt(fields[9]);
                    DateTimeOffset? modified_date = ParseNullableDateTime(fields[10]);
                    bool? is_deleted = ParseNullableBool(fields[11]);
                    int? deleted_by = ParseNullableInt(fields[12]);
                    DateTimeOffset? deleted_date = ParseNullableDateTime(fields[13]);
                    bool is_mandatory = fields.Length > 14 && ParseBool(fields[14]);

                    var insertQuery = @"
                        INSERT INTO dynamic_table_labels (
                            id, company_id, page_name, column_name, label_text, sequence_id, is_non_listing_page, created_by, created_date, modified_by, modified_date, is_deleted, deleted_by, deleted_date, is_mandatory
                        ) VALUES (
                            @id, @company_id, @page_name, @column_name, @label_text, @sequence_id, @is_non_listing_page, @created_by, @created_date, @modified_by, @modified_date, @is_deleted, @deleted_by, @deleted_date, @is_mandatory
                        )
                        ON CONFLICT (id) DO UPDATE SET
                            company_id = EXCLUDED.company_id,
                            page_name = EXCLUDED.page_name,
                            column_name = EXCLUDED.column_name,
                            label_text = EXCLUDED.label_text,
                            sequence_id = EXCLUDED.sequence_id,
                            is_non_listing_page = EXCLUDED.is_non_listing_page,
                            created_by = EXCLUDED.created_by,
                            created_date = EXCLUDED.created_date,
                            modified_by = EXCLUDED.modified_by,
                            modified_date = EXCLUDED.modified_date,
                            is_deleted = EXCLUDED.is_deleted,
                            deleted_by = EXCLUDED.deleted_by,
                            deleted_date = EXCLUDED.deleted_date,
                            is_mandatory = EXCLUDED.is_mandatory;";

                    using var cmd = new NpgsqlCommand(insertQuery, pgConn);
                    cmd.Parameters.AddWithValue("id", id);
                    cmd.Parameters.AddWithValue("company_id", companyId);
                    cmd.Parameters.AddWithValue("page_name", page_name);
                    cmd.Parameters.AddWithValue("column_name", column_name);
                    cmd.Parameters.AddWithValue("label_text", label_text);
                    cmd.Parameters.AddWithValue("sequence_id", (object?)sequence_id ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("is_non_listing_page", is_non_listing_page);
                    cmd.Parameters.AddWithValue("created_by", (object?)created_by ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("created_date", (object?)created_date ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("modified_by", (object?)modified_by ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("modified_date", (object?)modified_date ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("is_deleted", (object?)is_deleted ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("deleted_by", (object?)deleted_by ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("deleted_date", (object?)deleted_date ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("is_mandatory", is_mandatory);

                    recordsInserted += await cmd.ExecuteNonQueryAsync();
                }
            }

            _logger.LogInformation($"Seeded {recordsInserted} records into dynamic_table_labels table for all companies");
            return recordsInserted;
        }

        private static bool ParseBool(string value)
        {
            return value.Trim().ToLower() switch
            {
                "true" => true,
                "1" => true,
                _ => false
            };
        }
        private static bool? ParseNullableBool(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return ParseBool(value);
        }
        private static int? ParseNullableInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (int.TryParse(value, out int result)) return result;
            return null;
        }
        private static DateTimeOffset? ParseNullableDateTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTimeOffset.TryParse(value, out var dt)) return dt;
            return null;
        }
    }
}
