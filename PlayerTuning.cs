// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * PlayerTuning - host-controlled per-player overrides, applied consistently on EVERY client:
 *   - movement speed multiplier
 *   - cooldown multiplier (kill timer + every TOR CustomButton, i.e. UC/UTS/ChanceMod buttons too)
 *   - vent ban (a player who normally could vent may not)
 *   - a full task-list replacement (used by the host-side role-control tooling)
 *
 * This module has NO game logic of its own - it is a remote-control surface for host tooling
 * (ForceImpostorMod drives it via reflection). It ships inside UC because the EFFECTS are
 * client-side (movement is client-authoritative, cooldowns tick locally, vents resolve locally),
 * so the receiving code must exist on every client - and everyone already runs UC.
 *
 * Design notes (all patterns proven elsewhere in this codebase):
 *   - RPC: module byte 213 on the shared UC channel (UCRpc.CallId = 230), Beacon.cs is the
 *     reference module. Sender applies locally (it never receives its own broadcast).
 *   - Speed: velocity-multiply in PlayerPhysics.FixedUpdate (owner) + CustomNetworkTransform.
 *     FixedUpdate (remote view) - the SaboteurTrap/TrapperLimp/PropHunt approach. Deliberately
 *     NOT MyPhysics.Speed: that global field used to be fought over by absolute writes from
 *     Scout/Werewolf/Poltergeist (AUDIT-2026-08-23, M-6); all three now use the same
 *     velocity-multiply pattern, so nothing left in this mod writes it outright anymore.
 *   - Cooldowns: rate-scaling. CustomButton.Update gets a prefix/postfix pair measuring the
 *     actual tick delta (inherits ALL of TOR's gates for free: draft, vent, moveable, effect
 *     duration is exempt). The vanilla kill timer gets the same treatment in a
 *     PlayerControl.FixedUpdate postfix - a SetKillTimer prefix would fight TOR's clamp
 *     (PlayerControlPatch.cs:1358) and re-trigger every tick.
 *   - Vent ban AND vent grant go through the same hook, `Helpers.roleCanUseVents` - the approach
 *     ChanceMod uses for its vent roll. TOR asks that helper everywhere it matters, so flipping its
 *     answer is enough and no vanilla role change (Engineer) is needed. The ban adds three more
 *     postfixes (Vent.CanUse for the last word, button hide, RpcEnterVent as the hard stop); the
 *     grant adds a button PLACEMENT patch, because the vent button otherwise ends up underneath a
 *     role's ability buttons and cannot be clicked. Postfix, never prefix - TOR's own Vent.CanUse
 *     prefix returns false and would win against any foreign prefix (HarmonyX rule, see
 *     PoltergeistManifest.cs). A player already IN a vent is never locked in.
 *   - Tasks: the UC subtype clears the target's local task HUD (clearAllTasks), then the host
 *     sends the real vanilla RpcSetTasks (host-owned NetworkedPlayerInfo). TOR's
 *     RecomputeTaskCounts replacement reads playerInfo.Tasks live, so totals stay consistent.
 *
 * Gating: sends are host-only and require TeslaVersionHandshake.EveryoneHasMod() - a mixed
 * lobby simply never gets tuning values (fail-safe, the host tooling surfaces the refusal).
 * State clears on resetVariables (round start) and OnGameJoined (lobby-leak rule).
 */

using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TheOtherRoles;
using UnityEngine;

namespace UnknownsCollection {
    public static class PlayerTuning {
        private const byte RpcId = UnknownsCollectionPlugin.PlayerTuningRpcId; // 213
        private const byte SubSetTuning = 0;  // [pid][speed float][cooldown float][noVent bool][canVent bool]
        private const byte SubClear     = 1;  // [pid] (255 = alle)
        private const byte SubSetTasks  = 2;  // [pid][count][taskIndexBytes...]
        private const byte SubScrubRole = 3;  // [pid] - leftover TOR role statics (see ScrubTorRoles)
        private const byte SubSetFaction = 4; // [pid][impostor bool] - vanilla faction, applied locally everywhere
        private const byte SubRevive    = 5;  // [pid] - revive a dead player (outside meetings)
        private const byte SubArmKillFx = 6;  // [victimId][kind] - host tooling: pick the next cutscene

