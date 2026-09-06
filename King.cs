// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The King (Crewmate)
 *
 * A crewmate who does no work and has no power - only a court. The King carries NO tasks (they are
 * removed server-side at the assignment, so the crew's task total shrinks by his share) and no
 * ability button. What he has:
 *
 *  - AN ADVISOR. At the assignment the host picks one other crewmate; the King's client learns that
 *    player's role and shows it under their name for the whole game (world and meeting). It is the
 *    STARTING role, captured once shortly after the roles are final (after the draft / the random
 *    promotions) - later changes (a Sidekick recruitment, a shift, an erase) are deliberately not
 *    tracked: the King knows what his advisor was appointed as, not what they became. The advisor
 *    is never told.
 *  - THE CROWN IS THE VIP (option 1662, on by default): the King is always a VIP - on top of
 *    whoever TOR rolled (those keep their tag; TOR's quantity/rate settings decide how many). His
 *    death notifies everyone, but with a royal flash of its own (gold, or the killer's team colour
 *    when TOR's "Show Team Color" is on) and a line of text instead of TOR's silent yellow blink.
 *    The King is NOT put into TOR's Vip list (that would fire TOR's flash on top of ours); the tag
 *    is appended to his role info and the flash is our own MurderPlayer postfix. While the option
 *    is on and the King can spawn, the host cannot park TOR's VIP rate at 0 (VipRateClampPatch
 *    keeps it at 10-100 %) - a forced VIP with the modifier "off" would contradict itself.
 *
 * ARCHITECTURE mirrors Beacon: crew tag over a plain Crewmate, host-authoritative pick, custom RPC
 * module 220 on UCRpc.CallId = 230, gated on "everyone has the mod". Options 1660-1662, draft
 * sentinel 221. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class King {
        // ---- Theme ----
        // Royal blue: the Void owns purple (its TDS-style void theme), gold was taken twice already
        // (Beacon, Collector), and the Stalker's indigo is much greyer and darker than this.
        public static readonly Color Color = new Color(0.30f, 0.40f, 0.95f);
        private static readonly Color RoyalGold = new Color(1f, 0.82f, 0.25f);

        // ---- Options (IDs 1660-1662) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption AlwaysVip;

        // ---- Runtime state ----
        public static PlayerControl king;
        public static bool active;
        private static byte kingPlayerId = byte.MaxValue;
        private static byte advisorId = byte.MaxValue;
        private static string advisorRoleText;      // captured once on the King's client
        private static float captureAt;             // when the capture may run (roles final)
        private static bool crownVip;               // the crown is the VIP this game

        // ---- Custom RPC subtypes: module byte 220 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.KingRpcId;
        private const byte SubSet = 0;              // kingId (255 = clear), advisorId   host -> everyone

        private static readonly System.Random rnd = new System.Random();

        // ---- Role identity ----
        private static RoleInfo kingInfo;
        public static RoleInfo KingInfo() => kingInfo ??= new RoleInfo(
            "King", Color, "No tasks, no powers - but you know your advisor's role",
            "Rule; your advisor's role is known to you", RoleId.Crewmate);

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1660, Types.Crewmate, "King",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1661, Types.Crewmate, "King Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                AlwaysVip = CustomOption.Create(1662, Types.Crewmate, "King Is Always The VIP",
                    true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[King] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[King] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalKing() =>
            active && king != null && PlayerControl.LocalPlayer != null
            && king.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static PlayerControl Advisor() => advisorId == byte.MaxValue ? null : Helpers.playerById(advisorId);
        private static bool VipModifierEnabled() {
            try { return CustomOptionHolder.modifierVip != null && CustomOptionHolder.modifierVip.getSelection() > 0; }
            catch { return false; }
        }

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte subtype) {
            var w = UCRpc.Begin(RpcId);
            w.Write(subtype);
            return w;
        }

        public static void SendSet(byte id, byte advisor) {
            try {
                var w = BeginRpc(SubSet);
                w.Write(id);
                w.Write(advisor);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id, advisor);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[King] SendSet failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                if (subtype == SubSet) {
                    byte id = reader.ReadByte();
                    byte advisor = reader.ReadByte();
                    if (UCRpc.RequireHost("King.Set")) ApplySet(id, advisor);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[King] HandleRpc failed: {e}");
            }
        }

        private static void ApplySet(byte id, byte advisor) {
            king = Helpers.playerById(id);
            active = king != null;
            kingPlayerId = active ? id : byte.MaxValue;
            advisorId = active ? advisor : byte.MaxValue;
            advisorRoleText = null;
            captureAt = Time.time + 1f;   // the same intro frame still promotes other UC roles
            crownVip = false;
            if (!active) return;
            UCPromotion.Claim(id);

            // The crown is the VIP - IN ADDITION to whoever TOR rolled (design decision 2026-09-06:
            // the rolled VIPs keep their tag, the option only guarantees that the King is one too).
            // The King is not put into TOR's Vip list (that would fire TOR's flash on top of ours);
            // the tag is appended in RoleInfoPatch, the flash is MurderPatch. If TOR itself also
            // rolled him as VIP, TOR's list entry is dropped so he never gets both flashes.
            if (AlwaysVip?.getBool() ?? true) {
                crownVip = true;
                try {
                    if (Vip.vip != null && Vip.vip.Any(p => p != null && p.PlayerId == kingPlayerId))
                        Vip.vip.RemoveAll(p => p != null && p.PlayerId == kingPlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[King] VIP list check failed: {e.Message}");
                }
            }

            // No tasks - server-visibly (the Necromancer/Auditor rule: the task win is server-
            // authoritative, so the tasks must leave the host's bookkeeping, not just the HUD).
            if (AmHost()) {
                try {
                    king.Data.RpcSetTasks(new Il2CppStructArray<byte>(0));
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[King] stripped {king.Data.PlayerName}'s tasks (server-visible).");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[King] task strip failed: {e}");
                }
            }
            UnknownsCollectionPlugin.Logger?.LogInfo(
                $"[King] The King is {king.Data?.PlayerName}, advisor {Advisor()?.Data?.PlayerName ?? "none"}.");
        }

        public static void MarkFromDraft(byte playerId) {
            // The draft mark carries no advisor; the host appoints one on its first tick after the
            // intro (HostAppointAdvisorIfMissing).
            ApplySet(playerId, byte.MaxValue);
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
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    var pick = candidates[rnd.Next(candidates.Count)];
                    SendSet(pick.PlayerId, PickAdvisor(pick.PlayerId));
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[King] IntroEnd pick failed: {e}");
                }
            }
        }

        // A crew advisor if there is one (no impostor, no neutral), otherwise anyone else alive.
        private static byte PickAdvisor(byte exclude) {
            var all = PlayerControl.AllPlayerControls.ToArray().Where(p => IsAlive(p) && p.PlayerId != exclude).ToList();
            var crew = all.Where(p => {
                try {
                    if (p.Data.Role != null && p.Data.Role.IsImpostor) return false;
                    var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                    return info == null || !info.isNeutral;
                } catch { return false; }
            }).ToList();
            var pool = crew.Count > 0 ? crew : all;
            if (pool.Count == 0) return byte.MaxValue;
            return pool[rnd.Next(pool.Count)].PlayerId;
        }

        private static float nextHostTick;
        private static void HostAppointAdvisorIfMissing() {
            if (!AmHost() || !active || king == null) return;
            if (advisorId != byte.MaxValue) return;
            byte a = PickAdvisor(kingPlayerId);
            if (a == byte.MaxValue) return;
            SendSet(kingPlayerId, a);   // re-Apply is idempotent (same king, now with an advisor)
        }

        // ---- Lobby rule: while the crown forces the VIP, TOR's VIP modifier cannot sit at 0 ----
        // "King Is Always The VIP" makes no sense with the VIP modifier switched off, so the host
        // keeps TOR's VIP spawn rate at 10 % or more (10-100) as long as the King can spawn and the
        // option is on. Polled on the host (options only ever change there); the correction goes
        // through TOR's own updateSelection (lobby notice) and is shared/saved like a manual change
        // when the settings menu is closed (updateSelection only shares while a menu row exists).
        private static float nextClampTick;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class VipRateClampPatch {
            public static void Postfix() {
                try {
                    if (Time.realtimeSinceStartup < nextClampTick) return;
                    nextClampTick = Time.realtimeSinceStartup + 0.5f;
                    if (!AmHost() || PlayerControl.LocalPlayer == null) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!(AlwaysVip?.getBool() ?? false)) return;
                    var vip = CustomOptionHolder.modifierVip;
                    if (vip == null || vip.getSelection() > 0) return;

                    bool menuRow = vip.optionBehaviour != null && vip.optionBehaviour is StringOption;
                    vip.updateSelection(1);
                    if (!menuRow) {
                        try { if (vip.entry != null) vip.entry.Value = vip.selection; } catch { }
                        CustomOption.ShareOptionChange((uint)vip.id);
                    }
                    UnknownsCollectionPlugin.Logger?.LogInfo("[King] VIP modifier rate raised to 10 % (King Is Always The VIP is on).");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[King] VIP rate clamp failed: {e.Message}");
                }
            }
        }

        // ---- Per-frame driver: advisor reveal (King's client) + host bookkeeping ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active) return;
                    if (IsLocalKing()) AdvisorTick();
                    if (!AmHost()) return;
                    if (Time.realtimeSinceStartup < nextHostTick) return;
                    nextHostTick = Time.realtimeSinceStartup + 0.5f;
                    HostAppointAdvisorIfMissing();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[King] HudUpdate failed: {e}");
                }
            }
        }

        private static void AdvisorTick() {
            var advisor = Advisor();
            if (advisor == null || advisor.Data == null) return;

            // Capture once, after the roles are final. Colours included (TOR's own role string).
            if (advisorRoleText == null) {
                if (Time.time < captureAt) return;
                try {
                    advisorRoleText = RoleInfo.GetRolesString(advisor, true, false, true);
                } catch { advisorRoleText = ""; }
                try {
                    var hud = FastDestroyableSingleton<HudManager>.Instance;
                    if (hud != null && hud.Chat != null)
                        hud.Chat.AddChat(PlayerControl.LocalPlayer,
                            UCLocalization.Tr("uc.ui.king.advisor_chat", advisor.Data.PlayerName, advisorRoleText));
                } catch { }
            }
            if (string.IsNullOrEmpty(advisorRoleText)) return;

            // TOR writes the "Info" line only for the local player, for ghosts and for the Lawyer's
            // client; a living King therefore owns the advisor's line. Once the King is dead TOR's
            // ghost info takes over the same text object, so we stop writing.
            if (PlayerControl.LocalPlayer.Data == null || PlayerControl.LocalPlayer.Data.IsDead) return;

            try {
                var nameText = advisor.cosmetics?.nameText;
                if (nameText != null) {
                    Transform infoTr = nameText.transform.parent.FindChild("Info");
                    TMPro.TextMeshPro info = infoTr != null ? infoTr.GetComponent<TMPro.TextMeshPro>() : null;
                    if (info == null) {
                        info = UnityEngine.Object.Instantiate(nameText, nameText.transform.parent);
                        info.transform.localPosition += Vector3.up * 0.225f;
                        info.fontSize *= 0.75f;
                        info.gameObject.name = "Info";
                        info.color = info.color.SetAlpha(1f);
                    }
                    info.text = advisorRoleText;
                    info.gameObject.SetActive(advisor.Visible);
                }
            } catch { }

            try {
                var meeting = MeetingHud.Instance;
                if (meeting?.playerStates == null) return;
                foreach (var ps in meeting.playerStates) {
                    if (ps == null || ps.TargetPlayerId != advisorId || ps.NameText == null) continue;
                    Transform infoTr = ps.NameText.transform.parent.FindChild("Info");
                    TMPro.TextMeshPro info = infoTr != null ? infoTr.GetComponent<TMPro.TextMeshPro>() : null;
                    if (info == null) {
                        info = UnityEngine.Object.Instantiate(ps.NameText, ps.NameText.transform.parent);
                        info.transform.localPosition += Vector3.down * 0.2f;
                        info.fontSize *= 0.60f;
                        info.gameObject.name = "Info";
                        ps.NameText.transform.localPosition = new Vector3(0.3384f, 0.0311f, -0.1f);
                    }
                    info.text = meeting.state == MeetingHud.VoteStates.Results ? "" : advisorRoleText;
                }
            } catch { }
        }

        // ---- The royal death flash (our own VIP notice, everyone's client) ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        [HarmonyPriority(Priority.Low)]
        static class MurderPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || !crownVip || king == null || target == null) return;
                    if (target.PlayerId != kingPlayerId) return;
                    if (target.Data == null || !target.Data.IsDead) return;   // a suppressed murder
                    Color color = RoyalGold;
                    if (Vip.showColor && __instance != null && __instance.Data != null) {
                        color = Color.white;
                        if (__instance.Data.Role != null && __instance.Data.Role.IsImpostor) color = Color.red;
                        else {
                            var info = RoleInfo.getRoleInfoForPlayer(__instance, false).FirstOrDefault();
                            if (info != null && info.isNeutral) color = Color.blue;
                        }
                    }
                    Helpers.showFlash(color, 2f, UCLocalization.Tr("uc.ui.king.fallen"));
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[King] royal flash failed: {e}");
                }
            }
        }

        // ---- Task accounting (belt and braces: the strip above already leaves nothing to count) ----
        [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
        static class TaskPatch {
            public static void Postfix(GameData __instance) {
                try {
                    if (!active || king == null || king.Data == null) return;
                    var (done, total) = TasksHandler.taskInfo(king.Data);
                    __instance.TotalTasks -= total;
                    __instance.CompletedTasks -= done;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[King] TaskPatch failed: {e}");
                }
            }
        }

        // ---- Role identity: King replaces the Crewmate entry; the VIP tag is appended when the
        // crown carries it (TOR's own visibility rule for hidden modifiers is mirrored). ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, [HarmonyArgument(1)] bool showModifier,
                                        ref List<RoleInfo> __result) {
                try {
                    if (!active || king == null || p == null || p != king || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) {
                            __result[i] = KingInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, KingInfo());

                    if (crownVip && showModifier && !__result.Contains(RoleInfo.vip)) {
                        bool visible = true;
                        try {
                            visible = !CustomOptionHolder.modifiersAreHidden.getBool()
                                      || (PlayerControl.LocalPlayer?.Data?.IsDead ?? false)
                                      || AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Ended;
                        } catch { }
                        if (visible) __result.Insert(0, RoleInfo.vip);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[King] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            king = null;
            active = false;
            kingPlayerId = byte.MaxValue;
            advisorId = byte.MaxValue;
            advisorRoleText = null;
            captureAt = 0f;
            crownVip = false;
            nextHostTick = 0f;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("King", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("King", FullReset);
        }
    }
}
