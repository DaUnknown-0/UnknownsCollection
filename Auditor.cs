// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Auditor (Impostor)
 *
 * Every task a LIVING crewmate finishes lands in the Auditor's queue and shows up as a REAL task in
 * his own task list. Doing it himself resets that exact task back to "open" for that exact crewmate -
 * server-authoritatively, so the task bar really drops and the crew's task win really gets pushed back.
 * His kill cooldown scales with how much of that work he has done: punished at zero reverts (2x by
 * default), rewarded once he has put in the shifts (0.5x at the configured target).
 *
 * The queue holds a configurable number of entries; further completions are simply lost. Each entry
 * has its own lifetime (paused during meetings, frozen while he is actually working on it), the queue
 * itself survives meetings, and an entry dies when its victim is RECOGNISED dead (i.e. at the end of
 * the meeting that revealed it) or when the Auditor dies.
 *
 * WHY THE TWO TASK PATHS DIFFER
 * -----------------------------
 *  - The AUDITOR's copies are LOCAL ONLY. Impostor tasks count for nothing (TasksHandler.taskInfo
 *    excludes them twice over), so there is nothing to sync - and building them locally means a queue
 *    change never throws away the partial progress of the other entries, which a full RpcSetTasks
 *    would. The construction is exactly what vanilla's PlayerControl.CoSetTasks does.
 *  - The VICTIM's reset goes through vanilla RpcSetTasks (host owns the NetworkedPlayerInfo). That is
 *    the only path the SERVER also sees: a client-side "Complete = false" would drop the local bar but
 *    leave the server counting the task as done (measured, see tmp/OGG-era BypassMod notes). Because
 *    RpcSetTasks resets the WHOLE list, the victim's own client re-completes everything else right
 *    after - netting exactly one open task.
 *
 * Options 1600-1609, module byte 214 on the shared UC channel, draft sentinel 218.
 * Full design record: tmp/AUDITOR_PLAN.md. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Auditor {
        // ---- Theme ----
        public static readonly Color Color = Palette.ImpostorRed; // impostor role -> red role tag

        // ---- Options (IDs 1600-1609) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption QueueSize;
        public static CustomOption EntryLifetime;
        public static CustomOption CooldownAtZero;
        public static CustomOption CooldownAtFull;
        public static CustomOption RevertsForFull;
        public static CustomOption ShowCompleter;
        public static CustomOption CannotGuessSnitch;
        public static CustomOption VictimNotice;

        // Victim notification modes. The order IS the option's selection order, and TOR's string[]
        // overload always defaults to index 0 - so the intended default has to sit first.
        public const int NoticeInstant = 0, NoticeMeeting = 1, NoticeOff = 2;

        // ---- Runtime state (synced via RPC) ----
        public static PlayerControl auditor;
        public static bool active;
        public static int revertCount;              // successful reverts this round (drives the cooldown)
        public static float lastOverflow;           // Time.time of the last lost completion (HUD warning)

        // One stolen task. Mirrored on every client (so the HUD and the timers agree); only the host
        // ever adds or removes entries.
        public sealed class Entry {
            public byte id;                 // queue entry id, also the offset of the local task id
            public byte victim;             // PlayerId of the crewmate who completed it
            public byte typeId;             // ShipStatus task index (what RpcSetTasks speaks)
            public uint victimTaskId;       // TaskInfo.Id inside the victim's own task list
            public float remaining;         // seconds until this entry expires
            public bool locked;             // the Auditor is working on it -> no expiry
            public NormalPlayerTask localTask; // only ever set on the Auditor's own client
        }

        private static readonly List<Entry> queue = new List<Entry>();
        public static IReadOnlyList<Entry> Queue => queue;

        // Host-only: completions we ORDERED ourselves (the victim re-completing everything after a
        // reset). Without this the re-completions would instantly refill the queue - and never stop.
        private static readonly Dictionary<byte, HashSet<uint>> pendingRecomplete =
            new Dictionary<byte, HashSet<uint>>();

        // Host-only: rolling entry id.
        private static byte nextEntryId;

        // Local task ids live far above the victim-side 0..n-1 range, so they can never be mistaken
        // for an entry in the Auditor's own (untouched) Data.Tasks list.
        private const uint SyntheticIdBase = 5000;

        // ---- Custom RPC subtypes: module byte 214 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.AuditorRpcId;
        private const byte SubSetAuditor = 0; // playerId
        private const byte SubEnqueue    = 1; // entryId, victimPid, typeId, victimTaskId(uint), lifetime(float)
        private const byte SubDequeue    = 2; // entryId, reason
        private const byte SubReverted   = 3; // entryId, victimPid, count, ids...
        private const byte SubRequest    = 4; // entryId              (Auditor -> host)
        private const byte SubLock       = 5; // entryId, locked      (Auditor -> everyone)
        private const byte SubOverflow   = 6; // -                    (host -> everyone)

        // Dequeue reasons (HUD/logging only).
        public const byte ReasonExpired = 0, ReasonVictimKnownDead = 1, ReasonAuditorDead = 2, ReasonDropped = 3;

        // ---- Role identity ----
        private static RoleInfo auditorInfo;
        public static RoleInfo AuditorInfo() => auditorInfo ??= new RoleInfo(
            "Auditor", Color, "Undo the tasks the crew completes",
            "Undo the crew's tasks", RoleId.Impostor);

        // TOR's float-based Create builds its selection list by accumulating the step in FLOAT
        // (CustomOptions.cs:83-88). With a 0.1/0.05 step the error adds up: the lobby shows raw
        // values like "1,6000003" (display is selections[i].ToString()), and worse, the ctor's
        // Array.IndexOf misses the intended default (2.0 is stored as 2.0000005), silently making
        // index 0 the default. So the two multiplier options build their selections in DOUBLE,
        // rounded to 2 decimals, via the public object[] ctor that Create(float,...) wraps anyway.
        // The half-step tolerance keeps the max value in the list (float accumulation can overshoot
        // max and drop it).
        private static object[] FloatRange(float min, float max, float step) {
            var sels = new List<object>();
            for (double s = min; s <= max + step * 0.5; s += step) sels.Add((float)Math.Round(s, 2));
            return sels.ToArray();
        }

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1600, Types.Impostor, "Auditor",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1601, Types.Impostor, "Auditor Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                QueueSize = CustomOption.Create(1602, Types.Impostor, "Stolen Tasks Kept At Once",
                    3f, 1f, 8f, 1f, SpawnRate);
                EntryLifetime = CustomOption.Create(1603, Types.Impostor, "Stolen Task Lifetime",
                    90f, 30f, 300f, 10f, SpawnRate);
                CooldownAtZero = new CustomOption(1604, Types.Impostor, "Kill Cooldown Multiplier At 0 Reverts",
                    FloatRange(0.5f, 3f, 0.1f), 2f, SpawnRate, false);
                CooldownAtFull = new CustomOption(1605, Types.Impostor, "Kill Cooldown Multiplier At Full Reverts",
                    FloatRange(0.25f, 2f, 0.05f), 0.5f, SpawnRate, false);
                RevertsForFull = CustomOption.Create(1606, Types.Impostor, "Reverts For Full Effect",
                    8f, 1f, 20f, 1f, SpawnRate);
                ShowCompleter = CustomOption.Create(1607, Types.Impostor, "Auditor Sees Who Completed The Task",
                    true, SpawnRate);
                CannotGuessSnitch = CustomOption.Create(1608, Types.Impostor, "Auditor Cannot Guess The Snitch",
                    true, SpawnRate);
                VictimNotice = CustomOption.Create(1609, Types.Impostor, "Victim Notification",
                    new string[] { "Immediately", "At The Next Meeting", "Off" }, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Auditor] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            // Receiver registration on the shared UC channel; every Harmony patch in this file is
            // attribute-based and gets collected by the plugin-wide PatchAll.
            UCRpc.Register(RpcId, HandleModuleRpc);
        }

        // ====================================================================
        // Small helpers
        // ====================================================================
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalAuditor() =>
            active && auditor != null && PlayerControl.LocalPlayer != null
            && auditor.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        // The Spy sits on the Auditor's own impostor list and is the ONLY player there who finishes
        // real tasks - showing the completer's name would out them instantly. So the name is hidden
        // whenever a Spy COULD be in the game, regardless of what option 1607 says (info-leak rule).
        public static bool SpyPossible() {
            try {
                return CustomOptionHolder.spySpawnRate != null
                       && CustomOptionHolder.spySpawnRate.getSelection() > 0;
            } catch { return false; }
        }
        public static bool ShowsCompleterName() =>
            (ShowCompleter?.getBool() ?? true) && !SpyPossible();

        // Linear between the two configured multipliers, frozen at the endpoint (user decision).
        public static float CooldownMultiplier() {
            float atZero = CooldownAtZero?.getFloat() ?? 2f;
            float atFull = CooldownAtFull?.getFloat() ?? 0.5f;
            float target = Mathf.Max(1f, RevertsForFull?.getFloat() ?? 8f);
            return Mathf.Lerp(atZero, atFull, Mathf.Clamp01(revertCount / target));
        }

        public static Entry FindEntry(byte id) {
            for (int i = 0; i < queue.Count; i++) if (queue[i].id == id) return queue[i];
            return null;
        }

        // ====================================================================
        // RPC plumbing
        // ====================================================================
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = UCRpc.Begin(RpcId); // shared UC channel; RpcId is the module byte
            w.Write(subtype);
            return w;
        }

        public static void SendSetAuditor(byte id) {
            try {
                var w = BeginRpc(SubSetAuditor);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetAuditor(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] SendSet failed: {e}"); }
        }

        private static void SendEnqueue(byte entryId, byte victim, byte typeId, uint victimTaskId, float lifetime) {
            try {
                var w = BeginRpc(SubEnqueue);
                w.Write(entryId); w.Write(victim); w.Write(typeId); w.Write(victimTaskId); w.Write(lifetime);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyEnqueue(entryId, victim, typeId, victimTaskId, lifetime);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] SendEnqueue failed: {e}"); }
        }

        private static void SendDequeue(byte entryId, byte reason) {
            try {
                var w = BeginRpc(SubDequeue);
                w.Write(entryId); w.Write(reason);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyDequeue(entryId, reason);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] SendDequeue failed: {e}"); }
        }

        private static void SendReverted(byte entryId, byte victim, List<uint> keepComplete) {
            try {
                var w = BeginRpc(SubReverted);
                w.Write(entryId); w.Write(victim);
                w.Write((byte)Mathf.Min(keepComplete.Count, 255));
                for (int i = 0; i < keepComplete.Count && i < 255; i++) w.Write(keepComplete[i]);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyReverted(entryId, victim, keepComplete);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] SendReverted failed: {e}"); }
        }

        private static void SendRequest(byte entryId) {
            try {
                var w = BeginRpc(SubRequest);
                w.Write(entryId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                if (AmHost()) HostHandleRequest(entryId); // the sender never receives its own broadcast
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] SendRequest failed: {e}"); }
        }

        private static void SendLock(byte entryId, bool locked) {
            try {
                var w = BeginRpc(SubLock);
                w.Write(entryId); w.Write(locked);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyLock(entryId, locked);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] SendLock failed: {e}"); }
        }

        private static void SendOverflow() {
            try {
                var w = BeginRpc(SubOverflow);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyOverflow();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] SendOverflow failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSetAuditor: { byte id = reader.ReadByte();
                        // Host-authoritative role assignment (host pick in IntroCutscene.OnDestroy / UCRoleDraft) - a
                    // forged one would let any client declare any player this role (AUDIT H-3).
                        if (UCRpc.RequireHost("Auditor.SetAuditor")) ApplySetAuditor(id); break; }
                    case SubEnqueue: {
                        byte entryId = reader.ReadByte();
                        byte victim = reader.ReadByte();
                        byte typeId = reader.ReadByte();
                        uint taskId = reader.ReadUInt32();
                        float life = reader.ReadSingle();
                        ApplyEnqueue(entryId, victim, typeId, taskId, life);
                        break;
                    }
                    case SubDequeue: {
                        byte entryId = reader.ReadByte();
                        byte reason = reader.ReadByte();
                        ApplyDequeue(entryId, reason);
                        break;
                    }
                    case SubReverted: {
                        byte entryId = reader.ReadByte();
                        byte victim = reader.ReadByte();
                        int n = reader.ReadByte();
                        var ids = new List<uint>(n);
                        for (int i = 0; i < n; i++) ids.Add(reader.ReadUInt32());
                        ApplyReverted(entryId, victim, ids);
                        break;
                    }
                    case SubRequest: {
                        byte entryId = reader.ReadByte();
                        if (AmHost()) HostHandleRequest(entryId);
                        break;
                    }
                    case SubLock: {
                        byte entryId = reader.ReadByte();
                        bool locked = reader.ReadBoolean();
                        ApplyLock(entryId, locked);
                        break;
                    }
                    case SubOverflow: ApplyOverflow(); break;
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] HandleRpc failed: {e}");
            }
        }

        // ====================================================================
        // Apply (runs on every client)
        // ====================================================================
        private static void ApplySetAuditor(byte id) {
            auditor = Helpers.playerById(id);
            active = auditor != null;
            revertCount = 0;
            ClearQueue();
            if (!active) return;
            UCPromotion.Claim(id);
            // His vanilla impostor fake-task list is replaced by the revert targets (user decision),
            // so tear the local objects down. Deliberately NOT Helpers.clearAllTasks: that also empties
            // the SYNCED Data.Tasks, and we have no reason to touch synced state for a role whose
            // tasks count for nothing anyway.
            if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == id)
                ClearLocalTaskObjects(PlayerControl.LocalPlayer);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Auditor] The Auditor is {auditor.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySetAuditor(playerId);

        private static void ApplyEnqueue(byte entryId, byte victim, byte typeId, uint victimTaskId, float lifetime) {
            if (FindEntry(entryId) != null) return; // idempotent
            var e = new Entry {
                id = entryId, victim = victim, typeId = typeId,
                victimTaskId = victimTaskId, remaining = lifetime, locked = false,
            };
            queue.Add(e);
            if (IsLocalAuditor()) {
                e.localTask = BuildLocalTask(typeId, entryId);
                UCAssets.PlayAuditorStamp();
            }
        }

        private static void ApplyDequeue(byte entryId, byte reason) {
            var e = FindEntry(entryId);
            if (e == null) return;
            DestroyLocalTask(e);
            queue.Remove(e);
        }

        private static void ApplyLock(byte entryId, bool locked) {
            var e = FindEntry(entryId);
            if (e != null) e.locked = locked;
        }

        private static void ApplyOverflow() {
            lastOverflow = Time.time;
        }

        // The reset itself already travelled as a vanilla RpcSetTasks from the host. This message
        // carries the follow-up: the victim re-completes everything that must STAY complete, so net
        // exactly one task is open again - server-visible, because these are real vanilla RPCs.
        private static void ApplyReverted(byte entryId, byte victim, List<uint> keepComplete) {
            var e = FindEntry(entryId);
            if (e != null) { DestroyLocalTask(e); queue.Remove(e); }
            revertCount++;

            var me = PlayerControl.LocalPlayer;
            if (me == null) return;

            if (me.PlayerId == victim) {
                foreach (uint id in keepComplete) {
                    try { me.RpcCompleteTask(id); } catch { }
                }
                NotifyVictim();
            }
            if (IsLocalAuditor()) {
                UCAssets.PlayAuditorRevert();
                try { Helpers.showFlash(new Color(0.85f, 0.15f, 0.15f, 0.25f), 0.25f); } catch { }
            }
            try { GameData.Instance?.RecomputeTaskCounts(); } catch { }
        }

        // ---- Victim feedback (never names the culprit) ----
        private static bool victimNoticePending;

        private static void NotifyVictim() {
            int mode = VictimNotice?.getSelection() ?? NoticeInstant;
            if (mode == NoticeOff) return;
            if (mode == NoticeMeeting) { victimNoticePending = true; return; }
            AuditorHud.ShowVictimNotice();
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class VictimNoticeMeetingPatch {
            public static void Postfix() {
                try {
                    if (!victimNoticePending) return;
                    victimNoticePending = false;
                    var hud = HudManager.Instance;
                    var me = PlayerControl.LocalPlayer;
                    if (hud?.Chat == null || me == null) return;
                    hud.Chat.AddChat(me, UCLocalization.Tr("uc.ui.auditor.victim_chat")); // local-only
                } catch { }
            }
        }

        // ====================================================================
        // Local task objects (Auditor's client only)
        //
        // Straight out of vanilla's PlayerControl.CoSetTasks: instantiate the ShipStatus prototype,
        // give it an id/owner, Initialize() (which picks its consoles and builds the arrow) and drop
        // it into myTasks. Console.CanUse reads myTasks, so the consoles open for him.
        // ====================================================================
        private static NormalPlayerTask BuildLocalTask(byte typeId, byte entryId) {
            try {
                var me = PlayerControl.LocalPlayer;
                var ship = ShipStatus.Instance;
                if (me == null || ship == null) return null;
                var proto = ship.GetTaskById(typeId);
                if (proto == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Auditor] no task prototype for type {typeId}.");
                    return null;
                }
                var task = UnityEngine.Object.Instantiate(proto, me.transform);
                task.Id = SyntheticIdBase + entryId;
                task.Owner = me;
                task.Initialize();
                me.myTasks.Add(task);
                return task;
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] BuildLocalTask failed: {e}");
                return null;
            }
        }

        private static void DestroyLocalTask(Entry e) {
            try {
                if (e?.localTask == null) return;
                var me = PlayerControl.LocalPlayer;
                if (me != null && me.myTasks != null) me.myTasks.Remove(e.localTask);
                e.localTask.OnRemove();
                UnityEngine.Object.Destroy(e.localTask.gameObject);
            } catch { }
            finally { if (e != null) e.localTask = null; }
        }

        // Local-only variant of Helpers.clearAllTasks: destroys the task OBJECTS but leaves the synced
        // Data.Tasks alone (see ApplySetAuditor for why). Sabotage tasks are spared - they are not
        // NormalPlayerTasks, they are added by the game while a sabotage runs, and half the mod
        // (comms detection, the fix prompt) reads them out of myTasks.
        private static void ClearLocalTaskObjects(PlayerControl player) {
            try {
                if (player == null || player.myTasks == null) return;
                for (int i = player.myTasks.Count - 1; i >= 0; i--) {
                    var t = player.myTasks[i];
                    if (t == null || t.TryCast<NormalPlayerTask>() == null) continue;
                    player.myTasks.RemoveAt(i);
                    t.OnRemove();
                    UnityEngine.Object.Destroy(t.gameObject);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] ClearLocalTaskObjects failed: {e}");
            }
        }

        // The Auditor's list must only ever contain revert targets. His vanilla fake tasks are torn
        // down when he is promoted, but a later task assignment (draft ordering, host tooling, another
        // mod) could put some back - so anything that is a NormalPlayerTask without one of our
        // synthetic ids gets dropped here. Sabotage tasks are untouched (see above).
        private static void PruneForeignTasks() {
            try {
                var me = PlayerControl.LocalPlayer;
                if (me == null || me.myTasks == null) return;
                for (int i = me.myTasks.Count - 1; i >= 0; i--) {
                    var t = me.myTasks[i];
                    if (t == null || t.Id >= SyntheticIdBase) continue;
                    if (t.TryCast<NormalPlayerTask>() == null) continue;
                    me.myTasks.RemoveAt(i);
                    t.OnRemove();
                    UnityEngine.Object.Destroy(t.gameObject);
                }
            } catch { }
        }

        private static void ClearQueue() {
            for (int i = queue.Count - 1; i >= 0; i--) DestroyLocalTask(queue[i]);
            queue.Clear();
        }

        // ====================================================================
        // Host: picking up crew completions
        //
        // PlayerControl.CompleteTask is the RECEIVING side of the vanilla task RPC and runs on every
        // client for every player - so on the host it fires for the whole lobby.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
        static class CompleteTaskPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] uint idx) {
                try {
                    if (!AmHost() || !active || __instance == null || __instance.Data == null) return;

                    // Our own re-completions after a reset must never refill the queue.
                    if (pendingRecomplete.TryGetValue(__instance.PlayerId, out var expected)
                        && expected.Remove(idx)) {
                        if (expected.Count == 0) pendingRecomplete.Remove(__instance.PlayerId);
                        return;
                    }

                    if (!IsAlive(auditor)) return;
                    if (__instance.PlayerId == auditor.PlayerId) return;
                    if (!IsAlive(__instance)) return;                       // "solange der Crewmate lebt"
                    var role = __instance.Data.Role;
                    if (role == null || role.IsImpostor || !role.TasksCountTowardProgress) return;
                    if (__instance.hasFakeTasks()) return;

                    int cap = Mathf.RoundToInt(QueueSize?.getFloat() ?? 3f);
                    if (queue.Count >= cap) { SendOverflow(); return; }

                    var info = __instance.Data.FindTaskById(idx);
                    if (info == null) return;

                    byte entryId = nextEntryId++;
                    SendEnqueue(entryId, __instance.PlayerId, info.TypeId, idx,
                                EntryLifetime?.getFloat() ?? 90f);
                    UnknownsCollectionPlugin.Logger?.LogInfo(
                        $"[Auditor] queued task {info.TypeId} of {__instance.Data.PlayerName} (entry {entryId}).");
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] CompleteTask postfix failed: {e}");
                }
            }
        }

        // ====================================================================
        // Auditor side: finishing a stolen task
        // ====================================================================

        // The moment a task finishes on its OWNER's client - independent of whatever the RPC layer
        // does with it afterwards (our synthetic ids never leave this machine, see the prefix below).
        [HarmonyPatch(typeof(NormalPlayerTask), nameof(NormalPlayerTask.Complete))]
        static class TaskCompletePatch {
            public static void Postfix(NormalPlayerTask __instance) {
                try {
                    if (__instance == null || !IsLocalAuditor()) return;
                    var e = queue.FirstOrDefault(q => q.localTask != null && q.localTask.Id == __instance.Id);
                    if (e == null) return;
                    SendRequest(e.id);
                } catch (Exception ex) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] task complete hook failed: {ex}");
                }
            }
        }

        // The Auditor's task ids exist ONLY on his machine, so the vanilla completion RPC would hand
        // every other client an id its FindTaskById cannot resolve. Suppress it entirely: the visual
        // strike-through in the task list hangs off taskStep (set by NextStep before Complete runs),
        // not off this call.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcCompleteTask))]
        [HarmonyPriority(Priority.First)]
        static class SuppressSyntheticCompletePatch {
            public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] uint idx) {
                try {
                    if (idx < SyntheticIdBase) return true;
                    if (__instance == null || !IsLocalAuditor()) return true;
                    return __instance.PlayerId != auditor.PlayerId;
                } catch { return true; }
            }
        }

        // Freeze the lifetime while he actually works on an entry: an open minigame on that entry's
        // task locks it, closing unlocks again unless he already made partial progress.
        //
        // DELIBERATELY NOT a Harmony patch on Minigame.Begin/Close. A detour on Minigame.Close(bool)
        // broke the ENTIRE process's HTTP stack (empty bodies on every UnityWebRequest/HttpClient
        // call, EOS sign-in hung) - bisected 2026-08-03. Il2Cpp's linker DEDUPLICATES identical
        // method bodies, so a detour on a tiny method like Close(bool) can silently detour an
        // unrelated method sharing its machine code - here something in the network path. Polling
        // Minigame.Instance from the per-frame tick observes the same state with no detour at all
        // (and catches ForceClose paths for free).
        private static void PollMinigameLock() {
            try {
                if (!IsLocalAuditor()) return;
                var mg = Minigame.Instance;
                bool hasOpen = mg != null && mg.MyNormTask != null;
                uint openId = hasOpen ? mg.MyNormTask.Id : 0;
                foreach (var e in queue) {
                    if (e.localTask == null) continue;
                    bool shouldLock = hasOpen && e.localTask.Id == openId;
                    if (shouldLock && !e.locked) SendLock(e.id, true);
                    else if (!shouldLock && e.locked && e.localTask.taskStep == 0) SendLock(e.id, false);
                }
            } catch { }
        }

        // ====================================================================
        // Host: executing a revert
        // ====================================================================
        private static void HostHandleRequest(byte entryId) {
            try {
                var e = FindEntry(entryId);
                if (e == null) return;
                var victim = Helpers.playerById(e.victim);
                if (victim == null || victim.Data == null || victim.Data.Tasks == null) {
                    SendDequeue(entryId, ReasonDropped);
                    return;
                }

                // Rebuild the victim's list unchanged (same order -> RpcSetTasks hands out the same
                // ids again, so every other queue entry of this victim stays valid) and remember
                // which entries have to be re-completed afterwards.
                int count = victim.Data.Tasks.Count;
                var typeIds = new Il2CppStructArray<byte>(count);
                var keepComplete = new List<uint>();
                bool targetWasComplete = false;
                for (int i = 0; i < count; i++) {
                    var t = victim.Data.Tasks[i];
                    if (t == null) { SendDequeue(entryId, ReasonDropped); return; }
                    typeIds[i] = t.TypeId;
                    if (t.Id == e.victimTaskId) { targetWasComplete = t.Complete; continue; }
                    if (t.Complete) keepComplete.Add(t.Id);
                }
                if (!targetWasComplete) { // already undone by something else - nothing to take back
                    SendDequeue(entryId, ReasonDropped);
                    return;
                }

                if (!pendingRecomplete.TryGetValue(e.victim, out var expected)) {
                    expected = new HashSet<uint>();
                    pendingRecomplete[e.victim] = expected;
                }
                foreach (uint id in keepComplete) expected.Add(id);

                victim.Data.RpcSetTasks(typeIds);   // the reset the SERVER also sees
                SendReverted(entryId, e.victim, keepComplete);
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Auditor] reverted task {e.typeId} of {victim.Data.PlayerName} " +
                    $"({keepComplete.Count} tasks re-completed).");
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] HostHandleRequest failed: {ex}");
            }
        }

        // ====================================================================
        // Per-frame: lifetime countdown (everyone) + host-side expiry/cleanup
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!active) return;
                    bool meeting = InMeeting();

                    if (!meeting && IsLocalAuditor()) {
                        PruneForeignTasks();
                        PollMinigameLock();
                    }

                    // Everyone counts down locally so the HUD stays smooth; only the host removes.
                    if (!meeting) {
                        float dt = Time.deltaTime;
                        for (int i = 0; i < queue.Count; i++)
                            if (!queue[i].locked) queue[i].remaining = Mathf.Max(0f, queue[i].remaining - dt);
                    }

                    if (!AmHost()) return;

                    if (!IsAlive(auditor)) {
                        for (int i = queue.Count - 1; i >= 0; i--) SendDequeue(queue[i].id, ReasonAuditorDead);
                        return;
                    }
                    if (meeting) return;
                    for (int i = queue.Count - 1; i >= 0; i--) {
                        var e = queue[i];
                        if (e.locked || e.remaining > 0f) continue;
                        SendDequeue(e.id, ReasonExpired);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] tick failed: {e}");
                }
            }
        }

        // "Sobald der Crewmate als tot erkannt wird" - the meeting that revealed the death is over,
        // so every entry belonging to a dead victim dies with it.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() {
                try {
                    if (!AmHost() || !active) return;
                    for (int i = queue.Count - 1; i >= 0; i--) {
                        var victim = Helpers.playerById(queue[i].victim);
                        if (victim == null || victim.Data == null || victim.Data.IsDead || victim.Data.Disconnected)
                            SendDequeue(queue[i].id, ReasonVictimKnownDead);
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] meeting cleanup failed: {e}");
                }
            }
        }

        // ====================================================================
        // Kill cooldown: rate-scaling of the vanilla timer.
        //
        // Same technique as PlayerTuning.KillTimerRatePatch (measure the tick, divide by the
        // multiplier) - a SetKillTimer prefix would fight TOR's own clamp and re-trigger every tick.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        [HarmonyPriority(Priority.Low)]
        static class KillTimerRatePatch {
            public static void Prefix(PlayerControl __instance, out float __state)
                => __state = __instance.killTimer;

            public static void Postfix(PlayerControl __instance, float __state) {
                try {
                    if (!active || auditor == null) return;
                    if (!__instance.AmOwner || __instance.PlayerId != auditor.PlayerId) return;
                    float mult = CooldownMultiplier();
                    if (Mathf.Abs(mult - 1f) < 0.0001f) return;
                    float delta = __state - __instance.killTimer;
                    if (delta <= 0f) return; // no tick, or the timer was just re-set
                    __instance.killTimer = Mathf.Max(0f, __state - delta / mult);
                } catch { }
            }
        }

        // ====================================================================
        // Spawn pick (host) + resets
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (UCRoleDraft.DraftWillRun()) return;
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 6f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(UCPromotion.IsPlainImpostor).ToList();
                    if (candidates.Count == 0) return;
                    SendSetAuditor(candidates[rnd.Next(candidates.Count)].PlayerId);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] IntroEnd pick failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                auditor = null;
                active = false;
                revertCount = 0;
                nextEntryId = 0;
                lastOverflow = 0f;
                victimNoticePending = false;
                pendingRecomplete.Clear();
                ClearQueue();
            }
        }

        // PlayerId-keyed state must ALSO be cleared when joining another lobby - resetVariables alone
        // leaks it into the next lobby (see the resetVariables-Lobby-Leak rule).
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() {
                auditor = null;
                active = false;
                revertCount = 0;
                nextEntryId = 0;
                victimNoticePending = false;
                pendingRecomplete.Clear();
                queue.Clear(); // objects belong to the old scene; nothing to destroy here
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || auditor == null || p == null || p != auditor || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++) {
                        if (__result[i] != null && __result[i].roleId == RoleId.Impostor) {
                            __result[i] = AuditorInfo();
                            replaced = true;
                        }
                    }
                    if (!replaced) __result.Insert(0, AuditorInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Auditor] RoleInfo postfix failed: {e}");
                }
            }
        }
    }
}
