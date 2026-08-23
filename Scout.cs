// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Scout (Crewmate)
 *
 * A normal TOR Crewmate is silently promoted to "The Scout" at game start (host-authoritative pick,
 * broadcast via RPC 203). The Scout has a CustomButton to activate "Transparent Mode" for a configurable
 * duration. While active:
 *   - The Scout's movement speed is boosted (configurable multiplier)
 *   - The Scout becomes semi-transparent (configurable alpha %, 10% steps)
 *   - Lights sabotage does not reduce the Scout's vision
 *
 * Options live in the 1530-1535 block. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;
using AmongUs.GameOptions;

namespace UnknownsCollection {
    public static class Scout {
        // ---- Theme ----
        public static readonly Color Color = new Color(0.25f, 0.85f, 0.70f); // teal

        // ---- Options (IDs 1530-1535) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption Duration;
        public static CustomOption Cooldown;
        public static CustomOption SpeedMultiplier;
        public static CustomOption Transparency;

        // ---- Runtime state ----
        public static PlayerControl scout;
        public static bool active;
        public static bool abilityActive;
        public static float abilityEndTime;

        // For controlling visibility (local player only)
        private static float currentAlpha = 1f;

        // Synced transparency alpha from RPC (for non-Scout clients)
        private static float syncedScoutAlpha = 1f;
        // Observer-side smoothing: every OTHER client lerps its own local view of the Scout's alpha
        // toward syncedScoutAlpha instead of snapping straight to it (see HudUpdatePatch below) - the
        // Scout's own client already had this smoothing via currentAlpha, observers didn't.
        private static float observedAlpha = 1f;

        // ---- Custom RPC subtypes: module byte 203 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = 203;
        private const byte SubSetScout = 0;
        private const byte SubActivate = 1;
        private const byte SubDeactivate = 2;
        private const byte SubTransparency = 3; // alpha(float) — broadcast to sync transparency on Scout to others

        // ---- Role identity ----
        private static RoleInfo scoutInfo;
        public static RoleInfo ScoutInfo() => scoutInfo ??= new RoleInfo(
            "Scout", Color, "Go transparent and fast; lights don't hinder you",
            "Go transparent and fast; lights don't hinder you", RoleId.Crewmate);

