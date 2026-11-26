using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore.Config;

namespace RetakesAllocatorCore.Db;

public interface IWeaponPreferences
{
    CsItem? GetWeaponPreference(CsTeam team, WeaponAllocationType allocation);
    bool IsEnemyStuffEnabledForTeam(CsTeam team);
}

public sealed class PreferenceSnapshot : IWeaponPreferences
{
    private readonly Dictionary<(CsTeam Team, WeaponAllocationType Allocation), CsItem> _weapons = new();

    internal PreferenceSnapshot(UserSetting settings)
    {
        ZeusEnabled = settings.ZeusEnabled;
        EnemyStuffTeamPreference = settings.EnemyStuffTeamPreference;
        if (settings.WeaponPreferences is null)
            return;

        foreach (var (team, weapons) in settings.WeaponPreferences)
            foreach (var (allocation, weapon) in weapons)
                _weapons[(team, allocation)] = weapon;
    }

    public bool ZeusEnabled { get; }
    public EnemyStuffTeamPreference EnemyStuffTeamPreference { get; }
    public bool IsEnemyStuffEnabledForTeam(CsTeam team) => UserSetting.IsEnemyStuffEnabledForTeam(EnemyStuffTeamPreference, team);

    public CsItem? GetWeaponPreference(CsTeam team, WeaponAllocationType allocation) =>
        _weapons.TryGetValue((team, allocation), out var weapon) ? weapon : null;
}
