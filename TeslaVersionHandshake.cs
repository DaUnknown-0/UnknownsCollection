// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace UnknownsCollection {

    // Mod-presence handshake, modelled on TOR's own VersionHandshake (and Useful TOR Stuff's).
    // Every client with this mod broadcasts its version + assembly GUID at lobby time (module byte
    // 191 on the shared UC channel UCRpc.CallId = 230; the payload behind the module byte is
    // unchanged). A client on an OLD UC build does not know channel 230 and therefore never shows
    // up in playerVersions - which is exactly the desired outcome: EveryoneHasMod() stays false and
    // no UC role spawns, the same protection a genuine version mismatch already gave us.
    // The Tesla is fundamentally client-side (meeting UI for the Tesla, charge indicator + danger
    // warning for the victims), so it is GATED on "everyone has the mod" - exactly like the
    // Revenger/Snitch features. EveryoneHasMod() is read by Tesla.cs when picking the role.
    public static class TeslaVersionHandshake {
        public static readonly Dictionary<int, PlayerVersion> playerVersions = new Dictionary<int, PlayerVersion>();
        private static bool versionSent;

        public sealed class PlayerVersion {
            public readonly Version version;
            public readonly Guid guid;
            public PlayerVersion(Version version, Guid guid) { this.version = version; this.guid = guid; }
            public bool GuidMatches() =>
                Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.Equals(guid);
        }

        // AUDIT-2026-08-15: EveryoneHasMod() used to go through BuildMismatchMessage(), which
        // allocates a fresh allClients.ToArray() copy and formats localized strings for every
        // client on every lobby frame (GameStartManagerUpdatePatch.Postfix runs each frame while a
        // UC role is enabled), even though this caller only ever wants a yes/no answer. Walk
        // allClients by index (no array copy) and bail out on the first mismatch. Must stay in
        // lockstep with BuildMismatchMessage()'s mismatch cases: missing entry, older/newer 3-part
        // version, mismatched module GUID.
        public static bool EveryoneHasMod() {
            try {
                if (AmongUsClient.Instance == null) return true;
                var localV = UnknownsCollectionPlugin.Version;
                var local3 = new Version(localV.Major, localV.Minor, localV.Build);
                var allClients = AmongUsClient.Instance.allClients;
                if (allClients == null) return true;
                for (int i = 0; i < allClients.Count; i++) {
                    InnerNet.ClientData client = allClients[i];
                    if (client == null || client.Character == null) continue;
                    if (client.Id == AmongUsClient.Instance.ClientId) continue;
                    if (client.Character == PlayerControl.LocalPlayer) continue;

                    if (!playerVersions.TryGetValue(client.Id, out PlayerVersion pv)) return false;
                    if (local3.CompareTo(pv.version) != 0) return false;
                    if (!pv.GuidMatches()) return false;
                }
                return true;
            } catch { return false; }
        }

        // True if any of the mod-gated Unknown's Collection roles is enabled. Used to scope the
        // "everyone has the mod" gate (lobby warning + start block) to lobbies that actually need it -
        // a pure vanilla/TOR round must not be blocked just because a client is missing this mod.
        public static bool AnyUCRoleEnabled() {
            return (Tesla.SpawnRate != null && Tesla.SpawnRate.getSelection() > 0) ||
                   (Saboteur.SpawnRate != null && Saboteur.SpawnRate.getSelection() > 0) ||
                   (Poisoner.SpawnRate != null && Poisoner.SpawnRate.getSelection() > 0) ||
                   (Silencer.SpawnRate != null && Silencer.SpawnRate.getSelection() > 0) ||
                   (Illusionist.SpawnRate != null && Illusionist.SpawnRate.getSelection() > 0) ||
                   (Siphoner.SpawnRate != null && Siphoner.SpawnRate.getSelection() > 0) ||
                   (Witness.SpawnRate != null && Witness.SpawnRate.getSelection() > 0) ||
                   (Bug.SpawnRate != null && Bug.SpawnRate.getSelection() > 0) ||
                   (Maniac.SpawnRate != null && Maniac.SpawnRate.getSelection() > 0) ||
                   (Follower.SpawnRate != null && Follower.SpawnRate.getSelection() > 0) ||
                   (Shade.SpawnRate != null && Shade.SpawnRate.getSelection() > 0) ||
                   (Copycat.SpawnRate != null && Copycat.SpawnRate.getSelection() > 0) ||
                   (Scout.SpawnRate != null && Scout.SpawnRate.getSelection() > 0) ||
                   (Beacon.SpawnRate != null && Beacon.SpawnRate.getSelection() > 0) ||
                   (Poltergeist.SpawnRate != null && Poltergeist.SpawnRate.getSelection() > 0) ||
                   (Collector.SpawnRate != null && Collector.SpawnRate.getSelection() > 0) ||
                   (Manipulator.SpawnRate != null && Manipulator.SpawnRate.getSelection() > 0) ||
                   (Werewolf.SpawnRate != null && Werewolf.SpawnRate.getSelection() > 0) ||
                   // No Hunter entry on purpose: he has no spawn rate of his own, he is an event
                   // inside a Werewolf round and therefore already covered by the Werewolf check.
                   (Pelican.SpawnRate != null && Pelican.SpawnRate.getSelection() > 0);
        }

        // Receiver registration for the shared UC channel. Called once from
        // UnknownsCollectionPlugin.Load() - this module has no TryPatch (all its patches are
        // attribute-based and picked up by PatchAll).
        public static void RegisterRpc() {
            UCRpc.Register(UnknownsCollectionPlugin.VersionHandshakeRpcId, HandleModuleRpc);
        }

        private static void HandleModuleRpc(MessageReader reader) {
            // No subtype byte here (there never was one) - the payload starts straight at the
            // version bytes, exactly as it did when 191 was its own callId.
            try { ReceiveRpc(reader); } catch { }
        }

        public static void ShareVersion() {
            if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
            var v = UnknownsCollectionPlugin.Version;

            MessageWriter writer = UCRpc.Begin(UnknownsCollectionPlugin.VersionHandshakeRpcId);
            writer.Write((byte)v.Major);
            writer.Write((byte)v.Minor);
            writer.Write((byte)v.Build);
            writer.WritePacked(AmongUsClient.Instance.ClientId);
            writer.Write(Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToByteArray());
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            // Apply locally too (the sender never receives its own broadcast).
            Receive(v.Major, v.Minor, v.Build,
                Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId, AmongUsClient.Instance.ClientId);
        }

        public static void ReceiveRpc(MessageReader reader) {
            byte major = reader.ReadByte();
            byte minor = reader.ReadByte();
            byte build = reader.ReadByte();
            int clientId = reader.ReadPackedInt32();
            Guid guid = new Guid(reader.ReadBytes(16));
            Receive(major, minor, build, guid, clientId);
        }

        private static void Receive(int major, int minor, int build, Guid guid, int clientId) {
            playerVersions[clientId] = new PlayerVersion(new Version(major, minor, build), guid);
        }

        // Lists every connected client that lacks this mod or runs a different/modified build.
        // Returns "" when everyone matches.
        public static string BuildMismatchMessage() {
            string message = "";
            if (AmongUsClient.Instance == null) return message;
            foreach (InnerNet.ClientData client in AmongUsClient.Instance.allClients.ToArray()) {
                if (client == null || client.Character == null) continue;
                // The local player is, by definition, running this mod - never flag yourself. Without
                // this, a solo lobby (only the host) reports "missing mod", which both shows the lobby
                // warning AND blocks the game start. Trust the local client unconditionally.
                if (client.Id == AmongUsClient.Instance.ClientId) continue;
                if (client.Character == PlayerControl.LocalPlayer) continue;
                string name = client.Character.Data.PlayerName;

                if (!playerVersions.TryGetValue(client.Id, out PlayerVersion pv)) {
                    message += $"<color=#FF0000FF>{UCLocalization.Tr("uc.lobby.missing_mod", name)}</color>";
                    continue;
                }
                // The handshake only transmits Major.Minor.Build (3 bytes), so the received version is
                // always 3-part. Compare against a 3-part local version too - otherwise a TEST build
                // (vX.Y.Z.W, Revision>0) is always "newer" than the received vX.Y.Z and the handshake
                // wrongly blocks the start. Distinct builds are still caught by the module-GUID check.
                var localV = UnknownsCollectionPlugin.Version;
                var local3 = new System.Version(localV.Major, localV.Minor, localV.Build);
                int diff = local3.CompareTo(pv.version);
                if (diff > 0)
                    message += $"<color=#FF0000FF>{UCLocalization.Tr("uc.lobby.older_mod", name, pv.version)}</color>";
                else if (diff < 0)
                    message += $"<color=#FF0000FF>{UCLocalization.Tr("uc.lobby.newer_mod", name, pv.version)}</color>";
                else if (!pv.GuidMatches())
                    message += $"<color=#FF0000FF>{UCLocalization.Tr("uc.lobby.modified_mod", name, pv.version)}</color>";
            }
            return message;
        }

        // --- F1: Cross-mod lobby handshake board (publish-only) ---
        // Same documented AppDomain contract as Useful/Chance (plain strings / Dictionary<int,string>
        // only, no cross-assembly types):
        //   TORMods.Handshake.Registry        → comma-separated guids that have published
        //   TORMods.Handshake.{guid}.name     → short display name
        //   TORMods.Handshake.{guid}.status   → Dictionary<int,string>: clientId → "code<0x1F>version"
        //                                       code ∈ ok | old | new | mod ; missing clients omitted
        // UsefulTORStuff OWNS and renders the combined per-player "Mod-Check" overview; this mod only
        // PUBLISHES its snapshot so it shows up as a column there. Wire format behind module byte
        // 191 unchanged (only the transport callId moved to UCRpc.CallId).
        private const string HandshakeRegistryKey = "TORMods.Handshake.Registry";
        private const string HandshakeKeyPrefix = "TORMods.Handshake.";
        private const char StatusSep = '';

        private static void PublishSnapshot() {
            try {
                if (AmongUsClient.Instance == null) return;
                var status = new Dictionary<int, string>();
                // 3-part local version, same as BuildMismatchMessage: the handshake only transmits
                // Major.Minor.Build, so a 4-part test build must not read as "newer" than its own wire echo.
                var localV = UnknownsCollectionPlugin.Version;
                var local3 = new Version(localV.Major, localV.Minor, localV.Build);
                foreach (var kv in playerVersions) {
                    PlayerVersion pv = kv.Value;
                    string code;
                    int diff = local3.CompareTo(pv.version);
                    if (diff > 0) code = "old";
                    else if (diff < 0) code = "new";
                    else code = pv.GuidMatches() ? "ok" : "mod";
                    status[kv.Key] = code + StatusSep + pv.version;
                }
                string guid = UnknownsCollectionPlugin.PluginGuid;
                AppDomain.CurrentDomain.SetData(HandshakeKeyPrefix + guid + ".name", "Unknown's");
                AppDomain.CurrentDomain.SetData(HandshakeKeyPrefix + guid + ".status", status);
                var reg = AppDomain.CurrentDomain.GetData(HandshakeRegistryKey) as string ?? "";
                if (!reg.Split(',').Contains(guid))
                    AppDomain.CurrentDomain.SetData(HandshakeRegistryKey, reg == "" ? guid : reg + "," + guid);
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Handshake] snapshot publish failed: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OnGameJoinedPatch {
            public static void Postfix() { playerVersions.Clear(); versionSent = false; }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
        static class OnPlayerJoinedPatch {
            public static void Postfix() { if (PlayerControl.LocalPlayer != null) ShareVersion(); }
        }

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
        static class GameStartManagerStartPatch {
            public static void Postfix() { versionSent = false; }
        }

        // Share once per lobby; (host-only) warn on TOR's GameStartText when any UC role is ON
        // but someone is missing the mod - these roles then will NOT spawn/start (gated on EveryoneHasMod()).
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class GameStartManagerUpdatePatch {
            public static void Postfix(GameStartManager __instance) {
                if (PlayerControl.LocalPlayer != null && !versionSent) { versionSent = true; ShareVersion(); }
                if (AmongUsClient.Instance == null) return;
                // F1: publish the snapshot every lobby frame (all clients, BEFORE the host-only
                // return) so UsefulTORStuff's combined Mod-Check overview gets a UC column.
                PublishSnapshot();
                if (!AmongUsClient.Instance.AmHost) return;
                if (__instance.startState == GameStartManager.StartingStates.Countdown) return;

                var text = __instance.GameStartText;
                if (text == null) return;
                if (!AnyUCRoleEnabled() || EveryoneHasMod()) return;
                string marker = "Unknown's Collection";
                if (text.text != null && text.text.Contains(marker)) return;

                string msg = $"<color=#FFA500FF>{UCLocalization.Tr("uc.lobby.role_enabled_warning")}</color>";
                text.text = string.IsNullOrEmpty(text.text) ? msg : text.text + "\n" + msg;
                var cam = Camera.main;
                if (cam != null) {
                    Vector3 tl = cam.ViewportToWorldPoint(new Vector3(0f, 1f, 10f));
                    tl.z = text.transform.position.z;
                    text.transform.position = tl + new Vector3(0.7f, -0.5f, 0f);
                }
                text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                text.rectTransform.pivot = new Vector2(0f, 1f);
                __instance.GameStartTextParent.SetActive(true);
            }
        }

        // Block the game start while not every player has the same Unknown's Collection build - but
        // only if a UC role is actually enabled; otherwise a modless friend must be able to join a
        // pure vanilla/TOR round. Host-only (only the host can begin); the lobby warning above tells
        // everyone why. Returning false suppresses GameStartManager.BeginGame, exactly like the lobby
        // password gate.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
        static class BeginGameGatePatch {
            public static bool Prefix() {
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return true;
                if (EveryoneHasMod() || !AnyUCRoleEnabled()) return true;

                try {
                    var hud = HudManager.Instance;
                    if (hud != null && hud.Notifier != null)
                        hud.Notifier.AddDisconnectMessage(
                            UCLocalization.Tr("uc.lobby.start_blocked"));
                } catch { }
                UnknownsCollectionPlugin.Logger?.LogInfo("[Tesla] Game start blocked - version mismatch.");
                return false;
            }
        }

    }
}
