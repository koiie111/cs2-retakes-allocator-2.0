using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Events;
using RetakesAllocatorCore.Managers;
using RetakesAllocator.Menus;
using RetakesAllocator.Hud;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using SQLitePCL;
using RetakesAllocator.AdvancedMenus;
using static RetakesAllocatorCore.PluginInfo;
using RetakesPluginShared;
using RetakesPluginShared.Events;

namespace RetakesAllocator;

[MinimumApiVersion(380)]
public class RetakesAllocator : BasePlugin
{
    public override string ModuleName => "Retakes Allocator Plugin";
    public override string ModuleVersion => PluginInfo.Version;
    public override string ModuleAuthor => "Yoni Lerner, B3none, Gold KingZ";
    public override string ModuleDescription => "https://github.com/yonilerner/cs2-retakes-allocator";

    private readonly AllocatorMenuManager _allocatorMenuManager = new();
    private readonly AdvancedGunMenu _advancedGunMenu = new();
    private RetakeHud? _hud;
    private StrongBox<Func<CCSPlayerController, bool>?>? _toastProvider;
    private readonly Dictionary<CCSPlayerController, Dictionary<ItemSlotType, CsItem>> _allocatedPlayerItems = new();
    private IRetakesPluginEventSender? RetakesPluginEventSender { get; set; }

    private CustomGameData? CustomFunctions { get; set; }

    private bool IsAllocatingForRound { get; set; }
    private string _bombsite = "";
    private bool _announceBombsite;
    private bool _bombsiteAnnounceOneTime;
    private bool _weaponDataSignatureFailed;
    private bool _canAcquireHooked;
    private bool _active;
    private bool _centerTickRegistered;
    private bool _centerPending;
    private int _centerGeneration;
    private readonly HashSet<uint> _weapons = [];
    private readonly List<uint> _bombTargets = [];
    private bool _mapEntitiesIndexed;
    private int _roundGeneration;
    private int _mapGeneration;
    private readonly Dictionary<int, object> _sessions = new();

    private sealed record RoundPlayer(CCSPlayerController Player, int Slot, ulong SteamId, CsTeam Team,
        uint Pawn, object Session, bool Awp, bool Ssg, bool EnemyStuff);

    private object GetSession(CCSPlayerController player)
    {
        if (!_sessions.TryGetValue(player.Slot, out var session))
            _sessions[player.Slot] = session = new object();
        return session;
    }

    private CCSPlayerController? ResolveSession(int slot, object session)
    {
        return _active && _sessions.TryGetValue(slot, out var current) && current == session &&
            Utilities.GetPlayerFromSlot(slot) is { IsValid: true, Connected: PlayerConnectedState.Connected } player
            ? player : null;
    }

    private bool IsCurrent(RoundPlayer state) =>
        ResolveSession(state.Slot, state.Session) is { } player && player.Team == state.Team &&
        player.PlayerPawn.Raw == state.Pawn && player.PawnIsAlive;

    private static void ConfigureDatabase(bool retryMigration = false)
    {
        if ((Queries.ConfigureRuntimeDatabase() || retryMigration) && Configs.GetConfigData().MigrateOnStartup)
            _ = Queries.ReportFailure(Queries.StartMigration());
    }

    private void PrefetchPlayers()
    {
        Queries.Prefetch(Utilities.GetPlayers().Select(Helpers.GetSteamId));
    }

    private void ReplyWhenSaved(CCSPlayerController player, int callingContext, Task<string> response, bool split = true)
    {
        var slot = player.Slot;
        var session = GetSession(player);
        _ = CompleteAsync();

        async Task CompleteAsync()
        {
            string message;
            try { message = await response.ConfigureAwait(false); }
            catch (Exception error)
            {
                Logger.LogError(error, "Could not save Retakes preferences");
                message = Translator.Instance["weapon_preference.storage_error"];
            }

            Server.NextFrame(() =>
            {
                if (ResolveSession(slot, session) is not { } recipient)
                    return;
                Action<string> reply = callingContext == (int)CommandCallingContext.Console
                    ? recipient.PrintToConsole : recipient.PrintToChat;
                if (split)
                    Helpers.WriteNewlineDelimited(message, reply);
                else
                    reply($"{MessagePrefix}{message}");
            });
        }
    }

    private static async Task<string> Message(Task<Tuple<string, CsItem?>> response) =>
        (await response.ConfigureAwait(false)).Item1;

    #region Setup

