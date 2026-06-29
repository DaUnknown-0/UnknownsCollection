// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * IllusionistClone - the decoy clone (client-side FX, no netcode for movement).
 *
 * On playback every client builds its OWN clone and replays the recorded path. The clone wears a
 * Medic-shield outline so it reads as a protected player, and it CANNOT die (it is not a real player).
 * The "kill attempt -> shield flash" interaction lives in Illusionist.cs (KillButton.DoClick).
 *
 * Rendering: we do NOT clone the live CosmeticsLayer GameObject (instantiating an active object runs the
 * cloned MonoBehaviours' Awake/OnEnable, which re-initializes cosmetics and snaps the hat to a default
 * scale). Instead we SNAPSHOT the player's visible SpriteRenderers (body, hat, visor, skin) into fresh
 * GameObjects, forcing maskInteraction = None (the body/visor/skin normally render only INSIDE the
 * player's sight mask, so a detached copy would be invisible except the unmasked hat - that was the
 * "only the hat shows" bug).
 *
 * The clone then mirrors EVERYTHING about the live Illusionist each frame so disguises read correctly:
 *   - appearance: sprite + material colors are copied from the live player, so Camouflage, the Fungle
 *     Mushroom-Mixup sabotage and any other look change are reflected automatically (frozen only while
 *     the live player is dead/vented/hidden, so the clone never copies an invisible state);
 *   - facing: derived from the clone's own movement direction (the recorded path), via a root scale flip;
 *   - venting: the recording stores an in-vent flag per sample; on replay the clone shrinks into / pops
 *     out of the vent at those points instead of sitting on top of it.
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
        private static SpriteRenderer[] renderers;   // clone renderers
        private static SpriteRenderer[] sources;     // live source renderers, parallel to `renderers`
        private static PlayerControl src;            // the live Illusionist we mirror

        private static List<Vector2> path = new();
        private static List<bool> vents = new();     // per-sample "in a vent" flag, parallel to `path`
        private static float startTime;
        private static float interval;
        private static bool active;

        private static Vector2 currentPos;           // current path point (feet / TruePosition), used by the kill intercept
        private static Vector3 anchorOffset;         // constant body-vs-feet visual offset, baked at spawn
        private static float flashUntil;             // shield-flash highlight end time
        private static float facingSign = 1f;        // +1 facing right, -1 facing left (from movement)
        private static float ventScale = 1f;         // 1 = out, 0 = fully inside the vent (shrink animation)

        private const float VentTween = 0.22f;       // seconds to shrink into / grow out of a vent
        private const float FaceEps = 0.012f;        // ignore tiny horizontal jitter when picking a facing

        public static bool IsActive() => active && go != null;
        public static Vector2 Position() => currentPos;

        // ---- Spawn the clone and start replaying `points` (+ `ventFlags`) over points.Count*interval s ----
        public static void Spawn(List<Vector2> points, List<bool> ventFlags, float sampleInterval) {
            try {
                Despawn();
                if (points == null || points.Count == 0) return;
                src = Illusionist.illusionist;
                if (src == null || src.cosmetics == null) return;
                var cos = src.cosmetics;

                // Anchor on the body sprite so the clone reproduces the exact body-above-feet offset.
                var bodyRend = cos.currentBodySprite != null ? cos.currentBodySprite.BodySprite : null;
                Vector3 bodyWorld = bodyRend != null ? bodyRend.transform.position : cos.transform.position;
                Vector2 trueNow = src.GetTruePosition();
                anchorOffset = bodyWorld - new Vector3(trueNow.x, trueNow.y, 0f);

                // Collect the visible source renderers (deduped): body first (in case it is not a child of
                // the cosmetics layer), then the rest (hat front/back, visor, skin).
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
                if (srcList.Count == 0) return;

                go = new GameObject("IllusionistClone");
                var built = new List<SpriteRenderer>(srcList.Count);
                foreach (var sr in srcList) {
                    var child = new GameObject(sr.name);
                    child.transform.SetParent(go.transform, false);
                    // go carries facing/vent scale; children sit at their world offset from the body anchor.
                    child.transform.localPosition = sr.transform.position - bodyWorld;
                    child.transform.localRotation = sr.transform.rotation;
                    child.transform.localScale = sr.transform.lossyScale;

                    var cr = child.AddComponent<SpriteRenderer>();
                    cr.sprite = sr.sprite;
                    try { cr.material = new Material(sr.material); } catch { cr.sharedMaterial = sr.sharedMaterial; }
                    cr.color = sr.color;
                    cr.flipX = false;                   // facing is handled by the root scale, not per-sprite
                    cr.flipY = sr.flipY;
                    cr.sortingLayerID = sr.sortingLayerID;
                    cr.sortingOrder = sr.sortingOrder;
                    cr.maskInteraction = SpriteMaskInteraction.None; // the clone lives outside the sight mask
                    built.Add(cr);
                }
                renderers = built.ToArray();
                sources = srcList.ToArray();

                path = new List<Vector2>(points);
                vents = ventFlags != null ? new List<bool>(ventFlags) : new List<bool>();
                interval = Mathf.Max(sampleInterval, 0.02f);
                startTime = Time.time;
                ventScale = (vents.Count > 0 && vents[0]) ? 0f : 1f;
                facingSign = InitialFacing();
                currentPos = path[0];
                ApplyTransform(currentPos);
                go.SetActive(true);
                active = true;
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Illusionist] clone spawned, {path.Count} points over {path.Count * interval:F1}s, {renderers.Length} renderers.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] clone spawn failed: {e}");
                Despawn();
            }
        }

        public static void Flash(float seconds) => flashUntil = Time.time + seconds;

        // ---- Per-frame replay + appearance mirror + shield outline (HudManager.Update) ----
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

                // Facing follows the clone's own movement direction.
                float dx = b.x - a.x;
                if (dx > FaceEps) facingSign = 1f;
                else if (dx < -FaceEps) facingSign = -1f;

                // Vent shrink/grow: target 0 while the recorded sample was in a vent, else 1.
                bool inVent = i < vents.Count && vents[i];
                ventScale = Mathf.MoveTowards(ventScale, inVent ? 0f : 1f, Time.deltaTime / VentTween);

                ApplyTransform(currentPos);
                MirrorAppearance();
                ApplyOutline();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] clone update failed: {e}");
                Despawn();
            }
        }

        // Copy the live Illusionist's current look (Camouflage, Mushroom-Mixup, disguises, walk frame).
        // Skipped while the live player is gone/dead/vented/hidden, so we never copy an invisible state.
        private static void MirrorAppearance() {
            if (renderers == null || sources == null) return;
            bool srcGood = src != null && src.cosmetics != null && src.Data != null
                           && !src.Data.IsDead && !src.inVent;
            if (!srcGood) return;
            for (int k = 0; k < renderers.Length && k < sources.Length; k++) {
                var s = sources[k];
                var c = renderers[k];
                if (s == null || c == null) continue;
                c.sprite = s.sprite;
                c.color = s.color;
                c.flipX = false;
                try { c.material.CopyPropertiesFromMaterial(s.material); } catch { }
            }
        }

        private static void ApplyOutline() {
            if (renderers == null) return;
            Color outline = Time.time < flashUntil ? Color.white : Medic.shieldedColor;
            foreach (var r in renderers) {
                if (r == null) continue;
                var c = r.color; c.a = 1f; r.color = c;
                try {
                    r.material.SetFloat("_Outline", 1f);
                    r.material.SetColor("_OutlineColor", outline);
                } catch { }
            }
        }

        private static void ApplyTransform(Vector2 p) {
            if (go == null) return;
            go.transform.position = new Vector3(p.x + anchorOffset.x, p.y + anchorOffset.y, p.y / 1000f + 0.001f);
            go.transform.localScale = new Vector3(facingSign * ventScale, ventScale, 1f);
        }

        // Pick the starting facing from the first noticeable horizontal move in the path.
        private static float InitialFacing() {
            for (int i = 0; i + 1 < path.Count; i++) {
                float dx = path[i + 1].x - path[i].x;
                if (dx > FaceEps) return 1f;
                if (dx < -FaceEps) return -1f;
            }
            return 1f;
        }

        public static void Despawn() {
            active = false;
            if (go != null) { try { UnityEngine.Object.Destroy(go); } catch { } go = null; }
            renderers = null;
            sources = null;
            src = null;
        }
    }
}
