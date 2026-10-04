// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * The Surveyor (Crewmate)
 *
 * A small, fixed admin table (User 2026-10-02). The Surveyor marks up to three rooms by standing in
 * them and pressing his button; from then on a line on his HUD always shows how many other living
 * players are in each marked room. A fourth mark replaces the oldest one. Good placement is the skill.
 *
 *  - LOCAL ONLY: nobody else sees the marks or the counts, so after the host's pick nothing goes over
 *    the network. Rooms come from ShipStatus.FastRooms (roomArea collider), the same rooms the admin
 *    table counts - Unknown's Atlas extra rooms included.
 *  - COMMS: during a comms sabotage the counts read "?" (the admin table goes dark too).
 *  - MEETINGS: the marks stay (option, on by default); the HUD line hides while a meeting is open.
 *
 * Crew tag over a plain Crewmate (keeps his tasks). Options 1745-1749, RPC module 226 (Sub 0 set),
 * draft sentinel 223. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Surveyor {
        // Survey green-teal, between the Scout's teal and the Medic's green.
        public static readonly Color Color = new Color(0.30f, 0.80f, 0.62f);

        // ---- Options (IDs 1745-1749) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption MaxMarks;
        public static CustomOption MarkCooldown;
        public static CustomOption KeepMarks;      // marks survive meetings

        // ---- Runtime state ----
        public static PlayerControl surveyor;
        public static bool active;
        private static readonly List<SystemTypes> marks = new List<SystemTypes>();
        private static CustomButton markButton;
        private static TMPro.TextMeshPro hudText;

        private const byte RpcId = UnknownsCollectionPlugin.SurveyorRpcId;
        private const byte SubSet = 0;

        private static RoleInfo info;
        public static RoleInfo SurveyorInfo() => info ??= new RoleInfo(
            "Surveyor", Color, "Mark rooms and always see how many players are inside",
            "Watch the rooms you marked", RoleId.Crewmate);

        private static readonly System.Random rnd = new System.Random();

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1745, Types.Crewmate, "Surveyor", CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1746, Types.Crewmate, "Surveyor Minimum Players To Spawn",
                    6f, 4f, 15f, 1f, SpawnRate);
                MaxMarks = CustomOption.Create(1747, Types.Crewmate, "Surveyor Marked Rooms", 3f, 1f, 3f, 1f, SpawnRate);
                MarkCooldown = CustomOption.Create(1748, Types.Crewmate, "Surveyor Mark Cooldown", 15f, 5f, 60f, 2.5f, SpawnRate);
                KeepMarks = CustomOption.Create(1749, Types.Crewmate, "Marks Stay After A Meeting", true, SpawnRate);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Surveyor] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Surveyor] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) => UCRpc.Register(RpcId, HandleModuleRpc);

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static bool InMeeting() => MeetingHud.Instance != null || ExileController.Instance != null;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalSurveyor() =>
            active && surveyor != null && PlayerControl.LocalPlayer != null && surveyor.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        public static void SendSet(byte id) {
            try {
                var w = UCRpc.Begin(RpcId);
                w.Write(SubSet);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Surveyor] SendSet failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                if (sub == SubSet) {
                    byte id = reader.ReadByte();
                    if (UCRpc.RequireHost("Surveyor.Set")) ApplySet(id);
                }
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Surveyor] HandleRpc failed: {e}"); }
        }

        private static void ApplySet(byte id) {
            surveyor = id == byte.MaxValue ? null : Helpers.playerById(id);
            active = surveyor != null;
            marks.Clear();
            if (!active) return;
            UCPromotion.Claim(id);
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Surveyor] The Surveyor is {surveyor.Data?.PlayerName}.");
        }

        public static void MarkFromDraft(byte playerId) => ApplySet(playerId);

        // ---- Pick (host, random path) ----
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
                    UnknownsCollectionPlugin.Logger?.LogError($"[Surveyor] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- Rooms ----
        // AllRooms, like the game's own room tracker: Unknown's Atlas builds some rooms from several
        // areas of the same SystemType, and FastRooms holds only one of them (the first test marked the
        // Rotunda while standing in the Gallery).
        // Overlapping areas (an Atlas room laid over a neighbour) resolve to the SMALLEST one that
        // contains the point - the most specific room, which is what the game's room tracker shows.
        private static PlainShipRoom RoomAt(Vector2 pos) {
            var ship = ShipStatus.Instance;
            if (ship == null || ship.AllRooms == null) return null;
            PlainShipRoom best = null;
            float bestArea = float.MaxValue;
            foreach (var room in ship.AllRooms) {
                if (room == null || room.roomArea == null || room.RoomId == SystemTypes.Hallway) continue;
                if (!room.roomArea.OverlapPoint(pos)) continue;
                var size = room.roomArea.bounds.size;
                float area = size.x * size.y;
                if (area < bestArea) { bestArea = area; best = room; }
            }
            return best;
        }

        private static bool InRoom(SystemTypes type, Vector2 pos) => RoomAt(pos)?.RoomId == type;

        // The Surveyor's own room: exactly what the game's room tracker shows him.
        private static PlainShipRoom LocalRoom() {
            try {
                var tracked = FastDestroyableSingleton<HudManager>.Instance?.roomTracker?.LastRoom;
                if (tracked != null && tracked.RoomId != SystemTypes.Hallway) return tracked;
            } catch { }
            var lp = PlayerControl.LocalPlayer;
            return lp != null ? RoomAt(lp.GetTruePosition()) : null;
        }

        private static string RoomName(SystemTypes type) {
            try { return FastDestroyableSingleton<TranslationController>.Instance.GetString(type); }
            catch { return type.ToString(); }
        }

        private static bool CommsSabotaged() {
            try {
                foreach (PlayerTask task in PlayerControl.LocalPlayer.myTasks.GetFastEnumerator())
                    if (task != null && task.TaskType == TaskTypes.FixComms) return true;
            } catch { }
            return false;
        }

        internal static void DiagMarkHere() => Mark();

        private static void Mark() {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null) return;
            var room = LocalRoom();
            if (room == null) return;
            var type = room.RoomId;
            if (marks.Contains(type)) return;   // the button is dark for a marked room (CanMarkHere)
            marks.Add(type);
            int max = Mathf.RoundToInt(MaxMarks?.getFloat() ?? 3f);
            while (marks.Count > max) marks.RemoveAt(0);
            try { Helpers.showFlash(Color, 0.5f, UCLocalization.Tr("uc.ui.surveyor.marked", RoomName(type))); } catch { }
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Surveyor] marked {type} ({marks.Count}/{max}).");
        }

        // Marking the room again did nothing but cost the cooldown, without a word (audit 04.10.).
        private static bool CanMarkHere() {
            var room = LocalRoom();
            return room != null && !marks.Contains(room.RoomId);
        }

        // ---- HUD line: "Electrical 2 · MedBay 0" ----
        private static float nextHud;

        private static void HudTick(HudManager hud) {
            if (!IsLocalSurveyor()) { if (hudText != null) hudText.gameObject.SetActive(false); return; }
            if (Time.time < nextHud) return;
            nextHud = Time.time + 0.25f;
            var lp = PlayerControl.LocalPlayer;
            bool show = lp != null && lp.Data != null && !lp.Data.IsDead && !InMeeting() && marks.Count > 0;
            if (hudText == null) {
                if (!show) return;
                // a clone of a live HUD label brings the game's font and material (a bare
                // TextMeshPro has no font asset in Among Us and stays invisible)
                // the room tracker's text: white with an outline, the same look as the room name below it
                var src = hud.roomTracker != null ? hud.roomTracker.text : null;
                if (src == null) return;
                hudText = UnityEngine.Object.Instantiate(src, hud.transform);
                hudText.gameObject.name = "UCSurveyorHud";
                // the RoomTracker sits on the same object as its text, so the clone carried a second tracker
                // (and an AspectPosition) that kept dragging it onto the room name
                var tracker = hudText.GetComponent<RoomTracker>();
                if (tracker != null) UnityEngine.Object.DestroyImmediate(tracker);
                var ap = hudText.GetComponent<AspectPosition>();
                if (ap != null) UnityEngine.Object.DestroyImmediate(ap);
                hudText.transform.localPosition = new Vector3(0f, -1.25f, -10f);   // half a unit above the room tracker (about -1.75)
                hudText.transform.localScale = Vector3.one;
                hudText.fontSize = 2f;
                hudText.fontSizeMin = 2f; hudText.fontSizeMax = 2f;
                hudText.enableAutoSizing = false;
                hudText.rectTransform.sizeDelta = new Vector2(12f, 1f);
                hudText.color = Color.white;
                hudText.enableVertexGradient = false;
                hudText.alignment = TMPro.TextAlignmentOptions.Center;
                hudText.enableWordWrapping = false;
                hudText.outlineWidth = 0.2f;
                hudText.outlineColor = new Color32(0, 0, 0, 255);
            }
            hudText.gameObject.SetActive(show);
            if (!show) return;
            bool comms = CommsSabotaged();
            var parts = new List<string>();
            foreach (var type in marks) {
                string count = "?";
                if (!comms) {
                    int n = 0;
                    foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                        if (p == null || p == lp || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
                        if (p.inVent) continue;   // like the admin table: a player in a vent has no collider there (04.10.)
                        if (InRoom(type, p.GetTruePosition())) n++;
                    }
                    count = n.ToString();
                }
                parts.Add($"{RoomName(type)} <b>{count}</b>");
            }
            string hex = ColorUtility.ToHtmlStringRGB(Color);
            hudText.text = $"<color=#{hex}>{string.Join("   ", parts)}</color>";
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix(HudManager __instance) {
                try { if (active) HudTick(__instance); } catch { }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try { if (active && !(KeepMarks?.getBool() ?? true)) marks.Clear(); } catch { }
            }
        }

        // ---- Button ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(HudManager __instance) {
                try {
                    hudText = null;   // the old HUD object died with the previous HudManager
                    var sprite = UCAssets.SurveyorIcon ?? Helpers.loadSpriteFromResources("TheOtherRoles.Resources.PlaceCameraButton.png", 115f);
                    markButton = new CustomButton(
                        () => { Mark(); markButton.Timer = markButton.MaxTimer; },
                        () => IsLocalSurveyor() && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove && CanMarkHere(),
                        () => { },
                        sprite,
                        CustomButton.ButtonPositions.lowerRowRight,
                        __instance, KeyCode.F, false, UCLocalization.Tr("uc.ui.surveyor.button"));
                    markButton.MaxTimer = MarkCooldown?.getFloat() ?? 15f;
                    markButton.Timer = 5f;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Surveyor] Button creation failed: {e}");
                }
            }
        }

        // ---- Role identity ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || surveyor == null || p == null || p != surveyor || __result == null) return;
                    bool replaced = false;
                    for (int i = 0; i < __result.Count; i++)
                        if (__result[i] != null && __result[i].roleId == RoleId.Crewmate) { __result[i] = SurveyorInfo(); replaced = true; }
                    if (!replaced) __result.Insert(0, SurveyorInfo());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Surveyor] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            surveyor = null;
            active = false;
            marks.Clear();
            if (hudText != null) hudText.gameObject.SetActive(false);
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Surveyor", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Surveyor", FullReset);
        }
    }
}
