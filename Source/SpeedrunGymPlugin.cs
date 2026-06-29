using System;
using BepInEx;
using HarmonyLib;
using HutongGames.PlayMaker;
using SpeedrunGym.Source.Moves;
using UnityEngine.SceneManagement;

namespace SpeedrunGym.Source;

[BepInAutoPlugin("io.github.jakobhellermann.speedrungym")]
public partial class SpeedrunGymPlugin : BaseUnityPlugin {
    private Harmony harmony = null!;

    private void Awake() {
        Log.Init(Logger);
        Log.Info($"Plugin {Name} ({Id}) has loaded!");

        PogoEndlagDetector.BindConfig(Config);
        ForceCrawPogo.BindConfig(Config);

        SceneManager.sceneLoaded += OnSceneLoaded;

        try {
            harmony = Harmony.CreateAndPatchAll(GetType().Assembly);
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to initialize: {e}");
        }
    }

    private void LateUpdate() {
        try {
            PogoEndlagDetector.LateUpdate();
        } catch (Exception e) {
            Log.Error($"Error during LateUpdate: {e}");
        }
    }

    private void OnDestroy() {
        // Clean up everything, in order to support hot reloading

        try {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            harmony.UnpatchSelf();
            ForceCrawPogo.Cleanup();
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to clean up: {e}");
        }

        Log.Info($"Plugin {Name} ({Id}) has been unloaded!");
    }

    // Single global scene-loaded listener; dispatches to features that need it.
    private static void OnSceneLoaded(Scene scene, LoadSceneMode loadMode) {
        try {
            ForceCrawPogo.OnSceneLoaded();
        } catch (Exception e) {
            Log.Error($"Error during OnSceneLoaded: {e}");
        }
    }
}

[HarmonyPatch]
internal static class FsmStatePatches {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Fsm), "EnterState", typeof(FsmState))]
#pragma warning disable HARMONIZE001
    private static void EnterState(Fsm __instance, FsmState state) {
#pragma warning restore HARMONIZE001
        try {
            ForceCrawPogo.OnEnterState(__instance, state);
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
