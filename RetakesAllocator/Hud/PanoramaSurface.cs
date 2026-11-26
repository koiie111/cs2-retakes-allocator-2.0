using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace RetakesAllocator.Hud;

internal sealed class PanoramaSurface(string layoutName)
{
    private CCSCustomHudLayout? _entity;
    internal bool Ready => _entity is { IsValid: true };
    internal bool Owns(CCSCustomHudLayout entity) => Ready && _entity!.Handle == entity.Handle;

    internal bool Ensure()
    {
        if (Ready)
            return true;

        var handle = VirtualFunctions.UTIL_CreateEntityByName("custom_hud_layout", -1);
        if (handle == IntPtr.Zero)
            return false;

        var entity = new CCSCustomHudLayout(handle);
        using var keyValues = new CEntityKeyValues();
        keyValues.SetString("layout", $"panorama/layout/custom_game/{layoutName}.vxml_c");
        keyValues.SetBool("observable", false);
        keyValues.SetVector("origin", 0, 0, 0);
        entity.DispatchSpawn(keyValues);
        _entity = entity;
        return entity.IsValid;
    }

    internal View? For(CCSPlayerController player) =>
        Ready && player.Slot < _entity!.PlayerLayoutStates.Count ? new View(_entity, player) : null;

    internal void Destroy()
    {
        if (Ready)
            _entity!.Remove();

        _entity = null;
    }

    internal sealed class View(CCSCustomHudLayout entity, CCSPlayerController player)
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
        private readonly HashSet<(string Panel, string Class)> _classes = [];
        private readonly Dictionary<string, string> _variants = new(StringComparer.Ordinal);

        internal bool Valid => entity.IsValid && player.IsValid;
        internal bool Captures => Valid && entity.IsInputCaptureEnabled(player);

        internal bool Capture(bool enabled)
        {
            if (!Valid)
                return false;

            if (Captures != enabled)
                entity.SetInputCaptureEnabled(player, enabled);

            return Captures == enabled;
        }

        internal void Text(string id, string value)
        {
            if (_text.TryGetValue(id, out var previous) && previous == value)
                return;

            entity.SetDialogVariableStringForPlayer(player, id, "text", value);
            _text[id] = value;
        }

        internal void Class(string id, string name, bool enabled)
        {
            if (enabled ? _classes.Add((id, name)) : _classes.Remove((id, name)))
                entity.SetHasClassForPlayer(player, id, name, enabled);
        }

        internal void Variant(string id, string value)
        {
            if (_variants.TryGetValue(id, out var previous) && previous == value)
                return;

            if (!string.IsNullOrEmpty(previous))
                Class(id, previous, false);

            if (value.Length > 0)
                Class(id, value, true);

            _variants[id] = value;
        }
    }
}

