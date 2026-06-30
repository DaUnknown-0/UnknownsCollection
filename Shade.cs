// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Shade (Impostor)
 *
 * A normal TOR Impostor is silently promoted to "The Shade" at game start (host-authoritative pick,
 * broadcast via RPC 205). When the Shade kills a player, the victim's body disappears (DeadBody is
 * hidden). If another player (not the Shade) walks within FindDistance of the kill location, the body
 * becomes visible and reportable again.
 *
 * Options live in the 1510-1512 block. See ID-Registry.md.
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

namespace UnknownsCollection {
    public static class Shade {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed;

        // ---- Options (IDs 1510-1512) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption FindDistance;

        // ---- Runtime state ----
        public static PlayerControl shade;
        public static bool active;

        // PlayerId -> kill position, for hidden bodies (host authoritative)
        private static readonly Dictionary<byte, Vector2> hiddenBodies = new();
        // PlayerId -> DeadBody reference, for showing/hiding
        private static readonly Dictionary<byte, DeadBody> hiddenBodyRefs = new();

        // ---- Custom RPC (201) subtypes ----
        private const byte RpcId = 205;
        private const byte SubSetShade = 0;
        private const byte SubHideBody = 1;   // victimId, posX, posY
        private const byte SubRevealBody = 2; // victimId

        // ---- Role identity ----
        private static RoleInfo shadeInfo;
        public static RoleInfo ShadeInfo() => shadeInfo ??= new RoleInfo(
            "Shade", Color, "Your kills vanish; others can find them by proximity",
            "Your kills vanish; others can find them by proximity", RoleId.Impostor);

        private static System.Random rng = new();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1510, Types.Impostor, "Shade",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1511, Types.Impostor, "Shade Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                FindDistance = CustomOption.Create(1512, Types.Impostor, "Shade Find Distance",
                    1.5f, 0.5f, 4f, 0.25f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Shade] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Shade] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalShade() =>
            shade != null && PlayerControl.LocalPlayer != null && shade.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetShade(byte id) {
            try {
                var w = BeginRpc(SubSetShade);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetShade(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Shade] SendSetShade failed: {e}"); }
        }

        private static void SendHideBody(byte victimId, Vector2 pos) {
            try {
                var w = BeginRpc(SubHideBody);
                w.Write(victimId);
                w.Write(pos.x);
                w.Write(pos.y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyHideBody(victimId, pos);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Shade] SendHideBody failed: {e}"); }
        }

        private static void SendRevealBody(byte victimId) {
            try {
                var w = BeginRpc(SubRevealBody);
                w.Write(victimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyRevealBody(victimId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Shade] SendRevealBody failed: {e}"); }
        }

        private static void ApplySetShade(byte id) {
            shade = Helpers.playerById(id);
            active = shade != null;
            if (active) UCPromotion.Claim(id);
            hiddenBodies.Clear();
            hiddenBodyRefs.Clear();
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Shade] The Shade is {shade.Data?.PlayerName}.");
        }

        private static void ApplyHideBody(byte victimId, Vector2 pos) {
            hiddenBodies[victimId] = pos;
            // Find the DeadBody object and disable it
            try {
                foreach (var db in GameObject.FindObjectsOfType<DeadBody>()) {
                    if (db != null && db.ParentId == victimId) {
                        db.gameObject.SetActive(false);
                        hiddenBodyRefs[victimId] = db;
                        break;
                    }
                }
            } catch { }
        }

        private static void ApplyRevealBody(byte victimId) {
            hiddenBodies.Remove(victimId);
            try {
                if (hiddenBodyRefs.TryGetValue(victimId, out var db) && db != null) {
                    db.gameObject.SetActive(true);
                }
                hiddenBodyRefs.Remove(victimId);
            } catch { }
        }

        public static void MarkFromDraft(byte playerId) => ApplySetShade(playerId);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetShade:
                            ApplySetShade(reader.ReadByte());
                            break;
                        case SubHideBody: {
                            byte vid = reader.ReadByte();
                            float x = reader.ReadSingle();
                            float y = reader.ReadSingle();
                            ApplyHideBody(vid, new Vector2(x, y));
                            break;
                        }
                        case SubRevealBody:
                            ApplyRevealBody(reader.ReadByte());
                            break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Shade] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                shade = null;
                active = false;
                hiddenBodies.Clear();
                hiddenBodyRefs.Clear();
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
                    if (rng.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSetShade(candidates[rng.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Shade] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- On murder: hide the body (host broadcasts) ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || shade == null || target == null) return;
                    if (__instance.PlayerId != shade.PlayerId) return; // only Shade's kills
                    if (!IsAlive(shade)) return;

                    Vector2 pos = target.GetTruePosition();
                    SendHideBody(target.PlayerId, pos);
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[Shade] Body of {target.Data?.PlayerName} hidden at {pos}.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Shade] MurderPatch failed: {e}");
                }
            }
        }

        // ---- Proximity check: reveal body if a non-Shade player walks near ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!active || shade == null || hiddenBodies.Count == 0) return;

                    float findDist = FindDistance != null ? FindDistance.getFloat() : 1.5f;

                    // Check each alive player against each hidden body
                    var toReveal = new List<byte>();
                    foreach (var pc in PlayerControl.AllPlayerControls) {
                        if (pc == null || !IsAlive(pc)) continue;
                        if (pc.PlayerId == shade.PlayerId) continue; // shade cannot trigger reveal

                        Vector2 pcPos = pc.GetTruePosition();
                        foreach (var kvp in hiddenBodies) {
                            if (Vector2.Distance(pcPos, kvp.Value) <= findDist) {
                                toReveal.Add(kvp.Key);
                            }
                        }
                    }

                    foreach (byte vid in toReveal) {
                        SendRevealBody(vid);
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[Shade] Body of player {vid} revealed by proximity.");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Shade] HudUpdate proximity check failed: {e}");
                }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || shade == null || p == null || p != shade || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = ShadeInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, ShadeInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Shade] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
