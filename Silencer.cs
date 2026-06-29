// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Silencer (Impostor)
 *
 * A normal TOR Impostor is silently promoted to "The Silencer" at game start (host-authoritative pick,
 * broadcast via RPC 194). During a round the Silencer marks a victim with a SILENCE button (cooldown +
 * a per-round budget). A marked player is MUTED in the NEXT meeting: they cannot vote (vote area click
 * + skip blocked) and cannot chat (SendChat blocked). To make the mute obvious to EVERYONE - so the
 * victim can also mute their voice client - a red mute marker is shown next to their name both in-game
 * and on their meeting vote area. The mute lasts exactly one meeting and is cleared when it ends.
 *
 * ARCHITECTURE mirrors the Tesla/Saboteur: brand-new role built WITHOUT touching TOR source - own
 * RoleInfo tag over the real Impostor role, a CustomButton, a small custom RPC (194), client-side
 * cosmetics. Gated by the mod-wide No-Start handshake (everyone has the mod), so the meeting/chat
 * blocks and the marker run safely on every client.
 *
 * Options live in the 1440-1445 block. See ID-Registry.md.
 */

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
    public static class Silencer {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed; // impostor role -> red role tag
        private static readonly Color MarkColor = new Color(1f, 0.31f, 0.31f); // red mute marker
        // Red mute marker shown next to a muted player's name (everyone sees it). Plain text so it
        // renders in every font (the default TMP font has no muted-speaker glyph).
        private const string Marker = " <color=#FF5050><b>[MUTED]</b></color>";

        // ---- Options (IDs 1440-1445) ----
        public static CustomOption SpawnRate;        // 1440 (header) - impostor role chance
        public static CustomOption SpawnMinPlayers;  // 1441 - minimum LOBBY players to spawn
        public static CustomOption MarkCooldown;      // 1442 - SILENCE button cooldown
        public static CustomOption TargetsPerRound;   // 1443 - how many players can be silenced per round
        public static CustomOption CanStillSkip;      // 1444 - a muted player may still press Skip
        public static CustomOption ShowInGameMarker;  // 1445 - also show the mute marker in-game

        // ---- Runtime state ----
        public static PlayerControl silencer;
        public static bool active;
        public static int marksLeftThisRound;
        // Players muted for the CURRENT/NEXT meeting (set when marked, cleared at meeting end).
        public static readonly HashSet<byte> silencedIds = new();

        // Tracks whether muted players' auto-skip has been processed for the current meeting.
        // Reset on meeting end alongside silencedIds.
        private static bool mutesAutoProcessed;

        private static PlayerControl currentTarget; // local Silencer's outlined victim candidate
        private static bool wasInMeeting;

        // ---- Custom RPC (194) subtypes ----
        private const byte RpcId = 194; // == UnknownsCollectionPlugin.SilencerRpcId
        private const byte SubSetSilencer = 0; // silencerId
        private const byte SubSilence = 1;     // targetId
        private const byte SubClear = 2;       // (none) - clear all silences (meeting end)

        // ---- Role identity ----
        private static RoleInfo silencerInfo;
        public static RoleInfo SilencerInfo() => silencerInfo ??= new RoleInfo(
            "Silencer", Color, "Mute a player for the next meeting",
            "Mute a player for the next meeting", RoleId.Impostor);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1440, Types.Impostor, "Silencer",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1441, Types.Impostor, "Silencer Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                MarkCooldown = CustomOption.Create(1442, Types.Impostor, "Silencer Mark Cooldown",
                    25f, 10f, 60f, 2.5f, SpawnRate);
                TargetsPerRound = CustomOption.Create(1443, Types.Impostor, "Silencer Targets Per Round",
                    1f, 1f, 3f, 1f, SpawnRate);
                CanStillSkip = CustomOption.Create(1444, Types.Impostor, "Muted Player Can Still Skip",
                    false, SpawnRate);
                ShowInGameMarker = CustomOption.Create(1445, Types.Impostor, "Show Mute Marker In-Game",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Silencer] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) { /* all patches are attribute-based */ }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalSilencer() =>
            silencer != null && PlayerControl.LocalPlayer != null && silencer.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        public static bool IsSilenced(byte id) => active && silencedIds.Contains(id);
        public static bool LocalIsSilenced() =>
            PlayerControl.LocalPlayer != null && IsSilenced(PlayerControl.LocalPlayer.PlayerId);

        private static int TargetsPerRoundValue() =>
            TargetsPerRound != null ? Mathf.RoundToInt(TargetsPerRound.getFloat()) : 1;

        // ====================================================================
        // Custom RPC senders (each applies locally too)
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        public static void SendSetSilencer(byte id) {
            try {
                var w = BeginRpc(SubSetSilencer);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetSilencer(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] SendSetSilencer failed: {e}"); }
        }

        public static void SendSilence(byte targetId) {
            try {
                var w = BeginRpc(SubSilence);
                w.Write(targetId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySilence(targetId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] SendSilence failed: {e}"); }
        }

        public static void SendClearSilences() {
            try {
                var w = BeginRpc(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClearSilences();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] SendClearSilences failed: {e}"); }
        }

        // ---- Appliers (run on every client) ----
        private static void ApplySetSilencer(byte id) {
            silencer = Helpers.playerById(id);
            active = silencer != null;
            if (active) UCPromotion.Claim(id);
            silencedIds.Clear();
            marksLeftThisRound = TargetsPerRoundValue();
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Silencer] The Silencer is {silencer.Data?.PlayerName}.");
        }

        private static void ApplySilence(byte targetId) {
            silencedIds.Add(targetId);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Silencer] {Helpers.playerById(targetId)?.Data?.PlayerName} will be muted next meeting.");
        }

        private static void ApplyClearSilences() => silencedIds.Clear();

        public static void MarkFromDraft(byte playerId) => ApplySetSilencer(playerId);

        // ====================================================================
        // RPC receiver
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubSetSilencer: ApplySetSilencer(reader.ReadByte()); break;
                        case SubSilence: ApplySilence(reader.ReadByte()); break;
                        case SubClear: ApplyClearSilences(); break;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        // ====================================================================
        // Round reset
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                silencer = null;
                active = false;
                marksLeftThisRound = 0;
                silencedIds.Clear();
                currentTarget = null;
                wasInMeeting = false;
                mutesAutoProcessed = false;
            }
        }

        // ====================================================================
        // Game start: host picks the Silencer among plain Impostors and broadcasts it.
        // Low priority so Tesla (normal) / Saboteur claim first; UCPromotion prevents collisions.
        // ====================================================================
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

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSetSilencer(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Meeting end edge (every client): clear the one-meeting mute + refill the per-round budget.
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    bool nowMeeting = InMeeting();
                    if (wasInMeeting && !nowMeeting) {
                        silencedIds.Clear();              // mute lasts exactly one meeting
                        marksLeftThisRound = TargetsPerRoundValue();
                        mutesAutoProcessed = false;
                    }
                    wasInMeeting = nowMeeting;

                    UpdateTargeting();
                    if (ShowInGameMarker == null || ShowInGameMarker.getBool()) ShowInGameMarkers();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] HudUpdate failed: {e}");
                }
            }
        }

        // Local Silencer: keep the nearest crew target outlined for the SILENCE button.
        private static void UpdateTargeting() {
            if (!IsLocalSilencer() || InMeeting() || marksLeftThisRound <= 0) { currentTarget = null; return; }
            currentTarget = PlayerControlFixedUpdatePatch.setTarget(true);
            if (currentTarget != null) PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, MarkColor);
        }

        // Append the red mute marker to every muted player's in-game name (visible to all).
        private static void ShowInGameMarkers() {
            if (!active || silencedIds.Count == 0 || InMeeting()) return;
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (p == null || p.cosmetics == null || p.cosmetics.nameText == null) continue;
                if (!silencedIds.Contains(p.PlayerId)) continue;
                var t = p.cosmetics.nameText.text;
                if (!t.Contains("MUTED")) p.cosmetics.nameText.text = t + Marker;
            }
        }

        // ====================================================================
        // Meeting: show the mute marker on vote areas + disable the skip button for a muted local player.
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
        static class MeetingUpdatePatch {
            public static void Postfix(MeetingHud __instance) {
                try {
                    if (!active || __instance == null) return;

                    // Auto-skip for silenced local player: cast a skip vote once when the meeting
                    // first opens, so the muted player is excluded from the voting tally and the
                    // meeting can end sooner (they are "already voted").
                    if (LocalIsSilenced() && !mutesAutoProcessed && __instance.playerStates != null
                        && __instance.playerStates.Count > 0)
                    {
                        // Verify the local player's vote area exists and hasn't voted yet
                        foreach (var pva in __instance.playerStates) {
                            if (pva == null) continue;
                            if ((byte)pva.TargetPlayerId == PlayerControl.LocalPlayer.PlayerId
                                && !pva.DidVote)
                            {
                                __instance.CmdCastVote(byte.MaxValue, byte.MaxValue); // skip
                                break;
                            }
                        }
                        mutesAutoProcessed = true;
                    }

                    foreach (var pva in __instance.playerStates) {
                        if (pva == null || pva.NameText == null) continue;
                        if (!silencedIds.Contains((byte)pva.TargetPlayerId)) continue;
                        var t = pva.NameText.text;
                        if (!t.Contains("MUTED")) pva.NameText.text = t + Marker;
                    }
                    // Block the Skip button for a muted local player (unless allowed to skip).
                    if (LocalIsSilenced() && (CanStillSkip == null || !CanStillSkip.getBool())
                        && __instance.SkipVoteButton != null)
                        __instance.SkipVoteButton.gameObject.SetActive(false);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] MeetingUpdate failed: {e}");
                }
            }
        }

        // Block a muted local player from casting a vote (clicking a vote area).
        [HarmonyPatch(typeof(PlayerVoteArea), nameof(PlayerVoteArea.Select))]
        [HarmonyPriority(Priority.High)]
        static class VoteSelectPatch {
            static bool Prefix() => !LocalIsSilenced();
        }

        // Block a muted local player from chatting (during the meeting where they are muted).
        [HarmonyPatch(typeof(ChatController), nameof(ChatController.SendChat))]
        [HarmonyPriority(Priority.High)]
        static class SendChatPatch {
            static bool Prefix(ChatController __instance) {
                if (!LocalIsSilenced() || !InMeeting()) return true;
                try { __instance.freeChatField?.Clear(); __instance.quickChatMenu?.Clear(); } catch { }
                return false; // swallow the message
            }
        }

        // ====================================================================
        // Buttons: the SILENCE mark button.
        // ====================================================================
        private static TheOtherRoles.Objects.CustomButton silenceButton;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    Sprite sprite = __instance.KillButton != null && __instance.KillButton.graphic != null
                        ? __instance.KillButton.graphic.sprite : null;
                    silenceButton = new TheOtherRoles.Objects.CustomButton(
                        () => { // OnClick
                            if (currentTarget == null || marksLeftThisRound <= 0) return;
                            SendSilence(currentTarget.PlayerId);
                            marksLeftThisRound--;
                            silenceButton.Timer = silenceButton.MaxTimer;
                        },
                        () => active && IsLocalSilencer()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead
                              && marksLeftThisRound > 0,
                        () => PlayerControl.LocalPlayer.CanMove && currentTarget != null,
                        () => { },
                        sprite,
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                        __instance, KeyCode.F, false, "SILENCE");
                    silenceButton.MaxTimer = MarkCooldown != null ? MarkCooldown.getFloat() : 25f;
                    silenceButton.Timer = 10f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] Button creation failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity: show the Silencer as its own role over the Impostor entry.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || silencer == null || p == null || p != silencer || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = SilencerInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, SilencerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Silencer] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
