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
        private static float originalSpeed;

        // For controlling visibility (local player only)
        private static float currentAlpha = 1f;
        private static bool wasAbilityActive;

        // ---- Custom RPC (203) subtypes ----
        private const byte RpcId = 203;
        private const byte SubSetScout = 0;
        private const byte SubActivate = 1;
        private const byte SubDeactivate = 2;

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

        public static void TryPatch(Harmony harmony) { }

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
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
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

        private static void ApplySetScout(byte id) {
            scout = Helpers.playerById(id);
            active = scout != null;
            if (active) UCPromotion.Claim(id);
            abilityActive = false;
            abilityEndTime = 0;
            currentAlpha = 1f;
            wasAbilityActive = false;
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Scout] The Scout is {scout.Data?.PlayerName}.");
        }

        private static void ApplyActivate() {
            abilityActive = true;
            float dur = Duration != null ? Duration.getFloat() : 10f;
            abilityEndTime = Time.time + dur;
            if (IsLocalScout()) {
                originalSpeed = PlayerControl.LocalPlayer.MyPhysics.Speed;
                float mult = SpeedMultiplier != null ? SpeedMultiplier.getFloat() : 1.5f;
                PlayerControl.LocalPlayer.MyPhysics.Speed = originalSpeed * mult;
                if (scoutButton != null) scoutButton.Timer = dur;
            }
        }

        private static void ApplyDeactivate() {
            abilityActive = false;
            abilityEndTime = 0;
            if (IsLocalScout() && originalSpeed > 0) {
                PlayerControl.LocalPlayer.MyPhysics.Speed = originalSpeed;
            }
            currentAlpha = 1f;
            if (scoutButton != null) scoutButton.Timer = scoutButton.MaxTimer;
        }

        public static void MarkFromDraft(byte playerId) => ApplySetScout(playerId);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetScout: ApplySetScout(reader.ReadByte()); break;
                        case SubActivate: ApplyActivate(); break;
                        case SubDeactivate: ApplyDeactivate(); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Scout] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                scout = null;
                active = false;
                abilityActive = false;
                abilityEndTime = 0;
                currentAlpha = 1f;
                wasAbilityActive = false;
                originalSpeed = 0;
                scoutButton = null;
            }
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
                    var sprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.InvisButton.png", 115f);
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
                                var w = AmongUsClient.Instance.StartRpcImmediately(
                                    PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
                                w.Write(SubActivate);
                                AmongUsClient.Instance.FinishRpcImmediately(w);
                            }
                        },
                        () => active && IsLocalScout()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove && !abilityActive,
                        () => { abilityActive = false; abilityEndTime = 0; currentAlpha = 1f; },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance, KeyCode.F, false, "SCOUT");
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

                    // Timer check (host-side: auto-end)
                    if (abilityActive && Time.time >= abilityEndTime) {
                        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) {
                            SendDeactivate();
                        } else if (local) {
                            ApplyDeactivate();
                        }
                    }

                    // Speed management
                    if (local && abilityActive) {
                        if (wasAbilityActive != abilityActive) {
                            wasAbilityActive = abilityActive;
                            float speedMult = SpeedMultiplier != null ? SpeedMultiplier.getFloat() : 1.5f;
                            originalSpeed = PlayerControl.LocalPlayer.MyPhysics.Speed / speedMult;
                        }
                        float m = SpeedMultiplier != null ? SpeedMultiplier.getFloat() : 1.5f;
                        PlayerControl.LocalPlayer.MyPhysics.Speed = originalSpeed * m;
                    }

                    // Transparency management (client-side visual)
                    if (local) {
                        float targetAlpha = abilityActive ? GetTransparency() : 1f;
                        currentAlpha = Mathf.Lerp(currentAlpha, targetAlpha, Time.deltaTime * 8f);
                        SetPlayerAlpha(PlayerControl.LocalPlayer, currentAlpha);
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
        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CalculateLightRadius))]
        static class LightPatch {
            public static void Postfix(ref float __result, ShipStatus __instance, [HarmonyArgument(0)] NetworkedPlayerInfo p) {
                try {
                    if (!active || scout == null || p == null) return;
                    if (p.PlayerId == scout.PlayerId && abilityActive && IsAlive(scout)) {
                        __result = __instance.MaxLightRadius * GameOptionsManager.Instance.currentNormalGameOptions.CrewLightMod;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Scout] LightPatch failed: {e}");
                }
            }
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
    }
}