        public struct Tune {
            public float Speed;      // 1 = normal
            public float Cooldown;   // 1 = normal, 2 = doppelte Abklingzeit
            public bool NoVent;      // darf nicht venten (auch wenn die Rolle es erlaubt)
            public bool CanVent;     // darf venten (auch wenn die Rolle es nicht erlaubt)
        }
        private static readonly Dictionary<byte, Tune> tunes = new Dictionary<byte, Tune>();

        // ---- public queries (also read by host tooling via reflection) ----
        public static bool AnyTuningConfigured() => tunes.Count > 0;
        public static float SpeedMult(byte pid) => tunes.TryGetValue(pid, out var t) ? Mathf.Clamp(t.Speed, 0.05f, 10f) : 1f;
        public static float CdMult(byte pid) => tunes.TryGetValue(pid, out var t) ? Mathf.Clamp(t.Cooldown, 0.05f, 20f) : 1f;
        public static bool VentBanned(byte pid) => tunes.TryGetValue(pid, out var t) && t.NoVent;
        public static bool VentGranted(byte pid) => tunes.TryGetValue(pid, out var t) && t.CanVent && !t.NoVent;
        public static string DescribeTune(byte pid) =>
            tunes.TryGetValue(pid, out var t)
                ? $"speed x{t.Speed:0.##}, cooldown x{t.Cooldown:0.##}" +
                  (t.NoVent ? ", no vents" : t.CanVent ? ", may vent" : "")
                : "";

        public static void TryPatch(Harmony harmony) {
            // Receiver registration on the shared UC channel; the Harmony patches below are
            // attribute-based and get collected by the plugin-wide PatchAll.
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        // ====================================================================
        // RPC send (host-only, gated) + apply
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = UCRpc.Begin(RpcId);
            w.Write(subtype);
            return w;
        }

        private static bool MaySend(out string reason) {
            reason = null;
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) { reason = "not host"; return false; }
            if (!TeslaVersionHandshake.EveryoneHasMod()) { reason = "not everyone runs this UC build"; return false; }
            return true;
        }

