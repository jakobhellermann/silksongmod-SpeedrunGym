using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SpeedrunGym.Source.WorldToasts;

internal record struct WorldToastEntry(
    GameObject Go,
    Text Text,
    Image Backdrop,
    float StartTime,
    Vector3 WorldPos,
    Color Color,
    bool Fade,
    bool MoveUp) {
    public bool Clamped; // was the toast clamped to a screen edge last frame (anchor off-screen)?
}

// Floating text popups anchored to world positions. Inlined from DevUtils so this mod has no
// runtime dependency on it. The plugin owns the overlay canvas and pumps Update().
internal class WorldToastManager {
    internal static float MaxAge = 3f; // toast lifetime in seconds; configurable via the plugin
    private const float FadeFraction = 1f / 3f; // toast holds full opacity, then fades over this last fraction
    private const float FloatSpeed = 0.5f;
    private const float BackdropAlpha = 0.5f;
    private const int DefaultFontSize = 10;
    private const float ScreenMargin = 40f; // keep clamped toasts this far inside the canvas edges

    private static WorldToastManager? instance;

    private readonly Canvas canvas;
    private readonly List<WorldToastEntry> entries = [];

    private WorldToastManager(Canvas canvas) {
        this.canvas = canvas;
    }

    // Create the screen-space overlay canvas + manager. Call once from plugin Awake.
    internal static WorldToastManager Create() {
        var go = new GameObject("SpeedrunGymToastCanvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        Object.DontDestroyOnLoad(go);
        return instance = new WorldToastManager(canvas);
    }

    // Destroy all live toasts (e.g. on scene change — the canvas is DontDestroyOnLoad, so toasts would
    // otherwise linger into the next scene, floating at stale world positions).
    internal static void ClearAll() => instance?.ClearAllInner();

    private void ClearAllInner() {
        foreach (var entry in entries)
            if (entry.Go)
                Object.Destroy(entry.Go);
        entries.Clear();
    }

    // Destroy the canvas and clear the static instance (hot-reload cleanup).
    internal void Destroy() {
        if (canvas) Object.Destroy(canvas.gameObject);
        if (instance == this) instance = null;
    }

    internal static void Show(string message, Vector3 worldPos, Color? color = null, int fontSize = DefaultFontSize,
        bool fade = true, bool moveUp = true) {
        instance?.ShowInner(message, worldPos, color, fontSize, fade, moveUp);
    }

    private void ShowInner(string message, Vector3 worldPos, Color? color, int fontSize, bool fade, bool moveUp) {
        var resolvedColor = color ?? Color.white;

        // Root holds a backdrop Image and hugs the text via a layout group + content-size fitter,
        // so the panel stays readable on any background regardless of message length.
        var go = new GameObject("WorldToast");
        go.transform.SetParent(canvas.transform, false);

        var backdrop = go.AddComponent<Image>();
        backdrop.color = new Color(0, 0, 0, BackdropAlpha);
        backdrop.raycastTarget = false;

        var layout = go.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(4, 4, 2, 2);
        layout.childControlWidth = true;
        layout.childControlHeight = true;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);

        var text = textGo.AddComponent<Text>();
        text.text = message;
        text.fontSize = fontSize;
        text.color = resolvedColor;
        text.alignment = TextAnchor.MiddleCenter;
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;

        entries.Add(new WorldToastEntry(go, text, backdrop, Time.time, worldPos, resolvedColor, fade, moveUp));
        UpdatePosition(entries[^1]);
    }

    internal void Update() {
        var now = Time.time;
        for (var i = entries.Count - 1; i >= 0; i--) {
            var entry = entries[i];

            // Reset the lifetime only when the toast comes back from off-screen into view, and only if it
            // hasn't started fading out yet — while clamped off-screen (or once fading) it keeps ageing
            // normally and expires after MaxAge like any other.
            var clamped = UpdatePosition(entry);
            var age = now - entry.StartTime;
            var fadeStart = MaxAge * (1f - FadeFraction);
            if (entry.Clamped && !clamped && age < fadeStart) {
                entry.StartTime = now;
                age = 0f;
            }

            entry.Clamped = clamped;

            if (age > MaxAge) {
                Object.Destroy(entry.Go);
                entries.RemoveAt(i);
                continue;
            }

            var alpha = entry.Fade ? Mathf.Clamp01((MaxAge - age) / (MaxAge * FadeFraction)) : 1f;
            entry.Text.color = new Color(entry.Color.r, entry.Color.g, entry.Color.b, entry.Color.a * alpha);
            entry.Backdrop.color = new Color(0, 0, 0, BackdropAlpha * alpha);
            entries[i] = entry;
        }
    }

    // Positions the toast; returns true if it had to be clamped to a screen edge (anchor off-screen).
    private bool UpdatePosition(WorldToastEntry entry) {
        // Camera.main is null while a scene is loading / before the gameplay camera exists — skip until it's back.
        var camera = Camera.main;
        if (camera == null) return false;

        var age = Time.time - entry.StartTime;
        var floatedWorld = entry.MoveUp ? entry.WorldPos + Vector3.up * (age * FloatSpeed) : entry.WorldPos;
        var screenPos = camera.WorldToScreenPoint(floatedWorld);

        // If the target is behind the camera WorldToScreenPoint mirrors x/y — flip it back so a clamped
        // off-screen toast lands on the correct edge instead of the opposite one.
        if (screenPos.z < 0f) {
            screenPos.x = Screen.width - screenPos.x;
            screenPos.y = Screen.height - screenPos.y;
        }

        // convert screen pos to canvas local pos
        var canvasRect = canvas.GetComponent<RectTransform>();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvasRect,
            screenPos,
            canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera,
            out var localPoint
        );

        // Clamp into the canvas so the toast stays visible even when the camera doesn't follow Hornet
        // (some tricks freeze the camera while she flies off-screen).
        var rect = canvasRect.rect;
        var clampedX = Mathf.Clamp(localPoint.x, rect.xMin + ScreenMargin, rect.xMax - ScreenMargin);
        var clampedY = Mathf.Clamp(localPoint.y, rect.yMin + ScreenMargin, rect.yMax - ScreenMargin);
        var clamped = !Mathf.Approximately(clampedX, localPoint.x) || !Mathf.Approximately(clampedY, localPoint.y);

        entry.Go.GetComponent<RectTransform>().localPosition = new Vector2(clampedX, clampedY);
        return clamped;
    }
}
