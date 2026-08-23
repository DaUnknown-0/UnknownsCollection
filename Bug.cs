// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using TheOtherRoles;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Bug {
        public static readonly Color Color = new Color(0.20f, 1f, 0.35f);

        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;

        public static PlayerControl bug;
        public static bool active;
        public static byte bugPlayerId = byte.MaxValue;

        private const byte RpcId = 198;
        private const byte SubSetBug = 0;

        private static RoleInfo bugInfo;
        public static RoleInfo BugInfo() => bugInfo ??= new RoleInfo(
            "Bug", Color, "Survive until the end to win alone",
            "Survive until the end to win alone", RoleId.Crewmate)
        { isNeutral = true };

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1480, Types.Neutral, "Bug",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1481, Types.Neutral, "Bug Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] CreateOptions failed: {e}");
            }
        }

        // The Bug win is handled entirely by attribute-based patches (RpcEndGameHijackPatch +
        // OnGameEndPatch), picked up by PatchAll — no reflection needed here.
        public static void TryPatch(Harmony harmony) {
            // Receiver registration for the shared UC channel (UCRpc.CallId = 230). Every module
            // registers here even when it has no Harmony work left to do - TryPatch is the single
            // place UnknownsCollectionPlugin.Load() calls for every module.
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        // Legacy Bug-win reason: kept as a fallback (old hosts / unexpected paths). New hosts encode the
        // ORIGINAL game-over reason into the value instead (BugHijackBase + reason, see the hijack patch),
        // so clients can first show the stolen team win before the Bug takes the screen over. Encoded in
        // the reason itself because Hazel-Reliable RPCs have no ordering guarantee - a separate
        // "original win" RPC could arrive after the game end, or never.
        private const int BugWinReason = 18;
        private const int BugHijackBase = 20; // occupied values: 20-26 (vanilla team wins 0-6) and 31 (TeamJackal 11)

        // "Stolen win" end-screen dramaturgy: the screen shows the ORIGINAL team win for TakeoverDelay
        // seconds, then the Bug hijacks it (glitch burst, win-text morph, podium swap). Fixed constants
        // by design, not config.
        private const float TakeoverDelay = 3.0f;
        private const float MorphDuration = 1.2f;

        private static bool IsBugReason(int r) =>
            r == BugWinReason || (r >= BugHijackBase && r <= BugHijackBase + TeamJackalWinReason);
        private static int OriginalReason(int r) =>
            (r >= BugHijackBase && r <= BugHijackBase + TeamJackalWinReason) ? r - BugHijackBase : -1;

        // The Bug's PlayerId, snapshotted at game-end BEFORE TOR's resetVariables wipes bugPlayerId.
        // Deliberately NOT part of resetVariables: TOR's own end-of-game reset would clear it before our
        // Priority.Last postfix could read it. Re-snapshotted every game-end, so a stale value is
        // harmless (the postfix also gates on IsBugReason(gameOverReason)).
        private static byte winnerBugId = byte.MaxValue;

        // Game-end snapshots for the two-phase end screen, taken in OnGameEndPatch.Prefix while the role
        // statics and PlayerControls are still alive (the end scene has neither). Same lifetime rules as
        // winnerBugId: re-stamped every game end, never cleared in resetVariables.
        private static int originalReason = -1;                          // -1 = legacy 18 / not a bug win
        private static List<CachedPlayerData> originalWinners;           // display list of the stolen win
        private static CachedPlayerData bugWinnerData;                   // outfit/name for the podium swap

        // TeamJackalWin from TOR's CustomGameOverReason enum (EndGamePatch.cs). The Jackal is a "team"
        // win the Bug should also hijack; the other custom reasons (Lovers 10, Mini 12, Jester 13,
        // Arsonist 14, Vulture 15, Prosecutor 16) are neutral solo wins the Bug must NOT steal.
        private const int TeamJackalWinReason = 11;

        // Host-authoritative Bug win ("survive to the end -> win alone"): when a TEAM win is about to be
        // broadcast and the Bug is still alive, rewrite the reason to BugWinReason in-place. This reuses
        // the single RpcEndGame the original caller already makes — no second broadcast, no per-frame
        // instant win. Only the three team wins qualify: vanilla Crew/Impostor (reason < 10) and the
        // Jackal team (11). Neutral solo wins (Jester, Arsonist, Vulture, Lovers, Prosecutor, Mini) are
        // left untouched, so the Bug never steals those.
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.RpcEndGame))]
        [HarmonyPriority(Priority.Low)] // behind Collector's Priority.High; the real rule is the
                                        // explicit stand-down below, this is only reinforcement.
        static class RpcEndGameHijackPatch {
            public static void Prefix(ref GameOverReason endReason) {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!BugIsAliveAndActive()) return;
                    // PRECEDENCE: a full, living Collector in Survive-To-End
                    // mode beats the Bug - its win contains the Bug's (survive to the end) and adds
                    // every relic on top. Checked EXPLICITLY rather than left to Harmony patch order:
                    // previously whichever prefix ran first won, and the loser only backed off as a
                    // side effect of the "don't steal neutral solo wins" guard below, which is not a
                    // rule anybody could read as one (AUDIT-2026-08-11.md, M-3).
                    if (Collector.WouldHijackTeamWin()) {
                        UnknownsCollectionPlugin.Logger?.LogInfo(
                            "[Bug] Standing down - a full Collector survived and takes the win (documented precedence).");
                        return;
                    }
                    int r = (int)endReason;
                    if (r >= 10 && r != TeamJackalWinReason) return; // only Crew/Impostor (<10) or Jackal (11)
                    // Encode the stolen reason instead of the flat legacy 18, so every client can
                    // reconstruct (and first display) the original team win. 18 stays as receive-fallback.
                    endReason = (GameOverReason)(BugHijackBase + r);
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[Bug] Bug survived to the end — hijacking win (stolen reason {r}).");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] RpcEndGame hijack failed: {e}");
                }
            }
        }

        private static bool BugIsAliveAndActive() =>
            active && bug != null && bug.Data != null && !bug.Data.IsDead && !bug.Data.Disconnected;

        private static FieldInfo winConditionField;
        private static TMPro.TMP_Text bonusText;
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
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] SetWinCondition failed: {e}");
            }
        }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalBug() =>
            bug != null && PlayerControl.LocalPlayer != null && bug.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = UCRpc.Begin(RpcId); // shared UC channel; RpcId is the module byte
            w.Write(subtype);
            return w;
        }

        public static void SendSetBug(byte id) {
            try {
                var w = BeginRpc(SubSetBug);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetBug(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Bug] SendSetBug failed: {e}"); }
        }

        private static void ApplySetBug(byte id) {
            bug = Helpers.playerById(id);
            active = bug != null;
            bugPlayerId = active ? id : byte.MaxValue;
            if (active) UCPromotion.Claim(id);
            if (active) UnknownsCollectionPlugin.Logger?.LogInfo($"[Bug] The Bug is {bug.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySetBug(playerId);

        // RPC receiver, registered on the shared UC channel in TryPatch. UCRpc's dispatcher
        // already consumed the module byte, so this starts at the subtype byte - the wire
        // format behind the module byte is byte-for-byte what the old per-callId RPC used.
        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                if (subtype == SubSetBug) {
                    byte id = reader.ReadByte();
                    // Host-authoritative role assignment (host pick in IntroCutscene.OnDestroy / UCRoleDraft) - a
                    // forged one would let any client declare any player this role (AUDIT H-3).
                    if (UCRpc.RequireHost("Bug.SetBug")) ApplySetBug(id);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Bug] HandleRpc failed: {e}");
            }
        }

        // PlayerId-keyed state is cleared on OnGameJoined as well as on resetVariables
        // (AUDIT M-12). PlayerIds are handed out per LOBBY, and resetVariables only ever
        // arrives from a host that has this mod - so joining a vanilla host, or leaving a
        // lobby abnormally, used to carry the previous game's ids into the next one and let
        // them act on whoever happens to reuse them. Same belt-and-suspenders rule the
        // Silencer and the Shade already followed; the body is shared so the two entry
        // points can never drift apart.
        private static void ClearState() {
            bug = null;
            active = false;
            bugPlayerId = byte.MaxValue;
            // NOTE: winnerBugId is intentionally NOT reset here (see its declaration).
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Bug", ClearState);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() => UCResetGuard.Run("Bug", ClearState);
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
                    SendSetBug(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] IntroEnd pick failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() {
                try {
                    if (!active || bug == null || !IsAlive(bug)) return;
                    // Deliberately NOT gated on IsLocalBug(): this is a bewusster Meta-Tell that a third
                    // party exists and is still alive, audible to everyone (see SPEC.md decision 2).
                    UCAssets.PlayBugGlitch();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] meeting sound failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        static class OnGameEndPatch {
            // Runs before TOR's OnGameEnd postfix calls resetVariables(): snapshot the Bug's id so the
            // postfix below can still award the win after bugPlayerId has been reset. Fires on every
            // client (OnGameEnd runs everywhere), so all clients agree on the winner.
            // Priority.Last also puts this prefix AFTER TOR's (gameOverReason is already stamped) and
            // after Copycat's (WinnerCopycatId is already decided).
            public static void Prefix() {
                // Always reassign, never only on success: without the else branch a stale winnerBugId
                // from an earlier round survives into a round the Bug is not in, and the postfix below
                // would award a win to a player who no longer holds the role (Copycat.cs does it this way).
                winnerBugId = (active && bugPlayerId != byte.MaxValue) ? bugPlayerId : byte.MaxValue;
                try {
                    int reason = (int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason;
                    originalReason = OriginalReason(reason);
                    originalWinners = null;
                    bugWinnerData = null;
                    if (!IsBugReason(reason) || winnerBugId == byte.MaxValue) return;

                    // Snapshot everything the end scene will need - PlayerControls and role statics are
                    // both gone once the end-game scene has loaded.
                    PlayerControl bugPlayer = Helpers.playerById(winnerBugId);
                    if (bugPlayer != null && bugPlayer.Data != null)
                        bugWinnerData = new CachedPlayerData(bugPlayer.Data);
                    if (originalReason >= 0)
                        originalWinners = BuildOriginalWinners(originalReason);
                } catch (Exception e) {
                    originalWinners = null;
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] game-end snapshot failed: {e}");
                }
            }

            // Purely cosmetic reconstruction of "who would have won" for the phase-A display - no win
            // logic is re-derived, the winner of the game stays the Bug either way.
            private static List<CachedPlayerData> BuildOriginalWinners(int reason) {
                var list = new List<CachedPlayerData>();
                if (reason == TeamJackalWinReason) {
                    // Mirror of TOR's teamJackalWin branch: jackal + sidekick + former jackals,
                    // IsImpostor forced off so vanilla renders them as a non-impostor podium.
                    void AddJackal(PlayerControl p) {
                        if (p == null || p.Data == null) return;
                        var d = new CachedPlayerData(p.Data);
                        d.IsImpostor = false;
                        list.Add(d);
                    }
                    AddJackal(Jackal.jackal);
                    AddJackal(Sidekick.sidekick);
                    if (Jackal.formerJackals != null)
                        foreach (PlayerControl p in Jackal.formerJackals) AddJackal(p);
                } else {
                    // Vanilla's own classifier decides crew vs impostor (covers the disconnect reasons
                    // 5/6 without hardcoding their semantics here).
                    bool humansWon = GameManager.Instance != null &&
                                     GameManager.Instance.DidHumansWin((GameOverReason)reason);
                    foreach (PlayerControl p in PlayerControl.AllPlayerControls) {
                        if (p == null || p.Data == null || p.Data.Role == null) continue;
                        if (humansWon) {
                            if (p.Data.Role.IsImpostor) continue;
                            // Exclude neutrals (TOR and UC alike) via their RoleInfo flag - the same
                            // players TOR strips from a real crew-win podium.
                            bool neutral = false;
                            var infos = RoleInfo.getRoleInfoForPlayer(p);
                            if (infos != null)
                                foreach (var ri in infos)
                                    if (ri != null && ri.isNeutral) { neutral = true; break; }
                            if (neutral) continue;
                            list.Add(new CachedPlayerData(p.Data));
                        } else if (p.Data.Role.IsImpostor) {
                            list.Add(new CachedPlayerData(p.Data));
                        }
                    }
                }
                // A Copycat that earned her shared win would have stood on that podium too.
                if (Copycat.WinnerCopycatId != byte.MaxValue) {
                    PlayerControl cp = Helpers.playerById(Copycat.WinnerCopycatId);
                    if (cp != null && cp.Data != null && !list.Any(w => UCWinners.IsSameWinner(w, cp.Data)))
                        list.Add(new CachedPlayerData(cp.Data));
                }
                return list;
            }

            // Runs AFTER TOR's postfix (Priority.Last), so our winner list has the final say. Keys on
            // the host-broadcast BugWinReason, which every client sees via TOR's OnGameEndPatch.Prefix.
            public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ref EndGameResult endGameResult) {
                try {
                    if (!IsBugReason((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason)) return;
                    if (winnerBugId == byte.MaxValue) return;

                    bool twoPhase = originalReason >= 0 && originalWinners != null &&
                                    originalWinners.Count > 0 && bugWinnerData != null;
                    if (!twoPhase) {
                        // Legacy 18 (or snapshot failed): previous behaviour, the Bug alone on the podium.
                        originalReason = -1;
                        PlayerControl bugPlayer = Helpers.playerById(winnerBugId);
                        if (bugWinnerData == null && bugPlayer != null && bugPlayer.Data != null)
                            bugWinnerData = new CachedPlayerData(bugPlayer.Data);
                        if (bugWinnerData == null) return; // leave vanilla winners untouched (old behaviour)
                    }

                    EndGameResult.CachedWinners.Clear();
                    if (twoPhase) {
                        // Two-phase screen: vanilla builds the podium from CachedWinners, so filling it
                        // with the ORIGINAL winners makes phase A automatically authentic. The Bug only
                        // lives in bugWinnerData until the takeover swaps the podium.
                        foreach (var w in originalWinners) EndGameResult.CachedWinners.Add(w);
                    } else {
                        EndGameResult.CachedWinners.Add(bugWinnerData);
                    }
                    // 12 is intentionally outside TOR's WinCondition enum (0-10): no vanilla end-screen
                    // branch matches it, and the Bug draws its own green "Bug Wins" banner in EndGameFxPatch.
                    SetWinCondition(12);
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Bug wins alone! (survived to the end)");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] OnGameEnd failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        [HarmonyPriority(Priority.Last)]
        static class EndGameFxPatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    if (!IsBugReason((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason)) return;

                    bool twoPhase = originalReason >= 0 && bugWinnerData != null;
                    if (!twoPhase) {
                        // Legacy 18: previous single-phase behaviour, Bug identity from second 0.
                        CreateBugBanner(__instance);
                        if (__instance.BackgroundBar != null && UnknownsCollectionPlugin.BugGlitchEnabled.Value)
                            __instance.BackgroundBar.material.SetColor("_Color", Color);
                        if (UnknownsCollectionPlugin.BugGlitchEnabled.Value) {
                            var fx = __instance.gameObject.AddComponent<BugGlitchEffect>();
                            fx.mgr = __instance;
                            UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Glitch effect attached to end screen.");
                        }
                        return;
                    }

                    // ---- Phase A: the perfect stolen team win. ----
                    // Vanilla already shows Victory/Defeat + podium built from our ORIGINAL winner list,
                    // and TOR rebuilt the podium beans with names/roles - all untouched. The only missing
                    // piece of a genuine TOR team win is TOR's bonus line, which stays empty for our
                    // out-of-enum WinCondition(12) - so fake exactly that line (TOR's literal strings,
                    // deliberately not localized: TOR's originals aren't either).
                    CreateFakeTeamLine(__instance);
                    var timer = __instance.gameObject.AddComponent<BugTakeoverTimer>();
                    timer.mgr = __instance;
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[Bug] Phase A (stolen reason {originalReason}) shown, takeover in {TakeoverDelay}s.");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] EndGameFx failed: {e}");
                }
            }
        }

        // ---- Two-phase end-screen helpers ----

        private static GameObject fakeLineGo;    // phase-A fake TOR bonus line, destroyed at takeover
        private static Vector3 podiumBeanScale = new Vector3(0.9f, 0.9f, 1f);

        // Where the banner sits: directly under the win text, measured against what that text ACTUALLY
        // renders. TOR's own bonus line uses a flat -0.5 (EndGamePatch.cs:288), which is fine for a line
        // created together with a freshly laid-out "Victory" - but our takeover banner is born three
        // seconds later, on top of a win text that the glitch effect is already scrambling into longer
        // strings, and a fixed offset dropped "Bug Wins" straight into the middle of it (playtest
        // 2026-07-26). Taking the drop from the rendered half-height holds at any font size or
        // resolution; the flat 0.62 is only the fallback for a text that has not been meshed yet.
        private static Vector3 BannerPos(EndGameManager mgr) {
            var win = mgr.WinText.transform;
            float drop = 0.62f;
            try {
                var r = mgr.WinText.GetComponent<Renderer>();
                if (r != null && r.bounds.extents.y > 0.01f) drop = r.bounds.extents.y + 0.3f;
            } catch { }
            return new Vector3(win.position.x, win.position.y - drop, win.position.z);
        }

        // The green "Bug Wins" banner (previously created inline in EndGameFxPatch, unchanged look).
        private static void CreateBugBanner(EndGameManager mgr) {
            if (mgr == null || mgr.WinText == null) return;
            GameObject bonus = UnityEngine.Object.Instantiate(mgr.WinText.gameObject);
            bonus.transform.position = BannerPos(mgr);
            bonusText = bonus.GetComponent<TMP_Text>();
            bonusText.text = UCLocalization.Tr("uc.ui.bug.win_banner");
            bool glitchOn = UnknownsCollectionPlugin.BugGlitchEnabled.Value;
            // With the glitch effect running, start tiny/invisible so BugGlitchEffect.Update()
            // can ease it in ("materializing out of the noise"); without it (accessibility
            // toggle off), keep the instant full-size behaviour unchanged.
            bonus.transform.localScale = glitchOn ? new Vector3(0.05f, 0.05f, 1f) : new Vector3(0.7f, 0.7f, 1f);
            bonusText.color = glitchOn ? new Color(Color.r, Color.g, Color.b, 0f) : Color;
        }

        // Phase-A fake of TOR's bonus line - the exact strings/colors EndGameManagerSetUpPatch would
        // have shown for the stolen reason.
        private static void CreateFakeTeamLine(EndGameManager mgr) {
            if (mgr == null || mgr.WinText == null) return;
            string txt;
            Color col;
            switch ((GameOverReason)originalReason) {
                case GameOverReason.ImpostorByKill:     txt = "Impostors Win - By Kill";              col = Color.red;   break;
                case GameOverReason.ImpostorBySabotage: txt = "Impostors Win - By Sabotage";          col = Color.red;   break;
                case GameOverReason.ImpostorByVote:     txt = "Impostors Win - By Vote, Guess or DC"; col = Color.red;   break;
                case GameOverReason.ImpostorDisconnect: txt = "Last Crewmate Disconnected";           col = Color.red;   break;
                case GameOverReason.HumansByTask:       txt = "Crew Wins - Taskwin";                  col = Color.white; break;
                case GameOverReason.HumansByVote:
                case GameOverReason.HumansDisconnect:   txt = "Crew Wins - No Evil Killers Left";     col = Color.white; break;
                default:
                    if (originalReason == TeamJackalWinReason) { txt = "Team Jackal Wins"; col = Jackal.color; }
                    else { txt = ""; col = Color.white; }
                    break;
            }
            if (txt.Length == 0) return;
            GameObject fake = UnityEngine.Object.Instantiate(mgr.WinText.gameObject);
            fake.transform.position = new Vector3(mgr.WinText.transform.position.x,
                mgr.WinText.transform.position.y - 0.5f, mgr.WinText.transform.position.z);
            fake.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
            var t = fake.GetComponent<TMP_Text>();
            t.text = txt;
            t.color = col;
            fakeLineGo = fake;
        }

        // The true outcome the win text morphs into at takeover: only the Bug keeps "Victory".
        private static string TrueOutcomeText() {
            bool isYou = bugWinnerData != null && bugWinnerData.IsYou;
            try {
                return DestroyableSingleton<TranslationController>.Instance.GetString(
                    isYou ? StringNames.Victory : StringNames.Defeat);
            } catch {
                return isYou ? "Victory" : "Defeat";
            }
        }

        // Replace the original winners' podium with a single Bug bean (TOR's own podium idiom, i=0 slot).
        // Returns the bean at scale 0 - the caller pops or snaps it to podiumBeanScale.
        private static PoolablePlayer SwapPodiumToBug(EndGameManager mgr) {
            foreach (PoolablePlayer pb in mgr.transform.GetComponentsInChildren<PoolablePlayer>())
                UnityEngine.Object.Destroy(pb.gameObject);
            if (bugWinnerData == null || mgr.PlayerPrefab == null) return null;

            int num = Mathf.CeilToInt(7.5f);
            PoolablePlayer bean = UnityEngine.Object.Instantiate<PoolablePlayer>(mgr.PlayerPrefab, mgr.transform);
            bean.transform.localPosition = new Vector3(0f, FloatRange.SpreadToEdges(-1.125f, 0f, 0, num), -8f) * 0.9f;
            bean.transform.localScale = Vector3.zero;
            bean.SetFlipX(true);
            bean.UpdateFromPlayerOutfit(bugWinnerData.Outfit, PlayerMaterial.MaskType.None, bugWinnerData.IsDead, true);
            if (bean.cosmetics != null && bean.cosmetics.nameText != null) {
                bean.cosmetics.nameText.color = Color.white;
                bean.cosmetics.nameText.transform.localScale =
                    new Vector3(1f / podiumBeanScale.x, 1f / podiumBeanScale.y, 1f);
                bean.cosmetics.nameText.transform.localPosition = new Vector3(
                    bean.cosmetics.nameText.transform.localPosition.x,
                    bean.cosmetics.nameText.transform.localPosition.y, -15f);
                bean.cosmetics.nameText.text = bugWinnerData.PlayerName + $"\n{Helpers.cs(Color, BugInfo().name)}";
            }
            return bean;
        }

        private static void DoTakeover(EndGameManager mgr) {
            if (mgr == null) return;
            if (fakeLineGo != null) { UnityEngine.Object.Destroy(fakeLineGo); fakeLineGo = null; }

            PoolablePlayer bean = SwapPodiumToBug(mgr);
            CreateBugBanner(mgr);
            if (mgr.BackgroundBar != null)
                mgr.BackgroundBar.material.SetColor("_Color", Color);

            string trueText = TrueOutcomeText();
            if (UnknownsCollectionPlugin.BugGlitchEnabled.Value) {
                var fx = mgr.gameObject.AddComponent<BugGlitchEffect>();
                fx.mgr = mgr;
                fx.morphTarget = trueText;   // scramble-morph fake Victory/Defeat -> true outcome
                fx.podiumBean = bean;        // scale-pop hidden inside the first glitch burst
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Takeover: glitch effect attached.");
            } else {
                // Accessibility toggle off: hard cut, same two-phase dramaturgy without the effect.
                if (bean != null) bean.transform.localScale = podiumBeanScale;
                if (mgr.WinText != null) {
                    mgr.WinText.text = trueText;
                    mgr.WinText.color = (bugWinnerData != null && bugWinnerData.IsYou) ? Color : Palette.ImpostorRed;
                }
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] Takeover: hard cut (glitch disabled).");
            }
        }

        private class BugTakeoverTimer : MonoBehaviour {
            static BugTakeoverTimer() => ClassInjector.RegisterTypeInIl2Cpp<BugTakeoverTimer>();
            public EndGameManager mgr;
            private float created = -1f;

            private void Start() => created = Time.time;

            private void Update() {
                if (created < 0f || Time.time - created < TakeoverDelay) return;
                try { DoTakeover(mgr); }
                catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Bug] takeover failed: {e}"); }
                Destroy(this);
            }
        }

        private class BugGlitchEffect : MonoBehaviour {
            static BugGlitchEffect() => ClassInjector.RegisterTypeInIl2Cpp<BugGlitchEffect>();
            public EndGameManager mgr;
            public string morphTarget;       // set by DoTakeover: morph baseWinStr -> this, then behave as before
            public PoolablePlayer podiumBean; // set by DoTakeover: scale-pop 0 -> podiumBeanScale
            private int[] revealRank;         // stable, once-shuffled reveal order (letters lock in, no flicker)
            private float nextPulse;
            private string baseWinStr;
            private Vector3 baseWinPos;
            private float startTime;

            private RawImage glitchOverlay;
            private Texture2D glitchTex;
            private float glitchEndTime;

            private void Start() {
                startTime = Time.time;
                nextPulse = Time.time + UnityEngine.Random.Range(0.1f, 0.5f);
                if (mgr != null && mgr.WinText != null) {
                    baseWinStr = mgr.WinText.text;
                    baseWinPos = mgr.WinText.transform.localPosition;
                }
                if (morphTarget != null) {
                    // Fisher-Yates over the TARGET's indices, rolled exactly once: each position gets a
                    // stable reveal rank, so characters snap in one by one instead of flickering.
                    int n = morphTarget.Length;
                    var order = new int[n];
                    for (int i = 0; i < n; i++) order[i] = i;
                    for (int i = n - 1; i > 0; i--) {
                        int j = UnityEngine.Random.Range(0, i + 1);
                        (order[i], order[j]) = (order[j], order[i]);
                    }
                    revealRank = new int[n];
                    for (int pos = 0; pos < n; pos++) revealRank[order[pos]] = pos;
                }
                CreateGlitchOverlay();
                // "Power-on" stinger for the corrupted-system moment - the same clip that also plays
                // (quieter) on every subsequent block-glitch pulse, see TriggerBlockGlitch().
                UCAssets.PlayBugGlitch(UCAssets.VolStd);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Bug] BugGlitchEffect started!");
            }

            private void CreateGlitchOverlay() {
                try {
                    if (mgr == null) return;
                    var go = new GameObject("BugGlitchCanvas");
                    go.transform.SetParent(mgr.transform, false);
                    var canvas = go.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 999;

                    var imgGo = new GameObject("BugGlitchImg");
                    imgGo.transform.SetParent(go.transform, false);
                    var rt = imgGo.AddComponent<RectTransform>();
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;

                    glitchOverlay = imgGo.AddComponent<RawImage>();

                    int cols = 128, rows = 72;
                    glitchTex = new Texture2D(cols, rows, TextureFormat.RGBA32, false);
                    glitchTex.filterMode = FilterMode.Point;
                    glitchTex.wrapMode = TextureWrapMode.Clamp;
                    ClearGlitchTex();
                    glitchOverlay.texture = glitchTex;
                    glitchOverlay.color = new Color(1f, 1f, 1f, 1f);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] CreateGlitchOverlay failed: {e}");
                }
            }

            private void ClearGlitchTex() {
                if (glitchTex == null) return;
                var cols = glitchTex.width;
                var rows = glitchTex.height;
                var pixels = new Color32[cols * rows];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(0, 0, 0, 0);
                glitchTex.SetPixels32(pixels);
                glitchTex.Apply();
            }

            private void TriggerBlockGlitch() {
                if (glitchTex == null) return;
                int cols = glitchTex.width;
                int rows = glitchTex.height;

                var pixels = new Color32[cols * rows];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(0, 0, 0, 0);

                int blockY = UnityEngine.Random.Range(0, rows - 8);
                int blockH = UnityEngine.Random.Range(4, 12);
                int offset = UnityEngine.Random.Range(-20, 21);
                glitchEndTime = Time.time + UnityEngine.Random.Range(0.08f, 0.2f);

                byte gray = (byte)UnityEngine.Random.Range(20, 60);
                for (int r = blockY; r < blockY + blockH && r < rows; r++) {
                    for (int c = 0; c < cols; c++) {
                        int dst = r * cols + ((c + offset + cols) % cols);
                        pixels[dst] = new Color32(gray, gray, gray, 180);
                    }
                }

                // Classic chroma-glitch fringe: faint red/cyan copies of the same band, offset a couple
                // pixels either side. Only painted into still-empty pixels so the fringe reads as a
                // colour split around the grey band instead of a flat tri-colour stripe.
                const int split = 2;
                const byte fringeAlpha = 110;
                for (int r = blockY; r < blockY + blockH && r < rows; r++) {
                    for (int c = 0; c < cols; c++) {
                        int baseCol = (c + offset + cols) % cols;

                        int redIdx = r * cols + ((baseCol - split + cols) % cols);
                        if (pixels[redIdx].a == 0) pixels[redIdx] = new Color32(220, 40, 40, fringeAlpha);

                        int cyanIdx = r * cols + ((baseCol + split) % cols);
                        if (pixels[cyanIdx].a == 0) pixels[cyanIdx] = new Color32(40, 220, 220, fringeAlpha);
                    }
                }

                glitchTex.SetPixels32(pixels);
                glitchTex.Apply();

                // Quiet layer of the same clip synced to the visual block-glitch pulse, so the "system
                // fault" moment reads in both image and sound.
                UCAssets.PlayBugGlitch(0.18f);
            }

            private static string ColorToHex(Color c) =>
                $"{(byte)(c.r * 255):X2}{(byte)(c.g * 255):X2}{(byte)(c.b * 255):X2}";

            private void Update() {
                try {
                    if (mgr == null) return;
                    float t = Time.time;

                    // After ~3-5s let the glitch settle into a calmer "system stabilizing" state:
                    // pulses space out and the scramble bins below shift toward the readable/tinted-only
                    // end, without ever fully switching the glitch off (keeps the identity, but lets
                    // players actually read/screenshot the win text).
                    float elapsed = t - startTime;
                    float decay = elapsed < 3f ? 1f : Mathf.Lerp(1f, 0.22f, Mathf.Clamp01((elapsed - 3f) / 2f));

                    if (mgr.BackgroundBar != null) {
                        float hue = Mathf.PingPong(t * 0.4f, 1f);
                        mgr.BackgroundBar.material.SetColor("_Color",
                            Color.HSVToRGB(hue, 0.7f, 1f));
                    }

                    // Takeover extras (both no-ops in the legacy single-phase path):
                    // podium bean pops 0 -> full over 0.25s, hidden inside the opening glitch burst.
                    if (podiumBean != null) {
                        float pp = Mathf.Clamp01(elapsed / 0.25f);
                        float ease = 1f - (1f - pp) * (1f - pp);
                        podiumBean.transform.localScale = podiumBeanScale * ease;
                        if (pp >= 1f) podiumBean = null;
                    }

                    // Scramble-morph: over MorphDuration a growing share of characters locks onto the
                    // true outcome text (stable reveal order), the rest keeps glitching. Afterwards the
                    // target IS the base string and the pulse logic below owns the text again.
                    if (morphTarget != null && mgr.WinText != null) {
                        mgr.WinText.richText = true;
                        float mp = elapsed / MorphDuration;
                        if (mp >= 1f) {
                            baseWinStr = morphTarget;
                            morphTarget = null;
                            mgr.WinText.text = baseWinStr;
                        } else {
                            int reveal = Mathf.FloorToInt(mp * morphTarget.Length);
                            string morphed = "";
                            for (int i = 0; i < morphTarget.Length; i++) {
                                if (revealRank != null && revealRank[i] < reveal) {
                                    morphed += morphTarget[i];
                                } else {
                                    char c = (char)UnityEngine.Random.Range(33, 127);
                                    Color rc = new Color(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value);
                                    morphed += $"<color=#{ColorToHex(rc)}>{c}</color>";
                                }
                            }
                            mgr.WinText.text = morphed;
                            mgr.WinText.transform.localPosition = baseWinPos;
                        }
                    }

                    if (t > nextPulse) {
                        nextPulse = t + UnityEngine.Random.Range(0.2f, 0.5f) / decay;
                        int r = UnityEngine.Random.Range(0, 6);
                        if (UnityEngine.Random.value > decay) r = Mathf.Max(r, 4); // calm phase: bias toward the tinted-only bin
                        if (r < 2) TriggerBlockGlitch();

                        // While the takeover morph runs it owns the win text; pulses keep driving the
                        // block glitches above, but must not overwrite the morph string.
                        if (mgr.WinText != null && morphTarget == null) {
                            mgr.WinText.richText = true;
                            int len = baseWinStr?.Length ?? 10;
                            if (r < 3) {
                                string glitched = "";
                                for (int i = 0; i < len; i++) {
                                    char c = (char)UnityEngine.Random.Range(33, 127);
                                    Color rc = new Color(
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value);
                                    glitched += $"<color=#{ColorToHex(rc)}>{c}</color>";
                                }
                                mgr.WinText.text = glitched;
                            } else if (r < 4) {
                                char[] chars = baseWinStr?.ToCharArray() ?? new char[0];
                                string mixed = "";
                                for (int i = 0; i < chars.Length; i++) {
                                    char c = UnityEngine.Random.value < 0.5f
                                        ? (char)UnityEngine.Random.Range(33, 127) : chars[i];
                                    Color rc = new Color(
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value);
                                    mixed += $"<color=#{ColorToHex(rc)}>{c}</color>";
                                }
                                mgr.WinText.text = mixed;
                            } else {
                                string colored = "";
                                foreach (char c in baseWinStr) {
                                    Color rc = new Color(
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value,
                                        UnityEngine.Random.value);
                                    colored += $"<color=#{ColorToHex(rc)}>{c}</color>";
                                }
                                mgr.WinText.text = colored;
                            }
                            mgr.WinText.transform.localPosition = baseWinPos;
                        }
                    }

                    if (glitchEndTime > 0 && t > glitchEndTime) {
                        glitchEndTime = 0;
                        ClearGlitchTex();
                    }

                    if (mgr.WinText != null) {
                        if (UnityEngine.Random.value < 0.015f * decay)
                            mgr.WinText.color = new Color(
                                UnityEngine.Random.value, UnityEngine.Random.value,
                                UnityEngine.Random.value, 1f);
                        else
                            mgr.WinText.color = Color.white;
                    }

                    if (bonusText != null) {
                        // Entry "materialize": ease scale + alpha in over ~0.35s from Start() - a no-op
                        // once entryEase reaches 1, so this only matters for the opening moment.
                        float entryT = Mathf.Clamp01((t - startTime) / 0.35f);
                        float entryEase = 1f - (1f - entryT) * (1f - entryT);

                        Color target = (UnityEngine.Random.value < 0.02f * decay)
                            ? new Color(
                                Mathf.Clamp01(Color.r + UnityEngine.Random.Range(-0.3f, 0.3f)),
                                Mathf.Clamp01(Color.g + UnityEngine.Random.Range(-0.3f, 0.3f)),
                                Mathf.Clamp01(Color.b + UnityEngine.Random.Range(-0.3f, 0.3f)))
                            : Color;
                        bonusText.color = new Color(target.r, target.g, target.b, entryEase);
                        bonusText.transform.localScale = new Vector3(Mathf.Lerp(0.05f, 0.7f, entryEase), Mathf.Lerp(0.05f, 0.7f, entryEase), 1f);
                        // Re-anchored EVERY frame under the win text instead of restored to a position
                        // captured once: the scramble-morph changes the win text's own extents while
                        // this runs, so a frozen offset ends up inside it.
                        bonusText.transform.position = BannerPos(mgr);
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskCountPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (bug == null || bug.Data == null) return;
                    var (completed, total) = TasksHandler.taskInfo(bug.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= completed;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] TaskCountPatch failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || bug == null || p == null || p != bug || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = BugInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, BugInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Bug] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
