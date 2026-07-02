// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Collector (Neutral)
 *
 * The host scatters RELICS across the map (anchored near task consoles - available on every map,
 * excluding critical-sabotage consoles and the emergency button). Only the Collector sees them
 * clearly; impostors can optionally sense a faint shimmer nearby (see CollectorRelics). The Collector
 * must CHANNEL for a few seconds at a relic to collect it - moving cancels, and everyone close enough
 * hears a quiet glitter (the built-in counterplay). Collecting enough relics wins:
 *   - "Instant": the host ends the game immediately (own GameOverReason 19; the Bug uses 18);
 *   - "Survive To End": like the Bug, the win hijacks the next TEAM win while the Collector lives.
 *     If both a Bug and a full Collector are alive at a team win, whichever prefix rewrites the
 *     reason first wins the steal; the other one backs off because the reason is no longer a team win.
 *
 * ARCHITECTURE mirrors Bug/Follower: neutral tag over a plain Crewmate, host-authoritative pick,
 * custom RPC (209), gated on "everyone has the mod". Options 1580-1588. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Collector {
        // ---- Theme ----
        public static readonly Color Color = new Color(1f, 0.78f, 0.25f); // relic gold

        // ---- Options (IDs 1580-1589, 1596) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption RelicsSpawned;
        public static CustomOption RelicsNeeded;
        public static CustomOption ChannelDuration;
        public static CustomOption WinMode;          // Instant / Survive To End
        public static CustomOption ImpostorsSense;
        public static CustomOption SenseRadius;
        public static CustomOption HasTasks;
        public static CustomOption CollectCooldown;  // button cooldown after a successful collect
        public static CustomOption RelicPerTasks;    // spawn an extra relic every X crew tasks (0=off)
        public static CustomOption ExtraRaisesGoal;  // each extra relic also raises the needed count

        // ---- Runtime state ----
        public static PlayerControl collector;
        public static bool active;
        public static int collected;
        private static byte collectorPlayerId = byte.MaxValue;
        private static bool relicsSpawned;           // host: relics already placed this game
        private static int relicIdCounter;           // host: next relic id for extra spawns
        private static int extraRelicsGranted;       // host: task-progress relics already spawned
        private static int extraSpawnedTotal;        // ALL clients (via RPC): extra relics in play
        private static float nextTaskCheck;          // host: throttle for the task-progress check
        private static float nextWinTry;             // host: throttle for the instant-win retry

        // Local channel state (Collector's client only).
        private static bool channeling;
        private static float channelStart;
        private static int channelRelicId = -1;
        private static Vector2 channelStartPos;
        // Relic currently being channeled (or -1) - CollectorRelics pulses that crystal as feedback.
        public static int ChannelingRelicId => channeling ? channelRelicId : -1;

        private const int CollectorWinReason = 19;   // Bug uses 18; TOR custom reasons end at 16
        private static byte winnerCollectorId = byte.MaxValue; // survives resetVariables (see Bug)

        // ---- Custom RPC (209) subtypes ----
        private const byte RpcId = UnknownsCollectionPlugin.CollectorRpcId;
        private const byte SubSetCollector = 0;  // playerId
        private const byte SubSpawnRelics = 1;   // count, then count * (x float, y float)
        private const byte SubCollect = 2;       // relicId
        private const byte SubSpawnExtra = 3;    // relicId, x float, y float (task-progress spawn)

        // ---- Role identity ----
        private static RoleInfo collectorInfo;
        public static RoleInfo CollectorInfo() => collectorInfo ??= new RoleInfo(
            "Collector", Color, "Find and collect the hidden relics",
            "Collect the hidden relics", RoleId.Crewmate)
        { isNeutral = true };

        private static TheOtherRoles.Objects.CustomButton collectButton;

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1580, Types.Neutral, "Collector",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1581, Types.Neutral, "Collector Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                RelicsSpawned = CustomOption.Create(1582, Types.Neutral, "Relics Spawned On The Map",
                    6f, 3f, 8f, 1f, SpawnRate);
                RelicsNeeded = CustomOption.Create(1583, Types.Neutral, "Relics Needed To Win",
                    4f, 2f, 8f, 1f, SpawnRate);
                ChannelDuration = CustomOption.Create(1584, Types.Neutral, "Collecting Duration",
                    3f, 1f, 8f, 0.5f, SpawnRate);
                WinMode = CustomOption.Create(1585, Types.Neutral, "Collector Win",
                    new string[] { "Instantly", "Survive To The End" }, SpawnRate);
                ImpostorsSense = CustomOption.Create(1586, Types.Neutral, "Impostors Sense Nearby Relics",
                    false, SpawnRate);
                SenseRadius = CustomOption.Create(1587, Types.Neutral, "Relic Sense Radius",
                    5f, 2f, 10f, 1f, SpawnRate);
                HasTasks = CustomOption.Create(1588, Types.Neutral, "Collector Has Tasks",
                    false, SpawnRate);
                CollectCooldown = CustomOption.Create(1589, Types.Neutral, "Collect Cooldown",
                    15f, 0f, 60f, 2.5f, SpawnRate);
                RelicPerTasks = CustomOption.Create(1596, Types.Neutral, "New Relic Every X Crew Tasks (0 = Off)",
                    0f, 0f, 15f, 1f, SpawnRate);
                ExtraRaisesGoal = CustomOption.Create(1597, Types.Neutral, "Extra Relics Raise The Needed Count",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Collector] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Collector] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalCollector() =>
            active && collector != null && PlayerControl.LocalPlayer != null
            && collector.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static int NeededCount() {
            int baseNeeded = Mathf.Min(
                RelicsNeeded != null ? Mathf.RoundToInt(RelicsNeeded.getFloat()) : 4,
                RelicsSpawned != null ? Mathf.RoundToInt(RelicsSpawned.getFloat()) : 6);
            // Option: every task-progress extra relic also raises the goal - crew task progress
            // actively delays the Collector instead of feeding him.
            if (ExtraRaisesGoal?.getBool() ?? false) baseNeeded += extraSpawnedTotal;
            return baseNeeded;
        }
        public static bool HasAllRelics() => active && collected >= NeededCount();

        // ---- RPC plumbing ----

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetCollector(byte id) {
            try {
                var w = BeginRpc(SubSetCollector);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetCollector(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Collector] SendSet failed: {e}"); }
        }

        private static void SendSpawnRelics(List<Vector2> positions) {
            try {
                var w = BeginRpc(SubSpawnRelics);
                w.Write((byte)positions.Count);
                foreach (var p in positions) { w.Write(p.x); w.Write(p.y); }
                AmongUsClient.Instance.FinishRpcImmediately(w);
                CollectorRelics.SpawnAll(positions);
                relicIdCounter = positions.Count; // extra spawns continue the id sequence
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Collector] SendSpawnRelics failed: {e}"); }
        }

        private static void SendSpawnExtra(int relicId, Vector2 p) {
            try {
                var w = BeginRpc(SubSpawnExtra);
                w.Write((byte)relicId);
                w.Write(p.x);
                w.Write(p.y);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySpawnExtra(relicId, p);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Collector] SendSpawnExtra failed: {e}"); }
        }

        // Runs on every client (sender + RPC receivers) so the raised goal stays in sync.
        private static void ApplySpawnExtra(int relicId, Vector2 p) {
            CollectorRelics.SpawnOne(relicId, p);
            extraSpawnedTotal++;
        }

        private static void SendCollect(int relicId) {
            try {
                var w = BeginRpc(SubCollect);
                w.Write((byte)relicId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyCollect(relicId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Collector] SendCollect failed: {e}"); }
        }

        // ---- RPC application (every client) ----

        private static void ApplySetCollector(byte id) {
            collector = Helpers.playerById(id);
            active = collector != null;
            collectorPlayerId = active ? id : byte.MaxValue;
            collected = 0;
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Collector] The Collector is {collector.Data?.PlayerName}.");
        }

        private static void ApplyCollect(int relicId) {
            var relic = CollectorRelics.ById(relicId);
            Vector2 at = relic != null ? relic.pos : (collector != null ? collector.GetTruePosition() : Vector2.zero);
            CollectorRelics.Collect(relicId);
            collected++;
            // Quiet glitter for EVERYONE near the relic - the deliberate counterplay tell.
            UCAssets.PlayRelicPickup(at);
            if (IsLocalCollector())
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Collector] Collected {collected}/{NeededCount()}.");
            // Instant win happens in TryInstantWin (HudUpdate): a single RpcEndGame here proved
            // fragile - the bypass test-freeze swallowed it and the win was lost for the round.
        }

        // Host: end the game with the Collector's own reason - RETRIED every 2s while the win
        // condition holds, so a swallowed/suppressed RpcEndGame does not lose the win.
        private static void TryInstantWin() {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if ((WinMode?.getSelection() ?? 0) != 0 || !HasAllRelics() || !IsAlive(collector)) return;
            if (Time.time < nextWinTry) return;
            nextWinTry = Time.time + 2f;
            UnknownsCollectionPlugin.Logger?.LogInfo("[Collector] All relics collected - instant win.");
            GameManager.Instance.RpcEndGame((GameOverReason)CollectorWinReason, false);
        }

        public static void MarkFromDraft(byte playerId) => ApplySetCollector(playerId);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetCollector: ApplySetCollector(reader.ReadByte()); break;
                        case SubSpawnRelics: {
                            int count = reader.ReadByte();
                            var positions = new List<Vector2>(count);
                            for (int i = 0; i < count; i++)
                                positions.Add(new Vector2(reader.ReadSingle(), reader.ReadSingle()));
                            CollectorRelics.SpawnAll(positions);
                            break;
                        }
                        case SubCollect: ApplyCollect(reader.ReadByte()); break;
                        case SubSpawnExtra: {
                            int relicId = reader.ReadByte();
                            float ex = reader.ReadSingle();
                            float ey = reader.ReadSingle();
                            ApplySpawnExtra(relicId, new Vector2(ex, ey));
                            break;
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                collector = null;
                active = false;
                collectorPlayerId = byte.MaxValue;
                collected = 0;
                relicsSpawned = false;
                relicIdCounter = 0;
                extraRelicsGranted = 0;
                extraSpawnedTotal = 0;
                nextTaskCheck = 0f;
                nextWinTry = 0f;
                channeling = false;
                channelRelicId = -1;
                // collectButton is deliberately NOT nulled: TOR runs resetVariables at ROUND START,
                // AFTER HudManager.Start created the button. Nulling the static reference here left
                // the live button working (OnClick lambdas use the statics directly) but killed all
                // logic gated on "collectButton != null" - the channel never progressed or aborted.
                CollectorRelics.Clear();
                // NOTE: winnerCollectorId deliberately survives (read after reset at game end, like Bug).
            }
        }

        // ---- Pick + relic spawn (host) ----

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
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
                    SendSetCollector(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] IntroEnd pick failed: {e}");
                }
            }
        }

        // Host: spawn the relics once the game is running. Callers: the intro-end hook below (the
        // earliest sensible moment, covers random AND draft picks) and a per-frame fallback in
        // HudUpdatePatch - in a solo/bypass test round the IntroCutscene.OnDestroy postfix chain
        // demonstrably never reached us (no log line at all), so the intro hook alone is not enough.
        private static void EnsureRelicsSpawned() {
            try {
                if (relicsSpawned) return;
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                if (!active || !AmongUsClient.Instance.IsGameStarted || ShipStatus.Instance == null) return;
                relicsSpawned = true;
                var positions = PickRelicPositions(RelicsSpawned != null ? Mathf.RoundToInt(RelicsSpawned.getFloat()) : 6);
                if (positions.Count == 0) {
                    UnknownsCollectionPlugin.Logger?.LogWarning("[Collector] No relic anchors found on this map!");
                    return;
                }
                SendSpawnRelics(positions);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Collector] relic placement failed: {e}");
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.VeryLow)]
        static class IntroEndRelicsPatch {
            public static void Postfix() => EnsureRelicsSpawned();
        }

        // Map-agnostic anchors: task consoles exist on every map (Skeld/Mira/Polus/Airship/Fungle/
        // Submerged). Excludes critical-sabotage consoles + emergency button (shared rule with the
        // Saboteur traps) and enforces a minimum pairwise distance so relics spread over the map.
        // Shuffled task-console anchors (excluding critical-sabotage consoles + emergency button).
        private static List<Vector2> CollectAnchors() {
            var anchors = new List<Vector2>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Console>()) {
                if (c == null) continue;
                Vector2 p = c.transform.position;
                if (SaboteurTrap.NearCriticalSpot(p, 5f)) continue;
                anchors.Add(p);
            }
            // Shuffle (host rnd - only the host picks).
            for (int i = anchors.Count - 1; i > 0; i--) {
                int j = rnd.Next(i + 1);
                (anchors[i], anchors[j]) = (anchors[j], anchors[i]);
            }
            return anchors;
        }

        private static Vector2 RandomAnchorOffset() =>
            new Vector2((float)(rnd.NextDouble() - 0.5) * 1.6f, (float)(rnd.NextDouble() - 0.5) * 1.6f);

        private static List<Vector2> PickRelicPositions(int count) {
            var anchors = CollectAnchors();
            var picked = new List<Vector2>();
            // Relaxing pairwise spread. Starting at 10 units effectively FORCED the same picks every
            // round: with 6 relics only the map-extreme consoles satisfy 10u pairwise on Polus-sized
            // maps, so the "random" shuffle always converged on the same rooms. 7u keeps the spread
            // but leaves the shuffle real choice.
            foreach (float minDist in new[] { 7f, 5f, 3f, 0f }) {
                foreach (var a in anchors) {
                    if (picked.Count >= count) break;
                    bool tooClose = picked.Any(p => Vector2.Distance(p, a) < minDist);
                    if (!tooClose) picked.Add(a);
                }
                if (picked.Count >= count) break;
            }
            // Random offset so relics don't sit exactly inside console sprites.
            for (int i = 0; i < picked.Count; i++)
                picked[i] += RandomAnchorOffset();
            return picked;
        }

        // One position for a task-progress extra relic: first shuffled anchor that keeps some
        // distance to the relics still lying around (relaxing, so it always finds a spot).
        private static Vector2? PickExtraRelicPosition() {
            var anchors = CollectAnchors();
            if (anchors.Count == 0) return null;
            var existing = CollectorRelics.CurrentPositions();
            foreach (float minDist in new[] { 7f, 5f, 3f, 0f })
                foreach (var a in anchors)
                    if (!existing.Any(p => Vector2.Distance(p, a) < minDist))
                        return a + RandomAnchorOffset();
            return anchors[0] + RandomAnchorOffset();
        }

        // Host: spawn one extra relic per X completed crew tasks (option, 0 = off). Task counts
        // come from GameData (kept current by RecomputeTaskCounts, already adjusted by TaskPatch).
        private static void TrySpawnTaskRelic() {
            try {
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || !relicsSpawned) return;
                int per = RelicPerTasks != null ? Mathf.RoundToInt(RelicPerTasks.getFloat()) : 0;
                if (per <= 0) return;
                if (Time.time < nextTaskCheck) return;
                nextTaskCheck = Time.time + 1f;
                var gd = GameData.Instance;
                if (gd == null) return;
                int earned = gd.CompletedTasks / per;
                while (extraRelicsGranted < earned) {
                    extraRelicsGranted++;
                    var pos = PickExtraRelicPosition();
                    if (pos == null) return;
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[Collector] {gd.CompletedTasks} crew tasks done -> extra relic {relicIdCounter}.");
                    SendSpawnExtra(relicIdCounter++, pos.Value);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Collector] task relic spawn failed: {e}");
            }
        }

        // ---- Collect button + channel ----

        // How close (units) the Collector must stand to a relic's CENTER. 1.4 felt broken in the
        // playtest - standing visually "at" the relic was often just outside it.
        private const float CollectRange = 2.0f;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    collectButton = new TheOtherRoles.Objects.CustomButton(
                        () => {
                            if (channeling) {
                                UnknownsCollectionPlugin.Logger?.LogInfo("[Collector] channel cancelled by button press.");
                                channeling = false;
                                return;
                            }
                            Vector2 herePos = PlayerControl.LocalPlayer.GetTruePosition();
                            var relic = CollectorRelics.NearestRelic(herePos, CollectRange);
                            if (relic == null) {
                                var any = CollectorRelics.NearestRelic(herePos, float.MaxValue);
                                if (any != null)
                                    UnknownsCollectionPlugin.Logger?.LogInfo(
                                        $"[Collector] collect pressed - nearest relic is {Vector2.Distance(herePos, any.pos):F1}u away (range {CollectRange}).");
                                return;
                            }
                            UnknownsCollectionPlugin.Logger?.LogInfo(
                                $"[Collector] channel started on relic {relic.id} ({Vector2.Distance(herePos, relic.pos):F1}u away).");
                            channeling = true;
                            channelStart = Time.time;
                            channelRelicId = relic.id;
                            channelStartPos = PlayerControl.LocalPlayer.GetTruePosition();
                        },
                        () => IsLocalCollector()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead
                              && !HasAllRelics(),
                        () => channeling
                              || (PlayerControl.LocalPlayer.CanMove
                                  && CollectorRelics.NearestRelic(PlayerControl.LocalPlayer.GetTruePosition(), CollectRange) != null),
                        () => { channeling = false; },
                        UCAssets.CollectorIcon,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.F, false, "COLLECT");
                    collectButton.MaxTimer = 1f;
                    collectButton.Timer = 0f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] Button creation failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    CollectorRelics.Tick();
                    if (!active) return;
                    EnsureRelicsSpawned(); // host fallback if the intro hook never fired
                    TrySpawnTaskRelic();
                    TryInstantWin();

                    // NOTE: the channel logic must not be gated on collectButton - the button label
                    // is cosmetic, the progress/abort handling is not.
                    if (IsLocalCollector()) {
                        if (channeling) {
                            float dur = ChannelDuration?.getFloat() ?? 3f;
                            float progress = (Time.time - channelStart) / dur;
                            bool moved = Vector2.Distance(PlayerControl.LocalPlayer.GetTruePosition(), channelStartPos) > 0.5f;
                            bool relicGone = CollectorRelics.ById(channelRelicId) == null;
                            bool blocked = MeetingHud.Instance != null || ExileController.Instance != null
                                           || PlayerControl.LocalPlayer.Data.IsDead;
                            if (moved || relicGone || blocked) {
                                channeling = false;
                                UnknownsCollectionPlugin.Logger?.LogInfo(
                                    $"[Collector] channel aborted (moved={moved}, relicGone={relicGone}, blocked={blocked}).");
                                // Make the silent abort visible - "the click did nothing" feedback.
                                if (moved) Helpers.showFlash(new Color(1f, 0.75f, 0.2f, 0.3f), 0.2f);
                            } else if (progress >= 1f) {
                                channeling = false;
                                SendCollect(channelRelicId);
                                // Cooldown between collects (option): CustomButton blocks clicks
                                // while Timer > 0 and renders the countdown.
                                float cd = CollectCooldown?.getFloat() ?? 15f;
                                if (collectButton != null && cd > 0f) {
                                    collectButton.MaxTimer = cd;
                                    collectButton.Timer = cd;
                                }
                            } else if (collectButton != null) {
                                collectButton.buttonText = $"COLLECT {(int)(progress * 100)}%";
                            }
                        } else if (collectButton != null) {
                            collectButton.buttonText = $"RELICS {collected}/{NeededCount()}";
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] HudUpdate failed: {e}");
                }
            }
        }

        // ---- Win: survive mode hijacks team wins like the Bug (reason 19 instead of 18) ----

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.RpcEndGame))]
        static class RpcEndGameHijackPatch {
            private const int TeamJackalWinReason = 11; // see Bug.cs
            public static void Prefix(ref GameOverReason endReason) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if ((WinMode?.getSelection() ?? 0) != 1) return; // only in Survive-To-End mode
                    if (!active || !HasAllRelics() || !IsAlive(collector)) return;
                    int r = (int)endReason;
                    if (r >= 10 && r != TeamJackalWinReason) return; // team wins only (never solo neutrals)
                    endReason = (GameOverReason)CollectorWinReason;
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Collector] Full Collector survived - hijacking the team win.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] RpcEndGame hijack failed: {e}");
                }
            }
        }

        // ---- Winner list + end screen (mirrors Bug.cs) ----

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
                UnknownsCollectionPlugin.Logger?.LogError($"[Collector] SetWinCondition failed: {e}");
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        static class OnGameEndPatch {
            public static void Prefix() {
                if (active && collectorPlayerId != byte.MaxValue) winnerCollectorId = collectorPlayerId;
            }

            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != CollectorWinReason) return;
                    if (winnerCollectorId == byte.MaxValue) return;

                    PlayerControl winner = Helpers.playerById(winnerCollectorId);
                    if (winner == null || winner.Data == null) return;

                    EndGameResult.CachedWinners.Clear();
                    EndGameResult.CachedWinners.Add(new CachedPlayerData(winner.Data));
                    SetWinCondition(13); // outside TOR's enum (like Bug's 12): our own banner below
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Collector] Collector wins alone!");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] OnGameEnd failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        [HarmonyPriority(Priority.Last)]
        static class EndGameFxPatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != CollectorWinReason) return;
                    if (__instance.WinText != null) {
                        GameObject bonus = UnityEngine.Object.Instantiate(__instance.WinText.gameObject);
                        bonus.transform.position = new Vector3(__instance.WinText.transform.position.x,
                            __instance.WinText.transform.position.y - 0.5f,
                            __instance.WinText.transform.position.z);
                        bonus.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                        var text = bonus.GetComponent<TMP_Text>();
                        text.text = "Collector Wins";
                        text.color = Color;
                    }
                    UCAssets.PlayCollectorWin();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] end-screen FX failed: {e}");
                }
            }
        }

        // ---- Task accounting: a neutral's tasks never count toward the crew total ----

        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || collector == null || collector.Data == null) return;
                    if (HasTasks?.getBool() ?? false) return;
                    var (done, total) = TasksHandler.taskInfo(collector.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= done;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] TaskPatch failed: {e}");
                }
            }
        }

        // ---- Role identity ----

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || collector == null || p == null || p != collector || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = CollectorInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, CollectorInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Collector] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