    public override void Load(bool hotReload)
    {
        Configs.Shared.Module = ModuleDirectory;

        Log.Debug($"Loaded. Hot reload: {hotReload}");
        Batteries.Init();
        ResetState();
        _active = true;
        if (hotReload)
            PrefetchPlayers();
        RegisterListener<Listeners.OnClientAuthorized>((_, steamId) => Queries.Prefetch([steamId.SteamId64]));

        _hud = new RetakeHud(this, (player, round) =>
            _allocatorMenuManager.GetMenu<VoteMenu>(MenuType.NextRoundVote).CastPanoramaVote(player, round));
        PublishToastProvider(_hud.HasRoundToast);
        RegisterListener<Listeners.OnCustomHudClicked>(_hud.Click);
        // A jointeam request may enter the queue without producing a player_team event.
        UpdateQueueTimer();
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            _mapGeneration++;
            _roundGeneration++;
            CloseCenterAnnouncement();
            _hud?.Reset();
            _weapons.Clear();
            _bombTargets.Clear();
            _mapEntitiesIndexed = false;
        });
        RegisterListener<Listeners.OnEntitySpawned>(IndexEntity);
        RegisterListener<Listeners.OnEntityDeleted>(entity =>
        {
            var handle = entity.EntityHandle.Raw;
            _weapons.Remove(handle);
            _bombTargets.Remove(handle);
        });
        if (hotReload)
            EnsureEntityIndex();
        RegisterEventHandler<EventPlayerConnectFull>((ev, _) =>
        {
            var player = ev.Userid;
            if (player is { IsValid: true })
                _sessions[player.Slot] = new object();
            Queries.Prefetch([Helpers.GetSteamId(player)]);
            if (player is { IsValid: true })
            {
                var slot = player.Slot;
                var session = GetSession(player);
                AddTimer(1.1f, () =>
                {
                    if (ResolveSession(slot, session) is { } current)
                        _hud?.Join(current);
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
            _hud?.RefreshQueue();
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            var player = ev.Userid;
            if (player is { IsValid: true })
            {
                var slot = player.Slot;
                var session = GetSession(player);
                var generation = _mapGeneration;
                Server.NextFrame(() =>
                {
                    if (generation == _mapGeneration && ResolveSession(slot, session) is { } current)
                        _hud?.Join(current);
                });
            }
            _hud?.RefreshQueue();
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDisconnect>((ev, info) =>
        {
            if (ev.Userid is { } player)
            {
                _ = Queries.ForgetPlayer(player.SteamID);
                _sessions.Remove(player.Slot);
                _allocatedPlayerItems.Remove(player);
            }
            _hud?.Disconnect(ev.Userid);
            return HookResult.Continue;
        }, HookMode.Pre);
        RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            _hud?.Close(ev.Userid);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundPrestart>((_, _) =>
        {
            _roundGeneration++;
            IsAllocatingForRound = false;
            foreach (var allocations in _allocatedPlayerItems.Values)
                allocations.Clear();
            PrefetchPlayers();
            _hud?.RoundBoundary();
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            _roundGeneration++;
            IsAllocatingForRound = false;
            _hud?.BetweenRounds();
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundFreezeEnd>((_, _) =>
        {
            _hud?.RoundBoundary();
            return HookResult.Continue;
        });

        AddCommand("css_guns", "Открыть снаряжение ретейка", (player, command) =>
        {
            if (player is { IsValid: true } && command.ArgCount == 1 && _hud?.OpenGuns(player) != true)
                _ = _advancedGunMenu.OpenMenuForPlayerAsync(player);
        });
        AddCommand("css_guns_legacy", "Открыть прежнее меню снаряжения ретейка", (player, command) =>
        {
            if (player is { IsValid: true } && command.ArgCount == 1)
            {
                _hud?.Close(player);
                _ = _advancedGunMenu.OpenMenuForPlayerAsync(player);
            }
        });
        AddCommand("css_retake", "Как играть в ретейк", (player, _) =>
        {
            if (player is { IsValid: true })
                _hud?.Help(player);
        });
        AddCommand("css_retake_close", "Закрыть интерфейс ретейка", (player, _) => _hud?.Close(player));
        AddCommand("css_retake_admin", "Управление ретейком", (player, _) =>
        {
            if (player is { IsValid: true })
                _hud?.OpenAdmin(player);
        });

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            ResetState();
            _advancedGunMenu.Cleanup();
            EnsureEntityIndex();
            PrefetchPlayers();
            Log.Debug($"Setting map name {mapName}");
            RoundTypeManager.Instance.SetMap(mapName);
        });

        var useCustomGameData =
            Configs.GetConfigData().EnableCanAcquireHook || Configs.GetConfigData().CapabilityWeaponPaints;

        if (useCustomGameData)
        {
            _ = Task.Run(async () =>
            {
                var downloadedNewGameData = await Helpers.DownloadMissingFiles();
                if (!downloadedNewGameData)
                {
                    return;
                }

                Server.NextFrame(() =>
                {
                    if (!_active)
                        return;
                    UpdateGameData(reload: true);
                });
            });
        }

        AddTimer(0.1f, () => { GetRetakesPluginEventSender().RetakesPluginEventHandlers += RetakesEventHandler; });

        UpdateGameData();

        if (hotReload)
        {
            HandleHotReload();
        }
    }

    private void PublishToastProvider(Func<CCSPlayerController, bool> hasToast)
    {
        const string toastKey = "retakes_allocator:round_toast";
        // CSS# retains the first supplier. Replace its delegate when the allocator reloads.
        if (AppContext.GetData(toastKey) is not StrongBox<Func<CCSPlayerController, bool>?> slot)
        {
            slot = new();
            AppContext.SetData(toastKey, slot);
            Capabilities.RegisterPluginCapability(new PluginCapability<Func<CCSPlayerController, bool>>(toastKey), () => slot.Value!);
        }
        _toastProvider = slot;
        slot.Value = hasToast;
    }

    private void ResetState(bool loadConfig = true)
    {
        if (loadConfig)
        {
            Configs.Load(ModuleDirectory, true);
            ConfigureDatabase();
            if (_active)
            {
                UpdateQueueTimer();
                UpdateGameData();
            }
        }

        Translator.Initialize(Localizer);

        RoundTypeManager.Instance.SetNextRoundTypeOverride(null);
        RoundTypeManager.Instance.SetCurrentRoundType(null);
        RoundTypeManager.Instance.Initialize();

        _roundGeneration++;
        IsAllocatingForRound = false;
        _allocatedPlayerItems.Clear();
        CloseCenterAnnouncement();
        _bombsiteAnnounceOneTime = false;
    }

    private void HandleHotReload()
    {
        Server.ExecuteCommand($"map {Server.MapName}");
    }

    public override void Unload(bool hotReload)
    {
        _active = false;
        if (_toastProvider is not null)
            _toastProvider.Value = null;
        _sessions.Clear();
        Log.Debug("Unloaded");
        _queueTimer?.Kill();
        _queueTimer = null;
        _hud?.Unload();
        _hud = null;
        _advancedGunMenu.Cleanup();
        ResetState(loadConfig: false);
        Queries.Disconnect();

        if (RetakesPluginEventSender is { } sender)
            sender.RetakesPluginEventHandlers -= RetakesEventHandler;

        UnhookCanAcquire();
        CustomFunctions = null;
    }

    private void UnhookCanAcquire()
    {
        if (!_canAcquireHooked)
            return;
        CustomFunctions!.CCSPlayer_ItemServices_CanAcquireFunc!.Unhook(OnWeaponCanAcquire, HookMode.Pre);
        _canAcquireHooked = false;
    }

    private void UpdateGameData(bool reload = false)
    {
        var config = Configs.GetConfigData();
        if (reload || !config.EnableCanAcquireHook)
            UnhookCanAcquire();
        if (!config.EnableCanAcquireHook && !config.CapabilityWeaponPaints)
        {
            CustomFunctions = null;
            return;
        }

        if (reload || CustomFunctions is null)
        {
            CustomFunctions = new CustomGameData();
            _weaponDataSignatureFailed = false;
        }

        if (config.EnableCanAcquireHook && !_canAcquireHooked &&
            CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc is { } canAcquire)
        {
            canAcquire.Hook(OnWeaponCanAcquire, HookMode.Pre);
            _canAcquireHooked = true;
        }
    }

    private IRetakesPluginEventSender GetRetakesPluginEventSender()
    {
        if (RetakesPluginEventSender is not null)
        {
            return RetakesPluginEventSender;
        }

        var sender = new PluginCapability<IRetakesPluginEventSender>("retakes_plugin:event_sender").Get();
        if (sender is null)
        {
            throw new Exception("Couldn't load retakes plugin event sender capability");
        }

        RetakesPluginEventSender = sender;
        return sender;
    }

    private void RetakesEventHandler(object? _, IRetakesPluginEvent @event)
    {
        Log.Trace("Got retakes event");
        Action? handler = @event switch
        {
            AllocateEvent => HandleAllocateEvent,
            AnnounceBombsiteEvent announcement => () => _hud?.RoundStarted(announcement.Bombsite),
            _ => null
        };
        handler?.Invoke();
    }

    #endregion

    #region Commands

    [ConsoleCommand("css_nextround", "Открыть голосование за тип следующего раунда.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnNextRoundCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}This command can only be executed by a valid player.");
            return;
        }

        if (!Configs.GetConfigData().EnableNextRoundTypeVoting)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Next round voting is disabled.");
            return;
        }

        if (_hud?.OpenVote(player!) != true)
            _allocatorMenuManager.OpenMenuForPlayer(player!, MenuType.NextRoundVote);
    }

    [ConsoleCommand("css_gun")]
    [CommandHelper(usage: "<оружие> [T|CT]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Configs.GetConfigData().GunCommandsEnabled)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Gun command is currently disabled by server config.");
            return;
        }
        HandleWeaponCommand(player, commandInfo);
    }

    private void HandleWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        var currentTeam = player!.Team;

        ReplyWhenSaved(player, (int)commandInfo.CallingContext, Message(OnWeaponCommandHelper.HandleAsync(
            Helpers.CommandInfoToArgList(commandInfo), playerId, RoundTypeManager.Instance.GetCurrentRoundType(),
            currentTeam, false)));

        // Preferences now only affect the next allocation; no weapon swap during the current round
    }

    [ConsoleCommand("css_awp", "Включить или отключить выбор AWP.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnAwpCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand("Cannot save preferences with invalid Steam ID.");
            return;
        }

        var currentTeam = player!.Team;

        var awpMode = Configs.GetConfigData().GetAwpMode();
        if (awpMode == AccessMode.Disabled)
        {
            var message = Translator.Instance["weapon_preference.awp_disabled"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        if (awpMode == AccessMode.VipOnly && !Helpers.HasAwpPermission(player))
        {
            var message = Translator.Instance["weapon_preference.only_vip_can_use"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        ReplyWhenSaved(player, (int)commandInfo.CallingContext, Message(OnWeaponCommandHelper.HandleAsync(
            [CsItem.AWP.ToString()], playerId, RoundTypeManager.Instance.GetCurrentRoundType(),
            currentTeam, false, togglePreferred: true, removeAnyPreferred: true)));
    }

    [ConsoleCommand("css_ssg", "Включить или отключить выбор SSG 08.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnSsgCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var ssgMode = Configs.GetConfigData().GetSsgMode();
        if (ssgMode == AccessMode.Disabled)
        {
            var message = Translator.Instance["weapon_preference.ssg_disabled"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        if (ssgMode == AccessMode.VipOnly && !Helpers.HasSsgPermission(player!))
        {
            var message = Translator.Instance["weapon_preference.only_vip_can_use"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand("Cannot save preferences with invalid Steam ID.");
            return;
        }

        var currentTeam = player!.Team;

        ReplyWhenSaved(player, (int)commandInfo.CallingContext, Message(OnWeaponCommandHelper.HandleAsync(
            [CsItem.Scout.ToString()], playerId, RoundTypeManager.Instance.GetCurrentRoundType(),
            currentTeam, false, togglePreferred: true, removeAnyPreferred: false)));
    }

    [ConsoleCommand("css_zeus", "Включить или отключить получение Zeus.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnZeusCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        if (!Configs.GetConfigData().IsZeusEnabled())
        {
            var message = Translator.Instance["guns_menu.zeus_disabled_message"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand("Cannot save preferences with invalid Steam ID.");
            return;
        }

        ReplyWhenSaved(player!, (int)commandInfo.CallingContext, ToggleZeus());

        async Task<string> ToggleZeus()
        {
            var enabled = await Queries.ToggleZeusAsync(playerId);
            return Translator.Instance[enabled ? "guns_menu.zeus_enabled_message" : "guns_menu.zeus_disabled_message"];
        }
    }
    [ConsoleCommand("css_removegun")]
    [CommandHelper(minArgs: 1, usage: "<оружие> [T|CT]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnRemoveWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Configs.GetConfigData().GunCommandsEnabled)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Gun command is currently disabled by server config.");
            return;
        }
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        var currentTeam = player!.Team;

        ReplyWhenSaved(player, (int)commandInfo.CallingContext, Message(OnWeaponCommandHelper.HandleAsync(
            Helpers.CommandInfoToArgList(commandInfo), playerId, RoundTypeManager.Instance.GetCurrentRoundType(),
            currentTeam, true)), split: false);
    }

    [ConsoleCommand("css_setnextround", "Выбрать тип следующего раунда.")]
    [CommandHelper(minArgs: 1, usage: "<P/H/F>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnSetNextRoundCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        var roundTypeInput = commandInfo.GetArg(1).ToLower();
        var roundType = RoundTypeHelpers.ParseRoundType(roundTypeInput);
        if (roundType is null)
        {
            var message = Translator.Instance["announcement.next_roundtype_set_invalid", roundTypeInput];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
        }
        else
        {
            RoundTypeManager.Instance.SetNextRoundTypeOverride(roundType);
            var roundTypeName = RoundTypeHelpers.TranslateRoundTypeName(roundType.Value);
            var message = Translator.Instance["announcement.next_roundtype_set", roundTypeName];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
        }
    }

    [ConsoleCommand("css_reload_allocator_config", "Перезагрузить конфигурацию аллокатора.")]
    [RequiresPermissions("@css/root")]
    public void OnReloadAllocatorConfigCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        commandInfo.ReplyToCommand($"{MessagePrefix}Reloading config for version {ModuleVersion}");
        Configs.Load(ModuleDirectory);
        ConfigureDatabase(retryMigration: true);
        UpdateGameData();
        CloseCenterAnnouncement();
        _hud?.Reset();
        _advancedGunMenu.Cleanup();
        UpdateQueueTimer();
        PrefetchPlayers();
        RoundTypeManager.Instance.Initialize();
    }

    [ConsoleCommand("css_print_config", "Вывести всю конфигурацию или отдельный раздел.")]
    [CommandHelper(usage: "<раздел>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnPrintConfigCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        var configName = commandInfo.ArgCount > 1 ? commandInfo.GetArg(1) : null;
        var response = Configs.StringifyConfig(configName);
        if (response is null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid config name.");
            return;
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{response}");
        Log.Info(response);
    }

    #endregion

    #region Events

    public HookResult OnWeaponCanAcquire(DynamicHook hook)
    {
        var acquireMethod = hook.GetParam<AcquireMethod>(2);
        if (acquireMethod == AcquireMethod.PickUp)
        {
            return HookResult.Continue;
        }

        var isWarmup = Helpers.IsWarmup();

        if (isWarmup)
        {
            return HookResult.Continue;
        }

        // Log.Trace($"OnWeaponCanAcquire enter {IsAllocatingForRound}");
        if (IsAllocatingForRound)
        {
            Log.Debug("Skipping OnWeaponCanAcquire because we're allocating for round");
            return HookResult.Continue;
        }

        HookResult RetStop()
        {
            // Log.Debug($"Exiting OnWeaponCanAcquire {acquireMethod}");
            hook.SetReturn(
                acquireMethod != AcquireMethod.PickUp
                    ? AcquireResult.AlreadyOwned
                    : AcquireResult.InvalidItem
            );

            return HookResult.Stop;
        }

        if (CustomFunctions is null)
        {
            return RetStop();
        }

        if (_weaponDataSignatureFailed)
        {
            return HookResult.Continue;
        }

        CCSWeaponBaseVData? weaponData = null;
        try
        {
            weaponData = CustomFunctions.GetCSWeaponDataFromKeyFunc?.Invoke(-1,
                hook.GetParam<CEconItemView>(1).ItemDefinitionIndex.ToString());
        }
        catch (NativeException ex)
        {
            _weaponDataSignatureFailed = true;
            CustomFunctions.GetCSWeaponDataFromKeyFunc = null;
            Log.Error(
                $"GetCSWeaponDataFromKey invocation failed. This usually means your RetakesAllocator_gamedata.json signatures are outdated. Error: {ex.Message}");
            return HookResult.Continue;
        }

        var player = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value.Controller.Value?.As<CCSPlayerController>();
        if (player is null || !player.IsValid || !player.PawnIsAlive)
        {
            Log.Debug($"Invalid player controller {player} {player?.IsValid} {player?.PawnIsAlive}");
            return HookResult.Continue;
        }

        if (weaponData == null)
        {
            Log.Warn($"Invalid weapon data {hook.GetParam<CEconItemView>(1).ItemDefinitionIndex}");
            return HookResult.Continue;
        }

        var team = player.Team;
        var item = Utils.ToEnum<CsItem>(weaponData.Name);

        if (item is CsItem.KnifeT or CsItem.KnifeCT)
        {
            return HookResult.Continue;
        }

        if (item is CsItem.Taser)
            return CanBuyZeus(Helpers.GetSteamId(player)) ? HookResult.Continue : RetStop();

        if (!WeaponHelpers.IsUsableWeapon(item))
        {
            return RetStop();
        }

        var isPreferred = WeaponHelpers.IsPreferred(team, item);
        var purchasedAllocationType = RoundTypeManager.Instance.GetCurrentRoundType() is not null
            ? WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                RoundTypeManager.Instance.GetCurrentRoundType(), team, item
            )
            : null;
        var isValidAllocation = WeaponHelpers.IsAllocationTypeValidForRound(purchasedAllocationType,
            RoundTypeManager.Instance.GetCurrentRoundType());

        // Log.Debug($"item {item} team {team} player {playerId}");
        // Log.Debug($"weapon alloc {purchasedAllocationType} valid? {isValidAllocation}");
        // Log.Debug($"Preferred? {isPreferred}");

        if (
            Helpers.IsWeaponAllocationAllowed() &&
            !isPreferred &&
            isValidAllocation &&
            purchasedAllocationType is not null
        )
        {
            return HookResult.Continue;
        }

        return RetStop();
    }

    private static bool CanBuyZeus(ulong steamId) =>
        steamId != 0 && Configs.GetConfigData().IsZeusEnabled() && Queries.IsZeusEnabled(steamId);

    [GameEventHandler]
    public HookResult OnPostItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;
        var pawnHandle = player?.PlayerPawn;

        if (Helpers.IsWarmup())
        {
            return HookResult.Continue;
        }

        if (!Helpers.PlayerIsValid(player) || pawnHandle is null || !pawnHandle.IsValid)
        {
            return HookResult.Continue;
        }

        var controller = player!;
        var item = Utils.ToEnum<CsItem>(@event.Weapon);
        var team = controller.Team;
        var playerId = Helpers.GetSteamId(controller);

        // The native hook allows opted-in Zeus purchases. They are not loadout-slot changes.
        if (item is CsItem.Taser && CanBuyZeus(playerId))
            return HookResult.Continue;

        var isPreferred = WeaponHelpers.IsPreferred(team, item);
        var roundType = RoundTypeManager.Instance.GetCurrentRoundType();
        var debug = Log.IsEnabled(LogLevel.Debug);

        var purchasedAllocationType = roundType is not null
            ? WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                roundType, team, item
            )
            : null;

        var isValidAllocation = WeaponHelpers.IsAllocationTypeValidForRound(purchasedAllocationType,
            roundType) && WeaponHelpers.IsUsableWeapon(item);

        if (debug)
            Log.Debug($"item {item} team {team} player {playerId}");
        if (debug)
            Log.Debug($"weapon alloc {purchasedAllocationType} valid? {isValidAllocation}");
        if (debug)
            Log.Debug($"Preferred? {isPreferred}");

        if (
            Helpers.IsWeaponAllocationAllowed() &&
            // Preferred weapons are treated like un-buy-able weapons, but at the end we'll set the user preference
            !isPreferred &&
            isValidAllocation &&
            // redundant, just for null checker
            purchasedAllocationType is not null
        )
        {
            Queries.SetWeaponPreferenceForUser(
                playerId,
                team,
                purchasedAllocationType.Value,
                item
            );
            var slotType = WeaponHelpers.GetSlotTypeForItem(item);
            if (slotType is not null)
            {
                SetPlayerRoundAllocation(controller, slotType.Value, item);
            }
            else
            {
                if (debug)
                    Log.Debug($"WARN: No slot for {item}");
            }
        }
        else
        {
            var removedAnyWeapons = Helpers.RemoveWeapons(controller,
                i =>
                {
                    if (!WeaponHelpers.IsWeapon(i))
                    {
                        return i == item;
                    }

                    if (roundType is null)
                    {
                        return true;
                    }

                    var at = WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                        roundType, team, i);
                    if (Log.IsEnabled(LogLevel.Trace))
                        Log.Trace($"at: {at}");
                    return at is null || at == purchasedAllocationType;
                });
            if (debug)
                Log.Debug($"Removed {item}? {removedAnyWeapons}");

            var replacementSlot = roundType == RoundType.Pistol
                ? ItemSlotType.Secondary
                : ItemSlotType.Primary;

            var replacedWeapon = false;
            var slotToSelect = WeaponHelpers.GetSlotNameForSlotType(replacementSlot);
            if (removedAnyWeapons && roundType is not null &&
                WeaponHelpers.IsWeapon(item))
            {
                var replacementAllocationType =
                    WeaponHelpers.GetReplacementWeaponAllocationTypeForWeapon(roundType);
                if (debug)
                    Log.Debug($"Replacement allocation type {replacementAllocationType}");
                if (replacementAllocationType is not null)
                {
                    var replacementItem = GetPlayerRoundAllocation(controller, replacementSlot);
                    if (debug)
                        Log.Debug($"Replacement item {replacementItem} for slot {replacementSlot}");
                    if (replacementItem is not null)
                    {
                        replacedWeapon = true;
                        AllocateItemsForPlayer(controller, new List<CsItem>
                        {
                            replacementItem.Value
                        }, slotToSelect);
                    }
                }
            }

            if (!replacedWeapon)
            {
                var generation = _roundGeneration;
                var state = new RoundPlayer(controller, controller.Slot, playerId, team, pawnHandle.Raw,
                    GetSession(controller), false, false, false);
                AddTimer(0.1f, () =>
                {
                    if (generation == _roundGeneration && IsCurrent(state) && controller.UserId is { } userId)
                        NativeAPI.IssueClientCommand(userId, slotToSelect);
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }

        ScheduleWeaponCleanup(controller);

        if (isPreferred)
        {
            var itemName = Enum.GetName(item);
            if (itemName is not null)
            {
                ReplyWhenSaved(controller, (int)CommandCallingContext.Chat, Message(OnWeaponCommandHelper.HandleAsync(
                    [itemName], playerId, roundType, team, false)));
            }
        }

        return HookResult.Continue;
    }

    private void HandleAllocateEvent()
    {
        IsAllocatingForRound = true;
        Log.Debug($"Handling allocate event");
        Server.ExecuteCommand("mp_max_armor 0");

        var menu = _allocatorMenuManager.GetMenu<VoteMenu>(MenuType.NextRoundVote);
        menu.GatherAndHandleVotes();

        var allPlayers = Utilities.GetPlayers();
        var participants = allPlayers
            .Where(player => player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist && player.PawnIsAlive)
            .Select(player => new RoundPlayer(player, player.Slot,
                Helpers.GetSteamId(player), player.Team, player.PlayerPawn.Raw, GetSession(player),
                Helpers.HasAwpPermission(player), Helpers.HasSsgPermission(player),
                Helpers.HasEnemyStuffPermission(player))).ToArray();
        Queries.Prefetch(participants.Where(player => !Queries.HasSnapshot(player.SteamId))
            .Select(player => player.SteamId));

        var roundTypes = RoundTypeManager.Instance;
        var currentRoundType = roundTypes.GetNextRoundType();
        roundTypes.SetCurrentRoundType(currentRoundType);
        roundTypes.SetNextRoundTypeOverride(null);

        var generation = _roundGeneration;
        AddTimer(0.1f, () => GiveRoundItems(participants, currentRoundType, generation), TimerFlags.STOP_ON_MAPCHANGE);

        switch(currentRoundType)
        {
            case RoundType.Pistol:
            {
                Server.ExecuteCommand("execifexists cs2-retakes/Pistol.cfg");
                break;
            }
            case RoundType.HalfBuy:
            {
                Server.ExecuteCommand("execifexists cs2-retakes/SmallBuy.cfg");
                break;
            }
            case RoundType.FullBuy:
            {
                Server.ExecuteCommand("execifexists cs2-retakes/FullBuy.cfg");
                break;
            }
        }

        if (Configs.GetConfigData().EnableRoundTypeAnnouncement)
        {
            var roundType = RoundTypeManager.Instance.GetCurrentRoundType()!.Value;
            var roundTypeName = RoundTypeHelpers.TranslateRoundTypeName(roundType);
            var message = Translator.Instance["announcement.roundtype", roundTypeName];
            Server.PrintToChatAll($"{MessagePrefix}{message}");
            if (Configs.GetConfigData().EnableRoundTypeAnnouncementCenter)
            {
                foreach (var player in allPlayers)
                {
                    player.PrintToCenter(
                        $"{MessagePrefix}{Translator.Instance["center.announcement.roundtype", roundTypeName]}");
                }
            }
        }

        AddTimer(.5f, () =>
        {
            Log.Debug("Turning off round allocation");
            if (generation == _roundGeneration)
                IsAllocatingForRound = false;
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void GiveRoundItems(RoundPlayer[] participants, RoundType roundType, int generation)
    {
        if (!_active || generation != _roundGeneration)
            return;

        // A disconnect, team change or respawn must not consume a team-limited item.
        var recipients = participants.Where(IsCurrent).ToArray();
        var snapshots = new Dictionary<ulong, PreferenceSnapshot>();
        foreach (var participant in recipients)
        {
            if (participant.SteamId != 0 && Queries.ReadSnapshot(participant.SteamId) is { } settings)
                snapshots[participant.SteamId] = settings;
        }

        var slots = new List<(RoundPlayer Player, string Slot)>(participants.Length);
        OnRoundPostStartHelper.Handle(recipients, player => player?.SteamId ?? 0, player => player.Team,
            player =>
            {
                if (!IsCurrent(player))
                    return;
                try { GiveDefuseKit(player.Player); }
                catch (Exception error) { Logger.LogError(error, "Could not give a Retakes defuse kit"); }
            },
            (player, items, slot) =>
            {
                if (!IsCurrent(player))
                    return;
                try
                {
                    GiveItems(player.Player, items);
                    if (slot is not null)
                        slots.Add((player, slot));
                }
                catch (Exception error)
                {
                    Logger.LogError(error, "Could not give Retakes round equipment");
                }
            },
            player => player.Awp, player => player.Ssg, player => player.EnemyStuff, roundType, snapshots);
        if (slots.Count == 0)
            return;
        AddTimer(0.1f, () =>
        {
            if (generation != _roundGeneration)
                return;
            foreach (var (player, slot) in slots)
                if (IsCurrent(player) && player.Player.UserId is { } userId)
                    NativeAPI.IssueClientCommand(userId, slot);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private CounterStrikeSharp.API.Modules.Timers.Timer? _queueTimer;

    private void UpdateQueueTimer()
    {
        _queueTimer?.Kill();
        _queueTimer = Configs.GetConfigData().EnablePanoramaHud && _hud is not null
            ? AddTimer(2, _hud.RefreshQueue, TimerFlags.REPEAT)
            : null;
        _hud?.RefreshQueue();
    }

    private void EnsureEntityIndex()
    {
        if (_mapEntitiesIndexed || !EntitySystem.IsInitialized)
            return;
        foreach (var entity in Utilities.GetAllEntities())
            IndexEntity(entity);
        _mapEntitiesIndexed = true;
    }

    private void IndexEntity(CEntityInstance entity)
    {
        if (!entity.IsValid || entity.DesignerName is not { } name)
            return;
        var handle = entity.EntityHandle.Raw;
        if (name.StartsWith("weapon", StringComparison.Ordinal) && name != "weapon_c4")
            _weapons.Add(handle);
        if (name is "info_bomb_target_hint_A" or "info_bomb_target_hint_B" && !_bombTargets.Contains(handle))
            _bombTargets.Add(handle);
    }

    private void ScheduleWeaponCleanup(CCSPlayerController player)
    {
        if (player.PlayerPawn.Value?.AbsOrigin is not { } origin)
            return;
        var x = origin.X;
        var y = origin.Y;
        List<uint>? nearby = null;
        foreach (var handle in _weapons)
        {
            if (EntitySystem.GetEntityByHandle(handle) is not { } pointer || pointer == IntPtr.Zero)
                continue;
            var weapon = new CBasePlayerWeapon(pointer);
            if (!weapon.IsValid || weapon.AbsOrigin is not { } position)
                continue;
            var dx = x - position.X;
            var dy = y - position.Y;
            if ((double)dx * dx + (double)dy * dy < 900)
                (nearby ??= []).Add(handle);
        }
        if (nearby is null)
            return;

        AddTimer(.5f, () =>
        {
            foreach (var handle in nearby)
            {
                if (EntitySystem.GetEntityByHandle(handle) is not { } pointer || pointer == IntPtr.Zero)
                    continue;
                var weapon = new CBasePlayerWeapon(pointer);
                if (weapon.IsValid && !weapon.OwnerEntity.IsValid)
                    weapon.Remove();
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void CloseCenterAnnouncement()
    {
        _centerGeneration++;
        _bombsite = "";
        _centerPending = false;
        _announceBombsite = false;
        if (_centerTickRegistered)
        {
            RemoveListener<Listeners.OnTick>(OnTick);
            _centerTickRegistered = false;
        }
    }

    private (string Site, int T, int CT, string Culture) _centerTextKey;
    private string? _centerT;
    private string? _centerCt;
    private readonly List<(CCSPlayerController Player, CsTeam Team)> _centerRecipients = [];

    public void OnTick()
    {
        if (!_announceBombsite)
            return;

        int t = 0, ct = 0;
        var ctOnly = Configs.GetConfigData().BombSiteAnnouncementCenterToCTOnly;
        try
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.PawnIsAlive || player.IsHLTV)
                    continue;
                var team = player.Team;
                if (team == CsTeam.Terrorist)
                    t++;
                else if (team == CsTeam.CounterTerrorist)
                    ct++;
                else
                    continue;
                if ((!ctOnly || team == CsTeam.CounterTerrorist) && !player.IsBot && _hud?.HasRoundToast(player) != true)
                    _centerRecipients.Add((player, team));
            }

            var key = (_bombsite, t, ct, System.Globalization.CultureInfo.CurrentUICulture.Name);
            if (_centerTextKey != key)
            {
                _centerTextKey = key;
                var image = _bombsite is "A" or "B" ? Translator.Instance[$"BombSite.{_bombsite}"] : "";
                _centerT = string.Format(Localizer["T.Message"], _bombsite, image, t, ct);
                _centerCt = string.Format(Localizer["CT.Message"], _bombsite, image, t, ct);
            }
            foreach (var (player, team) in _centerRecipients)
                player.PrintToCenterHtml(team == CsTeam.Terrorist ? _centerT! : _centerCt!);
        }
        finally
        {
            _centerRecipients.Clear();
        }
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnEventBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;

        if (Configs.GetConfigData().DisableDefaultBombPlantedCenterMessage)
        {
            info.DontBroadcast = true;
        }

        if (Configs.GetConfigData().ForceCloseBombSiteAnnouncementCenterOnPlant)
        {
            CloseCenterAnnouncement();
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnEventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;
        CloseCenterAnnouncement();
        _bombsiteAnnounceOneTime = false;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnEventRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;
        CloseCenterAnnouncement();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnEventEnterBombzone(EventEnterBombzone @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null || Helpers.IsWarmup() || _bombsiteAnnounceOneTime) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.TeamNum != (byte) CsTeam.Terrorist) return HookResult.Continue;

        var playerPawn = player.PlayerPawn;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (playerPawn == null || !playerPawn.IsValid) return HookResult.Continue;

        var playerPosition = playerPawn.Value?.AbsOrigin;
        if (playerPosition is null || _centerPending)
            return HookResult.Continue;

        var config = Configs.GetConfigData();
        if (!config.EnableBombSiteAnnouncementChat && !config.EnableBombSiteAnnouncementCenter)
            return HookResult.Continue;

        foreach (var handle in _bombTargets)
        {
            if (EntitySystem.GetEntityByHandle(handle) is not { } pointer || pointer == IntPtr.Zero)
                continue;
            var entity = new CBombTarget(pointer);
            if (entity.AbsOrigin is not { } position || (playerPosition - position).Length() > 400f)
                continue;
            _bombsite = entity.DesignerName == "info_bomb_target_hint_A" ? "A" : "B";
            _bombsiteAnnounceOneTime = true;
            if (config.EnableBombSiteAnnouncementChat)
                for (var line = 1; line <= 6; line++)
                    Server.PrintToChatAll(Localizer[$"chat{_bombsite}site.line{line}"]);

            if (config.EnableBombSiteAnnouncementCenter)
            {
                _centerPending = true;
                var generation = _centerGeneration;
                Server.NextFrame(() =>
                {
                    if (!_active || generation != _centerGeneration)
                        return;
                    AddTimer(config.BombSiteAnnouncementCenterDelay, () =>
                    {
                        if (generation != _centerGeneration)
                            return;
                        _centerPending = false;
                        _announceBombsite = true;
                        if (config.UseOnTickFeatures && !_centerTickRegistered)
                        {
                            RegisterListener<Listeners.OnTick>(OnTick);
                            _centerTickRegistered = true;
                        }
                        AddTimer(config.BombSiteAnnouncementCenterShowTimer, () =>
                        {
                            if (generation == _centerGeneration)
                                CloseCenterAnnouncement();
                        }, TimerFlags.STOP_ON_MAPCHANGE);
                    }, TimerFlags.STOP_ON_MAPCHANGE);
                });
            }
            break;
        }

        return HookResult.Continue;
    }


    [GameEventHandler]
    public HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuCenterCommands))
        {
            _advancedGunMenu.OnEventPlayerDisconnect(@event, info);
        }

        return HookResult.Continue;
    }

    // ReSharper disable once RedundantArgumentDefaultValue
    [GameEventHandler(HookMode.Post)]
    public HookResult OnEventPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;

        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuCenterCommands))
        {
            // CounterStrikeSharp owns !guns and /guns through css_guns. Preserve
            // the allocator's additional configured plain-chat aliases.
            var text = (@event.Text ?? "").Trim();
            if (text.Equals("!guns", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/guns", StringComparison.OrdinalIgnoreCase))
                return HookResult.Continue;

            var chatPlayer = Utilities.GetPlayerFromUserid(@event.Userid);
            if (chatPlayer is not { IsValid: true } ||
                !Configs.GetConfigData().InGameGunMenuCenterCommands.Split(',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Contains(text, StringComparer.OrdinalIgnoreCase) ||
                _hud?.OpenGuns(chatPlayer) != true)
                _advancedGunMenu.OnEventPlayerChat(@event, info);
        }

        var eventplayer = @event.Userid;
        var eventmessage = @event.Text;
        var player = Utilities.GetPlayerFromUserid(eventplayer);

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (player == null || !player.IsValid) return HookResult.Continue;

        if (string.IsNullOrWhiteSpace(eventmessage)) return HookResult.Continue;
        string trimmedMessageStart = eventmessage.TrimStart();
        string message = trimmedMessageStart.TrimEnd();

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnEventRoundAnnounceWarmup(EventRoundAnnounceWarmup @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;

        if (Configs.GetConfigData().ResetStateOnGameRestart)
        {
            ResetState();
        }

        return HookResult.Continue;
    }

    #endregion

    #region Helpers

    private void SetPlayerRoundAllocation(CCSPlayerController player, ItemSlotType slotType, CsItem item)
    {
        if (!_allocatedPlayerItems.TryGetValue(player, out var allocations))
            _allocatedPlayerItems[player] = allocations = new();

        allocations[slotType] = item;
        if (Log.IsEnabled(LogLevel.Trace))
            Log.Trace($"Round allocation for player {player.Slot} {slotType} {item}");
    }

    private CsItem? GetPlayerRoundAllocation(CCSPlayerController player, ItemSlotType? slotType)
    {
        if (slotType is null || !_allocatedPlayerItems.TryGetValue(player, out var playerItems))
        {
            return null;
        }

        if (playerItems.TryGetValue(slotType.Value, out var localReplacementItem))
        {
            return localReplacementItem;
        }

        return null;
    }

    private static readonly Dictionary<CsItem, string?> ItemNames =
        Enum.GetValues<CsItem>().Distinct().ToDictionary(item => item, EnumUtils.GetEnumMemberAttributeValue);

    private void GiveItems(CCSPlayerController player, ICollection<CsItem> items)
    {
        var paintFunction = Configs.GetConfigData().CapabilityWeaponPaints &&
            CustomFunctions?.PlayerGiveNamedItemEnabled() == true ? CustomFunctions : null;
        foreach (var item in items)
        {
            if (!ItemNames.TryGetValue(item, out var itemString) || string.IsNullOrWhiteSpace(itemString))
                continue;

            if (paintFunction is not null)
                paintFunction.PlayerGiveNamedItem(player, itemString);
            else
                player.GiveNamedItem(itemString);

            if (WeaponHelpers.GetSlotTypeForItem(item) is { } slotType)
                SetPlayerRoundAllocation(player, slotType, item);
        }
    }

    private void AllocateItemsForPlayer(CCSPlayerController player, ICollection<CsItem> items, string? slotToSelect)
    {
        var state = new RoundPlayer(player, player.Slot, Helpers.GetSteamId(player), player.Team, player.PlayerPawn.Raw,
            GetSession(player), false, false, false);
        var generation = _roundGeneration;
        AddTimer(0.1f, () =>
        {
            if (generation != _roundGeneration || !IsCurrent(state))
                return;
            GiveItems(player, items);
            if (slotToSelect is not null)
                AddTimer(0.1f, () =>
                {
                    if (generation == _roundGeneration && IsCurrent(state) && player.UserId is { } userId)
                        NativeAPI.IssueClientCommand(userId, slotToSelect);
                }, TimerFlags.STOP_ON_MAPCHANGE);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void GiveDefuseKit(CCSPlayerController player)
    {
        if (player.PlayerPawn.Value?.ItemServices is { } services)
            new CCSPlayer_ItemServices(services.Handle).HasDefuser = true;
    }

    #endregion
}
