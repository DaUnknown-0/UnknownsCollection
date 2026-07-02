// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Poltergeist - Manifest ability.
 *
 * The dead Poltergeist temporarily appears as a COPY of a nearby living player (name + full outfit via
 * TOR's setLook, the Morphling mechanism). While manifested:
 *   - every client force-renders the ghost (PlayerControl.FixedUpdate re-hides dead players each frame,
 *     so a postfix keeps Visible = true and the sprite alpha at 1);
 *   - on the Poltergeist's OWN client the GameObject layer switches Ghost -> Players and the collider
 *     re-enables, so walls block it like a living player (movement is client-authoritative, so this is
 *     enough - and manifesting requires standing on clear ground, see StandingClear);
 *   - it may use vents (option): a high-priority Vent.CanUse prefix that runs before TOR's own patch;
 *   - it CANNOT kill, report, use consoles or call meetings (all vanilla-gated on being alive);
 *   - a kill attempt on it "succeeds": the manifest POOFS - no body, no death - and the killer's kill
 *     cooldown is refunded per option (no/half/full). The poof tells the killer a Poltergeist exists.
 *
 * The template is the nearest living player, so the Poltergeist can frame someone by venting in their
 * skin. A meeting ends the manifestation instantly and silently.
 */

using System;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using AmongUs.GameOptions;

namespace UnknownsCollection {
    public static class PoltergeistManifest {
        // RPC subtypes within the Poltergeist RPC (208); dispatched from Poltergeist.HandleRpcPatch.
        private const byte SubManifestStart = 5;   // templateId, duration(float)
        private const byte SubManifestEnd = 6;     // reason: 0 timeout, 1 killed, 2 silent (meeting)

        public static bool IsManifested { get; private set; }
        private static byte templateId = byte.MaxValue;
        private static float endTime;
        // Player the ghost CHOSE to copy (K cycles through the living; shown on the button label).
        private static byte selectedTemplateId = byte.MaxValue;
        // Whether the template look is currently worn. False while a global disguise (Camouflager
        // camo / Fungle mushroom sabotage) suppresses it - see EnforceLook().
        private static bool lookApplied;

        private static TheOtherRoles.Objects.CustomButton manifestButton;

        public static void Reset() {
            IsManifested = false;
            templateId = byte.MaxValue;
            selectedTemplateId = byte.MaxValue;
            lookApplied = false;
            endTime = 0;
            // manifestButton deliberately kept (resetVariables runs after HudManager.Start).
        }

        // ---- RPC ----

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, UnknownsCollectionPlugin.PoltergeistRpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void HandleRpc(byte subtype, MessageReader reader) {
            switch (subtype) {
                case SubManifestStart: {
                    byte template = reader.ReadByte();
                    float duration = reader.ReadSingle();
                    ApplyStart(template, duration);
                    break;
                }
                case SubManifestEnd: ApplyEnd(reader.ReadByte()); break;
            }
        }

        private static void SendStart(byte template, float duration) {
            try {
                var w = BeginRpc(SubManifestStart);
                w.Write(template);
                w.Write(duration);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyStart(template, duration);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] SendManifestStart failed: {e}"); }
        }

        private static void SendEnd(byte reason) {
            try {
                var w = BeginRpc(SubManifestEnd);
                w.Write(reason);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyEnd(reason);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] SendManifestEnd failed: {e}"); }
        }

        // ---- Apply (every client) ----

        private static void ApplyStart(byte template, float duration) {
            var ghost = Poltergeist.poltergeist;
            var templatePlayer = Helpers.playerById(template);
            if (ghost == null || templatePlayer == null || templatePlayer.Data == null) return;

            IsManifested = true;
            templateId = template;
            endTime = Time.time + duration;

            // Wear the template's identity (Morphling mechanism) - unless a global disguise
            // (Camouflager camo / mushroom sabotage) is running: then stay grey like everyone
            // else; EnforceLook() applies the template look once the disguise ends.
            if (!DisguiseActive()) {
                ApplyTemplateLook(ghost);
                lookApplied = true;
            } else {
                ghost.setLook("", 6, "", "", "", "");
                lookApplied = false;
            }

            PoltergeistFx.SpawnPoof(ghost.GetTruePosition());
            UCAssets.PlayManifest();

            // Physicality only on the ghost's own client (movement is client-authoritative there).
            if (Poltergeist.IsLocalPoltergeist()) SetPhysical(true);
        }

        // A global disguise that hides everyone's identity - the manifest must not stick out of it.
        private static bool DisguiseActive() {
            try { return Camouflager.camouflageTimer > 0f || Helpers.MushroomSabotageActive(); }
            catch { return false; }
        }

        private static void ApplyTemplateLook(PlayerControl ghost) {
            var templatePlayer = Helpers.playerById(templateId);
            if (ghost == null || templatePlayer == null || templatePlayer.Data == null) return;
            var outfit = templatePlayer.Data.DefaultOutfit;
            ghost.setLook(templatePlayer.Data.PlayerName, outfit.ColorId, outfit.HatId,
                outfit.VisorId, outfit.SkinId, outfit.PetId);
        }

        // Per-frame look enforcement (every client): TOR's camo start greys everyone including the
        // ghost, but its camo END resets everyone to their OWN outfit (only the Morphling gets
        // re-morphed by TOR) - which would unmask the manifest. Mirror TOR's Morphling handling.
        private static void EnforceLook() {
            var ghost = Poltergeist.poltergeist;
            if (ghost == null) return;
            bool disguised = DisguiseActive();
            if (disguised && lookApplied) {
                lookApplied = false;
                ghost.setLook("", 6, "", "", "", ""); // covers ghosts a disguise pass skipped
            } else if (!disguised && !lookApplied) {
                lookApplied = true;
                ApplyTemplateLook(ghost); // re-apply after TOR's resetCamouflage
            }
        }

        private static void ApplyEnd(byte reason) {
            var ghost = Poltergeist.poltergeist;
            IsManifested = false;
            templateId = byte.MaxValue;
            lookApplied = false;
            endTime = 0;

            if (ghost != null && ghost.Data != null) {
                // Back to the own identity (stay grey while a global disguise is still running -
                // TOR resets everyone anyway when it ends)...
                if (DisguiseActive()) {
                    ghost.setLook("", 6, "", "", "", "");
                } else {
                    var own = ghost.Data.DefaultOutfit;
                    ghost.setLook(ghost.Data.PlayerName, own.ColorId, own.HatId, own.VisorId, own.SkinId, own.PetId);
                }
                // ...and the poof (silent on meeting start - everyone is teleported anyway).
                if (reason != 2) {
                    PoltergeistFx.SpawnPoof(ghost.GetTruePosition());
                    UCAssets.PlayPoof(ghost.GetTruePosition());
                }
            }

            if (Poltergeist.IsLocalPoltergeist()) SetPhysical(false);
            // (The vent button is hidden by Tick's housekeeping the moment IsManifested is false.)
        }

        // Ghost client only: solid vs spectral. Manifesting requires clear ground (StandingClear), so
        // enabling the collider can not trap the ghost inside a wall.
        private static void SetPhysical(bool solid) {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null) return;
                int layer = LayerMask.NameToLayer(solid ? "Players" : "Ghost");
                if (layer >= 0) me.gameObject.layer = layer;
                if (me.Collider != null) me.Collider.enabled = solid;
                if (!solid && me.inVent) {
                    // Manifest ended inside a vent: climb out so the ghost is not stuck rendered in it.
                    var vent = Vent.currentVent;
                    if (vent != null) me.MyPhysics.RpcExitVent(vent.Id);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Poltergeist] SetPhysical failed: {e.Message}");
            }
        }

        // The manifest may only start on clear ground - never inside ship geometry. Radius 0.1
        // (roughly half the player's own collision circle): the earlier 0.3 rejected most legal
        // spots because it already grazed walls/props at distances where living players stand
        // routinely. logBlockers is set on an actual click so the log names what rejected the spot.
        private static bool StandingClear(bool logBlockers = false) {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null) return false;
                var hits = Physics2D.OverlapCircleAll(me.GetTruePosition(), 0.1f, Constants.ShipAndObjectsMask);
                foreach (var h in hits)
                    if (h != null && !h.isTrigger) {
                        if (logBlockers)
                            UnknownsCollectionPlugin.Logger?.LogInfo(
                                $"[Poltergeist] manifest blocked by '{h.name}' (layer {h.gameObject.layer})");
                        return false;
                    }
                return true;
            } catch { return false; }
        }

        // ---- Button + template selection ----

        public static void CreateButton(HudManager hud) {
            manifestButton = new TheOtherRoles.Objects.CustomButton(
                () => {
                    if (IsManifested) return;
                    var template = CurrentTemplate();
                    if (template == null || !StandingClear(logBlockers: true)) return;
                    float cost = Poltergeist.ManifestCost?.getFloat() ?? 60f;
                    if (Poltergeist.energy < cost) return;
                    Poltergeist.energy -= cost;
                    SendStart(template.PlayerId, Poltergeist.ManifestDuration?.getFloat() ?? 12f);
                },
                () => Poltergeist.IsLocalPoltergeist()
                      && PlayerControl.LocalPlayer.Data != null && PlayerControl.LocalPlayer.Data.IsDead
                      && MeetingHud.Instance == null && ExileController.Instance == null
                      && !IsManifested,
                () => Poltergeist.energy >= (Poltergeist.ManifestCost?.getFloat() ?? 60f)
                      && CurrentTemplate() != null && StandingClear(),
                () => { },
                UCAssets.ManifestIcon,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowLeft,
                hud, KeyCode.T, false, "MANIFEST");
            manifestButton.MaxTimer = 1f; manifestButton.Timer = 0f;
        }

        private static bool IsValidTemplate(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected
            && (PlayerControl.LocalPlayer == null || p.PlayerId != PlayerControl.LocalPlayer.PlayerId);

        // The player the manifest copies: the ghost's explicit pick (K key), or - if none picked /
        // the pick died - the nearest living player as fallback (no distance cap; the ghost roams).
        private static PlayerControl CurrentTemplate() {
            var picked = Helpers.playerById(selectedTemplateId);
            if (IsValidTemplate(picked)) return picked;
            return NearestLivingPlayer(float.MaxValue);
        }

        // K cycles the manifest template through the living players (by player id, wraps around).
        public static void PollTemplateSelection() {
            try {
                if (!Poltergeist.IsLocalPoltergeist() || IsManifested) return;
                if (Input.GetKeyDown(KeyCode.K)) {
                    var living = PlayerControl.AllPlayerControls.ToArray()
                        .Where(IsValidTemplate).OrderBy(p => p.PlayerId).ToList();
                    if (living.Count > 0) {
                        int idx = living.FindIndex(p => p.PlayerId == selectedTemplateId);
                        selectedTemplateId = living[(idx + 1) % living.Count].PlayerId;
                    }
                }
                if (manifestButton != null) {
                    var t = CurrentTemplate();
                    manifestButton.buttonText = t != null && t.Data != null
                        ? $"AS {t.Data.PlayerName} [K]" : "MANIFEST";
                }
            } catch { }
        }

        private static PlayerControl NearestLivingPlayer(float maxDist) {
            if (PlayerControl.LocalPlayer == null) return null;
            Vector2 pos = PlayerControl.LocalPlayer.GetTruePosition();
            PlayerControl best = null;
            float bestD = maxDist;
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (p == null || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
                if (p.PlayerId == PlayerControl.LocalPlayer.PlayerId) continue;
                float d = Vector2.Distance(pos, p.GetTruePosition());
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        // ---- Per-frame (from Poltergeist.HudUpdatePatch) ----

        public static void Tick() {
            PollTemplateSelection();

            if (!IsManifested) {
                // Housekeeping: we force-Show the vent button below while manifested, and nothing in
                // vanilla hides it again for a dead player - so it lingered (overlapping the HAND
                // button). Hide it whenever the ghost is NOT manifested.
                if (Poltergeist.IsLocalPoltergeist()) {
                    var h = HudManager.Instance;
                    if (h != null && h.ImpostorVentButton != null && h.ImpostorVentButton.isActiveAndEnabled)
                        h.ImpostorVentButton.Hide();
                }
                return;
            }

            // Keep the disguise consistent with Camouflager camo / mushroom sabotage (see EnforceLook).
            EnforceLook();

            // Duration is owned by the Poltergeist's client; host is the disconnect fallback.
            if (Time.time >= endTime) {
                if (Poltergeist.IsLocalPoltergeist()) SendEnd(0);
                else if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost
                         && (Poltergeist.poltergeist == null || Poltergeist.poltergeist.Data == null
                             || Poltergeist.poltergeist.Data.Disconnected))
                    ApplyEnd(2);
                return;
            }

            // Show the vent button for the manifested ghost (vanilla only shows it for vent roles).
            if (Poltergeist.IsLocalPoltergeist() && (Poltergeist.ManifestCanVent?.getBool() ?? true)) {
                var hud = HudManager.Instance;
                if (hud != null && hud.ImpostorVentButton != null && !hud.ImpostorVentButton.isActiveAndEnabled)
                    hud.ImpostorVentButton.Show();
            }
        }

        public static void OnMeeting() {
            if (IsManifested) ApplyEnd(2); // runs on every client, no RPC needed
        }

        // ---- Force-render the manifested ghost (vanilla re-hides dead players every FixedUpdate) ----

        // Dead players are drawn with the GHOST animation set (floating, no legs) - PlayerPhysics
        // re-picks the animation every LateUpdate from Data.IsDead. Forcing amDead=false while
        // manifested makes every client play the normal idle/run animations, so the manifest walks
        // and vents like a living player instead of appearing as a ghost.
        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.HandleAnimation))]
        static class AnimationPatch {
            public static void Prefix(PlayerPhysics __instance, [HarmonyArgument(0)] ref bool amDead) {
                try {
                    if (!IsManifested || Poltergeist.poltergeist == null || __instance == null
                        || __instance.myPlayer == null) return;
                    if (__instance.myPlayer.PlayerId != Poltergeist.poltergeist.PlayerId) return;
                    amDead = false;
                } catch { }
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        static class VisibilityPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (!IsManifested || Poltergeist.poltergeist == null || __instance == null) return;
                    if (__instance.PlayerId != Poltergeist.poltergeist.PlayerId) return;
                    if (!__instance.Visible) __instance.Visible = true;
                    ForceOpaque(__instance);
                } catch { }
            }
        }

        // Ghost rendering dims sprites for dead viewers; the manifest must read fully alive for everyone.
        private static void ForceOpaque(PlayerControl player) {
            try {
                if (player == null || player.cosmetics == null) return;
                player.SetHatAndVisorAlpha(1f);
                var body = player.cosmetics.currentBodySprite != null ? player.cosmetics.currentBodySprite.BodySprite : null;
                if (body != null && body.color.a < 1f) {
                    var c = body.color; c.a = 1f; body.color = c;
                }
                if (player.cosmetics.skin != null && player.cosmetics.skin.layer != null
                    && player.cosmetics.skin.layer.color.a < 1f) {
                    var c = player.cosmetics.skin.layer.color; c.a = 1f; player.cosmetics.skin.layer.color = c;
                }
                if (player.cosmetics.nameText != null && player.cosmetics.nameText.color.a < 1f) {
                    var c = player.cosmetics.nameText.color; c.a = 1f; player.cosmetics.nameText.color = c;
                }
            } catch { }
        }

        // ---- Vent access for the manifested ghost ----
        // POSTFIX, not prefix: under HarmonyX a prefix returning false only skips the ORIGINAL -
        // TOR's own Vent.CanUse prefix still ran after ours and re-denied the dead player via its
        // "!pc.IsDead" check, which broke venting entirely. A postfix runs after all prefixes and
        // has the last word.
        [HarmonyPatch(typeof(Vent), nameof(Vent.CanUse))]
        static class VentCanUsePatch {
            public static void Postfix(Vent __instance, ref float __result,
                [HarmonyArgument(0)] NetworkedPlayerInfo pc,
                [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse) {
                try {
                    if (!IsManifested || Poltergeist.poltergeist == null || pc == null) return;
                    if (pc.PlayerId != Poltergeist.poltergeist.PlayerId) return;
                    if (!(Poltergeist.ManifestCanVent?.getBool() ?? true)) {
                        canUse = couldUse = false;
                        __result = float.MaxValue;
                        return;
                    }
                    // Sealed and Jack-in-the-box vents keep their special deny rules from TOR.
                    if (__instance.name.StartsWith("SealedVent_") || __instance.name.StartsWith("JackInTheBoxVent_")) return;
                    couldUse = true;
                    Vector2 pos = Poltergeist.poltergeist.GetTruePosition();
                    __result = Vector2.Distance(pos, __instance.transform.position);
                    canUse = __result <= __instance.UsableDistance;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] Vent.CanUse failed: {e}");
                }
            }
        }

        // ---- Kill interception: a kill on the manifest poofs it (no body), refund per option ----

        [HarmonyPatch(typeof(KillButton), nameof(KillButton.DoClick))]
        [HarmonyPriority(Priority.High)]
        static class KillButtonDoClickPatch {
            public static bool Prefix(KillButton __instance) {
                try {
                    if (!IsManifested || Poltergeist.poltergeist == null) return true;
                    var me = PlayerControl.LocalPlayer;
                    if (me == null || me.Data == null || me.Data.IsDead) return true;
                    if (__instance == null || __instance.isCoolingDown || !me.CanMove) return true;
                    if (Poltergeist.poltergeist.inVent) return true; // can't stab a vent

                    Vector2 here = me.GetTruePosition();
                    float manifestDist = Vector2.Distance(here, Poltergeist.poltergeist.GetTruePosition());
                    if (manifestDist > KillRange()) return true;

                    float realDist = __instance.currentTarget != null
                        ? Vector2.Distance(here, __instance.currentTarget.GetTruePosition()) : float.MaxValue;
                    if (manifestDist > realDist) return true; // a real victim is closer -> normal kill

                    // The manifest "dies": poof everywhere, no body, cooldown refund per option.
                    SendEnd(1);
                    float cooldown = GameOptionsManager.Instance.currentNormalGameOptions.KillCooldown;
                    int refund = Poltergeist.ManifestKillRefund?.getSelection() ?? 1;
                    float timer = refund switch { 0 => cooldown, 1 => cooldown * 0.5f, _ => 0.1f };
                    me.SetKillTimer(timer);
                    __instance.SetTarget(null);
                    return false;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] kill intercept failed: {e}");
                    return true;
                }
            }

            private static float KillRange() {
                try {
                    int idx = Mathf.Clamp(GameOptionsManager.Instance.currentNormalGameOptions.KillDistance, 0, 2);
                    return GameOptionsData.KillDistances[idx];
                } catch { return 1.8f; }
            }
        }
    }
}
