// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Cursed Pirate (Impostor)
 *
 * User 2026-10-02: a pirate whose curse keeps him aboard after death. Alive he is a plain Impostor
 * (User: no ability of his own). Dead, however he died, his ghost keeps a SPYGLASS:
 *  - He floats to a living non-Impostor (within 3 units, he has to go and look) and marks him.
 *  - Every living Impostor teammate sees the marked player for a few seconds (4 s by default): an
 *    arrow at the screen edge and the player's own dot on the sabotage map.
 *  - Then a cooldown (25 s by default); no limit unless "Marks Per Game" sets one (User). Marks end
 *    at a meeting, nothing works during one. Without a living teammate the spyglass is grey.
 *  - Optional counterplay: the marked player feels a chill (a cold flash and a line in his own chat,
 *    never who or why).
 * Only spawns with at least two Impostors: alone he would have nobody to help.
 *
 * Network: Sub 0 set (host), Sub 1 mark (the Pirate himself, while dead). Every client decides on
 * its own whether it is a living teammate and shows the mark. Impostor tag over a plain Impostor.
 * Options 1774-1779, RPC module 234, draft sentinel 227. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class CursedPirate {
        public static readonly Color Color = Palette.ImpostorRed;
        // the mark's colour for the teammates: old gold, like a treasure map's X
        public static readonly Color MarkColor = new Color(1f, 0.78f, 0.2f);

        // ---- Options (IDs 1774-1779) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption ShowSeconds;
        public static CustomOption Cooldown;
        public static CustomOption MarksPerGame;   // 0 unlimited, else MarkLimits[sel]
        public static CustomOption Chill;

        private static readonly int[] MarkLimits = { 0, 1, 2, 3, 5, 10 };
        private const float Range = 3f;   // world units, same reach as the Poltergeist's hex

        // ---- Runtime state ----
        public static PlayerControl pirate;
        public static bool active;
        private static int marksUsed;              // the Pirate's own client
        private static bool curseNoticeShown;      // the Pirate's own client
        private static PlayerControl currentTarget;
        private static CustomButton spyglassButton;
        // teammates: who is marked and until when (Time.time)
        private static readonly Dictionary<byte, float> marks = new Dictionary<byte, float>();
        private static readonly Dictionary<byte, Arrow> arrows = new Dictionary<byte, Arrow>();
        private static readonly Dictionary<byte, SpriteRenderer> mapPoints = new Dictionary<byte, SpriteRenderer>();

        private const byte RpcId = UnknownsCollectionPlugin.CursedPirateRpcId;
        private const byte SubSet = 0;    // playerId          host -> everyone
        private const byte SubMark = 1;   // targetId          Pirate -> everyone

        private static RoleInfo info;
        public static RoleInfo CursedPirateInfo() => info ??= new RoleInfo(
            "Cursed Pirate", Color, "Even dead, your spyglass shows your crew their prey",
            "Even dead, point out prey to your crew", RoleId.Impostor);

        private static readonly System.Random rnd = new System.Random();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1774, Types.Impostor, "Cursed Pirate", CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1775, Types.Impostor, "Cursed Pirate Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                ShowSeconds = CustomOption.Create(1776, Types.Impostor, "Marked Player Shown For (s)", 4f, 2f, 8f, 1f, SpawnRate);
                Cooldown = CustomOption.Create(1777, Types.Impostor, "Spyglass Cooldown", 25f, 10f, 45f, 2.5f, SpawnRate);
                MarksPerGame = new CustomOption(1778, Types.Impostor, "Marks Per Game",
                    new string[] { "Unlimited", "1", "2", "3", "5", "10" }, "Unlimited", SpawnRate, false);
                Chill = CustomOption.Create(1779, Types.Impostor, "The Marked Player Feels A Chill", false, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[CursedPirate] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[CursedPirate] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) => UCRpc.Register(RpcId, HandleModuleRpc);

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static bool Alive(PlayerControl p) => p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static bool IsImpostor(PlayerControl p) => p != null && p.Data != null && p.Data.Role != null && p.Data.Role.IsImpostor;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        private static int ImpostorCount() => PlayerControl.AllPlayerControls.ToArray().Count(IsImpostor);
        public static bool IsLocalPirate() =>
            active && pirate != null && PlayerControl.LocalPlayer != null && pirate.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static bool TeammateAlive() =>
            pirate != null && PlayerControl.AllPlayerControls.ToArray().Any(p => Alive(p) && IsImpostor(p) && p.PlayerId != pirate.PlayerId);
        private static int MarkLimit => MarkLimits[Mathf.Clamp(MarksPerGame?.getSelection() ?? 0, 0, MarkLimits.Length - 1)];
        private static float ShowFor => ShowSeconds?.getFloat() ?? 4f;

        /// <summary>The draft and the random pick: only with at least two Impostors.</summary>
        public static bool EnoughImpostors() => ImpostorCount() >= 2;

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
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[CursedPirate] SendSet failed: {e}"); }
        }

        private static void SendMark(byte target) {
            try {
                var w = BeginRpc(SubMark);
                w.Write(target);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyMark(target);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[CursedPirate] SendMark failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                byte val = reader.ReadByte();
                if (sub == SubSet) { if (UCRpc.RequireHost("CursedPirate.Set")) ApplySet(val); }
                else if (sub == SubMark) { if (UCRpc.RequireOwnerOrHost(pirate, "CursedPirate.Mark")) ApplyMark(val); }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[CursedPirate] HandleRpc failed: {e}"); }
        }

        private static void ApplySet(byte id) {
            pirate = id == byte.MaxValue ? null : Helpers.playerById(id);
            active = pirate != null;
            marksUsed = 0;
            curseNoticeShown = false;
            ClearMarks();
            if (!active) return;
            UCPromotion.Claim(id);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[CursedPirate] The Cursed Pirate is {pirate.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySet(playerId);

        // Every client: a living teammate sees the mark, the marked player maybe feels a chill.
        private static void ApplyMark(byte targetId) {
            if (!active || pirate == null || pirate.Data == null || !pirate.Data.IsDead || InMeeting()) return;
            var target = Helpers.playerById(targetId);
            if (!Alive(target) || IsImpostor(target)) return;
            var me = PlayerControl.LocalPlayer;
            if (me == null) return;
            if (Alive(me) && IsImpostor(me) && me.PlayerId != pirate.PlayerId) {
                marks[targetId] = Time.time + ShowFor;
                UnknownsCollectionPlugin.Logger?.LogInfo($"[CursedPirate] spyglass: {target.Data.PlayerName} shown for {ShowFor:F0} s.");
            } else if (me.PlayerId == targetId && (Chill?.getBool() ?? false)) {
                try { Helpers.showFlash(new Color(0.55f, 0.8f, 1f, 1f), 0.8f); } catch { }
                try { HudManager.Instance?.Chat?.AddChat(me, UCLocalization.Tr("uc.ui.cursedpirate.chill")); } catch { }
            }
        }

        // ---- the teammates' view: arrows (HUD) and map dots (sabotage map) ----
        private static void ClearMarks() {
            marks.Clear();
            foreach (var a in arrows.Values) try { if (a?.arrow != null) UnityEngine.Object.Destroy(a.arrow); } catch { }
            arrows.Clear();
            foreach (var p in mapPoints.Values) try { if (p != null) UnityEngine.Object.Destroy(p.gameObject); } catch { }
            mapPoints.Clear();
        }

        private static void DropMark(byte id) {
            marks.Remove(id);
            if (arrows.TryGetValue(id, out var a)) { try { if (a?.arrow != null) UnityEngine.Object.Destroy(a.arrow); } catch { } arrows.Remove(id); }
            if (mapPoints.TryGetValue(id, out var p)) { try { if (p != null) UnityEngine.Object.Destroy(p.gameObject); } catch { } mapPoints.Remove(id); }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active) return;
                    // the Pirate's own client: one line when the curse takes over
                    if (IsLocalPirate() && !curseNoticeShown && PlayerControl.LocalPlayer.Data != null
                        && PlayerControl.LocalPlayer.Data.IsDead && !InMeeting()) {
                        curseNoticeShown = true;
                        try { HudManager.Instance?.Chat?.AddChat(PlayerControl.LocalPlayer, UCLocalization.Tr("uc.ui.cursedpirate.cursed")); } catch { }
                    }
                    // the ghost's target outline
                    if (SpyglassUsable()) {
                        currentTarget = NearestTarget();
                        PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, MarkColor);
                    } else currentTarget = null;

                    if (marks.Count == 0) return;
                    var me = PlayerControl.LocalPlayer;
                    bool canSee = Alive(me) && IsImpostor(me) && !InMeeting();
                    foreach (var id in marks.Keys.ToList()) {
                        var t = Helpers.playerById(id);
                        if (!canSee || Time.time >= marks[id] || !Alive(t)) { DropMark(id); continue; }
                        if (!arrows.TryGetValue(id, out var a) || a?.arrow == null) { a = new Arrow(MarkColor); arrows[id] = a; }
                        a.Update(t.transform.position, MarkColor);
                        a.arrow.SetActive(true);
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        static class MapPatch {
            public static void Postfix(MapBehaviour __instance) {
                try {
                    if (!active || __instance == null || __instance.HerePoint == null) return;
                    var ship = MapUtilities.CachedShipStatus;
                    if (ship == null) return;
                    foreach (var kv in marks) {
                        var t = Helpers.playerById(kv.Key);
                        if (t == null) continue;
                        Vector3 v = t.transform.position;
                        v /= ship.MapScale;
                        v.x *= Mathf.Sign(ship.transform.localScale.x);
                        v.z = -2.2f;
                        if (!mapPoints.TryGetValue(kv.Key, out var dot) || dot == null) {
                            dot = UnityEngine.Object.Instantiate(__instance.HerePoint, __instance.HerePoint.transform.parent, true);
                            dot.enabled = true;
                            t.SetPlayerMaterialColors(dot);
                            mapPoints[kv.Key] = dot;
                        }
                        dot.transform.localPosition = v;
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() { try { if (active) ClearMarks(); } catch { } }
        }

        // ---- the ghost's spyglass ----
        private static bool SpyglassUsable() =>
            IsLocalPirate() && PlayerControl.LocalPlayer.Data != null && PlayerControl.LocalPlayer.Data.IsDead && !InMeeting()
            && (MarkLimit == 0 || marksUsed < MarkLimit);

        private static PlayerControl NearestTarget() {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null) return null;
            Vector2 pos = lp.GetTruePosition();
            PlayerControl best = null;
            float bestD = Range;
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (!Alive(p) || IsImpostor(p) || p.PlayerId == lp.PlayerId) continue;
                float d = Vector2.Distance(pos, p.GetTruePosition());
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.CursedPirateIcon ?? Helpers.loadSpriteFromResources("TheOtherRoles.Resources.TrackerButton.png", 115f);
                    spyglassButton = new CustomButton(
                        () => {
                            if (currentTarget == null || !TeammateAlive()) return;
                            marksUsed++;
                            SendMark(currentTarget.PlayerId);
                            spyglassButton.Timer = spyglassButton.MaxTimer;
                        },
                        SpyglassUsable,
                        () => currentTarget != null && TeammateAlive(),
                        () => { if (spyglassButton != null) spyglassButton.Timer = spyglassButton.MaxTimer; },
                        sprite,
                        CustomButton.ButtonPositions.upperRowCenter,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.cursedpirate.button"));
                    spyglassButton.MaxTimer = Cooldown?.getFloat() ?? 25f;
                    spyglassButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[CursedPirate] Button creation failed: {e}");
                }
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
                    if (!EnoughImpostors()) return;
                    if (rnd.Next(1, 101) > SpawnRate.getSelection() * 10) return;
                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[CursedPirate] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || pirate == null || p == null || p != pirate || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++)
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) { __result[i] = CursedPirateInfo(); replaced = true; }
                    if (!replaced) __result.Insert(0, CursedPirateInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[CursedPirate] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            pirate = null;
            active = false;
            marksUsed = 0;
            curseNoticeShown = false;
            currentTarget = null;
            ClearMarks();
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("CursedPirate", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("CursedPirate", FullReset);
        }
    }
}
