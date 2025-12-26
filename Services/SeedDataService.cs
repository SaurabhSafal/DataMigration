using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DataMigration.Services
{
    public class SeedDataService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SeedDataService> _logger;

        public SeedDataService(IConfiguration configuration, ILogger<SeedDataService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<(bool Success, string Message, int RecordsInserted, List<string> TablesSeeded)> RunSeedDataAsync()
        {
            try
            {
                _logger.LogInformation("Starting seed data migration...");

                var pgConnString = _configuration.GetConnectionString("PostgreSql");
                if (string.IsNullOrEmpty(pgConnString))
                {
                    _logger.LogError("PostgreSQL connection string not found in configuration");
                    return (false, "PostgreSQL connection string not found in configuration", 0, new List<string>());
                }

                using var pgConn = new NpgsqlConnection(pgConnString);
                await pgConn.OpenAsync();

                int totalRecordsInserted = 0;
                var seedTables = new List<string>();

                // Truncate tables first (in reverse order of dependencies)
                _logger.LogInformation("Truncating seed tables before inserting data...");
                await TruncateSeedTablesAsync(pgConn);

                // Seed tables in the correct order (respecting foreign key dependencies)
                totalRecordsInserted += await SeedRolesAsync(pgConn, seedTables);
                totalRecordsInserted += await SeedPermissionGroupsAsync(pgConn, seedTables);
                totalRecordsInserted += await SeedPermissionsAsync(pgConn, seedTables);
                totalRecordsInserted += await SeedPermissionsTemplateAsync(pgConn, seedTables);
                totalRecordsInserted += await SeedUserAuditActionAsync(pgConn, seedTables);

                await pgConn.CloseAsync();

                var message = $"Seed data migration completed. {totalRecordsInserted} records inserted into {seedTables.Count} table(s): {string.Join(", ", seedTables)}";
                _logger.LogInformation(message);

                return (true, message, totalRecordsInserted, seedTables);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during seed data migration.");
                return (false, ex.Message, 0, new List<string>());
            }
        }

        private async Task TruncateSeedTablesAsync(NpgsqlConnection pgConn)
        {
            try
            {
                // Truncate only country_master and company_master tables
                var tablesToTruncate = new[]
                {
                    "country_master",
                    "company_master"
                };

                foreach (var table in tablesToTruncate)
                {
                    try
                    {
                        var truncateQuery = $"TRUNCATE TABLE {table} CASCADE";
                        using var truncateCmd = new NpgsqlCommand(truncateQuery, pgConn);
                        await truncateCmd.ExecuteNonQueryAsync();
                        _logger.LogInformation($"Truncated table: {table}");
                    }
                    catch (Exception ex)
                    {
                        // Table might not exist, log and continue
                        _logger.LogWarning($"Could not truncate {table}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error truncating tables");
                throw;
            }
        }

        private async Task<int> SeedRolesAsync(NpgsqlConnection pgConn, List<string> seedTables)
        {
            // Create table if it doesn't exist
            var createTableQuery = @"
                CREATE TABLE IF NOT EXISTS roles (
                    role_id SERIAL PRIMARY KEY,
                    name VARCHAR(100) NOT NULL UNIQUE,
                    description VARCHAR(200),
                    created_by INTEGER,
                    created_date TIMESTAMP(3) WITH TIME ZONE,
                    modified_by INTEGER,
                    modified_date TIMESTAMP(3) WITH TIME ZONE,
                    is_deleted BOOLEAN DEFAULT FALSE,
                    deleted_by INTEGER,
                    deleted_date TIMESTAMP(3) WITH TIME ZONE
                )";
            
            using var createCmd = new NpgsqlCommand(createTableQuery, pgConn);
            await createCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Ensured roles table exists");

            var checkQuery = "SELECT COUNT(*) FROM roles";
            using var checkCmd = new NpgsqlCommand(checkQuery, pgConn);
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count > 0)
            {
                _logger.LogInformation($"roles table already has {count} records, skipping seed");
                return 0;
            }

            _logger.LogInformation("Seeding roles table...");
            int recordsInserted = 0;

            var roles = new[]
            {
                (1, "Admin", "Administrator"),
                (2, "Buyer", "Buyer Role"),
                (3, "Supplier", "Supplier Role"),
                (4, "HOD", "HOD Role"),
                (5, "Technical", "Technical Role")
            };

            foreach (var role in roles)
            {
                var insertQuery = @"
                    INSERT INTO roles (role_id, name, description, created_date, is_deleted)
                    VALUES (@roleId, @name, @description, @createdDate, false)
                    ON CONFLICT (role_id) DO NOTHING";

                using var insertCmd = new NpgsqlCommand(insertQuery, pgConn);
                insertCmd.Parameters.AddWithValue("roleId", role.Item1);
                insertCmd.Parameters.AddWithValue("name", role.Item2);
                insertCmd.Parameters.AddWithValue("description", role.Item3);
                insertCmd.Parameters.AddWithValue("createdDate", DateTime.UtcNow);

                var rowsAffected = await insertCmd.ExecuteNonQueryAsync();
                recordsInserted += rowsAffected;
            }

            seedTables.Add("roles");
            _logger.LogInformation($"Seeded {recordsInserted} records into roles table");

            return recordsInserted;
        }

        private async Task<int> SeedPermissionGroupsAsync(NpgsqlConnection pgConn, List<string> seedTables)
        {
            // Create table if it doesn't exist
            var createTableQuery = @"
                CREATE TABLE IF NOT EXISTS permission_group (
                    permission_group_id SERIAL PRIMARY KEY,
                    permission_group_name VARCHAR(100) NOT NULL UNIQUE,
                    display_name VARCHAR(150) NOT NULL,
                    icon VARCHAR(100),
                    order_index INTEGER,
                    is_active BOOLEAN DEFAULT TRUE,
                    created_by INTEGER,
                    created_date TIMESTAMP(3) WITH TIME ZONE,
                    modified_by INTEGER,
                    modified_date TIMESTAMP(3) WITH TIME ZONE,
                    is_deleted BOOLEAN DEFAULT FALSE,
                    deleted_by INTEGER,
                    deleted_date TIMESTAMP(3) WITH TIME ZONE
                )";
            
            using var createCmd = new NpgsqlCommand(createTableQuery, pgConn);
            await createCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Ensured permission_group table exists");

            // Remove the check that skips seeding if records exist, always upsert
            var permissionGroups = new[]
            {
                (1, "Purchase_Requisition", "Requisitions", "material-symbols:edit-note-outline-rounded", 2),
                (2, "Events", "Events", "simple-line-icons:event", 3),
                (3, "Annual_Rate_Contract", "Contracts", "hugeicons:contracts", 6),
                (4, "Note_for_Approval", "Awards", "material-symbols:trophy-outline-rounded", 4),
                (5, "Supplier", "Supplier", "pepicons-print:people", 7),
                (6, "Purchase_Order", "Orders", "streamline-ultimate:notes-tasks", 5),
                (7, "Home", "Home", "material-symbols:home-outline-rounded", 1),
                (8, "Users", "Users", "simple-line-icons:event", 8),
                (9, "Workflow", "Workflow", "mdi:workflow", 9),
                (10, "More", "More", "circum:square-more", 10),
                (11, "Master", "Master", "oui:arrow-down", 11)
            };

            int recordsInserted = 0;
            foreach (var group in permissionGroups)
            {
                // Step 1: Fix any existing row with same name but different ID
                var fixNameConflictQuery = @"
                    UPDATE permission_group
                    SET permission_group_id = @id
                    WHERE permission_group_name = @name AND permission_group_id <> @id";
                using (var fixCmd = new NpgsqlCommand(fixNameConflictQuery, pgConn))
                {
                    fixCmd.Parameters.AddWithValue("id", group.Item1);
                    fixCmd.Parameters.AddWithValue("name", group.Item2);
                    await fixCmd.ExecuteNonQueryAsync();
                }

                // Step 2: Fix any existing row with same ID but different name
                var fixIdConflictQuery = @"
                    UPDATE permission_group
                    SET permission_group_name = @name
                    WHERE permission_group_id = @id AND permission_group_name <> @name";
                using (var fixCmd = new NpgsqlCommand(fixIdConflictQuery, pgConn))
                {
                    fixCmd.Parameters.AddWithValue("id", group.Item1);
                    fixCmd.Parameters.AddWithValue("name", group.Item2);
                    await fixCmd.ExecuteNonQueryAsync();
                }

                // Step 3: Upsert by PK
                var insertByIdQuery = @"
                    INSERT INTO permission_group (permission_group_id, permission_group_name, display_name, icon, order_index, is_active, created_date, is_deleted)
                    VALUES (@id, @name, @displayName, @icon, @orderIndex, true, @createdDate, false)
                    ON CONFLICT (permission_group_id) DO UPDATE SET
                        permission_group_name = EXCLUDED.permission_group_name,
                        display_name = EXCLUDED.display_name,
                        icon = EXCLUDED.icon,
                        order_index = EXCLUDED.order_index,
                        is_active = EXCLUDED.is_active,
                        modified_date = EXCLUDED.created_date,
                        is_deleted = EXCLUDED.is_deleted";

                using (var insertCmd = new NpgsqlCommand(insertByIdQuery, pgConn))
                {
                    insertCmd.Parameters.AddWithValue("id", group.Item1);
                    insertCmd.Parameters.AddWithValue("name", group.Item2);
                    insertCmd.Parameters.AddWithValue("displayName", group.Item3);
                    insertCmd.Parameters.AddWithValue("icon", group.Item4);
                    insertCmd.Parameters.AddWithValue("orderIndex", group.Item5);
                    insertCmd.Parameters.AddWithValue("createdDate", DateTime.UtcNow);
                    var rowsAffected = await insertCmd.ExecuteNonQueryAsync();
                    recordsInserted += rowsAffected;
                }

                // Step 4: Upsert by unique name (handles unique constraint violation)
                var insertByNameQuery = @"
                    INSERT INTO permission_group (permission_group_id, permission_group_name, display_name, icon, order_index, is_active, created_date, is_deleted)
                    VALUES (@id, @name, @displayName, @icon, @orderIndex, true, @createdDate, false)
                    ON CONFLICT (permission_group_name) DO UPDATE SET
                        permission_group_id = EXCLUDED.permission_group_id,
                        display_name = EXCLUDED.display_name,
                        icon = EXCLUDED.icon,
                        order_index = EXCLUDED.order_index,
                        is_active = EXCLUDED.is_active,
                        modified_date = EXCLUDED.created_date,
                        is_deleted = EXCLUDED.is_deleted";

                using (var insertCmd = new NpgsqlCommand(insertByNameQuery, pgConn))
                {
                    insertCmd.Parameters.AddWithValue("id", group.Item1);
                    insertCmd.Parameters.AddWithValue("name", group.Item2);
                    insertCmd.Parameters.AddWithValue("displayName", group.Item3);
                    insertCmd.Parameters.AddWithValue("icon", group.Item4);
                    insertCmd.Parameters.AddWithValue("orderIndex", group.Item5);
                    insertCmd.Parameters.AddWithValue("createdDate", DateTime.UtcNow);
                    await insertCmd.ExecuteNonQueryAsync();
                    // Don't increment recordsInserted here to avoid double counting
                }
            }

            seedTables.Add("permission_group");
            _logger.LogInformation($"Seeded {recordsInserted} records into permission_group table");

            return recordsInserted;
        }

        private async Task<int> SeedPermissionsAsync(NpgsqlConnection pgConn, List<string> seedTables)
        {
            // Create table if it doesn't exist
            var createTableQuery = @"
                CREATE TABLE IF NOT EXISTS permissions (
                    permission_id SERIAL PRIMARY KEY,
                    permission_name VARCHAR(100) NOT NULL UNIQUE,
                    description VARCHAR(255),
                    permission_group_id INTEGER NOT NULL,
                    created_by INTEGER,
                    created_date TIMESTAMP(3) WITH TIME ZONE,
                    modified_by INTEGER,
                    modified_date TIMESTAMP(3) WITH TIME ZONE,
                    is_deleted BOOLEAN DEFAULT FALSE,
                    deleted_by INTEGER,
                    deleted_date TIMESTAMP(3) WITH TIME ZONE,
                    FOREIGN KEY (permission_group_id) REFERENCES permission_group(permission_group_id)
                )";
            
            using var createCmd = new NpgsqlCommand(createTableQuery, pgConn);
            await createCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Ensured permissions table exists");

            var checkQuery = "SELECT COUNT(*) FROM permissions";
            using var checkCmd = new NpgsqlCommand(checkQuery, pgConn);
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count > 0)
            {
                _logger.LogInformation($"permissions table already has {count} records, skipping seed");
                return 0;
            }

            _logger.LogInformation("Seeding permissions table using PermissionSeed...");
            int recordsInserted = 0;
            // Use PermissionSeed.GetSeedPermissions() to get the permissions list
            var permissions = Seed.PermissionSeed.GetSeedPermissions();

            foreach (var permission in permissions)
            {
                // Upsert by PK
                var upsertByIdQuery = @"
                    INSERT INTO permissions (permission_id, permission_name, description, permission_group_id, created_date, is_deleted)
                    VALUES (@id, @name, @description, @groupId, @createdDate, false)
                    ON CONFLICT (permission_id) DO UPDATE SET
                        permission_name = EXCLUDED.permission_name,
                        description = EXCLUDED.description,
                        permission_group_id = EXCLUDED.permission_group_id,
                        modified_date = EXCLUDED.created_date,
                        is_deleted = EXCLUDED.is_deleted";

                using (var insertCmd = new NpgsqlCommand(upsertByIdQuery, pgConn))
                {
                    insertCmd.Parameters.AddWithValue("id", permission.PermissionId);
                    insertCmd.Parameters.AddWithValue("name", permission.PermissionName);
                    insertCmd.Parameters.AddWithValue("description", permission.Description ?? "");
                    insertCmd.Parameters.AddWithValue("groupId", permission.PermissionGroupId);
                    insertCmd.Parameters.AddWithValue("createdDate", DateTime.UtcNow);
                    var rowsAffected = await insertCmd.ExecuteNonQueryAsync();
                    recordsInserted += rowsAffected;
                }

                // Upsert by unique name
                var upsertByNameQuery = @"
                    INSERT INTO permissions (permission_id, permission_name, description, permission_group_id, created_date, is_deleted)
                    VALUES (@id, @name, @description, @groupId, @createdDate, false)
                    ON CONFLICT (permission_name) DO UPDATE SET
                        permission_id = EXCLUDED.permission_id,
                        description = EXCLUDED.description,
                        permission_group_id = EXCLUDED.permission_group_id,
                        modified_date = EXCLUDED.created_date,
                        is_deleted = EXCLUDED.is_deleted";

                using (var insertCmd = new NpgsqlCommand(upsertByNameQuery, pgConn))
                {
                    insertCmd.Parameters.AddWithValue("id", permission.PermissionId);
                    insertCmd.Parameters.AddWithValue("name", permission.PermissionName);
                    insertCmd.Parameters.AddWithValue("description", permission.Description ?? "");
                    insertCmd.Parameters.AddWithValue("groupId", permission.PermissionGroupId);
                    insertCmd.Parameters.AddWithValue("createdDate", DateTime.UtcNow);
                    await insertCmd.ExecuteNonQueryAsync();
                }
            }

            seedTables.Add("permissions");
            _logger.LogInformation($"Seeded {recordsInserted} records into permissions table");

            return recordsInserted;
        }

        private async Task<int> SeedPermissionsTemplateAsync(NpgsqlConnection pgConn, List<string> seedTables)
        {
            // Create table if it doesn't exist
            var createTableQuery = @"
                CREATE TABLE IF NOT EXISTS permissions_template (
                    id SERIAL PRIMARY KEY,
                    role_id INTEGER NOT NULL,
                    permission_group_id INTEGER NOT NULL,
                    permission_id INTEGER NOT NULL,
                    created_by INTEGER,
                    created_date TIMESTAMP(3) WITH TIME ZONE,
                    modified_by INTEGER,
                    modified_date TIMESTAMP(3) WITH TIME ZONE,
                    is_deleted BOOLEAN DEFAULT FALSE,
                    deleted_by INTEGER,
                    deleted_date TIMESTAMP(3) WITH TIME ZONE,
                    FOREIGN KEY (role_id) REFERENCES roles(role_id),
                    FOREIGN KEY (permission_group_id) REFERENCES permission_group(permission_group_id),
                    FOREIGN KEY (permission_id) REFERENCES permissions(permission_id)
                )";
            
            using var createCmd = new NpgsqlCommand(createTableQuery, pgConn);
            await createCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Ensured permissions_template table exists");

            var checkQuery = "SELECT COUNT(*) FROM permissions_template";
            using var checkCmd = new NpgsqlCommand(checkQuery, pgConn);
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count > 0)
            {
                _logger.LogInformation($"permissions_template table already has {count} records, skipping seed");
                return 0;
            }

            _logger.LogInformation("Seeding permissions_template table...");
            int recordsInserted = 0;

            // Manually insert PermissionsTemplateSeed.cs data
            var permissionsTemplateSeed = new List<(int id, int roleId, int groupId, int permissionId)>
            {
                (1, 2, 1, 2), (2, 2, 1, 4), (3, 2, 1, 5), (4, 2, 1, 7), (5, 2, 1, 8), (6, 2, 1, 9), (7, 2, 1, 10), (8, 2, 1, 11), (9, 2, 1, 12), (10, 2, 1, 13),
                (11, 2, 2, 16), (12, 2, 2, 17), (13, 2, 2, 19), (14, 2, 2, 21), (15, 2, 2, 22), (16, 2, 2, 23), (17, 2, 2, 24), (18, 2, 2, 25), (19, 2, 2, 26), (20, 2, 2, 27),
                (21, 2, 2, 28), (22, 2, 2, 29), (23, 2, 2, 30), (24, 2, 2, 31), (25, 2, 2, 32), (26, 2, 2, 33), (27, 2, 2, 35), (28, 2, 2, 36), (29, 2, 2, 37), (30, 2, 2, 38),
                (31, 2, 2, 39), (32, 2, 2, 41), (33, 2, 2, 42), (34, 2, 2, 43), (35, 2, 2, 44), (36, 2, 2, 45), (37, 2, 2, 47), (38, 2, 2, 48), (39, 2, 2, 49), (40, 2, 2, 50),
                (41, 2, 2, 51), (42, 2, 2, 52), (43, 2, 2, 53), (44, 2, 2, 54), (45, 2, 2, 56), (46, 2, 2, 57), (47, 2, 2, 64), (48, 2, 2, 83), (49, 2, 2, 84), (50, 2, 2, 97),
                (51, 2, 2, 98), (52, 2, 2, 100), (53, 2, 2, 101), (54, 2, 3, 58), (55, 2, 3, 61), (56, 2, 3, 62), (57, 2, 3, 67), (58, 2, 3, 86),
                (60, 2, 4, 68), (61, 2, 4, 69), (62, 2, 4, 73), (63, 2, 4, 74), (64, 2, 4, 76), (65, 2, 4, 77), (66, 2, 4, 79), (67, 2, 4, 81), (68, 2, 4, 82),
                (69, 2, 5, 103), (70, 2, 5, 104), (71, 2, 5, 105), (72, 2, 6, 107), (73, 2, 6, 108),
                (74, 4, 1, 1), (75, 4, 1, 4), (76, 4, 1, 5), (77, 4, 1, 6), (78, 4, 1, 8), (79, 4, 1, 9), (80, 4, 1, 10), (81, 4, 1, 11), (82, 4, 1, 12), (83, 4, 1, 13),
                (84, 4, 2, 16), (85, 4, 2, 18), (86, 4, 2, 19), (87, 4, 2, 20), (88, 4, 2, 22), (89, 4, 2, 23), (90, 4, 2, 24), (91, 4, 2, 25), (92, 4, 2, 27), (93, 4, 2, 28),
                (94, 4, 2, 29), (95, 4, 2, 30), (96, 4, 2, 31), (97, 4, 2, 32), (98, 4, 2, 33), (99, 4, 2, 34), (100, 4, 2, 35), (101, 4, 2, 36), (102, 4, 2, 37), (103, 4, 2, 38),
                (104, 4, 2, 39), (105, 4, 2, 41), (106, 4, 2, 42), (107, 4, 2, 43), (108, 4, 2, 44), (109, 4, 2, 45), (110, 4, 2, 47), (111, 4, 2, 48), (112, 4, 2, 49), (113, 4, 2, 50),
                (114, 4, 2, 51), (115, 4, 2, 52), (116, 4, 2, 53), (117, 4, 2, 54), (118, 4, 2, 56), (119, 4, 2, 57), (120, 4, 2, 64), (121, 4, 2, 83), (122, 4, 2, 84), (123, 4, 2, 97),
                (124, 4, 2, 98), (125, 4, 2, 99), (126, 4, 2, 100), (127, 4, 2, 101), (128, 4, 3, 58), (129, 4, 3, 61), (130, 4, 3, 62), (131, 4, 3, 67), (132, 4, 3, 86),
                (133, 4, 4, 68), (134, 4, 4, 69), (135, 4, 4, 73), (136, 4, 4, 74), (137, 4, 4, 75), (138, 4, 4, 76), (139, 4, 4, 77), (140, 4, 4, 78), (141, 4, 4, 79), (142, 4, 4, 81),
                (143, 4, 4, 82), (144, 4, 5, 103), (145, 4, 5, 104), (146, 4, 5, 105), (147, 4, 6, 106), (148, 4, 6, 108),
                (149, 5, 1, 4), (150, 5, 2, 29), (151, 5, 2, 30), (152, 5, 2, 31), (153, 5, 2, 32), (154, 5, 2, 33), (155, 5, 2, 34), (156, 5, 2, 35), (157, 5, 2, 36), (158, 5, 2, 37),
                (159, 5, 2, 38), (160, 5, 2, 39), (161, 5, 2, 41)
            };
            foreach (var (id, roleId, groupId, permissionId) in permissionsTemplateSeed)
            {
                var insertQuery = @"INSERT INTO permissions_template (id, role_id, permission_group_id, permission_id, created_date, is_deleted) VALUES (@id, @roleId, @groupId, @permissionId, @createdDate, false) ON CONFLICT (id) DO NOTHING";
                using var insertCmd = new NpgsqlCommand(insertQuery, pgConn);
                insertCmd.Parameters.AddWithValue("id", id);
                insertCmd.Parameters.AddWithValue("roleId", roleId);
                insertCmd.Parameters.AddWithValue("groupId", groupId);
                insertCmd.Parameters.AddWithValue("permissionId", permissionId);
                insertCmd.Parameters.AddWithValue("createdDate", DateTime.UtcNow);
                var rowsAffected = await insertCmd.ExecuteNonQueryAsync();
                recordsInserted += rowsAffected;
            }

            seedTables.Add("permissions_template");
            _logger.LogInformation($"Seeded {recordsInserted} records into permissions_template table");

            return recordsInserted;
        }

        private async Task<int> SeedUserAuditActionAsync(NpgsqlConnection pgConn, List<string> seedTables)
        {
            // Create table if it doesn't exist
            var createTableQuery = @"
                CREATE TABLE IF NOT EXISTS user_audit_actionss (
                    id INTEGER PRIMARY KEY,
                    action_name VARCHAR(255) NOT NULL,
                    action_description TEXT,
                    action_type VARCHAR(100)
                )";
            
            using var createCmd = new NpgsqlCommand(createTableQuery, pgConn);
            await createCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Ensured user_audit_actions table exists");

            var checkQuery = "SELECT COUNT(*) FROM user_audit_actionss";
            using var checkCmd = new NpgsqlCommand(checkQuery, pgConn);
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count > 0)
            {
                _logger.LogInformation($"user_audit_actions table already has {count} records, skipping seed");
                return 0;
            }

            _logger.LogInformation("Seeding user_audit_actions table...");
            int recordsInserted = 0;

            var userAuditActions = new[]
            {
                // Alert Actions
                (1, "PR Delegate", "PR Delegate action", "Alert"),
                (2, "Auto Assigned PR", "Auto Assigned PR action", "Alert"),
                (3, "Add Collaborative User", "Add Collaborative User action", "Alert"),
                (4, "Delete Collaborative User", "Delete Collaborative User action", "Alert"),
                (5, "Transfer Collaborative User", "Transfer Collaborative User action", "Alert"),
                (6, "Assign Technical Approval", "Assign Technical Approval action", "Alert"),
                (7, "Send for Approval NFA for Approver", "Send for Approval NFA for Approver action", "Alert"),
                (8, "Hold NFA", "Hold NFA action", "Alert"),
                (9, "Reject NFA", "Reject NFA action", "Alert"),
                (10, "Approve NFA", "Approve NFA action", "Alert"),
                (11, "All Level Approved NFA", "All Level Approved NFA action", "Alert"),
                (12, "Send for Approval Standalone NFA", "Send for Approval Standalone NFA action", "Alert"),
                (13, "After Publish Event Settings change", "After Publish Event Settings change action", "Alert"),
                (14, "Event Communication", "Event Communication action", "Alert"),
                (15, "Supplier Deviating T&C", "Supplier deviating T&C action", "Alert"),
                (16, "Responding to Deviating T&C", "Responding to deviating T&C action", "Alert"),
                (17, "Send for Approval ARC", "Send for Approval ARC action", "Alert"),
                (18, "Reject ARC", "Reject ARC action", "Alert"),
                (19, "Approve ARC", "Approve ARC action", "Alert"),
                (20, "All Level Approved ARC", "All Level Approved ARC action", "Alert"),
                (46, "NFA Clarification", "NFA Clarification action", "Alert"),
                (48, "Terminate ARC", "Terminate ARC action", "Alert"),
                
                // Notification Actions
                (21, "Create Event", "Create Event action", "Notification"),
                (22, "Terminate Event", "Terminate Event action", "Notification"),
                (23, "Recall-Partial Qty", "Recall-Partial Qty action", "Notification"),
                (24, "After Publish add and Delete supplier", "After Publish add and Delete supplier action", "Notification"),
                (25, "After Publish Change Schedule", "After Publish Change Schedule action", "Notification"),
                (26, "Recall Technical Approval", "Recall Technical Approval action", "Notification"),
                (27, "Publish Event", "Publish Event action", "Notification"),
                (28, "Next Round", "Next Round action", "Notification"),
                (29, "Bid Optimization", "Bid Optimization action", "Notification"),
                (30, "Send for Approval NFA for Reporting Manager", "Send for Approval NFA for Reporting Manager action", "Notification"),
                (31, "Recall NFA", "Recall NFA action", "Notification"),
                (32, "Update PO Number", "Update PO Number action", "Notification"),
                (33, "Send for Approval Standalone NFA", "Send for Approval Standalone NFA action", "Notification"),
                (34, "Create PO", "Create PO action", "Notification"),
                (35, "After Publish Upload Technical Doc by Collaborative User", "After Publish Upload Technical Doc by Collaborative User action", "Notification"),
                (36, "Supplier Participate in Event", "Supplier Participate in Event action", "Notification"),
                (37, "Supplier Regret in Event", "Supplier Regret in Event action", "Notification"),
                (38, "Supplier Accepting T&C", "Supplier deviating T&C action", "Notification"),
                (39, "Supplier Upload Doc", "Supplier Upload Doc action", "Notification"),
                (40, "Supplier Submit Bid", "Supplier Submit Bid action", "Notification"),
                (41, "Buyer Responding to Deviating T&C", "Buyer Responding to Deviating T&C action", "Notification"),
                (42, "Send for Approval ARC", "Send for Approval ARC action", "Notification"),
                (43, "Recall ARC", "Recall ARC action", "Notification"),
                (44, "Approve ARC", "Approve ARC action", "Notification"),
                (45, "Convert to Regular Vendor", "Convert Temp to Regular Vendor action", "Notification"),
                (47, "Terminate NFA", "Terminate NFA action", "Notification"),
            };

            foreach (var action in userAuditActions)
            {
                var insertQuery = @"
                    INSERT INTO user_audit_actionss (id, action_name, action_description, action_type)
                    VALUES (@id, @actionName, @actionDescription, @actionType)
                    ON CONFLICT (id) DO NOTHING";

                using var insertCmd = new NpgsqlCommand(insertQuery, pgConn);
                insertCmd.Parameters.AddWithValue("id", action.Item1);
                insertCmd.Parameters.AddWithValue("actionName", action.Item2);
                insertCmd.Parameters.AddWithValue("actionDescription", action.Item3);
                insertCmd.Parameters.AddWithValue("actionType", (object?)action.Item4 ?? DBNull.Value);

                await insertCmd.ExecuteNonQueryAsync();
                recordsInserted++;
            }

            // Insert additional actions with null ActionType
            var nullTypeActions = new[]
            {
                (49, "NFA Deleted", "NFA Deleted action"),
                (50, "Update Deviation-Term", "Update Deviation-Term Remarks action"),
                (51, "Event Deleted", "Event Deleted action")
            };

            foreach (var action in nullTypeActions)
            {
                var insertQuery = @"
                    INSERT INTO user_audit_actionss (id, action_name, action_description, action_type)
                    VALUES (@id, @actionName, @actionDescription, NULL)
                    ON CONFLICT (id) DO NOTHING";

                using var insertCmd = new NpgsqlCommand(insertQuery, pgConn);
                insertCmd.Parameters.AddWithValue("id", action.Item1);
                insertCmd.Parameters.AddWithValue("actionName", action.Item2);
                insertCmd.Parameters.AddWithValue("actionDescription", action.Item3);

                await insertCmd.ExecuteNonQueryAsync();
                recordsInserted++;
            }

            seedTables.Add("user_audit_actions");
            _logger.LogInformation($"Seeded {recordsInserted} records into user_audit_actions table");

            return recordsInserted;
        }
    }
}
