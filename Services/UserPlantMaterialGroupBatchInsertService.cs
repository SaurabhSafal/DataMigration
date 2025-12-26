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
    /// <summary>
    /// Service for optimized batch insert of user-plant-material-group mappings into PostgreSQL.
    /// </summary>
    public class UserPlantMaterialGroupBatchInsertService : MigrationService
    {
        private const int BATCH_SIZE = 1000;
        private readonly ILogger<UserPlantMaterialGroupBatchInsertService> _logger;
        private MigrationLogger? _migrationLogger;

        public UserPlantMaterialGroupBatchInsertService(IConfiguration configuration, ILogger<UserPlantMaterialGroupBatchInsertService> logger) : base(configuration)
        {
            _logger = logger;
        }

        /// <summary>
        /// Batch inserts user-plant-material-group records into the target table using efficient batching and transaction.
        /// </summary>
        /// <param name="records">List of user-plant-material-group records to insert.</param>
        /// <returns>Total number of records inserted (excluding conflicts).</returns>
        public MigrationLogger? GetLogger() => _migrationLogger;

        protected override string SelectQuery => @"
            SELECT
            MaterialGroupId,
            UserId,
            PlantId
            FROM TBL_UserMaterialGroupMaster
            ORDER BY UMG_Id";

        protected override string InsertQuery => @"
            INSERT INTO user_plant_material_group_master (
            user_id,
            plant_id,
            material_group_id
        ) VALUES (
            @user_id,
            @plant_id,
            @material_group_id
        )
            ON CONFLICT (user_plant_material_group_id) DO NOTHING";

        public async Task<int> MigrateAsync()
        {
            return await base.MigrateAsync(useTransaction: true);
        }

        protected override async Task<int> ExecuteMigrationAsync(SqlConnection sqlConn, Npgsql.NpgsqlConnection pgConn, Npgsql.NpgsqlTransaction? transaction = null)
        {
            _migrationLogger = new MigrationLogger(_logger, "user_plant_material_group_master");
            _migrationLogger.LogInfo("Starting migration");

            _logger.LogInformation("Starting User Plant Material Group Master migration...");

            int totalRecords = 0;
            int migratedRecords = 0;
            int skippedRecords = 0;
            var skippedRecordsList = new List<(string RecordId, string Reason)>();

            using var sqlCommand = new SqlCommand(SelectQuery, sqlConn);
            sqlCommand.CommandTimeout = 300;

            using var reader = await sqlCommand.ExecuteReaderAsync();

            var batch = new List<Dictionary<string, object>>();
            var processedKeys = new HashSet<string>();

            // Assume validPlantIds is a HashSet<int> loaded from plant_master
            var validPlantIds = LoadValidPlantIds(pgConn);

            while (await reader.ReadAsync())
            {
                totalRecords++;

                var userId = reader["UserId"];
                var plantId = reader["PlantId"];
                var materialGroupId = reader["MaterialGroupId"];

                // Skip if any key is NULL
                if (userId == DBNull.Value || plantId == DBNull.Value || materialGroupId == DBNull.Value)
                {
                    skippedRecords++;
                    string reason = "user_id, plant_id, or material_group_id is NULL";
                    _logger.LogWarning($"Skipping record - {reason}");
                    skippedRecordsList.Add(("", reason));
                    continue;
                }

                string key = $"{userId}-{plantId}-{materialGroupId}";
                if (processedKeys.Contains(key))
                {
                    skippedRecords++;
                    string reason = $"Duplicate key {key}";
                    skippedRecordsList.Add((key, reason));
                    continue;
                }

                // Assume validPlantIds is a HashSet<int> loaded from plant_master
                if (!validPlantIds.Contains(Convert.ToInt32(plantId)))
                {
                    skippedRecords++;
                    string reason = $"Invalid plant_id: {plantId}";
                    _logger.LogWarning($"Skipping record - {reason}");
                    skippedRecordsList.Add((key, reason));
                    continue;
                }

                var record = new Dictionary<string, object>
                {
                    ["user_id"] = userId,
                    ["plant_id"] = plantId,
                    ["material_group_id"] = materialGroupId
                };

                batch.Add(record);
                processedKeys.Add(key);

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

            // Export migration statistics to Excel (optional, can be removed if not needed)
            string outputPath = Path.Combine("migration_outputs", $"UserPlantMaterialGroupBatchInsertStats_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
            MigrationStatsExporter.ExportToExcel(
                outputPath,
                totalRecords,
                migratedRecords,
                skippedRecords,
                _logger,
                skippedRecordsList
            );
            _logger.LogInformation($"Migration statistics exported to {outputPath}");

            _logger.LogInformation($"User Plant Material Group Master migration completed. Total: {totalRecords}, Migrated: {migratedRecords}, Skipped: {skippedRecords}");

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
                "Direct",  // user_id
                "Direct",  // plant_id
                "Direct"   // material_group_id
            };
        }

        private HashSet<int> LoadValidPlantIds(NpgsqlConnection pgConn)
        {
            var validIds = new HashSet<int>();
            using var cmd = new NpgsqlCommand("SELECT plant_id FROM plant_master", pgConn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                validIds.Add(reader.GetInt32(0));
            }
            return validIds;
        }
    }

    /// <summary>
    /// Model representing a user-plant-material-group mapping.
    /// </summary>
    public class UserPlantMaterialGroup
        /// <summary>
        /// Example usage:
        /// <code>
        /// var service = new UserPlantMaterialGroupBatchInsertService(configuration, logger);
        /// var records = new List<UserPlantMaterialGroupBatchInsertService.UserPlantMaterialGroup>
        /// {
        ///     new UserPlantMaterialGroupBatchInsertService.UserPlantMaterialGroup { UserId = 1, PlantId = 10, MaterialGroupId = 100 },
        ///     new UserPlantMaterialGroupBatchInsertService.UserPlantMaterialGroup { UserId = 2, PlantId = 20, MaterialGroupId = 200 }
        /// };
        /// int inserted = await service.BatchInsertAsync(records);
        /// </code>
        /// </summary>
    {
        public int UserId { get; set; }
        public int PlantId { get; set; }
        public int MaterialGroupId { get; set; }
    }
}
