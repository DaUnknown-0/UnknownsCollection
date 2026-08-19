// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Unknown's Collection - a separate BepInEx plugin that layers brand-new "roles" on top of
 * The Other Roles WITHOUT modifying TOR's source. Like the Revenger in "Useful TOR Stuff",
 * each role is built from Harmony patches: own RoleInfo (display tag), CustomButton/meeting UI,
 * a small custom RPC, and host-authoritative game logic.
 *
 * Roles:
 *  - The Tesla (Impostor) - charges two players (+ / -); a hidden countdown drains while the pair is
 *    too close and only refills in meetings; at zero both die. See Tesla.cs.
 *  - The Saboteur (Impostor) - once per round sabotages a task console (lethal on completion, with a
 *    crew search/defuse counterplay) or lays an invisible stun trap. See Saboteur.cs.
 */

global using Il2CppInterop.Runtime;
global using Il2CppInterop.Runtime.Attributes;
global using Il2CppInterop.Runtime.InteropTypes;
global using Il2CppInterop.Runtime.InteropTypes.Arrays;
global using Il2CppInterop.Runtime.Injection;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnknownsCollection;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("Among Us.exe")]
[BepInDependency("me.eisbison.theotherroles", BepInDependency.DependencyFlags.HardDependency)]
public class UnknownsCollectionPlugin : BasePlugin
{
    public const string PluginGuid = "com.tormod.unknownscollection";
    public const string PluginName = "Unknown's Collection";
    public const string PluginVersion = "1.2.2.4";
    public static readonly System.Version Version = System.Version.Parse(PluginVersion);

    // MODULE BYTES, not callIds (since the RPC consolidation).
    //
    // All of Unknown's Collection now speaks over ONE custom callId - UCRpc.CallId = 230 - and the
    // byte below is written directly after it to say WHICH module a message belongs to (see UCRpc.cs
    // for the rationale). The values are the historical per-module callIds and were kept unchanged
    // so logs, comments and ID-Registry.md still line up; they no longer occupy anything in TOR's
    // callId space, they only have to be unique WITHIN this mod.
    //
    // The block currently in use is 190-214.
    //
    // Consequence: only ONE byte (230) has to stay free globally instead of 18. TOR's CustomRPC enum
    // currently runs 100-183 and keeps growing; the watchdog in Load() below shouts if it ever gets
    // close to our channel. Other DaUnknown mods: 104/105/139/167 (TOR refs), 200-202/250-251
    // (ChanceMod), 240 + 244-254 (Useful TOR Stuff).
    public const byte TeslaRpcId = 190;
    public const byte VersionHandshakeRpcId = 191;
    public const byte SaboteurRpcId = 192;
    public const byte PoisonerRpcId = 193;
    public const byte SilencerRpcId = 194;
    public const byte IllusionistRpcId = 195;
    public const byte SiphonerRpcId = 196;
    public const byte WitnessRpcId = 197;
    public const byte BugRpcId = 198;
    public const byte ManiacRpcId = 199;
    public const byte FollowerRpcId = 207; // 200 conflicts with another DaUnknown mod's reserved range
    public const byte ShadeRpcId = 205;   // 201 conflicts with Chance.ChaosRpcId
    public const byte CopycatRpcId = 206; // 202 conflicts with Chance.ChaosModifierClearRpcId
    public const byte ScoutRpcId = 203;
    public const byte BeaconRpcId = 204;
    public const byte PoltergeistRpcId = 208;
    public const byte CollectorRpcId = 209;
    public const byte ManipulatorRpcId = 210;
    public const byte WerewolfRpcId = 211;
    public const byte PelicanRpcId = 212;
    public const byte PlayerTuningRpcId = 213; // host tooling: per-player speed/cooldown/vent/tasks
    public const byte AuditorRpcId = 214;
    public const byte GamblerRpcId = 215;  // crew MODIFIER, no draft entry (rides on top of a role)
    public const byte NecromancerRpcId = 216;  // neutral; raises corpses (Sub 0 set, Sub 1 raise)

