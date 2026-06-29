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
    bool MoveUp);

// Floating text popups anchored to world positions. Inlined from DevUtils so this mod has no
// runtime dependency on it. The plugin owns the overlay canvas and pumps Update().
internal class WorldToastManager {
    private const float MaxAge = 3f;
    private const float FloatSpeed = 0.5f;
    private const float BackdropAlpha = 0.5f;
    private const int DefaultFontSize = 10;

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
            var age = now - entry.StartTime;
            if (age > MaxAge) {
                Object.Destroy(entry.Go);
                entries.RemoveAt(i);
                continue;
            }

            UpdatePosition(entry);
            var alpha = entry.Fade ? 1f - age / MaxAge : 1f;
            entry.Text.color = new Color(entry.Color.r, entry.Color.g, entry.Color.b, entry.Color.a * alpha);
            entry.Backdrop.color = new Color(0, 0, 0, BackdropAlpha * alpha);
        }
    }

    private void UpdatePosition(WorldToastEntry entry) {
        // Camera.main is null while a scene is loading / before the gameplay camera exists — skip until it's back.
        var camera = Camera.main;
        if (camera == null) return;

        var age = Time.time - entry.StartTime;
        var floatedWorld = entry.MoveUp ? entry.WorldPos + Vector3.up * (age * FloatSpeed) : entry.WorldPos;
        var screenPos = camera.WorldToScreenPoint(floatedWorld);

        // convert screen pos to canvas local pos
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.GetComponent<RectTransform>(),
            screenPos,
            canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera,
            out var localPoint
        );
        entry.Go.GetComponent<RectTransform>().localPosition = localPoint;
    }
}
