using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using HutongGames.PlayMaker;
using SpeedrunGym.Source.Moves;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpeedrunGym.Source;

[BepInAutoPlugin("io.github.jakobhellermann.speedrungym")]
[BepInDependency("org.silksong-modding.assethelper")]
public partial class SpeedrunGymPlugin : BaseUnityPlugin {
    private static SpeedrunGymPlugin instance = null!;
    private Harmony harmony = null!;
    private PreHeroUpdate preHeroUpdate = null!;
    private readonly TrainingDummy trainingDummy = new();
    private WorldToastManager worldToasts = null!;

    private void Awake() {
        instance = this;
        Log.Init(Logger);
        Log.Info($"Plugin {Name} ({Id}) has loaded!");

        ForceCrawPogo.BindConfig(Config);
        PogoEndlagDetector.BindConfig(Config);
        JumpTimingDetector.BindConfig(Config);
        WallJumpDetector.BindConfig(Config);
        SilkWasteDetector.BindConfig(Config);
        UnhittableTint.BindConfig(Config);
        trainingDummy.BindConfig(Config);

        WorldToastManager.MaxAge = Config.Bind("Toasts",
            "Lifetime seconds",
            3f,
            "How long feedback popups stay on screen before fading out.").Value;

        worldToasts = WorldToastManager.Create();
        preHeroUpdate = gameObject.AddComponent<PreHeroUpdate>();
        SceneManager.sceneLoaded += OnSceneLoaded;

        try {
            harmony = Harmony.CreateAndPatchAll(GetType().Assembly);
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to initialize: {e}");
        }
    }

    private void Start() {
        // Register after DebugMod Awake
        try {
            DebugModInterop.RegisterSubmenu("Speedrun Gym",
                () => [
                    DebugModInterop.CreateSubmenu("Training Dummy",
                        () => [
                            DebugModInterop.CreateAction("Spawn", trainingDummy.Spawn),
                            DebugModInterop.CreateAction("Despawn all", trainingDummy.DespawnAll),
                        ]),
                    DebugModInterop.CreateSubmenu("Visualization",
                        () => [
                            CreateConfigToggle("Pogo Endlag", PogoEndlagDetector.enabled),
                            CreateConfigToggle("Jump Timing Repress", JumpTimingDetector.repress),
                            CreateConfigToggle("Jump Timing Sprintjump Release", JumpTimingDetector.sprintjumpRelease),
                            CreateConfigToggle("Walljump", WallJumpDetector.enabled),
                            CreateConfigToggle("Silk Waste", SilkWasteDetector.enabled),
                            CreateConfigToggle("Unhittable Tint", UnhittableTint.Enabled),
                        ]),
                ]);
        } catch (Exception e) {
            Log.Error(e);
        }
    }

    private static object? CreateConfigToggle(string title, ConfigEntry<bool> entry)
        => DebugModInterop.CreateAction(() => $"{title}: {(entry.Value ? "On" : "Off")}", () => entry.Value = !entry.Value);

    private void LateUpdate() {
        try {
            PogoEndlagDetector.LateUpdate();
            WallJumpDetector.LateUpdate(); // before JumpTimingDetector, which skips presses it claims
            JumpTimingDetector.LateUpdate();
            UnhittableTint.LateUpdate();
            worldToasts.Update();
        } catch (Exception e) {
            Log.Error($"Error during LateUpdate: {e}");
        }
    }

    private void OnDestroy() {
        // Clean up everything, in order to support hot reloading

        try {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Destroy(preHeroUpdate);
            harmony.UnpatchSelf();
            ForceCrawPogo.Cleanup();
            worldToasts.Destroy();
            trainingDummy.Dispose();
            UnhittableTint.Dispose();
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to clean up: {e}");
        }

        Log.Info($"Plugin {Name} ({Id}) has been unloaded!");
    }

    // Single global scene-loaded listener; dispatches to features that need it.
    private static void OnSceneLoaded(Scene scene, LoadSceneMode loadMode) {
        try {
            WorldToastManager.ClearAll();
            ForceCrawPogo.OnSceneLoaded();
            WallJumpDetector.Reset();
            UnhittableTint.OnSceneLoaded();
        } catch (Exception e) {
            Log.Error($"Error during OnSceneLoaded: {e}");
        }
    }

    internal static new void StartCoroutine(IEnumerator coroutine) {
        ((MonoBehaviour)instance).StartCoroutine(coroutine);
    }
}

[HarmonyPatch]
internal static class FsmStatePatches {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Fsm), nameof(Fsm.EnterState), typeof(FsmState))]
    private static void EnterState(Fsm __instance, FsmState state) {
        try {
            ForceCrawPogo.OnEnterState(__instance, state);
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