    public static ManualLogSource Logger { get; private set; }
    public static ConfigEntry<bool> BugGlitchEnabled { get; set; }
    public static ConfigEntry<bool> ButtonPulseEnabled { get; set; }
    public static ConfigEntry<bool> MusicWerewolf { get; set; }
    public static ConfigEntry<bool> MusicPelican { get; set; }
    public static ConfigEntry<bool> MusicReactor { get; set; }
    public static ConfigEntry<bool> HelpMenuGerman { get; set; }
    public static ConfigEntry<bool> KillAnimationsUC { get; set; }
    public static ConfigEntry<bool> KillAnimationsTOR { get; set; }
    public static ConfigEntry<string> PreviousHatBeforeLock { get; set; }

    internal static Assembly TORAssembly;

    public override void Load()
    {
        Logger = Log;
        Logger.LogInfo($"{PluginName} v{PluginVersion} loading...");

        var enabled = Config.Bind("General", "Enabled", true, "Enable this mod");
        if (!enabled.Value) {
            // Register in the Mod Manager EVEN WHEN DISABLED, then stop. Without this the mod
            // vanishes from the manager's list the moment someone switches it off - and the only
            // switch to turn it back on lives in exactly that list, so the mod could never be
            // re-enabled from inside the game (chicken and egg; it took editing the .cfg by hand).
            // Nothing else runs: no patches, no options, no RPC channel.
            RegisterInModManager(enabled);
            Logger.LogInfo($"{PluginName} is disabled in config - skipping load (still listed in the Mod Manager).");
            return;
        }

        TORAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");

        var harmony = new Harmony(PluginGuid);

        // Collision watchdog for our single custom callId (see UCRpc.cs). TOR's CustomRPC enum grows
        // with every release; if it ever reaches our channel (or Useful TOR Stuff's), the two mods
        // would silently mis-parse each other's payloads. Reflection-only, log-only, once per start.
        WarnOnRpcIdCollisions();

        // Mod-presence handshake receiver on the shared UC channel. It has no TryPatch (all its
        // patches are attribute-based), so the registration happens here - and BEFORE the roles, so
        // a very early lobby broadcast can never find the channel unregistered.
        TeslaVersionHandshake.RegisterRpc();

        // The Tesla role. CreateOptions must run after TOR's CustomOptionHolder.Load() (guaranteed
        // by the hard dependency). Most patches are attribute-based and picked up by PatchAll below;
        // TryPatch adds the reflection patch on TOR's resetVariables + resolves the UncheckedMurder RPC.
        Tesla.CreateOptions();
        Tesla.TryPatch(harmony);

        // The Saboteur role (Impostor). Same construction as the Tesla: options after TOR's
        // CustomOptionHolder.Load(), reflection setup in TryPatch, attribute patches via PatchAll.
        Saboteur.CreateOptions();
        Saboteur.TryPatch(harmony);

        // The Silencer role (Impostor). Mutes a marked player (vote + chat) for the next meeting.
        Silencer.CreateOptions();
        Silencer.TryPatch(harmony);

        // The Siphoner role (Crewmate). Drains a nearby Impostor's kill cooldown (host-authoritative).
        Siphoner.CreateOptions();
        Siphoner.TryPatch(harmony);

        // The Witness role (Crewmate). Sole-witness sighting -> red name, body-report reveal, anon notes.
        Witness.CreateOptions();
        Witness.TryPatch(harmony);

        // The Poisoner role (Impostor). Kills poison bodies; reporter dies after X meetings unless saved by Medic.
        Poisoner.CreateOptions();
        Poisoner.TryPatch(harmony);

        // The Illusionist role (Impostor). Records a path and replays it as an unkillable shielded clone.
        Illusionist.CreateOptions();
        Illusionist.TryPatch(harmony);

        // Per-player settings (config file + in-game toggle via UC Options menu).
        BugGlitchEnabled = Config.Bind("Bug", "Bug Win Glitch Effects", true,
            "Enable visual/sound glitch effects on the Bug win screen");
        ButtonPulseEnabled = Config.Bind("Buttons", "Button Ready Pulse", false,
            "Gently pulse ability buttons in size while the ability is usable (the animated icons are unaffected). Off by default - some players find the size wobble distracting.");
        // Custom kill cutscenes (pure local cosmetics -> per-player config, NOT host-synced).
        // UC roles keep their overlays by default; the TOR-role pack is opt-in.
        KillAnimationsUC = Config.Bind("KillAnimations", "UC Role Kill Animations", true,
            "Custom kill cutscenes for Unknown's Collection roles (Tesla, Saboteur task kills, Poisoner, Shade, Maniac bomb). Off = vanilla kill overlay.");
        KillAnimationsTOR = Config.Bind("KillAnimations", "TOR Role Kill Animations", false,
            "Custom kill cutscenes for TOR roles with special kills (Sheriff, Vampire, Warlock, Witch, Ninja, Bomber, Guesser, Thief, Jackal/Sidekick, Bounty Hunter). Off = vanilla kill overlay.");
        HelpMenuGerman = Config.Bind("HelpMenu", "German", true,
            "Language of the in-game '?' role help menu (true = Deutsch, false = English). Also toggleable from the menu itself.");
        // Music beds (UCMusic channel). Purely local taste, like the kill cutscenes above - a player
        // who mutes them still sees every gameplay effect, so these are NOT host-synced. The reactor
        // score additionally has a host option (1483) that decides whether it exists in the round at
        // all; this switch only decides whether THIS client hears it.
        MusicWerewolf = Config.Bind("Music", "Werewolf Form Music", true,
            "Play the wolf-form music while the werewolf is transformed.");
        MusicPelican = Config.Bind("Music", "Pelican Hunt Music", true,
            "Play the hunt music during the pelican's final chase.");
        MusicReactor = Config.Bind("Music", "Reactor Music", true,
            "Play the reactor/seismic sabotage score (only ever heard if the host enabled it in the game options).");
        // Bookkeeping for the role-costume hat locks (UCHats.TickHatLock): the last hat this player
        // wore that is NOT a role costume (Werewolf / Monster Hunter), so a lobby that locks one can
        // swap it back - even across a game restart. Written by the mod, not meant to be edited by
        // hand. The config KEY still says "Werewolf" so an existing entry keeps working.
        PreviousHatBeforeLock = Config.Bind("Cosmetics", "Last Hat Before Werewolf", "",
            "Internal: the hat worn before a role-costume hat, restored while that role is enabled.");

        // The Bug role (Neutral). Survive until the end and win with the winning team.
        Bug.CreateOptions();
        Bug.TryPatch(harmony);

        // The Maniac role (Impostor). Plant a bomb on a player that can be passed before detonation.
        Maniac.CreateOptions();
        Maniac.TryPatch(harmony);

        // The Follower role (Neutral). Copy the role of the first player to die.
        Follower.CreateOptions();
        Follower.TryPatch(harmony);

        // The Shade role (Impostor). Victim's body disappears; others find it by proximity.
        Shade.CreateOptions();
        Shade.TryPatch(harmony);

        // The Copycat role (Neutral). Copies witnessed abilities, wins with winning team if alive.
        Copycat.CreateOptions();
        Copycat.TryPatch(harmony);

        // The Scout role (Crewmate). Goes transparent and fast; lights don't affect during ability.
        Scout.CreateOptions();
        Scout.TryPatch(harmony);

        // The Beacon role (Crewmate). Lights never affect them; nearby crew share their vision.
        Beacon.CreateOptions();
        Beacon.TryPatch(harmony);

        // The Poltergeist (ghost role, keeps its original team). The first player to die rises and
        // haunts: door slams, hexes, a ghost hand on reactor consoles and a manifest disguise.
        Poltergeist.CreateOptions();
        Poltergeist.TryPatch(harmony);

        // The Collector role (Neutral). Collect hidden relics scattered over the map to win alone.
        Collector.CreateOptions();
        Collector.TryPatch(harmony);

        // The Manipulator role (Impostor). Makes the admin table and vitals lie for a while.
        Manipulator.CreateOptions();
        Manipulator.TryPatch(harmony);

        // The Werewolf role (Impostor). As the last living Impostor he can charge up in the dark and
        // turn into a beast: unfixable darkness, flashlight vision for everyone else, and silver as
        // his only weakness.
        Werewolf.CreateOptions();
        Werewolf.TryPatch(harmony);

        // The Hunter (Paket W2) - not a spawnable role but the Sheriff's ENDGAME: once every
        // non-Werewolf Impostor is dead and the beast is still alive, the Sheriff is promoted into a
        // silver-armed hunter. Its options hang off the Werewolf spawn rate, so CreateOptions must run
        // AFTER Werewolf.CreateOptions() above.
        Hunter.CreateOptions();
        Hunter.TryPatch(harmony);

        // The Pelican (Paket W3) - a SOLO NEUTRAL with his own win condition: he swallows his victims
        // instead of killing them (no corpse until a meeting digests them or his own death frees them)
        // and wins as the last survivor. Once only he and one other player are left, the hunt starts:
        // a public countdown, no meetings, no reports, no vents, no abilities - eat or lose.
        Pelican.CreateOptions();
        Pelican.TryPatch(harmony);

        // The Auditor (Impostor). Every task a living crewmate finishes lands in his own task list;
        // doing it himself resets that exact task for that exact crewmate (server-authoritative, so
        // the task bar really drops). His kill cooldown scales with the number of reverts.
        Auditor.CreateOptions();
        Auditor.TryPatch(harmony);

        // The Gambler (crew MODIFIER, the first one in this mod). Predicts what the round will do;
        // every bet is settled inside a meeting so a win never leaks information early. Tier decides
        // the stake: speed, own tasks, and at the top the impostors' kill cooldown.
        Gambler.CreateOptions();
        Gambler.TryPatch(harmony);

        // The Necromancer (Neutral): raises fresh corpses into a silent army - thralls look alive,
        // vote with weight zero, cannot guess; he wins when enough of "the living" are his. Dies he,
        // dies the army. Mutually exclusive with the Poltergeist (option), see Necromancer.cs.
        Necromancer.CreateOptions();
        Necromancer.TryPatch(harmony);

        // Reactor music (Paket R) - not a role: a score for the reactor/seismic sabotage that is
        // written against the REAL ICriticalSabotage countdown, so the blast in its finale lands on
        // the explosion. Off by default; runs on the UCMusic channel at the mod's highest priority.
        ReactorMusic.CreateOptions();
        ReactorMusic.TryPatch(harmony);

        // PlayerTuning - remote-control surface for host tooling (per-player speed/cooldown/vent
        // ban/task replacement, module byte 213). No options, no own game logic; the Harmony
        // patches are attribute-based and come in via PatchAll below.
        PlayerTuning.TryPatch(harmony);

        // Publish this mod's role colours (by option ID and by name) so the settings list can print
        // them in colour instead of white. Must run after every CreateOptions above (it reads the
        // SpawnRate IDs) and before UCLocalization below, which rewrites the option names.
        UCOptionColors.Register();

        // Localization: loads the uc.* tables and translates UC's RoleInfos + options by
        // matching their pristine English text (follows UTS's UTS.Loc.ActiveCode/Epoch via
        // the poll patches picked up by PatchAll below). Must run AFTER every CreateOptions
        // above so the first-pass originals are complete.
        UCLocalization.Initialize();

        // All attribute-based [HarmonyPatch] classes in this assembly (Tesla patches + handshake +
        // the PingTracker version line + UCOptionsPatch).
        harmony.PatchAll(typeof(UnknownsCollectionPlugin).Assembly);

        // Reflection-based patch (internal TOR type): inject the Tesla/Saboteur spawn rates into the
        // Role Draft so the draft respects their configured rate + 100% force.
        UCRoleDraft.PatchDraftData(harmony);

        // Self-updater: checks GitHub releases and offers an in-game update (channel-aware: follows the
        // shared test-versions toggle). Must exist before registration so the repo fields resolve.
        AddComponent<UnknownsCollectionUpdater>();

        // Register in the shared Mod Manager registry (cross-plugin, via AppDomain - no hard reference
        // to Useful TOR Stuff). Mirrors how ForceImpostorMod registers itself.
        RegisterInModManager(enabled);

        // Cosmetic button animations (flipbook icons + ready-pulse). Init() only forces the
        // static ctor so its UCFx tick/reset registration happens before the first round.
        UCButtonAnim.Init();

        // Custom role kill overlays (Tesla/Saboteur-task/Poisoner/Shade/Maniac). Same pattern.
        UCKillOverlay.Init();

        // Trap cleanup registration (frees trap-frozen players on round reset AND game end).
        SaboteurTrap.Init();

        // Own custom hats ("Virus", "Werbetafel", full-body "Werewolf"). Extracts the PNGs into
        // TOR's TheOtherHats folder and registers them through reflection - TOR itself stays
        // untouched (see UCHats.cs). Purely cosmetic, deliberately NOT gated on the mod handshake.
        UCHats.TryPatch(harmony);

        Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
    }

