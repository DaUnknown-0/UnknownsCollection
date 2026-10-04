// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCPromotion - shared pick-arbiter for the Unknown's Collection "layered" roles.
 *
 * Several UC roles are display-tags promoted over a plain TOR Impostor (Tesla, Saboteur, Poisoner,
 * Silencer, Illusionist) or a plain Crewmate (Siphoner, Witness). Each role's host-authoritative pick
 * (IntroCutscene.OnDestroy) must avoid landing two UC roles on the SAME player. Instead of every role
 * knowing about every other, they all funnel through this tiny claim registry:
 *
 *   - candidates are filtered with !UCPromotion.IsClaimed(id);
 *   - when a role is assigned (its Apply* runs on every client) it calls Claim(id).
 *
 * Because picks only happen on the host, the host's claim set is what actually gates exclusion; the
 * clients just keep their copy in step (harmless). Cleared on a full game reset.
 *
 * IsPlainImpostor / IsPlainCrewmate are centralized here so every role uses the exact same eligibility
 * rule (its FIRST RoleInfo is the vanilla Impostor / Crewmate entry, i.e. no special TOR role on top).
 *
 * Claim() is also the single choke point every UC role's Apply* runs through (Draft picks AND random
 * IntroCutscene promotion alike), so it doubles as the hook for the role-agnostic "you have been
 * promoted" reveal cue (UCRevealFx). Claim() runs on EVERY client for EVERY UC role assignment - the
 * reveal is therefore gated to playerId == PlayerControl.LocalPlayer.PlayerId here, exactly once, so
 * individual role files never need to remember the gate themselves. A role that already has its own
 * bespoke promotion feedback (e.g. a future Tesla-specific stinger in Tesla.ApplySetTesla) can pass
 * suppressFx: true to Claim() to avoid a double cue - see the suppressFx parameter below.
 *
 * THE RANDOM PICKS RUN IN A SHUFFLED ORDER (User 2026-10-04). Every role still rolls in its own
 * IntroCutscene.OnDestroy postfix, but those postfixes used to run in Harmony's registration order,
 * fixed per build: the same roles always had first choice of the plain Impostors/Crewmates.
 * CentralizePicks (called once after PatchAll) takes every UC IntroEndPatch / IntroEndPickPatch
 * postfix off the hook and RunPicks calls them from ONE postfix, in a fresh random order each game.
 * UC neutrals count against TOR's "Maximum Neutral Roles" there, like they do in the draft (their
 * draft entries are isNeutral, UCRoleDraft.Make): a neutral pick is skipped once that many players
 * already hold a neutral role.
 */

