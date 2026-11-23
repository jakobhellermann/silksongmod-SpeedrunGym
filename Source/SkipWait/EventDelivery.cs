using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SpeedrunGym.Source.SkipWait;

/// Where the events a state waits on come from: game systems outside the fsm (with a way to run them up to sending),
/// other fsms broadcasting to a register, and targeted sends that arrived before their receiver was ready.
internal static class EventDelivery {
    /// events sent by game systems outside the fsm, each with a way to run that system up to sending it
    private static readonly Dictionary<string, Func<string?>> eventSourceFastForwards = new() {
        ["REGENERATED SILK CHUNK"] = SilkRegen,
        ["GATE LOCKS PRE COMPLETE"] = GateLock,
        ["GATE LOCKS COMPLETE"] = GateLock,
        ["CINEMATIC START"] = CinematicStart,
    };

    /// the gate lock's play routine, taken over from its coroutine and driven by skips from then on
    private static (BellShrineGateLock gateLock, IEnumerator routine) drivenGateLock;

    internal static IEnumerable<EventWait> TransitionWaits(SkipRequest req, Fsm fsm, FsmState state, string stateName) {
        HashSet<string> awaited = state.Transitions.Select(t => t.EventName).ToHashSet();
        foreach (string eventName in awaited.Where(eventSourceFastForwards.ContainsKey)) {
            yield return new EventWait(eventName, () => FastForwardEventSource(req, fsm, state, eventName, stateName));
        }
        // a running dialogue ends the state once it is fast-forwarded, no other fsm has to send anything
        if (DialogueBox._instance is { isDialogueRunning: true } && state.Actions.Any(a => a is RunDialogueBase)) yield break;
        yield return new EventWait("register senders", () => FastForwardRegisterSenders(req, fsm, state, awaited, stateName));
    }

    private static SkipResult FastForwardEventSource(SkipRequest req, Fsm fsm, FsmState state, string eventName, string stateName) {
        string? error = eventSourceFastForwards[eventName]();
        if (error == null && fsm.ActiveState == state && !fsm.IsSwitchingState) error = "the event did not leave the state";
        req.Log.Line(error == null
            ? $"{stateName}: fast-forwarded {eventName}{FsmStepper.Transition(fsm, state)}"
            : $"{stateName}: stuck, cannot fast-forward {eventName}: {error}");
        return error == null ? SkipResult.Done : SkipResult.Refused;
    }

    /// skips other fsms that broadcast an event this state waits for, until one of them makes it leave
    private static SkipResult FastForwardRegisterSenders(SkipRequest req, Fsm fsm, FsmState state, HashSet<string> awaited, string stateName) {
        List<(PlayMakerFSM sender, string[] events)> senders = req.Fsms
            .Where(s => s.Fsm != fsm && s.isActiveAndEnabled && !req.WaitingOnSenders.Contains(s.Fsm))
            .Select(s => (s, SentEvents(s, fsm).Where(awaited.Contains).Distinct().ToArray()))
            .Where(s => s.Item2.Length > 0 && SendsUnprompted(s.Item1.Fsm, s.Item2, fsm))
            .ToList();
        if (senders.Count == 0) return SkipResult.Nothing;

        req.WaitingOnSenders.Add(fsm);
        try {
            foreach ((PlayMakerFSM sender, string[] events) in senders) {
                string header = $"{stateName}: waiting on {string.Join(", ", events)}, skipping sender {FsmStepper.Name(sender)}";
                if (FsmStepper.Chain(req, sender.Fsm, header, () => fsm.ActiveState != state) == SkipResult.Break) return SkipResult.Break;
                if (fsm.ActiveState != state) {
                    req.Log.Line($"{stateName}: received from {FsmStepper.Name(sender)}{FsmStepper.Transition(fsm, state)}");
                    return SkipResult.Done;
                }
            }
        } finally {
            req.WaitingOnSenders.Remove(fsm);
        }

        req.Log.Line($"{stateName}: stuck, none of the {senders.Count} senders made it leave");
        return SkipResult.Refused;
    }

