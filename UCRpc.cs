// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCRpc - the single custom-RPC channel of Unknown's Collection.
 *
 * WHY
 * ---
 * Every custom RPC in Among Us occupies one byte in the SAME id space TOR's own CustomRPC enum
 * uses. TOR's enum keeps growing (100-114, 120-183 today) and every new TOR release can claim the
 * next free byte. UC used to burn 18 separate bytes (190-210) for its 18 modules, so 18 chances
 * for a future TOR release to collide with us - and a collision is not a compile error, it is a
 * silent mis-parse: TOR reads our payload as its own RPC (or vice versa), which can kill players,
 * flip roles or desync the round.
 *
 * WHAT
 * ----
 * From now on the whole mod speaks over exactly ONE callId (CallId = 230). The first byte after
 * the callId is the MODULE byte, which keeps each module's historical id (Tesla 190, Saboteur 192,
 * ... Manipulator 210) so logs, comments and ID-Registry.md stay readable. Everything after the
 * module byte is unchanged per module (usually a subtype byte + payload) - the wire format behind
 * the module byte was deliberately NOT touched during the migration.
 *
 *   [callId 230][moduleId][ ... module's own payload, unchanged ... ]
 *
 * So the surface exposed to TOR shrank from 18 bytes to 1. Only 230 has to stay free.
 *
 * MIXED VERSIONS
 * --------------
 * An old UC build does not know callId 230 and silently ignores it (and its own 190-210 sends are
 * ignored by this build). That is safe here because UC gates every role spawn on
 * TeslaVersionHandshake.EveryoneHasMod() - and the handshake itself travels on this channel too,
 * so a mixed lobby simply never confirms "everyone has the mod" and no UC role spawns at all.
 * (Useful TOR Stuff has no such gate, which is why UTSRpc there uses dual-send instead.)
 *
 * USAGE
 * -----
 *   sender:   var w = UCRpc.Begin(RpcId); w.Write(subtype); ...; FinishRpcImmediately(w);
 *   receiver: UCRpc.Register(RpcId, HandleModuleRpc);   // in the module's TryPatch()
 *             private static void HandleModuleRpc(MessageReader reader) { byte subtype = reader.ReadByte(); ... }
 *
 * The dispatcher below is the ONLY PlayerControl.HandleRpc patch this mod uses for its own RPCs.
 * (Copycat still patches HandleRpc, but purely to SNIFF TOR's ability RPCs - it never consumes them.)
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;

namespace UnknownsCollection {
    public static class UCRpc {
        // The one and only custom callId of Unknown's Collection. 230 sits in the free 211-243
        // window (TOR <= 183, HostFix 167, ChanceMod 200-202/250-251, UTS 240/244-254).
        public const byte CallId = 230;

        // moduleId -> handler. Filled from each module's TryPatch()/init, read by the dispatcher.
        private static readonly Dictionary<byte, Action<MessageReader>> handlers =
            new Dictionary<byte, Action<MessageReader>>();

        // The PlayerControl the currently dispatched RPC arrived on (i.e. the sender). Set only for
        // the duration of a handler call. MeetingMapPing-style modules that need the sender identity
        // read this instead of taking __instance from their own patch.
        public static PlayerControl Sender { get; private set; }

        // Start a message on the UC channel. The module byte is written for you; the caller writes
        // its own subtype + payload exactly as before and finishes with FinishRpcImmediately.
        public static MessageWriter Begin(byte moduleId) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, CallId, SendOption.Reliable, -1);
            w.Write(moduleId);
            return w;
        }

        // Register a module's receiver. Called once per module at load time; a duplicate module byte
        // is a programming error (two modules would eat each other's payload), so it is logged loudly.
        public static void Register(byte moduleId, Action<MessageReader> handler) {
            if (handler == null) return;
            if (handlers.ContainsKey(moduleId))
                UnknownsCollectionPlugin.Logger?.LogError(
                    $"[UCRpc] module byte {moduleId} registered twice - the later handler wins, " +
                    "one of the two modules will never receive its RPCs.");
            handlers[moduleId] = handler;
        }

        public static int RegisteredCount => handlers.Count;

        // Single dispatcher. Runs BEFORE TOR's own HandleRpc handler (Priority.High) and always
        // consumes callId 230 - the channel belongs to us, nobody else may parse it.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(PlayerControl __instance, byte callId, MessageReader reader) {
                if (callId != CallId) return true;
                try {
                    byte moduleId = reader.ReadByte();
                    if (handlers.TryGetValue(moduleId, out var handler)) {
                        Sender = __instance;
                        try { handler(reader); }
                        finally { Sender = null; }
                    } else {
                        // Unknown module byte: a newer UC build sent a module this one does not have.
                        // Harmless (the role is gated on the handshake anyway) but worth a log line.
                        UnknownsCollectionPlugin.Logger?.LogWarning(
                            $"[UCRpc] unknown module byte {moduleId} on channel {CallId} - ignored.");
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCRpc] dispatch failed: {e}");
                }
                return false; // channel 230 is ours - never hand it to TOR
            }
        }
    }
}
