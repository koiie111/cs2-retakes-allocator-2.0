using System.Reflection;
using System.Runtime.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
#if RETAKE_PLAYER_SETTINGS
using PlayerSettings;
#endif
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using RetakesAllocatorCore.Managers;
using RetakesPluginShared.Enums;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace RetakesAllocator.Hud;

internal sealed class RetakeHud(BasePlugin plugin, Action<CCSPlayerController, RoundType> vote)
{
    private const int PageSize = 8;
    private const string WelcomeSetting = "retake17.skip_welcome";
    private readonly PanoramaSurface _menu = new("retake17_menu");
    private readonly PanoramaSurface _toast = new("retake17_toast");
    private readonly Dictionary<int, PlayerState> _players = [];
    private bool _queueRefreshPending;
    private bool _active = true;

#if RETAKE_PLAYER_SETTINGS
    private static readonly PluginCapability<ISettingsApi> PlayerSettings = new("settings:nfcore");
#endif

    // CSS 1.0.380 indexes its provider dictionary even for optional plugins.
    private static T? Optional<T>(PluginCapability<T> capability) where T : class
    {
        try { return capability.Get(); }
        catch (KeyNotFoundException) { return null; }
    }

    private static (bool Available, bool Skip) ReadWelcomePreference(CCSPlayerController player)
    {
#if RETAKE_PLAYER_SETTINGS
        var settings = Optional(PlayerSettings);
        return (settings is not null, settings?.GetPlayerSettingsValue(player, WelcomeSetting, "0") == "1");
#else
        return (false, false);
#endif
    }

    private static void WriteWelcomePreference(CCSPlayerController player, bool skip)
    {
#if RETAKE_PLAYER_SETTINGS
        Optional(PlayerSettings)?.SetPlayerSettingsValue(player, WelcomeSetting, skip ? "1" : "0");
#endif
    }
    private static readonly PluginCapability<Func<(int Playing, int Waiting, IReadOnlyDictionary<int, int> Positions)>> QueueSnapshot
        = new("retakes_plugin:hud_queue_snapshot");
    private static readonly PluginCapability<Func<CCSPlayerController, (int Position, int Playing, int Waiting)>> Queue
        = new("retakes_plugin:hud_queue");
    private static readonly PluginCapability<Func<CCSPlayerController, string, bool>> CommandAllowed
        = new("retakes_plugin:hud_command_allowed");
    private static readonly PluginCapability<Func<bool>> RetakesEnabled = new("retakes_plugin:enabled");

    private static readonly (WeaponAllocationType Type, string Name)[] GearSlots =
    [
        (WeaponAllocationType.FullBuyPrimary, "Основное"),
        (WeaponAllocationType.Secondary, "Пистолет"),
        (WeaponAllocationType.HalfBuyPrimary, "Малая закупка"),
        (WeaponAllocationType.PistolRound, "Пистолетный раунд")
    ];

    private static readonly Dictionary<CsItem, string> EquipmentNames = Enum.GetValues<CsItem>()
        .Distinct().ToDictionary(item => item, item => item switch
        {
            CsItem.Glock18 => "glock",
            CsItem.PPBizon => "bizon",
            CsItem.Galil => "galilar",
            CsItem.SG553 => "sg556",
            CsItem.AutoSniperT => "g3sg1",
            _ => typeof(CsItem).GetField(item.ToString())?.GetCustomAttribute<EnumMemberAttribute>()?.Value?
                .Replace("weapon_", "", StringComparison.Ordinal) ?? "default"
        });

    private sealed record Tile(string Name, Action Click, string Icon, string Detail = "", bool Selected = false);
    private sealed record Page(string Title, IReadOnlyList<Tile> Tiles, Action? Back = null,
        string Caption = "", CsTeam? Team = null, string MoreLabel = "", Action? More = null);

    private sealed class PlayerState(CCSPlayerController player)
    {
        internal CCSPlayerController Player { get; } = player;
        internal ulong SteamId { get; } = player.SteamID;
        internal PanoramaSurface.View? Menu { get; set; }
        internal PanoramaSurface.View? Toast { get; set; }
        internal Page? Page { get; set; }
        internal bool Welcome { get; set; }
        internal bool SkipWelcome { get; set; }
        internal bool ConnectedWelcomeAttempted { get; set; }
        internal bool Busy { get; set; }
        internal int Revision { get; set; }
        internal int Offset { get; set; }
        internal CsTeam Team { get; set; }
        internal UserSetting? Preferences { get; set; }
        internal ulong PreferenceId { get; set; }
        internal Timer? CloseTimer { get; set; }
        internal Timer? MenuTimer { get; set; }
        internal bool RoundToast { get; set; }
        internal bool ClientHasHud { get; set; }
    }

    private bool Enabled => _active && Configs.GetConfigData().EnablePanoramaHud && Optional(RetakesEnabled)?.Invoke() == true;