    // Reads TOR's internal CustomRPC enum via reflection and warns if TOR ever grew into the byte
    // range our (and the sibling mods') channels live in. Purely diagnostic: nothing is changed, the
    // log line just tells us to move a channel BEFORE players hit the mis-parse in a live round.
    private void WarnOnRpcIdCollisions()
    {
        try {
            var rpcEnum = TORAssembly?.GetType("TheOtherRoles.CustomRPC");
            if (rpcEnum == null || !rpcEnum.IsEnum) {
                Logger.LogWarning("[UCRpc] TOR's CustomRPC enum not found - RPC collision watchdog skipped.");
                return;
            }

            int highest = -1;
            var collisions = new List<string>();
            foreach (var name in Enum.GetNames(rpcEnum)) {
                int value = Convert.ToInt32(Enum.Parse(rpcEnum, name));
                if (value > highest) highest = value;
                // >= 200: TOR has entered the block the DaUnknown mods reserved for themselves.
                // == 230 / == 240: a direct hit on the Unknown's Collection / Useful TOR Stuff channel.
                if (value >= 200 || value == UCRpc.CallId || value == 240)
                    collisions.Add($"{name}={value}");
            }

            if (collisions.Count > 0)
                Logger.LogWarning(
                    "[UCRpc] TOR's CustomRPC now uses ids in the range reserved by the DaUnknown mods: "
                    + string.Join(", ", collisions)
                    + $". Our channel is {UCRpc.CallId} (Useful TOR Stuff uses 240) - move the affected "
                    + "channel before the next release or RPC payloads will be mis-parsed.");

            Logger.LogInfo($"[UCRpc] channel {UCRpc.CallId}; highest TOR CustomRPC id is {highest}.");
        } catch (Exception ex) {
            Logger.LogWarning($"[UCRpc] RPC collision watchdog failed: {ex.Message}");
        }
    }

