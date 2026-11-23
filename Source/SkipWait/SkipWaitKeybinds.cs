using System;
using System.Linq;
using System.Reflection;
using DebugMod;

namespace SpeedrunGym.Source.SkipWait;

/// Registered with DebugMod's keybind list.
public static class SkipWaitKeybinds {
    public const string KeybindName = "SPEEDRUNGYM_SKIPWAIT";

    [BindableMethod(name = KeybindName, category = "CATEGORY_MISC")]
    public static void SkipWait() {
#if DEBUG
        RequestCurrentGeneration();
#else
        SkipWaitFeature.RequestSkip();
#endif
    }

#if DEBUG
    /// AutoReload keeps this (orphaned) method as the keybind's target after a hot reload, so it forwards to the
    /// newest SpeedrunGym generation.
    private static void RequestCurrentGeneration() {
        Assembly newest = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => {
                string name = a.GetName().Name!;
                return name == "SpeedrunGym" || name.StartsWith("SpeedrunGym-");
            })
            .OrderByDescending(GenerationOf)
            .First();
        if (newest == typeof(SkipWaitKeybinds).Assembly) {
            SkipWaitFeature.RequestSkip();
            return;
        }

        newest.GetType("SpeedrunGym.Source.SkipWait.SkipWaitFeature")!
            .GetMethod(nameof(SkipWaitFeature.RequestSkip), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
    }

    /// the first generation is plain "SpeedrunGym", hot reloads append their creation ticks
    private static long GenerationOf(Assembly assembly) {
        string name = assembly.GetName().Name!;
        return name == "SpeedrunGym" || !long.TryParse(name["SpeedrunGym-".Length..], out long ticks) ? 0 : ticks;
    }
#endif
}