        public static bool SendSetTuning(byte pid, float speed, float cooldown, bool noVent, bool canVent = false) {
            try {
                if (!MaySend(out string reason)) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] SendSetTuning refused: {reason}.");
                    return false;
                }
                var w = BeginRpc(SubSetTuning);
                w.Write(pid);
                w.Write(speed);
                w.Write(cooldown);
                w.Write(noVent);
                w.Write(canVent);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetTuning(pid, speed, cooldown, noVent, canVent);
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] SendSetTuning failed: {e}");
                return false;
            }
        }

        public static bool SendClear(byte pid) { // 255 = alle
            try {
                if (!MaySend(out string reason)) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] SendClear refused: {reason}.");
                    return false;
                }
                var w = BeginRpc(SubClear);
                w.Write(pid);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear(pid);
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] SendClear failed: {e}");
                return false;
            }
        }

        // Replaces the target's task list. taskIds are PlayerTask.Index values into the CURRENT
        // ShipStatus catalog (the same bytes vanilla RpcSetTasks speaks). The UC subtype makes the
        // target client tear down its old task HUD BEFORE the vanilla task list arrives; both
        // messages leave the host back-to-back on the same reliable channel, so order holds.
        public static bool SendSetTasks(byte pid, byte[] taskIds) {
            try {
                if (!MaySend(out string reason)) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] SendSetTasks refused: {reason}.");
                    return false;
                }
                var target = Helpers.playerById(pid);
                if (target == null || target.Data == null) return false;

                var w = BeginRpc(SubSetTasks);
                w.Write(pid);
                w.Write((byte)taskIds.Length);
                for (int i = 0; i < taskIds.Length; i++) w.Write(taskIds[i]);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetTasks(pid);

                // The real task sync: vanilla RpcSetTasks on the host-owned NetworkedPlayerInfo.
                var arr = new Il2CppStructArray<byte>(taskIds.Length);
                for (int i = 0; i < taskIds.Length; i++) arr[i] = taskIds[i];
                target.Data.RpcSetTasks(arr);
                try { GameData.Instance?.RecomputeTaskCounts(); } catch { }
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[PlayerTuning] tasks of {target.Data.PlayerName} replaced ({taskIds.Length} tasks).");
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] SendSetTasks failed: {e}");
                return false;
            }
        }

        // ====================================================================
        // Task catalog of the CURRENT map. Kept here (not in the host tooling) so callers never
        // have to touch Il2Cpp collections: the catalog is handed out as plain strings and the
        // selection comes back as plain bytes - the same "BCL types only" rule the AppDomain
        // contracts in this project use.
        //
        // The bytes ARE PlayerTask.Index values (what ShipStatus.AssignTaskIndexes assigns and
        // ShipStatus.GetTaskById resolves) - exactly what vanilla RpcSetTasks speaks. One flat
        // index space over Common+Short+Long, NOT a per-array index.
        // ====================================================================
        public enum TaskKind { Common = 0, Short = 1, Long = 2 }

        private static List<(byte index, TaskKind kind, string name, string room)> Catalog() {
            var list = new List<(byte, TaskKind, string, string)>();
            var ship = ShipStatus.Instance;
            if (ship == null) return list;
            void Add(Il2CppReferenceArray<NormalPlayerTask> arr, TaskKind kind) {
                if (arr == null) return;
                for (int i = 0; i < arr.Count; i++) {
                    var t = arr[i];
                    if (t == null) continue;
                    list.Add(((byte)t.Index, kind, t.TaskType.ToString(), t.StartAt.ToString()));
                }
            }
            Add(ship.CommonTasks, TaskKind.Common);
            Add(ship.ShortTasks, TaskKind.Short);
            Add(ship.LongTasks, TaskKind.Long);
            return list;
        }

        // "index|kind|name|room" per entry - stringly typed on purpose (reflection-friendly).
        public static string[] TaskCatalog() {
            try {
                var cat = Catalog();
                var outArr = new string[cat.Count];
                for (int i = 0; i < cat.Count; i++)
                    outArr[i] = $"{cat[i].index}|{(int)cat[i].kind}|{cat[i].name}|{cat[i].room}";
                return outArr;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] TaskCatalog failed: {e}");
                return new string[0];
            }
        }

        // Random pick of n tasks per kind, clamped to what the map actually offers (the same
        // guard TOR applies in its ShipStatus.Begin prefix - asking for more tasks than exist
        // would otherwise produce invalid indexes).
        public static byte[] BuildTaskSelection(int common, int shortT, int longT) {
            try {
                var cat = Catalog();
                var result = new List<byte>();
                void Pick(TaskKind kind, int want) {
                    var pool = new List<byte>();
                    foreach (var e in cat) if (e.kind == kind) pool.Add(e.index);
                    int take = Mathf.Clamp(want, 0, pool.Count);
                    for (int i = 0; i < take; i++) {
                        int j = UnityEngine.Random.Range(0, pool.Count);
                        result.Add(pool[j]);
                        pool.RemoveAt(j);
                    }
                }
                Pick(TaskKind.Common, common);
                Pick(TaskKind.Short, shortT);
                Pick(TaskKind.Long, longT);
                return result.ToArray();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] BuildTaskSelection failed: {e}");
                return new byte[0];
            }
        }

        // How many tasks does the player currently have, and how many are done? "done/total",
        // read straight from the synced Data.Tasks so it is valid on every client.
        public static string TaskSummary(byte pid) {
            try {
                var p = Helpers.playerById(pid);
                if (p == null || p.Data == null || p.Data.Tasks == null) return "";
                int total = p.Data.Tasks.Count, done = 0;
                for (int i = 0; i < total; i++) if (p.Data.Tasks[i] != null && p.Data.Tasks[i].Complete) done++;
                return $"{done}/{total}";
            } catch { return ""; }
        }

        // ====================================================================
        // Leftover TOR role statics
        //
        // TOR's erasePlayerRoles calls each role's clearAndReload(), and SOME of those only reload
        // the option values without ever nulling the owner field - Yoyo is the plain example
        // (TheOtherRoles.cs: clearAndReload sets blinkDuration/markCooldown/... and markedLocation,
        // but never `yoyo = null`). The erase therefore leaves the role attached and the player ends
        // up carrying two roles at once ("Yo-Yo Evil Guesser").
        //
        // This runs right after the erase on EVERY client and nulls whatever is still pointing at
        // the player. It targets ONLY the owner field of a role class - a field whose name ends with
        // its own class name (Yoyo.yoyo, Mayor.mayor, Guesser.evilGuesser, Sheriff.formerSheriff).
        // Side-state such as Medic.shielded or the Lovers pair is deliberately left alone: TOR's own
        // clearAndReload owns those, and modifiers survive an erase by design.
        //
        // Deliberately not a fix inside TOR: the original source stays untouched.
        // ====================================================================
        public static bool SendScrubTorRoles(byte pid) {
            try {
                if (!MaySend(out string reason)) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] SendScrubTorRoles refused: {reason}.");
                    return false;
                }
                var w = BeginRpc(SubScrubRole);
                w.Write(pid);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyScrubTorRoles(pid);
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] SendScrubTorRoles failed: {e}");
                return false;
            }
        }

        private static void ApplyScrubTorRoles(byte pid) {
            try {
                var target = Helpers.playerById(pid);
                if (target == null) return;
                var asm = UnknownsCollectionPlugin.TORAssembly;
                if (asm == null) return;

                var cleared = new List<string>();
                foreach (var type in asm.GetTypes()) {
                    if (type.Namespace != "TheOtherRoles" || !type.IsAbstract || !type.IsSealed) continue; // static classes
                    foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public
                                                     | System.Reflection.BindingFlags.Static)) {
                        if (f.FieldType != typeof(PlayerControl)) continue;
                        // Owner field only: its name ends with the class name (yoyo, mayor,
                        // evilGuesser, formerSheriff, ...). Skips shielded/lover1/currentTarget/...
                        if (!f.Name.EndsWith(type.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (f.GetValue(null) is not PlayerControl held || held.PlayerId != pid) continue;
                        f.SetValue(null, null);
                        cleared.Add($"{type.Name}.{f.Name}");
                    }
                }
                if (cleared.Count > 0)
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[PlayerTuning] scrubbed leftover TOR role statics for {target.Data?.PlayerName}: {string.Join(", ", cleared)}.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] ApplyScrubTorRoles failed: {e}");
            }
        }

        // ====================================================================
        // Vanilla faction + revive (host tooling; applied LOCALLY on every client)
        //
        // WHY NOT vanilla RpcSetRole: its receiving path did not reliably apply a mid-game faction
        // change on the affected client (observed: a lobby-assigned crew role left the player a
        // vanilla Impostor on his OWN screen - kill button, impostor count - while the host saw the
        // change). TOR's own faction switches (Thief steals an impostor role RPC.cs:1140, Sidekick
        // promote :702) never use RpcSetRole either: they run RoleManager.Instance.SetRole locally
        // inside their OWN rpc handler on every client. These two subtypes are exactly that pattern,
        // exposed for the host tooling.
        // ====================================================================
        public static bool SendSetFaction(byte pid, bool impostor) {
            try {
                if (!MaySend(out string reason)) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] SendSetFaction refused: {reason}.");
                    return false;
                }
                var w = BeginRpc(SubSetFaction);
                w.Write(pid);
                w.Write(impostor);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetFaction(pid, impostor); // synchronous local apply - callers read Data.Role right after
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] SendSetFaction failed: {e}");
                return false;
            }
        }

        // ====================================================================
        // Arm a chosen kill cutscene (host tooling)
        //
        // WHY THIS EXISTS
        // There are 22 kill cutscenes across UC and TOR (UCKillOverlay.Kind), and the only way to
        // see one used to be to actually roll that role, reach that exact situation, and be the
        // killer or the victim. Verifying a single animation could cost a dozen rounds, and after a
        // change to the overlay code there was no practical way to check the other twenty-one.
        //
        // ForceImpostorMod already has a test kill (RoleControl.Kill) that runs a real
        // uncheckedMurderPlayer, so the kill itself is not this module's job. What was missing is
        // the CHOICE of cutscene: without it the overlay hook falls back to whatever the situation
        // happens to look like, which for a staged kill is nothing in particular. This arms one
        // specific kind on every client, and the kill that follows consumes it exactly the way a
        // real ability's arming would (UCKillOverlay.SelectRaw checks armedVictims first). So the
        // code under test is the real path, not a preview mode that could drift away from it.
        //
        // A broadcast rather than a host-local call, because the cutscene plays on the killer's and
        // the victim's own clients, not on the host's.
        private static void ApplyArmKillFx(byte victimId, byte kind) {
            try {
                if (kind == 0) return;                  // 0 = leave it to the normal detection
                UCKillOverlay.ArmVictim((UCKillOverlay.Kind)kind, victimId, 5f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] ApplyArmKillFx failed: {e.Message}");
            }
        }

        // Arms `kind` for the next death of `victimId`, on every client. The caller performs the
        // kill itself right afterwards; the 5s time-to-live is what bridges the gap between this
        // broadcast and the murder RPC that follows it.
        public static bool SendArmKillFx(byte victimId, byte kind) {
            try {
                if (!MaySend(out string reason)) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] SendArmKillFx refused: {reason}.");
                    return false;
                }
                var w = BeginRpc(SubArmKillFx);
                w.Write(victimId);
                w.Write(kind);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyArmKillFx(victimId, kind);
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] SendArmKillFx failed: {e}");
                return false;
            }
        }

        // The cutscene names a host tool can offer, in enum order so the index IS the wire value.
        // Plain strings, so ForceImpostorMod can read them by reflection without sharing the enum
        // type (the contract style PlayerTuningBridge uses for everything else).
        public static string[] KillFxKindNames() {
            try { return Enum.GetNames(typeof(UCKillOverlay.Kind)); }
            catch { return new string[0]; }
        }

        public static bool SendRevive(byte pid) {
            try {
                if (!MaySend(out string reason)) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] SendRevive refused: {reason}.");
                    return false;
                }
                var w = BeginRpc(SubRevive);
                w.Write(pid);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyRevive(pid);
                return true;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] SendRevive failed: {e}");
                return false;
            }
        }

        private static void ApplySetFaction(byte pid, bool impostor) {
            try {
                var p = Helpers.playerById(pid);
                if (p == null || p.Data == null) return;
                RoleManager.Instance.SetRole(p, impostor ? RoleTypes.Impostor : RoleTypes.Crewmate);
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[PlayerTuning] faction of {p.Data.PlayerName} -> {(impostor ? "Impostor" : "Crewmate")}.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] ApplySetFaction failed: {e}");
            }
        }

        private static void ApplyRevive(byte pid) {
            try {
                var p = Helpers.playerById(pid);
                if (p == null || p.Data == null || !p.Data.IsDead) return;
                // Second meeting guard (AUDIT L-9). RoleControl.Revive already refuses this during a
                // meeting - "the voting UI knows no resurrection" - but that guard sits with ONE
                // caller, while this applier runs on every client and is reachable from anything that
                // sends the module byte. A revive mid-meeting leaves the vote area showing a corpse
                // for a player who is walking again, so the rule belongs here as well.
                if (MeetingHud.Instance != null || ExileController.Instance != null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        $"[PlayerTuning] revive of {p.Data.PlayerName} ignored: a meeting is running.");
                    return;
                }
                // Faction BEFORE Revive: the ghost role still carries IsImpostor, afterwards we
                // re-issue the matching LIVING vanilla role (Revive alone leaves the ghost role).
                bool wasImp = p.Data.Role != null && p.Data.Role.IsImpostor;
                p.Revive();
                RoleManager.Instance.SetRole(p, wasImp ? RoleTypes.Impostor : RoleTypes.Crewmate);
                // The corpse must not stay reportable after its owner walks again.
                foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                    if (body != null && body.ParentId == pid)
                        UnityEngine.Object.Destroy(body.gameObject);
                try { GameData.Instance?.RecomputeTaskCounts(); } catch { }
                UnknownsCollectionPlugin.Logger?.LogInfo($"[PlayerTuning] revived {p.Data.PlayerName}.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] ApplyRevive failed: {e}");
            }
        }

        private static void ApplySetTuning(byte pid, float speed, float cooldown, bool noVent, bool canVent) {
            tunes[pid] = new Tune { Speed = speed, Cooldown = cooldown, NoVent = noVent, CanVent = canVent };
            UnknownsCollectionPlugin.Logger?.LogInfo($"[PlayerTuning] tune #{pid}: {DescribeTune(pid)}.");
        }

        private static void ApplyClear(byte pid) {
            if (pid == byte.MaxValue) tunes.Clear();
            else tunes.Remove(pid);
        }

        private static void ApplySetTasks(byte pid) {
            // Only the OWNING client has live task GameObjects to tear down; clearAllTasks also
            // empties Data.Tasks, which is fine - the vanilla SetTasks arriving right after
            // repopulates it. Everyone refreshes the totals.
            try {
                if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == pid)
                    PlayerControl.LocalPlayer.clearAllTasks();
                GameData.Instance?.RecomputeTaskCounts();
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] ApplySetTasks failed: {e}");
            }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                // HOST-ONLY, no exceptions: every subtype of this module is host tooling (Role Control)
                // that rewrites another player's speed, cooldown, vent access, tasks, TOR role, FACTION
                // or even revives them. There is no legitimate non-host sender, so the whole module is
                // gated here rather than per case (AUDIT-2026-08-11.md, H-3).
                if (!UCRpc.RequireHost($"PlayerTuning.subtype{subtype}")) return;
                switch (subtype) {
                    case SubSetTuning: {
                        byte pid = reader.ReadByte();
                        float speed = reader.ReadSingle();
                        float cd = reader.ReadSingle();
                        bool noVent = reader.ReadBoolean();
                        bool canVent = reader.ReadBoolean();
                        ApplySetTuning(pid, speed, cd, noVent, canVent);
                        break;
                    }
                    case SubClear:
                        ApplyClear(reader.ReadByte());
                        break;
                    case SubSetTasks: {
                        byte pid = reader.ReadByte();
                        int n = reader.ReadByte();
                        for (int i = 0; i < n; i++) reader.ReadByte(); // Payload nur fuer Logs/Zukunft
                        ApplySetTasks(pid);
                        break;
                    }
                    case SubScrubRole:
                        ApplyScrubTorRoles(reader.ReadByte());
                        break;
                    case SubSetFaction: {
                        byte pid = reader.ReadByte();
                        bool imp = reader.ReadBoolean();
                        ApplySetFaction(pid, imp);
                        break;
                    }
                    case SubRevive:
                        ApplyRevive(reader.ReadByte());
                        break;
                    case SubArmKillFx: {
                        byte victimId = reader.ReadByte();
                        byte kind = reader.ReadByte();
                        ApplyArmKillFx(victimId, kind);
                        break;
                    }
                    default:
                        UnknownsCollectionPlugin.Logger?.LogWarning($"[PlayerTuning] unknown subtype {subtype}.");
                        break;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[PlayerTuning] HandleRpc failed: {e}");
            }
        }

        // ====================================================================
        // Resets: Rundenstart + Lobby-Wechsel (PlayerId-keyed state, lobby-leak rule)
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("PlayerTuning", tunes.Clear);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("PlayerTuning", tunes.Clear);
        }

        // ====================================================================
        // Speed: velocity multiply. Owner side drives the real movement, the
        // CustomNetworkTransform side keeps the remote interpolation in step.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
        static class SpeedOwnerPatch {
            public static void Postfix(PlayerPhysics __instance) {
                try {
                    if (tunes.Count == 0) return;
                    if (!__instance.AmOwner || __instance.myPlayer == null || __instance.myPlayer.Data == null) return;
                    float m = SpeedMult(__instance.myPlayer.PlayerId);
                    if (Mathf.Abs(m - 1f) < 0.0001f) return;
                    if (GameData.Instance == null) return;
                    if (__instance.myPlayer.Data.IsDead || !__instance.myPlayer.CanMove) return; // Freeze/Trap respektieren
                    __instance.body.velocity *= m;
                } catch { }
            }
        }

        [HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.FixedUpdate))]
        static class SpeedRemotePatch {
            public static void Postfix(CustomNetworkTransform __instance) {
                try {
                    if (tunes.Count == 0) return;
                    if (__instance.AmOwner || __instance.myPlayer == null || __instance.myPlayer.Data == null) return;
                    float m = SpeedMult(__instance.myPlayer.PlayerId);
                    if (Mathf.Abs(m - 1f) < 0.0001f) return;
                    if (__instance.myPlayer.Data.IsDead) return;
                    __instance.body.velocity *= m;
                } catch { }
            }
        }

        // ====================================================================
        // Cooldowns (TOR CustomButtons): Rate-Scaling ueber die Delta-Messung.
        // Der Prefix merkt sich den Timer vor dem Tick; nur wenn TOR wirklich getickt hat
        // (delta > 0 - alle Gates gelaufen), wird die Rate skaliert. Effektdauern sind
        // ausgenommen (das ist keine Abklingzeit). TOR patcht seine eigene CustomButton.Update
        // nicht, daher ist der Prefix hier unbedenklich.
        // ====================================================================
        [HarmonyPatch(typeof(TheOtherRoles.Objects.CustomButton), nameof(TheOtherRoles.Objects.CustomButton.Update))]
        static class ButtonCooldownRatePatch {
            public static void Prefix(TheOtherRoles.Objects.CustomButton __instance, out float __state)
                => __state = __instance.Timer;

            public static void Postfix(TheOtherRoles.Objects.CustomButton __instance, float __state) {
                try {
                    if (tunes.Count == 0) return;
                    // Effektdauern sind keine Abklingzeit und bleiben unskaliert. Ebenso
                    // DeputyTimer: er ist bei aktiven Effekten deren Restzeit (CustomButton setzt
                    // ihn beim Handcuff auf EffectDuration und beendet den Effekt bei <= 0), und
                    // die Handcuff-Sperre ist eine Fremdwirkung des Deputy, kein eigener Cooldown.
                    if (__instance.HasEffect && __instance.isEffectActive) return;
                    var me = PlayerControl.LocalPlayer;
                    if (me == null) return;
                    float mult = CdMult(me.PlayerId);
                    if (Mathf.Abs(mult - 1f) < 0.0001f) return;
                    float delta = __state - __instance.Timer;
                    if (delta <= 0f) return; // kein Tick (Gates) oder Timer wurde gerade neu gesetzt

                    // NICHT auf 0 clampen: TOR schaltet den Button erst frei, wenn der Timer
                    // NEGATIV geworden ist ("if (Timer < 0f && HasButton() && CouldUse())").
                    // Der Countdown laeuft nur solange Timer >= 0, der letzte Tick ueberschiesst
                    // also ins Negative - genau das muss erhalten bleiben. Mit Max(0, ...) bliebe
                    // der Timer bei 0 kleben: der Button sieht bereit aus, reagiert aber nie mehr.
                    __instance.Timer = __state - delta / mult;
                } catch { }
            }
        }

        // Vanilla-Kill-Timer: dieselbe Delta-Messung wie beim CustomButton. Kein
        // SetKillTimer-Prefix: TORs Ersatz clamped auf KillCooldown (Werte darueber verpuffen) und
        // wird pro Tick zum Herunterzaehlen aufgerufen - ein Multiplikator wuerde sich aufschaukeln.
        //
        // Wichtig ist die Messung statt eines blinden "+dt*(1-1/mult)": der Timer ruht in vielen
        // Situationen (Meeting, Vent, unbewegbar). Blind aufaddieren wuerde ihn dann nach OBEN
        // treiben. Die Obergrenze ist der Wert VOR dem Tick, damit rollenabhaengig laengere
        // Cooldowns (Mini x2, BountyHunter-Zuschlag) nicht auf den Basiswert gekappt werden.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        [HarmonyPriority(Priority.Low)]
        static class KillTimerRatePatch {
            public static void Prefix(PlayerControl __instance, out float __state)
                => __state = __instance.killTimer;

            public static void Postfix(PlayerControl __instance, float __state) {
                try {
                    if (tunes.Count == 0) return;
                    if (!__instance.AmOwner) return;
                    float mult = CdMult(__instance.PlayerId);
                    if (Mathf.Abs(mult - 1f) < 0.0001f) return;
                    float delta = __state - __instance.killTimer;
                    if (delta <= 0f) return; // kein Tick, oder der Timer wurde gerade neu gesetzt
                    // Vanilla gibt bei <= 0 den Kill frei, hier ist 0 also das richtige Minimum.
                    __instance.killTimer = Mathf.Max(0f, __state - delta / mult);
                } catch { }
            }
        }

        // ====================================================================
        // Vent-Verbot (Postfix-Kette; ein Spieler IM Vent wird nie eingesperrt)
        // ====================================================================
        // Grant AND ban share this one postfix - the same hook ChanceMod uses for its vent roll.
        // No vanilla role change is involved: TOR asks roleCanUseVents everywhere it matters
        // (Vent.CanUse, the vent button visibility, the use-vent hotkey), so flipping the answer is
        // all it takes. A player already inside a vent is never locked in.
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.roleCanUseVents))]
        static class VentAccessPatch {
            public static void Postfix(PlayerControl player, ref bool __result) {
                try {
                    if (tunes.Count == 0 || player == null) return;
                    if (player.inVent) return;
                    if (VentBanned(player.PlayerId)) { __result = false; return; }
                    if (!__result && VentGranted(player.PlayerId)) __result = true;
                } catch { }
            }
        }

        // The granted vent button has to be REACHABLE, not just visible: TOR's ImpostorVentButton
        // sits in the same cluster as the role ability buttons, so for a role with buttons it ends
        // up underneath one of them and simply cannot be clicked. Same fix (and same slot grid) as
        // ChanceMod: put it in the first candidate slot no active ability button occupies, anchored
        // to the use button in world space, one z unit forward so the collider stays on top.
        // Re-applied every frame because AspectPosition can reset the transform.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class VentButtonPlacementPatch {
            private static readonly Vector3[] CandidateSlots = {
                TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowRight,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowCenter,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.lowerRowLeft,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowLeft,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowCenter,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowFarLeft,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.upperRowRight,
                TheOtherRoles.Objects.CustomButton.ButtonPositions.highRowRight,
            };
            private static readonly Vector3 FallbackSlot =
                TheOtherRoles.Objects.CustomButton.ButtonPositions.highRowRight + new Vector3(0f, 0.6f, 0f);
            private const float SlotEps = 0.25f;
            private static readonly List<Vector3> occupied = new List<Vector3>();

            public static void Postfix(HudManager __instance) {
                try {
                    if (tunes.Count == 0) return;
                    if (__instance == null || __instance.ImpostorVentButton == null || __instance.UseButton == null) return;
                    var lp = PlayerControl.LocalPlayer;
                    if (lp == null || lp.Data == null) return;
                    if (lp.Data.Role != null && lp.Data.Role.IsImpostor) return; // impostors vent natively
                    if (!VentGranted(lp.PlayerId)) return;

                    var vb = __instance.ImpostorVentButton;
                    if (!vb.isActiveAndEnabled) return;

                    occupied.Clear();
                    foreach (var b in TheOtherRoles.Objects.CustomButton.buttons) {
                        if (b == null || b.mirror) continue;
                        if (b.actionButtonGameObject == null || !b.actionButtonGameObject.activeSelf) continue;
                        occupied.Add(b.PositionOffset);
                    }

                    Vector3 chosen = FallbackSlot;
                    foreach (var slot in CandidateSlots) {
                        bool free = true;
                        foreach (var o in occupied)
                            if (Mathf.Abs(o.x - slot.x) < SlotEps && Mathf.Abs(o.y - slot.y) < SlotEps) { free = false; break; }
                        if (free) { chosen = slot; break; }
                    }

                    Vector3 u = __instance.UseButton.transform.position;
                    vb.transform.position = new Vector3(u.x + chosen.x, u.y + chosen.y, u.z - 1f);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(Vent), nameof(Vent.CanUse))]
        static class VentBanCanUsePatch {
            public static void Postfix(ref float __result,
                [HarmonyArgument(0)] NetworkedPlayerInfo pc,
                [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse) {
                try {
                    if (tunes.Count == 0 || pc == null || !VentBanned(pc.PlayerId)) return;
                    if (pc.Object != null && pc.Object.inVent) return; // Aussteigen bleibt erlaubt
                    canUse = couldUse = false;
                    __result = float.MaxValue;
                } catch { }
            }
        }

        // Der native Impostor-Vent-Button kommt vom RoleBehaviour, nicht von roleCanUseVents.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class VentBanButtonPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (tunes.Count == 0) return;
                    var me = PlayerControl.LocalPlayer;
                    if (me == null || !VentBanned(me.PlayerId) || me.inVent) return;
                    if (__instance.ImpostorVentButton != null && __instance.ImpostorVentButton.isActiveAndEnabled)
                        __instance.ImpostorVentButton.Hide();
                } catch { }
            }
        }

        // Harter lokaler Stopp, falls doch ein Pfad zum Vent-Betreten durchrutscht.
        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.RpcEnterVent))]
        static class VentBanEnterPatch {
            public static bool Prefix(PlayerPhysics __instance) {
                try {
                    if (tunes.Count == 0) return true;
                    if (__instance.myPlayer != null && !__instance.myPlayer.inVent
                        && VentBanned(__instance.myPlayer.PlayerId)) return false;
                } catch { }
                return true;
            }
        }
    }
}