    /// a skip can send an event before its receiver got to the state that handles it, where it is dropped; without the
    /// skip it would have arrived later, so run the receiver to that state and send it again; true on a break
    internal static bool RedeliverEarlySends(SkipRequest req, Fsm sender, FsmState state) {
        if (!req.CheckedSends.Add((sender, state))) return false;

        foreach (FsmStateAction action in state.Actions.Where(a => a.Enabled)) {
            (FsmEventTarget? target, string? eventName) = action switch {
                SendEventByName send => (send.eventTarget, send.sendEvent.Value),
                SendEventByNameV2 send => (send.eventTarget, send.sendEvent.Value),
                _ => (null, null),
            };
            if (target == null || string.IsNullOrEmpty(eventName)) continue;

            foreach (Fsm receiver in TargetedFsms(sender, target)) {
                if (receiver == sender || Handles(receiver, receiver.ActiveState, eventName)) continue;
                if (!ReachesHandlerUnprompted(receiver, eventName)) continue;

                string header = $"{state.Name}: {FsmStepper.Name(receiver)} wasn't ready for {eventName}";
                if (FsmStepper.Chain(req, receiver, header, () => Handles(receiver, receiver.ActiveState, eventName)) == SkipResult.Break) {
                    return true;
                }
                if (!Handles(receiver, receiver.ActiveState, eventName)) continue;

                FsmState before = receiver.ActiveState;
                receiver.Event(FsmEvent.GetFsmEvent(eventName));
                req.Log.Line($"{FsmStepper.Name(receiver)}: redelivered {eventName}{FsmStepper.Transition(receiver, before)}");
            }
        }
        return false;
    }

    private static IEnumerable<Fsm> TargetedFsms(Fsm sender, FsmEventTarget target) {
        switch (target.target) {
            case FsmEventTarget.EventTarget.GameObject:
            case FsmEventTarget.EventTarget.GameObjectFSM:
                GameObject? go = sender.GetOwnerDefaultTarget(target.gameObject);
                if (!go) return [];
                return PlayMakerFSM.FsmList
                    .Where(c => c && c.gameObject == go
                        && (target.target == FsmEventTarget.EventTarget.GameObject || c.FsmName == target.fsmName.Value))
                    .Select(c => c.Fsm)
                    .ToList();
            case FsmEventTarget.EventTarget.FSMComponent:
                return target.fsmComponent ? [target.fsmComponent.Fsm] : [];
            default:
                return [];
        }
    }

    private static bool Handles(Fsm fsm, FsmState? state, string eventName) =>
        state != null
        && (state.Transitions.Any(t => t.EventName == eventName) || fsm.GlobalTransitions.Any(t => t.EventName == eventName));

    /// only over FINISHED, i.e. states the fsm gets through on its own given time
    private static bool ReachesHandlerUnprompted(Fsm fsm, string eventName) {
        HashSet<FsmState> seen = [];
        for (FsmState? state = fsm.ActiveState; state != null && seen.Add(state);) {
            if (Handles(fsm, state, eventName)) return true;
            FsmTransition? finished = state.Transitions.FirstOrDefault(t => t.EventName == FsmEvent.Finished.Name);
            state = finished == null ? null : fsm.GetState(finished.ToState);
        }
        return false;
    }

    private static IEnumerable<string> SentEvents(PlayMakerFSM sender, Fsm receiver) =>
        sender.FsmStates.SelectMany(state => state.Actions).Select(a => SentEvent(sender.Fsm, a, receiver)).Where(e => e != null)!;

