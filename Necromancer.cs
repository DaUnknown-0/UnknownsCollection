// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Necromancer (Neutral)
 *
 * He RAISES fresh corpses. A raised player (a "thrall") walks, talks and does tasks like anyone
 * else - but their vote counts ZERO, they cannot guess, and neither shows: to the rest of the
 * lobby a thrall is indistinguishable from a living player. Only the Necromancer (and each thrall
 * themselves) knows who belongs to him. He wins when, at a meeting, more than the configured share
 * of the "living" belong to him (himself + living thralls).
 *
 * WHY THE STEALTH ONLY WORKS BEFORE MEETINGS
 * The meeting UI shows everyone who is dead. A corpse that was reported, or a player who sat
 * through a meeting as a ghost, is PUBLICLY dead - walking around afterwards cannot be hidden.
 * So raising only works on a DeadBody object, and those only exist between a kill and the next
 * meeting; exiled players never have one. That is not a technical shortcut, it is the game: the
 * Necromancer races reporters (and the Vulture and the Cleaner) for every fresh corpse, on top of
 * the freshness window option. Everything a thrall learned as a ghost (their killer, dead chat)
 * comes back with them - their ONLY path to victory is the Necromancer's, so spilling it costs
 * them their own win.
 *
 * THE VOTE, IN DETAIL (decisions from the design review):
 *  - A thrall VOTES normally - checkmark, icon, everything - but with weight 0: a postfix on TOR's
 *    vote counting (MeetingHudPatch+MeetingCalculateVotesPatch.CalculateVotes, the same hook
 *    ChanceMod's vote multiplier uses) subtracts their contribution afterwards, removing dictionary
 *    entries that reach zero (TOR's MaxPair starts at int.MinValue - a lingering 0 entry would
 *    falsely win the vote, the ChanceMod lesson). KNOWN TELL: with anonymous votes OFF the result
 *    screen still renders their (colored) icon while the outcome ignores it - recommend anonymous
 *    votes with this role.
 *  - A thrall never DELAYS the meeting: once every real voter has voted, a Priority.First prefix
 *    on MeetingHud.CheckForEndVoting (host-side - votes are processed by the host) force-skips
 *    every thrall who has not voted yet, so TOR's "everyone voted -> end" check passes.
 *  - A thrall cannot GUESS: a prefix on TOR's guesserOnClick simply refuses to open the shot UI
 *    for a local thrall. Invisible to everyone else - nobody sees a guess that never happens.
 *
 * WHEN THE NECROMANCER DIES (or leaves), every thrall dies with him - the magic held them up.
 * Host-driven via Helpers.MurderPlayer once no meeting/exile is on screen (killing into the
 * meeting UI desyncs it; the Witch's exile kills taught TOR the same). Deliberately UNCHECKED
 * kills: the cascade is a consequence, not an attempt - shields don't apply (the Lovers rule).
 *
 * WIN CHECK: host-side, only while a meeting is open plus a short window after the exile (an
 * ejection can flip the ratio) - per design "er gewinnt im Meeting". RpcEndGame with own
 * GameOverReason 33 (Pelican has 32), retried while the condition holds (the Collector lesson:
 * a single RpcEndGame can get swallowed). Winners on the end screen: Necromancer + all thralls.
 *
 * WHAT DELIBERATELY STAYS UNTOUCHED: a thrall keeps their role and abilities (a raised Sheriff
 * still shoots - but why would he shoot his own win condition?). Impostor corpses can be raised
 * too: the other impostors then KNOW ("I killed him, he walks") - re-killing a thrall is the
 * impostors' counterplay, and a re-killed thrall is a fresh corpse he may raise again.
 *
 * THRALL TASKS LEAVE THE GAME - SERVER-VISIBLY. The crew task win is SERVER-authoritative (the
 * bypass experiments: the host cannot intercept it), so a client-side subtraction (the Collector
 * pattern) would leave the thrall's tasks in the server's bookkeeping - a hidden thrall could
 * then hold the crew's task win hostage forever simply by never tasking (he wins with the
 * Necromancer, why would he help?). Instead the HOST empties the thrall's task list with a real
 * RpcSetTasks (the Auditor's server-visible reset; NetworkedPlayerInfo is host-owned): the crew
 * can finish without him, and he can neither block nor feed the task win. His own HUD task list
 * empties - only he sees that, and he knows what he is; "tasking" for cover means standing at
 * consoles, exactly like every TOR neutral. KNOWN TELL: with a visible task bar the total
 * shrinks at the moment of the raise.
 *
 * Mutual exclusion with the Poltergeist (both feed on the first deaths): option 1634, enforced
 * as a stand-down guard in the Poltergeist's promotion trigger.
 *
 * ARCHITECTURE mirrors Collector: neutral tag over a plain Crewmate, host-authoritative pick,
 * custom RPC module 216 on UCRpc.CallId = 230, gated on "everyone has the mod".
 * Options 1625-1634, draft sentinel 219, win reason 33. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AmongUs.GameOptions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Necromancer {
        // ---- Theme ----
        // Grave moss: darker and dirtier than the Gambler's casino green and the Medic's teal,
        // so the three greens never read as the same role at name-tag size.
        public static readonly Color Color = new Color(0.42f, 0.58f, 0.29f);

        // ---- Options (IDs 1625-1634) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption RaiseDuration;
        public static CustomOption RaiseCooldown;
        public static CustomOption Freshness;
        public static CustomOption WinThreshold;     // "More Than Two Thirds" / "More Than Half"
        public static CustomOption MinThralls;
        public static CustomOption CanVent;
        public static CustomOption HasTasks;
        public static CustomOption ExcludePoltergeist;

        // ---- Runtime state ----
        public static PlayerControl necromancer;
        public static bool active;
        private static byte necromancerPlayerId = byte.MaxValue;
        // The army. PlayerIds stay in here when a thrall is re-killed (their allegiance does not
        // die with them - only the win check filters for the living).
        private static readonly HashSet<byte> thralls = new HashSet<byte>();
        // Death timestamps (every client, via the uncheckedMurderPlayer postfix below) - the
        // freshness window for raising.
        private static readonly Dictionary<byte, float> deathAt = new Dictionary<byte, float>();

        public static bool IsThrall(byte playerId) => active && thralls.Contains(playerId);
        public static bool IsLocalNecromancer() =>
            active && necromancer != null && PlayerControl.LocalPlayer != null
            && necromancer.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static bool IsLocalThrall() =>
            PlayerControl.LocalPlayer != null && IsThrall(PlayerControl.LocalPlayer.PlayerId);

        // Local channel state (Necromancer's client only) - the Collector's channel pattern.
        private static bool channeling;
        private static float channelStart;
        private static byte channelTargetId = byte.MaxValue;
        private static Vector2 channelStartPos;

        private const int NecromancerWinReason = 33; // Pelican uses 32
        private static readonly List<byte> winnerIds = new List<byte>(); // survives resetVariables
        private static float nextWinTry;
        private static float winCheckUntil;   // evaluate this long after an exile ended
        private static bool exileWasActive;
        private static float nextTick;

        // ---- Custom RPC subtypes: module byte 216 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.NecromancerRpcId;
        private const byte SubSetNecromancer = 0;  // playerId (255 = clear)
        private const byte SubRaise = 1;           // thrallId (host -> everyone: confirmed raise)
        // AUDIT-2026-08-15: the meeting-gate decision used to run per-client (ApplyRaise checked
        // InMeeting() locally), so a raise could land on some clients and be dropped on others
        // depending on RPC arrival order - same player alive here, dead there. SubRaiseRequest is
        // the Necromancer's client asking the host to arbitrate; only the host's InMeeting() counts
        // now, and SubRaise (above) then applies unconditionally everywhere (Saboteur's
        // SubRequestKill/HostHandleRequestKill pattern).
        private const byte SubRaiseRequest = 2;    // thrallId (Necromancer -> host: request only)

        private static readonly System.Random rnd = new System.Random();

        // ---- Role identity ----
        private static RoleInfo necromancerInfo;
        public static RoleInfo NecromancerInfo() => necromancerInfo ??= new RoleInfo(
            "Necromancer", Color, "Raise fresh corpses into your silent army",
            "Raise the dead", RoleId.Crewmate)
        { isNeutral = true };

        private static TheOtherRoles.Objects.CustomButton raiseButton;

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1625, Types.Neutral, "Necromancer",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1626, Types.Neutral, "Necromancer Minimum Players To Spawn",
                    7f, 4f, 15f, 1f, SpawnRate);
                RaiseDuration = CustomOption.Create(1627, Types.Neutral, "Raising Duration",
                    3f, 1f, 8f, 0.5f, SpawnRate);
                RaiseCooldown = CustomOption.Create(1628, Types.Neutral, "Raise Cooldown",
                    20f, 0f, 60f, 2.5f, SpawnRate);
                Freshness = CustomOption.Create(1629, Types.Neutral, "Corpse Freshness Window",
                    60f, 10f, 180f, 5f, SpawnRate);
                WinThreshold = CustomOption.Create(1630, Types.Neutral, "Necromancer Win Threshold",
                    new string[] { "More Than Two Thirds", "More Than Half" }, SpawnRate);
                MinThralls = CustomOption.Create(1631, Types.Neutral, "Minimum Thralls To Win",
                    2f, 1f, 5f, 1f, SpawnRate);
                CanVent = CustomOption.Create(1632, Types.Neutral, "Necromancer Can Use Vents",
                    false, SpawnRate);
                HasTasks = CustomOption.Create(1633, Types.Neutral, "Necromancer Has Tasks",
                    false, SpawnRate);
                ExcludePoltergeist = CustomOption.Create(1634, Types.Neutral, "Necromancer And Poltergeist Exclude Each Other",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Necromancer] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);

            // Vote weight 0 - postfix on TOR's INTERNAL vote counting (the ChanceMod hook).
            try {
                var t = typeof(CustomOption).Assembly
                    .GetType("TheOtherRoles.Patches.MeetingHudPatch+MeetingCalculateVotesPatch");
                var m = t == null ? null : AccessTools.Method(t, "CalculateVotes");
                if (m != null)
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Necromancer), nameof(CalculateVotesPostfix)));
                else
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[Necromancer] CalculateVotes not found - thrall votes would COUNT; role stays disabled-safe only via spawn.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] vote patch failed: {e}");
            }

            // Guess block - prefix on TOR's guesserOnClick (opening the shot UI), local thrall only.
            try {
                var mh = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                var gm = mh == null ? null : AccessTools.Method(mh, "guesserOnClick");
                if (gm != null)
                    harmony.Patch(gm, prefix: new HarmonyMethod(typeof(Necromancer), nameof(GuesserOnClickPrefix)));
                else
                    UnknownsCollectionPlugin.Logger?.LogWarning("[Necromancer] guesserOnClick not found - thrall guess block inactive.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] guesser patch failed: {e}");
            }
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;

        // The Poltergeist's promotion trigger stands down while an excluding Necromancer is in play
        // (both roles feed on the round's first deaths).
        public static bool BlocksPoltergeist() => active && (ExcludePoltergeist?.getBool() ?? true);

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte subtype) {
            var w = UCRpc.Begin(RpcId);
            w.Write(subtype);
            return w;
        }

        public static void SendSetNecromancer(byte id) {
            try {
                var w = BeginRpc(SubSetNecromancer);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetNecromancer(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] SendSet failed: {e}"); }
        }

        // Owner's channel completed -> ask the host to arbitrate (AUDIT-2026-08-15). Applies NOTHING
        // locally anymore: the old code called ApplyRaise here too, so the Necromancer's own client
        // basically always won the race against a differently-ordered SubRaise from someone else.
        private static void SendRaiseRequest(byte thrallId) {
            try {
                var w = BeginRpc(SubRaiseRequest);
                w.Write(thrallId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                HostHandleRaiseRequest(thrallId); // host==sender path; no-op for non-host (Saboteur pattern)
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] SendRaiseRequest failed: {e}"); }
        }

        // Host -> everyone: the request passed validation, broadcast the confirmed raise (and apply
        // it locally, since the host never gets its own RPC back).
        private static void SendRaise(byte thrallId) {
            try {
                var w = BeginRpc(SubRaise);
                w.Write(thrallId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyRaise(thrallId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] SendRaise failed: {e}"); }
        }

        // Host-authoritative arbitration (AUDIT-2026-08-15): every plausibility check that used to
        // live in ApplyRaise (chiefly InMeeting()) now runs exactly once, here, on the host's own
        // view of the world - not once per client with whatever view each happened to have.
        private static void HostHandleRaiseRequest(byte pid) {
            if (!AmHost()) return;
            if (!active) return;
            if (InMeeting()) {
                UnknownsCollectionPlugin.Logger?.LogInfo("[Necromancer] raise request rejected (meeting on host).");
                return;
            }
            var p = Helpers.playerById(pid);
            if (p == null || p.Data == null) return;

            // AUDIT M-11: the three rules that make a raise a raise - there IS a corpse, it is still
            // fresh, and the Necromancer is standing at it - only ever ran in the owner's button path
            // (NearestFreshBody). The host used to take any player id on faith, so a tampered client
            // could raise a player who is merely dead-and-exiled (no corpse at all, which also skips
            // the SnapTo in ApplyRaise) or one whose body went cold minutes ago. Same shape as
            // Saboteur.HostHandleRequestKill: the host re-checks what the client claimed.
            if (!p.Data.IsDead) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Necromancer] raise request rejected: player {pid} is alive.");
                return;
            }
            if (!deathAt.TryGetValue(pid, out float diedAt)
                || Time.time - diedAt > (Freshness?.getFloat() ?? 60f)) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Necromancer] raise request rejected: corpse of {pid} is cold or unknown.");
                return;
            }
            DeadBody corpse = null;
            foreach (var db in GetDeadBodies())
                if (db != null && db.ParentId == pid) { corpse = db; break; }
            if (corpse == null) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Necromancer] raise request rejected: no body for {pid} on the host.");
                return;
            }
            if (necromancer == null || !IsAlive(necromancer)) {
                UnknownsCollectionPlugin.Logger?.LogInfo("[Necromancer] raise request rejected: no living Necromancer.");
                return;
            }
            // Range check with the same slack the Saboteur's console check uses: the host's view of
            // both positions is a network frame behind the owner's, and the channel takes seconds
            // during which the Necromancer must not move anyway, so the tolerance only absorbs jitter.
            float dist = Vector2.Distance(necromancer.GetTruePosition(), (Vector2)corpse.TruePosition);
            if (dist > RaiseRange + RaiseRangeTolerance) {
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Necromancer] raise request rejected: Necromancer is {dist:0.00} away from the corpse of {pid}.");
                return;
            }

            SendRaise(pid);
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSetNecromancer: {
                        byte id = reader.ReadByte();
                        // Host-authoritative pick (AUDIT H-3 rule).
                        if (UCRpc.RequireHost("Necromancer.SetNecromancer")) ApplySetNecromancer(id);
                        break;
                    }
                    case SubRaise: {
                        byte pid = reader.ReadByte();
                        // Host-confirmed raise (AUDIT-2026-08-15): the meeting gate already ran once,
                        // host-side, in HostHandleRaiseRequest - every client just applies the same
                        // outcome now, no more local InMeeting() re-check that could disagree.
                        if (UCRpc.RequireHost("Necromancer.Raise")) ApplyRaise(pid);
                        break;
                    }
                    case SubRaiseRequest: {
                        byte pid = reader.ReadByte();
                        // Necromancer's client -> host request only (the channel still completes on
                        // his client, but the host now decides whether it lands) (AUDIT-2026-08-15).
                        if (UCRpc.RequireOwnerOrHost(necromancer, "Necromancer.RaiseRequest"))
                            HostHandleRaiseRequest(pid); // no-op unless we are the host
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] HandleRpc failed: {e}");
            }
        }

        private static void ApplySetNecromancer(byte id) {
            necromancer = Helpers.playerById(id);
            active = necromancer != null;
            necromancerPlayerId = active ? id : byte.MaxValue;
            if (!active) thralls.Clear();
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[Necromancer] The Necromancer is {necromancer.Data?.PlayerName}.");
        }

        // Runs on EVERY client, only once the host has confirmed the raise via SubRaise (the meeting
        // gate was already decided host-side in HostHandleRaiseRequest - AUDIT-2026-08-15) - the
        // PlayerTuning revive pattern (Revive + living vanilla role from the ghost's faction +
        // destroy the corpse + task recompute), plus the raise-specific parts: snap the thrall
        // (owner client only) back onto their own corpse, and record the allegiance.
        private static void ApplyRaise(byte pid) {
            try {
                var p = Helpers.playerById(pid);
                if (p == null || p.Data == null) return;

                Vector2? bodyPos = null;
                foreach (var db in UnityEngine.Object.FindObjectsOfType<DeadBody>()) {
                    if (db == null || db.ParentId != pid) continue;
                    bodyPos = (Vector2)db.TruePosition;
                    UnityEngine.Object.Destroy(db.gameObject);
                }

                if (p.Data.IsDead) {
                    bool wasImp = p.Data.Role != null && p.Data.Role.IsImpostor;
                    p.Revive();
                    RoleManager.Instance.SetRole(p, wasImp ? RoleTypes.Impostor : RoleTypes.Crewmate);
                }
                try { GameData.Instance?.RecomputeTaskCounts(); } catch { }

                thralls.Add(pid);

                // Server-visible task strip (see the header: the crew task win is server-
                // authoritative, so the thrall's tasks must leave the SERVER's bookkeeping, not
                // just the client counters). Host only - NetworkedPlayerInfo is host-owned.
                if (AmHost()) {
                    try {
                        p.Data.RpcSetTasks(new Il2CppStructArray<byte>(0));
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            $"[Necromancer] stripped {p.Data.PlayerName}'s tasks (server-visible).");
                    } catch (Exception ex) {
                        UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] task strip failed: {ex}");
                    }
                }

                if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == pid) {
                    // The owner client moves its own pawn - everyone else gets the sync (the ghost
                    // may have floated anywhere; the thrall must RISE where the corpse lay).
                    if (bodyPos != null)
                        try { PlayerControl.LocalPlayer.NetTransform.SnapTo(bodyPos.Value); } catch { }
                    Helpers.showFlash(Color, 1f);
                    var hud = FastDestroyableSingleton<HudManager>.Instance;
                    if (hud != null && hud.Chat != null)
                        hud.Chat.AddChat(PlayerControl.LocalPlayer, UCLocalization.Tr("uc.ui.necromancer.raised"));
                }
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Necromancer] raised {p.Data.PlayerName} ({thralls.Count} thrall(s)).");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] ApplyRaise failed: {e}");
            }
        }

        public static void MarkFromDraft(byte playerId) => ApplySetNecromancer(playerId);

        // ---- Death timestamps (freshness window). Every kill in a modded lobby funnels through
        // TOR's uncheckedMurderPlayer, on every client. ----
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.uncheckedMurderPlayer))]
        static class DeathClockPatch {
            public static void Postfix([HarmonyArgument(1)] byte targetId) {
                try { deathAt[targetId] = Time.time; } catch { }
            }
        }

        // ---- Pick (host, random path - the draft path goes through MarkFromDraft) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 7f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    SendSetNecromancer(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Raise button + channel (Collector pattern: move cancels, meeting cancels) ----
        private const float RaiseRange = 2.0f;

        // Slack for the HOST's re-check of the same distance (AUDIT M-11). The owner's client decides
        // on its own positions; the host validates a frame or two later, so an exact comparison would
        // reject legitimate raises at the edge of the range.
        private const float RaiseRangeTolerance = 1.0f;

        // AUDIT-2026-08-16: CouldUse() calls NearestFreshBody() every frame the button exists (see
        // CustomButton.Update), and the button exists for the whole round even when not channeling.
        // FindObjectsOfType<DeadBody>() is a full scene scan, so that ran essentially every frame.
        // Cache the scan result for a short TTL (same pattern as Saboteur.GetConsoles, but time-based
        // here since DeadBody instances actually appear/disappear during the round - a report or
        // cleanup can destroy one at any time). 0.2s is well under human reaction time for "corpse just
        // became raisable", so the button's felt responsiveness is unaffected.
        private static DeadBody[] deadBodyCache;
        private static float deadBodyCacheAt = -999f;
        private const float DeadBodyCacheTTL = 0.2f;

        private static DeadBody[] GetDeadBodies() {
            if (deadBodyCache == null || Time.time - deadBodyCacheAt >= DeadBodyCacheTTL) {
                deadBodyCache = UnityEngine.Object.FindObjectsOfType<DeadBody>();
                deadBodyCacheAt = Time.time;
            }
            return deadBodyCache ?? System.Array.Empty<DeadBody>();
        }

        private static DeadBody NearestFreshBody(Vector2 from, float range) {
            DeadBody best = null;
            float bestD = range;
            float window = Freshness?.getFloat() ?? 60f;
            try {
                foreach (var db in GetDeadBodies()) {
                    if (db == null) continue;   // entry may be a since-removed (reported/cleaned) body
                    if (!deathAt.TryGetValue(db.ParentId, out float t0)) continue;
                    if (Time.time - t0 > window) continue;   // gone cold
                    float d = Vector2.Distance(from, (Vector2)db.TruePosition);
                    if (d < bestD) { bestD = d; best = db; }
                }
            } catch { }
            return best;
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    raiseButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            if (channeling) { channeling = false; return; }
                            Vector2 here = PlayerControl.LocalPlayer.GetTruePosition();
                            var body = NearestFreshBody(here, RaiseRange);
                            if (body == null) return;
                            channeling = true;
                            channelStart = Time.time;
                            channelTargetId = body.ParentId;
                            channelStartPos = here;
                            UnknownsCollectionPlugin.Logger?.LogInfo(
                                $"[Necromancer] channel started on corpse of player {body.ParentId}.");
                        },
                        () => IsLocalNecromancer()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => channeling
                              || (PlayerControl.LocalPlayer.CanMove
                                  && NearestFreshBody(PlayerControl.LocalPlayer.GetTruePosition(), RaiseRange) != null),
                        () => { channeling = false; },
                        UCAssets.NecromancerIcon,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.necromancer.button_raise"));
                    raiseButton.MaxTimer = 1f;
                    raiseButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] Button creation failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active) return;

                    ChannelTick();
                    NameColorTick();

                    if (Time.realtimeSinceStartup < nextTick) return;
                    nextTick = Time.realtimeSinceStartup + 0.5f;

                    // Exile just ended -> the ratio may have flipped; evaluate for a short window.
                    bool exiling = ExileController.Instance != null;
                    if (exileWasActive && !exiling) winCheckUntil = Time.time + 5f;
                    exileWasActive = exiling;

                    if (!AmHost()) return;
                    CascadeTick();
                    TryWin();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] HudUpdate failed: {e}");
                }
            }
        }

        private static void ChannelTick() {
            if (!IsLocalNecromancer()) return;
            if (channeling) {
                float dur = RaiseDuration?.getFloat() ?? 3f;
                float progress = (Time.time - channelStart) / dur;
                bool moved = Vector2.Distance(PlayerControl.LocalPlayer.GetTruePosition(), channelStartPos) > 0.5f;
                bool bodyGone = true;
                try {
                    // Uses the shared 0.2s cache, not a raw scene scan: ChannelTick runs BEFORE the
                    // 0.5s throttle in HudUpdatePatch, so this is a per-frame path for the whole
                    // channel. The cache was added for NearestFreshBody on 2026-08-16 and this second
                    // call site was missed then. Destroyed bodies still read as null, which the
                    // existing check below already handles.
                    foreach (var db in GetDeadBodies())
                        if (db != null && db.ParentId == channelTargetId) { bodyGone = false; break; }
                } catch { }
                bool blocked = InMeeting() || PlayerControl.LocalPlayer.Data.IsDead;
                if (moved || bodyGone || blocked) {
                    channeling = false;
                    // Same feedback grammar as the Collector: warm = you broke it, cool = the corpse
                    // itself vanished (reported, eaten, cleaned).
                    if (moved) Helpers.showFlash(new Color(1f, 0.75f, 0.2f, 0.3f), 0.2f);
                    else if (bodyGone && !blocked) Helpers.showFlash(new Color(0.35f, 0.55f, 1f, 0.3f), 0.2f);
                } else if (progress >= 1f) {
                    channeling = false;
                    SendRaiseRequest(channelTargetId);
                    float cd = RaiseCooldown?.getFloat() ?? 20f;
                    if (raiseButton != null && cd > 0f) {
                        raiseButton.MaxTimer = cd;
                        raiseButton.Timer = cd;
                    }
                } else if (raiseButton != null) {
                    raiseButton.buttonText = UCLocalization.Tr("uc.ui.necromancer.button_raise_progress", (int)(progress * 100));
                    if (raiseButton.actionButtonRenderer != null)
                        raiseButton.actionButtonRenderer.color = UnityEngine.Color.Lerp(
                            Palette.EnabledColor, Color, Mathf.Clamp01(progress));
                }
            } else if (raiseButton != null && UCLabelThrottle.Due("necromancer.army")) {
                // Throttled: the count itself is the expensive part (a playerById scan per thrall),
                // so the gate has to sit in front of it, not around the assignment (AUDIT-2026-08-16).
                int alive = thralls.Count(id => IsAlive(Helpers.playerById(id)));
                raiseButton.buttonText = UCLocalization.Tr("uc.ui.necromancer.button_army_count", alive);
            }
        }

        // Team sight: the Necromancer sees his thralls, each thrall sees the Necromancer. Moss-green
        // names, world and meeting alike (the Witness name-tint pattern: our postfix runs after
        // TOR's own per-frame name pass). Nobody else ever sees a mark.
        private static void NameColorTick() {
            try {
                bool amNecro = IsLocalNecromancer();
                bool amThrall = IsLocalThrall();
                if (!amNecro && !amThrall) return;

                if (amNecro) {
                    foreach (byte id in thralls) {
                        var p = Helpers.playerById(id);
                        if (p?.cosmetics?.nameText != null) p.cosmetics.nameText.color = Color;
                    }
                } else if (necromancer?.cosmetics?.nameText != null) {
                    necromancer.cosmetics.nameText.color = Color;
                }

                var meeting = MeetingHud.Instance;
                if (meeting?.playerStates == null) return;
                foreach (var ps in meeting.playerStates) {
                    if (ps == null || ps.NameText == null) continue;
                    bool mark = amNecro ? thralls.Contains(ps.TargetPlayerId)
                                        : ps.TargetPlayerId == necromancerPlayerId;
                    if (mark) ps.NameText.color = Color;
                }
            } catch { }
        }

        // ---- The cascade: the master falls, the magic fails ----
        private static void CascadeTick() {
            try {
                if (necromancer == null || necromancer.Data == null) return;
                bool gone = necromancer.Data.IsDead || necromancer.Data.Disconnected;
                if (!gone) return;
                if (InMeeting()) return;   // never kill into the meeting/exile UI
                foreach (byte id in thralls.ToList()) {
                    var p = Helpers.playerById(id);
                    if (!IsAlive(p)) continue;
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[Necromancer] master gone - thrall {p.Data.PlayerName} collapses.");
                    Helpers.MurderPlayer(p, p, false);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] cascade failed: {e}");
            }
        }

        // ---- The win: "more than X of the living belong to him", evaluated at meetings ----
        private static bool WinConditionHolds() {
            if (!active || !IsAlive(necromancer)) return false;
            int alive = 0, mine = 0, thrallsAlive = 0;
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (!IsAlive(p)) continue;
                alive++;
                if (p.PlayerId == necromancerPlayerId) mine++;
                else if (thralls.Contains(p.PlayerId)) { mine++; thrallsAlive++; }
            }
            if (thrallsAlive < Mathf.RoundToInt(MinThralls?.getFloat() ?? 2f)) return false;
            // sel 0: strictly more than 2/3; sel 1: strictly more than 1/2.
            return (WinThreshold?.getSelection() ?? 0) == 0
                ? mine * 3 > alive * 2
                : mine * 2 > alive;
        }

        private static void TryWin() {
            // Meeting-gated by design (plus the short post-exile window set in HudUpdate).
            if (MeetingHud.Instance == null && Time.time > winCheckUntil) return;
            if (!WinConditionHolds()) return;
            if (Time.time < nextWinTry) return;
            nextWinTry = Time.time + 2f;
            UnknownsCollectionPlugin.Logger?.LogInfo("[Necromancer] the living are his - ending the game.");
            GameManager.Instance.RpcEndGame((GameOverReason)NecromancerWinReason, false);
        }

        // ---- Vote weight 0 (postfix on TOR's CalculateVotes, manual patch in TryPatch) ----
        public static void CalculateVotesPostfix([HarmonyArgument(0)] MeetingHud hud,
                                                 ref Dictionary<byte, int> __result) {
            try {
                if (!active || thralls.Count == 0 || hud == null || __result == null) return;
                foreach (var ps in hud.playerStates) {
                    if (ps == null || ps.AmDead || !ps.DidVote) continue;
                    if (!thralls.Contains(ps.TargetPlayerId)) continue;
                    byte votedFor = ps.VotedFor;
                    if (votedFor == 252 || votedFor == 254 || votedFor == 255) continue; // dead/missed/none
                    // Same weight TOR just added for this voter (a thrall Mayor contributed 2).
                    int weight = (Mayor.mayor != null && Mayor.mayor.PlayerId == ps.TargetPlayerId
                                  && Mayor.voteTwice) ? 2 : 1;
                    if (!__result.TryGetValue(votedFor, out int cur)) continue;
                    int next = cur - weight;
                    // Remove instead of writing 0: TOR's MaxPair starts at int.MinValue, a lingering
                    // 0 entry would falsely win an otherwise voteless tally (the ChanceMod lesson).
                    if (next <= 0) __result.Remove(votedFor);
                    else __result[votedFor] = next;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] CalculateVotes postfix failed: {e}");
            }
        }

        // ---- No meeting delay: once every REAL voter voted, force-skip the dawdling thralls ----
        // Priority.First so it runs before TOR's own CheckForEndVoting prefix (which replaces the
        // original and does the All(voted) check). Votes are processed host-side, so host-only.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting))]
        [HarmonyPriority(Priority.First)]
        static class EndVotingNoDelayPatch {
            public static void Prefix(MeetingHud __instance) {
                try {
                    if (!active || thralls.Count == 0 || !AmHost()) return;
                    foreach (var ps in __instance.playerStates) {
                        if (ps == null || ps.AmDead || ps.DidVote) continue;
                        if (thralls.Contains(ps.TargetPlayerId)) continue;
                        return; // a real voter is still thinking - no need to do anything
                    }
                    foreach (var ps in __instance.playerStates) {
                        if (ps == null || ps.AmDead || ps.DidVote) continue;
                        if (!thralls.Contains(ps.TargetPlayerId)) continue;
                        // The canonical path (sets VotedFor + flag + overlay); 253 = skip. The vote
                        // is weightless anyway - this only satisfies TOR's "everyone voted" check.
                        try { ps.SetVote(253); } catch { ps.VotedFor = 253; }
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            $"[Necromancer] thrall {ps.TargetPlayerId} auto-skipped (no meeting delay).");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] end-voting prefix failed: {e}");
                }
            }
        }

        // ---- Thralls cannot guess (prefix on TOR's guesserOnClick, manual patch in TryPatch) ----
        public static bool GuesserOnClickPrefix() {
            try {
                if (IsLocalThrall() && PlayerControl.LocalPlayer.Data != null
                    && !PlayerControl.LocalPlayer.Data.IsDead)
                    return false;   // the shot UI simply never opens; nobody else sees a thing
            } catch { }
            return true;
        }

        // ---- Winner list + end screen (Collector pattern; reason 33, banner 14) ----
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        static class OnGameEndPatch {
            public static void Prefix() {
                // Cleared unconditionally, even if this round's Necromancer never actually rose: a stale
                // winnerIds list from a previous round must not survive into this game-end check, or the
                // Postfix below (gated only on gameOverReason, not on `active`) could hand out a leftover
                // win snapshot from a Necromancer game that already ended.
                winnerIds.Clear();
                if (!active || necromancerPlayerId == byte.MaxValue) return;
                winnerIds.Add(necromancerPlayerId);
                foreach (byte id in thralls) winnerIds.Add(id);
            }

            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != NecromancerWinReason) return;
                    if (winnerIds.Count == 0) return;

                    EndGameResult.CachedWinners.Clear();
                    foreach (byte id in winnerIds) {
                        var p = Helpers.playerById(id);
                        if (p != null && p.Data != null)
                            EndGameResult.CachedWinners.Add(new CachedPlayerData(p.Data));
                    }
                    SetWinCondition(14); // Bug 12, Collector 13 - own banner below
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Necromancer] The Necromancer and his army win!");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] OnGameEnd failed: {e}");
                }
            }
        }

        private static FieldInfo winConditionField;
        private static void SetWinCondition(int value) {
            try {
                if (winConditionField == null) {
                    var atdType = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.AdditionalTempData");
                    if (atdType != null)
                        winConditionField = atdType.GetField("winCondition", BindingFlags.Public | BindingFlags.Static);
                }
                if (winConditionField != null) {
                    var wcEnum = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.WinCondition");
                    if (wcEnum != null)
                        winConditionField.SetValue(null, Enum.ToObject(wcEnum, value));
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] SetWinCondition failed: {e}");
            }
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        [HarmonyPriority(Priority.Last)]
        static class EndGameFxPatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != NecromancerWinReason) return;
                    if (__instance.WinText != null) {
                        GameObject bonus = UnityEngine.Object.Instantiate(__instance.WinText.gameObject);
                        bonus.transform.position = new Vector3(__instance.WinText.transform.position.x,
                            __instance.WinText.transform.position.y - 0.5f,
                            __instance.WinText.transform.position.z);
                        bonus.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                        var text = bonus.GetComponent<TMP_Text>();
                        text.text = UCLocalization.Tr("uc.ui.necromancer.win_banner");
                        text.color = Color;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] end-screen FX failed: {e}");
                }
            }
        }

        // ---- Task accounting: the Necromancer's own tasks never count toward the crew total
        // (client-side Collector pattern). Thrall tasks need no clause here - the raise strips
        // them SERVER-visibly via RpcSetTasks (see ApplyRaise), so every counter agrees. ----
        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || necromancer == null || necromancer.Data == null) return;
                    if (HasTasks?.getBool() ?? false) return;
                    var (done, total) = TasksHandler.taskInfo(necromancer.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= done;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] TaskPatch failed: {e}");
                }
            }
        }

        // ---- Venting (option): the ChanceMod reference pattern - a postfix on TOR's central
        // vent helper, no vanilla role change needed. ----
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.roleCanUseVents))]
        static class VentPatch {
            public static void Postfix(PlayerControl player, ref bool __result) {
                try {
                    if (!active || player == null || necromancer == null) return;
                    if (player.PlayerId != necromancerPlayerId) return;
                    if (CanVent?.getBool() ?? false) __result = true;
                } catch { }
            }
        }

        // ---- Role identity (Necromancer only - thralls stay their old role EVERYWHERE) ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || necromancer == null || p == null || p != necromancer || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = NecromancerInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, NecromancerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Necromancer] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets. PlayerId-keyed state ALSO clears on OnGameJoined (the lobby-leak rule). ----
        private static void FullReset() {
            necromancer = null;
            active = false;
            necromancerPlayerId = byte.MaxValue;
            thralls.Clear();
            deathAt.Clear();
            channeling = false;
            channelTargetId = byte.MaxValue;
            nextWinTry = 0f;
            winCheckUntil = 0f;
            exileWasActive = false;
            deadBodyCache = null;
            deadBodyCacheAt = -999f;
            // raiseButton deliberately NOT nulled (the resetVariables button-timing rule).
            // winnerIds deliberately survives resetVariables (read after reset at game end).
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Necromancer", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { FullReset(); winnerIds.Clear(); }
        }
    }
}
