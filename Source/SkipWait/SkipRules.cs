using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SpeedrunGym.Source.SkipWait;

/// The rules layer: which fsms a skip may currently target, and how long one that was picked stays skippable.
internal static class SkipRules {
    /// scene events that always skip, identified by object name prefix and fsm name
    private static readonly (string objectPrefix, string fsmName)[] skippableFsms = [
        ("Weakness Scene", "Control"),
        ("Churchkeeper Intro Scene", "Control"),
        ("Silk Heart", "Control"),
        ("door_", "Wake Up"),
        ("door_", "Travel Control"), // the melody lift's arrival cinematic
        ("memory_orb", "Control"),
        ("Memory Control", "Memory Control"),
        ("Silk Heart Memory Return", "Silk Heart Memory Return"),
        ("Bone Beast NPC", "Interaction"),
        ("Bone Beast Corpse", "Death"),
        ("Lace NPC Citadel Meet", "Control"),
        ("Boss Scene", "Sequence"),
        ("Mapper NPC", "Ring Shout"), // Shakra's entrance before her first meeting
    ];

    private const string RoarExit = "ROAR EXIT";

    internal static IEnumerable<PlayMakerFSM> Skippable(IEnumerable<PlayMakerFSM> fsms) =>
        fsms.Where(fsm => IsSkippableThroughout(fsm) || IsConversation(fsm) || IsHeldPrompt(fsm) || IsStunningRoar(fsm));

    private static bool IsSkippableThroughout(PlayMakerFSM fsm) =>
        skippableFsms.Any(s => fsm.FsmName == s.fsmName && fsm.gameObject.name.StartsWith(s.objectPrefix))
        || (fsm.isActiveAndEnabled && IsPromptFsm(fsm))
        || IsPerformanceAudience(fsm);

    /// skipped only as long as what made it skippable lasts, e.g. a boss intro's roar but not the fight that follows
    internal static Func<bool>? SkipUntil(PlayMakerFSM fsm) {
        if (IsSkippableThroughout(fsm)) return null;
        if (IsConversation(fsm) || IsHeldPrompt(fsm)) return () => HeroController.instance.acceptingInput;
        return () => !IsStunningRoar(fsm);
    }

    /// an npc conversation holding the hero, e.g. waiting for its shop to close between lines
    private static bool IsConversation(PlayMakerFSM fsm) =>
        fsm.isActiveAndEnabled
        && !HeroController.instance.acceptingInput
        && fsm.FsmStates.Any(state => state.Actions.Any(a => a is RunDialogueBase));

    /// a prompt in the world, e.g. to bind after a boss: one gameplay button continues while the hero is held
    private static bool IsHeldPrompt(PlayMakerFSM fsm) =>
        fsm.isActiveAndEnabled
        && !HeroController.instance.acceptingInput
        && !fsm.transform.IsChildOf(HeroController.instance.transform)
        && fsm.Fsm.ActiveState is { } state
        && ActionCatalog.IsButtonPrompt(state)
        && state.Actions.Any(a => a.Enabled && a is ListenForCast or ListenForJump or ListenForAttack);

    private static bool IsPromptFsm(PlayMakerFSM fsm) =>
        fsm.FsmStates.Any(state => state.Actions.OfType<ListenForPromptContinue>().Any());

    /// reacts to the hero playing the needolin, e.g. a gate whose locks open the longer she plays
    private static bool IsPerformanceAudience(PlayMakerFSM fsm) =>
        HeroPerformanceRegion.IsPerforming
        && fsm.isActiveAndEnabled
        && fsm.FsmStates.Any(state => state.Actions.Any(a => a.Enabled && a is CheckHeroPerformanceRegionBase));

    /// a stunning roar locks the hero until some fsm sends ROAR EXIT
    internal static bool IsRoarLocked(GameObject hero) => hero
        .GetComponents<PlayMakerFSM>()
        .Any(fsm => fsm.Fsm.ActiveState is { } state && state.Transitions.Any(t => t.EventName == RoarExit));

    internal static bool SendsRoarExit(PlayMakerFSM fsm, GameObject hero) =>
        fsm.FsmStates.SelectMany(state => state.Actions).Any(a => a switch {
            SendEventByName send => send.sendEvent.Value == RoarExit && Targets(fsm, send.eventTarget, hero),
            SendEventByNameV2 send => send.sendEvent.Value == RoarExit && Targets(fsm, send.eventTarget, hero),
            // sends ROAR EXIT itself
            CallMethodProper call => call.behaviour.Value == nameof(HeroController)
                && call.methodName.Value == nameof(HeroController.StopRoarLock)
                && fsm.Fsm.GetOwnerDefaultTarget(call.gameObject) == hero,
            _ => false,
        });

    private static bool Targets(PlayMakerFSM fsm, FsmEventTarget target, GameObject go) =>
        target.target is FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM
        && fsm.Fsm.GetOwnerDefaultTarget(target.gameObject) == go;

    /// the roar ends via StartRoarEmitter.OnExit or a StopRoarEmitter in a following state
    private static bool IsStunningRoar(PlayMakerFSM fsm) => fsm.isActiveAndEnabled && IsStunningRoar(fsm.Fsm.ActiveState);

    internal static bool IsStunningRoar(FsmState? state) =>
        state != null && state.Actions.Any(a => a.Enabled && a switch {
            StartRoarEmitter roar => roar.stunHero.Value,
            StartRoarEmitterV2 roar => roar.stunHero.Value,
            _ => false,
        });
}
