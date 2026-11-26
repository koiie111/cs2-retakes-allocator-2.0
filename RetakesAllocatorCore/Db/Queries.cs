using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;
using RetakesAllocatorCore.Config;

namespace RetakesAllocatorCore.Db;

public class Queries
{
    private sealed record CachedSettings(PreferenceSnapshot? Value);

    private static readonly object WorkLock = new();
    private static readonly ConcurrentDictionary<ulong, CachedSettings> Cache = new();
    private static readonly HashSet<ulong> Refreshing = [];
    private static Task _tail = Task.CompletedTask;
    private static Exception? _migrationError;
    private static (DatabaseProvider Provider, string ConnectionString)? _database;
    private static int _databaseGeneration;

    public static bool ConfigureRuntimeDatabase()
    {
        var config = Configs.GetConfigData();
        var database = (config.DatabaseProvider, config.DatabaseConnectionString);
        lock (WorkLock)
        {
            if (_database == database)
                return false;
            _database = database;
            _databaseGeneration++;
            Cache.Clear();
            Refreshing.Clear();
            return true;
        }
    }

    private static void Publish(int generation, ulong id, UserSetting? setting)
    {
        var snapshot = new CachedSettings(Snapshot(setting));
        lock (WorkLock)
            if (generation == _databaseGeneration)
                Cache[id] = snapshot;
    }

