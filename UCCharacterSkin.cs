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
 *   - SIGHT is applied by hand: the renderer fades out at the edge of the LOCAL player's light radius
 *     and disappears behind walls, because a plain SpriteRenderer carries none of the vanilla player
 *     material that normally hides a crewmate in the dark (see VisibilityAlpha).
 *   - The NAME TAG is lifted by exactly as much as the replacement is taller than the crewmate body it
 *     hides, so name + TOR's role line keep the same distance to the head they have on a vanilla
 *     crewmate instead of being swallowed by the beast's skull. The whole "Names" parent is moved (name,
 *     role info and colourblind text together) and only its Y is touched, because TOR rewrites that
 *     parent's Z every frame (PlayerControlPatch: SetLocalZ) to sort the tag behind map objects.
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
        private readonly float ppu, idleFps, walkFps, walkThreshold, z, contentTop;
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

        private Transform nameParent;   // the "Names" holder: name + TOR role line + colourblind text
        private float nameBaseY;        // its untouched local Y, restored on detach
        private float nameLift;         // how far the tag is pushed up while the skin is attached

        /// <param name="contentTop">
        /// Where the drawn figure ends inside its frame, as a fraction of the frame height counted from
        /// the BOTTOM (the sprites keep transparent margins, so the frame is taller than the character).
        /// Used only to lift the name tag by the right amount; 0.8 is a sane default for these canvases.
        /// </param>
        public UCCharacterSkin(string logTag, string idleBase, int idleCount, string walkBase, int walkCount,
                                float ppu, float idleFps = 8f, float walkFps = 12f,
                                float walkThreshold = 0.35f, float z = -0.06f, float contentTop = 0.8f) {
            this.logTag = logTag;
            this.idleBase = idleBase; this.idleCount = idleCount;
            this.walkBase = walkBase; this.walkCount = walkCount;
            this.ppu = ppu; this.idleFps = idleFps; this.walkFps = walkFps;
            this.walkThreshold = walkThreshold; this.z = z; this.contentTop = contentTop;
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
                SetupNameLift(player, body);
                HideCosmetics(player);
                LiftName();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[{logTag}] Attach failed: {e}");
                Detach();
            }
        }

        public void Detach() {
            try { RestoreName(); } catch { }
            try { if (owner != null) RestoreCosmetics(owner); } catch { }
            try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
            go = null;
            renderer = null;
            owner = null;
        }

        // ---- sight ----
        // A hand-drawn SpriteRenderer is NOT a crewmate: it carries none of the vanilla player material,
        // so nothing hides it once the owner walks out of the viewer's torch cone or behind a wall - the
        // beast (and the hunter) would light up across the whole map. The renderer is therefore faded by
        // hand against the LOCAL player's own light radius. ShipStatus.CalculateLightRadius is the right
        // source because everything that widens or narrows a view already ends up in it: the Lighter's
        // torch, a lights sabotage, the impostor light mod, and this mod's own Beacon/Scout/Poltergeist
        // postfixes. Walls use the same probe TOR uses to decide whether a player can be targeted.
        private const float FadeBand = 0.22f;   // fraction of the radius the figure fades out over

        private float VisibilityAlpha() {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.Data == null || owner == null) return 1f;
                if (me.Data.IsDead) return 1f;                      // ghosts see the whole map anyway
                if (owner.PlayerId == me.PlayerId) return 1f;       // your own costume is always yours
                var ship = ShipStatus.Instance;
                if (ship == null) return 1f;                        // no ship (lobby/intro): never hide

                float radius = ship.CalculateLightRadius(me.Data);
                if (radius <= 0f) return 0f;
                Vector2 from = me.GetTruePosition();
                Vector2 diff = owner.GetTruePosition() - from;
                float dist = diff.magnitude;
                if (dist > radius) return 0f;
                if (PhysicsHelpers.AnyNonTriggersBetween(from, diff.normalized, dist,
                                                         Constants.ShipAndObjectsMask)) return 0f;

                float band = radius * FadeBand;
                return band <= 0.001f ? 1f : Mathf.Clamp01((radius - dist) / band);
            } catch {
                return 1f;   // never let a sight probe make the transformation invisible by accident
            }
        }

        // ---- name tag ----

        private void SetupNameLift(PlayerControl player, SpriteRenderer body) {
            nameParent = null;
            nameLift = 0f;
            try {
                var tag = player.cosmetics != null ? player.cosmetics.nameText : null;
                if (tag == null || tag.transform == null || tag.transform.parent == null) return;

                // Top of the crewmate the skin replaces, and top of the drawn figure - both in the
                // player's own local space. Anything the skin adds on top is what the tag has to clear.
                float bodyTop = body != null ? body.bounds.max.y - player.transform.position.y : 0.35f;
                float skinTop = feetLocalY + idleFrames[0].bounds.size.y * contentTop;
                float lift = skinTop - bodyTop;
                if (lift <= 0.01f) return;      // skin no taller than the crewmate: leave the tag alone

                nameParent = tag.transform.parent;
                nameBaseY = nameParent.localPosition.y;
                nameLift = lift;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[{logTag}] name lift setup failed: {e.Message}");
                nameParent = null;
                nameLift = 0f;
            }
        }

        // Re-applied every frame like HideCosmetics: the tag holder is re-positioned by AU and by TOR
        // (Z sorting) behind our back, and Y must survive all of it. Only Y is written.
        private void LiftName() {
            if (nameParent == null || nameLift <= 0f) return;
            try {
                var lp = nameParent.localPosition;
                nameParent.localPosition = new Vector3(lp.x, nameBaseY + nameLift, lp.z);
            } catch { }
        }

        private void RestoreName() {
            if (nameParent == null) return;
            try {
                var lp = nameParent.localPosition;
                nameParent.localPosition = new Vector3(lp.x, nameBaseY, lp.z);
            } catch { }
            nameParent = null;
            nameLift = 0f;
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

            // Sight: the replacement obeys the viewer's own light exactly like the crewmate it hides.
            float vis = VisibilityAlpha();
            renderer.enabled = vis > 0.01f;
            renderer.color = new Color(tint.r, tint.g, tint.b, tint.a * vis);

            // Facing: CosmeticsLayer.FlipX is what AU itself uses for the crewmate, so the skin always
            // looks the same way its owner does (including while standing still).
            try { renderer.flipX = owner.cosmetics.FlipX; } catch { }

            go.transform.localPosition = new Vector3(0f, feetLocalY, z);

            // Re-hide every frame - night vision (setLook), morph/camouflage and the vanilla animator
            // all re-enable these renderers behind our back.
            HideCosmetics(owner);
            LiftName();
        }
    }
}
