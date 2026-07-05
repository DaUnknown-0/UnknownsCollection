// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpTORData - data source for the "?" help menu (UCHelpMenu.cs) describing every TheOtherRoles
 * role/modifier as a help-menu entry. UC references the TOR assembly directly, so this simply wraps
 * TheOtherRoles.RoleInfo instances (name/color) and their matching TheOtherRoles.CustomOptionHolder
 * spawn-rate option (used by the menu's "only show if enabled" filter, same as UC's own roles).
 *
 * Scope: every entry of TheOtherRoles.RoleInfo.allRoleInfos except the two base roles (plain
 * Impostor/Crewmate - always present, nothing to explain) and the modifiers (isModifier = true,
 * flagged via IsModifier instead of a distinct Faction). RoleInfo also holds Hunter/Hunted/Prop
 * (Hide n Seek / Prop Hunt reskins of Impostor/Crewmate) - those are deliberately NOT part of
 * allRoleInfos upstream (see RoleInfo.cs) and are skipped here for the same reason: they are not
 * independently spawn-rate-gated roles.
 *
 * Faction is derived exactly like RoleInfo.isImpostor does it (color == Palette.ImpostorRed, with
 * the Spy special case: Spy is colored ImpostorRed but explicitly excluded from isImpostor, and is
 * therefore Crew here too) / RoleInfo.isNeutral; anything left over falls into Crew. Some
 * roles have no dedicated spawn-rate option of their own and are gated only through a parent role's
 * option (Sidekick through Jackal, Pursuer/Prosecutor through Lawyer) - the parent's option is used
 * for Rate in those cases, see remarks below and in the final report.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using TheOtherRoles;

namespace UnknownsCollection {
    public static class UCHelpTORData {
        public readonly struct TORHelpEntry {
            public readonly string Key;      // "tor.help.<RoleInfo-Feldname>"
            public readonly string Name;     // RoleInfo display name (English)
            public readonly int Faction;     // 0 = Impostor, 1 = Crew, 2 = Neutral (mapping done by UCHelpMenu)
            public readonly bool IsModifier;
            public readonly Func<Color> Color;
            public readonly Func<CustomOption> Rate; // null = no dedicated spawn-rate option exists

            public TORHelpEntry(string key, string name, int faction, bool isModifier, Func<Color> color, Func<CustomOption> rate) {
                Key = key;
                Name = name;
                Faction = faction;
                IsModifier = isModifier;
                Color = color;
                Rate = rate;
            }
        }

        private const int Impostor = 0;
        private const int Crew = 1;
        private const int Neutral = 2;

