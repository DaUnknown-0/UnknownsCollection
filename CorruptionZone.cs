// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * CorruptionZone - the Corrupter's hallucination field (client-side FX, no netcode).
 *
 * A zone is an invisible disc placed at a body. While the LOCAL crewmate stands inside it, a handful of
 * "fake players" - snapshot clones of random real players' cosmetics - drift around and flicker, so the
 * crew "sees people where they aren't". The figures are pure local visuals: every client builds its own
 * copy and only shows them to a living non-impostor standing in the zone. Impostors don't see them
 * (unless the option says so).
 *
 * RENDERING mirrors IllusionistClone: instead of cloning the entire CosmeticsLayer GameObject (which
 * runs Awake/OnEnable on the child MonoBehaviours, breaking hats and custom materials), we SNAPSHOT the
 * visible SpriteRenderers (body, hat front/back, visor, skin) into fresh GameObjects with
 * maskInteraction = None. This fixes hat rendering and works with color-blind mode.
 *
 * Everything is heavily guarded: if a figure fails, the zone just shows fewer/no figures (graceful,
 * never fatal).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using InnerNet;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class CorruptionZone {
        private class Figure {
            public GameObject go;                    // root GameObject for this figure
            public SpriteRenderer[] renderers;        // clone renderers (parallel to sources)
            public SpriteRenderer[] sources;          // live source renderers this figure mirrors
            public PlayerControl srcPlayer;           // the real player we mirror
            public Vector2 home;                      // anchor inside the zone
            public Vector2 target;                    // current wander goal
            public float seed;                        // flicker phase offset
            public TextMeshPro colorBlindLabel;       // color-blind name text (optional)
        }

        private class Zone {
            public Vector2 center;
            public float expiry;                      // Time.time at which it vanishes
            public readonly List<Figure> figures = new();
        }

        private static readonly List<Zone> zones = new();

        public static int ActiveCount => zones.Count;

        // ---- Place a new zone (called on every client) ----
        public static void Place(float x, float y) {
            try {
                // Enforce the max-active-zones cap by retiring the oldest.
                int max = Corrupter.MaxZonesValue();
                while (zones.Count >= max && zones.Count > 0) { DestroyZone(zones[0]); zones.RemoveAt(0); }

                var z = new Zone { center = new Vector2(x, y), expiry = Time.time + Corrupter.ZoneDurationValue() };
                int n = Corrupter.FiguresPerZoneValue();
                float r = Corrupter.ZoneRadiusValue();

                // Snapshot sources: any real players (prefer the living, fall back to all).
                var sources = PlayerControl.AllPlayerControls.ToArray()
                    .Where(p => p != null && p.cosmetics != null && p.cosmetics.gameObject != null).ToList();
                if (sources.Count == 0) { zones.Add(z); return; }

                for (int i = 0; i < n; i++) {
                    var src = sources[UnityEngine.Random.Range(0, sources.Count)];
                    var fig = MakeFigure(src, z.center, r, i);
                    if (fig != null) z.figures.Add(fig);
                }
                zones.Add(z);
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Corrupter] zone placed at ({x:F1},{y:F1}) with {z.figures.Count} figures.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Corrupter] Place failed: {e}");
            }
        }

        // ---- Snapshot-based figure construction (mirrors IllusionistClone.Spawn) ----
        private static Figure MakeFigure(PlayerControl src, Vector2 center, float radius, int idx) {
            try {
                if (src == null || src.cosmetics == null) return null;
                var cos = src.cosmetics;

                // Collect the visible source renderers (deduped): body first, then hat/visor/skin.
                var bodyRend = cos.currentBodySprite?.BodySprite;
                var seen = new HashSet<SpriteRenderer>();
                var srcList = new List<SpriteRenderer>();
                void tryAdd(SpriteRenderer sr) {
                    if (sr == null || sr.sprite == null) return;
                    if (!sr.gameObject.activeInHierarchy || !sr.enabled) return;
                    if (!seen.Add(sr)) return;
                    srcList.Add(sr);
                }
                tryAdd(bodyRend);
                foreach (var sr in cos.GetComponentsInChildren<SpriteRenderer>(false)) tryAdd(sr);
                if (srcList.Count == 0) return null;

                Vector3 bodyWorld = bodyRend != null ? bodyRend.transform.position : cos.transform.position;

                var root = new GameObject("CorruptionFigure");
                root.SetActive(false); // stay hidden until inside the zone

                var built = new List<SpriteRenderer>(srcList.Count);
                foreach (var sr in srcList) {
                    var child = new GameObject(sr.name);
                    child.transform.SetParent(root.transform, false);
                    child.transform.localPosition = sr.transform.position - bodyWorld;
                    child.transform.localRotation = sr.transform.rotation;
                    child.transform.localScale = sr.transform.lossyScale;

                    var cr = child.AddComponent<SpriteRenderer>();
                    cr.sprite = sr.sprite;
                    try { cr.material = new Material(sr.material); } catch { cr.sharedMaterial = sr.sharedMaterial; }
                    cr.color = sr.color;
                    cr.flipX = false;
                    cr.flipY = sr.flipY;
                    cr.sortingLayerID = sr.sortingLayerID;
                    cr.sortingOrder = sr.sortingOrder;
                    cr.maskInteraction = SpriteMaskInteraction.None; // render outside the sight mask
                    built.Add(cr);
                }

                Vector2 home = center + UnityEngine.Random.insideUnitCircle * (radius * 0.5f);
                var f = new Figure {
                    go = root,
                    renderers = built.ToArray(),
                    sources = srcList.ToArray(),
                    srcPlayer = src,
                    home = home,
                    target = home + UnityEngine.Random.insideUnitCircle * (radius * 0.4f),
                    seed = idx * 1.7f
                };

                // Color-blind mode: clone the source player's color-blind name text
                if (DataManager.Settings.Accessibility.ColorBlindMode && cos.colorBlindText != null) {
                    var cbLabel = UnityEngine.Object.Instantiate(cos.colorBlindText.gameObject, root.transform);
                    cbLabel.name = "ColorBlindLabel";
                    f.colorBlindLabel = cbLabel.GetComponent<TextMeshPro>();
                    if (f.colorBlindLabel != null) {
                        f.colorBlindLabel.text = cos.colorBlindText.text;
                        f.colorBlindLabel.transform.localPosition = new Vector3(0f, -0.4f, -0.01f);
                        f.colorBlindLabel.transform.localScale = Vector3.one * 0.5f;
                    }
                }

                SetPos(f, home);
                root.SetActive(false);
                return f;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Corrupter] MakeFigure failed: {e.Message}");
                return null;
            }
        }

        private static void SetPos(Figure f, Vector2 p) {
            if (f.go == null) return;
            f.go.transform.position = new Vector3(p.x, p.y, p.y / 1000f + 0.001f);
        }

        // ---- Per-frame: mirror appearance from the live source player ----
        private static void MirrorAppearance(Figure f) {
            if (f.renderers == null || f.sources == null) return;
            bool srcGood = f.srcPlayer != null && f.srcPlayer.cosmetics != null && f.srcPlayer.Data != null
                           && !f.srcPlayer.Data.IsDead && !f.srcPlayer.inVent;
            if (!srcGood) return;
            for (int k = 0; k < f.renderers.Length && k < f.sources.Length; k++) {
                var s = f.sources[k];
                var c = f.renderers[k];
                if (s == null || c == null) continue;
                c.sprite = s.sprite;
                c.color = s.color;
                c.flipX = false;
                try { c.material.CopyPropertiesFromMaterial(s.material); } catch { }
            }
        }

        // ---- Per-frame update (HudManager.Update) ----
        public static void Update() {
            if (zones.Count == 0) return;
            var me = PlayerControl.LocalPlayer;
            bool localAlive = me != null && me.Data != null && !me.Data.IsDead && !me.Data.Disconnected;
            bool localImpostor = me != null && me.Data != null && me.Data.Role != null && me.Data.Role.IsImpostor;
            bool viewerEligible = localAlive && (!localImpostor || Corrupter.ImpostorsSeeZonesValue());
            float radius = Corrupter.ZoneRadiusValue();
            float drift = Corrupter.DriftSpeedValue();

            for (int zi = zones.Count - 1; zi >= 0; zi--) {
                var z = zones[zi];
                if (Time.time >= z.expiry) { DestroyZone(z); zones.RemoveAt(zi); continue; }

                bool inside = viewerEligible && me != null
                              && Vector2.Distance(me.GetTruePosition(), z.center) <= radius;

                foreach (var f in z.figures) {
                    if (f.go == null) continue;
                    if (!inside) { if (f.go.activeSelf) f.go.SetActive(false); continue; }
                    if (!f.go.activeSelf) f.go.SetActive(true);

                    // Mirror appearance from the live source player each frame (so camo/mushroom mixup etc.
                    // are reflected on the figures). Skipped while the source is dead/vented.
                    MirrorAppearance(f);

                    // Slow random-walk drift within the zone.
                    Vector2 pos = f.go.transform.position;
                    Vector2 next = Vector2.MoveTowards(pos, f.target, drift * Time.deltaTime);
                    if (Vector2.Distance(next, f.target) < 0.05f)
                        f.target = f.home + UnityEngine.Random.insideUnitCircle * (radius * 0.45f);
                    SetPos(f, next);

                    // Flicker the figure's transparency.
                    float a = 0.35f + 0.35f * Mathf.PingPong(Time.time * (0.6f + drift) + f.seed, 1f);
                    if (f.renderers != null)
                        foreach (var r in f.renderers)
                            if (r != null) { var c = r.color; c.a = a; r.color = c; }
                    if (f.colorBlindLabel != null) {
                        var cb = f.colorBlindLabel.color; cb.a = a; f.colorBlindLabel.color = cb;
                    }
                }
            }
        }

        private static void DestroyZone(Zone z) {
            foreach (var f in z.figures)
                if (f.go != null) try { UnityEngine.Object.Destroy(f.go); } catch { }
            z.figures.Clear();
        }

        public static void Clear() {
            foreach (var z in zones) DestroyZone(z);
            zones.Clear();
        }
    }
}
