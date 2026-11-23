using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using TeamCherry.Cinematics;
using UnityEngine;

namespace SpeedrunGym.Source.SkipWait;

[HarmonyPatch]
internal static class SkipWaitPatches {
#pragma warning disable HARMONIZE002 // each complete [HarmonyPatch] is patched as its own original; the analyzer assumes they merge into one
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.RunDialogue))]
    [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.PrintText))]
    [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.LineEndedWait))]
    [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.Close))]
    [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.CloseAndEnd))]
    [HarmonyPatch(typeof(CinematicPlayer), nameof(CinematicPlayer.StartVideo))]
    [HarmonyPatch(typeof(CinematicPlayer), nameof(CinematicPlayer.Skip))]
    [HarmonyPatch(typeof(CinematicPlayer), nameof(CinematicPlayer.FinishInGameVideo))]
    // registers its load only on the next frame, too late for a transition the skip starts right away
    [HarmonyPatch(typeof(ScenePreloader), nameof(ScenePreloader.LoadRoutine))]
    [HarmonyPatch(typeof(UIMsgBase<PowerUpGetMsg.PowerUps>), nameof(UIMsgBase<>.DoMsg))]
    [HarmonyPatch(typeof(UIMsgBase<ToolItemSkill>), nameof(UIMsgBase<>.DoMsg))]
    [HarmonyPatch(typeof(UIMsgBase<ToolCrest>), nameof(UIMsgBase<>.DoMsg))]
    [HarmonyPatch(typeof(UIMsgBase<ToolItem>), nameof(UIMsgBase<>.DoMsg))]
    [HarmonyPatch(typeof(UIMsgBase<ToolItemType>), nameof(UIMsgBase<>.DoMsg))]
    private static void TrackRoutine(MonoBehaviour __instance, MethodBase __originalMethod, ref IEnumerator __result) {
        RoutineTracker.Track(__instance, __result, __originalMethod.Name == nameof(CinematicPlayer.StartVideo));
    }
#pragma warning restore HARMONIZE002

    // a new conversation stops all of the box's coroutines
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.ShowShared))]
    private static void DropDialogueRoutines(DialogueBox __instance) => RoutineTracker.DropOwner(__instance);
}

/// Patches that only matter while a request runs, applied when the first one starts instead of at startup.
internal static class RequestPatches {
    private static bool applied;

    public static void Ensure() {
        if (applied) return;
        applied = true;
        Harmony harmony = SpeedrunGymPlugin.Instance.Harmony;

        // AnimateUp only starts its animation in the next Update, where it would override an AnimateDown from the same frame
        harmony.Patch(Method(typeof(InvAnimateUpAndDown), nameof(InvAnimateUpAndDown.AnimateDown)),
            prefix: new(typeof(RequestPatches), nameof(DropPendingAnimateUp)));
        // the video engine only starts playing on a later frame, which a skipped video doesn't need
        harmony.Patch(typeof(EmbeddedCinematicVideoPlayer).GetProperty(nameof(EmbeddedCinematicVideoPlayer.IsPlaying))?.GetGetMethod()
                ?? throw new MissingMethodException("EmbeddedCinematicVideoPlayer", "IsPlaying.get"),
            prefix: new(typeof(RequestPatches), nameof(AssumeStartedVideo)));
        harmony.Patch(Method(typeof(HeroController), nameof(HeroController.FinishedEnteringScene)),
            postfix: new(typeof(RequestPatches), nameof(ContinueOnceEntered)));
    }

    // the game's runtime assemblies are not publicized, so private members need the NonPublic flag
    private static MethodInfo Method(Type type, string name)
        => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, name);

    private static void DropPendingAnimateUp(InvAnimateUpAndDown __instance) {
        if (SkipWaitFeature.Current is not { } request || !__instance.readyingAnimUp) return;

        __instance.readyingAnimUp = false;
        request.Log.Line($"{__instance.name}: dropped pending AnimateUp for AnimateDown");
    }

    private static bool AssumeStartedVideo(EmbeddedCinematicVideoPlayer __instance, ref bool __result) {
        if (__instance != RoutineTracker.StartingVideo) return true;
        __result = true;
        return false;
    }

    private static void ContinueOnceEntered() => SkipWaitFeature.ContinueOnceEntered();
}
