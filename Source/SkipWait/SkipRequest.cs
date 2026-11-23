using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SpeedrunGym.Source.SkipWait;

/// One skip request: repeats rounds of cutscene/message/dialogue/fsm skips until nothing progresses. Each kind of skip
/// can uncover another (a message ends a state, a state starts a dialogue). Holds all mutable state of the request, so
/// requests can't leak into each other.
internal sealed class SkipRequest {
    private const int MaxSkipRounds = 50;

    internal readonly SkipLog Log;

    /// snapshots of the scene's fsms and jitters, refreshed per round since a skip may spawn new ones
    internal readonly List<PlayMakerFSM> Fsms = [];
    internal readonly List<JitterSelf> Jitters = [];

    internal readonly HashSet<(Fsm fsm, FsmState state)> CheckedSends = [];

    /// fsms currently waiting on register senders, never picked as a sender themselves
    internal readonly HashSet<Fsm> WaitingOnSenders = [];

    private bool finishedDialogue;

    internal SkipRequest(SkipLog log) {
        Log = log;
    }

    internal void DialogueFinished() => finishedDialogue = true;

    internal string Run() {
        bool textPrintSkip = CheatManager.IsTextPrintSkipEnabled;
        CheatManager.IsTextPrintSkipEnabled = true;
        SceneLoad loadBefore = GameManager.instance.LastSceneLoad;
        List<string> skipped = [];
        bool skippedAny = false;
        try {
            for (int round = 0; ; round++) {
                if (round == MaxSkipRounds) {
                    Log.Line($"stopped after {round} rounds, it seems to loop");
                    break;
                }

                RefreshSnapshots();
                RoutineTracker.FinishStarted(Log);

                // a triggered cinematic only enters the cutscene state on its next update
                if (GameManager.instance.GameState == GameState.CUTSCENE
                    || RoutineTracker.Has<CinematicPlayer>(EventDelivery.IsTriggeredCinematic)) {
                    if (!SkipCutscene(GameManager.instance)) break;
                    skippedAny = true;
                    skipped.Add("cutscene");
                    continue;
                }
                if (SkipUIMsg()) {
                    skippedAny = true;
                    skipped.Add("message");
                    continue;
                }
                if (RoutineTracker.Has<DialogueBox>()) {
                    skippedAny = true;
                    skipped.Add("dialogue");
                    FastForwardDialogue();
                    continue;
                }
                SkipResult fsms = SkipFsms(skipped);
                if (fsms == SkipResult.Nothing) break;
                skippedAny = true;
                if (fsms == SkipResult.Break) break;
            }

            if (!skippedAny) {
                // being stuck without control is when chains silently ending matter, so name the candidates then
                List<string> candidates = HeroController.instance.acceptingInput
                    ? []
                    : SkipRules.Skippable(Fsms)
                        .Select(fsm => $"{FsmStepper.Name(fsm)} in {fsm.ActiveStateName}, {FsmStepper.Awaiting(fsm.Fsm)}")
                        .ToList();
                Log.Line(candidates.Count == 0 ? "Nothing to skip" : $"Nothing to skip: {string.Join("; ", candidates)}");
            }
        } finally {
            CheatManager.IsTextPrintSkipEnabled = textPrintSkip;
        }

        // the close animation would still play out after an instantly ended conversation
        if (finishedDialogue && DialogueBox._instance is { isBoxOpen: false }) DialogueBox.HideInstant();

        GameManager gm = GameManager.instance;
        if (gm.IsInSceneTransition && gm.LastSceneLoad != loadBefore) SkipWaitFeature.ContinueAfterSceneLoad(gm.LastSceneLoad);

        return skippedAny ? $"Skipped: {string.Join(", ", skipped.Distinct())}" : "Nothing to skip";
    }

    private bool SkipCutscene(GameManager gm) {
        // a cinematic still starting or showing its act card, during which the game doesn't allow skipping
        if (RoutineTracker.Has<CinematicPlayer>(EventDelivery.IsTriggeredCinematic)) {
            Log.Line($"Fast-forwarding cinematic {RoutineTracker.Describe<CinematicPlayer>(EventDelivery.IsTriggeredCinematic)}");
            RoutineTracker.FastForward<CinematicPlayer>(EventDelivery.IsTriggeredCinematic);
            return true;
        }

        SkipPromptMode mode = gm.inputHandler.SkipMode;
        if (mode is not (SkipPromptMode.SKIP_INSTANT or SkipPromptMode.SKIP_PROMPT)) {
            Log.Line($"Cutscene is not skippable right now ({mode})");
            return false;
        }
        if (gm.inputHandler.skippingCutscene) return false;

        Log.Line("Skipping cutscene");
        gm.inputHandler.skippingCutscene = true;
        gm.SkipCutscene();
        return true;
    }

    /// runs a shown message to its end within the frame, so its hide and afterMsg callback happen as on a real press
    internal bool SkipUIMsg() {
        if (!RoutineTracker.Has<UIMsgProxy>()) return false;

        Log.Line($"Skipping message {RoutineTracker.Describe<UIMsgProxy>()}");
        RoutineTracker.FastForward<UIMsgProxy>();
        return true;
    }

    private void FastForwardDialogue() {
        Log.Line("Fast-forwarding dialogue");
        RoutineTracker.FastForward<DialogueBox>();
        Log.Line(DialogueBox._instance.isDialogueRunning ? "Dialogue lines done, conversation still open" : "Dialogue ended");
    }

    private SkipResult SkipFsms(List<string> skipped) {
        foreach (PlayMakerFSM fsm in SkipRules.Skippable(Fsms)) {
            // one fsm that is stuck must not keep the others from being skipped
            SkipResult result = FsmStepper.Chain(this, fsm.Fsm, FsmStepper.Name(fsm), SkipRules.SkipUntil(fsm));
            if (result is SkipResult.Done or SkipResult.Break) {
                skipped.Add(FsmStepper.Name(fsm));
                return result;
            }
        }
        if (SkipRoarLock()) {
            skipped.Add("roar lock");
            return SkipResult.Done;
        }
        return SkipResult.Nothing;
    }

    private bool SkipRoarLock() {
        GameObject hero = HeroController.instance.gameObject;
        if (!SkipRules.IsRoarLocked(hero)) return false;

        foreach (PlayMakerFSM fsm in Fsms) {
            if (fsm.gameObject == hero || !fsm.isActiveAndEnabled || !SkipRules.SendsRoarExit(fsm, hero)) continue;

            FsmStepper.Chain(this, fsm.Fsm, $"{FsmStepper.Name(fsm)}: releases the roar lock", () => !SkipRules.IsRoarLocked(hero));
            if (!SkipRules.IsRoarLocked(hero)) return true;
        }
        return false;
    }

    private void RefreshSnapshots() {
        Fsms.Clear();
        Fsms.AddRange(Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None));
        Jitters.Clear();
        Jitters.AddRange(Object.FindObjectsByType<JitterSelf>(FindObjectsSortMode.None));
    }
}
