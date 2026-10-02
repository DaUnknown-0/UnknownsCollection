// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * HypnotistView - the Hypnotist looks through the eyes of the hypnotised player (User 2026-10-02).
 *
 * Option 1756 "Hypnotist Sees Through The Victim":
 *  - Off.
 *  - Peek (default): while a hypnosis holds, a PEEK button shows the victim's view for 5 s,
 *    20 s cooldown.
 *  - Until The Meeting: the view stays open as long as the hypnosis holds.
 *
 * The view is a small round picture in the lower left, like a security camera that follows the
 * victim: a second camera renders the area around the victim into a texture. Its radius is the
 * victim's own light radius (lights sabotage and vision options included), so the Hypnotist never
 * sees further than the victim would. Walls do not block it, exactly like vanilla security cameras.
 * Roles are not shown: the picture is the plain world, names and colours as everybody sees them.
 *
 * The camera comes from the map's surveillance minigame prefab where there is one (a camera built
 * from scratch shows only the floor, the vision-masked objects are missing: Atlas autotest
 * 2025-09-25). Without one it falls back to a copy of the main camera's settings.
 * Purely local: only the Hypnotist's client renders anything; no RPC.
 */

using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TheOtherRoles.Objects;

namespace UnknownsCollection {
    public static class HypnotistView {
        private const float PeekSeconds = 5f, PeekCooldown = 20f;
        private const float ViewPx = 340f;           // diameter at the 1920x1080 reference

        private static CustomButton peekButton;
        private static RenderTexture rt;
        private static Camera cam;
        private static GameObject overlay;
        private static TMPro.TextMeshProUGUI caption;
        private static Sprite circle;
        private static bool cameraLogged;

        /// <summary>IdeasPackDiag: behave as "Until The Meeting" without touching the saved option.</summary>
        internal static bool DiagForce;

        private static int Mode() => DiagForce ? 2 : Hypnotist.ViewMode?.getSelection() ?? 1;

