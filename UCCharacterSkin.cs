// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCCharacterSkin - shared "full renderer swap" character skin for Unknown's Collection roles.
 *
 * Extracted from WerewolfFx (Paket W1) so the Hunter (Paket W2, HunterFx.cs) can reuse the exact
 * same mechanic instead of a copy-pasted clone: the real crewmate cosmetics are hidden and a single
 * child SpriteRenderer plays a hand-drawn idle/walk flipbook instead, chosen from the player's own
 * movement (MyPhysics.Velocity, falling back to a position-delta so a REMOTE owner never freezes
 * into the idle cycle while visibly walking) with flipX taken from the cosmetics' own facing flag.
 *
 *   - Frames are loaded with a BOTTOM-CENTRE pivot (UCAssets.GetSkinFrames) and the renderer is
 *     anchored on the crewmate's FEET (body-sprite bounds minimum, measured once at attach), so the
 *     replacement stands on exactly the same ground line a centred pivot would bury it to the hips.
 *   - The GameObject uses layer 11 like every other procedural world object in this mod; on the
 *     Default layer the AU ship camera simply does not render it.
 *   - Cosmetics are re-hidden EVERY frame, not once: TOR's night-vision code (setLook), morph,
 *     camouflage and the vanilla animator all re-enable renderers behind our back.
 *   - An optional tint color multiplies the renderer (Hunter: the player's own colour, so the crew
 *     still recognises "their sheriff" under the hunter garb; Werewolf: left null - one beast per
 *     round, no need to tell it apart from itself).
 *
 * One instance = one attach slot (one owner at a time), matching how both callers use it: Werewolf
 * has exactly one beast, Hunter has exactly one hunter per round.
 */

using System;
using UnityEngine;

namespace UnknownsCollection {
    public sealed class UCCharacterSkin {
        private readonly string idleBase, walkBase;
        private readonly int idleCount, walkCount;
        private readonly float ppu, idleFps, walkFps, walkThreshold, z;
        private readonly string logTag;

        private Sprite[] idleFrames, walkFrames;
        private bool framesTried;

        private GameObject go;
        private SpriteRenderer renderer;
        private PlayerControl owner;
        private float feetLocalY;
        private float start;
        private Vector2 lastOwnerPos;
        private float lastOwnerPosTime;
        private Color tint = Color.white;

        public UCCharacterSkin(string logTag, string idleBase, int idleCount, string walkBase, int walkCount,
                                float ppu, float idleFps = 8f, float walkFps = 12f,
                                float walkThreshold = 0.35f, float z = -0.06f) {
            this.logTag = logTag;
            this.idleBase = idleBase; this.idleCount = idleCount;
            this.walkBase = walkBase; this.walkCount = walkCount;
            this.ppu = ppu; this.idleFps = idleFps; this.walkFps = walkFps;
            this.walkThreshold = walkThreshold; this.z = z;
        }

        public bool Attached => go != null && owner != null;

        private void LoadFrames() {
            if (framesTried) return;
            framesTried = true;
            idleFrames = UCAssets.GetSkinFrames(idleBase, idleCount, ppu);
            walkFrames = UCAssets.GetSkinFrames(walkBase, walkCount, ppu);
            if (idleFrames == null || walkFrames == null)
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    $"[{logTag}] skin frames missing - the transformation stays audible/mechanical but " +
                    "invisible (cosmetics are left untouched).");
        }

