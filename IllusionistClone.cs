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
 * The clone then behaves like a real player:
 *   - body animation: a SpriteAnim on the body plays the vanilla Idle / Run / EnterVent / ExitVent clips,
 *     driven by the CLONE's own movement along the recorded path (not the live player's movement). If the
 *     clips cannot be wired up it falls back to mirroring the live player's body sprite + a shrink vent.
 *   - appearance: cosmetic sprites + material colors are copied from the live player each frame, so
 *     Camouflage, the Fungle Mushroom-Mixup sabotage and any other look change are reflected (frozen only
 *     while the live player is dead/vented/hidden, so the clone never copies an invisible state);
 *   - facing: derived from the clone's own movement direction, via a root scale flip;
 *   - shield glow: gated by the "Clone Shield Visible To Everyone" option (and hidden during camouflage,
 *     mirroring how vanilla hides outlines), so it is not always a give-away to the crew.
 *
 * One clone at a time: a new playback replaces the old. Everything guarded; a failed clone is a no-op.
 */

using System;
using System.Collections.Generic;
using PowerTools;
using UnityEngine;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class IllusionistClone {
        private static GameObject go;
        private static SpriteRenderer[] renderers;   // clone renderers
        private static SpriteRenderer[] sources;     // live source renderers, parallel to `renderers`
        private static PlayerControl src;            // the live Illusionist we mirror

        private static SpriteRenderer bodyClone;     // the clone's body renderer (driven by SpriteAnim)
        private static SpriteAnim bodyAnim;          // plays the vanilla Idle/Run/EnterVent/ExitVent clips
        private static AnimationClip idleClip, runClip, enterClip, exitClip;
        private static bool useAnim;                 // true once the SpriteAnim + clips are confirmed working

        private static List<Vector2> path = new();
        private static List<bool> vents = new();     // per-sample "in a vent" flag, parallel to `path`
        private static float startTime;
        private static float interval;
        private static bool active;

        private static Vector2 currentPos;           // current path point (feet / TruePosition), used by the kill intercept
        private static Vector3 anchorOffset;         // constant body-vs-feet visual offset, baked at spawn
        private static float flashUntil;             // shield-flash highlight end time
        private static float facingSign = 1f;        // +1 facing right, -1 facing left (from movement)

        private enum VentPhase { Out, Entering, In, Exiting }
        private static VentPhase ventPhase = VentPhase.Out;
        private enum BodyState { None, Idle, Run }
        private static BodyState bodyState = BodyState.None;
        private static float ventScale = 1f;         // fallback shrink (only used when the clips are missing)

        private const float VentTween = 0.22f;       // fallback shrink duration
        private const float FaceEps = 0.012f;        // ignore tiny horizontal jitter when picking a facing
        private const float MoveSpeedThresh = 0.5f;  // units/s above which the clone plays the run animation

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
                int bodyIdx = -1;
                foreach (var sr in srcList) {
                    if (sr == bodyRend) bodyIdx = built.Count;
                    var child = new GameObject(sr.name);
                    child.transform.SetParent(go.transform, false);
                    // go carries facing (and the fallback vent scale); children sit at their world offset.
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

                // Drive the body with the vanilla animation clips so it walks on its OWN movement.
                bodyClone = bodyIdx >= 0 ? renderers[bodyIdx] : null;
                WireBodyAnimation();

                path = new List<Vector2>(points);
                vents = ventFlags != null ? new List<bool>(ventFlags) : new List<bool>();
                interval = Mathf.Max(sampleInterval, 0.02f);
                startTime = Time.time;
                ventPhase = (vents.Count > 0 && vents[0]) ? VentPhase.In : VentPhase.Out;
                ventScale = ventPhase == VentPhase.In && !useAnim ? 0f : 1f;
                if (ventPhase == VentPhase.In) SetVisibleAll(false);
                bodyState = BodyState.None;
                facingSign = InitialFacing();
                currentPos = path[0];
                ApplyTransform(currentPos);
                go.SetActive(true);
                active = true;
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Illusionist] clone spawned, {path.Count} points over {path.Count * interval:F1}s, {renderers.Length} renderers, anim={useAnim}.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] clone spawn failed: {e}");
                Despawn();
            }
        }

        // Add a SpriteAnim to the body and confirm it actually plays. If anything is missing we fall back
        // to mirroring the live body sprite (which animates off the live player) + a shrink vent.
        private static void WireBodyAnimation() {
            try {
                var anims = src != null && src.MyPhysics != null ? src.MyPhysics.Animations : null;
                if (anims != null && anims.group != null) {
                    idleClip = anims.group.IdleAnim;
                    runClip = anims.group.RunAnim;
                    enterClip = anims.group.EnterVentAnim;
                    exitClip = anims.group.ExitVentAnim;
                }
                if (bodyClone == null || idleClip == null || runClip == null) { useAnim = false; return; }
                bodyClone.gameObject.AddComponent<Animator>();
                bodyAnim = bodyClone.gameObject.AddComponent<SpriteAnim>();
                bodyAnim.Play(idleClip, 1f);
                useAnim = bodyAnim.Playing;   // verify it really animates on this detached object
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Illusionist] body-anim wiring failed, mirroring live body instead: {e}");
                useAnim = false;
            }
        }

        public static void Flash(float seconds) => flashUntil = Time.time + seconds;

        // ---- Per-frame replay + body animation + appearance mirror + shield outline (HudManager.Update) ----
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

                // Facing + locomotion are both derived from the clone's OWN movement along the path.
                float dx = b.x - a.x;
                if (dx > FaceEps) facingSign = 1f;
                else if (dx < -FaceEps) facingSign = -1f;
                bool moving = Vector2.Distance(a, b) / interval > MoveSpeedThresh;
                bool inVent = i < vents.Count && vents[i];

                UpdateVent(inVent, moving);
                ApplyTransform(currentPos);
                if (ventPhase == VentPhase.Out) MirrorAppearance();
                ApplyOutline();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] clone update failed: {e}");
                Despawn();
            }
        }

        // Vent state machine + locomotion. With the clips the body plays the exact EnterVent/ExitVent and
        // Idle/Run animations; the other cosmetics hide while inside the vent. Without the clips it falls
        // back to shrinking the whole clone into the vent.
        private static void UpdateVent(bool inVent, bool moving) {
            if (!useAnim) {
                ventPhase = inVent ? VentPhase.In : VentPhase.Out;
                ventScale = Mathf.MoveTowards(ventScale, inVent ? 0f : 1f, Time.deltaTime / VentTween);
                return;
            }

            switch (ventPhase) {
                case VentPhase.Out:
                    if (inVent) { StartEnter(); break; }
                    PlayLocomotion(moving);
                    break;
                case VentPhase.Entering:
                    if (!inVent) { StartExit(); break; }
                    if (!IsBodyAnimPlaying()) { ventPhase = VentPhase.In; SetVisibleAll(false); }
                    break;
                case VentPhase.In:
                    if (!inVent) StartExit();
                    break;
                case VentPhase.Exiting:
                    if (inVent) { StartEnter(); break; }
                    if (!IsBodyAnimPlaying()) { ventPhase = VentPhase.Out; SetVisibleAll(true); bodyState = BodyState.None; }
                    break;
            }
        }

        private static void PlayLocomotion(bool moving) {
            var want = moving ? BodyState.Run : BodyState.Idle;
            if (want == bodyState) return;
            bodyState = want;
            try { bodyAnim.Play(want == BodyState.Run ? runClip : idleClip, 1f); } catch { }
        }

        private static void StartEnter() {
            ventPhase = VentPhase.Entering;
            bodyState = BodyState.None;
            bodyClone.gameObject.SetActive(true);
            SetCosmeticsVisible(false);          // hat/visor/skin disappear into the vent with the body
            try { bodyAnim.Play(enterClip != null ? enterClip : idleClip, 1f); } catch { }
        }

        private static void StartExit() {
            ventPhase = VentPhase.Exiting;
            bodyState = BodyState.None;
            bodyClone.gameObject.SetActive(true);
            SetCosmeticsVisible(false);          // cosmetics reappear only once the body is back out
            try { bodyAnim.Play(exitClip != null ? exitClip : idleClip, 1f); } catch { }
        }

        private static void SetVisibleAll(bool on) {
            if (renderers == null) return;
            foreach (var r in renderers) if (r != null) r.gameObject.SetActive(on);
        }

        private static void SetCosmeticsVisible(bool on) {
            if (renderers == null) return;
            foreach (var r in renderers) {
                if (r == null || r == bodyClone) continue;
                r.gameObject.SetActive(on);
            }
        }

        private static bool IsBodyAnimPlaying() {
            try { return bodyAnim != null && bodyAnim.Playing; } catch { return false; }
        }

        // Copy the live Illusionist's current look (Camouflage, Mushroom-Mixup, disguises). The body sprite
        // itself is owned by the SpriteAnim when clips are active, so for the body we copy only the material
        // (colors); the cosmetics copy sprite + material + color. Skipped while the live player is
        // gone/dead/vented/hidden, so we never copy an invisible state.
        private static void MirrorAppearance() {
            if (renderers == null || sources == null) return;
            bool srcGood = src != null && src.cosmetics != null && src.Data != null
                           && !src.Data.IsDead && !src.inVent;
            if (!srcGood) return;
            for (int k = 0; k < renderers.Length && k < sources.Length; k++) {
                var s = sources[k];
                var c = renderers[k];
                if (s == null || c == null) continue;
                bool isBody = c == bodyClone;
                if (!(isBody && useAnim)) c.sprite = s.sprite;   // body sprite is animation-driven
                c.color = s.color;
                c.flipX = false;
                try { c.material.CopyPropertiesFromMaterial(s.material); } catch { }
            }
        }

        private static void ApplyOutline() {
            if (renderers == null) return;
            bool show = ShouldShowShield();
            Color outline = Time.time < flashUntil ? Color.white : Medic.shieldedColor;
            foreach (var r in renderers) {
                if (r == null || !r.gameObject.activeSelf) continue;
                var c = r.color; c.a = 1f; r.color = c;
                try {
                    r.material.SetFloat("_Outline", show ? 1f : 0f);
                    if (show) r.material.SetColor("_OutlineColor", outline);
                } catch { }
            }
        }

        // The shield glow honors the "Clone Shield Visible To Everyone" option and is hidden during
        // camouflage / mushroom sabotage (mirroring how vanilla suppresses outlines). When the option is
        // off, only the Illusionist, impostors and ghosts see it - the crew sees a normal-looking player.
        private static bool ShouldShowShield() {
            try {
                if (Camouflager.camouflageTimer > 0f || Helpers.MushroomSabotageActive()) return false;
                if (Illusionist.ShieldVisibleAll == null || Illusionist.ShieldVisibleAll.getBool()) return true;
                var lp = PlayerControl.LocalPlayer;
                if (lp == null) return true;
                if (Illusionist.illusionist != null && lp.PlayerId == Illusionist.illusionist.PlayerId) return true;
                if (lp.Data != null && lp.Data.IsDead) return true;
                if (lp.Data != null && lp.Data.Role != null && lp.Data.Role.IsImpostor) return true;
                return false;
            } catch { return true; }
        }

        private static void ApplyTransform(Vector2 p) {
            if (go == null) return;
            go.transform.position = new Vector3(p.x + anchorOffset.x, p.y + anchorOffset.y, p.y / 1000f + 0.001f);
            float sx = facingSign, sy = 1f;
            if (!useAnim) { sx *= ventScale; sy = ventScale; }   // fallback shrink
            go.transform.localScale = new Vector3(sx, sy, 1f);
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
            bodyClone = null;
            bodyAnim = null;
            idleClip = runClip = enterClip = exitClip = null;
            useAnim = false;
            ventPhase = VentPhase.Out;
            bodyState = BodyState.None;
            ventScale = 1f;
        }
    }
}
