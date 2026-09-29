namespace PCBoost.Persistence.Schema;

/// <summary>Migration de schéma : appliquée une seule fois, dans une transaction, puis <c>PRAGMA user_version = Version</c>.</summary>
public sealed record SchemaMigration(int Version, string Name, string Sql);

/// <summary>
/// Liste ordonnée des migrations. Ne jamais modifier une migration publiée : ajouter une nouvelle version.
/// Dates : ticks UTC (INTEGER) pour le tri et le filtrage. Énumérations : noms (TEXT). Objets complexes : JSON.
/// </summary>
public static class SchemaMigrations
{
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new(1, "Schéma initial", """
            CREATE TABLE IF NOT EXISTS sessions (
                id               TEXT    NOT NULL PRIMARY KEY,
                type             TEXT    NOT NULL,
                status           TEXT    NOT NULL,
                started_at       INTEGER NOT NULL,
                completed_at     INTEGER NULL,
                title_json       TEXT    NULL,
                profile_id       TEXT    NULL,
                bytes_freed      INTEGER NOT NULL DEFAULT 0,
                requires_restart INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_status ON sessions (status);
            CREATE INDEX IF NOT EXISTS ix_sessions_started_at ON sessions (started_at);

            CREATE TABLE IF NOT EXISTS changes (
                id               TEXT    NOT NULL PRIMARY KEY,
                session_id       TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
                sequence         INTEGER NOT NULL,
                optimization_id  TEXT    NOT NULL,
                kind             TEXT    NOT NULL,
                target           TEXT    NOT NULL,
                description_json TEXT    NOT NULL,
                before_state     TEXT    NULL,
                after_state      TEXT    NULL,
                reversible       INTEGER NOT NULL,
                status           TEXT    NOT NULL,
                recorded_at      INTEGER NOT NULL,
                rolled_back_at   INTEGER NULL,
                error_detail     TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_changes_session_sequence ON changes (session_id, sequence);

            CREATE TABLE IF NOT EXISTS gaming_sessions (
                id                      TEXT    NOT NULL PRIMARY KEY,
                status                  TEXT    NOT NULL,
                started_at              INTEGER NOT NULL,
                ended_at                INTEGER NULL,
                game_id                 TEXT    NULL,
                game_name               TEXT    NULL,
                process_id              INTEGER NULL,
                optimization_session_id TEXT    NULL,
                optimizations_json      TEXT    NOT NULL,
                frame_stats_json        TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_gaming_sessions_status ON gaming_sessions (status);
            CREATE INDEX IF NOT EXISTS ix_gaming_sessions_started_at ON gaming_sessions (started_at);

            CREATE TABLE IF NOT EXISTS scans (
                id            TEXT    NOT NULL PRIMARY KEY,
                timestamp     INTEGER NOT NULL,
                score         INTEGER NOT NULL,
                finding_count INTEGER NOT NULL,
                summary_json  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_scans_timestamp ON scans (timestamp);

            CREATE TABLE IF NOT EXISTS snapshots (
                id            INTEGER NOT NULL PRIMARY KEY,
                timestamp     INTEGER NOT NULL,
                cpu_percent   REAL    NOT NULL,
                memory_percent REAL   NOT NULL,
                disk_percent  REAL    NULL,
                gpu_percent   REAL    NULL,
                cpu_temp_c    REAL    NULL,
                gpu_temp_c    REAL    NULL,
                fps           REAL    NULL,
                frame_time_ms REAL    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_snapshots_timestamp ON snapshots (timestamp);

            CREATE TABLE IF NOT EXISTS benchmarks (
                id            TEXT    NOT NULL PRIMARY KEY,
                timestamp     INTEGER NOT NULL,
                phase         TEXT    NOT NULL,
                paired_run_id TEXT    NULL,
                data_json     TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_benchmarks_timestamp ON benchmarks (timestamp);

            CREATE TABLE IF NOT EXISTS kv (
                key        TEXT    NOT NULL PRIMARY KEY,
                value      TEXT    NOT NULL,
                updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS activity (
                id           TEXT    NOT NULL PRIMARY KEY,
                timestamp    INTEGER NOT NULL,
                kind         TEXT    NOT NULL,
                message_json TEXT    NOT NULL,
                detail       TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_activity_timestamp ON activity (timestamp);
            """),
        new(2, "Historique par jeu", """
            CREATE INDEX IF NOT EXISTS ix_gaming_sessions_game ON gaming_sessions (game_id, started_at);
            """),
    ];

    /// <summary>Version de schéma la plus récente connue de cette version de l'application.</summary>
    public static int LatestVersion => All.Count == 0 ? 0 : All[^1].Version;
}
