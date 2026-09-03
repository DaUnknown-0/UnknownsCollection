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
        // A dissolve (DespawnWithFx) detaches its fading instance from every OTHER static field below, so
        // a round reset landing mid-fade would otherwise leave it to self-destruct on its own timer instead
        // of being cleaned up immediately. Tracked separately and registered with UCFx's reset registry so
        // "Aufräumen über UCFx.RegisterReset" applies here too, not just to the live-clone bookkeeping.
        private static GameObject dissolveGo;
        // AUDIT-2026-08-23, L-17: the dissolve's owned Material instances (see `ownedMaterials` below),
        // captured alongside dissolveGo so a round reset landing mid-fade destroys them too instead of
        // just the fading GameObject.
        private static List<Material> dissolveOwnedMaterials;

        static IllusionistClone() {
            UCFx.RegisterReset(() => {
                if (dissolveGo != null) { try { UnityEngine.Object.Destroy(dissolveGo); } catch { } dissolveGo = null; }
                DestroyOwnedMaterials(dissolveOwnedMaterials);
                dissolveOwnedMaterials = null;
            });
        }

        private static GameObject go;
        private static SpriteRenderer[] renderers;   // clone renderers
        private static SpriteRenderer[] sources;     // live source renderers, parallel to `renderers`
        private static PlayerControl src;            // the live Illusionist we mirror
        // AUDIT-2026-08-23, L-17: the per-renderer Material instances created below via
        // `new Material(sr.material)`. They cannot be a sharedMaterial like the fallback path (each one
        // is independently re-synced from its own source's material every frame in MirrorAppearance()),
        // which means destroying `go` alone does NOT free them either - a GameObject's teardown only
        // detaches its Renderer components, the native Material objects they pointed at leak until
        // explicitly destroyed. Tracked here and destroyed alongside the clone in every teardown path.
        private static List<Material> ownedMaterials;
        // PERF: renderers[k].material cached once at spawn time, parallel to `renderers`/`sources`.
        // Renderer.material is an Il2Cpp property getter (Interop call) even once the instance already
        // exists, so re-reading it every frame in MirrorAppearance()/ApplyOutline() was wasted work; the
        // Material reference itself never changes for the lifetime of a clone.
        private static Material[] cloneMaterials;

        // PERF: MirrorAppearance() change-detection cache - see MirrorAppearance() for how it's used.
        // Reset (nulled / cleared) in ResetState() so a fresh clone always does a full copy on its first
        // frame instead of trusting state left over from a previous clone's renderers.
        private static bool mirrorInit;
        // Il2Cpp object references can go stale/get GC'd and reissued at the same managed wrapper while
        // the underlying native sprite differs, so ReferenceEquals on the wrapper is not a safe identity
        // check here - compare the native pointer instead (Il2CppHelpers convention used elsewhere in
        // this repo, e.g. UsefulTORStuff/TorLeakFixes.cs's cachedTaskPtr).
        private static IntPtr[] lastSourceSprite;      // per source renderer, parallel to sources/renderers (native Sprite pointer, IntPtr.Zero = none)
        private static Color[] lastSourceColor;
        private static int[] lastSourceSortingLayer;
        private static int[] lastSourceSortingOrder;
        private static bool lastCamoActive;
        private static bool lastMushroomActive;
        private static int lastOutfitColorId;

        // PERF: ApplyOutline() change-detection cache + cached shader property ids (see ApplyOutline()).
        private static bool outlineInit;
        private static bool lastOutlineShow;
        private static Color lastOutlineColor;
        private static int outlineId = -1, outlineColorId = -1;

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
        private static Vector3 colorBlindBaseScale = Vector3.one; // label scale at spawn, for the counter-flip

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
        private static float flashStart;             // shield-flash highlight start time (for the decay curve)
        private static float flashDuration = 0.4f;   // shield-flash total length, parallel to flashUntil-flashStart
        private static float facingSign = 1f;        // +1 facing right, -1 facing left (from movement)

        // Dissolve (soft despawn): captures the outgoing clone's GameObject/renderers into a short-lived
        // local closure (see DespawnWithFx) so an immediate re-spawn never touches the fading instance -
        // the static fields below are cleared the instant the dissolve starts, same as a hard Despawn().
        private const float DissolveDuration = 0.25f;

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
                var newOwnedMaterials = new List<Material>(srcList.Count);
                var builtMaterials = new List<Material>(srcList.Count);
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
                    // AUDIT-2026-08-23, L-17: only the `new Material` branch needs to be tracked for
                    // later Destroy() - the catch fallback assigns the SOURCE's own sharedMaterial (the
                    // live player's actual cosmetics material), and destroying that would break the
                    // real player's rendering, not just the clone's.
                    try { cr.material = new Material(sr.material); newOwnedMaterials.Add(cr.material); }
                    catch { cr.sharedMaterial = sr.sharedMaterial; }
                    // Read back whichever material ended up assigned (own instance in the normal case;
                    // in the rare catch-fallback case this is the same lazy own-copy Renderer.material
                    // would have produced anyway on its first per-frame access before this change) and
                    // cache it so ApplyOutline()/MirrorAppearance() never need the property getter again.
                    builtMaterials.Add(cr.material);
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
                ownedMaterials = newOwnedMaterials;
                cloneMaterials = builtMaterials.ToArray();

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
                        colorBlindBaseScale = textGo.transform.localScale;
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
                if (Illusionist.IsLocalIllusionist()) UCAssets.PlayCloneShimmer(currentPos); // sound Illusionist-only
                IllusionistFx.SpawnMaterializePoof(currentPos); // visual beat stays public (clone is visible to all)
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

        public static void Flash(float seconds) {
            flashStart = Time.time;
            flashDuration = Mathf.Max(seconds, 0.01f);
            flashUntil = flashStart + flashDuration;
        }

        // ---- Per-frame replay + body animation + appearance mirror + shield outline (HudManager.Update) ----
        public static void Update() {
            if (!active || go == null) return;
            try {
                float t = Time.time - startTime;
                float fIdx = t / interval;
                int i = Mathf.FloorToInt(fIdx);
                if (i >= path.Count - 1) {
                    // Reached the end of the recording -> the illusion fades (dissolve, not a hard cut).
                    DespawnWithFx();
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
            PlayVentSound();
            try { bodyAnim.Play(enterClip != null ? enterClip : idleClip, 1f); } catch { }
            PlaySkin(sEnter != null ? sEnter : sIdle);
        }

        private static void StartExit() {
            ventPhase = VentPhase.Exiting;
            bodyState = BodyState.None;
            ventAnimStartTime = Time.time;
            ventAnimProgress = 1f;
            SetVisibleAll(true);                 // the figure reappears as the body climbs back out
            PlayVentSound();
            try { bodyAnim.Play(exitClip != null ? exitClip : idleClip, 1f); } catch { }
            PlaySkin(sExit != null ? sExit : sIdle);
        }

        // The clone's vent transitions used to be completely silent (a real player makes vent noise; a
        // mute vent was itself a tell that something was off). The base game's own vent whoosh lives in
        // an IL2CPP AnimationEvent on PlayerPhysics' Enter/ExitVent clips (Vent.Use -> MyPhysics.RpcEnterVent/
        // RpcExitVent - no TOR-side wrapper exposes it directly, confirmed via SoundEffectsManager.cs and
        // UsablesPatch.cs, neither of which registers a generic vent SFX name), so it can't be replayed
        // through a managed call here. Fallback per spec: a quiet illusionist_unravel cue instead.
        private static void PlayVentSound() { if (Illusionist.IsLocalIllusionist()) UCAssets.PlayIllusionistUnravelAt(currentPos, 0.35f); } // sound Illusionist-only

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

            // PERF: CopyPropertiesFromMaterial (and the sprite/color/sorting assignments feeding it) is
            // the expensive part of this loop, run per renderer per frame. Everything it could actually
            // need to react to boils down to: this renderer's own sprite/color, or one of the three
            // GLOBAL look-changing signals below (Camouflage tint, Fungle Mushroom-Mixup, an
            // outfit/color change e.g. Morphling). None of those change most frames, so track the last
            // seen values and only touch a renderer when something relevant to IT actually changed.
            bool camoActive = Camouflager.camouflageTimer > 0f;
            bool mushroomActive = Helpers.MushroomSabotageActive();
            int outfitColorId = -1;
            try { outfitColorId = src.CurrentOutfit.ColorId; } catch { }
            bool globalChanged = !mirrorInit || camoActive != lastCamoActive
                                  || mushroomActive != lastMushroomActive || outfitColorId != lastOutfitColorId;

            if (lastSourceSprite == null || lastSourceSprite.Length != sources.Length) {
                lastSourceSprite = new IntPtr[sources.Length];
                lastSourceColor = new Color[sources.Length];
                lastSourceSortingLayer = new int[sources.Length];
                lastSourceSortingOrder = new int[sources.Length];
                mirrorInit = false; // fresh cache => force a full copy below regardless of `globalChanged`
            }

            for (int k = 0; k < renderers.Length && k < sources.Length; k++) {
                var s = sources[k];
                var c = renderers[k];
                if (s == null || c == null) continue;
                bool animDriven = (c == bodyClone && useAnim) || (c == skinClone && useSkinAnim);

                IntPtr spritePtr = s.sprite != null ? s.sprite.Pointer : IntPtr.Zero;
                bool spriteChanged = spritePtr != lastSourceSprite[k];
                bool colorChanged = s.color != lastSourceColor[k];
                bool sortChanged = s.sortingLayerID != lastSourceSortingLayer[k]
                                    || s.sortingOrder != lastSourceSortingOrder[k];

                if (!animDriven && (spriteChanged || !mirrorInit)) c.sprite = s.sprite; // body/skin sprites are animation-driven
                if (colorChanged || !mirrorInit) { c.color = s.color; c.flipX = false; }
                // CosmeticsLayer.SetCosmeticZIndices() recomputes hat/visor/skin sort order on the live
                // player (e.g. on body-type or vent-related changes), so a one-time copy at Spawn() can go
                // stale and show the hat front/back layers in the wrong order. Re-sync on change instead.
                if (sortChanged || !mirrorInit) { c.sortingLayerID = s.sortingLayerID; c.sortingOrder = s.sortingOrder; }

                if (spriteChanged || colorChanged || globalChanged) {
                    var mat = (cloneMaterials != null && k < cloneMaterials.Length) ? cloneMaterials[k] : c.material;
                    try { if (mat != null) mat.CopyPropertiesFromMaterial(s.material); } catch { }
                }

                lastSourceSprite[k] = spritePtr;
                lastSourceColor[k] = s.color;
                lastSourceSortingLayer[k] = s.sortingLayerID;
                lastSourceSortingOrder[k] = s.sortingOrder;
            }
            lastCamoActive = camoActive;
            lastMushroomActive = mushroomActive;
            lastOutfitColorId = outfitColorId;
            mirrorInit = true;

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
            // Kill-block flash: decays from white back to the normal shield color over flashDuration
            // instead of a hard on/off step, so a blocked kill reads as an impact instead of a light switch.
            bool flashing = Time.time < flashUntil;
            Color outline = Medic.shieldedColor;
            if (flashing) {
                float decay = Mathf.Clamp01((Time.time - flashStart) / flashDuration);
                outline = Color.Lerp(Color.white, Medic.shieldedColor, decay);
            }

            // PERF: shader property ids resolved once (see UsefulTORStuff/UTSShieldOutlines.cs - the
            // string overloads marshal + hash the name into Il2Cpp on every call).
            if (outlineId < 0) {
                outlineId = Shader.PropertyToID("_Outline");
                outlineColorId = Shader.PropertyToID("_OutlineColor");
            }
            // PERF: show/outline are the same for every renderer this call, so the SetFloat/SetColor
            // Interop calls are skipped whenever neither changed since the last time we wrote them -
            // except during the flash decay above, which is a continuous animation and needs every frame.
            bool changed = !outlineInit || flashing || lastOutlineShow != show
                           || (show && lastOutlineColor != outline);
            if (changed) { outlineInit = true; lastOutlineShow = show; lastOutlineColor = outline; }

            for (int k = 0; k < renderers.Length; k++) {
                var r = renderers[k];
                if (r == null) continue;
                // The activeSelf skip only applies to the color write (a disabled renderer doesn't
                // render, so its alpha doesn't matter right now). The material outline properties still
                // get written below regardless - otherwise a renderer that happens to be inactive on the
                // one frame `changed` is true keeps whatever outline state it had, and never catches up
                // once it reactivates (nothing re-triggers `changed` for it later).
                if (r.gameObject.activeSelf) {
                    var c = r.color; c.a = 1f; r.color = c;
                }
                if (!changed) continue;
                var mat = (cloneMaterials != null && k < cloneMaterials.Length) ? cloneMaterials[k] : r.material;
                if (mat == null) continue;
                try {
                    mat.SetFloat(outlineId, show ? 1f : 0f);
                    if (show) mat.SetColor(outlineColorId, outline);
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

            // Counter-flip the colorblind label: it hangs under the root whose x-scale carries the
            // facing, so a left-facing clone showed a MIRRORED color name ("kcalB") - an instant
            // giveaway. Multiplying the label's local x by facingSign nets the flip out to always-
            // readable text (vanilla never mirrors it either: it flips body sprites, not scale).
            if (colorBlindClone != null)
                colorBlindClone.transform.localScale = new Vector3(
                    colorBlindBaseScale.x * facingSign, colorBlindBaseScale.y, colorBlindBaseScale.z);
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

        // Hard, immediate despawn - no FX. Used to clear a stale clone before a fresh Spawn(), and at
        // meeting-start/round-reset where a scene change already covers the transition.
        public static void Despawn() {
            var toDestroy = go;
            var toDestroyMaterials = ownedMaterials; // AUDIT-2026-08-23, L-17: captured before ResetState() nulls the field
            ResetState();
            if (toDestroy != null) { try { UnityEngine.Object.Destroy(toDestroy); } catch { } }
            DestroyOwnedMaterials(toDestroyMaterials);
        }

        // Soft despawn: the clone dissolves (renderers fade out over DissolveDuration) instead of
        // vanishing instantly, with a poof + illusionist_unravel cue at its last position. The static
        // bookkeeping is cleared IMMEDIATELY (same as a hard Despawn) so a fresh Spawn() right after never
        // collides with the fading instance - the fade runs entirely off captured local references.
        public static void DespawnWithFx() {
            try {
                if (!active || go == null) { Despawn(); return; }
                var fadeGo = go;
                var fadeRenderers = renderers;
                var fadeOwnedMaterials = ownedMaterials; // AUDIT-2026-08-23, L-17: captured before ResetState() nulls the field
                var fadePos = currentPos;
                ResetState();
                if (dissolveGo != null) { try { UnityEngine.Object.Destroy(dissolveGo); } catch { } } // any earlier fade still in flight
                DestroyOwnedMaterials(dissolveOwnedMaterials); // ditto for that earlier fade's owned materials
                dissolveGo = fadeGo;
                dissolveOwnedMaterials = fadeOwnedMaterials;

                IllusionistFx.SpawnMaterializePoof(fadePos); // visual dissolve stays public
                if (Illusionist.IsLocalIllusionist()) UCAssets.PlayIllusionistUnravelAt(fadePos); // sound Illusionist-only

                var hud = HudManager.Instance;
                if (hud == null) {
                    if (fadeGo != null) UnityEngine.Object.Destroy(fadeGo);
                    DestroyOwnedMaterials(fadeOwnedMaterials);
                    dissolveGo = null;
                    dissolveOwnedMaterials = null;
                    return;
                }
                hud.StartCoroutine(Effects.Lerp(DissolveDuration, new Action<float>((t) => {
                    try {
                        if (fadeGo == null) return;
                        float a = Mathf.Clamp01(1f - t);
                        if (fadeRenderers != null) {
                            foreach (var r in fadeRenderers) {
                                if (r == null) continue;
                                var c = r.color; c.a = a; r.color = c;
                            }
                        }
                        if (t >= 1f) {
                            UnityEngine.Object.Destroy(fadeGo);
                            DestroyOwnedMaterials(fadeOwnedMaterials);
                            if (dissolveGo == fadeGo) { dissolveGo = null; dissolveOwnedMaterials = null; }
                        }
                    } catch { }
                })));
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Illusionist] clone dissolve failed: {e}");
                Despawn();
            }
        }

        // AUDIT-2026-08-23, L-17: destroying a GameObject only detaches its Renderer components, it
        // does not free the native Material instances that were assigned to them via `new Material(...)`
        // - those leak for the rest of the process unless explicitly destroyed here. Every teardown path
        // (hard despawn, soft dissolve, the superseded-fade case, and the round-reset registration)
        // routes through this one helper.
        private static void DestroyOwnedMaterials(List<Material> mats) {
            if (mats == null) return;
            foreach (var m in mats) if (m != null) { try { UnityEngine.Object.Destroy(m); } catch { } }
        }

        private static void ResetState() {
            active = false;
            go = null;
            renderers = null;
            sources = null;
            src = null;
            ownedMaterials = null;
            cloneMaterials = null;
            mirrorInit = false;
            lastSourceSprite = null;
            lastSourceColor = null;
            lastSourceSortingLayer = null;
            lastSourceSortingOrder = null;
            lastCamoActive = false;
            lastMushroomActive = false;
            lastOutfitColorId = 0;
            outlineInit = false;
            lastOutlineShow = false;
            lastOutlineColor = default;
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
            flashStart = 0f;
            flashDuration = 0.4f;
        }
    }
}
