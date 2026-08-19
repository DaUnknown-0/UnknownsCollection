// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCOptionColors - publishes "role name -> colour" so the settings list (F1 overlay and the lobby
 * text) can print this mod's roles in their own colour instead of white.
 *
 * WHY THIS EXISTS
 * The Other Roles colours a role in that list by baking a <color> tag into the option NAME
 * (`cs(Sheriff.color, "Sheriff")`, CustomOptionHolder.cs:595). This mod deliberately does NOT do
 * that: the option name is also what the settings menu, the web config, the settings share and the
 * localization layer read, and a markup tag in there would have to be stripped again by every one
 * of them. So the colour travels on its own channel instead, and the option names stay plain text.
 *
 * THE CHANNEL
 * AppDomain key "UTS.OptionColors" holds a Dictionary<string,string>. Each role is written twice:
 *   "id:1400" -> "FF1919"   the option ID, which nothing ever renames
 *   "Tesla"   -> "FF1919"   the plain option name, for any reader that only knows the text
 * The ID entry is the one that matters. UCLocalization rewrites option names on every language
 * switch, so a name-only table would go blank the moment a player leaves English.
 *
 * Useful TOR Stuff's SettingsOverlayView reads the table while it renders; a mod that finds the key
 * missing simply creates it. Neither side needs the other to be installed: without UTS nobody reads
 * the table, and without this file UTS falls back to the faction colour.
 *
 * Roles whose option is not a spawn rate of their own (the Hunter, whose options hang under the
 * Werewolf) are still listed by name: an entry that is never looked up costs nothing, and it keeps
 * this table a complete picture of the mod's roles.
 *
 * Register() must run AFTER every CreateOptions() so the SpawnRate options it reads the IDs from
 * already exist.
 */

using System;
using System.Collections.Generic;
using TheOtherRoles;
using UnityEngine;

namespace UnknownsCollection {

    public static class UCOptionColors {

        public const string AppKeyOptionColors = "UTS.OptionColors";

        public static void Register() {
            try {
                var table = AppDomain.CurrentDomain.GetData(AppKeyOptionColors) as Dictionary<string, string>;
                if (table == null) {
                    table = new Dictionary<string, string>();
                    AppDomain.CurrentDomain.SetData(AppKeyOptionColors, table);
                }

                Add(table, "Tesla",       Tesla.Color,       Tesla.SpawnRate);
                Add(table, "Saboteur",    Saboteur.Color,    Saboteur.SpawnRate);
                Add(table, "Silencer",    Silencer.Color,    Silencer.SpawnRate);
                Add(table, "Siphoner",    Siphoner.Color,    Siphoner.SpawnRate);
                Add(table, "Witness",     Witness.Color,     Witness.SpawnRate);
                Add(table, "Poisoner",    Poisoner.Color,    Poisoner.SpawnRate);
                Add(table, "Illusionist", Illusionist.Color, Illusionist.SpawnRate);
                Add(table, "Bug",         Bug.Color,         Bug.SpawnRate);
                Add(table, "Maniac",      Maniac.Color,      Maniac.SpawnRate);
                Add(table, "Follower",    Follower.Color,    Follower.SpawnRate);
                Add(table, "Shade",       Shade.Color,       Shade.SpawnRate);
                Add(table, "Copycat",     Copycat.Color,     Copycat.SpawnRate);
                Add(table, "Scout",       Scout.Color,       Scout.SpawnRate);
                Add(table, "Beacon",      Beacon.Color,      Beacon.SpawnRate);
                Add(table, "Poltergeist", Poltergeist.Color, Poltergeist.SpawnRate);
                Add(table, "Collector",   Collector.Color,   Collector.SpawnRate);
                Add(table, "Manipulator", Manipulator.Color, Manipulator.SpawnRate);
                Add(table, "Werewolf",    Werewolf.Color,    Werewolf.SpawnRate);
                Add(table, "Hunter",      Hunter.Color,      null); // options hang under the Werewolf
                Add(table, "Pelican",     Pelican.Color,     Pelican.SpawnRate);
                Add(table, "Auditor",     Auditor.Color,     Auditor.SpawnRate);
                Add(table, "Gambler",     Gambler.Color,     Gambler.SpawnRate);
                Add(table, "Necromancer", Necromancer.Color, Necromancer.SpawnRate);

                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[UCOptionColors] published {table.Count} option colour entries.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCOptionColors] Register failed: {e}");
            }
        }

        private static void Add(Dictionary<string, string> table, string optionName, Color color,
                                CustomOption option) {
            string hex = $"{ToByte(color.r):X2}{ToByte(color.g):X2}{ToByte(color.b):X2}";
            table[optionName] = hex;
            if (option != null) table["id:" + option.id] = hex;
        }

        private static byte ToByte(float f) => (byte)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);
    }
}
