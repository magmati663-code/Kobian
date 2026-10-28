namespace Kobian.Providers.Sources.MsSql;

/// <summary>
/// Zapytania T-SQL z R2.5.2–R2.5.3 (zgodne z SQL Server 2014+: bez STRING_AGG/STRING_SPLIT/TRIM — D5.8).
/// Wartości wyłącznie jako parametry; nazwy baz w dynamicznym SQL tylko przez QUOTENAME po stronie serwera (SEC-5.1).
/// </summary>
public static class MsSqlQueries
{
    /// <summary>D-01: serwer.</summary>
    public const string D01Server = """
        SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS major,
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(32)) AS version,
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition,
               CAST(SERVERPROPERTY('EngineEdition') AS int) AS engine_edition,
               CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)) AS instance,
               CAST(SERVERPROPERTY('IsClustered') AS int) AS is_clustered,
               CAST(SERVERPROPERTY('IsHadrEnabled') AS int) AS is_hadr;
        """;

    /// <summary>D-02: bazy użytkownika ONLINE.</summary>
    public const string D02Databases = """
        SELECT d.database_id, d.name, d.state_desc, d.recovery_model_desc, d.is_read_only,
               d.is_encrypted, d.compatibility_level, d.user_access_desc,
               (SELECT SUM(CAST(mf.size AS bigint)) * 8 * 1024 FROM sys.master_files mf
                 WHERE mf.database_id = d.database_id AND mf.type = 0) AS data_bytes,
               d.log_reuse_wait_desc
        FROM sys.databases d
        WHERE d.database_id > 4 AND d.state = 0;
        """;

    /// <summary>
    /// D-03: uprawnienia w kontekście bazy @db. Zamiast „USE [db]” w tekście — sp_executesql w kontekście bazy
    /// wybranej przez QUOTENAME po stronie serwera (nazwa bazy nigdy nie jest sklejana po stronie klienta).
    /// </summary>
    public const string D03Permissions = """
        DECLARE @sql nvarchar(max) = N'USE ' + QUOTENAME(@db) + N';
        SELECT HAS_PERMS_BY_NAME(DB_NAME(), ''DATABASE'', ''BACKUP DATABASE'') AS can_backup_db,
               HAS_PERMS_BY_NAME(DB_NAME(), ''DATABASE'', ''BACKUP LOG'') AS can_backup_log,
               HAS_PERMS_BY_NAME(NULL, NULL, ''VIEW SERVER STATE'') AS can_view_state,
               HAS_PERMS_BY_NAME(''master'', ''DATABASE'', ''CREATE DATABASE'') AS can_verifyonly;';
        EXEC sp_executesql @sql;
        """;

    /// <summary>D-04: TDE.</summary>
    public const string D04Tde = """
        SELECT DB_NAME(k.database_id) AS db, k.encryption_state, c.name AS cert_name, c.thumbprint
        FROM sys.dm_database_encryption_keys k
        LEFT JOIN master.sys.certificates c ON c.thumbprint = k.encryptor_thumbprint
        WHERE k.database_id > 4;
        """;

    /// <summary>P-01: operacje kolidujące (AC-02.2).</summary>
    public const string P01ConflictingOperations = """
        SELECT r.session_id, r.command, r.percent_complete, r.start_time
        FROM sys.dm_exec_requests r
        WHERE r.database_id = DB_ID(@db)
          AND r.command IN ('BACKUP DATABASE','BACKUP LOG','RESTORE DATABASE','DBCC','DbccFilesCompact','DbccSpaceReclaim');
        """;

    /// <summary>P-03a: ostatnia pełna nie-COPY_ONLY (D5.2).</summary>
    public const string P03LastFull = """
        SELECT TOP (1) b.backup_set_id, b.backup_set_uuid, b.backup_finish_date, b.is_copy_only,
               b.first_lsn, b.last_lsn, b.database_backup_lsn, m.physical_device_name, b.user_name, b.program_name
        FROM msdb.dbo.backupset b
        JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = b.media_set_id
        WHERE b.database_name = @db AND b.type = 'D' AND b.is_copy_only = 0
        ORDER BY b.backup_finish_date DESC;
        """;

    /// <summary>P-03b: baza różnicowa.</summary>
    public const string P03DifferentialBase = """
        SELECT differential_base_guid, differential_base_lsn
        FROM sys.master_files WHERE database_id = DB_ID(@db) AND file_id = 1;
        """;

    /// <summary>Sprawdzenie, że zakończony backup ma is_copy_only = 1 (AC-02.1).</summary>
    public const string BackupSetById = """
        SELECT TOP (1) b.backup_set_uuid, b.is_copy_only, b.has_backup_checksums, b.backup_finish_date
        FROM msdb.dbo.backupset b
        WHERE b.database_name = @db AND b.type = 'D'
        ORDER BY b.backup_finish_date DESC;
        """;

    /// <summary>
    /// Zestaw kopii utworzony przez TO DISK = @path (AC-02.1, BL-40 cz. 1): identyfikacja po urządzeniu, nie „najnowszy w bazie”
    /// — równoległa kopia innego narzędzia nie zostanie pomylona z naszą.
    /// </summary>
    public const string BackupSetByDevice = """
        SELECT TOP (1) b.backup_set_uuid, b.is_copy_only, b.has_backup_checksums, b.backup_finish_date, b.type,
               b.first_lsn, b.last_lsn, b.database_backup_lsn, CAST(b.server_name AS nvarchar(128)) AS server_name
        FROM msdb.dbo.backupset b
        JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = b.media_set_id
        WHERE b.database_name = @db AND m.physical_device_name = @path
        ORDER BY b.backup_finish_date DESC;
        """;
}
