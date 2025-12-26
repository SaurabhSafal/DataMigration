using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Data;
using Microsoft.Data.SqlClient;

namespace DataMigration.Services
{
    public class UserPlantPurchaseGroupBatchInsertService : MigrationService
    {
        private const int BATCH_SIZE = 1000;
        private readonly ILogger<UserPlantPurchaseGroupBatchInsertService> _logger;
        private MigrationLogger? _migrationLogger;

        public UserPlantPurchaseGroupBatchInsertService(IConfiguration configuration, ILogger<UserPlantPurchaseGroupBatchInsertService> logger) : base(configuration)
        {
            _logger = logger;
        }

        public MigrationLogger? GetLogger() => _migrationLogger;

        protected override string SelectQuery => @"
            SELECT
            UP_Id,
            UserId,
            PurchaseGroupId,
            PlantId
            FROM TBL_UserPurchaseGroupMaster
            ORDER BY UP_Id";

        protected override string InsertQuery => @"
            INSERT INTO user_plant_purchase_group_master (
            user_plant_purchase_group_id,
            purchase_group_id,
            user_id,
            plant_id,
            created_by,
            created_date,
            modified_by,
            modified_date,
            is_deleted,
            deleted_by,
            deleted_date
            ) VALUES (
            @user_plant_purchase_group_id,
            @purchase_group_id,
            @user_id,
            @plant_id,
            @created_by,
            @created_date,
            @modified_by,
            @modified_date,
            @is_deleted,
            @deleted_by,
            @deleted_date
            )
            ON CONFLICT (user_plant_purchase_group_id) DO UPDATE SET
            purchase_group_id = EXCLUDED.purchase_group_id,
            user_id = EXCLUDED.user_id,
            plant_id = EXCLUDED.plant_id,
            modified_by = EXCLUDED.modified_by,
            modified_date = EXCLUDED.modified_date,
            is_deleted = EXCLUDED.is_deleted,
            deleted_by = EXCLUDED.deleted_by,
            deleted_date = EXCLUDED.deleted_date";

        public async Task<int> MigrateAsync()
        {
            return await base.MigrateAsync(useTransaction: true);
        }

        protected override async Task<int> ExecuteMigrationAsync(SqlConnection sqlConn, NpgsqlConnection pgConn, NpgsqlTransaction? transaction = null)
        {
            _migrationLogger = new MigrationLogger(_logger, "user_plant_purchase_group_master");
            _migrationLogger.LogInfo("Starting migration");

            _logger.LogInformation("Starting User Plant Purchase Group Master migration...");

            int totalRecords = 0;
            int migratedRecords = 0;
            int skippedRecords = 0;
            var skippedRecordsList = new List<(string RecordId, string Reason)>();

            using var sqlCommand = new SqlCommand(SelectQuery, sqlConn);
            sqlCommand.CommandTimeout = 300;

            using var reader = await sqlCommand.ExecuteReaderAsync();

            var batch = new List<Dictionary<string, object>>();
            var processedIds = new HashSet<int>();

            while (await reader.ReadAsync())
            {
                totalRecords++;

                var upId = reader["UP_Id"];
                var userId = reader["UserId"];
                var purchaseGroupId = reader["PurchaseGroupId"];
                var plantId = reader["PlantId"];

                // Skip if UP_Id is NULL
                if (upId == DBNull.Value)
                {
                    skippedRecords++;
                    string reason = "UP_Id is NULL";
                    _logger.LogWarning($"Skipping record - {reason}");
                    skippedRecordsList.Add(("", reason));
                    continue;
                }

                int upIdValue = Convert.ToInt32(upId);

                // Skip duplicates
                if (processedIds.Contains(upIdValue))
                {
                    skippedRecords++;
                    string reason = $"Duplicate UP_Id {upIdValue}";
                    skippedRecordsList.Add((upIdValue.ToString(), reason));
                    continue;
                }

                var record = new Dictionary<string, object>
                {
                    ["user_plant_purchase_group_id"] = upIdValue,
                    ["user_id"] = userId == DBNull.Value ? (object)DBNull.Value : Convert.ToInt32(userId),
                    ["purchase_group_id"] = purchaseGroupId == DBNull.Value ? (object)DBNull.Value : Convert.ToInt32(purchaseGroupId),
                    ["plant_id"] = plantId == DBNull.Value ? (object)DBNull.Value : Convert.ToInt32(plantId),
                    ["created_by"] = DBNull.Value,
                    ["created_date"] = DBNull.Value,
                    ["modified_by"] = DBNull.Value,
                    ["modified_date"] = DBNull.Value,
                    ["is_deleted"] = false,
                    ["deleted_by"] = DBNull.Value,
                    ["deleted_date"] = DBNull.Value
                };

                batch.Add(record);
                processedIds.Add(upIdValue);

                if (batch.Count >= BATCH_SIZE)
                {
                    int batchMigrated = await InsertBatchAsync(batch, pgConn, transaction);
                    migratedRecords += batchMigrated;
                    batch.Clear();
                }
            }

            // Insert remaining records
            if (batch.Count > 0)
            {
                int batchMigrated = await InsertBatchAsync(batch, pgConn, transaction);
                migratedRecords += batchMigrated;
            }

            // Export migration statistics to Excel (optional)
            string outputPath = System.IO.Path.Combine("migration_outputs", $"UserPlantPurchaseGroupBatchInsertStats_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
            MigrationStatsExporter.ExportToExcel(
                outputPath,
                totalRecords,
                migratedRecords,
                skippedRecords,
                _logger,
                skippedRecordsList
            );
            _logger.LogInformation($"Migration statistics exported to {outputPath}");

            _logger.LogInformation($"User Plant Purchase Group Master migration completed. Total: {totalRecords}, Migrated: {migratedRecords}, Skipped: {skippedRecords}");

            return migratedRecords;
        }

        private async Task<int> InsertBatchAsync(List<Dictionary<string, object>> batch, NpgsqlConnection pgConn, NpgsqlTransaction? transaction)
        {
            int insertedCount = 0;
            try
            {
                foreach (var record in batch)
                {
                    using var cmd = new NpgsqlCommand(InsertQuery, pgConn, transaction);
                    foreach (var kvp in record)
                    {
                        cmd.Parameters.AddWithValue($"@{kvp.Key}", kvp.Value ?? DBNull.Value);
                    }
                    await cmd.ExecuteNonQueryAsync();
                    insertedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error inserting batch of {batch.Count} records");
                throw;
            }
            return insertedCount;
        }

        protected override List<string> GetLogics()
        {
            return new List<string>
            {
                "Direct",  // user_plant_purchase_group_id
                "Direct",  // user_id
                "Direct",  // purchase_group_id
                "Direct",  // plant_id
                "Fixed",   // created_by
                "Fixed",   // created_date
                "Fixed",   // modified_by
                "Fixed",   // modified_date
                "Fixed",   // is_deleted
                "Fixed",   // deleted_by
                "Fixed"    // deleted_date
            };
        }
    }
}
