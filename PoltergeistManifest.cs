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

        private static TheOtherRoles.Objects.CustomButton manifestButton;

        public static void Reset() {
            IsManifested = false;
            templateId = byte.MaxValue;
            endTime = 0;
            manifestButton = null;
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

            // Wear the template's identity (Morphling mechanism).
            var outfit = templatePlayer.Data.DefaultOutfit;
            ghost.setLook(templatePlayer.Data.PlayerName, outfit.ColorId, outfit.HatId,
                outfit.VisorId, outfit.SkinId, outfit.PetId);

            PoltergeistFx.SpawnPoof(ghost.GetTruePosition());
            UCAssets.PlayManifest();

            // Physicality only on the ghost's own client (movement is client-authoritative there).
            if (Poltergeist.IsLocalPoltergeist()) SetPhysical(true);
        }

        private static void ApplyEnd(byte reason) {
            var ghost = Poltergeist.poltergeist;
            IsManifested = false;
            templateId = byte.MaxValue;
            endTime = 0;

            if (ghost != null && ghost.Data != null) {
                // Back to the own identity...
                var own = ghost.Data.DefaultOutfit;
                ghost.setLook(ghost.Data.PlayerName, own.ColorId, own.HatId, own.VisorId, own.SkinId, own.PetId);
                // ...and the poof (silent on meeting start - everyone is teleported anyway).
                if (reason != 2) {
                    PoltergeistFx.SpawnPoof(ghost.GetTruePosition());
                    UCAssets.PlayPoof(ghost.GetTruePosition());
                }
            }

            if (Poltergeist.IsLocalPoltergeist()) SetPhysical(false);
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

        // The manifest may only start on clear ground - never inside ship geometry.
        private static bool StandingClear() {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null) return false;
                var hits = Physics2D.OverlapCircleAll(me.GetTruePosition(), 0.3f, Constants.ShipAndObjectsMask);
                foreach (var h in hits)
                    if (h != null && !h.isTrigger) return false;
                return true;
            } catch { return false; }
        }

        // ---- Button ----

        public static void CreateButton(HudManager hud) {
            manifestButton = new TheOtherRoles.Objects.CustomButton(
                () => {
                    if (IsManifested) return;
                    var template = NearestLivingPlayer(3f);
                    if (template == null || !StandingClear()) return;
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
                      && NearestLivingPlayer(3f) != null && StandingClear(),
                () => { },
                UCAssets.ManifestIcon,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowLeft,
                hud, KeyCode.T, false, "MANIFEST");
            manifestButton.MaxTimer = 1f; manifestButton.Timer = 0f;
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
            if (!IsManifested) return;

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

        // ---- Vent access for the manifested ghost (runs BEFORE TOR's VentCanUsePatch) ----

        [HarmonyPatch(typeof(Vent), nameof(Vent.CanUse))]
        [HarmonyPriority(Priority.High)]
        static class VentCanUsePatch {
            public static bool Prefix(Vent __instance, ref float __result,
                [HarmonyArgument(0)] NetworkedPlayerInfo pc,
                [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse) {
                try {
                    if (!IsManifested || Poltergeist.poltergeist == null || pc == null) return true;
                    if (pc.PlayerId != Poltergeist.poltergeist.PlayerId) return true;
                    if (!(Poltergeist.ManifestCanVent?.getBool() ?? true)) {
                        canUse = couldUse = false;
                        __result = float.MaxValue;
                        return false;
                    }
                    couldUse = true;
                    Vector2 pos = Poltergeist.poltergeist.GetTruePosition();
                    __result = Vector2.Distance(pos, __instance.transform.position);
                    canUse = __result <= __instance.UsableDistance;
                    return false;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Poltergeist] Vent.CanUse failed: {e}");
                    return true;
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
