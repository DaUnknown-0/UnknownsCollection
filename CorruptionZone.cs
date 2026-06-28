// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * CorruptionZone - the Corrupter's hallucination field (client-side FX, no netcode).
 *
 * A zone is an invisible disc placed at a body. While the LOCAL crewmate stands inside it, a handful of
 * "fake players" - frozen clones of random real players' cosmetics - drift around and flicker, so the
 * crew "sees people where they aren't". The figures are pure local visuals: every client builds its own
 * copy and only shows them to a living non-impostor standing in the zone. Impostors don't see them
 * (unless the option says so).
 *
 * Figures are made by cloning a real player's CosmeticsLayer GameObject (Object.Instantiate). Everything
 * is heavily guarded: if a clone fails, the zone just shows fewer/no figures (graceful, never fatal).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class CorruptionZone {
        private class Figure {
            public GameObject go;
            public SpriteRenderer[] renderers;
            public Vector2 home;     // anchor inside the zone
            public Vector2 target;   // current wander goal
            public float seed;       // flicker phase offset
        }
        private class Zone {
            public Vector2 center;
            public float expiry;     // Time.time at which it vanishes
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

                // Clone sources: any real players (prefer the living, fall back to all).
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

        private static Figure MakeFigure(PlayerControl src, Vector2 center, float radius, int idx) {
            try {
                var clone = UnityEngine.Object.Instantiate(src.cosmetics.gameObject);
                clone.name = "CorruptionFigure";
                // Disable behaviours that would try to drive the clone (animators etc.) - keep only visuals.
                foreach (var beh in clone.GetComponentsInChildren<MonoBehaviour>(true)) {
                    try { beh.enabled = false; } catch { }
                }
                Vector2 home = center + UnityEngine.Random.insideUnitCircle * (radius * 0.5f);
                var f = new Figure {
                    go = clone,
                    renderers = clone.GetComponentsInChildren<SpriteRenderer>(true),
                    home = home,
                    target = home + UnityEngine.Random.insideUnitCircle * (radius * 0.4f),
                    seed = idx * 1.7f
                };
                SetPos(f, home);
                clone.SetActive(false);
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
