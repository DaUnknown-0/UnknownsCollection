// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Hypnotist (Impostor)
 *
 * Decisions by the user (2026-10-02): the hypnotised player notices nothing, and the Hypnotist picks
 * where that player's vote goes on his own - independent of his own vote.
 *
 *  - ROUND: "Hypnotize" marks one living player in kill range (cooldown, limited uses per game). One
 *    hypnosis at a time; it lasts until the end of the next meeting.
 *  - MEETING: the living Hypnotist gets a small button on every living player's vote area and on the
 *    Skip area. A click picks the redirect target, a second click on the same button clears it. No pick
 *    = the victim's vote stays as it is.
 *  - TALLY (host): a Priority.First prefix on MeetingHud.CheckForEndVoting rewrites the victim's
 *    VotedFor before TOR's own prefix counts the votes AND builds the result states, so the tally and
 *    the result screen agree. With visible votes the victim's icon under the redirect target is the
 *    only tell. The original vote is remembered, so a changed or cleared pick is applied correctly on
 *    the next call.
 *  - NO ESCAPE (option 1754, on by default, User 2026-10-02): a Skip is redirected, and a victim who
 *    does not vote at all gets his vote cast for the picked target when the voting time runs out
 *    (ForceSkipAll prefix). Off: only real player votes are redirected.
 *  - The hypnosis breaks when the Hypnotist dies (option, on by default).
 *
 * Network: the Hypnotist's mark and pick go to everyone (Sub 1/2, owner-authored); the host applies
 * them in the tally. Impostor tag over a plain Impostor. Options 1750-1755, RPC module 227, draft
 * sentinel 224. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using TheOtherRoles.Patches;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Hypnotist {
        public static readonly Color Color = Palette.ImpostorRed;
        // the hypnosis colour: buttons, outline, picked target
        private static readonly Color Spiral = new Color(0.72f, 0.40f, 1f);

        // ---- Options (IDs 1750-1755) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption Cooldown;
        public static CustomOption Uses;
        public static CustomOption CannotEscape;   // skip AND a vote never cast are redirected too
        public static CustomOption BreakOnDeath;
        public static CustomOption ViewMode;       // 1756: Off / Peek / Until The Meeting (HypnotistView)

        // ---- Runtime state ----
        public static PlayerControl hypnotist;
        public static bool active;
        private static int usesLeft;
        private static byte victimId = byte.MaxValue;
        private static byte pick = NoPick;             // player id, SkipPick or NoPick
        private static byte originalVote = NoPick;     // host: the victim's own vote, remembered once
        private const byte SkipPick = 253, NoPick = 255;
        private static PlayerControl currentTarget;
        private static CustomButton hypnotizeButton;

        private const byte RpcId = UnknownsCollectionPlugin.HypnotistRpcId;
        private const byte SubSet = 0;         // playerId                    host -> everyone
        private const byte SubHypnotize = 1;   // victimId                    Hypnotist -> everyone
        private const byte SubPick = 2;        // target (253 skip, 255 none) Hypnotist -> everyone

        private static RoleInfo info;
        public static RoleInfo HypnotistInfo() => info ??= new RoleInfo(
            "Hypnotist", Color, "Hypnotize a player and steer their next vote",
            "Steer a hypnotised player's vote", RoleId.Impostor);

        private static readonly System.Random rnd = new System.Random();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1750, Types.Impostor, "Hypnotist", CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1751, Types.Impostor, "Hypnotist Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                Cooldown = CustomOption.Create(1752, Types.Impostor, "Hypnotize Cooldown", 25f, 10f, 60f, 2.5f, SpawnRate);
                Uses = CustomOption.Create(1753, Types.Impostor, "Hypnoses Per Game", 2f, 1f, 5f, 1f, SpawnRate);
                CannotEscape = CustomOption.Create(1754, Types.Impostor, "Hypnotised Player Cannot Escape", true, SpawnRate);
                BreakOnDeath = CustomOption.Create(1755, Types.Impostor, "Hypnosis Breaks When The Hypnotist Dies", true, SpawnRate);
                ViewMode = new CustomOption(1756, Types.Impostor, "Hypnotist Sees Through The Victim",
                    new string[] { "Off", "Peek", "Until The Meeting" }, "Peek", SpawnRate, false);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Hypnotist] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) => UCRpc.Register(RpcId, HandleModuleRpc);

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalHypnotist() =>
            active && hypnotist != null && PlayerControl.LocalPlayer != null && hypnotist.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static bool HypnotistAlive() => hypnotist != null && hypnotist.Data != null && !hypnotist.Data.IsDead && !hypnotist.Data.Disconnected;
        /// <summary>For HypnotistView: the living victim the local, living Hypnotist may look through.</summary>
        internal static PlayerControl ViewVictim() {
            if (!IsLocalHypnotist() || !HypnosisHolds() || InMeeting()) return null;
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null || me.Data.IsDead) return null;
            var v = Helpers.playerById(victimId);
            return v != null && v.Data != null && !v.Data.IsDead && !v.Data.Disconnected ? v : null;
        }

        private static bool HypnosisHolds() =>
            active && victimId != byte.MaxValue && (HypnotistAlive() || !(BreakOnDeath?.getBool() ?? true));

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
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] SendSet failed: {e}"); }
        }

        private static void SendHypnotize(byte target) {
            try {
                var w = BeginRpc(SubHypnotize);
                w.Write(target);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyHypnotize(target);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] SendHypnotize failed: {e}"); }
        }

        private static void SendPick(byte target) {
            try {
                var w = BeginRpc(SubPick);
                w.Write(target);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyPick(target);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] SendPick failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                byte val = reader.ReadByte();
                switch (sub) {
                    case SubSet:
                        if (UCRpc.RequireHost("Hypnotist.Set")) ApplySet(val);
                        break;
                    case SubHypnotize:
                        if (UCRpc.RequireOwnerOrHost(hypnotist, "Hypnotist.Hypnotize")) ApplyHypnotize(val);
                        break;
                    case SubPick:
                        if (UCRpc.RequireOwnerOrHost(hypnotist, "Hypnotist.Pick")) ApplyPick(val);
                        break;
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] HandleRpc failed: {e}"); }
        }

        private static void ApplySet(byte id) {
            hypnotist = id == byte.MaxValue ? null : Helpers.playerById(id);
            active = hypnotist != null;
            usesLeft = Mathf.RoundToInt(Uses?.getFloat() ?? 2f);
            ClearHypnosis();
            if (!active) return;
            UCPromotion.Claim(id);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Hypnotist] The Hypnotist is {hypnotist.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySet(playerId);

        private static void ApplyHypnotize(byte target) {
            if (!active || InMeeting()) return;
            var p = Helpers.playerById(target);
            if (p == null || p.Data == null || p.Data.IsDead) return;
            victimId = target;
            pick = NoPick;
            originalVote = NoPick;
            usesLeft = Mathf.Max(0, usesLeft - 1);
            if (IsLocalHypnotist()) UnknownsCollectionPlugin.Logger?.LogInfo($"[Hypnotist] hypnotised {p.Data.PlayerName} ({usesLeft} left).");
        }

        /// <summary>IdeasPackDiag only: hypnotise without the button (freeplay updates no CustomButton).</summary>
        internal static void DiagHypnotize(byte target) => ApplyHypnotize(target);

        private static void ApplyPick(byte target) {
            if (!active || victimId == byte.MaxValue) return;
            pick = target;
            if (AmHost()) UnknownsCollectionPlugin.Logger?.LogInfo($"[Hypnotist] redirect pick: {(target == SkipPick ? "skip" : target == NoPick ? "none" : target.ToString())}.");
        }

        private static void ClearHypnosis() {
            victimId = byte.MaxValue;
            pick = NoPick;
            originalVote = NoPick;
        }

        // ---- Pick (host, random path) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
            public static void Postfix() {
                try {
                    if (!AmHost() || active) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;
                    if (rnd.Next(1, 101) > SpawnRate.getSelection() * 10) return;
                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Round: target + button ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        static class TargetPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (!active || __instance != PlayerControl.LocalPlayer || !IsLocalHypnotist()) return;
                    currentTarget = null;
                    if (InMeeting() || usesLeft <= 0 || victimId != byte.MaxValue) return;
                    if (__instance.Data == null || __instance.Data.IsDead) return;
                    currentTarget = PlayerControlFixedUpdatePatch.setTarget();
                    if (currentTarget != null) PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, Spiral);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.HypnotistIcon ?? Helpers.loadSpriteFromResources("TheOtherRoles.Resources.SpellButton.png", 115f);
                    hypnotizeButton = new CustomButton(
                        () => {
                            if (currentTarget == null || usesLeft <= 0) return;
                            SendHypnotize(currentTarget.PlayerId);
                            hypnotizeButton.Timer = hypnotizeButton.MaxTimer;
                        },
                        () => IsLocalHypnotist() && PlayerControl.LocalPlayer.Data != null
                              && !PlayerControl.LocalPlayer.Data.IsDead && usesLeft > 0,
                        () => PlayerControl.LocalPlayer.CanMove && currentTarget != null && victimId == byte.MaxValue,
                        () => { },
                        sprite,
                        CustomButton.ButtonPositions.upperRowLeft,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.hypnotist.button"));
                    hypnotizeButton.MaxTimer = Cooldown?.getFloat() ?? 25f;
                    hypnotizeButton.Timer = 10f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] Button creation failed: {e}");
                }
            }
        }

        // ---- Meeting: the Hypnotist's redirect buttons ----
        private static readonly Dictionary<byte, SpriteRenderer> pickButtons = new Dictionary<byte, SpriteRenderer>();

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        [HarmonyPriority(Priority.Low)]
        static class MeetingStartPatch {
            public static void Postfix(MeetingHud __instance) {
                try {
                    pickButtons.Clear();
                    originalVote = NoPick;
                    if (!IsLocalHypnotist() || !HypnosisHolds()) return;
                    if (PlayerControl.LocalPlayer.Data == null || PlayerControl.LocalPlayer.Data.IsDead) return;
                    var victim = Helpers.playerById(victimId);
                    if (victim == null || victim.Data == null || victim.Data.IsDead) return;
                    float x = HandleGuesser.isGuesser(PlayerControl.LocalPlayer.PlayerId) ? -0.5f : -0.95f;
                    var sprite = UCAssets.HypnotistVoteIcon;
                    foreach (var pva in __instance.playerStates) {
                        if (pva == null || pva.AmDead) continue;
                        var pc = Helpers.playerById(pva.TargetPlayerId);
                        if (pc == null || pc.Data == null || pc.Data.IsDead) continue;
                        AddButton(pva, pva.TargetPlayerId, x, sprite);
                    }
                    if (__instance.SkipVoteButton != null) AddButton(__instance.SkipVoteButton, SkipPick, x, sprite);
                    Helpers.showFlash(new Color(Spiral.r, Spiral.g, Spiral.b, 0.4f), 1f,
                        UCLocalization.Tr("uc.ui.hypnotist.meeting_hint", victim.Data.PlayerName));
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] meeting UI failed: {e}");
                }
            }
        }

        private static void AddButton(PlayerVoteArea pva, byte target, float x, Sprite sprite) {
            var templateTr = pva.Buttons != null ? pva.Buttons.transform.Find("CancelButton") : null;
            if (templateTr == null) return;
            var go = UnityEngine.Object.Instantiate(templateTr.gameObject, pva.transform);
            go.name = "HypnotistPick";
            go.transform.localPosition = new Vector3(x, 0.03f, -1.3f);
            var r = go.GetComponent<SpriteRenderer>();
            if (sprite != null) r.sprite = sprite;
            r.color = Color.white;
            var button = go.GetComponent<PassiveButton>();
            button.OnClick.RemoveAllListeners();
            button.OnClick.AddListener((System.Action)(() => OnPickClick(target)));
            pickButtons[target] = r;
        }

        private static void OnPickClick(byte target) {
            try {
                var hud = MeetingHud.Instance;
                if (hud == null || hud.state == MeetingHud.VoteStates.Results) return;
                byte next = pick == target ? NoPick : target;
                SendPick(next);
                foreach (var kv in pickButtons)
                    if (kv.Value != null) kv.Value.color = kv.Key == next ? Spiral : Color.white;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] pick click failed: {e}");
            }
        }

        // ---- Tally (host): rewrite the victim's vote before TOR counts and builds the results ----
        // The priority sits on the METHOD: this HarmonyX ignores [HarmonyPriority] on the patch class
        // (measured 2026-10-02, every class-level priority came out as 400). On the class, TOR's
        // tally prefix ran first, so a vote arriving with the tally itself (the victim voting last,
        // or the voting time running out) was rewritten only after the result.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting))]
        static class RedirectPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(MeetingHud __instance) {
                try {
                    if (!AmHost() || !HypnosisHolds()) return;
                    PlayerVoteArea area = null;
                    foreach (var ps in __instance.playerStates)
                        if (ps != null && ps.TargetPlayerId == victimId) { area = ps; break; }
                    if (DiagVote && area != null) UnknownsCollectionPlugin.Logger?.LogInfo($"[Hypnotist] redirect check: victim {victimId} didVote {area.DidVote} votedFor {area.VotedFor}, pick {pick}.");
                    if (area == null || area.AmDead || !area.DidVote) return;
                    if (originalVote == NoPick) originalVote = area.VotedFor;
                    byte orig = originalVote;
                    // 252-255 are the vote sentinels (dead, skip, missed, none); everything below is a player
                    // with "Cannot Escape" a skip (253), a missed vote (254) and no vote (255) are redirected too
                    bool redirectable = orig < 252 || orig >= SkipPick && Escapeless();
                    byte want = pick == NoPick || !redirectable ? orig : pick;
                    if (area.VotedFor == want) return;
                    area.VotedFor = want;
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[Hypnotist] vote of player {victimId} redirected: {orig} -> {want}.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] redirect failed: {e}");
                }
            }
        }

        private static readonly bool DiagVote = Environment.GetEnvironmentVariable("UC_DIAG_VOTE") is "1" or "timeout";
        private static bool Escapeless() => CannotEscape?.getBool() ?? true;

        // A victim who simply does not vote (User 2026-10-02): when the voting time runs out, the host
        // casts the vote for him - for the picked target - BEFORE vanilla marks every missing vote as
        // skipped. SetVote is the canonical path (VotedFor + DidVote + overlay); the tally prefix above
        // then sees an ordinary vote.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.ForceSkipAll))]
        static class ForcedVotePatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(MeetingHud __instance) {
                try {
                    if (!AmHost() || !HypnosisHolds() || !Escapeless() || pick == NoPick) return;
                    foreach (var ps in __instance.playerStates) {
                        if (ps == null || ps.TargetPlayerId != victimId || ps.AmDead || ps.DidVote) continue;
                        try { ps.SetVote(pick); } catch { ps.VotedFor = pick; }   // DidVote derives from VotedFor
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[Hypnotist] player {victimId} did not vote; forced vote -> {pick}.");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] forced vote failed: {e}");
                }
            }
        }

        // The hypnosis ends with its meeting.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
        static class MeetingEndPatch {
            public static void Postfix() {
                try { if (active) ClearHypnosis(); pickButtons.Clear(); } catch { }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || hypnotist == null || p == null || p != hypnotist || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++)
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) { __result[i] = HypnotistInfo(); replaced = true; }
                    if (!replaced) __result.Insert(0, HypnotistInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Hypnotist] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            hypnotist = null;
            active = false;
            usesLeft = 0;
            ClearHypnosis();
            currentTarget = null;
            pickButtons.Clear();
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Hypnotist", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Hypnotist", FullReset);
        }
    }
}
