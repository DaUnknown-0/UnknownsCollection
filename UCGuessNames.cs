// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCGuessNames - a guess on a UC role keeps its name on every client.
 *
 * TOR sends a guess as (byte)roleInfo.roleId (MeetingPatch.cs:492) and every receiver turns it back
 * into a RoleInfo with allRoleInfos.FirstOrDefault(roleId) (RPC.cs:997 and 1066). UC's grid entries
 * share RoleId.Impostor / RoleId.Crewmate with the base roles (they have to: the hit is a reference
 * compare on the guesser's own client), so the ghosts' chat line, the Thief's guess check and the
 * replay all read "Impostor" or "Crewmate" for a Tesla, Bug, Hypnotist ... guess (User 2026-10-02).
 *
 * The guesser's client knows the button: TOR's grid takes two clicks on the same button (the first
 * selects, the second shoots), so every click on a grid button tells everyone which UC entry is
 * selected (index into UCGuesser.AllInfos, 255 = a TOR role), on UC's channel, module 233. The
 * reliable channel keeps that message ahead of TOR's GuesserShoot from the same sender. Inside
 * guesserShoot the picked entry then sits at the FRONT of allRoleInfos, so TOR's FirstOrDefault and
 * everyone patching after us (UTS replay) find it; a finalizer puts the list back. Without a pick
 * (an older client) a correct guess still names the target's own role.
 *
 * MADE ROBUST (User 2026-10-18: the ghosts still read "Crewmate"/"Impostor" for UC guesses)
 *  - The pick used to be sent only while TeslaVersionHandshake.EveryoneHasMod() held, which demands the
 *    same version AND the same build GUID on every client. One differing client and nobody sent a pick,
 *    silently. The pick is plain information now and always goes out; clients without UC ignore module 233.
 *  - It carries the role NAME next to the index, so a receiver finds the entry by name, whatever order its
 *    own UCGuesser.AllInfos has. The index stays for older senders (no name in the message).
 *  - Without a pick, a hit names the target's role from UCGuessTruth (TOR's raw result plus the replayed
 *    postfix chain) instead of the live getRoleInfoForPlayer, whose UC postfixes tiering can drop.
 *  - One info line per guess says what the ghosts' line names and where the name came from.
 *
 * HIT CHECK SAFETY NET (review 2026-10-02). TOR decides hit or miss on the guesser's client with
 * getRoleInfoForPlayer(target).First() == button entry. A UC role only shows up there through UC's
 * postfixes on getRoleInfoForPlayer, a hot managed TOR method whose detour .NET tiering drops (see
 * UTS DetourWatchdog). With the detour gone, the guesser's client sees the base Impostor/Crewmate:
 * guessing the right UC role killed the guesser, guessing "Impostor" killed a Tesla.
 *
 * The guesser's client therefore judges each picked entry itself, at the click, and only when the
 * live call shows the dropped state (its first entry equals TOR's raw result while the replayed
 * postfix chain says otherwise). The raw result comes from a reverse-patched copy of TOR's method,
 * the postfixes are replayed from Harmony's patch info. The verdict travels with the pick, so every
 * client applies the SAME correction in guesserShoot; nobody re-judges from their own point of view
 * (some postfixes depend on the viewer). No verdict, no correction: TOR's decision stands.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class UCGuessNames {
        private const byte RpcId = UnknownsCollectionPlugin.GuessPickRpcId;
        private const byte NoUcRole = 255;

        private const byte VerdictNone = 0, VerdictHit = 1, VerdictMiss = 2;

        private struct Verdict { public byte Target; public byte Value; }
        private struct Pick { public byte Idx; public string Name; }   // Name null: an older sender

        private static readonly Dictionary<byte, Pick> picks = new Dictionary<byte, Pick>();    // guesserId -> pick
        private static readonly Dictionary<byte, Verdict> verdicts = new Dictionary<byte, Verdict>();
        private static int lastSent = -1;                                                        // local, per meeting
        private static FieldInfo guesserUiField;
        private static FieldInfo currentTargetField;

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, Handle);
            UCGuessTruth.Init(harmony);
            try {
                // MeetingHudPatch is internal and guesserOnClick private: reflection, like Hunter.cs
                var mp = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                guesserUiField = mp?.GetField("guesserUI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                currentTargetField = mp?.GetField("guesserCurrentTarget", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var m = mp?.GetMethod("guesserOnClick", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (m == null || guesserUiField == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning("[UCGuessNames] guesserOnClick/guesserUI not found - UC guesses keep showing as Impostor/Crewmate.");
                    return;
                }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(UCGuessNames).GetMethod(nameof(AfterGrid), BindingFlags.Public | BindingFlags.Static)));
                UnknownsCollectionPlugin.Logger?.LogInfo("[UCGuessNames] guess grid hooked.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCGuessNames] TryPatch failed: {e}");
            }
        }

        private static string Plain(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]*>", "").Trim();

        // Postfix on guesserOnClick: one extra listener per role button of the grid just built.
        public static void AfterGrid() {
            try {
                var ui = guesserUiField?.GetValue(null) as GameObject;
                if (ui == null) return;
                var infos = UCGuesser.AllInfos();
                foreach (var pb in ui.GetComponentsInChildren<PassiveButton>(true)) {
                    if (pb == null) continue;
                    var label = pb.GetComponentInChildren<TMPro.TextMeshPro>(true);
                    string name = label != null ? Plain(label.text) : "";
                    if (name.Length == 0) continue;                       // the exit button
                    int idx = Array.FindIndex(infos, ri => ri != null && Plain(ri.name) == name);
                    byte pick = idx >= 0 ? (byte)idx : NoUcRole;
                    // the entry TOR compares by reference: UC's own, or the TOR entry of that name
                    RoleInfo entry = idx >= 0 ? infos[idx]
                        : RoleInfo.allRoleInfos.FirstOrDefault(ri => ri != null && ri.name == name && Array.IndexOf(infos, ri) < 0);
                    string picked = name;
                    pb.OnClick.AddListener((Action)(() => SendPick(pick, picked, entry)));
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCGuessNames] grid hook failed: {e.Message}");
            }
        }

        private static byte CurrentTarget() {
            try { return currentTargetField != null ? (byte)currentTargetField.GetValue(null) : byte.MaxValue; }
            catch { return byte.MaxValue; }
        }

        // Guesser side only: a verdict exists only when the live getRoleInfoForPlayer is in the
        // dropped state and the replayed chain names a different main role (see the header).
        private static byte Judge(byte targetId, RoleInfo entry) {
            try {
                if (entry == null) return VerdictNone;
                var p = Helpers.playerById(targetId);
                if (p == null || p.Data == null) return VerdictNone;
                if (!UCGuessTruth.TryMain(p, out RoleInfo raw, out RoleInfo truth)) return VerdictNone;
                var live = RoleInfo.getRoleInfoForPlayer(p, false)?.FirstOrDefault();
                if (live != raw || truth == raw) return VerdictNone;   // detour alive, or no postfix changes the main role
                byte v = truth == entry ? VerdictHit : VerdictMiss;
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    $"[UCGuessNames] getRoleInfoForPlayer detour is dropped on this client: TOR sees {live?.name} for {p.Data.PlayerName}, "
                    + $"the postfix chain says {truth?.name}. Guess on {entry.name} judged {(v == VerdictHit ? "hit" : "miss")}.");
                return v;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCGuessNames] judge failed: {e.Message}");
                return VerdictNone;
            }
        }

        private static void SendPick(byte idx, string name, RoleInfo entry) {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null) return;
                byte target = CurrentTarget();
                byte verdict = Judge(target, entry);
                int key = idx | (target << 8) | (verdict << 16);
                if (lastSent == key) return;
                lastSent = key;
                picks[me.PlayerId] = new Pick { Idx = idx, Name = name };
                verdicts[me.PlayerId] = new Verdict { Target = target, Value = verdict };
                MessageWriter w = UCRpc.Begin(RpcId);
                w.Write(idx);
                w.Write(target);
                w.Write(verdict);
                w.Write(name ?? "");           // older receivers stop reading after the verdict
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[UCGuessNames] pick send failed: {e.Message}");
            }
        }

        private static void Handle(MessageReader reader) {
            byte idx = reader.ReadByte();
            var sender = UCRpc.Sender;
            if (sender == null) return;
            string name = null;
            if (reader.Length - reader.Position >= 2) {
                verdicts[sender.PlayerId] = new Verdict { Target = reader.ReadByte(), Value = reader.ReadByte() };
                if (reader.Length - reader.Position > 0) name = reader.ReadString();
            } else {
                verdicts.Remove(sender.PlayerId);
            }
            picks[sender.PlayerId] = new Pick { Idx = idx, Name = string.IsNullOrEmpty(name) ? null : name };
        }

        // The UC entry a pick names: by name when the sender sent one (independent of this client's
        // AllInfos order), by index only for an older sender. A TOR role's name finds nothing here.
        private static RoleInfo Resolve(Pick pick, RoleInfo[] infos) {
            if (pick.Name != null) return infos.FirstOrDefault(ri => ri != null && Plain(ri.name) == pick.Name);
            return pick.Idx < infos.Length ? infos[pick.Idx] : null;
        }

        // The target's main role on a hit without a usable pick: the replayed postfix chain first (it
        // survives a dropped getRoleInfoForPlayer detour), the live call only when the replay is off.
        private static RoleInfo TargetMain(byte targetId) {
            var t = Helpers.playerById(targetId);
            if (t == null) return null;
            try {
                if (UCGuessTruth.TryMain(t, out _, out RoleInfo truth) && truth != null) return truth;
            } catch { }
            return RoleInfo.getRoleInfoForPlayer(t, false)?.FirstOrDefault();
        }

        private static string NameOf(byte id) => Helpers.playerById(id)?.Data?.PlayerName ?? $"#{id}";

        private struct Moved { public RoleInfo Info; public int From; }   // From -1: not in the list before

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        static class ShootPatch {
            // ahead of everything that reads the guessed role (UTS' replay tap runs at First = 800)
            [HarmonyPriority(1000)]
            public static void Prefix([HarmonyArgument(0)] byte killerId, [HarmonyArgument(1)] ref byte dyingId,
                                      [HarmonyArgument(2)] byte guessedId, [HarmonyArgument(3)] byte roleId, out Moved __state) {
                __state = default;
                try {
                    // The guesser's verdict overrides a decision made on a dropped detour. Every
                    // client gets the same verdict, so every client corrects the same way.
                    if (verdicts.TryGetValue(killerId, out var v) && v.Value != VerdictNone && v.Target == guessedId) {
                        byte want = v.Value == VerdictHit ? guessedId : killerId;
                        if (dyingId != want) {
                            UnknownsCollectionPlugin.Logger?.LogWarning(
                                $"[UCGuessNames] guess by {killerId} on {guessedId}: TOR chose {dyingId} to die, the guesser's verdict says {want}. Corrected.");
                            dyingId = want;
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[UCGuessNames] verdict check failed: {e.Message}");
                }
                try {
                    var infos = UCGuesser.AllInfos();
                    RoleInfo ri = null;
                    string how;
                    bool heard = picks.TryGetValue(killerId, out var pick);
                    if (heard) {
                        ri = Resolve(pick, infos);
                        how = pick.Name != null ? $"pick \"{pick.Name}\"" : $"pick #{pick.Idx} (older sender)";
                    } else {
                        how = "no pick heard";
                    }
                    if (ri == null && dyingId == guessedId) {
                        // a hit can only have named the target's own role
                        var main = TargetMain(guessedId);
                        if (main != null && Array.IndexOf(infos, main) >= 0) { ri = main; how += ", hit names the target's role"; }
                    }
                    if (ri != null && (byte)ri.roleId != roleId) { how += $", but {ri.name} has another role id"; ri = null; }
                    string named = ri != null ? ri.name
                        : RoleInfo.allRoleInfos.FirstOrDefault(x => x != null && (byte)x.roleId == roleId)?.name ?? "?";
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[UCGuessNames] guess by {NameOf(killerId)} on {NameOf(guessedId)}: the ghosts' line names {Plain(named)} ({how}).");
                    if (ri == null) return;
                    var list = RoleInfo.allRoleInfos;
                    int at = list.IndexOf(ri);
                    if (at == 0) return;
                    if (at > 0) list.RemoveAt(at);
                    list.Insert(0, ri);
                    __state = new Moved { Info = ri, From = at };
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[UCGuessNames] prefix failed: {e.Message}");
                }
            }

            public static void Finalizer([HarmonyArgument(0)] byte killerId, Moved __state) {
                try {
                    if (__state.Info != null) {
                        var list = RoleInfo.allRoleInfos;
                        list.Remove(__state.Info);
                        if (__state.From >= 0) list.Insert(Mathf.Min(__state.From, list.Count), __state.Info);
                    }
                } catch { }
                picks.Remove(killerId);
                verdicts.Remove(killerId);
                if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == killerId) lastSent = -1;
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() { picks.Clear(); verdicts.Clear(); lastSent = -1; }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() { picks.Clear(); verdicts.Clear(); lastSent = -1; }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { picks.Clear(); verdicts.Clear(); lastSent = -1; }
        }
    }
}
