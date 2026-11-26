using System.Runtime.Serialization;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.EntityFrameworkCore;

namespace RetakesAllocatorCore;

public static class Utils
{
    /**
     * Randomly get an item from the collection
     */
    public static T? Choice<T>(ICollection<T> items)
    {
        // Log.Write($"Item count: {items.Count}");
        if (items.Count == 0)
        {
            return default;
        }

        var random = new Random().Next(items.Count);
        // Log.Write($"Random: {random}");
        var item = items.ElementAt(random);
        // Log.Write($"Item: {item}");
        return item;
    }

    public static void Shuffle<T>(IList<T> list)
    {
        var random = new Random();
        var n = list.Count;
        while (n > 1)
        {
            n--;
            var k = random.Next(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
    }

    public static CsTeam ParseTeam(string teamInput)
    {
        return teamInput.ToLower() switch
        {
            "t" => CsTeam.Terrorist,
            "terrorist" => CsTeam.Terrorist,
            "ct" => CsTeam.CounterTerrorist,
            "counterterrorist" => CsTeam.CounterTerrorist,
            _ => CsTeam.None,
        };
    }

    public static string TeamString(CsTeam team, bool shortName = false)
    {
        return team switch
        {
            CsTeam.Terrorist => Translator.Instance[shortName ? "teams.terrorist_short" : "teams.terrorist"],
            CsTeam.CounterTerrorist => Translator.Instance[
                shortName ? "teams.counter_terrorist_short" : "teams.counter_terrorist"
            ],
            _ => ""
        };
    }

    private static class EnumNames<T> where T : struct, Enum
    {
        internal static readonly Dictionary<string, T> ByName = new(StringComparer.Ordinal);

        static EnumNames()
        {
            foreach (var name in Enum.GetNames<T>())
            {
                var attribute = (EnumMemberAttribute?)Attribute.GetCustomAttribute(
                    typeof(T).GetField(name)!, typeof(EnumMemberAttribute));
                if (attribute?.Value is { } value)
                    ByName.TryAdd(value, Enum.Parse<T>(name));
            }
        }
    }

    public static T ToEnum<T>(string? str) where T : struct, Enum =>
        str is not null ? EnumNames<T>.ByName.GetValueOrDefault(str) : default;

    private static readonly object MySqlVersionLock = new();
    private static string? _mysqlConnection;
    private static ServerVersion? _mysqlVersion;

    public static void SetupMySql(string connectionString, DbContextOptionsBuilder optionsBuilder)
    {
        ServerVersion version;
        lock (MySqlVersionLock)
        {
            if (_mysqlVersion is null || _mysqlConnection != connectionString)
            {
                _mysqlVersion = ServerVersion.AutoDetect(connectionString);
                _mysqlConnection = connectionString;
            }
            version = _mysqlVersion;
        }
        optionsBuilder.UseMySql(connectionString, version);
    }

    public static void SetupSqlite(string connectionString, DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(connectionString);
    }
}