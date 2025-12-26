using System.Threading.Tasks;
using Npgsql;
using Microsoft.Extensions.Logging;

public class PostgresSequenceResetService
{
    private readonly ILogger<PostgresSequenceResetService> _logger;

    public PostgresSequenceResetService(ILogger<PostgresSequenceResetService> logger)
    {
        _logger = logger;
    }

    public async Task ResetAllSequencesAsync(NpgsqlConnection pgConn)
    {
        const string sql = @"DO $$
DECLARE
    r RECORD;
    max_id BIGINT;
BEGIN
    FOR r IN
        SELECT
            c.relname AS table_name,
            a.attname AS column_name,
            pg_get_serial_sequence(c.relname, a.attname) AS seq_name
        FROM pg_class c
        JOIN pg_attribute a ON a.attrelid = c.oid
        JOIN pg_depend d ON d.refobjid = c.oid AND d.refobjsubid = a.attnum
        JOIN pg_class s ON s.oid = d.objid
        WHERE c.relkind = 'r'
          AND s.relkind = 'S'
          AND a.attnum > 0
    LOOP
        EXECUTE format(
            'SELECT COALESCE(MAX(%I), 0) FROM %I',
            r.column_name,
            r.table_name
        ) INTO max_id;

        IF r.seq_name IS NOT NULL THEN
            EXECUTE format(
                'SELECT setval(%L, %s, false)',
                r.seq_name,
                max_id + 1
            );
        END IF;
    END LOOP;
END $$;";

        using var cmd = new NpgsqlCommand(sql, pgConn);
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("All PostgreSQL sequences have been reset to max id + 1.");
    }
}