    private void RegisterInModManager(ConfigEntry<bool> enabled)
    {
        try {
            var modData = new Dictionary<string, object> {
                { "Guid", PluginGuid },
                { "Name", PluginName },
                { "Version", Version },
                { "RepositoryOwner", UnknownsCollectionUpdater.RepositoryOwner },
                { "RepositoryName", UnknownsCollectionUpdater.RepositoryName },
                { "ButtonColor", new Color(0.12f, 0.72f, 1f) }, // electric cyan
                { "Enabled", enabled },
                // False while the mod sits disabled: its patches never ran this session, so a
                // re-enable needs a restart. The manager still shows the entry and its switch.
                { "RuntimeEnabled", enabled.Value }
            };
            AppDomain.CurrentDomain.SetData($"ModManager.RegisteredMod.{PluginGuid}", modData);

            // Append our GUID to the shared manifest so GetAllMods() finds us (we are not hardcoded).
            var manifest = AppDomain.CurrentDomain.GetData("ModManager.Manifest") as List<string>
                           ?? new List<string>();
            if (!manifest.Contains(PluginGuid)) {
                manifest.Add(PluginGuid);
                AppDomain.CurrentDomain.SetData("ModManager.Manifest", manifest);
            }
            Logger.LogInfo("Registered Unknown's Collection in the Mod Manager registry + manifest.");
        } catch (Exception ex) {
            Logger.LogError($"Failed to register Unknown's Collection in Mod Manager: {ex}");
        }
    }

    // PingTracker version line (top corner). Uses the shared vX.Y.Z(.W) formatter so a CI test build
    // (vX.Y.Z.W tag) shows its test number when the shared toggle is on, and a stable build shows vX.Y.Z.
    [HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
    [HarmonyPriority(Priority.Low)] // after TOR's own PingTracker postfix
    public static class VersionDisplayPatch
    {
        // The mod name, localized - the click-driven letter-decode animation this line used to play
        // on its own click is retired along with the rest of the old per-mod click handling below;
        // UnknownsCollective.Render() now owns the click entirely (toggle/expand + shared credit).
        private static string ModName => UCLocalization.Tr("uc.ui.modname");

        public static void Postfix(PingTracker __instance)
        {
            if (__instance == null || __instance.text == null) return;
            string text = __instance.text.text;
            if (string.IsNullOrEmpty(text)) return;

            string line = $"<color=#1FB8FF>{ModName}</color> v{VersionDisplay.FormatRich(UnknownsCollectionPlugin.Version)}";
            UnknownsCollective.Contribute(UnknownsCollectionPlugin.PluginGuid, line);
            text = UnknownsCollective.Render(__instance.text, text);

            __instance.text.text = text;
        }
    }
}