    /// the event the action sends to everyone, or to the receiver in particular
    private static string? SentEvent(Fsm sender, FsmStateAction action, Fsm receiver) => action switch {
        SendEventToRegister send => send.eventName.Value,
        SendEventToRegisterDelay send => send.EventName.Value,
        SendEventByName { eventTarget.target: FsmEventTarget.EventTarget.BroadcastAll } send => send.sendEvent.Value,
        SendEventByName send when TargetedFsms(sender, send.eventTarget).Contains(receiver) => send.sendEvent.Value,
        SendEventByNameV2 send when TargetedFsms(sender, send.eventTarget).Contains(receiver) => send.sendEvent.Value,
        _ => null,
    };

    /// whether the fsm gets from its current state to sending one of the events without the player deciding something;
    /// button prompts don't stop it since the skip presses them
    private static bool SendsUnprompted(Fsm fsm, string[] events, Fsm receiver) {
        HashSet<FsmState> seen = [];
        Queue<FsmState> queue = new();
        if (fsm.ActiveState != null) queue.Enqueue(fsm.ActiveState);
        while (queue.Count > 0) {
            FsmState state = queue.Dequeue();
            if (!seen.Add(state)) continue;
            if (state.Actions.Any(a => a.Enabled && events.Contains(SentEvent(fsm, a, receiver)))) return true;

            bool playerControlled = state.Actions.Any(a => a.Enabled && a.GetType().Name.StartsWith("ListenFor"))
                && !ActionCatalog.IsButtonPrompt(state);
            if (playerControlled) continue;
            foreach (FsmTransition transition in state.Transitions) {
                if (fsm.GetState(transition.ToState) is { } next) queue.Enqueue(next);
            }
        }
        return false;
    }

    private static string? CinematicStart() {
        if (!RoutineTracker.Has<CinematicPlayer>(IsTriggeredCinematic)) return "no cinematic is starting";
        RoutineTracker.FastForward<CinematicPlayer>(IsTriggeredCinematic);
        return null;
    }

    /// a preloaded cinematic's start routine waits for its trigger
    internal static bool IsTriggeredCinematic(CinematicPlayer player) =>
        player.playTrigger != CinematicPlayer.MovieTrigger.ManualPlayPreloaded || player.allowPlay;

    /// runs BellShrineGateLock.PlayRoutine, the delays between its bells, up to the next event it sends
    private static string? GateLock() {
        BellShrineGateLock gateLock = UnityEngine.Object.FindAnyObjectByType<BellShrineGateLock>();
        if (!gateLock) return "no gate lock in the scene";
        if (drivenGateLock.gateLock != gateLock) {
            if (gateLock.playRoutine == null) return "the gate lock isn't playing";
            gateLock.StopCoroutine(gateLock.playRoutine);
            drivenGateLock = (gateLock, gateLock.PlayRoutine());
        }

        Fsm eventFsm = gateLock.eventFsm.Fsm;
        FsmState? before = eventFsm.ActiveState;
        for (int steps = 0; eventFsm.ActiveState == before && !eventFsm.IsSwitchingState; steps++) {
            if (steps == 1000) throw new InvalidOperationException($"{gateLock.name} did not send an event after {steps} steps");
            if (!drivenGateLock.routine.MoveNext()) {
                drivenGateLock = default;
                return eventFsm.ActiveState == before && !eventFsm.IsSwitchingState ? "the gate lock stopped without completing" : null;
            }
        }
        return null;
    }

    /// runs the pending silk regen timers out the way HeroController.Update does, returns why not if it can't
    private static string? SilkRegen() {
        HeroController hero = HeroController.instance;
        if (hero.isSilkRegenBlocked || !hero.hasSilkSpoolAppeared) return "silk regen is blocked";

        if (hero.silkRegenDelayLeft > 0f) {
            hero.silkRegenDelayLeft = 0f;
            hero.StartSilkRegen();
        }
        if (hero.silkRegenDurationLeft > 0f) {
            hero.silkRegenDurationLeft = 0f;
            hero.DoSilkRegen();
        }
        return null;
    }
}