        public void Attach(PlayerControl player, Color? tintColor = null) {
            try {
                Detach();
                if (player == null || player.cosmetics == null) return;
                LoadFrames();
                if (idleFrames == null || walkFrames == null) return;

                var body = player.cosmetics.currentBodySprite != null
                    ? player.cosmetics.currentBodySprite.BodySprite : null;
                // Feet line of the crewmate in the player's own local space, measured once. Falls back
                // to a sane constant if the body renderer has no bounds yet (first frame after spawn).
                feetLocalY = body != null
                    ? body.bounds.min.y - player.transform.position.y
                    : -0.35f;

                go = new GameObject("UCSkin_" + logTag) { layer = 11 };
                go.transform.SetParent(player.transform, false);
                go.transform.localPosition = new Vector3(0f, feetLocalY, z);
                renderer = go.AddComponent<SpriteRenderer>();
                tint = tintColor ?? Color.white;
                renderer.color = tint;
                renderer.sprite = idleFrames[0];
                if (body != null) {
                    renderer.sortingLayerID = body.sortingLayerID;
                    renderer.sortingOrder = body.sortingOrder + 1;
                }
                owner = player;
                start = Time.time;
                lastOwnerPos = player.GetTruePosition();
                lastOwnerPosTime = Time.time;
                HideCosmetics(player);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[{logTag}] Attach failed: {e}");
                Detach();
            }
        }

        public void Detach() {
            try { if (owner != null) RestoreCosmetics(owner); } catch { }
            try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
            go = null;
            renderer = null;
            owner = null;
        }

        private static void HideCosmetics(PlayerControl p) {
            if (p == null || p.cosmetics == null) return;
            try {
                if (p.cosmetics.currentBodySprite != null && p.cosmetics.currentBodySprite.BodySprite != null)
                    p.cosmetics.currentBodySprite.BodySprite.enabled = false;
            } catch { }
            try { p.SetHatAndVisorAlpha(0f); } catch { }
            try {
                if (p.cosmetics.skin != null && p.cosmetics.skin.layer != null)
                    p.cosmetics.skin.layer.enabled = false;
            } catch { }
            // The pet is deliberately left alone: it is a separate creature, not part of the body, and
            // toggling it fights AU's own pet spawn/despawn logic.
        }

        private static void RestoreCosmetics(PlayerControl p) {
            if (p == null || p.cosmetics == null) return;
            try {
                if (p.cosmetics.currentBodySprite != null && p.cosmetics.currentBodySprite.BodySprite != null)
                    p.cosmetics.currentBodySprite.BodySprite.enabled = true;
            } catch { }
            try { p.SetHatAndVisorAlpha(1f); } catch { }
            try {
                if (p.cosmetics.skin != null && p.cosmetics.skin.layer != null)
                    p.cosmetics.skin.layer.enabled = true;
            } catch { }
        }

        public void Tick() {
            if (go == null || renderer == null || owner == null) return;

            // Owner gone/dead/despawned -> get out cleanly (the role file also detaches on its own
            // death path; this is the belt-and-suspenders half).
            if (owner.Data == null || owner.Data.Disconnected || owner.Data.IsDead) {
                Detach();
                return;
            }

            // Speed: PlayerPhysics.Velocity is the authoritative value, but it is only meaningful once
            // the rigidbody has been driven this frame - fall back to the position delta so a remote
            // owner never freezes into the idle cycle while visibly walking.
            float speed = 0f;
            try { speed = owner.MyPhysics != null ? owner.MyPhysics.Velocity.magnitude : 0f; } catch { }
            Vector2 now = owner.GetTruePosition();
            float dt = Time.time - lastOwnerPosTime;
            if (dt > 0.02f) {
                speed = Mathf.Max(speed, Vector2.Distance(now, lastOwnerPos) / dt);
                lastOwnerPos = now;
                lastOwnerPosTime = Time.time;
            }

            bool walking = speed > walkThreshold;
            var set = walking ? walkFrames : idleFrames;
            float fps = walking ? walkFps : idleFps;
            renderer.sprite = set[(int)((Time.time - start) * fps) % set.Length];
            renderer.color = tint;

            // Facing: CosmeticsLayer.FlipX is what AU itself uses for the crewmate, so the skin always
            // looks the same way its owner does (including while standing still).
            try { renderer.flipX = owner.cosmetics.FlipX; } catch { }

            go.transform.localPosition = new Vector3(0f, feetLocalY, z);

            // Re-hide every frame - night vision (setLook), morph/camouflage and the vanilla animator
            // all re-enable these renderers behind our back.
            HideCosmetics(owner);
        }
    }
}
