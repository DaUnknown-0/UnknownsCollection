// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Mixer (Crewmate), name not final (User 2026-10-02)
 *
 * The Mixer walks up to a player and MIXES him: after the next meeting that player gets another,
 * not assigned role at random. At most 3 mixes per game.
 *
 *  - SAME TEAM (User): crew stays crew, an Impostor gets another Impostor role, a neutral another
 *    neutral role. The pool is every role of that team that is switched on in the lobby (spawn rate
 *    above 0) and held by nobody, alive or dead, minus roles that only work when set up at the round
 *    start (Lawyer, Sidekick, Spy, Deputy, the Guessers; UC's Werewolf, Pelican, Necromancer,
 *    Stalker, King, Collector, Follower) and minus Maniac and other roles with live objects that a
 *    swap cannot carry. An empty pool gives the mix back (User: "Mix nicht verbraucht").
 *  - THE MEETING: the mixed player, if alive, is told in the next meeting that he was mixed. If he is
 *    evil (Impostor or neutral) and not a Guesser already, he gets ONE revenge guess on the Mixer:
 *    click a player twice. Right, the Mixer dies; wrong, he dies himself. A Guesser simply keeps his
 *    ordinary shots (User).
 *  - A KILLER (every killing evil: Impostors, Jackal, Sidekick, Thief, Arsonist, Pelican, Stalker -
 *    User: "alle toetenden Boesen"): after the meeting the Mixer learns the killer's OLD role, and his
 *    mixes are used up. The killer is still mixed (if a role is free).
 *  - THE SWAP (host, after the exile): TOR roles via erasePlayerRoles + the scrub of leftover statics
 *    + setRole, all inside one UC message so every client does the same; UC roles through their own
 *    SendSet (255 takes one away, Role Control's way). Modifiers stay.
 *  - The revenge shot runs through TOR's guesserShoot (deaths, votes, lovers, chat for ghosts), with
 *    the Guesser shot counters saved and restored around it: TOR takes the shot off the Evil
 *    Guesser when the shooter is no Guesser.
 *
 * Crew tag over a plain Crewmate. Options 1769-1773, RPC module 232, draft sentinel 226.
 * See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Mixer {
        // a blender's turquoise
        public static readonly Color Color = new Color(0.20f, 0.82f, 0.78f);

        // ---- Options (IDs 1769-1773) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption Cooldown;
        public static CustomOption Mixes;
        public static CustomOption RevengeGuess;

        // ---- Runtime state ----
        public static PlayerControl mixer;
        public static bool active;
        private static int mixesLeft;
        private static PlayerControl currentTarget;
        private static CustomButton mixButton;
        // host: target -> pending (decided at the meeting end)
        private static readonly HashSet<byte> pending = new HashSet<byte>();
        // every client: the revenge shot this client may fire in the running meeting
        private static bool revengeArmed;
        private static byte revengeMark = byte.MaxValue;
        // host: who holds a revenge shot in the running meeting
        private static readonly HashSet<byte> revengeHolders = new HashSet<byte>();

        private const byte RpcId = UnknownsCollectionPlugin.MixerRpcId;
        private const byte SubSet = 0;       // mixerId                                  host -> everyone
        private const byte SubMix = 1;       // targetId                                 Mixer -> host
        private const byte SubNotify = 2;    // targetId, revenge bool                   host -> everyone (target reads it)
        private const byte SubRevenge = 3;   // guessedId                                target -> host
        private const byte SubShoot = 4;     // shooterId, dyingId, guessedId            host -> everyone
        private const byte SubTorSwap = 5;   // targetId, torRole (255 = none)           host -> everyone
        private const byte SubResult = 6;    // targetId, kind, mixesLeft, string        host -> everyone (Mixer + target read it)

        private const byte ResultRefund = 0, ResultKiller = 1, ResultNewRole = 2;
        // A refused mix: only puts the Mixer's own counter back to the host's (he counted down before
        // sending). No text; an older client just takes the count (reviews 2026-10-02).
        private const byte ResultSync = 3;

        private static RoleInfo info;
        public static RoleInfo MixerInfo() => info ??= new RoleInfo(
            "Mixer", Color, "Mix a player: after the next meeting they get another role of their team",
            "Mix a player's role", RoleId.Crewmate);

        private static readonly System.Random rnd = new System.Random();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1769, Types.Crewmate, "Mixer", CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1770, Types.Crewmate, "Mixer Minimum Players To Spawn", 6f, 4f, 15f, 1f, SpawnRate);
                Cooldown = CustomOption.Create(1771, Types.Crewmate, "Mix Cooldown", 30f, 10f, 60f, 2.5f, SpawnRate);
                Mixes = CustomOption.Create(1772, Types.Crewmate, "Mixes Per Game", 3f, 1f, 3f, 1f, SpawnRate);
                RevengeGuess = CustomOption.Create(1773, Types.Crewmate, "An Evil Mixed Player Gets A Revenge Guess", true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Mixer] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) => UCRpc.Register(RpcId, HandleModuleRpc);

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalMixer() =>
            active && mixer != null && PlayerControl.LocalPlayer != null && mixer.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static bool Alive(PlayerControl p) => p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        // ====================================================================
        // The role table
        // ====================================================================
        private enum Team { Crew, Impostor, Neutral }

        private sealed class PoolRole {
            public string Name;
            public Team Team;
            public RoleId Tor = RoleId.Crewmate;      // TOR role, or Crewmate for a UC role
            public Func<CustomOption> Rate;
            public Func<PlayerControl> Holder;        // UC: the role's static; TOR: null (RoleInfo decides)
            public Action<byte> UcSet;                // UC: SendSet (255 = take it away)
            public Action UcResidue;                  // UC: what SendSet(255) leaves lying around
            public bool InPool = true;                // false: known for team/killer/clearing, never handed out
        }

        private static List<PoolRole> table;

        private static PoolRole Tor(RoleId id, Team team, Func<CustomOption> rate, bool inPool = true) =>
            new PoolRole { Name = TorName(id), Team = team, Tor = id, Rate = rate, InPool = inPool };

        private static PoolRole Uc(string name, Team team, Func<CustomOption> rate, Func<PlayerControl> holder, Action<byte> set,
                                   Action residue = null, bool inPool = true) =>
            new PoolRole { Name = name, Team = team, Rate = rate, Holder = holder, UcSet = set, UcResidue = residue, InPool = inPool };

        private static string TorName(RoleId id) {
            try { if (RoleInfo.roleInfoById.TryGetValue(id, out var ri) && ri != null) return ri.name; } catch { }
            return id.ToString();
        }

        private static List<PoolRole> Table() {
            if (table != null) return table;
            table = new List<PoolRole> {
                // TOR crew
                Tor(RoleId.Mayor, Team.Crew, () => CustomOptionHolder.mayorSpawnRate),
                Tor(RoleId.Portalmaker, Team.Crew, () => CustomOptionHolder.portalmakerSpawnRate),
                Tor(RoleId.Engineer, Team.Crew, () => CustomOptionHolder.engineerSpawnRate),
                Tor(RoleId.Lighter, Team.Crew, () => CustomOptionHolder.lighterSpawnRate),
                Tor(RoleId.Detective, Team.Crew, () => CustomOptionHolder.detectiveSpawnRate),
                Tor(RoleId.TimeMaster, Team.Crew, () => CustomOptionHolder.timeMasterSpawnRate),
                Tor(RoleId.Medic, Team.Crew, () => CustomOptionHolder.medicSpawnRate),
                Tor(RoleId.Swapper, Team.Crew, () => CustomOptionHolder.swapperSpawnRate),
                Tor(RoleId.Seer, Team.Crew, () => CustomOptionHolder.seerSpawnRate),
                Tor(RoleId.Hacker, Team.Crew, () => CustomOptionHolder.hackerSpawnRate),
                Tor(RoleId.Tracker, Team.Crew, () => CustomOptionHolder.trackerSpawnRate),
                Tor(RoleId.Snitch, Team.Crew, () => CustomOptionHolder.snitchSpawnRate),
                Tor(RoleId.Medium, Team.Crew, () => CustomOptionHolder.mediumSpawnRate),
                Tor(RoleId.Trapper, Team.Crew, () => CustomOptionHolder.trapperSpawnRate),
                Tor(RoleId.SecurityGuard, Team.Crew, () => CustomOptionHolder.securityGuardSpawnRate),
                Tor(RoleId.Sheriff, Team.Crew, () => CustomOptionHolder.sheriffSpawnRate),
                Tor(RoleId.Spy, Team.Crew, () => CustomOptionHolder.spySpawnRate, inPool: false),
                Tor(RoleId.Deputy, Team.Crew, () => CustomOptionHolder.deputySpawnRate, inPool: false),
                Tor(RoleId.NiceGuesser, Team.Crew, () => CustomOptionHolder.guesserSpawnRate, inPool: false),
                // TOR impostors
                Tor(RoleId.Morphling, Team.Impostor, () => CustomOptionHolder.morphlingSpawnRate),
                Tor(RoleId.Camouflager, Team.Impostor, () => CustomOptionHolder.camouflagerSpawnRate),
                Tor(RoleId.Vampire, Team.Impostor, () => CustomOptionHolder.vampireSpawnRate),
                Tor(RoleId.Eraser, Team.Impostor, () => CustomOptionHolder.eraserSpawnRate),
                Tor(RoleId.Trickster, Team.Impostor, () => CustomOptionHolder.tricksterSpawnRate),
                Tor(RoleId.Cleaner, Team.Impostor, () => CustomOptionHolder.cleanerSpawnRate),
                Tor(RoleId.Warlock, Team.Impostor, () => CustomOptionHolder.warlockSpawnRate),
                Tor(RoleId.BountyHunter, Team.Impostor, () => CustomOptionHolder.bountyHunterSpawnRate),
                Tor(RoleId.Witch, Team.Impostor, () => CustomOptionHolder.witchSpawnRate),
                Tor(RoleId.Ninja, Team.Impostor, () => CustomOptionHolder.ninjaSpawnRate),
                Tor(RoleId.Bomber, Team.Impostor, () => CustomOptionHolder.bomberSpawnRate),
                Tor(RoleId.Yoyo, Team.Impostor, () => CustomOptionHolder.yoyoSpawnRate),
                Tor(RoleId.EvilGuesser, Team.Impostor, () => CustomOptionHolder.guesserSpawnRate, inPool: false),
                // TOR neutrals
                Tor(RoleId.Jester, Team.Neutral, () => CustomOptionHolder.jesterSpawnRate),
                Tor(RoleId.Arsonist, Team.Neutral, () => CustomOptionHolder.arsonistSpawnRate),
                Tor(RoleId.Vulture, Team.Neutral, () => CustomOptionHolder.vultureSpawnRate),
                Tor(RoleId.Thief, Team.Neutral, () => CustomOptionHolder.thiefSpawnRate),
                Tor(RoleId.Jackal, Team.Neutral, () => CustomOptionHolder.jackalSpawnRate),
                Tor(RoleId.Sidekick, Team.Neutral, () => CustomOptionHolder.jackalSpawnRate, inPool: false),
                Tor(RoleId.Lawyer, Team.Neutral, () => CustomOptionHolder.lawyerSpawnRate, inPool: false),
                Tor(RoleId.Prosecutor, Team.Neutral, () => CustomOptionHolder.lawyerSpawnRate, inPool: false),
                Tor(RoleId.Pursuer, Team.Neutral, () => CustomOptionHolder.lawyerSpawnRate, inPool: false),
                // UC crew
                Uc("Paramedic", Team.Crew, () => Paramedic.SpawnRate, () => Paramedic.paramedic, Paramedic.SendSet),
                Uc("Surveyor", Team.Crew, () => Surveyor.SpawnRate, () => Surveyor.surveyor, Surveyor.SendSet),
                Uc("Scout", Team.Crew, () => Scout.SpawnRate, () => Scout.scout, Scout.SendSetScout),
                Uc("Beacon", Team.Crew, () => Beacon.SpawnRate, () => Beacon.beacon, Beacon.SendSetBeacon),
                Uc("Witness", Team.Crew, () => Witness.SpawnRate, () => Witness.witness, Witness.SendSetWitness),
                Uc("Siphoner", Team.Crew, () => Siphoner.SpawnRate, () => Siphoner.siphoner, Siphoner.SendSetSiphoner),
                Uc("King", Team.Crew, () => King.SpawnRate, () => King.king, null, inPool: false),
                Uc("Mixer", Team.Crew, () => SpawnRate, () => mixer, null, inPool: false),
                // UC impostors
                Uc("Tesla", Team.Impostor, () => Tesla.SpawnRate, () => Tesla.tesla, Tesla.SendSetTesla, Tesla.SendClear),
                Uc("Saboteur", Team.Impostor, () => Saboteur.SpawnRate, () => Saboteur.saboteur, Saboteur.SendSetSaboteur,
                   () => { Saboteur.SendClearSabotage(); Saboteur.SendClear(); }),
                Uc("Poisoner", Team.Impostor, () => Poisoner.SpawnRate, () => Poisoner.poisoner, Poisoner.SendSetPoisoner),
                Uc("Silencer", Team.Impostor, () => Silencer.SpawnRate, () => Silencer.silencer, Silencer.SendSetSilencer, Silencer.SendClearSilences),
                Uc("Illusionist", Team.Impostor, () => Illusionist.SpawnRate, () => Illusionist.illusionist, Illusionist.SendSetIllusionist),
                Uc("Shade", Team.Impostor, () => Shade.SpawnRate, () => Shade.shade, Shade.SendSetShade),
                Uc("Manipulator", Team.Impostor, () => Manipulator.SpawnRate, () => Manipulator.manipulator, Manipulator.SendSetManipulator),
                Uc("Hypnotist", Team.Impostor, () => Hypnotist.SpawnRate, () => Hypnotist.hypnotist, Hypnotist.SendSet),
                Uc("Skinwalker", Team.Impostor, () => Skinwalker.SpawnRate, () => Skinwalker.skinwalker, Skinwalker.SendSet),
                Uc("Auditor", Team.Impostor, () => Auditor.SpawnRate, () => Auditor.auditor, Auditor.SendSetAuditor),
                Uc("Cursed Pirate", Team.Impostor, () => CursedPirate.SpawnRate, () => CursedPirate.pirate, CursedPirate.SendSet),
                Uc("Maniac", Team.Impostor, () => Maniac.SpawnRate, () => Maniac.maniac, Maniac.SendSetManiac, Maniac.SendClear, inPool: false),
                Uc("Werewolf", Team.Impostor, () => Werewolf.SpawnRate, () => Werewolf.werewolf, null, inPool: false),
                // UC neutrals
                Uc("Bug", Team.Neutral, () => Bug.SpawnRate, () => Bug.bug, Bug.SendSetBug),
                Uc("Copycat", Team.Neutral, () => Copycat.SpawnRate, () => Copycat.copycat, Copycat.SendSetCopycat),
                Uc("Collector", Team.Neutral, () => Collector.SpawnRate, () => Collector.collector, null, inPool: false),
                Uc("Pelican", Team.Neutral, () => Pelican.SpawnRate, () => Pelican.pelican, null, inPool: false),
                Uc("Necromancer", Team.Neutral, () => Necromancer.SpawnRate, () => Necromancer.necromancer, null, inPool: false),
                Uc("Stalker", Team.Neutral, () => Stalker.SpawnRate, () => Stalker.stalker, null, inPool: false),
                Uc("Follower", Team.Neutral, () => Follower.SpawnRate, () => Follower.follower, null, inPool: false),
            };
            return table;
        }

        // every killing evil (User): the impostors, and these neutrals
        private static readonly HashSet<string> NeutralKillers = new HashSet<string> { "Jackal", "Sidekick", "Thief", "Arsonist", "Pelican", "Stalker" };

        /// <summary>The player's main role: a UC role first (UC tags sit over TOR's plain roles), then TOR's.</summary>
        private static (string Name, Team Team, PoolRole Entry) RoleOf(PlayerControl p) {
            foreach (var r in Table())
                if (r.Holder != null) {
                    PlayerControl h = null;
                    try { h = r.Holder(); } catch { }
                    if (h != null && h.PlayerId == p.PlayerId) return (r.Name, r.Team, r);
                }
            RoleInfo main = null;
            try { main = RoleInfo.getRoleInfoForPlayer(p, false)?.FirstOrDefault(i => i != null && !i.isModifier); } catch { }
            bool imp = p.Data?.Role != null && p.Data.Role.IsImpostor;
            if (main != null) {
                var entry = Table().FirstOrDefault(r => r.Holder == null && r.Tor == main.roleId);
                Team team = imp ? Team.Impostor : main.isNeutral ? Team.Neutral : Team.Crew;
                return (main.name, team, entry);
            }
            return (imp ? "Impostor" : "Crewmate", imp ? Team.Impostor : Team.Crew, null);
        }

        /// <summary>
        /// The UC role a player holds, with the host-side setter (SendSet, 255 takes it away) and its
        /// residue clearer, for features that move a UC role to someone else (UCThiefSteal).
        /// False for TOR roles, for UC roles without a setter (Werewolf, Pelican, ...) and for no role.
        /// </summary>
        internal static bool TryUcRole(byte playerId, out string name, out bool impostor, out Action<byte> set, out Action residue) {
            name = null; impostor = false; set = null; residue = null;
            foreach (var r in Table()) {
                if (r.Holder == null) continue;
                PlayerControl h = null;
                try { h = r.Holder(); } catch { }
                if (h == null || h.PlayerId != playerId) continue;
                name = r.Name; impostor = r.Team == Team.Impostor; set = r.UcSet; residue = r.UcResidue;
                return set != null;
            }
            return false;
        }

        private static bool IsKiller(PlayerControl p, string name, Team team) =>
            team == Team.Impostor || NeutralKillers.Contains(name);

        private static HashSet<string> HeldNames() {
            var held = new HashSet<string>();
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (p == null || p.Data == null) continue;
                held.Add(RoleOf(p).Name);
                try { foreach (var ri in RoleInfo.getRoleInfoForPlayer(p, false)) if (ri != null) held.Add(ri.name); } catch { }
            }
            return held;
        }

        private static List<PoolRole> PoolFor(Team team, string current) {
            var held = HeldNames();
            return Table().Where(r => r.InPool && r.Team == team && r.Name != current && !held.Contains(r.Name)
                                      && (r.Holder != null ? r.UcSet != null : true)
                                      && Rate(r) > 0).ToList();
        }

        private static int Rate(PoolRole r) {
            try { return r.Rate?.Invoke()?.getSelection() ?? 0; } catch { return 0; }
        }

        // ====================================================================
        // RPC
        // ====================================================================
        private static MessageWriter BeginRpc(byte sub) {
            var w = UCRpc.Begin(RpcId);
            w.Write(sub);
            return w;
        }

        public static void SendSet(byte id) {
            try {
                var w = BeginRpc(SubSet);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] SendSet failed: {e}"); }
        }

        private static void SendMix(byte target) {
            try {
                if (AmHost()) { HostHandleMix(mixer != null ? mixer.PlayerId : byte.MaxValue, target); return; }
                var w = BeginRpc(SubMix);
                w.Write(target);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                mixesLeft = Mathf.Max(0, mixesLeft - 1);     // the host confirms or refunds after the meeting
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] SendMix failed: {e}"); }
        }

        private static void Broadcast(byte sub, Action<MessageWriter> body, Action local) {
            try {
                var w = BeginRpc(sub);
                body(w);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] send {sub} failed: {e}"); }
            local();
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                switch (sub) {
                    case SubSet: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Mixer.Set")) ApplySet(id);
                        break;
                    }
                    case SubMix: {
                        byte target = reader.ReadByte();
                        if (!AmHost()) break;
                        if (!UCRpc.RequireOwnerOrHost(mixer, "Mixer.Mix")) break;
                        HostHandleMix(mixer.PlayerId, target);
                        break;
                    }
                    case SubNotify: {
                        byte target = reader.ReadByte();
                        bool revenge = reader.ReadBoolean();
                        if (UCRpc.RequireHost("Mixer.Notify")) ApplyNotify(target, revenge);
                        break;
                    }
                    case SubRevenge: {
                        byte guessed = reader.ReadByte();
                        var sender = UCRpc.Sender;
                        if (!AmHost() || sender == null) break;
                        HostHandleRevenge(sender.PlayerId, guessed);
                        break;
                    }
                    case SubShoot: {
                        byte shooter = reader.ReadByte(), dying = reader.ReadByte(), guessed = reader.ReadByte();
                        if (UCRpc.RequireHost("Mixer.Shoot")) ApplyShoot(shooter, dying, guessed);
                        break;
                    }
                    case SubTorSwap: {
                        byte target = reader.ReadByte(), role = reader.ReadByte();
                        if (UCRpc.RequireHost("Mixer.TorSwap")) ApplyTorSwap(target, role);
                        break;
                    }
                    case SubResult: {
                        byte target = reader.ReadByte(), kind = reader.ReadByte(), left = reader.ReadByte();
                        string text = reader.ReadString();
                        if (UCRpc.RequireHost("Mixer.Result")) ApplyResult(target, kind, left, text);
                        break;
                    }
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] HandleRpc failed: {e}"); }
        }

        private static void ApplySet(byte id) {
            mixer = id == byte.MaxValue ? null : Helpers.playerById(id);
            active = mixer != null;
            mixesLeft = Mathf.RoundToInt(Mixes?.getFloat() ?? 3f);
            if (!active) return;
            UCPromotion.Claim(id);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Mixer] The Mixer is {mixer.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySet(playerId);

        // ====================================================================
        // Host: the mix request
        // ====================================================================
        private static int hostMixesLeft = -1;

        private static void HostHandleMix(byte senderId, byte targetId) {
            string why = null;
            var target = Helpers.playerById(targetId);
            if (hostMixesLeft < 0) hostMixesLeft = Mathf.RoundToInt(Mixes?.getFloat() ?? 3f);
            if (!active || mixer == null || mixer.PlayerId != senderId) why = "not the Mixer";
            else if (!Alive(mixer)) why = "Mixer is dead";
            else if (hostMixesLeft <= 0) why = "no mixes left";
            else if (InMeeting()) why = "meeting running";
            else if (!Alive(target) || target.PlayerId == senderId) why = "bad target";
            else if (pending.Contains(targetId)) why = "already mixed";
            if (why != null) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Mixer] mix of {target?.Data?.PlayerName ?? targetId.ToString()} refused: {why}.");
                // the real Mixer counted this mix down already: give him the host's number back
                if (active && mixer != null && mixer.PlayerId == senderId) {
                    byte left = (byte)Mathf.Clamp(hostMixesLeft, 0, 255);
                    Broadcast(SubResult, w => { w.Write(targetId); w.Write(ResultSync); w.Write(left); w.Write(""); },
                              () => ApplyResult(targetId, ResultSync, left, ""));
                }
                return;
            }
            hostMixesLeft--;
            if (IsLocalMixer()) mixesLeft = hostMixesLeft;
            pending.Add(targetId);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Mixer] {target.Data.PlayerName} mixed, effective after the next meeting ({hostMixesLeft} left).");
        }

        // ====================================================================
        // The meeting: tell the mixed, arm the revenge shot
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(MeetingHud __instance) {
                try {
                    revengeArmed = false;
                    revengeMark = byte.MaxValue;
                    revengeButtons.Clear();
                    if (!AmHost() || pending.Count == 0) return;
                    revengeHolders.Clear();
                    foreach (byte id in pending.ToList()) {
                        var p = Helpers.playerById(id);
                        if (!Alive(p)) continue;
                        var role = RoleOf(p);
                        bool revenge = (RevengeGuess?.getBool() ?? true) && role.Team != Team.Crew && !HandleGuesser.isGuesser(id) && Alive(mixer);
                        if (revenge) revengeHolders.Add(id);
                        byte bid = id;
                        Broadcast(SubNotify, w => { w.Write(bid); w.Write(revenge); }, () => ApplyNotify(bid, revenge));
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] meeting start failed: {e}");
                }
            }
        }

        private static void ApplyNotify(byte target, bool revenge) {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null || lp.PlayerId != target) return;
            Helpers.showFlash(Color, 1.5f, UCLocalization.Tr("uc.ui.mixer.mixed"));
            Say(UCLocalization.Tr("uc.ui.mixer.mixed"));
            if (!revenge) return;
            revengeArmed = true;
            Say(UCLocalization.Tr("uc.ui.mixer.revenge"));
            BuildRevengeButtons();
        }

        private static void Say(string text) {
            try { HudManager.Instance?.Chat?.AddChat(PlayerControl.LocalPlayer, text); } catch { }
        }

        // ---- the revenge buttons (only on the mixed player's screen) ----
        private static readonly Dictionary<byte, SpriteRenderer> revengeButtons = new Dictionary<byte, SpriteRenderer>();
        private static readonly Color Marked = new Color(1f, 0.3f, 0.3f);

        private static void BuildRevengeButtons() {
            var hud = MeetingHud.Instance;
            if (hud == null) return;
            revengeButtons.Clear();
            var lp = PlayerControl.LocalPlayer;
            // beside the Hypnotist's icons if this player happens to hold them (-0.95)
            float x = Hypnotist.IsLocalHypnotist() ? -0.5f : -0.95f;
            var sprite = HandleGuesser.getTargetSprite();
            foreach (var pva in hud.playerStates) {
                if (pva == null || pva.AmDead || pva.TargetPlayerId == lp.PlayerId) continue;
                var pc = Helpers.playerById(pva.TargetPlayerId);
                if (!Alive(pc)) continue;
                var templateTr = pva.Buttons != null ? pva.Buttons.transform.Find("CancelButton") : null;
                if (templateTr == null) continue;
                var go = UnityEngine.Object.Instantiate(templateTr.gameObject, pva.transform);
                go.name = "MixerRevenge";
                go.transform.localPosition = new Vector3(x, 0.03f, -1.3f);
                var r = go.GetComponent<SpriteRenderer>();
                float templateWidth = r.sprite != null ? r.sprite.bounds.size.x : 0f;
                if (sprite != null) r.sprite = sprite;
                if (sprite != null && templateWidth > 0.01f && sprite.bounds.size.x > 0.01f) {
                    float k = templateWidth * 0.85f / sprite.bounds.size.x;
                    go.transform.localScale = new Vector3(go.transform.localScale.x * k, go.transform.localScale.y * k, 1f);
                }
                r.color = Color.white;
                byte target = pva.TargetPlayerId;
                var button = go.GetComponent<PassiveButton>();
                button.OnClick.RemoveAllListeners();
                button.OnClick.AddListener((System.Action)(() => OnRevengeClick(target)));
                revengeButtons[target] = r;
            }
        }

        // first click marks, the second on the same player fires
        private static void OnRevengeClick(byte target) {
            try {
                if (!revengeArmed || MeetingHud.Instance == null) return;
                var lp = PlayerControl.LocalPlayer;
                if (!Alive(lp)) return;
                if (revengeMark != target) {
                    revengeMark = target;
                    foreach (var kv in revengeButtons) if (kv.Value != null) kv.Value.color = kv.Key == target ? Marked : Color.white;
                    return;
                }
                revengeArmed = false;
                HideRevengeButtons();
                if (AmHost()) HostHandleRevenge(lp.PlayerId, target);
                else {
                    var w = BeginRpc(SubRevenge);
                    w.Write(target);
                    AmongUsClient.Instance.FinishRpcImmediately(w);
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] revenge click failed: {e}"); }
        }

        private static void HideRevengeButtons() {
            foreach (var kv in revengeButtons) if (kv.Value != null) kv.Value.gameObject.SetActive(false);
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.VotingComplete))]
        static class HideRevengePatch {
            public static void Postfix() { revengeArmed = false; try { HideRevengeButtons(); } catch { } }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
        static class RevengeTickPatch {
            public static void Postfix() {
                try {
                    if (revengeButtons.Count == 0) return;
                    if (!Alive(PlayerControl.LocalPlayer)) { revengeArmed = false; HideRevengeButtons(); return; }
                    // a target that died meanwhile loses its button
                    foreach (var kv in revengeButtons)
                        if (kv.Value != null && kv.Value.gameObject.activeSelf && !Alive(Helpers.playerById(kv.Key))) kv.Value.gameObject.SetActive(false);
                } catch { }
            }
        }

        private static void HostHandleRevenge(byte shooterId, byte guessedId) {
            var hud = MeetingHud.Instance;
            var shooter = Helpers.playerById(shooterId);
            var guessed = Helpers.playerById(guessedId);
            if (hud == null || !revengeHolders.Remove(shooterId) || !Alive(shooter) || !Alive(guessed) || guessedId == shooterId) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Mixer] revenge shot of {shooter?.Data?.PlayerName ?? shooterId.ToString()} refused.");
                return;
            }
            if (hud.state == MeetingHud.VoteStates.Results || hud.state == MeetingHud.VoteStates.Proceeding) return;
            bool hit = mixer != null && guessedId == mixer.PlayerId;
            byte dying = hit ? guessedId : shooterId;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Mixer] revenge: {shooter.Data.PlayerName} shoots at {guessed.Data.PlayerName} - {(hit ? "the Mixer dies" : "wrong, the shooter dies")}.");
            Broadcast(SubShoot, w => { w.Write(shooterId); w.Write(dying); w.Write(guessedId); }, () => ApplyShoot(shooterId, dying, guessedId));
        }

        // TOR's own guess death, without touching any Guesser's shot counter
        private static void ApplyShoot(byte shooterId, byte dyingId, byte guessedId) {
            int evil = Guesser.remainingShotsEvilGuesser, nice = Guesser.remainingShotsNiceGuesser;
            var (entry, shotsField, gm) = GuesserGmEntry(shooterId);
            try {
                RPCProcedure.guesserShoot(shooterId, dyingId, guessedId, (byte)RoleId.Crewmate);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] revenge shot failed: {e}");
            } finally {
                Guesser.remainingShotsEvilGuesser = evil;
                Guesser.remainingShotsNiceGuesser = nice;
                try { if (entry != null && shotsField != null) shotsField.SetValue(entry, gm); } catch { }
            }
        }

        // TOR's guesser game mode keeps one object per guesser (class GuesserGM, internal to TOR)
        private static (object Entry, System.Reflection.FieldInfo Shots, int Value) GuesserGmEntry(byte playerId) {
            try {
                var type = typeof(RPCProcedure).Assembly.GetType("TheOtherRoles.CustomGameModes.GuesserGM");
                var list = type?.GetField("guessers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
                var fGuesser = type?.GetField("guesser");
                var fShots = type?.GetField("shots");
                if (list == null || fGuesser == null || fShots == null) return (null, null, 0);
                foreach (var g in list) {
                    var pc = fGuesser.GetValue(g) as PlayerControl;
                    if (pc != null && pc.PlayerId == playerId) return (g, fShots, (int)fShots.GetValue(g));
                }
            } catch { }
            return (null, null, 0);
        }

        // ====================================================================
        // Host: after the exile, the swaps
        // ====================================================================
        [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
        static class WrapUpPatch {
            public static void Postfix() => HostAfterMeeting();
        }

        // Airship: the postfix runs before the exile (coroutine), so a player voted out right now
        // still looked alive and got swapped. Deferred until the cutscene is done, see UCAirshipWrapUp.
        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        static class AirshipWrapUpPatch {
            public static void Postfix(AirshipExileController __instance) =>
                UCAirshipWrapUp.Arm(__instance, "Mixer", HostAfterMeeting);
        }

        private static void HostAfterMeeting() {
            try {
                revengeHolders.Clear();
                if (!AmHost() || pending.Count == 0) return;
                var todo = pending.ToList();
                pending.Clear();
                foreach (byte id in todo) HostSwap(id);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] after-meeting swap failed: {e}");
            }
        }

        private static void HostSwap(byte targetId) {
            var p = Helpers.playerById(targetId);
            if (!Alive(p)) {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Mixer] {p?.Data?.PlayerName ?? targetId.ToString()} died before the mix took hold - mix spent.");
                return;
            }
            var (oldName, team, oldEntry) = RoleOf(p);
            bool killer = IsKiller(p, oldName, team);
            bool erasable = true;
            try { erasable = p.canBeErased(); } catch { }

            if (oldEntry != null && oldEntry.Holder != null && oldEntry.UcSet == null) erasable = false;   // King, Werewolf...
            var pool = erasable ? PoolFor(team, oldName) : new List<PoolRole>();
            PoolRole next = pool.Count > 0 ? pool[rnd.Next(pool.Count)] : null;

            if (next != null) {
                // take the old role away: a UC tag through its own SendSet(255), TOR's roles in one message
                foreach (var r in Table()) {
                    if (r.Holder == null || r.UcSet == null) continue;
                    PlayerControl h = null;
                    try { h = r.Holder(); } catch { }
                    if (h == null || h.PlayerId != targetId) continue;
                    try { r.UcResidue?.Invoke(); } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"[Mixer] {r.Name} residue: {e.Message}"); }
                    r.UcSet(byte.MaxValue);
                }
                byte torRole = next.Holder == null ? (byte)next.Tor : byte.MaxValue;
                Broadcast(SubTorSwap, w => { w.Write(targetId); w.Write(torRole); }, () => ApplyTorSwap(targetId, torRole));
                if (next.Holder != null) next.UcSet(targetId);
                try { GameData.Instance?.RecomputeTaskCounts(); } catch { }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Mixer] {p.Data.PlayerName}: {oldName} -> {next.Name}{(killer ? " (killer)" : "")}.");
            } else {
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Mixer] {p.Data.PlayerName} ({oldName}): no free {team} role{(erasable ? "" : " (cannot be erased)")} - mix refunded.");
            }

            // the outcome for the Mixer and the target
            byte kind;
            string text;
            if (killer) { kind = ResultKiller; text = oldName; hostMixesLeft = 0; }
            else if (next == null) { kind = ResultRefund; text = ""; hostMixesLeft++; }
            else { kind = ResultNewRole; text = next.Name; }
            if (next != null && killer) text = oldName + "|" + next.Name;
            else if (next != null) text = next.Name;
            byte left = (byte)Mathf.Clamp(hostMixesLeft, 0, 255);
            byte k = kind; string t = text;
            Broadcast(SubResult, w => { w.Write(targetId); w.Write(k); w.Write(left); w.Write(t); }, () => ApplyResult(targetId, k, left, t));
        }

        // every client: TOR's role off (modifiers stay), leftovers scrubbed, the new TOR role on
        private static void ApplyTorSwap(byte targetId, byte torRole) {
            try {
                RPCProcedure.erasePlayerRoles(targetId, true);
                PlayerTuning.ScrubTorRolesLocal(targetId);
                if (torRole != byte.MaxValue) RPCProcedure.setRole(torRole, targetId);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] TOR swap failed: {e}");
            }
        }

        private static void ApplyResult(byte targetId, byte kind, byte left, string text) {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null) return;
            var target = Helpers.playerById(targetId);
            string targetName = target?.Data?.PlayerName ?? "?";
            string[] parts = (text ?? "").Split('|');
            string newRole = kind == ResultKiller ? (parts.Length > 1 ? parts[1] : "") : parts[0];
            if (IsLocalMixer()) {
                mixesLeft = left;
                if (kind == ResultKiller) {
                    string msg = UCLocalization.Tr("uc.ui.mixer.killer_info", targetName, parts[0]);
                    Helpers.showFlash(Color, 2f, msg);
                    Say(msg);
                } else if (kind == ResultRefund) {
                    Say(UCLocalization.Tr("uc.ui.mixer.refund", targetName));
                }
            }
            if (lp.PlayerId == targetId && !string.IsNullOrEmpty(newRole)) {
                string msg = UCLocalization.Tr("uc.ui.mixer.new_role", newRole);
                Helpers.showFlash(Color, 2f, msg);
                Say(msg);
            }
        }

        // ====================================================================
        // Pick (host, random path)
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPickPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (!AmHost() || active) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;
                    if (rnd.Next(1, 101) > SpawnRate.getSelection() * 10) return;
                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainCrewmate).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] IntroEnd pick failed: {e}");
                }
            }
        }

        // ====================================================================
        // Round: target + button
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        static class TargetPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (!active || __instance != PlayerControl.LocalPlayer || !IsLocalMixer()) return;
                    currentTarget = null;
                    if (InMeeting() || mixesLeft <= 0 || !Alive(__instance)) return;
                    currentTarget = PlayerControlFixedUpdatePatch.setTarget();
                    if (currentTarget != null) PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, Color);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(HudManager __instance) {
                try {
                    var sprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.ShiftButton.png", 115f);
                    mixButton = new CustomButton(
                        () => {
                            if (currentTarget == null || mixesLeft <= 0) return;
                            SendMix(currentTarget.PlayerId);
                            Helpers.showFlash(Color, 0.8f, UCLocalization.Tr("uc.ui.mixer.queued", currentTarget.Data.PlayerName));
                            mixButton.Timer = mixButton.MaxTimer;
                        },
                        () => IsLocalMixer() && Alive(PlayerControl.LocalPlayer) && mixesLeft > 0,
                        () => PlayerControl.LocalPlayer.CanMove && currentTarget != null,
                        () => { mixButton.Timer = mixButton.MaxTimer; },
                        sprite,
                        CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.mixer.button"));
                    mixButton.MaxTimer = Cooldown?.getFloat() ?? 30f;
                    mixButton.Timer = 10f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] Button creation failed: {e}");
                }
            }
        }

        // the uses left on the button
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class UsesLabelPatch {
            public static void Postfix() {
                try {
                    if (mixButton?.actionButton == null || !IsLocalMixer()) return;
                    mixButton.actionButton.SetUsesRemaining(mixesLeft);
                } catch { }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || mixer == null || p == null || p != mixer || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++)
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) { __result[i] = MixerInfo(); replaced = true; }
                    if (!replaced) __result.Insert(0, MixerInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Mixer] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            mixer = null;
            active = false;
            mixesLeft = 0;
            hostMixesLeft = -1;
            currentTarget = null;
            pending.Clear();
            revengeHolders.Clear();
            revengeArmed = false;
            revengeMark = byte.MaxValue;
            revengeButtons.Clear();
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Mixer", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Mixer", FullReset);
        }

        // ---- Diagnostics (IdeasPackDiag): mix a player and run the swap at once ----
        internal static string DiagMixNow(byte targetId) {
            if (!AmHost()) return "not host";
            var p = Helpers.playerById(targetId);
            if (p == null) return "no player";
            var before = RoleOf(p);
            pending.Add(targetId);
            HostAfterMeeting();
            var after = RoleOf(p);
            return $"{p.Data?.PlayerName}: {before.Name} ({before.Team}) -> {after.Name}";
        }
    }
}
