using System;
using BepInEx;
using HarmonyLib;
using SpeedrunGym.Source.Moves;

namespace SpeedrunGym.Source;

[BepInAutoPlugin("io.github.jakobhellermann.speedrungym")]
public partial class SpeedrunGymPlugin : BaseUnityPlugin {
    private Harmony harmony = null!;

    private void Awake() {
        Log.Init(Logger);
        Log.Info($"Plugin {Name} ({Id}) has loaded!");

        PogoEndlagDetector.BindConfig(Config);

        try {
            harmony = Harmony.CreateAndPatchAll(GetType().Assembly);
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to initialize: {e}");
        }
    }

    private void OnDestroy() {
        // Clean up everything, in order to support hot reloading

        try {
            harmony.UnpatchSelf();
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to clean up: {e}");
        }

        Log.Info($"Plugin {Name} ({Id}) has been unloaded!");
    }

    private void LateUpdate() {
        try {
            PogoEndlagDetector.LateUpdate();
        } catch (Exception e) {
            Log.Error($"Error during LateUpdate: {e}");
        }
    }
}
