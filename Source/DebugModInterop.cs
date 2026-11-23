using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DebugMod.UI.CommandPalette;
using UnityEngine;

namespace SpeedrunGym.Source;

/// DebugMod is a soft dependency: every method below touches DebugMod types and must only be called while it is
/// loaded. Mono JITs method bodies lazily, so the DebugMod assembly is only resolved when one of them runs.
///
/// All registrations bind to the first DebugMod generation (compile-time type identity); a DebugMod hot reload
/// orphans them - restart the game after reloading DebugMod itself. SpeedrunGym reloads are handled fine.
internal static class DebugModInterop {
    public const string PluginId = "io.github.hk-speedrunning.debugmod";

    public static bool Installed => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(PluginId);

    public static CommandPaletteItem CreateAction(string title, Action execute)
        => new CommandPaletteItem.ActionItem(title, execute);

    public static CommandPaletteItem CreateAction(Func<string> title, Action execute)
        => new CommandPaletteItem.ActionItem(title, execute);

    public static CommandPaletteItem CreateSubmenu(string title, Func<IEnumerable<CommandPaletteItem>> getChildren)
        => new CommandPaletteItem.SubmenuItem(title, getChildren);

    public static void RegisterSubmenu(Func<string> title, Func<IEnumerable<CommandPaletteItem>> getChildren)
        => DebugMod.DebugMod.CommandPaletteRegistry.RegisterSubmenu(title, getChildren);

    public static void AddTranslationSheet(string sheet)
        => DebugMod.DebugMod.AddTranslationSheet(sheet);

    public static void LogConsole(string message)
        => DebugMod.DebugMod.LogConsole(message);

    public static bool IsKeybindHeld(string name) {
        if (!Installed) return false;
        if (!DebugMod.DebugMod.settings.binds.TryGetValue(name, out DebugMod.Binding binding)) return false;

        // the held-key variant of DebugMod's internal Modifiers.Held()/Without()
        return binding.Key != KeyCode.None && Input.GetKey(binding.Key)
            && (HeldModifiers() & ~AsModifier(binding.Key)) == binding.Modifiers;
    }

    private static DebugMod.Modifier HeldModifiers() =>
        (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? DebugMod.Modifier.Shift : 0)
        | (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ? DebugMod.Modifier.Ctrl : 0)
        | (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ? DebugMod.Modifier.Alt : 0)
        | (Input.GetKey(KeyCode.LeftMeta) || Input.GetKey(KeyCode.RightMeta) ? DebugMod.Modifier.Meta : 0);

    private static DebugMod.Modifier AsModifier(KeyCode key) => key switch {
        KeyCode.LeftControl or KeyCode.RightControl => DebugMod.Modifier.Ctrl,
        KeyCode.LeftShift or KeyCode.RightShift => DebugMod.Modifier.Shift,
        KeyCode.LeftAlt or KeyCode.RightAlt => DebugMod.Modifier.Alt,
        KeyCode.LeftMeta or KeyCode.RightMeta => DebugMod.Modifier.Meta,
        _ => DebugMod.Modifier.None,
    };

    /// Registers [BindableMethod]s of `bindables`.
    public static void AddKeybinds(Type bindables) {
        foreach (MethodInfo method in bindables.GetMethods()) {
            if (method.GetCustomAttribute<DebugMod.BindableMethod>(false) is not { } attr) continue;
#if DEBUG
            // DebugMod keeps the entry of a previous hot reload generation; Add would throw on the duplicate name
            if (DebugMod.DebugMod.bindActions.ContainsKey(attr.name)) continue;
#endif
            DebugMod.DebugMod.AddToKeyBindList(bindables);
            return;
        }
    }
}
