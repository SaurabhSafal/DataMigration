using Microsoft.Data.SqlClient;
using Npgsql;
using System.Data;
using DataMigration.Services;

namespace DataMigration.Services
{
    public class EventSupplierLineItemMigration
    {
        private readonly ILogger<EventSupplierLineItemMigration> _logger;
    private MigrationLogger? _migrationLogger;
        private readonly IConfiguration _configuration;

        public EventSupplierLineItemMigration(IConfiguration configuration, ILogger<EventSupplierLineItemMigration> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

    public MigrationLogger? GetLogger() => _migrationLogger;

        public List<object> GetMappings()
        {
            return new List<object>
            {
                new { source = "PBID", target = "event_supplier_line_item_id", type = "int -> integer" },
                new { source = "EVENTID", target = "event_id", type = "int -> integer (FK to event_master)" },
                new { source = "SUPPLIER_ID", target = "supplier_id", type = "int -> integer (FK to supplier_master)" },
                new { source = "Lookup: TBL_PB_BUYER.PBID by EVENTID, PRTRANSID", target = "event_item_id", type = "Lookup -> integer" },
                new { source = "Lookup: event_supplier_price_bid_id by SUPPLIER_ID, EVENTID", target = "event_supplier_price_bid_id", type = "Lookup -> integer" },
                new { source = "PRTRANSID", target = "erp_pr_lines_id", type = "int -> integer" },
                new { source = "HSNCode", target = "hsn_code", type = "nvarchar -> varchar" },
                new { source = "QTY", target = "qty", type = "decimal -> numeric" },
                new { source = "ProposedQty", target = "proposed_qty", type = "decimal -> numeric" },
                new { source = "ItemBidStatus (0:Bidding, 1:Included, 2:Regret)", target = "item_bid_status", type = "int -> varchar (CASE mapping)" },
                new { source = "UNIT_PRICE", target = "unit_price", type = "decimal -> numeric" },
                new { source = "DiscountPer", target = "discount_percentage", type = "decimal -> numeric" },
                new { source = "Calculated: UNIT_PRICE - (UNIT_PRICE * DiscountPer / 100)", target = "final_unit_price", type = "Calculated -> numeric" },
                new { source = "GSTID", target = "tax_master_id", type = "int -> integer" },
                new { source = "GSTPer", target = "tax_percentage", type = "decimal -> numeric" },
                new { source = "GSTAmount", target = "tax_amount", type = "decimal -> numeric" },
                new { source = "Calculated: (UNIT_PRICE - (UNIT_PRICE * DiscountPer / 100)) * QTY", target = "item_total", type = "Calculated -> numeric" },
                new { source = "AddtheReadyStock", target = "supplier_ready_stock", type = "decimal -> numeric" },
                new { source = "DeliveryDate", target = "item_delivery_date", type = "datetime -> timestamp with time zone" }
            };
        }
        public async Task<int> MigrateAndUpsertAuctionSupplierAsync()
        {
            var migrated = await MigrateAsync();
            var upserted = await UpsertAuctionSupplierAsync();
            _logger.LogInformation($"MigrateAsync migrated {migrated} records, UpsertAuctionSupplierAsync upserted {upserted} records.");
            return migrated + upserted;
        }
        public async Task<int> MigrateAsync()
        {
            _migrationLogger = new MigrationLogger(_logger, "event_supplier_line_item");
            _migrationLogger.LogInfo("Starting migration");

            var sqlConnectionString = _configuration.GetConnectionString("SqlServer");
            var pgConnectionString = _configuration.GetConnectionString("PostgreSql");

            if (string.IsNullOrEmpty(sqlConnectionString) || string.IsNullOrEmpty(pgConnectionString))
            {
                throw new InvalidOperationException("Database connection strings are not configured properly.");
            }

            var migratedRecords = 0;
            var skippedRecords = 0;
            var errors = new List<string>();
            var skippedRecordDetails = new List<(string RecordId, string Reason)>();

            try
            {
                using var sqlConnection = new SqlConnection(sqlConnectionString);
                using var pgConnection = new NpgsqlConnection(pgConnectionString);

                await sqlConnection.OpenAsync();
                await pgConnection.OpenAsync();

                _logger.LogInformation("Starting EventSupplierLineItem migration...");

                // Build lookup maps for foreign keys
                var validEventIds = new HashSet<int>();
                using (var cmd = new NpgsqlCommand("SELECT event_id FROM event_master", pgConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        validEventIds.Add(reader.GetInt32(0));
                    }
                }
                _logger.LogInformation($"Found {validEventIds.Count} valid event_ids");

                var validSupplierIds = new HashSet<int>();
                using (var cmd = new NpgsqlCommand("SELECT supplier_id FROM supplier_master", pgConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        validSupplierIds.Add(reader.GetInt32(0));
                    }
                }
                _logger.LogInformation($"Found {validSupplierIds.Count} valid supplier_ids");

                // Get valid event_item_ids from PostgreSQL
                var validEventItemIds = new HashSet<int>();
                using (var cmd = new NpgsqlCommand("SELECT event_item_id FROM event_items", pgConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        validEventItemIds.Add(reader.GetInt32(0));
                    }
                }
                _logger.LogInformation($"Found {validEventItemIds.Count} valid event_item_ids");

                // Build lookup for event_item_id from TBL_PB_BUYER (PBID by EVENTID and PRTRANSID)
                var eventItemIdMap = new Dictionary<(int eventId, int prTransId), int>();
                using (var cmd = new SqlCommand(@"
                    SELECT EVENTID, PRTRANSID, PBID
                    FROM TBL_PB_BUYER
                    WHERE EVENTID IS NOT NULL AND PRTRANSID IS NOT NULL AND PBID IS NOT NULL", sqlConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        try
                        {
                            var eventId = reader.GetInt32(0);
                            var prTransId = reader.GetInt32(1);
                            var pbId = reader.GetInt32(2);
                            eventItemIdMap[(eventId, prTransId)] = pbId;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Error reading event_item_id lookup: {ex.Message}");
                        }
                    }
                }
                _logger.LogInformation($"Found {eventItemIdMap.Count} event_item_id mappings from TBL_PB_BUYER");

                // Build lookup for event_supplier_price_bid_id from the just-created table
                var eventSupplierPriceBidIdMap = new Dictionary<(int supplierId, int eventId), int>();
                using (var cmd = new NpgsqlCommand(@"
                    SELECT event_supplier_price_bid_id, supplier_id, event_id
                    FROM event_supplier_price_bid", pgConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        try
                        {
                            var id = reader.GetInt32(0);
                            var supplierId = reader.GetInt32(1);
                            var eventId = reader.GetInt32(2);
                            eventSupplierPriceBidIdMap[(supplierId, eventId)] = id;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Error reading event_supplier_price_bid_id lookup: {ex.Message}");
                        }
                    }
                }
                _logger.LogInformation($"Found {eventSupplierPriceBidIdMap.Count} event_supplier_price_bid_id mappings");

                const int batchSize = 2000;
                var insertBatch = new List<TargetRow>();
                
                // Stream process source data from TBL_PB_SUPPLIER instead of loading all into memory
                using (var cmd = new SqlCommand(@"
                    SELECT 
                        PBID,
                        EVENTID,
                        SUPPLIER_ID,
                        PRTRANSID,
                        HSNCode,
                        QTY,
                        ProposedQty,
                        ItemBidStatus,
                        UNIT_PRICE,
                        DiscountPer,
                        GSTID,
                        GSTPer,
                        GSTAmount,
                        AddtheReadyStock,
                        DeliveryDate
                    FROM TBL_PB_SUPPLIER
                    WHERE ISNULL(SEQUENCEID, 0) > 0", sqlConnection))
                {
                    cmd.CommandTimeout = 300;
                    using var reader = await cmd.ExecuteReaderAsync();
                    
                    int totalRecords = 0;
                    while (await reader.ReadAsync())
                    {
                        totalRecords++;
                        
                        var record = new SourceRow
                        {
                            PBID = reader.GetInt32(0),
                            EVENTID = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                            SUPPLIER_ID = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                            PRTRANSID = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                            HSNCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                            QTY = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                            ProposedQty = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                            ItemBidStatus = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                            UNIT_PRICE = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                            DiscountPer = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                            GSTID = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                            GSTPer = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                            GSTAmount = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                            AddtheReadyStock = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                            DeliveryDate = reader.IsDBNull(14) ? null : reader.GetDateTime(14)
                        };
                        
                        // Process record inline
                        try
                        {
                            // Validate required fields
                            if (!record.EVENTID.HasValue || !record.SUPPLIER_ID.HasValue)
                        {
                            var reason = $"EVENTID or SUPPLIER_ID is NULL, skipping";
                            _logger.LogDebug($"PBID {record.PBID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.PBID.ToString(), reason));
                            continue;
                        }

                        // Validate event_id exists
                        if (!validEventIds.Contains(record.EVENTID.Value))
                        {
                            var reason = $"event_id {record.EVENTID.Value} not found in event_master";
                            _logger.LogDebug($"PBID {record.PBID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.PBID.ToString(), reason));
                            continue;
                        }

                        // Validate supplier_id exists
                        if (!validSupplierIds.Contains(record.SUPPLIER_ID.Value))
                        {
                            var reason = $"supplier_id {record.SUPPLIER_ID.Value} not found in supplier_master";
                            _logger.LogDebug($"PBID {record.PBID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.PBID.ToString(), reason));
                            continue;
                        }

                        // Lookup event_item_id (NOT NULL - skip record if not found)
                        int? eventItemId = null;
                        if (record.PRTRANSID.HasValue)
                        {
                            var key = (record.EVENTID.Value, record.PRTRANSID.Value);
                            if (eventItemIdMap.TryGetValue(key, out var itemId))
                            {
                                if (validEventItemIds.Contains(itemId))
                                {
                                    eventItemId = itemId;
                                }
                                else
                                {
                                    var reason = $"event_item_id {itemId} not found in event_items table (FK constraint), skipping record";
                                    _logger.LogDebug($"PBID {record.PBID}: {reason}");
                                    skippedRecords++;
                                    skippedRecordDetails.Add((record.PBID.ToString(), reason));
                                    continue;
                                }
                            }
                            else
                            {
                                var reason = $"No event_item_id mapping found for EVENTID {record.EVENTID.Value}, PRTRANSID {record.PRTRANSID.Value}, skipping record";
                                _logger.LogDebug($"PBID {record.PBID}: {reason}");
                                skippedRecords++;
                                skippedRecordDetails.Add((record.PBID.ToString(), reason));
                                continue;
                            }
                        }
                        else
                        {
                            var reason = $"PRTRANSID is NULL, cannot lookup event_item_id, skipping record";
                            _logger.LogDebug($"PBID {record.PBID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.PBID.ToString(), reason));
                            continue;
                        }

                        // Lookup event_supplier_price_bid_id (NOT NULL - skip record if not found)
                        int? eventSupplierPriceBidId = null;
                        var priceBidKey = (record.SUPPLIER_ID.Value, record.EVENTID.Value);
                        if (eventSupplierPriceBidIdMap.TryGetValue(priceBidKey, out var priceBidId))
                        {
                            eventSupplierPriceBidId = priceBidId;
                        }
                        else
                        {
                            var reason = $"No event_supplier_price_bid_id found for SUPPLIER_ID {record.SUPPLIER_ID.Value}, EVENTID {record.EVENTID.Value}, skipping record";
                            _logger.LogDebug($"PBID {record.PBID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.PBID.ToString(), reason));
                            continue;
                        }

                        // Validate erp_pr_lines_id (NOT NULL - skip if NULL)
                        if (!record.PRTRANSID.HasValue)
                        {
                            var reason = $"erp_pr_lines_id (PRTRANSID) is NULL (NOT NULL constraint), skipping record";
                            _logger.LogDebug($"PBID {record.PBID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.PBID.ToString(), reason));
                            continue;
                        }

                        // Validate tax_master_id (NOT NULL - skip if NULL)
                        if (!record.GSTID.HasValue)
                        {
                            var reason = $"tax_master_id (GSTID) is NULL (NOT NULL constraint), skipping record";
                            _logger.LogDebug($"PBID {record.PBID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.PBID.ToString(), reason));
                            continue;
                        }

                        // Map ItemBidStatus to string (nullable)
                        string? itemBidStatus = null;
                        if (record.ItemBidStatus.HasValue)
                        {
                            itemBidStatus = record.ItemBidStatus.Value switch
                            {
                                0 => "Bidding",
                                1 => "Included",
                                2 => "Regret",
                                _ => null
                            };
                        }

                        // Calculate final_unit_price (NOT NULL - default to 0)
                        var unitPrice = record.UNIT_PRICE ?? 0m;
                        var discountPer = record.DiscountPer ?? 0m;
                        var finalUnitPrice = unitPrice - (unitPrice * discountPer / 100);

                        // Calculate item_total (NOT NULL - default to 0)
                        var qty = record.QTY ?? 0m;
                        var itemTotal = finalUnitPrice * qty;

                        // Get other NOT NULL fields with defaults
                        var proposedQty = record.ProposedQty ?? 0m;
                        var taxPercentage = record.GSTPer ?? 0m;
                        var taxAmount = record.GSTAmount ?? 0m;

                        var targetRow = new TargetRow
                        {
                            EventSupplierLineItemId = record.PBID,
                            EventId = record.EVENTID.Value,
                            SupplierId = record.SUPPLIER_ID.Value,
                            EventItemId = eventItemId.Value, // NOT NULL - validated above
                            EventSupplierPriceBidId = eventSupplierPriceBidId.Value, // NOT NULL - validated above
                            ErpPrLinesId = record.PRTRANSID.Value, // NOT NULL - validated above
                            HsnCode = record.HSNCode ?? "", // NOT NULL - default to empty string
                            Qty = qty, // NOT NULL - defaults to 0
                            ProposedQty = proposedQty, // NOT NULL - defaults to 0
                            ItemBidStatus = itemBidStatus, // NULLABLE
                            UnitPrice = unitPrice, // NOT NULL - defaults to 0
                            DiscountPercentage = discountPer, // NOT NULL - defaults to 0
                            FinalUnitPrice = finalUnitPrice, // NOT NULL - calculated
                            TaxMasterId = record.GSTID.Value, // NOT NULL - validated above
                            TaxPercentage = taxPercentage, // NOT NULL - defaults to 0
                            TaxAmount = taxAmount, // NOT NULL - defaults to 0
                            ItemTotal = itemTotal, // NOT NULL - calculated
                            SupplierReadyStock = record.AddtheReadyStock ?? 0m, // NOT NULL - defaults to 0
                            ItemDeliveryDate = record.DeliveryDate
                        };

                            insertBatch.Add(targetRow);
                            migratedRecords++;

                            // Execute batch when it reaches the size limit
                            if (insertBatch.Count >= batchSize)
                            {
                                await ExecuteInsertBatchOptimized(pgConnection, insertBatch);
                                insertBatch.Clear();
                            }
                        }
                        catch (Exception ex)
                        {
                            var errorMsg = $"PBID {record.PBID}: {ex.Message}";
                            _logger.LogError(errorMsg);
                            errors.Add(errorMsg);
                            skippedRecords++;
                            skippedRecordDetails.Add((record.PBID.ToString(), ex.Message));
                        }
                    }
                    
                    _logger.LogInformation($"Processed {totalRecords} records from TBL_PB_SUPPLIER");
                }

                // Execute remaining batch
                if (insertBatch.Any())
                {
                    await ExecuteInsertBatchOptimized(pgConnection, insertBatch);
                }

                _logger.LogInformation($"Migration completed. Migrated: {migratedRecords}, Skipped: {skippedRecords}");
                
                if (errors.Any())
                {
                    _logger.LogWarning($"Encountered {errors.Count} errors during migration");
                }
                // Export migration stats to Excel
                MigrationStatsExporter.ExportToExcel(
                    "EventSupplierLineItemMigration_Stats.xlsx",
                    migratedRecords + skippedRecords,
                    migratedRecords,
                    skippedRecords,
                    _logger,
                    skippedRecordDetails
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Migration failed");
                throw;
            }

            return migratedRecords;
        }

        private async Task ExecuteInsertBatchOptimized(NpgsqlConnection connection, List<TargetRow> batch)
        {
            if (!batch.Any()) return;

            // Deduplicate by event_supplier_line_item_id (keep last occurrence)
            var dedupedBatch = batch
                .GroupBy(x => x.EventSupplierLineItemId)
                .Select(g => g.Last())
                .ToList();

            try
            {
                // Use PostgreSQL COPY for high-performance bulk insert
                var copyCommand = @"COPY event_supplier_line_item (
                    event_supplier_line_item_id, event_id, supplier_id, event_item_id,
                    event_supplier_price_bid_id, erp_pr_lines_id, hsn_code, qty,
                    proposed_qty, item_bid_status, unit_price, discount_percentage,
                    final_unit_price, tax_master_id, tax_percentage, tax_amount,
                    item_total, supplier_ready_stock, item_delivery_date,
                    created_by, created_date, modified_by, modified_date,
                    is_deleted, deleted_by, deleted_date
                ) FROM STDIN (FORMAT BINARY)";

                using (var writer = connection.BeginBinaryImport(copyCommand))
                {
                    foreach (var row in dedupedBatch)
                    {
                        writer.StartRow();
                        writer.Write(row.EventSupplierLineItemId, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(row.EventId, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(row.SupplierId, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(row.EventItemId, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(row.EventSupplierPriceBidId, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(row.ErpPrLinesId, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(row.HsnCode, NpgsqlTypes.NpgsqlDbType.Varchar);
                        writer.Write(row.Qty, NpgsqlTypes.NpgsqlDbType.Numeric);
                        writer.Write(row.ProposedQty, NpgsqlTypes.NpgsqlDbType.Numeric);
                        writer.Write(row.ItemBidStatus, NpgsqlTypes.NpgsqlDbType.Varchar);
                        writer.Write(row.UnitPrice, NpgsqlTypes.NpgsqlDbType.Numeric);
                        writer.Write(row.DiscountPercentage, NpgsqlTypes.NpgsqlDbType.Numeric);
                        writer.Write(row.FinalUnitPrice, NpgsqlTypes.NpgsqlDbType.Numeric);
                        writer.Write(row.TaxMasterId, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(row.TaxPercentage, NpgsqlTypes.NpgsqlDbType.Numeric);
                        writer.Write(row.TaxAmount, NpgsqlTypes.NpgsqlDbType.Numeric);
                        writer.Write(row.ItemTotal, NpgsqlTypes.NpgsqlDbType.Numeric);
                        writer.Write(row.SupplierReadyStock, NpgsqlTypes.NpgsqlDbType.Numeric);
                        DateTime? deliveryDate = row.ItemDeliveryDate;
                        if (deliveryDate.HasValue)
                        {
                            if (deliveryDate.Value.Kind == DateTimeKind.Unspecified)
                                deliveryDate = DateTime.SpecifyKind(deliveryDate.Value, DateTimeKind.Utc);
                            else if (deliveryDate.Value.Kind == DateTimeKind.Local)
                                deliveryDate = deliveryDate.Value.ToUniversalTime();
                        }
                        writer.Write(deliveryDate, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                        writer.Write(DBNull.Value, NpgsqlTypes.NpgsqlDbType.Integer); // created_by
                        writer.Write(DateTime.UtcNow, NpgsqlTypes.NpgsqlDbType.TimestampTz); // created_date
                        writer.Write(DBNull.Value, NpgsqlTypes.NpgsqlDbType.Integer); // modified_by
                        writer.Write(DBNull.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz); // modified_date
                        writer.Write(false, NpgsqlTypes.NpgsqlDbType.Boolean); // is_deleted
                        writer.Write(DBNull.Value, NpgsqlTypes.NpgsqlDbType.Integer); // deleted_by
                        writer.Write(DBNull.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz); // deleted_date
                    }
                    await writer.CompleteAsync();
                }
                _logger.LogDebug($"Batch inserted {dedupedBatch.Count} records using COPY");
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23505") // Unique violation
            {
                _logger.LogWarning($"Batch COPY failed due to duplicates, using fallback multi-row insert");
                await ExecuteInsertBatchFallback(connection, batch);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Batch insert failed: {ex.Message}");
                throw;
            }
        }
        
        private async Task ExecuteInsertBatchFallback(NpgsqlConnection connection, List<TargetRow> batch)
        {
            if (!batch.Any()) return;
            // Deduplicate by event_supplier_line_item_id (keep last occurrence)
            var dedupedBatch = batch
                .GroupBy(x => x.EventSupplierLineItemId)
                .Select(g => g.Last())
                .ToList();

            // Build a single multi-row INSERT ... ON CONFLICT DO NOTHING
            var sb = new System.Text.StringBuilder();
            sb.Append(@"INSERT INTO event_supplier_line_item (
                event_supplier_line_item_id, event_id, supplier_id, event_item_id,
                event_supplier_price_bid_id, erp_pr_lines_id, hsn_code, qty,
                proposed_qty, item_bid_status, unit_price, discount_percentage,
                final_unit_price, tax_master_id, tax_percentage, tax_amount,
                item_total, supplier_ready_stock, item_delivery_date,
                created_by, created_date, modified_by, modified_date,
                is_deleted, deleted_by, deleted_date
            ) VALUES ");
            var paramList = new List<Npgsql.NpgsqlParameter>();
            for (int i = 0; i < dedupedBatch.Count; i++)
            {
                var row = dedupedBatch[i];
                if (i > 0) sb.Append(",");
                sb.Append($"(@p{i}_0,@p{i}_1,@p{i}_2,@p{i}_3,@p{i}_4,@p{i}_5,@p{i}_6,@p{i}_7,@p{i}_8,@p{i}_9,@p{i}_10,@p{i}_11,@p{i}_12,@p{i}_13,@p{i}_14,@p{i}_15,@p{i}_16,@p{i}_17,@p{i}_18,@p{i}_19,@p{i}_20,@p{i}_21,@p{i}_22,@p{i}_23,@p{i}_24,@p{i}_25)");
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_0", row.EventSupplierLineItemId));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_1", row.EventId));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_2", row.SupplierId));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_3", row.EventItemId));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_4", row.EventSupplierPriceBidId));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_5", row.ErpPrLinesId));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_6", row.HsnCode ?? ""));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_7", row.Qty));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_8", row.ProposedQty));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_9", (object?)row.ItemBidStatus ?? DBNull.Value));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_10", row.UnitPrice));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_11", row.DiscountPercentage));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_12", row.FinalUnitPrice));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_13", row.TaxMasterId));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_14", row.TaxPercentage));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_15", row.TaxAmount));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_16", row.ItemTotal));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_17", row.SupplierReadyStock));
                DateTime? deliveryDate = row.ItemDeliveryDate;
                if (deliveryDate.HasValue)
                {
                    if (deliveryDate.Value.Kind == DateTimeKind.Unspecified)
                        deliveryDate = DateTime.SpecifyKind(deliveryDate.Value, DateTimeKind.Utc);
                    else if (deliveryDate.Value.Kind == DateTimeKind.Local)
                        deliveryDate = deliveryDate.Value.ToUniversalTime();
                }
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_18", (object?)deliveryDate ?? DBNull.Value));
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_19", DBNull.Value)); // created_by
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_20", DateTime.UtcNow)); // created_date
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_21", DBNull.Value)); // modified_by
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_22", DBNull.Value)); // modified_date
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_23", false)); // is_deleted
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_24", DBNull.Value)); // deleted_by
                paramList.Add(new Npgsql.NpgsqlParameter($"@p{i}_25", DBNull.Value)); // deleted_date
            }
            sb.Append(" ON CONFLICT (event_supplier_line_item_id) DO NOTHING");
            using var cmd = new Npgsql.NpgsqlCommand(sb.ToString(), connection);
            cmd.Parameters.AddRange(paramList.ToArray());
            await cmd.ExecuteNonQueryAsync();
            _logger.LogDebug($"Fallback multi-row inserted {dedupedBatch.Count} records");
        }

        private class SourceRow
        {
            public int PBID { get; set; }
            public int? EVENTID { get; set; }
            public int? SUPPLIER_ID { get; set; }
            public int? PRTRANSID { get; set; }
            public string? HSNCode { get; set; }
            public decimal? QTY { get; set; }
            public decimal? ProposedQty { get; set; }
            public int? ItemBidStatus { get; set; }
            public decimal? UNIT_PRICE { get; set; }
            public decimal? DiscountPer { get; set; }
            public int? GSTID { get; set; }
            public decimal? GSTPer { get; set; }
            public decimal? GSTAmount { get; set; }
            public decimal? AddtheReadyStock { get; set; }
            public DateTime? DeliveryDate { get; set; }
        }

        private class TargetRow
        {
            public int EventSupplierLineItemId { get; set; }
            public int EventId { get; set; }
            public int SupplierId { get; set; }
            public int EventItemId { get; set; } // NOT NULL
            public int EventSupplierPriceBidId { get; set; } // NOT NULL
            public int ErpPrLinesId { get; set; } // NOT NULL
            public string HsnCode { get; set; } = ""; // NOT NULL
            public decimal Qty { get; set; } // NOT NULL
            public decimal ProposedQty { get; set; } // NOT NULL
            public string? ItemBidStatus { get; set; } // NULLABLE
            public decimal UnitPrice { get; set; } // NOT NULL
            public decimal DiscountPercentage { get; set; } // NOT NULL
            public decimal FinalUnitPrice { get; set; } // NOT NULL
            public int TaxMasterId { get; set; } // NOT NULL
            public decimal TaxPercentage { get; set; } // NOT NULL
            public decimal TaxAmount { get; set; } // NOT NULL
            public decimal ItemTotal { get; set; } // NOT NULL
            public decimal SupplierReadyStock { get; set; } // NOT NULL
            public DateTime? ItemDeliveryDate { get; set; } // NULLABLE
        }

        /// <summary>
        /// Row structure for TBL_AUC_SUPPLIER (Auction Supplier) records.
        /// These records are ordered by UPDATEID and upserted based on event_id + supplier_id + erp_pr_lines_id.
        /// </summary>
        private class AucSupplierRow
        {
            public int UPDATEID { get; set; }
            public int? EVENTID { get; set; }
            public int? SUPPLIER_ID { get; set; }
            public int? PRTRANSID { get; set; }
            public string? HSNCode { get; set; }
            public decimal? QTY { get; set; }
            public decimal? ProposedQty { get; set; }
            public int? ItemBidStatus { get; set; }
            public decimal? UNIT_PRICE { get; set; }
            public decimal? DiscountPer { get; set; }
            public int? GSTID { get; set; }
            public decimal? GSTPer { get; set; }
            public decimal? GSTAmount { get; set; }
            public decimal? AddtheReadyStock { get; set; }
            public DateTime? DeliveryDate { get; set; }
        }

        /// <summary>
        /// Upsert records from TBL_AUC_SUPPLIER (Auction Supplier).
        /// Records are ordered by UPDATEID and upserted based on the unique key: event_id + supplier_id + erp_pr_lines_id.
        /// Later records (higher UPDATEID) will overwrite earlier records with the same key.
        /// </summary>
        public async Task<int> UpsertAuctionSupplierAsync()
        {
            var sqlConnectionString = _configuration.GetConnectionString("SqlServer");
            var pgConnectionString = _configuration.GetConnectionString("PostgreSql");

            if (string.IsNullOrEmpty(sqlConnectionString) || string.IsNullOrEmpty(pgConnectionString))
            {
                throw new InvalidOperationException("Database connection strings are not configured properly.");
            }

            var upsertedRecords = 0;
            var skippedRecords = 0;
            var errors = new List<string>();
            var skippedRecordDetails = new List<(string RecordId, string Reason)>();

            try
            {
                using var sqlConnection = new SqlConnection(sqlConnectionString);
                using var pgConnection = new NpgsqlConnection(pgConnectionString);

                await sqlConnection.OpenAsync();
                await pgConnection.OpenAsync();

                _logger.LogInformation("Starting EventSupplierLineItem UPSERT from TBL_AUC_SUPPLIER...");

                // Build lookup maps for foreign keys
                var validEventIds = new HashSet<int>();
                using (var cmd = new NpgsqlCommand("SELECT event_id FROM event_master", pgConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        validEventIds.Add(reader.GetInt32(0));
                    }
                }
                _logger.LogInformation($"Found {validEventIds.Count} valid event_ids");

                var validSupplierIds = new HashSet<int>();
                using (var cmd = new NpgsqlCommand("SELECT supplier_id FROM supplier_master", pgConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        validSupplierIds.Add(reader.GetInt32(0));
                    }
                }
                _logger.LogInformation($"Found {validSupplierIds.Count} valid supplier_ids");

                // Get valid event_item_ids from PostgreSQL
                var validEventItemIds = new HashSet<int>();
                using (var cmd = new NpgsqlCommand("SELECT event_item_id FROM event_items", pgConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        validEventItemIds.Add(reader.GetInt32(0));
                    }
                }
                _logger.LogInformation($"Found {validEventItemIds.Count} valid event_item_ids");

                // Build lookup for event_item_id from TBL_PB_BUYER (PBID by EVENTID and PRTRANSID)
                var eventItemIdMap = new Dictionary<(int eventId, int prTransId), int>();
                using (var cmd = new SqlCommand(@"
                    SELECT EVENTID, PRTRANSID, PBID
                    FROM TBL_PB_BUYER
                    WHERE EVENTID IS NOT NULL AND PRTRANSID IS NOT NULL AND PBID IS NOT NULL", sqlConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        try
                        {
                            var eventId = reader.GetInt32(0);
                            var prTransId = reader.GetInt32(1);
                            var pbId = reader.GetInt32(2);
                            eventItemIdMap[(eventId, prTransId)] = pbId;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Error reading event_item_id lookup: {ex.Message}");
                        }
                    }
                }
                _logger.LogInformation($"Found {eventItemIdMap.Count} event_item_id mappings from TBL_PB_BUYER");

                // Build lookup for event_supplier_price_bid_id from the just-created table
                var eventSupplierPriceBidIdMap = new Dictionary<(int supplierId, int eventId), int>();
                using (var cmd = new NpgsqlCommand(@"
                    SELECT event_supplier_price_bid_id, supplier_id, event_id
                    FROM event_supplier_price_bid", pgConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        try
                        {
                            var id = reader.GetInt32(0);
                            var supplierId = reader.GetInt32(1);
                            var eventId = reader.GetInt32(2);
                            eventSupplierPriceBidIdMap[(supplierId, eventId)] = id;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Error reading event_supplier_price_bid_id lookup: {ex.Message}");
                        }
                    }
                }
                _logger.LogInformation($"Found {eventSupplierPriceBidIdMap.Count} event_supplier_price_bid_id mappings");

                // Fetch auction source data from TBL_AUC_SUPPLIER, ordered by UPDATEID
                var auctionData = new List<AucSupplierRow>();
                
                using (var cmd = new SqlCommand(@"
                   SELECT 
                    UPDATEID,
                    EVENTID,
                    SUPPLIER_ID,
                    PRTRANSID,
                    QTY,
                    UNIT_PRICE,
                    DiscountPer,
                    GSTID,
                    GSTPer,
                    GSTAmount
                FROM TBL_AUC_SUPPLIER
                WHERE Isnull(UPDATEID,0) > 0
                AND Isnull(SEQUENCEID,0) > 0 
                ORDER BY UPDATEID", sqlConnection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                   while (await reader.ReadAsync())
                    {
                        auctionData.Add(new AucSupplierRow
                        {
                            UPDATEID = reader.GetInt32(0),
                            EVENTID = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                            SUPPLIER_ID = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                            PRTRANSID = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                            QTY = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                            UNIT_PRICE = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                            DiscountPer = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                            GSTID = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                            GSTPer = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                            GSTAmount = reader.IsDBNull(9) ? null : reader.GetDecimal(9)
                        });
                    }
                }

                _logger.LogInformation($"Found {auctionData.Count} records from TBL_AUC_SUPPLIER (ordered by UPDATEID)");

                // Batch process upserts for better performance
                const int upsertBatchSize = 500;
                var upsertBatch = new List<(AucSupplierRow record, int eventItemId, int priceBidId, string? itemBidStatus, decimal finalUnitPrice, decimal itemTotal, decimal unitPrice, decimal discountPer, decimal qty, decimal proposedQty, decimal taxPercentage, decimal taxAmount)>();
                
                foreach (var record in auctionData)
                {
                    try
                    {
                        // Validate required fields for upsert key
                        if (!record.EVENTID.HasValue || !record.SUPPLIER_ID.HasValue || !record.PRTRANSID.HasValue)
                        {
                            var reason = $"EVENTID, SUPPLIER_ID, or PRTRANSID is NULL, cannot form unique key, skipping";
                            _logger.LogDebug($"UPDATEID {record.UPDATEID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.UPDATEID.ToString(), reason));
                            continue;
                        }

                        // Validate event_id exists
                        if (!validEventIds.Contains(record.EVENTID.Value))
                        {
                            var reason = $"event_id {record.EVENTID.Value} not found in event_master";
                            _logger.LogDebug($"UPDATEID {record.UPDATEID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.UPDATEID.ToString(), reason));
                            continue;
                        }

                        // Validate supplier_id exists
                        if (!validSupplierIds.Contains(record.SUPPLIER_ID.Value))
                        {
                            var reason = $"supplier_id {record.SUPPLIER_ID.Value} not found in supplier_master";
                            _logger.LogDebug($"UPDATEID {record.UPDATEID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.UPDATEID.ToString(), reason));
                            continue;
                        }

                        // Lookup event_item_id (NOT NULL - skip record if not found)
                        int? eventItemId = null;
                        var key = (record.EVENTID.Value, record.PRTRANSID.Value);
                        if (eventItemIdMap.TryGetValue(key, out var itemId))
                        {
                            if (validEventItemIds.Contains(itemId))
                            {
                                eventItemId = itemId;
                            }
                            else
                            {
                                var reason = $"event_item_id {itemId} not found in event_items table (FK constraint), skipping record";
                                _logger.LogDebug($"UPDATEID {record.UPDATEID}: {reason}");
                                skippedRecords++;
                                skippedRecordDetails.Add((record.UPDATEID.ToString(), reason));
                                continue;
                            }
                        }
                        else
                        {
                            var reason = $"No event_item_id mapping found for EVENTID {record.EVENTID.Value}, PRTRANSID {record.PRTRANSID.Value}, skipping record";
                            _logger.LogDebug($"UPDATEID {record.UPDATEID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.UPDATEID.ToString(), reason));
                            continue;
                        }

                        // Lookup event_supplier_price_bid_id (NOT NULL - skip record if not found)
                        int? eventSupplierPriceBidId = null;
                        var priceBidKey = (record.SUPPLIER_ID.Value, record.EVENTID.Value);
                        if (eventSupplierPriceBidIdMap.TryGetValue(priceBidKey, out var priceBidId))
                        {
                            eventSupplierPriceBidId = priceBidId;
                        }
                        else
                        {
                            var reason = $"No event_supplier_price_bid_id found for SUPPLIER_ID {record.SUPPLIER_ID.Value}, EVENTID {record.EVENTID.Value}, skipping record";
                            _logger.LogDebug($"UPDATEID {record.UPDATEID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.UPDATEID.ToString(), reason));
                            continue;
                        }

                        // Validate tax_master_id (NOT NULL - skip if NULL)
                        if (!record.GSTID.HasValue)
                        {
                            var reason = $"tax_master_id (GSTID) is NULL (NOT NULL constraint), skipping record";
                            _logger.LogDebug($"UPDATEID {record.UPDATEID}: {reason}");
                            skippedRecords++;
                            skippedRecordDetails.Add((record.UPDATEID.ToString(), reason));
                            continue;
                        }

                        // Map ItemBidStatus to string (nullable)
                        string? itemBidStatus = null;
                        if (record.ItemBidStatus.HasValue)
                        {
                            itemBidStatus = record.ItemBidStatus.Value switch
                            {
                                0 => "Bidding",
                                1 => "Included",
                                2 => "Regret",
                                _ => null
                            };
                        }

                        // Calculate final_unit_price (NOT NULL - default to 0)
                        var unitPrice = record.UNIT_PRICE ?? 0m;
                        var discountPer = record.DiscountPer ?? 0m;
                        var finalUnitPrice = unitPrice - (unitPrice * discountPer / 100);

                        // Calculate item_total (NOT NULL - default to 0)
                        var qty = record.QTY ?? 0m;
                        var itemTotal = finalUnitPrice * qty;

                        // Get other NOT NULL fields with defaults
                        var proposedQty = record.ProposedQty ?? 0m;
                        var taxPercentage = record.GSTPer ?? 0m;
                        var taxAmount = record.GSTAmount ?? 0m;

                        // Add to batch
                        upsertBatch.Add((record, eventItemId.Value, eventSupplierPriceBidId.Value, itemBidStatus, finalUnitPrice, itemTotal, unitPrice, discountPer, qty, proposedQty, taxPercentage, taxAmount));
                        
                        // Execute batch when it reaches size limit
                        if (upsertBatch.Count >= upsertBatchSize)
                        {
                            await ExecuteUpsertBatch(pgConnection, upsertBatch);
                            upsertedRecords += upsertBatch.Count;
                            upsertBatch.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"UPDATEID {record.UPDATEID}: {ex.Message}";
                        _logger.LogError(errorMsg);
                        errors.Add(errorMsg);
                        skippedRecords++;
                        skippedRecordDetails.Add((record.UPDATEID.ToString(), ex.Message));
                    }
                }
                
                // Execute remaining batch
                if (upsertBatch.Any())
                {
                    await ExecuteUpsertBatch(pgConnection, upsertBatch);
                    upsertedRecords += upsertBatch.Count;
                }

                _logger.LogInformation($"Auction UPSERT completed. Upserted: {upsertedRecords}, Skipped: {skippedRecords}");
                if (errors.Any())
                {
                    _logger.LogWarning($"Encountered {errors.Count} errors during auction upsert");
                }
                // Export upsert stats to Excel
                MigrationStatsExporter.ExportToExcel(
                    "EventSupplierLineItemAuctionUpsert_Stats.xlsx",
                    upsertedRecords + skippedRecords,
                    upsertedRecords,
                    skippedRecords,
                    _logger,
                    skippedRecordDetails
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auction UPSERT failed");
                throw;
            }

            return upsertedRecords;
        }
        
        private async Task ExecuteUpsertBatch(NpgsqlConnection connection, List<(AucSupplierRow record, int eventItemId, int priceBidId, string? itemBidStatus, decimal finalUnitPrice, decimal itemTotal, decimal unitPrice, decimal discountPer, decimal qty, decimal proposedQty, decimal taxPercentage, decimal taxAmount)> batch)
        {
            if (!batch.Any()) return;
            
            foreach (var item in batch)
            {
                var record = item.record;
                // Set defaults for all NOT NULL columns
                var eventItemId = item.eventItemId;
                var priceBidId = item.priceBidId;
                var qty = item.qty;
                var unitPrice = item.unitPrice;
                var discountPer = item.discountPer;
                var finalUnitPrice = item.finalUnitPrice;
                var taxMasterId = record.GSTID ?? 0;
                var taxPercentage = item.taxPercentage;
                var taxAmount = item.taxAmount;
                var itemTotal = item.itemTotal;
                var proposedQty = item.proposedQty;
                var supplierReadyStock = record.AddtheReadyStock ?? 0m;
                var hsnCode = record.HSNCode ?? "";
                // Ensure ItemDeliveryDate is UTC or null
                DateTime? deliveryDate = record.DeliveryDate;
                if (deliveryDate.HasValue)
                {
                    if (deliveryDate.Value.Kind == DateTimeKind.Unspecified)
                        deliveryDate = DateTime.SpecifyKind(deliveryDate.Value, DateTimeKind.Utc);
                    else if (deliveryDate.Value.Kind == DateTimeKind.Local)
                        deliveryDate = deliveryDate.Value.ToUniversalTime();
                }

                // Try update first
                var updateCmd = new NpgsqlCommand(@"
                    UPDATE event_supplier_line_item
                    SET
                        event_item_id = @EventItemId,
                        event_supplier_price_bid_id = @EventSupplierPriceBidId,
                        qty = @Qty,
                        unit_price = @UnitPrice,
                        discount_percentage = @DiscountPercentage,
                        final_unit_price = @FinalUnitPrice,
                        tax_master_id = @TaxMasterId,
                        tax_percentage = @TaxPercentage,
                        tax_amount = @TaxAmount,
                        item_total = @ItemTotal,
                        proposed_qty = @ProposedQty,
                        supplier_ready_stock = @SupplierReadyStock,
                        hsn_code = @HsnCode,
                        item_delivery_date = @ItemDeliveryDate,
                        modified_date = CURRENT_TIMESTAMP
                    WHERE event_id = @EventId AND supplier_id = @SupplierId AND erp_pr_lines_id = @ErpPrLinesId", connection);

                updateCmd.Parameters.AddWithValue("@EventItemId", eventItemId);
                updateCmd.Parameters.AddWithValue("@EventSupplierPriceBidId", priceBidId);
                updateCmd.Parameters.AddWithValue("@Qty", qty);
                updateCmd.Parameters.AddWithValue("@UnitPrice", unitPrice);
                updateCmd.Parameters.AddWithValue("@DiscountPercentage", discountPer);
                updateCmd.Parameters.AddWithValue("@FinalUnitPrice", finalUnitPrice);
                updateCmd.Parameters.AddWithValue("@TaxMasterId", taxMasterId);
                updateCmd.Parameters.AddWithValue("@TaxPercentage", taxPercentage);
                updateCmd.Parameters.AddWithValue("@TaxAmount", taxAmount);
                updateCmd.Parameters.AddWithValue("@ItemTotal", itemTotal);
                updateCmd.Parameters.AddWithValue("@ProposedQty", proposedQty);
                updateCmd.Parameters.AddWithValue("@SupplierReadyStock", supplierReadyStock);
                updateCmd.Parameters.AddWithValue("@HsnCode", hsnCode);
                updateCmd.Parameters.AddWithValue("@ItemDeliveryDate", (object?)deliveryDate ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@EventId", record.EVENTID.Value);
                updateCmd.Parameters.AddWithValue("@SupplierId", record.SUPPLIER_ID.Value);
                updateCmd.Parameters.AddWithValue("@ErpPrLinesId", record.PRTRANSID.Value);

                int rowsAffected = await updateCmd.ExecuteNonQueryAsync();

                if (rowsAffected == 0)
                {
                    // Insert if not exists
                    var insertCmd = new NpgsqlCommand(@"
                        INSERT INTO event_supplier_line_item (
                            event_id, supplier_id, erp_pr_lines_id, event_item_id,
                            event_supplier_price_bid_id, qty, proposed_qty,
                            unit_price, discount_percentage,
                            final_unit_price, tax_master_id, tax_percentage, tax_amount,
                            item_total, supplier_ready_stock, hsn_code, item_delivery_date,
                            created_date, modified_date,
                            is_deleted
                        ) VALUES (
                            @EventId, @SupplierId, @ErpPrLinesId, @EventItemId,
                            @EventSupplierPriceBidId, @Qty, @ProposedQty,
                            @UnitPrice, @DiscountPercentage,
                            @FinalUnitPrice, @TaxMasterId, @TaxPercentage, @TaxAmount,
                            @ItemTotal, @SupplierReadyStock, @HsnCode, @ItemDeliveryDate,
                            CURRENT_TIMESTAMP, CURRENT_TIMESTAMP,
                            false
                        )", connection);

                    insertCmd.Parameters.AddWithValue("@EventId", record.EVENTID.Value);
                    insertCmd.Parameters.AddWithValue("@SupplierId", record.SUPPLIER_ID.Value);
                    insertCmd.Parameters.AddWithValue("@ErpPrLinesId", record.PRTRANSID.Value);
                    insertCmd.Parameters.AddWithValue("@EventItemId", eventItemId);
                    insertCmd.Parameters.AddWithValue("@EventSupplierPriceBidId", priceBidId);
                    insertCmd.Parameters.AddWithValue("@Qty", qty);
                    insertCmd.Parameters.AddWithValue("@ProposedQty", proposedQty);
                    insertCmd.Parameters.AddWithValue("@UnitPrice", unitPrice);
                    insertCmd.Parameters.AddWithValue("@DiscountPercentage", discountPer);
                    insertCmd.Parameters.AddWithValue("@FinalUnitPrice", finalUnitPrice);
                    insertCmd.Parameters.AddWithValue("@TaxMasterId", taxMasterId);
                    insertCmd.Parameters.AddWithValue("@TaxPercentage", taxPercentage);
                    insertCmd.Parameters.AddWithValue("@TaxAmount", taxAmount);
                    insertCmd.Parameters.AddWithValue("@ItemTotal", itemTotal);
                    insertCmd.Parameters.AddWithValue("@SupplierReadyStock", supplierReadyStock);
                    insertCmd.Parameters.AddWithValue("@HsnCode", hsnCode);
                    insertCmd.Parameters.AddWithValue("@ItemDeliveryDate", (object?)deliveryDate ?? DBNull.Value);
                    try
                    {
                        await insertCmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"UPDATEID {record.UPDATEID}: {ex.Message}");
                        throw;
                    }
                }
            }
        }
    }
}
