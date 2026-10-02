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
 * victim: a second camera renders the area around the victim into a texture. The frame is the
 * crew's normal (lights on) vision, so it does not zoom in when the lights go out. On top lies a
 * darkness mask cut exactly like the victim's own light (User 2026-10-02: he should see only what
 * the victim could see): the victim's light radius (lights sabotage, Trickster, vision options
 * included, through ShipStatus.CalculateLightRadius) and rays against Constants.ShadowMask, the
 * mask the game's light uses, so walls and closed doors hide what lies behind them.
 * Roles are not shown: the picture is the plain world, names and colours as everybody sees them.
 *
 * The camera comes from the map's surveillance minigame prefab where there is one (a camera built
 * from scratch shows only the floor, the vision-masked objects are missing: Atlas autotest
 * 2025-09-25). Without one it falls back to a copy of the main camera's settings.
 * Purely local: only the Hypnotist's client renders anything; no RPC.
 */

using System;
using System.Collections.Generic;
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

        // darkness mask: one ray per Rays-th of a turn, painted into a small texture over the picture
        // The game's light, measured 2026-10-02 against freeplay screenshots on the Skeld (radius 5 and 1):
        // it ends exactly at CalculateLightRadius (world units), its brightness falls LINEARLY from the
        // source to that edge, and unlit floor keeps about 23 % of its lit brightness. The source sits
        // 0.1 below the player's position.
        private const int Rays = 180, MaskPx = 128;
        private const float Dark = 0.77f, Edge = 0.15f, WallBias = 0.2f;
        private static readonly Vector2 LightOffset = new Vector2(0f, -0.1f);
        private static readonly float[] rayLen = new float[Rays];
        private static Texture2D maskTex;
        private static Color32[] maskPx;
        private static float nextMask;

        /// <summary>IdeasPackDiag: behave as "Until The Meeting" without touching the saved option.</summary>
        internal static bool DiagForce;

        private static int Mode() => DiagForce ? 2 : Hypnotist.ViewMode?.getSelection() ?? 1;

        // ---- Peek button ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            [HarmonyPriority(Priority.Low)]
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
                    float radius = 3f, frame = 3f;
                    try {
                        var ship = ShipStatus.Instance;
                        radius = ship.CalculateLightRadius(victim.Data);
                        frame = ship.MaxLightRadius * GameOptionsManager.Instance.currentNormalGameOptions.CrewLightMod;
                    } catch { }
                    radius = Mathf.Clamp(radius, 0.05f, 8f);
                    frame = Mathf.Clamp(Mathf.Max(frame, radius) * 1.1f, 0.5f, 8.8f);
                    var main = Camera.main;
                    Vector2 at = victim.transform.position;
                    cam.transform.position = new Vector3(at.x, at.y, main != null ? main.transform.position.z : -10f);
                    cam.orthographicSize = frame;
                    if (Time.unscaledTime >= nextMask) {
                        nextMask = Time.unscaledTime + 0.05f;
                        PaintMask(at, radius, frame);
                        RenderAsVictim(victim, at + LightOffset, radius);
                    }
                    if (!overlay.activeSelf) overlay.SetActive(true);
                    string name = victim.Data?.PlayerName ?? "";
                    if (caption != null && caption.text != name) caption.text = name;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[HypnotistView] tick failed: {e.Message}");
                    Hide();
                }
            }
        }

        // The game hides every player and body outside one's light completely; the dimmed map stays.
        // The camera therefore renders by hand (20 per second, with the mask) and switches the
        // renderers of whatever the victim cannot see off for exactly that one render.
        private static readonly List<Renderer> hiddenForShot = new List<Renderer>();

        private static void RenderAsVictim(PlayerControl victim, Vector2 eye, float radius) {
            hiddenForShot.Clear();
            try {
                foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                    if (p == null || p == victim || p.Data == null || p.Data.IsDead) continue;
                    if (CanSee(eye, radius, p.transform.position)) continue;
                    HideRenderers(p.gameObject);
                }
                foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                    if (b != null && !CanSee(eye, radius, b.TruePosition)) HideRenderers(b.gameObject);
                cam.Render();
            } finally {
                foreach (var r in hiddenForShot) if (r != null) r.enabled = true;
                hiddenForShot.Clear();
            }
        }

        private static void HideRenderers(GameObject go) {
            foreach (var r in go.GetComponentsInChildren<Renderer>())
                if (r != null && r.enabled) { r.enabled = false; hiddenForShot.Add(r); }
        }

        private static bool CanSee(Vector2 eye, float r, Vector2 target) {
            float reach = r + 0.25f;    // half a body: an edge already shows
            if ((target - eye).sqrMagnitude > reach * reach) return false;
            return !PhysicsHelpers.AnythingBetween(eye, target, Constants.ShadowMask, false);
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

            var dark = new GameObject("Darkness");
            dark.transform.SetParent(mask.transform, false);
            var drt = dark.AddComponent<RectTransform>();
            drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.sizeDelta = Vector2.zero;
            var draw = dark.AddComponent<RawImage>();
            draw.texture = MaskTexture();
            draw.raycastTarget = false;

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

        private static Texture2D MaskTexture() {
            if (maskTex != null) return maskTex;
            maskTex = new Texture2D(MaskPx, MaskPx, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            maskTex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            maskPx = new Color32[MaskPx * MaskPx];
            return maskTex;
        }

        // The victim's light: Rays rays against the light's own shadow mask, then every texel of the
        // frame is dark unless it lies within the radius AND in front of the wall in its direction.
        // A wall's own face stays lit (WallBias), as with the game's light.
        private static void PaintMask(Vector2 centre, float radius, float frame) {
            if (MaskTexture() == null) return;
            Vector2 eye = centre + LightOffset;
            int mask = Constants.ShadowMask;
            for (int k = 0; k < Rays; k++) {
                float a = k * Mathf.PI * 2f / Rays;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var hit = Physics2D.Raycast(eye, dir, radius, mask);
                rayLen[k] = hit.collider != null ? Mathf.Min(radius, hit.distance + WallBias) : radius;
            }
            float half = MaskPx / 2f, unit = frame / half, rr = Mathf.Max(0.05f, radius);
            for (int y = 0; y < MaskPx; y++) {
                for (int x = 0; x < MaskPx; x++) {
                    float dx = (x + 0.5f - half) * unit - LightOffset.x, dy = (y + 0.5f - half) * unit - LightOffset.y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float f = (Mathf.Atan2(dy, dx) / (Mathf.PI * 2f) + 1f) % 1f * Rays;
                    int k0 = (int)f % Rays, k1 = (k0 + 1) % Rays;
                    float limit = Mathf.Lerp(rayLen[k0], rayLen[k1], f - Mathf.Floor(f));
                    float lit = Mathf.Clamp01((limit - d) / Edge);
                    float a = Dark * Mathf.Lerp(1f, Mathf.Clamp01(d / rr), lit);    // linear falloff inside
                    maskPx[y * MaskPx + x] = new Color32(0, 0, 0, (byte)(a * 255f));
                }
            }
            maskTex.SetPixels32(maskPx);
            maskTex.Apply(false, false);
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