        // ---- Peek button ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.GetSprite("UnknownsCollection.Resources.hypnotist_vote.png", 115f);
                    peekButton = new CustomButton(
                        () => { },
                        () => Hypnotist.IsLocalHypnotist() && Mode() == 1 && PlayerControl.LocalPlayer.Data != null
                              && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => Hypnotist.ViewVictim() != null,
                        () => { peekButton.isEffectActive = false; peekButton.Timer = peekButton.MaxTimer; },
                        sprite,
                        CustomButton.ButtonPositions.upperRowFarLeft,
                        __instance, KeyCode.G, true, PeekSeconds,
                        () => { peekButton.Timer = peekButton.MaxTimer; },
                        false, UCLocalization.Tr("uc.ui.hypnotist.peek"));
                    peekButton.MaxTimer = PeekCooldown;
                    peekButton.Timer = 10f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[HypnotistView] Button creation failed: {e}");
                }
            }
        }

        // ---- The view, every frame (after TOR moved the players) ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                try {
                    var victim = Hypnotist.ViewVictim();
                    int mode = Mode();
                    bool show = victim != null && (mode == 2 || (mode == 1 && peekButton != null && peekButton.isEffectActive));
                    if (!show) { Hide(); return; }
                    if (!Ensure()) return;
                    float radius = 3f;
                    try { radius = ShipStatus.Instance.CalculateLightRadius(victim.Data); } catch { }
                    radius = Mathf.Clamp(radius, 0.5f, 8f);
                    var main = Camera.main;
                    Vector2 at = victim.transform.position;
                    cam.transform.position = new Vector3(at.x, at.y, main != null ? main.transform.position.z : -10f);
                    cam.orthographicSize = radius;
                    if (!cam.enabled) cam.enabled = true;
                    if (!overlay.activeSelf) overlay.SetActive(true);
                    string name = victim.Data?.PlayerName ?? "";
                    if (caption != null && caption.text != name) caption.text = name;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[HypnotistView] tick failed: {e.Message}");
                    Hide();
                }
            }
        }

        private static void Hide() {
            if (cam != null && cam.enabled) cam.enabled = false;
            if (overlay != null && overlay.activeSelf) overlay.SetActive(false);
        }

        // Camera and overlay live in the game scene and die with it; the texture is kept.
        private static bool Ensure() {
            var main = Camera.main;
            if (main == null) return false;
            if (rt == null) {
                rt = new RenderTexture(256, 256, 16) { name = "UC_HypnotistView" };
                rt.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            }
            if (cam == null) {
                Camera prefab = null;
                foreach (var sc in UnityEngine.Object.FindObjectsOfType<SystemConsole>()) {
                    var surv = sc != null && sc.MinigamePrefab != null ? sc.MinigamePrefab.TryCast<SurveillanceMinigame>() : null;
                    if (surv != null && surv.CameraPrefab != null) { prefab = surv.CameraPrefab; break; }
                }
                if (prefab != null) cam = UnityEngine.Object.Instantiate(prefab);
                else {
                    cam = new GameObject("UC_HypnotistCamera").AddComponent<Camera>();
                    cam.CopyFrom(main);
                    int shadow = LayerMask.NameToLayer("Shadow");
                    cam.cullingMask = main.cullingMask & ~(1 << 5) & (shadow >= 0 ? ~(1 << shadow) : ~0);
                }
                cam.gameObject.name = "UC_HypnotistCamera";
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.targetTexture = rt;
                cam.depth = main.depth - 5;
                cam.enabled = false;
                if (!cameraLogged) {
                    cameraLogged = true;
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[HypnotistView] camera from {(prefab != null ? "the surveillance prefab" : "main camera settings")}.");
                }
            }
            if (overlay == null) BuildOverlay();
            return cam != null && overlay != null;
        }

        private static void BuildOverlay() {
            overlay = new GameObject("UC_HypnotistViewUI");
            var canvas = overlay.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;
            var scaler = overlay.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // frame ring, then the round mask with the camera picture inside
            var ring = Disc(overlay, new Vector2(40, 40), ViewPx + 14f, new Color(0.72f, 0.40f, 1f, 0.95f));
            var mask = Disc(ring, Vector2.zero, ViewPx, Color.white, centred: true);
            mask.AddComponent<Mask>().showMaskGraphic = false;
            var pic = new GameObject("Picture");
            pic.transform.SetParent(mask.transform, false);
            var prt = pic.AddComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one; prt.sizeDelta = Vector2.zero;
            var raw = pic.AddComponent<RawImage>();
            raw.texture = rt;
            raw.raycastTarget = false;

            var lgo = new GameObject("Caption");
            lgo.transform.SetParent(ring.transform, false);
            var lrt = lgo.AddComponent<RectTransform>();
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 1f);
            lrt.pivot = new Vector2(0.5f, 0f);
            lrt.anchoredPosition = new Vector2(0f, 6f);
            lrt.sizeDelta = new Vector2(ViewPx + 80f, 34f);
            caption = lgo.AddComponent<TMPro.TextMeshProUGUI>();
            caption.fontSize = 22;
            caption.fontStyle = TMPro.FontStyles.Bold;
            caption.color = new Color(0.85f, 0.7f, 1f);
            caption.alignment = TMPro.TextAlignmentOptions.Center;
            caption.raycastTarget = false;
            overlay.SetActive(false);
        }

        private static GameObject Disc(GameObject parent, Vector2 pos, float size, Color color, bool centred = false) {
            var go = new GameObject("Disc");
            go.transform.SetParent(parent.transform, false);
            var r = go.AddComponent<RectTransform>();
            r.anchorMin = r.anchorMax = r.pivot = centred ? new Vector2(0.5f, 0.5f) : Vector2.zero;
            r.anchoredPosition = pos;
            r.sizeDelta = new Vector2(size, size);
            var img = go.AddComponent<Image>();
            img.sprite = Circle();
            img.color = color;
            img.raycastTarget = false;
            return go;
        }

        private static Sprite Circle() {
            if (circle != null) return circle;
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float r = n / 2f - 1f;
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) {
                    float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f;
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f));
                }
            tex.SetPixels(px);
            tex.Apply(false, true);
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            circle = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
            circle.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return circle;
        }
    }
}
