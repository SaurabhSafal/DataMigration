using Microsoft.Data.SqlClient;
using Npgsql;
using System.Data;
using System.Text;
using Microsoft.Extensions.Logging;
using DataMigration.Services;

namespace DataMigration.Services
{
    /// <summary>
    /// Optimized migration for erp_currency_exchange_rate with logging.
    /// </summary>
    public class ErpCurrencyExchangeRateMigration
    {
        private readonly ILogger<ErpCurrencyExchangeRateMigration> _logger;
        private readonly IConfiguration _configuration;
        private MigrationLogger? _migrationLogger;

        public ErpCurrencyExchangeRateMigration(IConfiguration configuration, ILogger<ErpCurrencyExchangeRateMigration> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public MigrationLogger? GetLogger() => _migrationLogger;

        public List<object> GetMappings() => new List<object>
        {
            new { source = "RecId", target = "erp_currency_exchange_rate_id", logic = "IDENTITY handled by Postgres", type = "serial/identity" },
            new { source = "FromCurrency", target = "from_currency", logic = "default 'USD' if NULL", type = "varchar -> character varying(10)" },
            new { source = "ToCurrency", target = "to_currency", logic = "default 'INR' if NULL", type = "varchar -> character varying(10)" },
            new { source = "ExchangeRate", target = "exchange_rate", logic = "default 1.0 if NULL or 0", type = "decimal -> numeric" },
            new { source = "FromDate", target = "valid_from", logic = "default NOW() if NULL", type = "timestamp with time zone" },
            new { source = "N/A (Generated)", target = "company_id", logic = "each company_id from company_master", type = "FK -> integer" }
        };

        public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
        {
            _migrationLogger = new MigrationLogger(_logger, "erp_currency_exchange_rate");
            _migrationLogger.LogInfo("Starting migration");

            var sqlConnectionString = _configuration.GetConnectionString("SqlServer");
            var pgConnectionString = _configuration.GetConnectionString("PostgreSql");

            if (string.IsNullOrEmpty(sqlConnectionString) || string.IsNullOrEmpty(pgConnectionString))
            {
                _migrationLogger.LogError("Database connection strings are not configured properly.", null);
                throw new InvalidOperationException("Database connection strings are not configured properly.");
            }

            _logger.LogInformation("Starting optimized ErpCurrencyExchangeRate migration...");

            // Read valid companies
            var validCompanyIds = new List<int>();
            try
            {
                await using (var pgForCompanies = new NpgsqlConnection(pgConnectionString))
                {
                    await pgForCompanies.OpenAsync(cancellationToken);
                    await using var cmd = new NpgsqlCommand("SELECT company_id FROM company_master WHERE is_deleted = false or is_deleted is null", pgForCompanies);
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        validCompanyIds.Add(reader.GetInt32(0));
                    }
                }
            }
            catch (Exception ex)
            {
                _migrationLogger.LogError("Failed to fetch company IDs", null, ex);
                throw;
            }

            if (!validCompanyIds.Any())
            {
                _migrationLogger.LogError("No valid companies found; aborting migration.", null);
                return 0;
            }

            // Read source data once
            var sourceData = new List<(int RecId, string? FromCurrency, string? ToCurrency, decimal? ExchangeRate, DateTime? FromDate)>();
            try
            {
                await using (var sqlConn = new SqlConnection(sqlConnectionString))
                {
                    await sqlConn.OpenAsync(cancellationToken);
                    var sql = @"SELECT RecId, FromCurrency, ToCurrency, FromDate, ExchangeRate FROM TBL_CurrencyConversionMaster";
                    await using var cmd = new SqlCommand(sql, sqlConn);
                    await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await rdr.ReadAsync(cancellationToken))
                    {
                        sourceData.Add((
                            rdr.GetInt32(0),
                            rdr.IsDBNull(1) ? null : rdr.GetString(1),
                            rdr.IsDBNull(2) ? null : rdr.GetString(2),
                            rdr.IsDBNull(4) ? null : rdr.GetDecimal(4),
                            rdr.IsDBNull(3) ? null : rdr.GetDateTime(3)
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                _migrationLogger.LogError("Failed to fetch source data", null, ex);
                throw;
            }

            if (!sourceData.Any())
            {
                _migrationLogger.LogInfo("No source rows found; nothing to migrate.");
                return 0;
            }

            // Truncate and reset sequence before inserting
            try
            {
                await using (var pgConn = new NpgsqlConnection(pgConnectionString))
                {
                    await pgConn.OpenAsync(cancellationToken);
                    await using var tx = await pgConn.BeginTransactionAsync(cancellationToken);
                    await using (var truncateCmd = new NpgsqlCommand("TRUNCATE TABLE erp_currency_exchange_rate RESTART IDENTITY CASCADE;", pgConn, tx))
                    {
                        await truncateCmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await tx.CommitAsync(cancellationToken);
                }
                _migrationLogger.LogInfo("Truncated erp_currency_exchange_rate and reset sequence.");
            }
            catch (Exception ex)
            {
                _migrationLogger.LogError("Failed to truncate/reset erp_currency_exchange_rate", null, ex);
                throw;
            }

            // Prepare flattened rows in company-major order
            var flattened = new List<TempRateRow>(capacity: sourceData.Count * validCompanyIds.Count);
            foreach (var companyId in validCompanyIds)
            {
                foreach (var src in sourceData)
                {
                    var fromCurrency = NormalizeCurrency(src.FromCurrency, "USD");
                    var toCurrency = NormalizeCurrency(src.ToCurrency, "INR");
                    var rate = src.ExchangeRate.HasValue && src.ExchangeRate.Value != 0m ? src.ExchangeRate.Value : 1.0m;
                    var validFrom = NormalizeTimestamp(src.FromDate ?? DateTime.UtcNow);
                    flattened.Add(new TempRateRow
                    {
                        FromCurrency = fromCurrency,
                        ToCurrency = toCurrency,
                        ValidFrom = validFrom,
                        ExchangeRate = rate,
                        CompanyId = companyId
                    });
                }
            }
            _migrationLogger.LogInfo($"Prepared {flattened.Count} rows for bulk insert (company-major order)");

            // Bulk insert all rows (no upsert, no ON CONFLICT)
            int insertedCount = 0;
            try
            {
                await using (var pgConn = new NpgsqlConnection(pgConnectionString))
                {
                    await pgConn.OpenAsync(cancellationToken);
                    await using var tx = await pgConn.BeginTransactionAsync(cancellationToken);
                    var insertSql = @"
                        INSERT INTO erp_currency_exchange_rate (
                            from_currency, to_currency, valid_from, exchange_rate, company_id, created_date, is_deleted
                        ) VALUES (
                            @from_currency, @to_currency, @valid_from, @exchange_rate, @company_id, CURRENT_TIMESTAMP, false
                        )";
                    await using var insertCmd = new NpgsqlCommand(insertSql, pgConn, tx);
                    foreach (var row in flattened)
                    {
                        insertCmd.Parameters.Clear();
                        insertCmd.Parameters.AddWithValue("@from_currency", row.FromCurrency);
                        insertCmd.Parameters.AddWithValue("@to_currency", row.ToCurrency);
                        insertCmd.Parameters.AddWithValue("@valid_from", row.ValidFrom);
                        insertCmd.Parameters.AddWithValue("@exchange_rate", row.ExchangeRate);
                        insertCmd.Parameters.AddWithValue("@company_id", row.CompanyId);
                        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                        insertedCount++;
                        if (insertedCount % 1000 == 0)
                        {
                            _migrationLogger.LogInfo($"Inserted {insertedCount} rows...");
                        }
                    }
                    await tx.CommitAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _migrationLogger.LogError("Bulk insert failed", null, ex);
                throw;
            }
            _migrationLogger.LogInfo($"Bulk insert completed. Inserted: {insertedCount}");
            // Export migration stats to Excel
            var excelPath = Path.Combine("migration_outputs", $"ErpCurrencyExchangeRateMigration_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx");
            MigrationStatsExporter.ExportToExcel(
                excelPath,
                flattened.Count,
                insertedCount,
                0,
                _logger,
                new List<(string, string)>()
            );
            _logger.LogInformation($"Migration stats exported to {excelPath}");
            return insertedCount;
        }

        private static string NormalizeCurrency(string? input, string defaultVal)
        {
            if (string.IsNullOrWhiteSpace(input))
                return defaultVal;

            var trimmed = input.Trim();
            return trimmed.Length > 10 ? trimmed.Substring(0, 10) : trimmed;
        }

        private static DateTime NormalizeTimestamp(DateTime input)
        {
            var utc = DateTime.SpecifyKind(input, DateTimeKind.Utc);
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc);
        }

        private class TempRateRow
        {
            public string FromCurrency { get; set; } = string.Empty;
            public string ToCurrency { get; set; } = string.Empty;
            public DateTime ValidFrom { get; set; }
            public decimal ExchangeRate { get; set; }
            public int CompanyId { get; set; }
        }
    }
}
