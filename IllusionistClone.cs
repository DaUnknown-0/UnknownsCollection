// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * IllusionistClone - the decoy clone (client-side FX, no netcode for movement).
 *
 * On playback every client builds its OWN clone by cloning the Illusionist's CosmeticsLayer GameObject
 * and replaying the recorded path (a list of points sampled at a fixed interval). The clone wears a
 * Medic-shield outline so it reads as a protected player, and it CANNOT die (it is not a real player).
 * The "kill attempt -> shield flash" interaction lives in Illusionist.cs (KillButton.DoClick).
 *
 * One clone at a time: a new playback replaces the old. Everything guarded; a failed clone is a no-op.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class IllusionistClone {
        private static GameObject go;
        private static SpriteRenderer[] renderers;
        private static List<Vector2> path = new();
        private static float startTime;
        private static float interval;
        private static bool active;
        private static Vector2 currentPos;
        private static float flashUntil; // shield-flash highlight end time

        public static bool IsActive() => active && go != null;
        public static Vector2 Position() => currentPos;

        // ---- Spawn the clone and start replaying `points` over points.Count * sampleInterval seconds ----
        public static void Spawn(List<Vector2> points, float sampleInterval) {
            try {
                Despawn();
                if (points == null || points.Count == 0) return;
                var src = Illusionist.illusionist;
                if (src == null || src.cosmetics == null || src.cosmetics.gameObject == null) return;

                go = UnityEngine.Object.Instantiate(src.cosmetics.gameObject);
                go.name = "IllusionistClone";
                foreach (var beh in go.GetComponentsInChildren<MonoBehaviour>(true)) {
                    try { beh.enabled = false; } catch { }
                }
                renderers = go.GetComponentsInChildren<SpriteRenderer>(true);

                path = new List<Vector2>(points);
                interval = Mathf.Max(sampleInterval, 0.02f);
                startTime = Time.time;
                currentPos = path[0];
                SetPos(currentPos);
                go.SetActive(true);
                active = true;
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Illusionist] clone spawned, {path.Count} points over {path.Count * interval:F1}s.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] clone spawn failed: {e}");
                Despawn();
            }
        }

        public static void Flash(float seconds) => flashUntil = Time.time + seconds;

        // ---- Per-frame replay + shield outline (HudManager.Update) ----
        public static void Update() {
            if (!active || go == null) return;
            try {
                float t = Time.time - startTime;
                float fIdx = t / interval;
                int i = Mathf.FloorToInt(fIdx);
                if (i >= path.Count - 1) {
                    // Reached the end of the recording -> the illusion fades.
                    Despawn();
                    return;
                }
                Vector2 a = path[i];
                Vector2 b = path[i + 1];
                currentPos = Vector2.Lerp(a, b, fIdx - i);
                SetPos(currentPos);

                // Medic-shield outline (white flash briefly when a kill is "blocked").
                Color outline = Time.time < flashUntil ? Color.white : Medic.shieldedColor;
                if (renderers != null)
                    foreach (var r in renderers) {
                        if (r == null) continue;
                        var c = r.color; c.a = 1f; r.color = c;
                        try {
                            r.material.SetFloat("_Outline", 1f);
                            r.material.SetColor("_OutlineColor", outline);
                        } catch { }
                    }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] clone update failed: {e}");
                Despawn();
            }
        }

        private static void SetPos(Vector2 p) {
            if (go == null) return;
            go.transform.position = new Vector3(p.x, p.y, p.y / 1000f + 0.001f);
        }

        public static void Despawn() {
            active = false;
            if (go != null) { try { UnityEngine.Object.Destroy(go); } catch { } go = null; }
            renderers = null;
        }
    }
}
