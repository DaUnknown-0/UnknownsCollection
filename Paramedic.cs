// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Paramedic (Crewmate)
 *
 * Fast help saves lives (User 2026-10-02). If the Paramedic reaches a fresh body within a few seconds
 * of the kill, he can bring the victim back. Limited uses per game (1 by default). The killer is told
 * that somebody came back (option, on by default) - never who.
 *
 *  - FRESHNESS: every client stamps each murder with Time.time in a MurderPlayer postfix. The button
 *    offers the nearest body in use range whose stamp is younger than the revive window.
 *  - HOST DECIDES: the Paramedic's client only asks (Sub 1 request). The host re-checks everything
 *    with its own stamps (window + 1 s tolerance for the round trip), refuses during meetings, and
 *    broadcasts the revive (Sub 2). Every client then revives the way Role Control does
 *    (PlayerTuning.ApplyRevive): Revive(), the matching living vanilla role, the body removed, the task
 *    totals recomputed. The revived player's own client snaps itself onto the body's spot.
 *  - Only kills leave bodies, so exiles, guesses and other body-less deaths can never be revived.
 *
 * Crew tag over a plain Crewmate (keeps his tasks). Options 1740-1744, RPC module 225, draft
 * sentinel 222. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Paramedic {
        // Paramedic signal orange-red, warmer than the Medic's green and far from the impostor red.
        public static readonly Color Color = new Color(1f, 0.45f, 0.30f);

        // ---- Options (IDs 1740-1744) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption ReviveWindow;   // seconds after the kill
        public static CustomOption Uses;           // revives per game
        public static CustomOption TellKiller;

        // ---- Runtime state ----
        public static PlayerControl paramedic;
        public static bool active;
        private static int usesLeft;
        private static readonly Dictionary<byte, float> killedAt = new Dictionary<byte, float>();
        private static readonly Dictionary<byte, byte> killerOf = new Dictionary<byte, byte>();
        private static DeadBody currentBody;
        private static CustomButton reviveButton;

        private const byte RpcId = UnknownsCollectionPlugin.ParamedicRpcId;
        private const byte SubSet = 0;       // playerId                 host -> everyone
        private const byte SubRequest = 1;   // victimId                 Paramedic -> host
        private const byte SubRevive = 2;    // victimId, x, y           host -> everyone

        private static RoleInfo info;
        public static RoleInfo ParamedicInfo() => info ??= new RoleInfo(
            "Paramedic", Color, "Reach a fresh body in time and bring the victim back",
            "Revive a fresh body", RoleId.Crewmate);

        private static readonly System.Random rnd = new System.Random();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1740, Types.Crewmate, "Paramedic", CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1741, Types.Crewmate, "Paramedic Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                ReviveWindow = CustomOption.Create(1742, Types.Crewmate, "Revive Window After The Kill (s)",
                    10f, 5f, 30f, 1f, SpawnRate);
                Uses = CustomOption.Create(1743, Types.Crewmate, "Paramedic Revives Per Game", 1f, 1f, 3f, 1f, SpawnRate);
                TellKiller = CustomOption.Create(1744, Types.Crewmate, "The Killer Learns Someone Came Back", true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Paramedic] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) => UCRpc.Register(RpcId, HandleModuleRpc);

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalParamedic() =>
            active && paramedic != null && PlayerControl.LocalPlayer != null && paramedic.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static float Window => ReviveWindow?.getFloat() ?? 10f;

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte sub) {
            var w = UCRpc.Begin(RpcId);
            w.Write(sub);
            return w;
        }

        public static void SendSet(byte id) {
            try {
                var w = BeginRpc(SubSet);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] SendSet failed: {e}"); }
        }

        private static void SendRequest(byte victimId) {
            try {
                if (AmHost()) { HostHandleRequest(paramedic != null ? paramedic.PlayerId : byte.MaxValue, victimId); return; }
                var w = BeginRpc(SubRequest);
                w.Write(victimId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] SendRequest failed: {e}"); }
        }

        private static void SendRevive(byte victimId, Vector2 at) {
            try {
                var w = BeginRpc(SubRevive);
                w.Write(victimId);
                w.Write(at.x);
                w.Write(at.y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyRevive(victimId, at);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] SendRevive failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                switch (sub) {
                    case SubSet: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Paramedic.Set")) ApplySet(id);
                        break;
                    }
                    case SubRequest: {
                        byte victim = reader.ReadByte();
                        if (!AmHost()) break;
                        if (!UCRpc.RequireOwnerOrHost(paramedic, "Paramedic.Request")) break;
                        HostHandleRequest(paramedic.PlayerId, victim);
                        break;
                    }
                    case SubRevive: {
                        byte victim = reader.ReadByte();
                        var at = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                        if (UCRpc.RequireHost("Paramedic.Revive")) ApplyRevive(victim, at);
                        break;
                    }
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] HandleRpc failed: {e}"); }
        }

        private static void ApplySet(byte id) {
            paramedic = id == byte.MaxValue ? null : Helpers.playerById(id);
            active = paramedic != null;
            usesLeft = Mathf.RoundToInt(Uses?.getFloat() ?? 1f);
            if (!active) return;
            UCPromotion.Claim(id);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Paramedic] The Paramedic is {paramedic.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySet(playerId);

        // ---- Host arbitration ----
        private static void HostHandleRequest(byte senderId, byte victimId) {
            string why = null;
            var victim = Helpers.playerById(victimId);
            DeadBody body = BodyOf(victimId);
            if (!active || paramedic == null || paramedic.PlayerId != senderId) why = "not the Paramedic";
            else if (paramedic.Data == null || paramedic.Data.IsDead) why = "Paramedic is dead";
            else if (usesLeft <= 0) why = "no revives left";
            else if (InMeeting()) why = "meeting running";
            else if (victim == null || victim.Data == null || !victim.Data.IsDead || victim.Data.Disconnected) why = "victim not dead";
            else if (body == null) why = "no body";
            else if (!killedAt.TryGetValue(victimId, out float t) || Time.time - t > Window + 1f) why = "too late";
            if (why != null) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Paramedic] revive of {victim?.Data?.PlayerName ?? victimId.ToString()} refused: {why}.");
                return;
            }
            SendRevive(victimId, body.TruePosition);
        }

        private static DeadBody BodyOf(byte victimId) {
            foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                if (b != null && b.ParentId == victimId && !b.Reported) return b;
            return null;
        }

        // ---- The revive (every client) ----
        private static void ApplyRevive(byte victimId, Vector2 at) {
            try {
                var p = Helpers.playerById(victimId);
                if (p == null || p.Data == null || !p.Data.IsDead) return;
                if (InMeeting()) return;
                bool wasImp = p.Data.Role != null && p.Data.Role.IsImpostor;
                p.Revive();
                RoleManager.Instance.SetRole(p, wasImp ? RoleTypes.Impostor : RoleTypes.Crewmate);
                foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                    if (body != null && body.ParentId == victimId) UnityEngine.Object.Destroy(body.gameObject);
                try { GameData.Instance?.RecomputeTaskCounts(); } catch { }
                // TOR's death ledger (Detective, Medic, Hacker vitals, end screen read the FIRST entry):
                // a revived player must leave it, or a second death reports the first killer and time.
                // Same cleanup as Pelican.RevivePlayer and Necromancer.ApplyRaise.
                try { Pelican.DeadPlayersLedger()?.RemoveAll(d => d != null && d.player != null && d.player.PlayerId == victimId); }
                catch { }
                usesLeft = Mathf.Max(0, usesLeft - 1);
                killedAt.Remove(victimId);

                var lp = PlayerControl.LocalPlayer;
                if (lp != null && lp.PlayerId == victimId) {
                    // the owner moves himself; a ghost may have wandered off from his body
                    try { lp.NetTransform.RpcSnapTo(at); } catch { }
                    Helpers.showFlash(Color, 1.5f, UCLocalization.Tr("uc.ui.paramedic.revived"));
                }
                if (lp != null && IsLocalParamedic()) Helpers.showFlash(Color, 1f, UCLocalization.Tr("uc.ui.paramedic.saved", p.Data.PlayerName));
                if (lp != null && (TellKiller?.getBool() ?? true) && killerOf.TryGetValue(victimId, out byte k) && k == lp.PlayerId && k != victimId)
                    Helpers.showFlash(new Color(1f, 1f, 1f, 0.6f), 1.5f, UCLocalization.Tr("uc.ui.paramedic.killer_hint"));
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Paramedic] {p.Data.PlayerName} revived ({usesLeft} revive(s) left).");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] ApplyRevive failed: {e}");
            }
        }

        // ---- Freshness: every client stamps every murder ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderStampPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || target == null || target.Data == null || !target.Data.IsDead) return;
                    killedAt[target.PlayerId] = Time.time;
                    if (__instance != null) killerOf[target.PlayerId] = __instance.PlayerId;
                } catch { }
            }
        }

        // ---- Pick (host, random path) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPickPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (!AmHost() || active) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;
                    if (rnd.Next(1, 101) > SpawnRate.getSelection() * 10) return;
                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Button ----
        private static float nextScan;

        private static void ScanBodies() {
            if (Time.time < nextScan) return;
            nextScan = Time.time + 0.2f;
            currentBody = null;
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || lp.Data == null || lp.Data.IsDead || InMeeting() || usesLeft <= 0) return;
            float range = lp.MaxReportDistance;
            float best = float.MaxValue;
            Vector2 me = lp.GetTruePosition();
            foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>()) {
                if (b == null || b.Reported) continue;
                if (!killedAt.TryGetValue(b.ParentId, out float t) || Time.time - t > Window) continue;
                float d = Vector2.Distance(me, b.TruePosition);
                if (d > range || d >= best) continue;
                if (PhysicsHelpers.AnythingBetween(me, b.TruePosition, Constants.ShipAndObjectsMask, false)) continue;
                best = d;
                currentBody = b;
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.ParamedicIcon ?? Helpers.loadSpriteFromResources("TheOtherRoles.Resources.ShieldButton.png", 115f);
                    reviveButton = new CustomButton(
                        () => {
                            if (currentBody == null) return;
                            SendRequest(currentBody.ParentId);
                            reviveButton.Timer = reviveButton.MaxTimer;
                        },
                        () => IsLocalParamedic() && PlayerControl.LocalPlayer.Data != null
                              && !PlayerControl.LocalPlayer.Data.IsDead && usesLeft > 0,
                        () => { ScanBodies(); return PlayerControl.LocalPlayer.CanMove && currentBody != null; },
                        () => { },
                        sprite,
                        CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.paramedic.button"));
                    reviveButton.MaxTimer = 2f;
                    reviveButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] Button creation failed: {e}");
                }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || paramedic == null || p == null || p != paramedic || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++)
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) { __result[i] = ParamedicInfo(); replaced = true; }
                    if (!replaced) __result.Insert(0, ParamedicInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Paramedic] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            paramedic = null;
            active = false;
            usesLeft = 0;
            killedAt.Clear();
            killerOf.Clear();
            currentBody = null;
            // reviveButton kept: resetVariables runs after HudManager.Start (see Manipulator.cs)
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Paramedic", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Paramedic", FullReset);
        }
    }
}
