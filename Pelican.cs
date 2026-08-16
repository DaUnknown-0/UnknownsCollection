// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Pelican (Neutral, solo) - Paket W, Stufe 3.
 *
 * A plain TOR Crewmate is silently promoted to "The Pelican" at game start (host-authoritative pick,
 * broadcast on the shared UC channel, module byte 212). He is UC's first neutral KILLER and the first
 * role in this mod with a win condition of its own that the crew cannot simply out-live:
 *
 *   SWALLOW  His kill button does not leave a body. The victim dies for real (TOR's whole kill chain
 *            runs: shields, armor, Mini, Time Master, ...), but the corpse is hidden the moment it
 *            spawns - it is NOT destroyed, because it still has to be able to come back.
 *   BELLY    Everyone he carries is listed in a self-only HUD readout (pelican_belly). Nobody else
 *            sees it; the rest of the ship only sees people who are simply GONE.
 *   DIGEST   The first meeting is the point of no return: the hidden corpses are destroyed and the
 *            swallowed are finally, unambiguously dead. (They already were - Among Us has no living
 *            "inside the belly" state and no in-game voice, so the GGD original's living stomach
 *            chat is adapted to "hidden-dead until digested or released"; see WEREWOLF_PLAN.md §10.)
 *   RELEASE  If the Pelican dies first, everyone still in the belly comes back ALIVE around his corpse
 *            (playtest 2026-07-26; they used to reappear as bodies). That is the built-in counterplay,
 *            and a much sharper one: kill him early and you get your crew back, kill him late and the
 *            meeting has already digested them. While they are in there the VITALS station lists them
 *            as alive - the one readout that answers "is he still with us", and a swallowed player
 *            still can be.
 *   BELLY VIEW  Because he is not finally dead yet, a swallowed player does not get the ghost's usual
 *            reward: no roles, no ghost info, no walking. His camera is locked onto the Pelican - the
 *            one thing he may watch is the bird that ate him. All three end the moment the belly does
 *            (digestion or release); see TickBelly.
 *   HUNT     The moment exactly two players are alive and one of them is the Pelican, the hunt starts:
 *            a public countdown (option 1547), and for EVERYONE no meetings, no reports and no vents
 *            (abilities and the vanilla Impostor kill deliberately stay - see below). Eats the last
 *            survivor -> the Pelican wins alone. Countdown runs out -> the survivor wins with HIS OWN
 *            team / win condition and the Pelican loses.
 *
 * WHY THE PATCHES LOOK LIKE THEY DO
 * ---------------------------------
 *  - THE END-GAME GUARD is the heart of this file. The Pelican is a neutral tag over a Crewmate, so
 *    TOR's PlayerStatistics counts him as crew: the instant the last Impostor dies,
 *    CheckAndEndGameForCrewmateWin (EndGamePatch.cs:562-577) would hand the crew the win while a
 *    living killer is still on the ship. A GameManager.RpcEndGame PREFIX therefore suppresses exactly
 *    the two "no killers left" crew reasons while the Pelican lives - and, once the board is down to
 *    the two hunt participants, every team win, because otherwise an Impostor survivor would end the
 *    round (CheckAndEndGameForImpostorWin fires at 1-vs-1) before the hunt could even start. Task
 *    wins, sabotage wins and the neutral solo wins are never touched: those are legitimate losses.
 *    Priority.First puts this prefix AHEAD of Bug's and Collector's own RpcEndGame prefixes, so it
 *    always inspects the RAW reason instead of one they already rewrote.
 *  - THE COUNTDOWN EXPIRY simply STOPS suppressing instead of broadcasting a hand-built win. TOR's
 *    CheckEndCriteria runs every frame on the host anyway, so the very next tick ends the round with
 *    whatever reason the survivor's own situation produces - crew, Impostor, Jackal team, Lovers,
 *    Jester. That is literally "the survivor wins with his own win condition", with zero duplicated
 *    win logic.
 *  - BODIES are hidden with gameObject.SetActive(false), the Shade's proven mechanic (Shade.cs:153-167)
 *    rather than Destroy: a destroyed DeadBody cannot be brought back on the Pelican's corpse.
 *  - THE SWALLOW LIST needs no RPC. Every client executes PlayerControl.MurderPlayer for every kill
 *    (TOR routes all of them through RPCProcedure.uncheckedMurderPlayer, RPC.cs:480), and "was the
 *    killer the Pelican" is synced state - so each client maintains its own identical list. The same
 *    is true for the release and for "the Pelican is the only one left": no message can be lost or
 *    arrive twice, which is also why a body can never be released twice (the list is cleared in the
 *    same call that reveals it).
 *  - THE HUNT RESTRICTIONS reuse the shapes W1 established: a Vent.CanUse POSTFIX (TOR replaces that
 *    method with a prefix returning false, so only a postfix has the last word), a
 *    SabotageButton.Refresh POSTFIX (TOR's own Janitor block, UsablesPatch.cs:205-215), an
 *    EmergencyMinigame.Update POSTFIX (TOR's Swapper/Jester block, UsablesPatch.cs:225-255) and a
 *    PlayerControl.CmdReportDeadBody PREFIX - the single funnel BOTH the report button and the
 *    emergency button go through, so blocking it there cannot be routed around. TOR patches that
 *    method too; returning false only skips the ORIGINAL, never TOR's prefix.
 *    Ability BUTTONS are left alone: freezing every CustomButton's Timer did block them, but a whole
 *    HUD of parked cooldowns reads as a broken game (playtest 2026-07-26), so the hunt restricts
 *    movement and information instead of taking abilities away.
 *  - MUSIC runs on the UCMusic channel (cue "pelican_hunt", priority 50), never on SoundManager
 *    directly, so it can never layer over the werewolf form music or a reactor. The loop VARIANT is
 *    rolled once by the host and shipped inside the role-assignment RPC (the Werewolf does the same
 *    with its seven variants), so the whole lobby hears the same score.
 *
 * Options: 1544-1549. Win reason: 32 (see the constant below). See ID-Registry.md.
 * RPC: module byte 212 on UCRpc.CallId 230.
 * NOT in this stage (Paket W4): UCRoleDraft entry, UCGuesser entry, UCHelpMenu page,
 * TeslaVersionHandshake.AnyUCRoleEnabled, the UCKillOverlay beak cutscene (its sprite is already
 * registered as UCAssets.OverlayPelican).
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
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Pelican {
        // ---- Theme ----
        public static readonly Color Color = new Color(0.16f, 0.78f, 0.74f); // pelican teal

        // ---- Options (1544-1549) ----
        public static CustomOption SpawnRate;          // 1544 (header, rates)
        public static CustomOption SpawnMinPlayers;    // 1545
        public static CustomOption SwallowCooldown;    // 1546
        public static CustomOption HuntCountdown;      // 1547 (s, default 60)
        public static CustomOption HasTasks;           // 1548
        public static CustomOption HuntBlocksSabotage; // 1549

        // ---- Runtime state (all of it derived from synced data, therefore equal on every client) ----
        public static PlayerControl pelican;
        public static bool active;
        private static byte pelicanPlayerId = byte.MaxValue;
        // Which of the six hunt loop variants this round uses - rolled by the host inside the
        // assignment RPC, exactly like the Werewolf's form music.
        private static int musicVariant;

        // victimId -> the hidden DeadBody (kept, never destroyed, until digestion or release).
        private static readonly Dictionary<byte, DeadBody> swallowedBodies = new();
        // victimId in swallow order - drives the belly readout and survives a body that never spawned.
        private static readonly List<byte> swallowed = new();
        // AUDIT-2026-08-16: bumped every time `swallowed` actually changes (swallow/release/digest).
        // TickHud compares against swallowedNamesCacheVersion so it only rewalks the list and resolves
        // names again on frames where the belly contents moved, instead of on every single frame.
        private static int swallowedVersion;
        // Reused belly-name buffer (avoids a fresh List<string> allocation every frame) plus the
        // swallowedVersion it was last built from.
        private static readonly List<string> swallowedNamesCache = new();
        private static int swallowedNamesCacheVersion = -1;
        // Victims whose DeadBody was not found in the murder postfix yet (retried for a few frames).
        private static readonly List<byte> pendingHide = new();
        private static float pendingHideUntil;

        // ---- Hunt phase ----
        public static bool huntActive;      // the countdown is running (every client)
        private static bool huntEnded;      // the hunt is over WITHOUT a Pelican win -> guard is off
        private static float huntEndTime;   // local Time.time deadline (host-resynced every 5 s)
        private static float huntStartTime; // used only to decide when the intro has finished
        private static float nextHuntSync;  // host: next resync broadcast
        private static float nextCall;      // next public pelican_call croak

        // ---- Win ----
        private static bool winOutro;       // the Pelican is alone; the outro is playing
        private static float winEndAt;      // host: when the deferred RpcEndGame fires
        private static float nextWinTry;    // host: retry throttle (Collector precedent)
        private static bool sawMultipleAlive; // arms the sole-survivor check (see PollWin)
        private static float nextGuardLog;

        // Own GameOverReason. TOR's own customs end at 16, the Bug uses 18 plus the hijack block
        // 20-26 and 31, the Collector uses 19 - so the first value that is guaranteed free above ALL
        // of them is 32. TOR maps every reason >= 10 to ImpostorByKill for vanilla
        // (EndGamePatch.cs:71) and keeps the real one in OnGameEndPatch.gameOverReason, which is what
        // the winner list and the banner below read.
        private const int PelicanWinReason = 32;
        private const int TeamJackalWinReason = 11; // TOR's CustomGameOverReason.TeamJackalWin
        private static byte winnerPelicanId = byte.MaxValue; // survives resetVariables (Bug/Collector rule)

        // ---- Constants ----
        private const int MusicVariants = 6;          // pelican_hunt_music + music2..music6
        private const string MusicCue = "pelican_hunt";
        private const int MusicPriority = 50;         // WEREWOLF_PLAN.md §11.2 (reactor 100 outranks us)
        private const float MusicVolume = 0.6f;
        private const float IntroFallbackSecs = 9.23f;
        private const float OutroFallbackSecs = 6.0f;
        private const float HuntSyncInterval = 5f;
        private const float CallInterval = 8f;

        // ---- Custom RPC subtypes: module byte 212 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.PelicanRpcId;
        private const byte SubSetPelican = 0; // playerId, musicVariant
        private const byte SubStartHunt = 1;  // seconds(float)
        private const byte SubHuntSync = 2;   // secondsRemaining(float)
        private const byte SubEndHunt = 3;    // (no payload - the hunt only ever ends one way here)

        // ---- Role identity ----
        private static RoleInfo pelicanInfo;
        public static RoleInfo PelicanInfo() => pelicanInfo ??= new RoleInfo(
            "Pelican", Color, "Swallow them all and be the last one standing",
            "Swallow them all and be the last one standing", RoleId.Crewmate)
        { isNeutral = true };

        private static TheOtherRoles.Objects.CustomButton swallowButton;
        private static PlayerControl currentTarget;

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1544, Types.Neutral, "Pelican",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1545, Types.Neutral, "Pelican Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                SwallowCooldown = CustomOption.Create(1546, Types.Neutral, "Pelican Swallow Cooldown",
                    27.5f, 10f, 60f, 2.5f, SpawnRate);
                HuntCountdown = CustomOption.Create(1547, Types.Neutral, "Pelican Hunt Countdown (s)",
                    60f, 15f, 180f, 5f, SpawnRate);
                HasTasks = CustomOption.Create(1548, Types.Neutral, "Pelican Has Tasks",
                    false, SpawnRate);
                // Deliberately verbose and role-specific: UCLocalization matches option NAMES (and
                // selection texts) by their English string across every uc.* key, so a generic label
                // would silently re-translate unrelated options elsewhere in the mod (the same reason
                // options 1507 and 1557 spell their choices out).
                HuntBlocksSabotage = CustomOption.Create(1549, Types.Neutral, "Hunt Phase Also Blocks Sabotage",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Pelican] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            // Receiver registration for the shared UC channel (UCRpc.CallId = 230). Every module
            // registers here even when it has no reflection work left to do - TryPatch is the single
            // place UnknownsCollectionPlugin.Load() calls for every module.
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;

        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);

        private static int AliveCount() {
            int n = 0;
            foreach (var p in PlayerControl.AllPlayerControls) if (IsAlive(p)) n++;
            return n;
        }

        public static bool IsLocalPelican() =>
            active && pelican != null && PlayerControl.LocalPlayer != null
            && pelican.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        private static float HuntSeconds() => HuntCountdown != null ? HuntCountdown.getFloat() : 60f;
        private static float CooldownValue() => SwallowCooldown != null ? SwallowCooldown.getFloat() : 27.5f;

        // The hunt restrictions apply to EVERY player, not just the two participants: a ghost cannot
        // vent anyway, but a third-party ability (a Poltergeist haunt, an Engineer fix) would still
        // interfere with a duel that is supposed to be exactly two people and a clock.
        public static bool HuntRestrictionsActive() => active && huntActive && !huntEnded;

        private static string LoopClipName() =>
            musicVariant <= 0 ? "pelican_hunt_music" : $"pelican_hunt_music{musicVariant + 1}";

        private static float ClipLength(string name, float fallback) {
            try {
                var c = UCAssets.GetClipByName(name);
                return c != null && c.length > 0.1f ? c.length : fallback;
            } catch { return fallback; }
        }

        // ====================================================================
        // RPC
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = UCRpc.Begin(RpcId); // shared UC channel; RpcId is the module byte
            w.Write(subtype);
            return w;
        }

        public static void SendSetPelican(byte id, byte variant) {
            try {
                var w = BeginRpc(SubSetPelican);
                w.Write(id);
                w.Write(variant);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetPelican(id, variant);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] SendSetPelican failed: {e}"); }
        }

        private static void SendStartHunt(float secs) {
            try {
                var w = BeginRpc(SubStartHunt);
                w.Write(secs);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyStartHunt(secs);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] SendStartHunt failed: {e}"); }
        }

        private static void SendHuntSync(float remaining) {
            try {
                var w = BeginRpc(SubHuntSync);
                w.Write(remaining);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyHuntSync(remaining);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] SendHuntSync failed: {e}"); }
        }

        private static void SendEndHunt() {
            try {
                var w = BeginRpc(SubEndHunt);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyEndHunt();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] SendEndHunt failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSetPelican: {
                        byte id = reader.ReadByte();
                        byte variant = reader.ReadByte();
                        // Host-authoritative role assignment (host pick in IntroCutscene.OnDestroy / UCRoleDraft) - a
                    // forged one would let any client declare any player this role (AUDIT H-3).
                        if (UCRpc.RequireHost("Pelican.SetPelican")) ApplySetPelican(id, variant);
                        break;
                    }
                    case SubStartHunt: {
                        float seconds = reader.ReadSingle();
                        // Host-authoritative (HostTickHunt) - a forged one would restart the hunt
                        // timer for everyone at will (AUDIT-2026-08-15).
                        if (UCRpc.RequireHost("Pelican.StartHunt")) ApplyStartHunt(seconds);
                        break;
                    }
                    case SubHuntSync: {
                        float secondsRemaining = reader.ReadSingle();
                        // Host-authoritative (HostTickHunt) - see SubStartHunt above (AUDIT-2026-08-15).
                        if (UCRpc.RequireHost("Pelican.HuntSync")) ApplyHuntSync(secondsRemaining);
                        break;
                    }
                    case SubEndHunt: {
                        // Host-authoritative (HostTickHunt). ApplyEndHunt only bails out on
                        // "!huntActive && huntEnded" - a forged one right after role draft would
                        // latch huntEnded=true forever and disable EndGameGuardPatch for the whole
                        // round (AUDIT-2026-08-15).
                        if (UCRpc.RequireHost("Pelican.EndHunt")) ApplyEndHunt();
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] HandleRpc failed: {e}");
            }
        }

        // ====================================================================
        // Appliers (every client)
        // ====================================================================
        private static void ApplySetPelican(byte id, byte variant) {
            // A handover (or a withdrawal via id 255) has to undo what the PREVIOUS bird left
            // behind, not just reset the round fields: anyone still inside the belly would stay
            // swallowed forever (the per-frame driver that releases them only runs while a pelican
            // is active), the hunt music would keep playing and the hunt HUD would stay up.
            // Idempotent - on a first assignment there is nothing to release.
            try { UCMusic.Release(MusicCue); } catch { }
            try { PelicanHud.HideAll(); } catch { }
            ForceLeaveBelly();

            pelican = Helpers.playerById(id);
            active = pelican != null;
            pelicanPlayerId = active ? id : byte.MaxValue;
            if (active) UCPromotion.Claim(id);
            musicVariant = Mathf.Clamp(variant, 0, MusicVariants - 1);
            ClearRoundState();
            if (active)
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Pelican] The Pelican is {pelican.Data?.PlayerName} (hunt music variant {musicVariant + 1}).");
        }

        // The draft has no music byte to carry, so the variant is derived from the drafted player -
        // still identical on every client, which is all the shared-identity rule needs. (The draft
        // ENTRY itself is Paket W4; this hook exists so W4 only has to register the sentinel.)
        public static void MarkFromDraft(byte playerId) =>
            ApplySetPelican(playerId, (byte)(playerId % MusicVariants));

        private static void ApplyStartHunt(float secs) {
            if (!active || huntActive || huntEnded) return;
            huntActive = true;
            huntStartTime = Time.time;
            huntEndTime = Time.time + secs;
            nextCall = Time.time + 4f;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Pelican] The hunt begins ({secs:F0}s).");
            try { Helpers.showFlash(Color, 2.0f, UCLocalization.Tr("uc.ui.pelican.hunt_flash")); } catch { }
        }

        // Host resync of the display. The host alone decides WHEN the hunt is over; this only keeps
        // the number on everyone's screen from drifting apart over a long countdown.
        private static void ApplyHuntSync(float remaining) {
            if (!active || !huntActive) return;
            huntEndTime = Time.time + Mathf.Max(0f, remaining);
        }

        // The hunt ended WITHOUT the Pelican eating the last survivor (countdown expired, or the
        // Pelican died/left). huntEnded switches the end-game guard off for good, so TOR's own
        // CheckEndCriteria ends the round on its next tick with the survivor's own win reason.
        private static void ApplyEndHunt() {
            if (!huntActive && huntEnded) return;
            huntActive = false;
            huntEnded = true;
            try { UCMusic.Release(MusicCue); } catch { }
            PelicanHud.HideHunt();
            UnknownsCollectionPlugin.Logger?.LogInfo("[Pelican] The hunt is over - the Pelican failed.");
        }

        // ====================================================================
        // Round reset
        // ====================================================================
        private static void ClearRoundState() {
            swallowedBodies.Clear();
            swallowed.Clear();
            // AUDIT-2026-08-16: reset the belly-name throttle cache too, or a leftover version number
            // from the previous round could match this round's fresh swallowedVersion=0 and leave the
            // belly HUD showing stale (or, worse, blank-after-recreation) names.
            swallowedVersion = 0;
            swallowedNamesCache.Clear();
            swallowedNamesCacheVersion = -1;
            pendingHide.Clear();
            pendingHideUntil = 0f;
            huntActive = false;
            huntEnded = false;
            huntEndTime = 0f;
            huntStartTime = 0f;
            nextHuntSync = 0f;
            nextCall = 0f;
            winOutro = false;
            winEndAt = 0f;
            nextWinTry = 0f;
            sawMultipleAlive = false;
            nextGuardLog = 0f;
            currentTarget = null;
        }

        private static void ClearState() {
            try { UCMusic.Release(MusicCue); } catch { }
            try { PelicanHud.HideAll(); } catch { }
            // AUDIT-2026-08-16: PelicanHud keeps its own "last shown" throttle caches; clear them here
            // so a stale value from the previous round can't suppress the next round's first update.
            try { PelicanHud.ResetState(); } catch { }
            ForceLeaveBelly();
            pelican = null;
            active = false;
            pelicanPlayerId = byte.MaxValue;
            musicVariant = 0;
            ClearRoundState();
            // swallowButton is deliberately NOT nulled: resetVariables runs at ROUND START, AFTER
            // HudManager.Start built the button (the documented UC pitfall).
            // winnerPelicanId deliberately survives - it is read after the reset, at game end.
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { ClearState(); }
        }

        // Same belt-and-suspenders rule the rest of the mod adopted after the "resetVariables lobby
        // leak": the PlayerId lists above must never travel into a FOREIGN lobby.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() { ClearState(); }
        }

        // ====================================================================
        // Game start: host-authoritative pick
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    // The loop variant is rolled ONCE per round, here, and travels with the role
                    // assignment - so the whole lobby hears the same hunt score.
                    SendSetPelican(candidates[rnd.Next(candidates.Count)].PlayerId, (byte)rnd.Next(MusicVariants));
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Swallow button
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = UCAssets.PelicanSwallowIcon
                        ?? (__instance.KillButton != null && __instance.KillButton.graphic != null
                            ? __instance.KillButton.graphic.sprite : null);
                    swallowButton = new TheOtherRoles.Objects.CustomButton(
                        OnSwallowClick,
                        () => active && IsLocalPelican()
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => currentTarget != null && PlayerControl.LocalPlayer.CanMove,
                        () => { if (swallowButton != null) swallowButton.Timer = swallowButton.MaxTimer; },
                        sprite,
                        // The slot every non-Impostor killer in TOR uses (Jackal/Sidekick,
                        // Buttons.cs:1057): the Pelican is always promoted onto a PLAIN Crewmate, so
                        // no TOR ability button can ever share the row with it.
                        TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowRight,
                        __instance, KeyCode.Q, false, UCLocalization.Tr("uc.ui.pelican.button_swallow"));
                    swallowButton.MaxTimer = CooldownValue();
                    swallowButton.Timer = CooldownValue();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] Button creation failed: {e}");
                }
            }
        }

        // Exactly the shape TOR's own Jackal button uses (Buttons.cs:1046-1060), so every TOR
        // shield / armor / rewind interaction behaves identically for the Pelican. The body hiding
        // is NOT done here: it hangs off PlayerControl.MurderPlayer so it also covers a swallow
        // that some other code path triggers.
        private static void OnSwallowClick() {
            try {
                if (!active || !IsLocalPelican() || currentTarget == null) return;
                if (Helpers.checkMurderAttemptAndKill(pelican, currentTarget) == MurderAttemptResult.SuppressKill) return;
                if (swallowButton != null) swallowButton.Timer = swallowButton.MaxTimer;
                currentTarget = null;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] swallow click failed: {e}");
            }
        }

        // ====================================================================
        // Per-frame driver
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    // Runs BEFORE the "is there even a Pelican" bail-out: this is also the path that
                    // hands movement, the camera and the ghost info back once the belly lets go.
                    TickBelly();

                    if (!active || pelican == null) { PelicanHud.HideAll(); return; }

                    RetryPendingHides();
                    TickHunt();
                    PollSoleSurvivor();
                    TickMusic();
                    TickHud();
                    if (AmHost()) { HostTickHunt(); HostTickWin(); }
                    if (IsLocalPelican()) TickOwner();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] HudUpdate failed: {e}");
                }
            }
        }

        // ====================================================================
        // Inside the belly: the swallowed spectate the Pelican and learn nothing
        // ====================================================================
        //
        // A swallowed player is dead in Among Us terms but NOT dead in the round's terms: while the
        // Pelican carries him he can still walk back out (RELEASE), which is why the vitals list him as
        // alive. So he must not get the ghost's usual reward either - reading everyone's role off their
        // head and flying wherever he likes. Until the belly lets go of him (digested at the first
        // meeting, or released when the Pelican falls) he is inside it: no role information, no walking,
        // and exactly one thing to look at - the bird that ate him.
        //
        // Everything here is LOCAL and edge-triggered, and each of the three pieces remembers whether WE
        // were the ones who took it away - nothing is ever handed back that we did not take.
        private static bool bellyLocked;     // we set moveable = false
        private static bool bellyCamera;     // we moved the camera off the local player
        private static bool bellyInfoHidden; // we forced TOR's ghost-info flags off

        // "The belly still has him." Dead (a release makes him alive again, which ends this state by
        // itself) and still on the swallow list (digestion at the first meeting clears it for good).
        public static bool LocalIsSwallowed() {
            try {
                var me = PlayerControl.LocalPlayer;
                if (!active || me == null || me.Data == null || !me.Data.IsDead) return false;
                return swallowed.Contains(me.PlayerId);
            } catch {
                return false;
            }
        }

        private static void TickBelly() {
            bool inBelly = LocalIsSwallowed();
            var me = PlayerControl.LocalPlayer;

            // 1. No role information. TOR reads these three flags in every place where it decides what a
            //    ghost may see (Helpers.shouldShowGhostInfo:232, the name plates in
            //    PlayerControlPatch:550-561, the haunt menu), so switching them off covers all of them
            //    at once instead of chasing each readout with a patch of its own.
            if (inBelly) {
                bellyInfoHidden = true;
                SetGhostFlags(false, false, false);
            } else if (bellyInfoHidden) {
                bellyInfoHidden = false;
                RestoreGhostInfoFlags();
            }

            var hud = FastDestroyableSingleton<HudManager>.Instance;
            var cam = hud != null ? hud.PlayerCam : null;

            // 2. Spectate the bird. Re-applied every frame on purpose: AU re-targets the camera itself
            //    (meeting, exile, respawn), and this has to win right after it does.
            if (cam != null) {
                if (inBelly && pelican != null && pelican.Data != null && !pelican.Data.Disconnected) {
                    if (cam.Target != pelican) cam.SetTarget(pelican);
                    bellyCamera = true;
                } else if (bellyCamera) {
                    bellyCamera = false;
                    if (me != null) cam.SetTarget(me);
                }
            }

            // 3. Nobody walks around inside a stomach. `moveable` is never touched during a meeting: AU
            //    owns it there, and handing movement back mid-vote is exactly the kind of release the
            //    Saboteur's traps had to learn to avoid.
            if (InMeeting() || me == null) return;
            if (inBelly) {
                bellyLocked = true;
                if (me.moveable) me.moveable = false;
            } else if (bellyLocked) {
                bellyLocked = false;
                me.moveable = true;
            }
        }

        // Restored from TOR'S OWN CONFIG rather than from a saved copy: the client options menu writes
        // the config entry AND the flag (ClientOptionsPatch.cs:17-20), so the config is the truthful
        // "what did this player actually pick" source even if he toggled it while inside the belly.
        private static void RestoreGhostInfoFlags() {
            try {
                SetGhostFlags(TheOtherRolesPlugin.GhostsSeeRoles.Value,
                              TheOtherRolesPlugin.GhostsSeeModifier.Value,
                              TheOtherRolesPlugin.GhostsSeeInformation.Value);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] ghost-info restore failed: {e}");
            }
        }

        // TORMapOptions is INTERNAL to TOR, so its three ghost-info flags are written by reflection -
        // the same idiom this file already uses for TOR's internal GameHistory ledger. Resolved once;
        // if a future TOR renames them the belly simply keeps whatever ghost info it would have had,
        // which is a readable degradation instead of a crash in a per-frame path.
        private static bool ghostFlagsResolved;
        private static FieldInfo fGhostRoles, fGhostModifier, fGhostInfo;

        private static void SetGhostFlags(bool roles, bool modifier, bool info) {
            if (!ghostFlagsResolved) {
                ghostFlagsResolved = true;
                try {
                    var t = AccessTools.TypeByName("TheOtherRoles.TORMapOptions");
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                    fGhostRoles = t?.GetField("ghostsSeeRoles", flags);
                    fGhostModifier = t?.GetField("ghostsSeeModifier", flags);
                    fGhostInfo = t?.GetField("ghostsSeeInformation", flags);
                    if (fGhostRoles == null || fGhostModifier == null || fGhostInfo == null)
                        UnknownsCollectionPlugin.Logger?.LogWarning(
                            "[Pelican] TORMapOptions ghost flags not found - the swallowed keep their ghost info.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] ghost flag lookup failed: {e}");
                }
            }
            try {
                fGhostRoles?.SetValue(null, roles);
                fGhostModifier?.SetValue(null, modifier);
                fGhostInfo?.SetValue(null, info);
            } catch { }
        }

        // Unconditional release for the two reset paths. A leftover moveable=false, a camera parked on
        // last round's Pelican or permanently blinded ghost info would all travel into the next game -
        // the same lobby-leak rule the rest of the mod follows.
        private static void ForceLeaveBelly() {
            if (bellyInfoHidden) { bellyInfoHidden = false; RestoreGhostInfoFlags(); }
            try {
                var me = PlayerControl.LocalPlayer;
                if (bellyCamera) {
                    bellyCamera = false;
                    var hud = FastDestroyableSingleton<HudManager>.Instance;
                    if (hud != null && hud.PlayerCam != null && me != null) hud.PlayerCam.SetTarget(me);
                }
                if (bellyLocked) {
                    bellyLocked = false;
                    if (me != null) me.moveable = true;
                }
            } catch { }
        }

        // A DeadBody is normally already instantiated when our MurderPlayer postfix runs (that is what
        // the Shade relies on), but the object is created by the game, not by us - so a victim that
        // was not found immediately is retried for half a second instead of staying visible forever.
        private static void RetryPendingHides() {
            if (pendingHide.Count == 0) return;
            if (Time.time > pendingHideUntil) { pendingHide.Clear(); return; }
            for (int i = pendingHide.Count - 1; i >= 0; i--)
                if (HideBodyOf(pendingHide[i])) pendingHide.RemoveAt(i);
        }

        private static void TickHunt() {
            // Safety net on EVERY client: a Pelican who died or left ends the hunt locally even if the
            // host's SubEndHunt never arrives (the host does the same thing authoritatively below).
            if (huntActive && !IsAlive(pelican)) {
                huntActive = false;
                huntEnded = true;
                try { UCMusic.Release(MusicCue); } catch { }
                PelicanHud.HideHunt();
            }
            // Public croak while the hunt is running: the survivor gets a directional, distance-graded
            // tell of where the Pelican is. This is the counterplay to the hunt's total lockdown.
            if (huntActive && !InMeeting() && Time.time >= nextCall) {
                nextCall = Time.time + CallInterval;
                try { UCAssets.PlayPelicanCallAt(pelican.GetTruePosition()); } catch { }
            }
        }

        // UCMusic wants a Request EVERY frame while the cue should be audible, plus a Release at the
        // end (done in ApplyEndHunt / ClearState). The clip changes WITHIN the cue - intro once, then
        // the loop variant, then the outro - which is exactly the intra-cue switch UCMusic.Request
        // supports (it hard-cuts and restarts the position when the clip name changes).
        private static void TickMusic() {
            if (InMeeting()) return;
            if (winOutro) {
                UCMusic.Request(MusicCue, "pelican_hunt_end", MusicPriority, MusicVolume,
                                ClipLength("pelican_hunt_end", OutroFallbackSecs), false);
                return;
            }
            if (!huntActive) return;
            float remain = Mathf.Max(0f, huntEndTime - Time.time);
            bool intro = Time.time < huntStartTime + ClipLength("pelican_hunt_intro", IntroFallbackSecs);
            UCMusic.Request(MusicCue, intro ? "pelican_hunt_intro" : LoopClipName(),
                            MusicPriority, MusicVolume, remain, !intro);
        }

        private static void TickHud() {
            if (huntActive && !InMeeting()) PelicanHud.ShowHunt(huntEndTime - Time.time);
            else PelicanHud.HideHunt();

            // Self-only readout, re-gated every frame (never "created once for the Pelican and then
            // left alone"): a stale belly overlay would tell a spectating client who is dead.
            if (IsLocalPelican() && !InMeeting() && IsAlive(pelican)) {
                // AUDIT-2026-08-16: the swallowed set changes rarely (a swallow, a release, a
                // digestion), so only walk it and resolve names again when swallowedVersion moved
                // since the last build - not on every one of the ~60 frames/second this runs on.
                if (swallowedNamesCacheVersion != swallowedVersion) {
                    swallowedNamesCacheVersion = swallowedVersion;
                    swallowedNamesCache.Clear();
                    foreach (var id in swallowed) {
                        var p = Helpers.playerById(id);
                        swallowedNamesCache.Add(p?.Data?.PlayerName ?? id.ToString());
                    }
                }
                PelicanHud.ShowBelly(swallowedNamesCache, swallowedVersion);
            } else {
                PelicanHud.HideBelly();
            }
        }

        private static void HostTickHunt() {
            if (!active || huntEnded) return;

            if (!huntActive) {
                // Trigger: exactly two players alive and one of them is the Pelican.
                if (IsAlive(pelican) && AliveCount() == 2 && !InMeeting()
                    && AmongUsClient.Instance.IsGameStarted) {
                    SendStartHunt(HuntSeconds());
                }
                return;
            }

            if (!IsAlive(pelican)) { SendEndHunt(); return; }
            if (Time.time >= huntEndTime) {
                // The clock ran out. No hand-built "the survivor wins" broadcast: dropping the
                // end-game guard is enough, TOR's CheckEndCriteria ends the round on its next tick
                // with the survivor's OWN win reason (crew, Impostor, Jackal team, Lovers, ...).
                SendEndHunt();
                return;
            }
            if (Time.time >= nextHuntSync) {
                nextHuntSync = Time.time + HuntSyncInterval;
                SendHuntSync(huntEndTime - Time.time);
            }
        }

        // Sole-survivor detection. Deliberately NOT host-only and deliberately NOT an RPC: "how many
        // players are alive" is synced state, so every client reaches the same verdict in the same
        // frame and can start the outro locally - a host-only flag would have left every remote client
        // silent through the whole finale. sawMultipleAlive arms it, so a half-initialised round (or a
        // bypass/solo test where the player list is still filling) can never declare an instant win.
        private static void PollSoleSurvivor() {
            if (!active || winOutro) return;
            if (AliveCount() >= 2) { sawMultipleAlive = true; return; }
            if (!sawMultipleAlive || !IsAlive(pelican)) return;
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.IsGameStarted) return;
            winOutro = true;
            huntActive = false;                       // the countdown is moot, the outro takes over
            PelicanHud.HideHunt();
            winEndAt = Time.time + ClipLength("pelican_hunt_end", OutroFallbackSecs);
            UnknownsCollectionPlugin.Logger?.LogInfo("[Pelican] Last survivor swallowed - playing the outro.");
        }

        private static void HostTickWin() {
            if (!active) return;
            // Deferred end: the graceful-end outro was authored for exactly this moment (it opens on a
            // downbeat hit that masks the loop cut and closes on the final croak), so the round is held
            // open until it has played. RETRIED every 2 s like the Collector's instant win, so a
            // swallowed RpcEndGame cannot lose the win.
            if (winOutro && Time.time >= winEndAt && Time.time >= nextWinTry) {
                nextWinTry = Time.time + 2f;
                try {
                    GameManager.Instance.RpcEndGame((GameOverReason)PelicanWinReason, false);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] RpcEndGame failed: {e}");
                }
            }
        }

        private static void TickOwner() {
            if (swallowButton != null) swallowButton.MaxTimer = CooldownValue();
            if (!IsAlive(pelican) || InMeeting()) { currentTarget = null; return; }
            currentTarget = PlayerControlFixedUpdatePatch.setTarget();
            if (currentTarget != null)
                PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, Color);
        }

        // ====================================================================
        // Swallow / release / digest
        // ====================================================================

        // Finds the victim's DeadBody and hides it. SetActive(false), NOT Destroy - the body has to be
        // able to come back on the Pelican's corpse (Shade.cs:159-166 is the same mechanic).
        private static bool HideBodyOf(byte victimId) {
            try {
                foreach (var db in UnityEngine.Object.FindObjectsOfType<DeadBody>()) {
                    if (db == null || db.ParentId != victimId) continue;
                    db.gameObject.SetActive(false);
                    swallowedBodies[victimId] = db;
                    return true;
                }
            } catch { }
            return false;
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        [HarmonyPriority(Priority.Low)] // after TOR's own murder bookkeeping
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || pelican == null || target == null || __instance == null) return;

                    // The Pelican falls -> everything he carries reappears on his body.
                    if (target.PlayerId == pelican.PlayerId) { ReleaseAll(target.GetTruePosition()); return; }

                    if (__instance.PlayerId != pelican.PlayerId) return;

                    // Runs on every client from identical inputs, so the swallow list needs no RPC.
                    if (!swallowed.Contains(target.PlayerId)) {
                        swallowed.Add(target.PlayerId);
                        swallowedVersion++; // AUDIT-2026-08-16: invalidate the belly-name cache
                    }
                    if (!HideBodyOf(target.PlayerId)) {
                        pendingHide.Add(target.PlayerId);
                        pendingHideUntil = Time.time + 0.5f;
                    }

                    // The gulp is heard ONLY by the Pelican and by his victim. A world-anchored cue for
                    // everyone nearby would hand the crew exactly the evidence this role is built to
                    // deny them (contrast: the Werewolf's kill sound, which is meant to be a tell).
                    bool mine = IsLocalPelican()
                                || (PlayerControl.LocalPlayer != null
                                    && PlayerControl.LocalPlayer.PlayerId == target.PlayerId);
                    if (mine) UCAssets.PlayPelicanSwallowAt(target.GetTruePosition());

                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[Pelican] Swallowed {target.Data?.PlayerName} ({swallowed.Count} in the belly).");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] MurderPatch failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Exiled))]
        static class ExiledPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (!active || pelican == null || __instance == null) return;
                    if (__instance.PlayerId != pelican.PlayerId) return;
                    // In practice the belly is always empty here (MeetingHud.Start digests before any
                    // vote can be cast), but an exile is still a death of the Pelican and must not be
                    // the one path where bodies silently stay hidden.
                    ReleaseAll(__instance.GetTruePosition());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] ExiledPatch failed: {e}");
                }
            }
        }

        // The Pelican falls -> everyone still in the belly walks back out ALIVE, in a small ring around
        // his corpse (playtest decision 2026-07-26; before this they came back as bodies). Only the
        // undigested are in these lists at all: a meeting empties them for good, so anyone swallowed
        // before the last meeting stays dead. That keeps the counterplay sharp - kill him early and you
        // get your crew back, kill him late and you only get the evidence.
        //
        // No RPC: like the swallow list itself, every client runs this from identical synced inputs
        // (PlayerControl.MurderPlayer / Exiled fire everywhere), so each one revives the same people.
        // The pieces that are NOT purely local are handled by the one client that owns them - the host
        // marks the player info dirty so late state stays right, and the revived player's own client is
        // the only one allowed to move him.
        private static void ReleaseAll(Vector2 at) {
            if (swallowed.Count == 0 && swallowedBodies.Count == 0) return;

            var ids = new List<byte>(swallowed);
            int n = Mathf.Max(1, ids.Count);
            for (int i = 0; i < ids.Count; i++) {
                float a = (Mathf.PI * 2f) * i / n;
                float r = ids.Count == 1 ? 0f : 0.55f;
                RevivePlayer(ids[i], new Vector2(at.x + Mathf.Cos(a) * r, at.y + Mathf.Sin(a) * r));
            }

            // The hidden corpses go with them - a body left behind would be reportable evidence of a
            // player who is standing right there.
            foreach (var kvp in swallowedBodies) {
                try { if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value.gameObject); } catch { }
            }

            int released = ids.Count;
            swallowedBodies.Clear();
            swallowed.Clear();
            if (released > 0) swallowedVersion++; // AUDIT-2026-08-16: invalidate the belly-name cache
            pendingHide.Clear();
            PelicanHud.HideBelly();
            if (released > 0) {
                try { UCAssets.PlayPelicanReleaseAt(at); } catch { }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Pelican] Released {released} player(s) alive from the belly.");
            }
        }

        // TOR's GameHistory is an INTERNAL static class, so its death ledger is reached by reflection
        // (the same shape UCRoleDraft uses for TOR's internal draft data). The FieldInfo is cached but
        // the VALUE never is: clearGameHistory() assigns a brand new list every round, so a cached list
        // would be last round's. A failure here is survivable - the player is alive either way, the
        // meeting would just still print him as murdered.
        private static FieldInfo deadPlayersField;
        private static bool ledgerTried;

        private static List<DeadPlayer> DeadPlayersLedger() {
            if (!ledgerTried) {
                ledgerTried = true;
                try {
                    var t = AccessTools.TypeByName("TheOtherRoles.GameHistory");
                    deadPlayersField = t?.GetField("deadPlayers", BindingFlags.Public | BindingFlags.Static);
                    if (deadPlayersField == null)
                        UnknownsCollectionPlugin.Logger?.LogWarning(
                            "[Pelican] GameHistory.deadPlayers not found - revived players stay in TOR's death list.");
                } catch { }
            }
            try { return deadPlayersField?.GetValue(null) as List<DeadPlayer>; } catch { return null; }
        }

        // One victim back on his feet. PlayerControl.Revive() is the game's own path (TOR uses it for
        // PropHunt's "prop becomes hunter", CustomGameModes/PropHunt.cs:500), so animation state,
        // collider and visibility come back the way the game expects them to.
        private static void RevivePlayer(byte id, Vector2 at) {
            try {
                var p = Helpers.playerById(id);
                if (p == null || p.Data == null || p.Data.Disconnected || !p.Data.IsDead) return;

                p.Revive();
                p.Data.IsDead = false;

                // TOR's own death ledger drives the meeting/end-screen "died at" lines and several
                // roles' information - leaving the entry in would report a living player as murdered.
                try { DeadPlayersLedger()?.RemoveAll(d => d != null && d.player != null && d.player.PlayerId == id); }
                catch { }

                // Host owns GameData: mark the info dirty so the revived state also reaches anyone
                // whose client did not run this (a late joiner, a dropped message).
                if (AmHost()) { try { p.Data.MarkDirty(); } catch { } }

                // Movement is owner-authoritative - only his own client may put him back on the floor.
                if (p.AmOwner) {
                    try { p.NetTransform.RpcSnapTo(at); } catch { p.transform.position = at; }
                    try { Helpers.showFlash(Color, 1.5f, UCLocalization.Tr("uc.ui.pelican.freed_flash")); } catch { }
                } else {
                    p.transform.position = at;   // instant local correction until his next position update
                }

                UnknownsCollectionPlugin.Logger?.LogInfo($"[Pelican] {p.Data.PlayerName} came back out alive.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] revive of {id} failed: {e}");
            }
        }

        // While a player is in the belly he is DEAD to every system in the game (that is what makes the
        // role work), but the vitals station is where the crew reads "is he still with us" - and someone
        // who can still be freed is not gone yet. Same postfix shape as the Manipulator's vitals lie
        // (Manipulator.cs:293-317), including the alive background the vanilla SetAlive() forgets.
        [HarmonyPatch(typeof(VitalsMinigame), nameof(VitalsMinigame.Update))]
        static class BellyVitalsPatch {
            public static void Postfix(VitalsMinigame __instance) {
                try {
                    if (!active || swallowed.Count == 0) return;
                    if (__instance == null || __instance.vitals == null) return;
                    foreach (var panel in __instance.vitals) {
                        if (panel == null || !panel.IsDead) continue;
                        if (panel.PlayerInfo == null || panel.PlayerInfo.Disconnected) continue;
                        if (!swallowed.Contains(panel.PlayerInfo.PlayerId)) continue;
                        panel.SetAlive();
                        var prefab = __instance.PanelPrefab;
                        if (prefab != null && prefab.Background != null && panel.Background != null)
                            panel.Background.sprite = prefab.Background.sprite;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] belly vitals failed: {e}");
                }
            }
        }

        // Meeting = digestion. The hidden bodies are destroyed for good (the Shade does the same with
        // its own hidden bodies), so nothing can be reported after the meeting and a later death of
        // the Pelican releases nothing.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    if (!active) return;
                    if (swallowed.Count > 0 && IsLocalPelican()) {
                        // Local-only cue: a public digestion sound would announce "somebody was
                        // swallowed" to the whole meeting.
                        try { UCAssets.PlayPelicanDigestAt(PlayerControl.LocalPlayer.GetTruePosition()); } catch { }
                    }
                    foreach (var kvp in swallowedBodies)
                        if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value.gameObject);
                    if (swallowed.Count > 0) {
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[Pelican] Digested {swallowed.Count} victim(s).");
                        swallowedVersion++; // AUDIT-2026-08-16: invalidate the belly-name cache
                    }
                    swallowedBodies.Clear();
                    swallowed.Clear();
                    pendingHide.Clear();
                    PelicanHud.HideAll();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] MeetingStart failed: {e}");
                }
            }
        }

        // ====================================================================
        // Hunt restrictions (for EVERYONE)
        // ====================================================================

        // Postfix, not a competing prefix: TOR replaces Vent.CanUse wholesale with a prefix that
        // returns false, so only a postfix gets the final word on canUse/couldUse. TOR's Vent.Use
        // prefix asks CanUse first, so the block also reaches the actual vent attempt.
        [HarmonyPatch(typeof(Vent), nameof(Vent.CanUse))]
        static class VentBlockPatch {
            public static void Postfix(ref float __result,
                                       [HarmonyArgument(1)] ref bool canUse,
                                       [HarmonyArgument(2)] ref bool couldUse) {
                try {
                    if (!HuntRestrictionsActive()) return;
                    canUse = couldUse = false;
                    __result = float.MaxValue;
                } catch { }
            }
        }

        // No meetings during the hunt. CmdReportDeadBody is the one funnel BOTH the report button and
        // the emergency button pass through, so this single prefix covers them; returning false only
        // skips the ORIGINAL, never TOR's own prefix on the same method (PlayerControlPatch.cs:1129).
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdReportDeadBody))]
        static class ReportBlockPatch {
            public static bool Prefix() {
                try { if (HuntRestrictionsActive()) return false; } catch { }
                return true;
            }
        }

        // Cosmetic companion to the block above: the report button should not even feel clickable.
        [HarmonyPatch(typeof(ReportButton), nameof(ReportButton.DoClick))]
        static class ReportButtonPatch {
            public static bool Prefix() {
                try { if (HuntRestrictionsActive()) return false; } catch { }
                return true;
            }
        }

        // Emergency button: same shape TOR uses for the Swapper/Jester/Lawyer (UsablesPatch.cs:225-255).
        // Ours runs after TOR's postfix (TOR's plugin loads first), so it has the last word.
        [HarmonyPatch(typeof(EmergencyMinigame), nameof(EmergencyMinigame.Update))]
        static class EmergencyBlockPatch {
            public static void Postfix(EmergencyMinigame __instance) {
                try {
                    if (!HuntRestrictionsActive() || __instance == null) return;
                    __instance.StatusText.text = UCLocalization.Tr("uc.ui.pelican.hunt_no_meeting");
                    __instance.NumberText.text = string.Empty;
                    __instance.ClosedLid.gameObject.SetActive(true);
                    __instance.OpenLid.gameObject.SetActive(false);
                    __instance.ButtonActive = false;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Pelican] emergency block failed: {e.Message}");
                }
            }
        }

        private static bool SabotageBlocked() =>
            HuntRestrictionsActive() && (HuntBlocksSabotage == null || HuntBlocksSabotage.getBool());

        // Sabotage (option 1549) has THREE doors, and the greyed-out HUD button is only the first one.
        // 1) The button itself. TOR's own Janitor block uses exactly this Refresh postfix
        //    (UsablesPatch.cs:205-215), so it is re-disabled right after the game re-enables it.
        [HarmonyPatch(typeof(SabotageButton), nameof(SabotageButton.Refresh))]
        static class SabotageBlockPatch {
            public static void Postfix() {
                try {
                    if (!SabotageBlocked()) return;
                    FastDestroyableSingleton<HudManager>.Instance.SabotageButton.SetDisabled();
                } catch { }
            }
        }

        // 2) The MAP. Tab opens the sabotage overlay directly for an Impostor, which walks straight
        //    past the disabled button (playtest 2026-07-26). Downgrading the mode is TOR's own move for
        //    PropHunt (CustomGameModes/PropHunt.cs:611-616): the map still opens, just without the
        //    sabotage controls.
        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Show))]
        static class SabotageMapBlockPatch {
            public static void Prefix(ref MapOptions opts) {
                try {
                    if (!SabotageBlocked()) return;
                    if (opts.Mode == MapOptions.Modes.Sabotage) opts.Mode = MapOptions.Modes.Normal;
                } catch { }
            }
        }

        // 3) The call itself, for every route that never touches our UI at all: a map left open when
        //    the hunt starts, a hotkey, TOR's Jackal lights button (Buttons.cs:1078-1092). Every
        //    sabotage in the game funnels through SystemTypes.Sabotage; REPAIRS carry the sabotaged
        //    system's own type instead, so the crew can still fix what is already running.
        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.RpcUpdateSystem),
                      new[] { typeof(SystemTypes), typeof(byte) })]
        static class SabotageCallBlockPatch {
            public static bool Prefix(SystemTypes systemType) {
                try {
                    if (!SabotageBlocked() || systemType != SystemTypes.Sabotage) return true;
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Pelican] Sabotage blocked - the hunt is running.");
                    return false;
                } catch { return true; }
            }
        }

        // Custom ability buttons are deliberately NOT touched by the hunt (playtest 2026-07-26).
        // Holding every CustomButton's Timer just above zero did block click and hotkey, but it also
        // parked every cooldown in plain sight and stalled abilities that were already running - on
        // screen that reads as a stuck game, not as a rule. The hunt now restricts only what the 1-vs-1
        // actually needs: meetings, reports, vents and (optionally) sabotage.

        // ====================================================================
        // End game: the guard, the win, the screen
        // ====================================================================

        // See the file header. Priority.First so this prefix inspects the RAW reason, before Bug's or
        // Collector's own RpcEndGame prefixes can rewrite it.
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.RpcEndGame))]
        [HarmonyPriority(Priority.First)]
        static class EndGameGuardPatch {
            public static bool Prefix(ref GameOverReason endReason) {
                try {
                    if (!AmHost()) return true;
                    if (!active || !IsAlive(pelican) || huntEnded) return true;
                    int r = (int)endReason;
                    if (r == PelicanWinReason) return true;   // our own win always goes through

                    // Down to the two hunt participants: NOTHING but the hunt may decide this round.
                    // (Checked on the board, not on huntActive, so the Impostor win that fires the very
                    // frame the third player dies cannot beat the hunt-start broadcast to the punch.)
                    bool huntBoard = AliveCount() <= 2;
                    bool block = huntBoard ? IsTeamWin(endReason) : IsCrewNoKillerWin(endReason);
                    if (!block) return true;

                    if (Time.time >= nextGuardLog) {
                        nextGuardLog = Time.time + 5f;
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            $"[Pelican] Suppressed end reason {r} - a living Pelican is still on the ship.");
                    }
                    return false;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] end-game guard failed: {e}");
                    return true;
                }
            }

            // "The crew wins because no killer is left" - which is exactly the claim a living Pelican
            // disproves. A TASK win (HumansByTask) is deliberately NOT in here: the crew earned that
            // one and the Pelican simply loses.
            private static bool IsCrewNoKillerWin(GameOverReason r) =>
                r == GameOverReason.HumansByVote || r == GameOverReason.HumansDisconnect;

            private static bool IsTeamWin(GameOverReason r) {
                switch (r) {
                    case GameOverReason.HumansByVote:
                    case GameOverReason.HumansByTask:
                    case GameOverReason.HumansDisconnect:
                    case GameOverReason.ImpostorByVote:
                    case GameOverReason.ImpostorByKill:
                    case GameOverReason.ImpostorBySabotage:
                    case GameOverReason.ImpostorDisconnect:
                        return true;
                    default:
                        return (int)r == TeamJackalWinReason;
                }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        static class OnGameEndPatch {
            // Snapshot BEFORE TOR's own reset can wipe the role statics (Bug/Collector precedent).
            public static void Prefix() {
                if (active && pelicanPlayerId != byte.MaxValue) winnerPelicanId = pelicanPlayerId;
            }

            public static void Postfix() {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != PelicanWinReason) return;
                    if (winnerPelicanId == byte.MaxValue) return;
                    PlayerControl winner = Helpers.playerById(winnerPelicanId);
                    if (winner == null || winner.Data == null) return;

                    EndGameResult.CachedWinners.Clear();
                    EndGameResult.CachedWinners.Add(new CachedPlayerData(winner.Data));
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Pelican] The Pelican wins alone!");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] OnGameEnd failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        [HarmonyPriority(Priority.Last)]
        static class EndGameFxPatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != PelicanWinReason) return;
                    if (__instance.WinText != null) {
                        GameObject bonus = UnityEngine.Object.Instantiate(__instance.WinText.gameObject);
                        bonus.transform.position = new Vector3(__instance.WinText.transform.position.x,
                            __instance.WinText.transform.position.y - 0.5f,
                            __instance.WinText.transform.position.z);
                        bonus.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                        var text = bonus.GetComponent<TMP_Text>();
                        text.text = UCLocalization.Tr("uc.ui.pelican.win_banner");
                        text.color = Color;
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] end-screen FX failed: {e}");
                }
            }
        }

        // ====================================================================
        // Task accounting: a neutral's tasks never count toward the crew total
        // ====================================================================
        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || pelican == null || pelican.Data == null) return;
                    if (HasTasks?.getBool() ?? false) return;
                    var (done, total) = TasksHandler.taskInfo(pelican.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= done;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] TaskPatch failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || pelican == null || p == null || p != pelican || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = PelicanInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, PelicanInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Pelican] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