    private static bool Human(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false, SteamID: > 0 };

    private bool Current(PlayerState state) =>
        _active && Human(state.Player) && state.Player.SteamID == state.SteamId &&
        _players.TryGetValue(state.Player.Slot, out var current) && ReferenceEquals(state, current);

    private PlayerState State(CCSPlayerController player)
    {
        if (_players.TryGetValue(player.Slot, out var current) && current.SteamId == player.SteamID &&
            current.Player.Handle == player.Handle)
            return current;

        if (current is not null)
            Forget(current);

        var state = new PlayerState(player);
        _players[player.Slot] = state;
        return state;
    }

    internal void Join(CCSPlayerController? player, int retries = 4)
    {
        if (!Enabled || !Human(player))
            return;

        var state = State(player!);
        if (state.ConnectedWelcomeAttempted)
            return;

        // Capture only after the player has reached a team. The team chooser is
        // still the normal entry point, and a pending welcome never opens in combat.
        if (player!.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist or CsTeam.Spectator))
        {
            if (retries > 0)
                plugin.AddTimer(0.8f, () => { if (Current(state)) Join(player, retries - 1); }, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        var preference = ReadWelcomePreference(player);
        if (!preference.Available)
        {
            state.ConnectedWelcomeAttempted = true;
            player.PrintToChat(" \x04[RETAKE]\x01 !retake — как играть, !guns — снаряжение.");
            return;
        }

        state.SkipWelcome = preference.Skip;
        if (state.SkipWelcome)
        {
            state.ConnectedWelcomeAttempted = true;
            return;
        }

        if (SafeForWelcome(player) && state.Page is null && !state.Welcome)
        {
            OpenWelcome(state);
            state.ConnectedWelcomeAttempted = state.Welcome;
        }
    }

    private static bool SafeForWelcome(CCSPlayerController player) =>
        !player.PawnIsAlive || Utilities.GetGameRules() is { } rules && (rules.WarmupPeriod || rules.FreezePeriod);

    internal bool OpenGuns(CCSPlayerController player)
    {
        if (!Enabled || !Human(player))
            return false;

        var state = State(player);
        if (!Configs.GetConfigData().CanPlayersSelectWeapons() || !Configs.GetConfigData().GunCommandsEnabled)
        {
            player.PrintToChat(" \x04[RETAKE]\x01 Выбор оружия выключен.");
            return true;
        }

        if (Helpers.GetSteamId(player) == 0)
        {
            player.PrintToChat(" \x04[RETAKE]\x01 Steam ещё подключается. Попробуй !guns чуть позже.");
            return true;
        }

        if (!Begin(state))
            return false;

        state.Team = player.Team == CsTeam.Terrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        LoadGear(state);
        return true;
    }

    internal void Help(CCSPlayerController player)
    {
        if (!Enabled || !Human(player))
            return;

        var state = State(player);
        if (Begin(state))
            ShowHelp(state);
        else
            player.PrintToChat(" \x04[RETAKE]\x01 CT обезвреживают бомбу, T защищают её. !guns_legacy — снаряжение.");
    }

    internal bool OpenVote(CCSPlayerController player)
    {
        if (!Enabled || !Human(player) || !Configs.GetConfigData().EnableNextRoundTypeVoting)
            return false;

        var state = State(player);
        if (!Begin(state))
            return false;

        ShowVote(state);
        return true;
    }

    internal void OpenAdmin(CCSPlayerController player)
    {
        if (!Enabled || !Human(player))
            return;

        var state = State(player);
        if (HasAdminActions(player) && Begin(state))
            ShowAdmin(state);
    }

    private bool Begin(PlayerState state)
    {
        if (!_menu.Ensure())
            return false;

        if (state.Menu is not { Valid: true })
            state.Menu = _menu.For(state.Player);

        if (state.Menu is null)
            return false;

        // An already-open native menu owns its own capture. A different
        // layout may already be handling mouse input for this player.
        if (!state.Menu.Captures && Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout")
            .Any(layout => layout.IsValid && !_menu.Owns(layout) &&
                state.Player.Slot < layout.PlayerLayoutStates.Count && layout.IsInputCaptureEnabled(state.Player)))
            return false;

        if (!state.Menu.Capture(true))
            return false;

        RenewMenuTimeout(state);
        return true;
    }

    private void OpenWelcome(PlayerState state)
    {
        if (!Begin(state))
            return;

        state.Revision++;
        state.Welcome = true;
        state.Busy = false;
        state.Page = null;
        var view = state.Menu!;
        var preference = ReadWelcomePreference(state.Player);
        if (preference.Available)
            state.SkipWelcome = preference.Skip;
        view.Class("intro_skip", "Off", !preference.Available);
        view.Class("intro_skip", "Checked", state.SkipWelcome);
        view.Class("body", "Visible", false);
        view.Class("intro", "Visible", true);
        view.Class("menu", "Show", true);
    }

    private void ShowHelp(PlayerState state)
    {
        var entries = new List<Tile>
        {
            new("Как играть", () => OpenWelcome(state), "help"),
            new("Команды", () => ShowCommands(state), "commands")
        };
        if (Configs.GetConfigData().GunCommandsEnabled && Configs.GetConfigData().CanPlayersSelectWeapons())
            entries.Insert(0, new("Выбрать оружие", () => OpenGuns(state.Player), "default", "!guns"));
        if (HasAdminActions(state.Player))
            entries.Add(new("Админ-панель", () => ShowAdmin(state), "admin"));

        Show(state, new Page("Ретейк", entries));
    }

    private void ShowCommands(PlayerState state)
    {
        var config = Configs.GetConfigData();
        var entries = new List<Tile> { new("!retake", () => ShowHelp(state), "help", "Справка") };
        if (config.GunCommandsEnabled && config.CanPlayersSelectWeapons())
        {
            entries.Insert(0, new("!guns", () => OpenGuns(state.Player), "default", "Выбрать оружие"));
            entries.Add(new("!gun", () => OpenGuns(state.Player), "search", "Выбор по названию"));
        }
        if (CanUseSniper(state.Player, CsItem.AWP))
            entries.Add(new("!awp", () => OpenGuns(state.Player), "awp", "Выбор AWP"));
        if (CanUseSniper(state.Player, CsItem.Scout))
            entries.Add(new("!ssg", () => OpenGuns(state.Player), "ssg08", "Выбор SSG 08"));
        if (config.IsZeusEnabled())
            entries.Add(new("!zeus", () => OpenGuns(state.Player), "taser", "Выбор Zeus"));
        if (config.EnableNextRoundTypeVoting)
            entries.Add(new("!nextround", () => ShowVote(state), "vote", "Голос за тип раунда"));

        Show(state, new Page("Команды", entries, () => ShowHelp(state)));
    }

    private void LoadGear(PlayerState state)
    {
        Show(state, new Page("Снаряжение", [], () => ShowHelp(state), "Загружаем настройки…", state.Team));
        var revision = state.Revision;
        var steamId = Helpers.GetSteamId(state.Player);
        state.PreferenceId = steamId;
        Run(state, revision, () => Queries.GetUserSettings(steamId), preferences =>
        {
            state.Preferences = preferences;
            ShowGear(state);
        });
    }

    private void ShowGear(PlayerState state)
    {
        var randomFallback = Configs.GetConfigData().CanAssignRandomWeapons();
        var entries = GearSlots.Select(slot =>
        {
            var current = state.Preferences?.GetWeaponPreference(state.Team, slot.Type);
            if (current is null && randomFallback)
                current = WeaponHelpers.RandomLoadoutPreference;
            return new Tile(PreferenceName(current), () => ShowChoices(state, slot.Type, slot.Name), Icon(current), slot.Name);
        }).ToList();

        if (CanUseSniper(state.Player, CsItem.AWP) || CanUseSniper(state.Player, CsItem.Scout)
            || Helpers.HasEnemyStuffPermission(state.Player) || Configs.GetConfigData().IsZeusEnabled())
            entries.Add(new("Дополнительно", () => ShowExtra(state), "options"));

        var other = state.Team == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        Show(state, new Page("Снаряжение", entries, () => ShowHelp(state), TeamName(state.Team), state.Team,
            $"Снаряжение {(other == CsTeam.Terrorist ? "T" : "CT")}", () =>
            {
                state.Team = other;
                ShowGear(state);
            }));
    }

    private void ShowChoices(PlayerState state, WeaponAllocationType slot, string title)
    {
        var config = Configs.GetConfigData();
        var current = state.Preferences?.GetWeaponPreference(state.Team, slot);
        var entries = WeaponHelpers.GetPossibleWeaponsForAllocationType(slot, state.Team).Distinct()
            .Select(weapon => new Tile(PreferenceName(weapon), () => SetWeapon(state, slot, title, weapon), Icon: Icon(weapon),
                Selected: current == weapon)).ToList();
        if (config.CanAssignDefaultWeapons())
            entries.Add(new("По умолчанию", () => SetWeapon(state, slot, title, WeaponHelpers.DefaultLoadoutPreference),
                Icon: "default", Selected: current is null && !config.CanAssignRandomWeapons() || current == WeaponHelpers.DefaultLoadoutPreference));
        if (config.CanAssignRandomWeapons())
            entries.Add(new("Случайно", () => SetWeapon(state, slot, title, WeaponHelpers.RandomLoadoutPreference),
                Icon: "random", Selected: current is null || current == WeaponHelpers.RandomLoadoutPreference));

        Show(state, new Page(title, entries, () => ShowGear(state), TeamName(state.Team), state.Team));
    }

    private void SetWeapon(PlayerState state, WeaponAllocationType slot, string title, CsItem weapon)
    {
        var config = Configs.GetConfigData();
        var team = state.Team;
        var allowed = weapon switch
        {
            WeaponHelpers.DefaultLoadoutPreference => config.CanAssignDefaultWeapons(),
            WeaponHelpers.RandomLoadoutPreference => config.CanAssignRandomWeapons(),
            _ => WeaponHelpers.GetPossibleWeaponsForAllocationType(slot, team).Contains(weapon)
        };
        if (!config.GunCommandsEnabled || !config.CanPlayersSelectWeapons() || !allowed)
            return;

        Save(state, steamId => Queries.SetWeaponPreferenceForUserAsync(steamId, team, slot, weapon), () =>
        {
            state.Preferences!.SetWeaponPreference(team, slot, weapon);
            ShowChoices(state, slot, title);
        });
    }

    private void ShowExtra(PlayerState state)
    {
        var entries = new List<Tile>();
        if (CanUseSniper(state.Player, CsItem.AWP) || CanUseSniper(state.Player, CsItem.Scout))
            entries.Add(new("Снайперская винтовка", () => ShowSnipers(state), "awp",
                SniperName(state.Preferences?.GetWeaponPreference(state.Team, WeaponAllocationType.Preferred))));
        if (Helpers.HasEnemyStuffPermission(state.Player))
            entries.Add(new("Оружие другой стороны", () => ShowEnemy(state), "switch"));
        if (Configs.GetConfigData().IsZeusEnabled())
            entries.Add(new("Zeus", () => ToggleZeus(state), "taser", state.Preferences?.ZeusEnabled == true ? "Вкл." : "Выкл."));

        Show(state, new Page("Дополнительно", entries, () => ShowGear(state), TeamName(state.Team), state.Team));
    }

    private void ShowSnipers(PlayerState state)
    {
        var current = state.Preferences?.GetWeaponPreference(state.Team, WeaponAllocationType.Preferred);
        var choices = new List<CsItem?> { null };
        if (CanUseSniper(state.Player, CsItem.AWP))
            choices.Add(CsItem.AWP);
        if (CanUseSniper(state.Player, CsItem.Scout))
            choices.Add(CsItem.Scout);
        if (choices.Count == 3)
            choices.Add(WeaponHelpers.RandomSniperPreference);

        var entries = choices.Select(weapon => new Tile(SniperName(weapon), () => SetSniper(state, weapon),
            Icon: weapon is null ? "none" : Icon(weapon), Selected: current == weapon)).ToArray();
        Show(state, new Page("Снайперская винтовка", entries, () => ShowExtra(state), TeamName(state.Team), state.Team));
    }

    private void SetSniper(PlayerState state, CsItem? weapon)
    {
        if (weapon is { } value && !CanUseSniper(state.Player, value))
            return;

        Save(state, steamId => Queries.SetAwpWeaponPreferenceAsync(steamId, weapon), () =>
        {
            state.Preferences!.SetWeaponPreference(CsTeam.Terrorist, WeaponAllocationType.Preferred, weapon);
            state.Preferences.SetWeaponPreference(CsTeam.CounterTerrorist, WeaponAllocationType.Preferred, weapon);
            ShowSnipers(state);
        });
    }

    private void ShowEnemy(PlayerState state)
    {
        var values = new (EnemyStuffTeamPreference Value, string Label, string Icon)[]
        {
            (EnemyStuffTeamPreference.None, "Выкл.", "none"),
            (EnemyStuffTeamPreference.Terrorist, "Только T", "team-t"),
            (EnemyStuffTeamPreference.CounterTerrorist, "Только CT", "team-ct"),
            (EnemyStuffTeamPreference.Both, "Обе стороны", "teams")
        };
        var current = state.Preferences?.EnemyStuffTeamPreference ?? EnemyStuffTeamPreference.None;
        var entries = values.Select(option => new Tile(option.Label, () => SetEnemy(state, option.Value), option.Icon,
            Selected: option.Value == current)).ToArray();
        Show(state, new Page("Оружие другой стороны", entries, () => ShowExtra(state)));
    }

    private void SetEnemy(PlayerState state, EnemyStuffTeamPreference preference)
    {
        if (!Helpers.HasEnemyStuffPermission(state.Player))
            return;

        Save(state, steamId => Queries.SetEnemyStuffPreferenceAsync(steamId, preference), () =>
        {
            state.Preferences!.EnemyStuffTeamPreference = preference;
            ShowEnemy(state);
        });
    }

    private void ToggleZeus(PlayerState state)
    {
        if (!Configs.GetConfigData().IsZeusEnabled())
            return;

        var enabled = state.Preferences?.ZeusEnabled != true;
        Save(state, steamId => Queries.SetZeusPreferenceAsync(steamId, enabled), () =>
        {
            state.Preferences!.ZeusEnabled = enabled;
            ShowExtra(state);
        });
    }

    private void Save(PlayerState state, Func<ulong, Task> write, Action saved)
    {
        if (state.Busy || state.PreferenceId == 0 || state.PreferenceId != Helpers.GetSteamId(state.Player))
            return;

        var steamId = state.PreferenceId;
        state.Busy = true;
        Status(state, "Сохраняем…");
        Run(state, state.Revision, async () => { await write(steamId); return true; }, _ =>
        {
            var keepOffset = state.Offset;
            state.Busy = false;
            state.Preferences ??= new UserSetting { UserId = state.PreferenceId };
            saved();
            state.Offset = keepOffset;
            Render(state);
            Status(state, "Сохранено");
        });
    }

    private void Run<T>(PlayerState state, int revision, Func<Task<T>> task, Action<T> complete)
    {
        // Submit in click order. Queries serializes the actual database operations.
        _ = CompleteAsync();

        async Task CompleteAsync()
        {
            try
            {
                var result = await task();
                Server.NextFrame(() =>
                {
                    if (Current(state) && state.Revision == revision && state.Menu is { Captures: true })
                        complete(result);
                });
            }
            catch (Exception error)
            {
                plugin.Logger.LogError(error, "Retake HUD preference operation failed");
                Server.NextFrame(() =>
                {
                    if (!Current(state) || state.Revision != revision)
                        return;
                    state.Busy = false;
                    Status(state, "Не удалось сохранить или загрузить настройки");
                });
            }
        }
    }

    private static string PreferenceName(CsItem? weapon) => weapon switch
    {
        null or WeaponHelpers.DefaultLoadoutPreference => "По умолчанию",
        WeaponHelpers.RandomLoadoutPreference => "Случайно",
        _ when WeaponHelpers.IsRandomSniperPreference(weapon) => "Случайно",
        _ => Icon(weapon) switch
        {
            "deagle" => "Desert Eagle", "glock" => "Glock-18", "usp_silencer" => "USP-S",
            "cz75a" => "CZ75-Auto", "elite" => "Dual Berettas", "hkp2000" => "P2000",
            "fiveseven" => "Five-SeveN", "revolver" => "R8 Revolver", "tec9" => "Tec-9",
            "ak47" => "AK-47", "m4a1" => "M4A4", "m4a1_silencer" => "M4A1-S",
            "galilar" => "Galil AR", "bizon" => "PP-Bizon", "mac10" => "MAC-10",
            "ssg08" => "SSG 08", "sg556" => "SG 553",
            _ => weapon.Value.GetName()
        }
    };

    private static string SniperName(CsItem? weapon) => weapon is null ? "Выкл." : PreferenceName(weapon);
    private static string TeamName(CsTeam team) => team == CsTeam.Terrorist ? "Атака · T" : "Защита · CT";
    private static string Icon(CsItem? weapon) => weapon switch
    {
        null or WeaponHelpers.DefaultLoadoutPreference => "default",
        WeaponHelpers.RandomLoadoutPreference => "random",
        _ when WeaponHelpers.IsRandomSniperPreference(weapon) => "random",
        _ => EquipmentNames.GetValueOrDefault(weapon.Value, "default")
    };

    private static bool CanUseSniper(CCSPlayerController player, CsItem weapon)
    {
        var config = Configs.GetConfigData();
        if (WeaponHelpers.IsRandomSniperPreference(weapon))
            return CanUseSniper(player, CsItem.AWP) && CanUseSniper(player, CsItem.Scout);

        var mode = weapon == CsItem.AWP ? config.GetAwpMode() : config.GetSsgMode();
        return mode == AccessMode.Everyone || mode == AccessMode.VipOnly &&
            (weapon == CsItem.AWP ? Helpers.HasAwpPermission(player) : Helpers.HasSsgPermission(player));
    }


    private static bool Allowed(CCSPlayerController player, string command) =>
        command == "css_setnextround" ? AdminManager.PlayerHasPermissions(player, "@css/root") :
            Optional(CommandAllowed)?.Invoke(player, command) == true;

    private static bool HasAdminActions(CCSPlayerController player) =>
        Allowed(player, "css_forcebombsite") || Allowed(player, "css_forcebombsitestop") ||
        Allowed(player, "css_scramble") || Allowed(player, "css_setnextround") ||
        HasServiceActions(player);

    private static bool HasSpawnActions(CCSPlayerController player) =>
        Allowed(player, "css_showspawns") || Allowed(player, "css_nearestspawn") ||
        Allowed(player, "css_addspawn") || Allowed(player, "css_removespawn") || Allowed(player, "css_hidespawns");

    private static bool HasServiceActions(CCSPlayerController player) =>
        Allowed(player, "css_mapconfigs") || Allowed(player, "css_mapconfig") ||
        HasSpawnActions(player) || Allowed(player, "css_debugqueues");

    private void ShowAdmin(PlayerState state)
    {
        var player = state.Player;
        var entries = new List<Tile>();
        if (Allowed(player, "css_forcebombsite") || Allowed(player, "css_forcebombsitestop"))
            entries.Add(new("Точка следующего раунда", () => ShowPoint(state), "site"));
        if (Allowed(player, "css_setnextround"))
            entries.Add(new("Тип следующего раунда", () => ShowAdminRound(state), "clock"));
        if (Allowed(player, "css_scramble"))
            entries.Add(new("Перемешать команды", () => Confirm(state, "Перемешать команды?",
                "css_scramble", "", () => ShowAdmin(state)), "random"));
        if (HasServiceActions(player))
            entries.Add(new("Служебные настройки", () => ShowService(state), "settings"));
        Show(state, new Page("Управление ретейком", entries, () => ShowHelp(state)));
    }

    private void ShowPoint(PlayerState state)
    {
        var entries = new List<Tile>();
        AddCommandTile(entries, state, "Точка A", "site-a", "css_forcebombsite", "A");
        AddCommandTile(entries, state, "Точка B", "site-b", "css_forcebombsite", "B");
        AddCommandTile(entries, state, "Авто", "random", "css_forcebombsitestop");
        Show(state, new Page("Точка следующего раунда", entries, () => ShowAdmin(state)));
    }

    private static string RoundTitle(RoundType round) => round switch
    {
        RoundType.Pistol => "Пистолетный",
        RoundType.HalfBuy => "Малая закупка",
        _ => "Полная закупка"
    };

    private static string RoundIcon(RoundType round) => round switch
    {
        RoundType.Pistol => "glock",
        RoundType.HalfBuy => "mac10",
        _ => "ak47"
    };

    private void ShowAdminRound(PlayerState state)
    {
        var entries = new List<Tile>();
        AddCommandTile(entries, state, RoundTitle(RoundType.Pistol), RoundIcon(RoundType.Pistol), "css_setnextround", "p");
        AddCommandTile(entries, state, RoundTitle(RoundType.HalfBuy), RoundIcon(RoundType.HalfBuy), "css_setnextround", "h");
        AddCommandTile(entries, state, RoundTitle(RoundType.FullBuy), RoundIcon(RoundType.FullBuy), "css_setnextround", "f");
        Show(state, new Page("Тип следующего раунда", entries, () => ShowAdmin(state)));
    }

    private void ShowVote(PlayerState state)
    {
        if (!Configs.GetConfigData().EnableNextRoundTypeVoting)
            return;
        var entries = RoundTypeHelpers.GetRoundTypes().Select(round => new Tile(RoundTitle(round), () =>
        {
            if (!Configs.GetConfigData().EnableNextRoundTypeVoting)
                return;
            vote(state.Player, round);
            Close(state);
        }, RoundIcon(round))).ToArray();
        Show(state, new Page("Следующий раунд", entries, () => ShowCommands(state)));
    }

    private void ShowService(PlayerState state)
    {
        var entries = new List<Tile>();
        if (Allowed(state.Player, "css_mapconfigs") || Allowed(state.Player, "css_mapconfig"))
            entries.Add(new("Конфигурации карт", () => ShowMaps(state), "maps"));
        if (HasSpawnActions(state.Player))
            entries.Add(new("Редактор спавнов", () => ShowSpawns(state), "spawn"));
        if (Allowed(state.Player, "css_debugqueues"))
            entries.Add(new("Диагностика очереди", () => ShowQueue(state), "queue"));
        Show(state, new Page("Служебные настройки", entries, () => ShowAdmin(state)));
    }

    private void ShowMaps(PlayerState state)
    {
        var entries = new List<Tile>();
        AddCommandTile(entries, state, "Доступные конфигурации", "maps", "css_mapconfigs");
        var map = Server.MapName;
        if (map.Length is > 0 and <= 64 && map.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
            Allowed(state.Player, "css_mapconfig"))
            entries.Add(new("Загрузить конфигурацию", () =>
                Confirm(state, "Загрузить конфигурацию карты?", "css_mapconfig", map, () => ShowMaps(state)),
                "refresh", "Стандартная для этой карты"));
        Show(state, new Page("Конфигурации карт", entries, () => ShowService(state), map));
    }

    private void ShowSpawns(PlayerState state)
    {
        var entries = new List<Tile>();
        AddCommandTile(entries, state, "Показать спавны A", "site-a", "css_showspawns", "A");
        AddCommandTile(entries, state, "Показать спавны B", "site-b", "css_showspawns", "B");
        AddCommandTile(entries, state, "Найти ближайший спавн", "search", "css_nearestspawn");
        AddCommandTile(entries, state, "Добавить спавн CT", "team-ct", "css_addspawn", "CT N");
        AddCommandTile(entries, state, "Добавить спавн T", "team-t", "css_addspawn", "T N");
        AddCommandTile(entries, state, "Добавить спавн установщика", "site", "css_addspawn", "T Y");
        if (Allowed(state.Player, "css_removespawn"))
            entries.Add(new("Удалить ближайший спавн", () => Confirm(state, "Удалить ближайший спавн?",
                "css_removespawn", "", () => ShowSpawns(state)), "remove"));
        AddCommandTile(entries, state, "Завершить · скрыть спавны", "done", "css_hidespawns");
        Show(state, new Page("Редактор спавнов", entries, () => ShowService(state)));
    }

    private void ShowQueue(PlayerState state)
    {
        if (!Allowed(state.Player, "css_debugqueues"))
            return;
        var queueState = Optional(Queue)?.Invoke(state.Player) ?? default;
        Show(state, new Page("Диагностика очереди",
            [],
            () => ShowService(state), $"Играют: {queueState.Playing} · Ждут: {queueState.Waiting}"));
    }

    private void AddCommandTile(List<Tile> tiles, PlayerState state, string title, string icon, string command, string args = "")
    {
        if (Allowed(state.Player, command))
            tiles.Add(new(title, () => AdminCommand(state, command, args), icon));
    }

    private void Confirm(PlayerState state, string title, string command, string args, Action back)
    {
        if (Allowed(state.Player, command))
            Show(state, new Page(title, [new("Подтвердить", () => AdminCommand(state, command, args), "done"),
                new("Отмена", back, "none")], back));
    }

    private void AdminCommand(PlayerState state, string command, string args)
    {
        if (!Enabled || !Allowed(state.Player, command))
            return;

        // Let the real command resolve the player's identity, permissions,
        // feedback and current game state.
        Close(state);
        state.Player.ExecuteClientCommandFromServer(args.Length == 0 ? command : $"{command} {args}");
    }

    private void Show(PlayerState state, Page page)
    {
        state.Revision++;
        state.Offset = 0;
        state.Busy = false;
        state.Page = page;
        state.Welcome = false;
        Render(state);
    }

    private void Render(PlayerState state)
    {
        if (state.Page is not { } page || state.Menu is not { Valid: true } view)
            return;

        view.Class("intro", "Visible", false);
        view.Class("body", "Visible", true);
        view.Text("title", page.Title);
        view.Text("subtitle", page.Caption);
        view.Class("caption", "Off", page.Caption.Length == 0 && page.Team is null);
        SetTeam(view, "team", page.Team);

        for (var i = 0; i < PageSize; i++)
        {
            var index = state.Offset + i;
            var tile = index < page.Tiles.Count ? page.Tiles[index] : null;
            var id = $"item_{i}";
            view.Class(id, "Off", tile is null);
            if (tile is null)
                continue;

            view.Text($"name_{i}", tile.Name);
            view.Text($"detail_{i}", tile.Detail);
            view.Class($"detail_{i}", "Off", tile.Detail.Length == 0);
            view.Class(id, "Selected", tile.Selected);
            view.Variant($"icon_{i}", $"i-{tile.Icon}");
        }

        view.Class("back", "Off", page.Back is null);
        view.Class("previous", "Off", state.Offset == 0);
        view.Class("next", "Off", state.Offset + PageSize >= page.Tiles.Count);
        view.Class("more", "Off", page.More is null);
        if (page.More is not null)
            view.Text("more_label", page.MoreLabel);

        Status(state, "");
        view.Class("menu", "Show", true);
    }

    private static void SetTeam(PanoramaSurface.View view, string id, CsTeam? team)
    {
        view.Class(id, "Off", team is null);
        view.Variant(id, team switch { CsTeam.Terrorist => "TIcon", CsTeam.CounterTerrorist => "CtIcon", _ => "" });
    }

    private static void Status(PlayerState state, string text)
    {
        if (state.Menu is not { Valid: true } view)
            return;

        view.Text("status", text);
        view.Class("status", "Off", text.Length == 0);
    }

    internal void Click(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (!_menu.Owns(layout) || !_players.TryGetValue(player.Slot, out var state) ||
            player.SteamID != state.SteamId || !Current(state) || state.Menu is not { Captures: true })
            return;
        state.ClientHasHud = true;
        if (buttonId == "close" || !Enabled)
        {
            Close(state);
            return;
        }

        RenewMenuTimeout(state);
        if (state.Welcome)
        {
            switch (buttonId)
            {
                case "intro_close": Close(state); break;
                case "intro_commands": ShowCommands(state); break;
                case "intro_skip":
                    if (!ReadWelcomePreference(player).Available)
                        break;
                    state.SkipWelcome = !state.SkipWelcome;
                    WriteWelcomePreference(player, state.SkipWelcome);
                    state.Menu.Class("intro_skip", "Checked", state.SkipWelcome);
                    break;
            }
            return;
        }

        if (state.Page is not { } page)
            return;
        if (buttonId == "play")
        {
            Close(state);
            return;
        }
        if (state.Busy)
            return;

        switch (buttonId)
        {
            case "back": page.Back?.Invoke(); break;
            case "more": page.More?.Invoke(); break;
            case "previous" when state.Offset > 0:
                state.Offset -= PageSize;
                Render(state);
                break;
            case "next" when state.Offset + PageSize < page.Tiles.Count:
                state.Offset += PageSize;
                Render(state);
                break;
            default:
                if (buttonId.Length == 6 && buttonId.StartsWith("item_", StringComparison.Ordinal)
                    && buttonId[5] is >= '0' and < '8')
                {
                    var index = state.Offset + buttonId[5] - '0';
                    if (index < page.Tiles.Count)
                        page.Tiles[index].Click();
                }
                break;
        }
    }

    private void RenewMenuTimeout(PlayerState state)
    {
        state.MenuTimer?.Kill();
        state.MenuTimer = plugin.AddTimer(40, () =>
        {
            state.MenuTimer = null;
            if (Current(state))
                Close(state);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void Close(PlayerState state)
    {
        state.MenuTimer?.Kill();
        state.MenuTimer = null;
        state.Revision++;
        state.Page = null;
        state.Welcome = false;
        state.Busy = false;
        if (state.Menu is { Valid: true } view)
        {
            view.Class("menu", "Show", false);
            view.Capture(false);
        }
    }

    internal void Close(CCSPlayerController? player)
    {
        if (player is not null && _players.TryGetValue(player.Slot, out var state))
            Close(state);
    }

    internal void Disconnect(CCSPlayerController? player)
    {
        if (player is not null && _players.Remove(player.Slot, out var state))
            Forget(state);
        RefreshQueue();
    }

    private static void Forget(PlayerState state)
    {
        Close(state);
        HideToast(state);
    }

    internal void RoundBoundary()
    {
        foreach (var state in _players.Values)
            Close(state);

        RefreshQueue();
    }

    internal void BetweenRounds()
    {
        if (!Enabled)
            return;

        foreach (var state in _players.Values)
        {
            if (!state.ConnectedWelcomeAttempted && Current(state))
                Join(state.Player, retries: 0);
        }
    }

    internal void RoundStarted(Bombsite site)
    {
        if (!Enabled)
            return;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!Human(player) || player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist))
                continue;

            var state = State(player);
            Close(state);
            var goal = player.Team == CsTeam.CounterTerrorist ? "Обезвредь бомбу" : "Защити бомбу до взрыва";
            if (DrawToast(state, site.ToString(), goal, player.Team))
            {
                state.CloseTimer?.Kill();
                state.RoundToast = true;
                state.CloseTimer = plugin.AddTimer(6.5f, () =>
                {
                    state.CloseTimer = null;
                    if (!Current(state))
                        return;
                    HideToast(state);
                    RefreshQueue();
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }
    }

    internal bool HasRoundToast(CCSPlayerController player) =>
        _players.TryGetValue(player.Slot, out var state) && state.SteamId == player.SteamID &&
        state.ClientHasHud && state.RoundToast && state.Toast is { Valid: true };

    internal void RefreshQueue()
    {
        if (!Enabled)
        {
            if (_players.Count > 0)
                Reset();
            return;
        }

        if (_queueRefreshPending)
            return;

        _queueRefreshPending = true;
        Server.NextFrame(() =>
        {
            _queueRefreshPending = false;
            if (!Enabled)
                return;

            var positions = Optional(QueueSnapshot)?.Invoke().Positions;
            var queue = positions is null ? Optional(Queue) : null;
            if (positions is null && queue is null)
                return;

            foreach (var player in Utilities.GetPlayers())
            {
                if (!Human(player))
                    continue;

                if (player.Team == CsTeam.Spectator && (positions is not null ? positions.GetValueOrDefault(player.Slot) : queue!(player).Position) is > 0 and var position)
                {
                    var state = State(player);
                    if (!state.RoundToast)
                        DrawToast(state, position.ToString(), "Ожидаешь входа в игру", null);
                }
                else if (_players.TryGetValue(player.Slot, out var state) && !state.RoundToast)
                    HideToast(state);
            }
        });
    }

    private bool DrawToast(PlayerState state, string badge, string message, CsTeam? team)
    {
        if (!_toast.Ensure())
            return false;
        if (state.Toast is not { Valid: true })
            state.Toast = _toast.For(state.Player);
        if (state.Toast is not { } view)
            return false;

        view.Text("badge", badge);
        view.Text("message", message);
        SetTeam(view, "toast_team", team);
        view.Class("toast", "Show", true);
        return true;
    }

    private static void HideToast(PlayerState state)
    {
        state.CloseTimer?.Kill();
        state.CloseTimer = null;
        state.RoundToast = false;
        if (state.Toast is { Valid: true } view)
            view.Class("toast", "Show", false);
    }

    internal void Reset()
    {
        foreach (var state in _players.Values)
            Forget(state);
        _players.Clear();
        _menu.Destroy();
        _toast.Destroy();
        _queueRefreshPending = false;
    }

    internal void Unload()
    {
        _active = false;
        Reset();
    }
}
