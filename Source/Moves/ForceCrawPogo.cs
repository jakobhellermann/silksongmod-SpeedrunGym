using System;
using BepInEx.Configuration;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace SpeedrunGym.Source.Moves;

// Forces the Greymoor_15 "crow enemies/Crow (3)" Behaviour FSM to swoop (the craw pogo) on a
// chosen flap-move decision, for practicing craw pogos.
//
// Each "Flap Move" state entry is one decision: SendRandomEventV4 randomly sends SHIFT (flap
// again) or ATTACK (swoop). We rewrite that action's weights on state entry so the choice is
// deterministic — SHIFT until the target decision, then ATTACK — and reset the counter after
// each attack so it repeats at a steady cadence.
//
// The EnterState patch and sceneLoaded listener that drive this live in the plugin; it forwards
// to OnEnterState / OnSceneLoaded here.
internal static class ForceCrawPogo {
    private enum Mode {
        Normal,
        Early, // attack on the 1st decision
        Late1, // attack on the 2nd decision
        Late2 // attack on the 3rd decision
    }

    private static ConfigEntry<Mode> mode = null!;

    internal static void BindConfig(ConfigFile config) {
        mode = config.Bind("Normalization", "Force craw pogo", Mode.Normal,
            "Force how often the craw in Greymoor_15 flaps before diving.");
        mode.SettingChanged += (_, _) => {
            decisionCount = 0;
            Restore();
        };
    }

    // Restore vanilla so disabling/hot-reloading the mod doesn't leave the action rewritten.
    internal static void Cleanup() {
        Restore();
    }

    internal static void OnSceneLoaded() {
        // Fresh scene → fresh (vanilla) FSM instance; forget the stale action and restart the count.
        decisionCount = 0;
        patchedAction = null;
    }

    private static int decisionCount;

    // The action we last rewrote plus its authored values, so we can restore vanilla randomness.
    private static SendRandomEventV4? patchedAction;
    private static float[] origWeights = [];
    private static int[] origEventMax = [];
    private static int[] origMissedMax = [];

    private static void Restore() {
        if (patchedAction is null) return;
        for (var i = 0; i < origWeights.Length; i++) patchedAction.weights[i].Value = origWeights[i];
        for (var i = 0; i < origEventMax.Length; i++) patchedAction.eventMax[i].Value = origEventMax[i];
        for (var i = 0; i < origMissedMax.Length; i++) patchedAction.missedMax[i].Value = origMissedMax[i];
        patchedAction = null;
    }

    internal static void OnEnterState(Fsm fsm, FsmState state) {
        if (mode.Value == Mode.Normal) return;
        if (state.Name != "Flap Move") return;
        if (fsm.Name != "Behaviour") return;

        var go = fsm.GameObject;
        if (!go || go.name != "Crow (3)") return;

        if (Array.Find(state.Actions, a => a is SendRandomEventV4) is not SendRandomEventV4 action) return;

        var attackIndex = Array.FindIndex(action.events, e => e.Name == "ATTACK");
        var shiftIndex = Array.FindIndex(action.events, e => e.Name == "SHIFT");
        if (attackIndex < 0 || shiftIndex < 0) return;

        // Capture the authored values once per action instance (before we clobber them) so
        // switching back to Normal can restore vanilla randomness.
        if (!ReferenceEquals(patchedAction, action)) {
            patchedAction = action;
            origWeights = Array.ConvertAll(action.weights, w => w.Value);
            origEventMax = Array.ConvertAll(action.eventMax, m => m.Value);
            origMissedMax = Array.ConvertAll(action.missedMax, m => m.Value);
        }

        var target = mode.Value switch {
            Mode.Early => 1,
            Mode.Late1 => 2,
            Mode.Late2 => 3,
            _ => 0
        };

        decisionCount++;
        var forceAttack = decisionCount >= target;
        Log.Info($"[craw] normalize flap-move decision #{decisionCount} (target {target}) -> {(forceAttack ? "ATTACK" : "SHIFT")}");
        if (forceAttack) decisionCount = 0;

        // Make the random choice deterministic: only the desired event keeps weight, and the
        // eventMax / missedMax guards never fire so the weighted pick always wins.
        var wantIndex = forceAttack ? attackIndex : shiftIndex;
        for (var i = 0; i < action.weights.Length; i++)
            action.weights[i].Value = i == wantIndex ? 1f : 0f;
        foreach (var max in action.eventMax) max.Value = int.MaxValue;
        foreach (var max in action.missedMax) max.Value = int.MaxValue;
    }
}
