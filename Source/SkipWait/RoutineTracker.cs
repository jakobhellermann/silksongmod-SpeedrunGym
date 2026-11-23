using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SpeedrunGym.Source.SkipWait;

/// Tracks the game coroutines a skip can fast-forward (dialogue, cinematics, messages, scene preloads) for the whole
/// session: routines started outside a skip must already be known when a request wants to run them to their end.
internal static class RoutineTracker {
    /// the IsPlaying patch reports this player as playing while a video-start routine steps
    internal static TeamCherry.Cinematics.CinematicVideoPlayer? StartingVideo;

    /// bounds routines that wait on something that doesn't happen without time passing, e.g. while the game is paused
    private const int MaxRoutineSteps = 10_000;

    /// running coroutines, oldest first
    private static readonly List<SkipRoutine> routines = [];

    internal static void Track(MonoBehaviour owner, IEnumerator inner, bool startsVideo) {
        SkipRoutine routine = new(owner, inner, startsVideo);
        routines.Add(routine);
    }

    internal static bool Has<T>(Func<T, bool>? owner = null) where T : class {
        DropStopped();
        return routines.Any(r => r.Owner is T o && (owner == null || owner(o)));
    }

    /// innermost first, so a routine resumes only once what it waits on has finished
    internal static void FastForward<T>(Func<T, bool>? owner = null) where T : class {
        DropStopped();
        while (routines.LastOrDefault(r => r.Owner is T o && (owner == null || owner(o))) is { } routine) routine.Finish();
    }

    /// routines started since the skip began first return to their starter like any coroutine, whose code after
    /// StartCoroutine (e.g. adding the input blocker the routine's end removes) must run before that end
    internal static void FinishStarted(SkipLog log) {
        DropStopped();
        while (routines.LastOrDefault(r => r.StartedWhileSkipping) is { } routine) {
            log.Line($"running {routine} to its end");
            routine.Finish();
        }
    }

    internal static void DropOwner(MonoBehaviour owner) {
        foreach (SkipRoutine routine in routines.Where(r => r.Owner == owner).ToList()) routine.Stop();
    }

    internal static string Describe<T>(Func<T, bool>? owner = null) where T : class {
        DropStopped();
        return string.Join(", ", routines.Where(r => r.Owner is T o && (owner == null || owner(o))).Select(r => r.Owner.name));
    }

    /// unity stops an object's coroutines when it is deactivated or destroyed, e.g. with its scene
    private static void DropStopped() {
        foreach (SkipRoutine routine in routines.Where(r => !r.Owner || !r.Owner.gameObject.activeInHierarchy).ToList()) {
            routine.Stop();
        }
    }

    /// a game coroutine that runs to its end within the frame while skipping, so everything it does along the way still
    /// happens, just without waiting
    private sealed class SkipRoutine(MonoBehaviour owner, IEnumerator inner, bool startsVideo) : IEnumerator {
        internal readonly MonoBehaviour Owner = owner;
        internal readonly bool StartedWhileSkipping = SkipWaitFeature.Current != null;
        internal readonly bool StartsVideo = startsVideo;
        private readonly IEnumerator inner = inner;
        private bool done;

        public object Current => inner.Current;

        public override string ToString() => $"{Owner.name} {inner.GetType().Name}";

        public bool MoveNext() => !done && Step();

        public void Reset() => throw new NotSupportedException();

        public void Finish() {
            if (Owner is DialogueBox) SkipWaitFeature.Current?.DialogueFinished();
            // a video's load only completes over real frames, so a start routine still mid-load can't run to its end
            // within the frame; the game's own skip ends the video instead
            if (StartsVideo && ((CinematicPlayer)Owner).cinematicVideoPlayer is { IsLoading: true }) {
                SkipWaitFeature.Current?.Log.Line($"{this}: video still loading, running its skip to the end");
                // the stopped start routine never ended its play wait, which the game's finish check waits on
                ((CinematicPlayer)Owner).isWaitingToPlay = false;
                Stop();
                SkipRoutine skipRoutine = new(Owner, ((CinematicPlayer)Owner).Skip(), startsVideo: false);
                routines.Add(skipRoutine);
                skipRoutine.Finish();
                return;
            }
            for (int steps = 0; ; steps++) {
                if (steps == MaxRoutineSteps) throw new InvalidOperationException($"{inner} did not finish after {steps} steps");
                if (Owner is DialogueBox box) {
                    box.waitingToAdvance = false;
                    box.currentRevealSpeed = 100_000f;
                }
                if (Owner is UIMsgProxy) PressToContinue();
                if (!(StartsVideo ? StepStartingVideo() : Step()) || done) return;

                switch (inner.Current) {
                    case SkipRoutine nested:
                        nested.Finish();
                        break;
                    // the routines it started since
                    case Coroutine:
                        while (routines[^1] != this) routines[^1].Finish();
                        break;
                    case WaitForSeconds when Owner is CinematicPlayer { actCard: { } card } && card.isActiveAndEnabled:
                        // the act card stays up until its animation has played
                        card.Update(0f);
                        AnimatorStateInfo info = card.GetCurrentAnimatorStateInfo(0);
                        card.Update(info.length * Mathf.Max(0f, 1f - info.normalizedTime));
                        break;
                    case null or WaitForSeconds:
                        break;
                    default:
                        throw new InvalidOperationException($"can't skip {inner} waiting on {inner.Current}");
                }
            }
        }

        /// a message waits for a press of its own
        private void PressToContinue() {
            FieldInfo wasPressed = inner.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .SingleOrDefault(f => f.Name.Contains("wasPressed"))
                ?? throw new MissingFieldException(inner.GetType().FullName, "wasPressed");
            wasPressed.SetValue(inner, true);
        }

        private bool StepStartingVideo() {
            StartingVideo = ((CinematicPlayer)Owner).cinematicVideoPlayer;
            try {
                return Step();
            } finally {
                StartingVideo = null;
            }
        }

        private bool Step() {
            if (inner.MoveNext()) return true;
            Stop();
            return false;
        }

        public void Stop() {
            done = true;
            routines.Remove(this);
        }
    }
}
