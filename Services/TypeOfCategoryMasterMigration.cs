using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DataMigration.Services;

public class TypeOfCategoryMasterMigration : MigrationService
{
    private readonly ILogger<TypeOfCategoryMasterMigration> _logger;
    private MigrationLogger? _migrationLogger;
    // SQL Server: TBL_TypeOfCategory -> PostgreSQL: type_of_category_master
    protected override string SelectQuery => @"
        SELECT 
            id,
            CategoryType
        FROM TBL_TypeOfCategory";

    protected override string InsertQuery => @"
        INSERT INTO type_of_category_master (
            type_of_category_name,
            company_id,
            created_by,
            created_date,
            modified_by,
            modified_date,
            is_deleted,
            deleted_by,
            deleted_date
        ) VALUES (
            @type_of_category_name,
            @company_id,
            @created_by,
            @created_date,
            @modified_by,
            @modified_date,
            @is_deleted,
            @deleted_by,
            @deleted_date
        )
        ON CONFLICT (type_of_category_name, company_id) DO NOTHING";

    public TypeOfCategoryMasterMigration(IConfiguration configuration, ILogger<TypeOfCategoryMasterMigration> logger) : base(configuration)
    {
        _logger = logger; }

    public MigrationLogger? GetLogger() => _migrationLogger;

    protected override List<string> GetLogics()
    {
        return new List<string>
        {
            "Direct", // type_of_category_id
            "Direct", // type_of_category_name
            "Fixed: 1"  // company_id
        };
    }

    public async Task<int> MigrateAsync()
    {
        return await base.MigrateAsync(useTransaction: true);
    }

    protected override async Task<int> ExecuteMigrationAsync(SqlConnection sqlConn, NpgsqlConnection pgConn, NpgsqlTransaction? transaction = null)
    {
        _migrationLogger = new MigrationLogger(_logger, "type_of_category_master");
        _migrationLogger.LogInfo("Starting migration");
        Console.WriteLine("🚀 Starting TypeOfCategoryMaster migration...");
        // Truncate and reset sequence
        Console.WriteLine("Truncating type_of_category_master and resetting sequence...");
        using (var truncateCmd = new NpgsqlCommand("TRUNCATE TABLE type_of_category_master RESTART IDENTITY CASCADE;", pgConn, transaction))
        {
            await truncateCmd.ExecuteNonQueryAsync();
        }
        Console.WriteLine("Table truncated and sequence reset.");
        Console.WriteLine($"📋 Executing query...");
        var companyIds = new List<int>();
        using (var compCmd = new NpgsqlCommand("SELECT company_id FROM company_master", pgConn, transaction))
        {
            using var compReader = await compCmd.ExecuteReaderAsync();
            while (await compReader.ReadAsync())
            {
                if (!compReader.IsDBNull(0)) companyIds.Add(compReader.GetInt32(0));
            }
        }
        Console.WriteLine($"✓ Found {companyIds.Count} companies. Each type_of_category will be inserted for all companies.");
        using var sqlCmd = new SqlCommand(SelectQuery, sqlConn);
        using var reader = await sqlCmd.ExecuteReaderAsync();
        Console.WriteLine($"✓ Query executed. Processing records...");
        // Read all categories into a list first
        var categoryList = new List<(object SourceId, object CategoryName)>();
        while (await reader.ReadAsync())
        {
            var sourceId = reader["id"];
            var categoryName = reader["CategoryType"];
            categoryList.Add((sourceId, categoryName));
        }
        int insertedCount = 0;
        int skippedCount = 0;
        int totalReadCount = categoryList.Count;
        var skippedRecordsList = new List<(string RecordId, string Reason)>();
        if (totalReadCount == 0)
        {
            Console.WriteLine($"\n⚠️  WARNING: No records found in TBL_TypeOfCategory table!");
        }
        else
        {
            Console.WriteLine($"✓ Found {totalReadCount} records! Processing...");
        }
        foreach (var companyId in companyIds)
        {
            int localCount = 0;
            foreach (var (sourceId, categoryName) in categoryList)
            {
                try
                {
                    var insertQuery = @"
                        INSERT INTO type_of_category_master (
                            type_of_category_name,
                            company_id,
                            created_by,
                            created_date,
                            modified_by,
                            modified_date,
                            is_deleted,
                            deleted_by,
                            deleted_date
                        ) VALUES (
                            @type_of_category_name,
                            @company_id,
                            @created_by,
                            @created_date,
                            @modified_by,
                            @modified_date,
                            @is_deleted,
                            @deleted_by,
                            @deleted_date
                        )";
                    using var pgCmd = new NpgsqlCommand(insertQuery, pgConn, transaction);
                    pgCmd.Parameters.AddWithValue("@type_of_category_name", categoryName ?? DBNull.Value);
                    pgCmd.Parameters.AddWithValue("@company_id", companyId);
                    pgCmd.Parameters.AddWithValue("@created_by", DBNull.Value);
                    pgCmd.Parameters.AddWithValue("@created_date", DBNull.Value);
                    pgCmd.Parameters.AddWithValue("@modified_by", DBNull.Value);
                    pgCmd.Parameters.AddWithValue("@modified_date", DBNull.Value);
                    pgCmd.Parameters.AddWithValue("@is_deleted", false);
                    pgCmd.Parameters.AddWithValue("@deleted_by", DBNull.Value);
                    pgCmd.Parameters.AddWithValue("@deleted_date", DBNull.Value);
                    int result = await pgCmd.ExecuteNonQueryAsync();
                    if (result > 0)
                    {
                        insertedCount++;
                    }
                }
                catch (PostgresException pgEx)
                {
                    skippedCount++;
                    string reason = $"PostgreSQL error for name={categoryName}, company={companyId}: {pgEx.MessageText}";
                    skippedRecordsList.Add((categoryName?.ToString() ?? "", reason));
                    Console.WriteLine($"❌ {reason}");
                    if (pgEx.Detail != null) Console.WriteLine($"   Detail: {pgEx.Detail}");
                    continue;
                }
                catch (Exception ex)
                {
                    skippedCount++;
                    string reason = $"Error migrating name={categoryName}, company={companyId}: {ex.Message}";
                    skippedRecordsList.Add((categoryName?.ToString() ?? "", reason));
                    Console.WriteLine($"❌ {reason}");
                    continue;
                }
                localCount++;
                if (localCount % 10 == 0)
                {
                    Console.WriteLine($"📊 Processed {localCount} categories for company {companyId}... (Inserted: {insertedCount}, Skipped: {skippedCount})");
                }
            }
        }
        Console.WriteLine($"\n📊 Migration Summary:");
        Console.WriteLine($"   Total source records read: {totalReadCount}");
        Console.WriteLine($"   ✓ Successfully inserted rows: {insertedCount}");
        Console.WriteLine($"   ❌ Skipped (errors/duplicates): {skippedCount}");
        string outputPath = System.IO.Path.Combine("migration_outputs", $"TypeOfCategoryMasterMigrationStats_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        MigrationStatsExporter.ExportToExcel(
            outputPath,
            totalReadCount * companyIds.Count,
            insertedCount,
            skippedCount,
            _logger,
            skippedRecordsList
        );
        Console.WriteLine($"Migration statistics exported to {outputPath}");
        return insertedCount;
    }
}
