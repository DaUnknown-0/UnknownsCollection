// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Maniac {
        public static readonly Color Color = Palette.ImpostorRed;

        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption BombCooldown;
        public static CustomOption UnawareDelay;
        public static CustomOption PassWindow;
        public static CustomOption ExplosionRange;

        public static PlayerControl maniac;
        public static bool active;

        // Bomb state (host-authoritative)
        public static PlayerControl bombCarrier;    // current bomb carrier (null = no active bomb)
        public static bool bombDetected;            // carrier has been warned
        public static float bombPlacedAt;           // Time.time when bomb was planted
        public static float bombDetectedAt;         // Time.time when carrier was warned
        private static bool bombKillUsed;           // prevent double-kill on explosion

        private const byte RpcId = 199;
        private const byte SubSetManiac = 0;
        private const byte SubPlantBomb = 1;   // targetId
        private const byte SubWarnBomb = 2;    // carrierId
        private const byte SubPassBomb = 3;    // oldCarrierId, newCarrierId
        private const byte SubExplode = 4;     // victimId
        private const byte SubClear = 5;

        private static RoleInfo maniacInfo;
        public static RoleInfo ManiacInfo() => maniacInfo ??= new RoleInfo(
            "Maniac", Color, "Plant a bomb on a player that can be passed before it explodes",
            "Plant a bomb on a player that can be passed before it explodes", RoleId.Impostor);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1490, Types.Impostor, "Maniac",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1491, Types.Impostor, "Maniac Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                BombCooldown = CustomOption.Create(1492, Types.Impostor, "Maniac Bomb Cooldown",
                    25f, 10f, 60f, 2.5f, SpawnRate);
                UnawareDelay = CustomOption.Create(1493, Types.Impostor, "Maniac Unaware Delay",
                    8f, 2f, 30f, 1f, SpawnRate);
                PassWindow = CustomOption.Create(1494, Types.Impostor, "Maniac Pass Window",
                    10f, 3f, 30f, 1f, SpawnRate);
                ExplosionRange = CustomOption.Create(1495, Types.Impostor, "Maniac Explosion Range",
                    1.5f, 0.5f, 4f, 0.5f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Maniac] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { }

        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalManiac() =>
            maniac != null && PlayerControl.LocalPlayer != null && maniac.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        public static bool LocalHasBomb() =>
            bombCarrier != null && PlayerControl.LocalPlayer != null
            && bombCarrier.PlayerId == PlayerControl.LocalPlayer.PlayerId && bombDetected;

        // Unchecked murder RPC byte (from TOR's enum)
        internal static byte uncheckedMurderRpc = 108;

        private static float BombUnawareDelay() => UnawareDelay != null ? UnawareDelay.getFloat() : 8f;
        private static float BombPassWindow() => PassWindow != null ? PassWindow.getFloat() : 10f;
        private static float BombRange() => ExplosionRange != null ? ExplosionRange.getFloat() : 1.5f;

        private static bool IsPlainImpostor(PlayerControl p) => UCPromotion.IsPlainImpostor(p);

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetManiac(byte id) {
            try {
                var w = BeginRpc(SubSetManiac);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetManiac(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] SendSetManiac failed: {e}"); }
        }

        private static void SendPlantBomb(byte targetId) {
            try {
                var w = BeginRpc(SubPlantBomb);
                w.Write(targetId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyPlantBomb(targetId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] SendPlantBomb failed: {e}"); }
        }

        private static void SendWarnBomb(byte carrierId) {
            try {
                var w = BeginRpc(SubWarnBomb);
                w.Write(carrierId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyWarnBomb(carrierId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] SendWarnBomb failed: {e}"); }
        }

        private static void SendPassBomb(byte oldCarrierId, byte newCarrierId) {
            try {
                var w = BeginRpc(SubPassBomb);
                w.Write(oldCarrierId);
                w.Write(newCarrierId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyPassBomb(oldCarrierId, newCarrierId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] SendPassBomb failed: {e}"); }
        }

        private static void SendExplode(byte victimId) {
            try {
                var w = BeginRpc(SubExplode);
                w.Write(victimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyExplode(victimId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] SendExplode failed: {e}"); }
        }

        private static void SendClear() {
            try {
                var w = BeginRpc(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] SendClear failed: {e}"); }
        }

        private static void ApplySetManiac(byte id) {
            maniac = Helpers.playerById(id);
            active = maniac != null;
            if (active) UCPromotion.Claim(id);
            ClearBomb();
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Maniac] The Maniac is {maniac.Data?.PlayerName}.");
        }

        private static void ApplyPlantBomb(byte targetId) {
            bombCarrier = Helpers.playerById(targetId);
            bombDetected = false;
            bombPlacedAt = Time.time;
            bombKillUsed = false;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Maniac] Bomb planted on {bombCarrier?.Data?.PlayerName}.");
        }

        private static void ApplyWarnBomb(byte carrierId) {
            if (bombCarrier != null && bombCarrier.PlayerId == carrierId) {
                bombDetected = true;
                bombDetectedAt = Time.time;
                // Play bomb warning sound for the carrier
                if (PlayerControl.LocalPlayer != null
                    && PlayerControl.LocalPlayer.PlayerId == carrierId)
                    SoundEffectsManager.play("bombFuseBurning");
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Maniac] Bomb detected by carrier {bombCarrier.Data?.PlayerName}.");
            }
        }

        private static void ApplyPassBomb(byte oldCarrierId, byte newCarrierId) {
            if (bombCarrier != null && bombCarrier.PlayerId == oldCarrierId) {
                // Stop the fuse sound for the old carrier
                SoundEffectsManager.stop("bombFuseBurning");
                bombCarrier = Helpers.playerById(newCarrierId);
                bombDetected = false;
                bombPlacedAt = Time.time;
                bombKillUsed = false;
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Maniac] Bomb passed to {bombCarrier?.Data?.PlayerName}.");
            }
        }

        private static void ApplyExplode(byte victimId) {
            if (bombCarrier != null && bombCarrier.PlayerId == victimId) {
                SoundEffectsManager.stop("bombFuseBurning");
                // Red explosion flash for everyone nearby
                var victim = Helpers.playerById(victimId);
                if (victim != null) {
                    float range = BombRange();
                    var me = PlayerControl.LocalPlayer;
                    if (me != null && Vector2.Distance(me.GetTruePosition(), victim.GetTruePosition()) <= range)
                        Helpers.showFlash(new Color(1f, 0.3f, 0f, 0.6f), 0.4f);
                }
                ClearBomb();
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Maniac] Bomb exploded on player {victimId}.");
            }
        }

        private static void ApplyClear() => ClearBomb();

        private static void ClearBomb() {
            bombCarrier = null;
            bombDetected = false;
            bombKillUsed = false;
        }

        public static void MarkFromDraft(byte playerId) => ApplySetManiac(playerId);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetManiac: ApplySetManiac(reader.ReadByte()); break;
                        case SubPlantBomb: ApplyPlantBomb(reader.ReadByte()); break;
                        case SubWarnBomb: ApplyWarnBomb(reader.ReadByte()); break;
                        case SubPassBomb: {
                            byte oldId = reader.ReadByte();
                            byte newId = reader.ReadByte();
                            ApplyPassBomb(oldId, newId);
                            break;
                        }
                        case SubExplode: ApplyExplode(reader.ReadByte()); break;
                        case SubClear: ApplyClear(); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                maniac = null;
                active = false;
                ClearBomb();
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

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSetManiac(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] IntroEnd pick failed: {e}");
                }
            }
        }

        // Per-frame bomb state machine (host-only): hidden countdown -> detected -> pass/explode
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || bombCarrier == null || !IsAlive(bombCarrier) || InMeeting()) {
                        if (bombCarrier != null && InMeeting()) ClearBomb();
                        return;
                    }

                    float elapsed = Time.time - bombPlacedAt;
                    float unaware = BombUnawareDelay();
                    float passWindow = BombPassWindow();

                    if (!bombDetected) {
                        // Hidden -> Detected after unaware delay
                        if (elapsed >= unaware) {
                            SendWarnBomb(bombCarrier.PlayerId);
                        }
                    } else {
                        // Detected: check pass window expiration
                        float detectedElapsed = Time.time - bombDetectedAt;
                        if (detectedElapsed >= passWindow && !bombKillUsed) {
                            bombKillUsed = true;
                            // Explode: kill the carrier
                            byte victimId = bombCarrier.PlayerId;
                            SendExplode(victimId);
                            RpcUncheckedMurder(victimId, victimId);
                            SendClear();
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] bomb tick failed: {e}");
                }
            }

            private static void RpcUncheckedMurder(byte sourceId, byte targetId) {
                try {
                    MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                        PlayerControl.LocalPlayer.NetId, uncheckedMurderRpc, SendOption.Reliable, -1);
                    w.Write(sourceId);
                    w.Write(targetId);
                    w.Write((byte)0);
                    AmongUsClient.Instance.FinishRpcImmediately(w);
                    RPCProcedure.uncheckedMurderPlayer(sourceId, targetId, 0);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] RpcUncheckedMurder failed: {e}");
                }
            }
        }

        // Per-frame: show the PASS button to the bomb carrier, and outline for the Maniac's target
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class HudVisualPatch {
            public static void Postfix() {
                try {
                    // Maniac's targeting outline
                    if (IsLocalManiac() && !InMeeting() && bombCarrier == null
                        && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead) {
                        if (currentTarget != null)
                            PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, Color);
                    }

                    // Bomb carrier warning text (local)
                    if (LocalHasBomb() && !InMeeting()) {
                        var me = PlayerControl.LocalPlayer;
                        if (me != null && me.cosmetics?.nameText != null) {
                            var t = me.cosmetics.nameText.text;
                            if (!t.Contains("BOMB"))
                                me.cosmetics.nameText.text = t + " <color=#FF0000><b>[BOMB!]</b></color>";
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] visual update failed: {e}");
                }
            }
        }

        // Meeting: clear bomb, reset cooldown
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                if (active) {
                    ClearBomb();
                    if (bombButton != null) bombButton.Timer = bombButton.MaxTimer;
                }
            }
        }

        // ---- Buttons ----
        private static PlayerControl currentTarget;
        private static TheOtherRoles.Objects.CustomButton bombButton;
        private static TheOtherRoles.Objects.CustomButton passButton;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var bombSprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.Bomb_Button_Plant.png", 115f);
                    var passSprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.Bomb_Button_Defuse.png", 115f);

                    bombButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            var target = PlayerControlFixedUpdatePatch.setTarget(true);
                            if (target == null || bombCarrier != null) return;
                            currentTarget = null;
                            SendPlantBomb(target.PlayerId);
                            bombButton.Timer = bombButton.MaxTimer;
                        },
                        () => active && IsLocalManiac()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead
                              && bombCarrier == null,
                        () => PlayerControl.LocalPlayer.CanMove
                              && PlayerControlFixedUpdatePatch.setTarget(true) != null,
                        () => { },
                        bombSprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance, KeyCode.F, false, "BOMB");
                    bombButton.MaxTimer = BombCooldown != null ? BombCooldown.getFloat() : 25f;
                    bombButton.Timer = 10f;

                    // PASS button shown to the bomb carrier
                    passButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            var target = PlayerControlFixedUpdatePatch.setTarget();
                            if (target == null || bombCarrier == null) return;
                            SendPassBomb(bombCarrier.PlayerId, target.PlayerId);
                            passButton.Timer = 2f;
                        },
                        () => active && LocalHasBomb()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove
                              && PlayerControlFixedUpdatePatch.setTarget() != null,
                        () => { },
                        passSprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowLeft,
                        __instance, KeyCode.G, false, "PASS");
                    passButton.MaxTimer = 0f;
                    passButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] Button creation failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || maniac == null || p == null || p != maniac || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = ManiacInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, ManiacInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Maniac] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}