        private static List<TORHelpEntry> entries;
        public static List<TORHelpEntry> Entries() {
            if (entries != null) return entries;
            entries = new List<TORHelpEntry> {
                // ---- Impostor roles ----
                E("godfather", "Godfather", Impostor, () => RoleInfo.godfather.color, () => CustomOptionHolder.mafiaSpawnRate),
                E("mafioso", "Mafioso", Impostor, () => RoleInfo.mafioso.color, () => CustomOptionHolder.mafiaSpawnRate),
                E("janitor", "Janitor", Impostor, () => RoleInfo.janitor.color, () => CustomOptionHolder.mafiaSpawnRate),
                E("morphling", "Morphling", Impostor, () => RoleInfo.morphling.color, () => CustomOptionHolder.morphlingSpawnRate),
                E("camouflager", "Camouflager", Impostor, () => RoleInfo.camouflager.color, () => CustomOptionHolder.camouflagerSpawnRate),
                E("vampire", "Vampire", Impostor, () => RoleInfo.vampire.color, () => CustomOptionHolder.vampireSpawnRate),
                E("eraser", "Eraser", Impostor, () => RoleInfo.eraser.color, () => CustomOptionHolder.eraserSpawnRate),
                E("trickster", "Trickster", Impostor, () => RoleInfo.trickster.color, () => CustomOptionHolder.tricksterSpawnRate),
                E("cleaner", "Cleaner", Impostor, () => RoleInfo.cleaner.color, () => CustomOptionHolder.cleanerSpawnRate),
                E("warlock", "Warlock", Impostor, () => RoleInfo.warlock.color, () => CustomOptionHolder.warlockSpawnRate),
                E("bountyHunter", "Bounty Hunter", Impostor, () => RoleInfo.bountyHunter.color, () => CustomOptionHolder.bountyHunterSpawnRate),
                E("witch", "Witch", Impostor, () => RoleInfo.witch.color, () => CustomOptionHolder.witchSpawnRate),
                E("ninja", "Ninja", Impostor, () => RoleInfo.ninja.color, () => CustomOptionHolder.ninjaSpawnRate),
                E("bomber", "Bomber", Impostor, () => RoleInfo.bomber.color, () => CustomOptionHolder.bomberSpawnRate),
                E("yoyo", "Yo-Yo", Impostor, () => RoleInfo.yoyo.color, () => CustomOptionHolder.yoyoSpawnRate),
                // Guesser: two RoleInfo instances share one spawn-rate option; faction follows
                // RoleInfo's own color/isNeutral (badGuesser is colored ImpostorRed -> Impostor,
                // goodGuesser is not -> falls through to Crew, see file header remark).
                E("goodGuesser", "Nice Guesser", Crew, () => RoleInfo.goodGuesser.color, () => CustomOptionHolder.guesserSpawnRate),
                E("badGuesser", "Evil Guesser", Impostor, () => RoleInfo.badGuesser.color, () => CustomOptionHolder.guesserSpawnRate),

                // ---- Neutral roles ----
                E("jester", "Jester", Neutral, () => RoleInfo.jester.color, () => CustomOptionHolder.jesterSpawnRate),
                E("arsonist", "Arsonist", Neutral, () => RoleInfo.arsonist.color, () => CustomOptionHolder.arsonistSpawnRate),
                E("jackal", "Jackal", Neutral, () => RoleInfo.jackal.color, () => CustomOptionHolder.jackalSpawnRate),
                // Sidekick has no own spawn rate - the Jackal creates it (jackalCanCreateSidekick),
                // itself gated behind jackalSpawnRate; that parent option is used here.
                E("sidekick", "Sidekick", Neutral, () => RoleInfo.sidekick.color, () => CustomOptionHolder.jackalSpawnRate),
                E("vulture", "Vulture", Neutral, () => RoleInfo.vulture.color, () => CustomOptionHolder.vultureSpawnRate),
                E("lawyer", "Lawyer", Neutral, () => RoleInfo.lawyer.color, () => CustomOptionHolder.lawyerSpawnRate),
                // Prosecutor/Pursuer are runtime transformations of Lawyer (see RPC.cs), neither has
                // its own spawn-rate option; both are gated through lawyerSpawnRate.
                E("prosecutor", "Prosecutor", Neutral, () => RoleInfo.prosecutor.color, () => CustomOptionHolder.lawyerSpawnRate),
                E("pursuer", "Pursuer", Neutral, () => RoleInfo.pursuer.color, () => CustomOptionHolder.lawyerSpawnRate),
                E("thief", "Thief", Neutral, () => RoleInfo.thief.color, () => CustomOptionHolder.thiefSpawnRate),

                // ---- Crew roles ----
                E("mayor", "Mayor", Crew, () => RoleInfo.mayor.color, () => CustomOptionHolder.mayorSpawnRate),
                E("portalmaker", "Portalmaker", Crew, () => RoleInfo.portalmaker.color, () => CustomOptionHolder.portalmakerSpawnRate),
                E("engineer", "Engineer", Crew, () => RoleInfo.engineer.color, () => CustomOptionHolder.engineerSpawnRate),
                E("sheriff", "Sheriff", Crew, () => RoleInfo.sheriff.color, () => CustomOptionHolder.sheriffSpawnRate),
                E("deputy", "Deputy", Crew, () => RoleInfo.deputy.color, () => CustomOptionHolder.deputySpawnRate),
                E("lighter", "Lighter", Crew, () => RoleInfo.lighter.color, () => CustomOptionHolder.lighterSpawnRate),
                E("detective", "Detective", Crew, () => RoleInfo.detective.color, () => CustomOptionHolder.detectiveSpawnRate),
                E("timeMaster", "Time Master", Crew, () => RoleInfo.timeMaster.color, () => CustomOptionHolder.timeMasterSpawnRate),
                E("medic", "Medic", Crew, () => RoleInfo.medic.color, () => CustomOptionHolder.medicSpawnRate),
                E("swapper", "Swapper", Crew, () => RoleInfo.swapper.color, () => CustomOptionHolder.swapperSpawnRate),
                E("seer", "Seer", Crew, () => RoleInfo.seer.color, () => CustomOptionHolder.seerSpawnRate),
                E("hacker", "Hacker", Crew, () => RoleInfo.hacker.color, () => CustomOptionHolder.hackerSpawnRate),
                E("tracker", "Tracker", Crew, () => RoleInfo.tracker.color, () => CustomOptionHolder.trackerSpawnRate),
                E("snitch", "Snitch", Crew, () => RoleInfo.snitch.color, () => CustomOptionHolder.snitchSpawnRate),
                // Spy is colored ImpostorRed but RoleInfo.isImpostor explicitly excludes RoleId.Spy,
                // and CustomOptionHolder files spySpawnRate under Types.Crewmate - Crew here as well.
                E("spy", "Spy", Crew, () => RoleInfo.spy.color, () => CustomOptionHolder.spySpawnRate),
                E("securityGuard", "Security Guard", Crew, () => RoleInfo.securityGuard.color, () => CustomOptionHolder.securityGuardSpawnRate),
                E("medium", "Medium", Crew, () => RoleInfo.medium.color, () => CustomOptionHolder.mediumSpawnRate),
                E("trapper", "Trapper", Crew, () => RoleInfo.trapper.color, () => CustomOptionHolder.trapperSpawnRate),

                // ---- Modifiers (isModifier = true; Faction falls through to Crew like RoleInfo's
                // own isImpostor/isNeutral computation would, but IsModifier is the flag UCHelpMenu
                // should actually branch on) ----
                E("bait", "Bait", Crew, true, () => RoleInfo.bait.color, () => CustomOptionHolder.modifierBait),
                E("lover", "Lover", Crew, true, () => RoleInfo.lover.color, () => CustomOptionHolder.modifierLover),
                E("bloody", "Bloody", Crew, true, () => RoleInfo.bloody.color, () => CustomOptionHolder.modifierBloody),
                E("antiTeleport", "Anti tp", Crew, true, () => RoleInfo.antiTeleport.color, () => CustomOptionHolder.modifierAntiTeleport),
                E("tiebreaker", "Tiebreaker", Crew, true, () => RoleInfo.tiebreaker.color, () => CustomOptionHolder.modifierTieBreaker),
                E("sunglasses", "Sunglasses", Crew, true, () => RoleInfo.sunglasses.color, () => CustomOptionHolder.modifierSunglasses),
                E("mini", "Mini", Crew, true, () => RoleInfo.mini.color, () => CustomOptionHolder.modifierMini),
                E("vip", "VIP", Crew, true, () => RoleInfo.vip.color, () => CustomOptionHolder.modifierVip),
                E("invert", "Invert", Crew, true, () => RoleInfo.invert.color, () => CustomOptionHolder.modifierInvert),
                E("chameleon", "Chameleon", Crew, true, () => RoleInfo.chameleon.color, () => CustomOptionHolder.modifierChameleon),
                E("armored", "Armored", Crew, true, () => RoleInfo.armored.color, () => CustomOptionHolder.modifierArmored),
                E("shifter", "Shifter", Crew, true, () => RoleInfo.shifter.color, () => CustomOptionHolder.modifierShifter),
            };
            return entries;
        }

        private static TORHelpEntry E(string field, string name, int faction, Func<Color> color, Func<CustomOption> rate)
            => E(field, name, faction, false, color, rate);

        private static TORHelpEntry E(string field, string name, int faction, bool isModifier, Func<Color> color, Func<CustomOption> rate)
            => new TORHelpEntry("tor.help." + field, name, faction, isModifier, color, rate);
    }
}