using System.Linq;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    public static class UCPromotion {
        // Player ids already claimed by a UC role this game (host: the authoritative exclusion set).
        private static readonly System.Collections.Generic.HashSet<byte> claimed = new();

        public static bool IsClaimed(byte playerId) => claimed.Contains(playerId);

        // Set (and reset) by UCRoleDraft.SetRolePatch around a Role-Draft pick: the reveal's screen
        // flash (Helpers.showFlash) disables HudManager.FullScreen when it finishes - the very renderer
        // TOR's Role Draft uses as its black backdrop - so a reveal fired mid-draft permanently cuts
        // the draft's blackscreen and exposes the game world/HUD behind it. A drafted player actively
        // clicked their role anyway, so the promotion cue is skipped entirely for draft picks.
        public static bool SuppressRevealForDraftPick = false;

        // suppressFx: pass true when the caller already gives the promoted player its own bespoke
        // reveal feedback, so UCRevealFx's generic gold/white cue does not double up with it.
        public static void Claim(byte playerId, bool suppressFx = false) {
            if (playerId == byte.MaxValue) return;
            claimed.Add(playerId);
            // Info-Leak-Regel: Claim() fires on EVERY client for EVERY UC role assignment - only the
            // player who was actually promoted may ever see/hear this, never bystanders.
            if (!suppressFx && !SuppressRevealForDraftPick
                && PlayerControl.LocalPlayer != null && playerId == PlayerControl.LocalPlayer.PlayerId)
                UCRevealFx.PlayReveal();
        }

        public static void ClearClaims() => claimed.Clear();

        // ---- the shuffled pick order ----
        private static readonly System.Collections.Generic.List<System.Reflection.MethodInfo> picks = new();
        private static readonly System.Random pickRnd = new();
        private static readonly System.Collections.Generic.HashSet<string> NeutralPickers = new() {
            "Bug", "Follower", "Copycat", "Collector", "Pelican", "Necromancer", "Stalker",
        };

        public static void CentralizePicks(Harmony harmony) {
            try {
                var original = AccessTools.Method(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy));
                var info = Harmony.GetPatchInfo(original);
                if (info == null) return;
                foreach (var p in info.Postfixes.ToList()) {
                    var m = p.PatchMethod;
                    var t = m?.DeclaringType;
                    if (p.owner != harmony.Id || t == null) continue;
                    if (t.Name != "IntroEndPatch" && t.Name != "IntroEndPickPatch") continue;
                    if (m.GetParameters().Length != 0) continue;
                    harmony.Unpatch(original, m);
                    picks.Add(m);
                }
                harmony.Patch(original, postfix: new HarmonyMethod(typeof(UCPromotion), nameof(RunPicks)) { priority = Priority.Low });
                UnknownsCollectionPlugin.Logger?.LogInfo($"[UCPromotion] {picks.Count} intro picks run in a shuffled order.");
            } catch (System.Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCPromotion] centralizing the picks failed: {e}");
            }
        }

        // Picks that depend on what the others handed out run last: the Poisoner needs to know
        // whether a Paramedic is in play (Fable review 2026-10-04).
        private static readonly System.Collections.Generic.HashSet<string> LatePickers = new() { "Poisoner" };

        public static void RunPicks() {
            var order = picks.OrderBy(_ => pickRnd.Next())
                             .OrderBy(m => LatePickers.Contains(m.DeclaringType?.DeclaringType?.Name ?? "") ? 1 : 0)
                             .ToList();
            foreach (var m in order) {
                try {
                    string role = m.DeclaringType?.DeclaringType?.Name ?? "";
                    if (NeutralPickers.Contains(role) && AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost
                        && !UCRoleDraft.DraftWillRun() && !NeutralSlotFree()) {
                        UnknownsCollectionPlugin.Logger?.LogInfo($"[UCPromotion] {role}: the neutral roles are at their maximum - not rolled.");
                        continue;
                    }
                    m.Invoke(null, null);
                } catch (System.Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCPromotion] intro pick {m.DeclaringType?.FullName} failed: {e}");
                }
            }
        }

        // Host, at the intro: are there fewer neutral role holders than TOR's maximum? TOR's own
        // neutrals and the UC ones already handed out (their RoleInfos are isNeutral) both count.
        private static bool NeutralSlotFree() {
            try {
                int max = TheOtherRoles.CustomOptionHolder.neutralRolesCountMax.getSelection();
                int n = 0;
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    if (RoleInfo.getRoleInfoForPlayer(p, false).Any(r => r != null && r.isNeutral)) n++;
                }
                return n < max;
            } catch { return true; }
        }

        public static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        // A plain TOR Impostor (no special impostor role like Morphling/Bomber/...): its first RoleInfo
        // is exactly the Impostor entry. Excludes anyone already claimed by another UC role.
        public static bool IsPlainImpostor(PlayerControl p) {
            if (!IsAlive(p) || p.Data.Role == null || !p.Data.Role.IsImpostor) return false;
            if (IsClaimed(p.PlayerId)) return false;
            var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
            return info != null && info.roleId == RoleId.Impostor;
        }

        // A plain TOR Crewmate (no special crew/neutral role on top): its first RoleInfo is exactly the
        // Crewmate entry. Excludes anyone already claimed by another UC role.
        /// One of TOR's Lovers. Neutral roles may be Lovers as in TOR; only the ones whose mechanic
        /// breaks with a partner leave them out (Pelican, Necromancer, Stalker; Fable review 2026-10-04).
        public static bool IsLover(PlayerControl p) =>
            p != null && ((Lovers.lover1 != null && Lovers.lover1.PlayerId == p.PlayerId)
                       || (Lovers.lover2 != null && Lovers.lover2.PlayerId == p.PlayerId));

        public static bool IsPlainCrewmate(PlayerControl p) {
            if (!IsAlive(p) || p.Data.Role == null || p.Data.Role.IsImpostor) return false;
            if (IsClaimed(p.PlayerId)) return false;
            var info = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
            return info != null && info.roleId == RoleId.Crewmate;
        }

        // Does the player already carry ANY modifier - one of TOR's twelve or one of UC's own (Gambler,
        // Void, Sleepwalker, Last Words, Sixth Sense, Colorblind)? The UC modifiers are picked on the host at IntroCutscene.OnDestroy, i.e. AFTER TOR's
        // assignModifiers has handed out its modifiers, so this is the "one modifier per player" gate
        // for those picks: TOR itself never stacks two modifiers on one player (a shared player pool
        // in assignModifiersToPlayers), and the UC modifiers must not undo that rule from outside.
        //
        // Two sources, on purpose: TOR's statics cover the host-visible truth for all twelve (Bait /
        // Bloody / VIP are read directly because getRoleInfoForPlayer hides that family from a living
        // local player while option 1009 is on). The RoleInfo pass on top catches holders that only a
        // sibling mod knows about - UTS's extra Minis / Armored / Tiebreakers are tracked in its own
        // lists and appended to getRoleInfoForPlayer via postfix, never written into TOR's single
        // statics - restricted to TOR's modifier id range so a display-only sentinel of some other
        // mod (ChanceMod's Chance tag, which by design rides on top of a TOR modifier) is not mistaken
        // for one.
        public static bool HasAnyModifier(PlayerControl p) {
            if (p == null) return false;
            byte id = p.PlayerId;
            try {
                if (Bait.bait != null && Bait.bait.Any(x => x != null && x.PlayerId == id)) return true;
                if (Bloody.bloody != null && Bloody.bloody.Any(x => x != null && x.PlayerId == id)) return true;
                if (Vip.vip != null && Vip.vip.Any(x => x != null && x.PlayerId == id)) return true;
                if (AntiTeleport.antiTeleport != null && AntiTeleport.antiTeleport.Any(x => x != null && x.PlayerId == id)) return true;
                if (Sunglasses.sunglasses != null && Sunglasses.sunglasses.Any(x => x != null && x.PlayerId == id)) return true;
                if (Invert.invert != null && Invert.invert.Any(x => x != null && x.PlayerId == id)) return true;
                if (Chameleon.chameleon != null && Chameleon.chameleon.Any(x => x != null && x.PlayerId == id)) return true;
                if (Lovers.lover1 != null && Lovers.lover1.PlayerId == id) return true;
                if (Lovers.lover2 != null && Lovers.lover2.PlayerId == id) return true;
                if (Tiebreaker.tiebreaker != null && Tiebreaker.tiebreaker.PlayerId == id) return true;
                if (Mini.mini != null && Mini.mini.PlayerId == id) return true;
                if (Armored.armored != null && Armored.armored.PlayerId == id) return true;
                if (Shifter.shifter != null && Shifter.shifter.PlayerId == id) return true;

                if (Gambler.active && Gambler.gambler != null && Gambler.gambler.PlayerId == id) return true;
                if (VoidModifier.active && VoidModifier.voidPlayer != null && VoidModifier.voidPlayer.PlayerId == id) return true;
                if (Sleepwalker.active && Sleepwalker.sleepwalker != null && Sleepwalker.sleepwalker.PlayerId == id) return true;
                if (LastWords.active && LastWords.carrier != null && LastWords.carrier.PlayerId == id) return true;
                if (SixthSense.active && SixthSense.carrier != null && SixthSense.carrier.PlayerId == id) return true;
                if (Colorblind.active && Colorblind.carrier != null && Colorblind.carrier.PlayerId == id) return true;
                if (Giant.active && Giant.giant != null && Giant.giant.PlayerId == id) return true;

                foreach (var ri in RoleInfo.getRoleInfoForPlayer(p, true))
                    if (ri != null && ri.isModifier && ri.roleId >= RoleId.Lover && ri.roleId <= RoleId.Shifter) return true;
            } catch { }
            return false;
        }

        // Clear claims on a full game-state reset (next game's start).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("UCPromotion", ClearClaims);
        }

        // Same lobby-leak rule as the roles (AUDIT M-12): the byte-keyed state above is keyed by
        // PlayerId, which is handed out per LOBBY, and resetVariables only arrives from a host
        // that has this mod. Clearing on OnGameJoined too keeps a previous lobby's ids from
        // acting on whoever inherits them here.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() => UCResetGuard.Run("UCPromotion", ClearClaims);
        }
    }
}
