using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;

namespace SpeedrunGym.Source;

// TODO: this is ai reflection slop.
// Find a better solution.
internal static class DebugModInterop {
    private const string PluginId = "io.github.hk-speedrunning.debugmod";

    private static object? registry;
    private static BaseUnityPlugin? registryPlugin;
    private static Type? itemType,
        actionType,
        toggleType,
        submenuType;
    private static MethodInfo? registerMethod;

    public static bool Installed => TryResolve();

    public static void Register(object? item) {
        if (item == null || !TryResolve()) return;
        registerMethod!.Invoke(registry,
        [
            item,
        ]);
    }

    public static void RegisterAction(string title, Action execute) => Register(CreateAction(title, execute));

    public static void RegisterToggle(string title, Func<bool> isEnabled, Action toggle) => Register(CreateToggle(title, isEnabled, toggle));

    public static void RegisterSubmenu(string title, Func<IEnumerable<object?>> getChildren) => Register(CreateSubmenu(title, getChildren));

    public static object? CreateAction(string title, Action execute)
        => TryResolve() ? Activator.CreateInstance(actionType!, title, execute) : null;

    public static object? CreateAction(Func<string> title, Action execute)
        => TryResolve() ? Activator.CreateInstance(actionType!, title, execute) : null;

    public static object? CreateToggle(string title, Func<bool> isEnabled, Action toggle)
        => TryResolve() ? Activator.CreateInstance(toggleType!, title, isEnabled, toggle) : null;

    public static object? CreateSubmenu(string title, Func<IEnumerable<object?>> getChildren) {
        if (!TryResolve()) return null;
        // DebugMod expects Func<IEnumerable<CommandPaletteItem>>; bind an adapter casting our object items.
        var wrap = typeof(SubmenuChildren).GetMethod(nameof(SubmenuChildren.Wrap))!.MakeGenericMethod(itemType!);
        var funcType = typeof(Func<>).MakeGenericType(typeof(IEnumerable<>).MakeGenericType(itemType!));
        var getChildrenDelegate = Delegate.CreateDelegate(funcType, new SubmenuChildren(getChildren), wrap);
        return Activator.CreateInstance(submenuType!, title, getChildrenDelegate, true);
    }

    private static bool TryResolve() {
        if (!Chainloader.PluginInfos.TryGetValue(PluginId, out var info) || info.Instance is not { } plugin)
            return false;
        // Re-resolve on hot reload: a new DebugMod generation brings its own types and registry.
        if (registry != null && ReferenceEquals(registryPlugin, plugin))
            return true;

        var registryField = plugin.GetType().Assembly.GetType("DebugMod.DebugMod")
            ?.GetField("CommandPaletteRegistry", BindingFlags.Public | BindingFlags.Static);
        if (registryField?.GetValue(null) is not { } resolvedRegistry)
            return false;

        itemType = registryField.FieldType.GetMethod("Register")?.GetParameters()[0].ParameterType;
        actionType = itemType?.GetNestedType("ActionItem");
        toggleType = itemType?.GetNestedType("ToggleItem");
        submenuType = itemType?.GetNestedType("SubmenuItem");
        registerMethod = registryField.FieldType.GetMethod("Register",
        [
            itemType!,
        ]);
        if (itemType == null || actionType == null || toggleType == null || submenuType == null || registerMethod == null)
            return false;

        registry = resolvedRegistry;
        registryPlugin = plugin;
        return true;
    }

    private sealed class SubmenuChildren(Func<IEnumerable<object?>> getChildren) {
        // Items are created once and reused: DebugMod's palette history dedupes by item reference, so fresh
        // instances per open would accumulate duplicate history entries.
        private object?[]? items;

        public IEnumerable<TItem> Wrap<TItem>() {
            items ??= getChildren().Where(item => item != null).ToArray();
            return items.Cast<TItem>();
        }
    }
}
