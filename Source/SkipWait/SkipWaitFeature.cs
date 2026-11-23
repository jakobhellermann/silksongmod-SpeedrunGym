using System;
using UnityEngine;

namespace SpeedrunGym.Source.SkipWait;

/// Entry point of the skip feature, plus the session state that outlives a single request.
internal static class SkipWaitFeature {
    private const float HoldRepeatAfter = 0.1f;

    internal static SkipRequest? Current { get; private set; }

    // a scene load can't be skipped, but the request goes on once the hero has entered the scene it loaded
    private static SceneLoad? pendingContinuation;

    // Time.time of the press; -1 while no hold is tracked
    private static float holdSince = -1f;

    // the last request's outcome, so a held key only reports to the console when it changes
    private static string? lastOutcome;

    internal static void RequestSkip() {
        SkipLog log = new();
        log.Line($"Skip requested (frame {Time.frameCount})");
        if (Current != null) {
            log.Line("Still skipping");
            return;
        }
        holdSince = Time.time;
        Report(RunSkip(log));
    }

    internal static void UpdateHold() {
        if (holdSince < 0f) return;

        if (!DebugModInterop.IsKeybindHeld(SkipWaitKeybinds.KeybindName)) {
            holdSince = -1f;
            // a fresh press always reports its outcome
            lastOutcome = null;
            return;
        }
        if (Time.time - holdSince >= HoldRepeatAfter) Report(RunSkip());
    }

    private static void Report(string outcome) {
        if (outcome == lastOutcome) return;
        lastOutcome = outcome;
        if (DebugModInterop.Installed) DebugModInterop.LogConsole(outcome);
    }

    internal static string RunSkip(SkipLog? log = null) {
        RequestPatches.Ensure();

        log ??= new SkipLog();
        SkipRequest request = new(log);
        Current = request;
        try {
            return request.Run();
        } finally {
            Current = null;
        }
    }

    internal static void ContinueAfterSceneLoad(SceneLoad load) {
        Log($"waiting for the load of {load.TargetSceneName} to continue");
        pendingContinuation = load;
    }

    /// after the entry gate's AfterEntry, which sends FINISH ENTRY to custom entry fsms like waking up on the ground
    internal static void ContinueOnceEntered() {
        if (pendingContinuation is not { } load) return;
        pendingContinuation = null;
        if (GameManager.instance.LastSceneLoad != load) {
            Log($"not continuing, another scene load replaced the one of {load.TargetSceneName}");
            return;
        }
        Log($"continuing after entering {load.TargetSceneName}");
        if (Current == null) Report(RunSkip());
    }

    private static void Log(string message) => (Current?.Log ?? new SkipLog()).Line(message);
}