    // Enqueue on the caller's thread to preserve the order of UI actions.
    private static Task<T> Enqueue<T>(Func<int, ConfigData, Task<T>> work)
    {
        lock (WorkLock)
        {
            var previous = _tail;
            var config = Configs.GetConfigData();
            var generation = _databaseGeneration;
            var operation = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch { /* Each caller receives its own failure. */ }
                return await work(generation, config).ConfigureAwait(false);
            });
            _tail = operation;
            return operation;
        }
    }

    private static int _migrationGeneration;

    public static Task StartMigration() => Enqueue((generation, config) =>
    {
        _migrationGeneration = generation;
        try
        {
            Migrate(config);
            _migrationError = null;
        }
        catch (Exception error)
        {
            _migrationError = error;
            throw;
        }
        return Task.FromResult(true);
    });

    private static void CheckReady(int generation)
    {
        if (_migrationGeneration == generation && _migrationError is { } error)
            throw new InvalidOperationException("Allocator database migration failed.", error);
    }

    public static PreferenceSnapshot? ReadSnapshot(ulong userId) =>
        Cache.TryGetValue(userId, out var cached) ? cached.Value : null;

    // Queue eviction behind any pending write or refresh for the departing player.
    public static Task ForgetPlayer(ulong userId)
    {
        if (userId == 0)
            return Task.CompletedTask;

        lock (WorkLock)
        {
            // A reconnect must be able to schedule a fresh read after eviction.
            Refreshing.Remove(userId);
            return Enqueue((generation, config) =>
            {
                lock (WorkLock)
                    if (generation == _databaseGeneration)
                        Cache.TryRemove(userId, out _);
                return Task.FromResult(true);
            });
        }
    }

    public static bool HasSnapshot(ulong userId) => Cache.ContainsKey(userId);

    public static bool IsZeusEnabled(ulong userId) =>
        Cache.TryGetValue(userId, out var cached) && cached.Value?.ZeusEnabled == true;

    private static PreferenceSnapshot? Snapshot(UserSetting? settings) =>
        settings is null ? null : new PreferenceSnapshot(settings);

    public static void Prefetch(IEnumerable<ulong> userIds)
    {
        var requested = userIds.Where(id => id != 0).ToArray();
        lock (WorkLock)
        {
            var ids = requested.Where(Refreshing.Add).ToList();
            if (ids.Count == 0)
                return;

            _ = Enqueue(async (generation, config) =>
            {
                try
                {
                    CheckReady(generation);
                    await using var db = new Db(config);
                    var rows = await db.UserSettings.Where(row => ids.Contains(row.UserId))
                        .ToDictionaryAsync(row => row.UserId);
                    foreach (var id in ids)
                        Publish(generation, id, rows.GetValueOrDefault(id));
                }
                catch (Exception error)
                {
                    Log.Error($"Could not refresh allocator preferences: {error.Message}");
                }
                finally
                {
                    lock (WorkLock)
                        if (generation == _databaseGeneration)
                            foreach (var id in ids)
                                Refreshing.Remove(id);
                }
                return true;
            });
        }
    }

    public static Task<UserSetting?> GetUserSettings(ulong userId) => Enqueue(async (generation, config) =>
    {
        if (userId == 0)
            return null;
        CheckReady(generation);
        await using var db = new Db(config);
        var setting = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);
        Publish(generation, userId, setting);
        return setting;
    });

    private static Task<UserSetting?> UpsertUserSettings(ulong userId, Action<UserSetting> mutation) => Enqueue(async (generation, config) =>
    {
        if (userId == 0)
            return null;

        CheckReady(generation);
        await using var db = new Db(config);
        var settings = await db.UserSettings.AsTracking().FirstOrDefaultAsync(u => u.UserId == userId);
        if (settings is null)
        {
            settings = new UserSetting { UserId = userId };
            db.UserSettings.Add(settings);
        }
        else
        {
            // The JSON converter does not take deep EF snapshots of nested dictionaries.
            db.Entry(settings).State = EntityState.Modified;
        }

        mutation(settings);
        await db.SaveChangesAsync();
        Publish(generation, userId, settings);
        return settings;
    });

    public static async Task<bool> ToggleZeusAsync(ulong userId) =>
        (await UpsertUserSettings(userId, settings => settings.ZeusEnabled = !settings.ZeusEnabled))?.ZeusEnabled == true;

    public static async Task<bool> ToggleAwpWeaponPreferenceAsync(ulong userId, CsTeam team, CsItem item, bool removeAny)
    {
        var removed = false;
        await UpsertUserSettings(userId, settings =>
        {
            var current = settings.GetWeaponPreference(team, WeaponAllocationType.Preferred);
            removed = removeAny ? current.HasValue : current == item;
            SetPreferred(settings, removed ? null : item);
        });
        return removed;
    }

    private static void SetPreferred(UserSetting settings, CsItem? item)
    {
        settings.SetWeaponPreference(CsTeam.Terrorist, WeaponAllocationType.Preferred,
            WeaponHelpers.CoercePreferredTeam(item, CsTeam.Terrorist));
        settings.SetWeaponPreference(CsTeam.CounterTerrorist, WeaponAllocationType.Preferred,
            WeaponHelpers.CoercePreferredTeam(item, CsTeam.CounterTerrorist));
    }

    public static async Task ReportFailure(Task operation)
    {
        try { await operation; }
        catch (Exception error) { Log.Error($"Could not save allocator preferences: {error.Message}"); }
    }

    public static async Task SetWeaponPreferenceForUserAsync(ulong userId, CsTeam team,
        WeaponAllocationType weaponAllocationType,
        CsItem? item)
    {
        await UpsertUserSettings(userId,
            userSetting => { userSetting.SetWeaponPreference(team, weaponAllocationType, item); });
    }

    public static void SetWeaponPreferenceForUser(ulong userId, CsTeam team, WeaponAllocationType weaponAllocationType,
        CsItem? item)
    {
        _ = ReportFailure(SetWeaponPreferenceForUserAsync(userId, team, weaponAllocationType, item));
    }

    public static async Task ClearWeaponPreferencesForUserAsync(ulong userId)
    {
        await UpsertUserSettings(userId, userSetting => { userSetting.WeaponPreferences = new(); });
    }

    public static void ClearWeaponPreferencesForUser(ulong userId)
    {
        _ = ReportFailure(ClearWeaponPreferencesForUserAsync(userId));
    }

    public static async Task SetAwpWeaponPreferenceAsync(ulong userId, CsItem? item)
    {
        await UpsertUserSettings(userId, settings => SetPreferred(settings, item));
    }

    public static void SetAwpWeaponPreference(ulong userId, CsItem? item)
    {
        _ = ReportFailure(SetAwpWeaponPreferenceAsync(userId, item));
    }

    public static async Task SetZeusPreferenceAsync(ulong userId, bool enabled)
    {
        await UpsertUserSettings(userId, userSetting => { userSetting.ZeusEnabled = enabled; });
    }

    public static void SetZeusPreference(ulong userId, bool enabled)
    {
        _ = ReportFailure(SetZeusPreferenceAsync(userId, enabled));
    }

    public static async Task SetEnemyStuffPreferenceAsync(ulong userId, EnemyStuffTeamPreference preference)
    {
        await UpsertUserSettings(userId, userSetting => { userSetting.EnemyStuffTeamPreference = preference; });
    }

    public static void SetEnemyStuffPreference(ulong userId, EnemyStuffTeamPreference preference)
    {
        _ = ReportFailure(SetEnemyStuffPreferenceAsync(userId, preference));
    }
    public static IDictionary<ulong, UserSetting> GetUsersSettings(ICollection<ulong> userIds)
    {
        using var db = new Db();
        var userSettingsList = db.UserSettings
            .AsNoTracking()
            .Where(u => userIds.Contains(u.UserId))
            .ToList();
        if (userSettingsList.Count == 0)
        {
            return new Dictionary<ulong, UserSetting>();
        }

        return userSettingsList
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private const int MySqlTableNotFoundError = 1146;

    private static readonly string EfProductVersion =
        typeof(DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DbContext).Assembly.GetName().Version?.ToString()
        ?? "7.0.0";

    public static void Migrate() => Migrate(Configs.GetConfigData());

    private static void Migrate(ConfigData config)
    {
        using var db = new Db(config);

        var isMySql = config.DatabaseProvider == DatabaseProvider.MySql;

        if (isMySql && !MySqlUserSettingsTableExists(db))
        {
            Log.Warn("UserSettings table was not found. Creating the schema and seeding migration history.");
            EnsureMySqlUserSettingsTable(db);
        }

        try
        {
            db.Database.Migrate();
        }
        catch (MySqlException ex) when (isMySql && IsMissingUserSettingsTable(ex))
        {
            Log.Warn(
                $"UserSettings table was missing when applying migrations ({ex.Message}). Attempting to recreate the schema.");
            EnsureMySqlUserSettingsTable(db);
            db.Database.Migrate();
        }
    }

    public static void Wipe()
    {
        using var db = new Db();
        db.UserSettings.ExecuteDelete();
        Cache.Clear();
    }

    public static void Disconnect()
    {
        Db.Disconnect();
    }

    private static bool IsMissingUserSettingsTable(MySqlException exception)
    {
        return exception.Number == MySqlTableNotFoundError &&
               exception.Message.Contains("UserSettings", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MySqlUserSettingsTableExists(Db context)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
        {
            connection.Open();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
SELECT COUNT(*)
FROM information_schema.tables
WHERE table_schema = DATABASE()
  AND table_name = 'UserSettings';
""";
            var result = command.ExecuteScalar();
            return Convert.ToInt64(result) > 0;
        }
        finally
        {
            if (shouldClose)
            {
                connection.Close();
            }
        }
    }

    private static void EnsureMySqlUserSettingsTable(Db context)
    {
        using var transaction = context.Database.BeginTransaction();

        context.Database.ExecuteSqlRaw("""
CREATE TABLE IF NOT EXISTS UserSettings
(
    UserId BIGINT UNSIGNED NOT NULL,
    WeaponPreferences LONGTEXT NULL,
    ZeusEnabled TINYINT(1) NOT NULL DEFAULT 0,
    EnemyStuffTeamPreference INT NOT NULL DEFAULT 0,
    CONSTRAINT PK_UserSettings PRIMARY KEY (UserId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
""");

        context.Database.ExecuteSqlRaw("""
ALTER TABLE UserSettings
    MODIFY COLUMN UserId BIGINT UNSIGNED NOT NULL;
""");

        context.Database.ExecuteSqlRaw(
            MySqlColumnSqlHelper.BuildAddColumnIfMissingSql(
                "UserSettings",
                "WeaponPreferences",
                "LONGTEXT NULL"));

        context.Database.ExecuteSqlRaw(
            MySqlColumnSqlHelper.BuildAddColumnIfMissingSql(
                "UserSettings",
                "ZeusEnabled",
                "TINYINT(1) NOT NULL DEFAULT 0"));

        context.Database.ExecuteSqlRaw(
            MySqlColumnSqlHelper.BuildAddColumnIfMissingSql(
                "UserSettings",
                "EnemyStuffTeamPreference",
                "INT NOT NULL DEFAULT 0"));

        SeedMigrationHistory(context);

        transaction.Commit();
    }

    private static void SeedMigrationHistory(Db context)
    {
        var historyRepository = context.GetService<IHistoryRepository>();
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();

        if (!historyRepository.Exists())
        {
            context.Database.ExecuteSqlRaw(historyRepository.GetCreateScript());
        }

        var appliedMigrations = historyRepository
            .GetAppliedMigrations()
            .Select(row => row.MigrationId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var migrationId in migrationsAssembly.Migrations.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (appliedMigrations.Contains(migrationId))
            {
                continue;
            }

            var insertScript = historyRepository.GetInsertScript(new HistoryRow(migrationId, EfProductVersion));
            if (!string.IsNullOrWhiteSpace(insertScript))
            {
                context.Database.ExecuteSqlRaw(insertScript);
            }
        }
    }
}