        private static TheOtherRoles.Objects.CustomButton scoutButton;

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1530, Types.Crewmate, "Scout",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1531, Types.Crewmate, "Scout Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                Duration = CustomOption.Create(1532, Types.Crewmate, "Scout Ability Duration",
                    10f, 5f, 30f, 1f, SpawnRate);
                Cooldown = CustomOption.Create(1533, Types.Crewmate, "Scout Ability Cooldown",
                    25f, 10f, 60f, 1f, SpawnRate);
                SpeedMultiplier = CustomOption.Create(1534, Types.Crewmate, "Scout Speed Multiplier",
                    1.5f, 1.0f, 2.5f, 0.25f, SpawnRate);
                Transparency = CustomOption.Create(1535, Types.Crewmate, "Scout Transparency (%)",
                    30f, 0f, 100f, 10f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Scout] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Scout] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            // Receiver registration for the shared UC channel (UCRpc.CallId = 230). Every module
            // registers here even when it has no Harmony work left to do - TryPatch is the single
            // place UnknownsCollectionPlugin.Load() calls for every module.
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalScout() =>
            scout != null && PlayerControl.LocalPlayer != null && scout.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static float GetTransparency() {
            float t = Transparency != null ? Transparency.getFloat() : 30f;
            return Mathf.Clamp01(t / 100f);
        }

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = UCRpc.Begin(RpcId); // shared UC channel; RpcId is the module byte
            w.Write(subtype);
            return w;
        }

        public static void SendSetScout(byte id) {
            try {
                var w = BeginRpc(SubSetScout);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetScout(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Scout] SendSetScout failed: {e}"); }
        }

        public static void SendActivate() {
            try {
                var w = BeginRpc(SubActivate);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyActivate();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Scout] SendActivate failed: {e}"); }
        }

        public static void SendDeactivate() {
            try {
                var w = BeginRpc(SubDeactivate);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyDeactivate();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Scout] SendDeactivate failed: {e}"); }
        }

        private static void SendTransparency(float alpha) {
            try {
                var w = BeginRpc(SubTransparency);
                w.Write(alpha);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyTransparency(alpha);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Scout] SendTransparency failed: {e}"); }
        }

        private static void ApplySetScout(byte id) {
            scout = Helpers.playerById(id);
            active = scout != null;
            if (active) UCPromotion.Claim(id);
            abilityActive = false;
            abilityEndTime = 0;
            currentAlpha = 1f;
            observedAlpha = 1f;
            // Also clear the synced TARGET alpha: it survives a game that ends mid-ability (ApplyDeactivate
            // never fires) and the observer branch below writes it onto the next round's Scout every frame,
            // rendering the NEW Scout transparent from second 0 - a full role reveal.
            syncedScoutAlpha = 1f;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Scout] The Scout is {scout.Data?.PlayerName}.");
        }

        private static void ApplyActivate() {
            abilityActive = true;
            float dur = Duration != null ? Duration.getFloat() : 10f;
            abilityEndTime = Time.time + dur;
            // Whoosh + poof are now Scout-only (per design): PlayScoutWhoosh uses PlayAt with no
            // line-of-sight check, so an ungated cue leaked the Scout's exact position + activation
            // timing through walls - beyond the transparency change, which is only visible on sight.
            // The transparency itself stays public via SendTransparency below.
            if (IsLocalScout() && scout != null) {
                UCAssets.PlayScoutWhoosh(scout.GetTruePosition());
                CrewFx.SpawnPoof(scout.GetTruePosition(), Color);
            }
            // The speed itself is NOT written here any more (AUDIT M-6): `abilityActive` alone
            // drives the velocity multiply in the FixedUpdate patches at the bottom of this file.
            // See SpeedMult there for why the absolute MyPhysics.Speed write had to go.
            if (IsLocalScout()) {
                if (scoutButton != null) scoutButton.Timer = dur;
                // Broadcast transparency to other clients
                float alpha = GetTransparency();
                SendTransparency(alpha);
            }
        }

        private static void ApplyDeactivate() {
            abilityActive = false;
            abilityEndTime = 0;
            // Scout-only, same rationale as ApplyActivate (no LOS on PlayAt -> would leak through walls).
            if (IsLocalScout() && scout != null) {
                UCAssets.PlayScoutWhoosh(scout.GetTruePosition(), 0.4f);
                CrewFx.SpawnPoof(scout.GetTruePosition(), Color);
            }
            // Nothing to restore (AUDIT M-6): the speed was never written, it was multiplied onto
            // the velocity each tick, and `abilityActive` is false from here on.
            currentAlpha = 1f;
            if (scoutButton != null) scoutButton.Timer = scoutButton.MaxTimer;
            // Apply (not send) - ApplyDeactivate() itself already runs on every client, either because the
            // Scout's own client called SendDeactivate() or because the host's disconnect fallback did, so
            // resetting the alpha locally here is enough; broadcasting another RPC on top would just be a
            // redundant round trip and, in the host-fallback case (Scout disconnected -> IsLocalScout() is
            // false everywhere), used to never fire at all, leaving the disconnected Scout stuck transparent.
            ApplyTransparency(1f);
        }

        private static void ApplyTransparency(float alpha) {
            // Only sets the TARGET now - observers lerp their own local view toward it every frame in
            // HudUpdatePatch below instead of snapping straight to it (that used to be a hard pop-in/out).
            syncedScoutAlpha = alpha;
        }

        public static void MarkFromDraft(byte playerId) => ApplySetScout(playerId);

        // RPC receiver, registered on the shared UC channel in TryPatch. UCRpc's dispatcher
        // already consumed the module byte, so this starts at the subtype byte - the wire
        // format behind the module byte is byte-for-byte what the old per-callId RPC used.
        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSetScout: { byte id = reader.ReadByte();
                        // Host-authoritative role assignment (host pick in IntroCutscene.OnDestroy / UCRoleDraft) - a
                    // forged one would let any client declare any player this role (AUDIT H-3).
                        if (UCRpc.RequireHost("Scout.SetScout")) ApplySetScout(id); break; }
                    // Owner-authored, with the host as the documented fallback sender (HudUpdatePatch
                    // deactivates on the host's behalf if the Scout disconnects mid-effect), which is
                    // exactly what RequireOwnerOrHost allows. Unguarded, anyone could switch the
                    // ability on and off for the Scout and desync its timer (AUDIT M-14).
                    case SubActivate:
                        if (UCRpc.RequireOwnerOrHost(scout, "Scout.Activate")) ApplyActivate();
                        break;
                    case SubDeactivate:
                        if (UCRpc.RequireOwnerOrHost(scout, "Scout.Deactivate")) ApplyDeactivate();
                        break;
                    case SubTransparency: { float alpha = reader.ReadSingle();
                        if (UCRpc.RequireOwnerOrHost(scout, "Scout.Transparency")) ApplyTransparency(alpha);
                        break; }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Scout] HandleRpc failed: {e}");
            }
        }

        // PlayerId-keyed state is cleared on OnGameJoined as well as on resetVariables
        // (AUDIT M-12). PlayerIds are handed out per LOBBY, and resetVariables only ever
        // arrives from a host that has this mod - so joining a vanilla host, or leaving a
        // lobby abnormally, used to carry the previous game's ids into the next one and let
        // them act on whoever happens to reuse them. Same belt-and-suspenders rule the
        // Silencer and the Shade already followed; the body is shared so the two entry
        // points can never drift apart.
        private static void ClearState() {
            scout = null;
            active = false;
            abilityActive = false;
            abilityEndTime = 0;
            currentAlpha = 1f;
            observedAlpha = 1f;
            syncedScoutAlpha = 1f; // synced target must not leak into the next round (see ApplySetScout)
            // scoutButton deliberately kept (resetVariables runs after HudManager.Start).
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Scout", ClearState);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() => UCResetGuard.Run("Scout", ClearState);
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    SendSetScout(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Scout] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Button creation ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.ScoutIcon
                        ?? Helpers.loadSpriteFromResources("TheOtherRoles.Resources.InvisButton.png", 115f);
                    scoutButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            if (abilityActive) return;
                            if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) {
                                SendActivate();
                            } else {
                                // Client asks host to activate via RPC (host authorative)
                                // For simplicity: client sends RPC, host receives and broadcasts
                                // Actually, since ability is visual+speed (client side), let the client trigger it
                                // and broadcast to others for transparency sync
                                ApplyActivate();
                                var w = BeginRpc(SubActivate); // shared UC channel (was an inline copy of BeginRpc)
                                AmongUsClient.Instance.FinishRpcImmediately(w);
                            }
                        },
                        () => active && IsLocalScout()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove && !abilityActive,
                        () => { if (IsLocalScout()) SendDeactivate(); },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.scout.button_scout"));
                    scoutButton.MaxTimer = Cooldown != null ? Cooldown.getFloat() : 25f;
                    scoutButton.Timer = 10f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Scout] Button creation failed: {e}");
                }
            }
        }

        // ---- Update: manage ability timer, speed, transparency ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active || scout == null) return;
                    bool local = IsLocalScout();

                    // Timer check: the Scout's own client is the authority for its timer (the button's
                    // hasButton gate is IsLocalScout()-only), broadcasting via RPC so non-host scouts also
                    // sync everyone else. The host still covers it as a fallback so play continues even if
                    // the Scout disconnects mid-effect.
                    if (abilityActive && Time.time >= abilityEndTime) {
                        if (local) {
                            SendDeactivate();
                        } else if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) {
                            SendDeactivate();
                        }
                    }

                    // The per-frame re-stamping of MyPhysics.Speed that used to sit here is gone
                    // (AUDIT M-6). It existed to win the absolute-write fight against the Werewolf
                    // and the Poltergeist hex; with the speed applied multiplicatively there is
                    // nothing left to fight over and nothing to re-stamp.

                    // Transparency management (client-side visual)
                    if (local) {
                        float targetAlpha = abilityActive ? GetTransparency() : 1f;
                        currentAlpha = Mathf.Lerp(currentAlpha, targetAlpha, Time.deltaTime * 8f);
                        SetPlayerAlpha(PlayerControl.LocalPlayer, currentAlpha);
                    } else {
                        // Observer view: lerp toward the synced target alpha instead of snapping to it,
                        // mirroring the Scout's own currentAlpha smoothing above (same lerp rate).
                        observedAlpha = Mathf.Lerp(observedAlpha, syncedScoutAlpha, Time.deltaTime * 8f);
                        SetPlayerAlpha(scout, observedAlpha);
                    }

                    // Button timer management
                    if (scoutButton != null) {
                        if (abilityActive) {
                            scoutButton.Timer = Mathf.Max(0, abilityEndTime - Time.time);
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Scout] HudUpdate failed: {e}");
                }
            }
        }

        // ---- Set player transparency ----
        private static void SetPlayerAlpha(PlayerControl player, float alpha) {
            try {
                if (player == null || player.cosmetics == null) return;
                alpha = Mathf.Clamp01(alpha);
                player.SetHatAndVisorAlpha(alpha);
                if (player.cosmetics.currentBodySprite != null && player.cosmetics.currentBodySprite.BodySprite != null) {
                    var c = player.cosmetics.currentBodySprite.BodySprite.color;
                    c.a = alpha;
                    player.cosmetics.currentBodySprite.BodySprite.color = c;
                }
                if (player.cosmetics.skin != null && player.cosmetics.skin.layer != null) {
                    var c = player.cosmetics.skin.layer.color;
                    c.a = alpha;
                    player.cosmetics.skin.layer.color = c;
                }
                if (player.cosmetics.nameText != null) {
                    var c = player.cosmetics.nameText.color;
                    c.a = alpha;
                    player.cosmetics.nameText.color = c;
                }
            } catch { }
        }

        // ---- Light radius: Scout with active ability has full vision ----
        // ---- Light radius: contributed to the central UC vision pipeline (UCVision.cs) ----
        // Was an own CalculateLightRadius postfix until 2026-08-11; five of those raced each other
        // with absolute assignments and no priorities (AUDIT-2026-08-11.md, M-5).
        public static bool WantsFullVision(NetworkedPlayerInfo p) {
            try {
                return active && scout != null && p != null
                       && p.PlayerId == scout.PlayerId && abilityActive && IsAlive(scout);
            } catch { return false; }
        }


        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || scout == null || p == null || p != scout || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = ScoutInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, ScoutInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Scout] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ====================================================================
        // Speed: a velocity multiply, not an absolute MyPhysics.Speed write (AUDIT-2026-08-23, M-6)
        //
        // MyPhysics.Speed is ONE global float per player, and three things used to assign it
        // outright: this ability, the Werewolf's wolf form and the Poltergeist's speed hex. Absolute
        // writes to a shared field do not compose - each one captures whatever the field happened to
        // hold, which may already be another effect's product, and each restore writes back a value
        // that may no longer be current. The measured consequences were real: a hex cast during a
        // scout run left a permanent x1.4 floor once the run ended, and a wolf reverting inside a hex
        // window rebased itself onto the hex's inflated value and kept it for the rest of the round.
        //
        // Multiplying the velocity instead is commutative and stateless: nothing is captured, nothing
        // is restored, and any number of effects stack in any order with the same result. The pattern
        // is PlayerTuning's (PlayerTuning.cs:498-527), which is in turn SaboteurTrap's and
        // TrapperLimp's - which is precisely why those three never collided with anything. The
        // Poltergeist hex is now the only absolute writer left in this mod, so it has nobody to fight.
        //
        // Both sides are patched for the same reason PlayerTuning patches both: the owner's physics
        // drive the real movement, and the remote CustomNetworkTransform drives what everyone else
        // sees between position updates. `abilityActive` and `scout` are RPC-synced, so every client
        // computes the same multiplier.
        // ====================================================================
        private static float SpeedMultFor(PlayerControl p) {
            if (!active || !abilityActive || scout == null || p == null) return 1f;
            if (p.PlayerId != scout.PlayerId) return 1f;
            return SpeedMultiplier != null ? SpeedMultiplier.getFloat() : 1.5f;
        }

        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
        static class SpeedOwnerPatch {
            public static void Postfix(PlayerPhysics __instance) {
                try {
                    if (!active || !abilityActive) return;
                    if (!__instance.AmOwner || __instance.myPlayer == null || __instance.myPlayer.Data == null) return;
                    float m = SpeedMultFor(__instance.myPlayer);
                    if (Mathf.Abs(m - 1f) < 0.0001f) return;
                    // Respect anything that froze the player (Trapper stun, SaboteurTrap, a meeting):
                    // multiplying a zero velocity is a no-op anyway, but CanMove also covers the
                    // frames where the body is being driven by something else entirely.
                    if (__instance.myPlayer.Data.IsDead || !__instance.myPlayer.CanMove) return;
                    __instance.body.velocity *= m;
                } catch { }
            }
        }

        [HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.FixedUpdate))]
        static class SpeedRemotePatch {
            public static void Postfix(CustomNetworkTransform __instance) {
                try {
                    if (!active || !abilityActive) return;
                    if (__instance.AmOwner || __instance.myPlayer == null || __instance.myPlayer.Data == null) return;
                    float m = SpeedMultFor(__instance.myPlayer);
                    if (Mathf.Abs(m - 1f) < 0.0001f) return;
                    if (__instance.myPlayer.Data.IsDead) return;
                    __instance.body.velocity *= m;
                } catch { }
            }
        }
    }
}
