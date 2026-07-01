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
using TMPro;
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

        private static SpriteRenderer skinClone;     // the clone's skin ("pants") renderer
        private static SpriteAnim skinAnim;          // plays the skin's matching Idle/Run/EnterVent/ExitVent
        private static AnimationClip sIdle, sRun, sEnter, sExit;
        private static bool useSkinAnim;             // true once the skin SpriteAnim + clips are confirmed working

        private static TextMeshPro colorBlindSrc;     // live colorblind-mode color-name label (CosmeticsLayer.colorBlindText)
        private static TextMeshPro colorBlindClone;   // its standalone clone, kept in sync each frame

        private static Transform[] cosmeticTransforms; // transforms of cosmetics (hat, visor) that need to move with vents
        private static Vector3[] cosmeticOriginalPos;  // original localPosition for each cosmetic, parallel to cosmeticTransforms
        private static float ventAnimProgress = 0f;    // 0 (out) -> 1 (in) during enter; 1 (in) -> 0 (out) during exit

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
        private static float ventAnimStartTime = 0f; // Time.time when vent animation started

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

                var skinRend = cos.skin != null ? cos.skin.layer : null;

                go = new GameObject("IllusionistClone");
                var built = new List<SpriteRenderer>(srcList.Count);
                int bodyIdx = -1, skinIdx = -1;
                foreach (var sr in srcList) {
                    if (sr == bodyRend) bodyIdx = built.Count;
                    if (skinRend != null && sr == skinRend) skinIdx = built.Count;
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

                // Drive the body (and skin) with the vanilla animation clips so they walk on the clone's
                // OWN movement instead of the live player's.
                bodyClone = bodyIdx >= 0 ? renderers[bodyIdx] : null;
                skinClone = skinIdx >= 0 ? renderers[skinIdx] : null;
                WireBodyAnimation();
                WireSkinAnimation(cos);

                // Identify cosmetics (anything that's not body or skin) that need to move during vent animations
                var cosmeticList = new List<Transform>();
                var cosmeticPosList = new List<Vector3>();
                for (int k = 0; k < renderers.Length; k++) {
                    if (renderers[k] == bodyClone || renderers[k] == skinClone) continue;
                    cosmeticList.Add(renderers[k].transform);
                    cosmeticPosList.Add(renderers[k].transform.localPosition);
                }
                cosmeticTransforms = cosmeticList.ToArray();
                cosmeticOriginalPos = cosmeticPosList.ToArray();

                // Colorblind-mode color-name label: not a SpriteRenderer, so it is invisible to the snapshot
                // above. It is a standalone leaf TextMeshPro object (unlike CosmeticsLayer/PlayerControl, its
                // Awake/OnEnable do not re-initialize cosmetics), so cloning the live GameObject directly is
                // safe here. Visibility/text are re-synced every frame from the live label in MirrorAppearance().
                try {
                    colorBlindSrc = cos.colorBlindText;
                    if (colorBlindSrc != null) {
                        var textGo = UnityEngine.Object.Instantiate(colorBlindSrc.gameObject, go.transform);
                        textGo.transform.localPosition = colorBlindSrc.transform.position - bodyWorld;
                        textGo.transform.localRotation = colorBlindSrc.transform.rotation;
                        textGo.transform.localScale = colorBlindSrc.transform.lossyScale;
                        colorBlindClone = textGo.GetComponent<TextMeshPro>();
                        textGo.SetActive(false); // MirrorAppearance() turns it on if/when the option is live
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Illusionist] colorblind-text clone failed: {e}");
                    colorBlindSrc = null;
                    colorBlindClone = null;
                }

                path = new List<Vector2>(points);
                vents = ventFlags != null ? new List<bool>(ventFlags) : new List<bool>();
                // A 1-sample recording has no second point to interpolate towards: Update() would see
                // i (0) >= path.Count - 1 (0) on the very first frame and despawn before the clone is ever
                // visible. Duplicate the single sample so there is always at least one interval-long segment
                // to play back (a static "clone" for one interval, then despawn as usual).
                if (path.Count < 2) {
                    path.Add(path[0]);
                    if (vents.Count > 0) vents.Add(vents[0]);
                }
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
                UCAssets.PlayCloneShimmer(currentPos);
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

        // Same idea for the skin ("pants"), which has its own animation that must run in lock-step with the
        // body - otherwise the legs move under static trousers.
        private static void WireSkinAnimation(CosmeticsLayer cos) {
            try {
                if (skinClone == null) { useSkinAnim = false; return; }
                SkinViewData view = null;
                try { view = cos.GetSkinView(); } catch { }
                if (view == null) { useSkinAnim = false; return; }
                sIdle = view.IdleAnim; sRun = view.RunAnim; sEnter = view.EnterVentAnim; sExit = view.ExitVentAnim;
                if (sIdle == null || sRun == null) { useSkinAnim = false; return; }
                skinClone.gameObject.AddComponent<Animator>();
                skinAnim = skinClone.gameObject.AddComponent<SpriteAnim>();
                skinAnim.Play(sIdle, 1f);
                useSkinAnim = skinAnim.Playing;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Illusionist] skin-anim wiring failed: {e}");
                useSkinAnim = false;
            }
        }

        private static void PlaySkin(AnimationClip clip) {
            if (!useSkinAnim || clip == null) return;
            try { skinAnim.Play(clip, 1f); } catch { }
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
                    ventAnimProgress = 0f;
                    break;
                case VentPhase.Entering:
                    if (!inVent) { StartExit(); break; }
                    UpdateVentCosmeticPositions(true);
                    if (!IsBodyAnimPlaying()) { ventPhase = VentPhase.In; SetVisibleAll(false); ventAnimProgress = 1f; }
                    break;
                case VentPhase.In:
                    if (!inVent) StartExit();
                    ventAnimProgress = 1f;
                    break;
                case VentPhase.Exiting:
                    if (inVent) { StartEnter(); break; }
                    UpdateVentCosmeticPositions(false);
                    if (!IsBodyAnimPlaying()) { ventPhase = VentPhase.Out; SetVisibleAll(true); bodyState = BodyState.None; ventAnimProgress = 0f; }
                    break;
            }
        }

        private static void PlayLocomotion(bool moving) {
            var want = moving ? BodyState.Run : BodyState.Idle;
            if (want == bodyState) return;
            bodyState = want;
            try { bodyAnim.Play(want == BodyState.Run ? runClip : idleClip, 1f); } catch { }
            PlaySkin(want == BodyState.Run ? sRun : sIdle);
        }

        private static void StartEnter() {
            ventPhase = VentPhase.Entering;
            bodyState = BodyState.None;
            ventAnimStartTime = Time.time;
            ventAnimProgress = 0f;
            // Keep the whole figure visible while the body ducks into the vent; only hide once it is fully
            // inside (Entering -> In). The skin ducks along via its own enter-vent animation.
            SetVisibleAll(true);
            try { bodyAnim.Play(enterClip != null ? enterClip : idleClip, 1f); } catch { }
            PlaySkin(sEnter != null ? sEnter : sIdle);
        }

        private static void StartExit() {
            ventPhase = VentPhase.Exiting;
            bodyState = BodyState.None;
            ventAnimStartTime = Time.time;
            ventAnimProgress = 1f;
            SetVisibleAll(true);                 // the figure reappears as the body climbs back out
            try { bodyAnim.Play(exitClip != null ? exitClip : idleClip, 1f); } catch { }
            PlaySkin(sExit != null ? sExit : sIdle);
        }

        private static void SetVisibleAll(bool on) {
            if (renderers == null) return;
            foreach (var r in renderers) if (r != null) r.gameObject.SetActive(on);
            // The colorblind label is not part of `renderers`; hide it with the rest of the figure while
            // vented. MirrorAppearance() re-shows it (if still appropriate) once the figure is out again.
            if (colorBlindClone != null && !on) colorBlindClone.gameObject.SetActive(false);
        }

        // Move cosmetics (hat, visor) down/up during vent animations so they follow the body sprite
        private static void UpdateVentCosmeticPositions(bool entering) {
            if (cosmeticTransforms == null || cosmeticOriginalPos == null) return;

            // Estimate animation progress based on time (VentTween is the fallback duration, use it as reference)
            float elapsed = Time.time - ventAnimStartTime;
            float duration = VentTween;

            // Get animation clip length if available for more accurate timing
            try {
                var clip = entering ? enterClip : exitClip;
                if (clip != null) duration = clip.length;
            } catch { }

            float t = Mathf.Clamp01(elapsed / Mathf.Max(duration, 0.01f));

            if (entering) {
                // Entering: progress from 0 (out) to 1 (in)
                ventAnimProgress = t;
            } else {
                // Exiting: progress from 1 (in) to 0 (out)
                ventAnimProgress = 1f - t;
            }

            // Move cosmetics down as the clone enters the vent (down ~0.5 units based on typical vent animation)
            const float ventDepth = 0.5f;
            for (int i = 0; i < cosmeticTransforms.Length && i < cosmeticOriginalPos.Length; i++) {
                if (cosmeticTransforms[i] == null) continue;
                Vector3 pos = cosmeticOriginalPos[i];
                pos.y -= ventAnimProgress * ventDepth;
                cosmeticTransforms[i].localPosition = pos;
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
                bool animDriven = (c == bodyClone && useAnim) || (c == skinClone && useSkinAnim);
                if (!animDriven) c.sprite = s.sprite;            // body/skin sprites are animation-driven
                c.color = s.color;
                c.flipX = false;
                // CosmeticsLayer.SetCosmeticZIndices() recomputes hat/visor/skin sort order on the live
                // player (e.g. on body-type or vent-related changes), so a one-time copy at Spawn() can go
                // stale and show the hat front/back layers in the wrong order. Re-sync every frame instead.
                c.sortingLayerID = s.sortingLayerID;
                c.sortingOrder = s.sortingOrder;
                try { c.material.CopyPropertiesFromMaterial(s.material); } catch { }
            }

            if (colorBlindClone != null && colorBlindSrc != null) {
                try {
                    bool show = src.cosmetics.showColorBlindText && colorBlindSrc.gameObject.activeInHierarchy;
                    colorBlindClone.gameObject.SetActive(show);
                    if (show) {
                        colorBlindClone.text = colorBlindSrc.text;
                        colorBlindClone.color = colorBlindSrc.color;
                    }
                } catch { }
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
            skinClone = null;
            skinAnim = null;
            sIdle = sRun = sEnter = sExit = null;
            useSkinAnim = false;
            cosmeticTransforms = null;
            cosmeticOriginalPos = null;
            colorBlindSrc = null;
            colorBlindClone = null;
            ventAnimProgress = 0f;
            ventAnimStartTime = 0f;
            ventPhase = VentPhase.Out;
            bodyState = BodyState.None;
            ventScale = 1f;
            flashUntil = 0f;
        }
    }
}
