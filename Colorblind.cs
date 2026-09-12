// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Colorblind (MODIFIER)
 *
 * The carrier plays on a black-and-white TV: the whole frame is desaturated - the map, every
 * player, hats, the HUD, the meeting, the chat, kill flashes, all of it - for the whole game,
 * dead or alive. "Red vented" is not a sentence the Colorblind can ever say; they have to work
 * with names, hats and positions. Nobody else sees anything; the grey is purely local.
 * Option (on by default): task minigames stay in colour, because Wires or the sample sorting
 * are guesswork without it and the modifier is meant to be a perception handicap, not a task
 * blocker.
 *
 * HOW. A real post effect, no custom shader file: the game ships "Unlit/DesatShader" (TOR
 * greys the ability buttons with it, property _Desat). A CommandBuffer on the main camera at
 * AfterEverything copies the camera target into a screen-sized temporary RT and blits it back
 * through that material with _Desat = 1. Command buffers run inside the camera's own render, so
 * screen-space UGUI overlays (help panels, host tooling) stay untouched on top. The camera
 * object changes between scenes, so the buffer is re-attached whenever Camera.main is a new
 * object, and removed the moment the grey is not wanted (lobby, end screen, an open minigame
 * with the option on).
 *
 * THE CURE (option, on by default): the MedBay scan. The host appends the map's "Submit Scan"
 * task to the carrier's list right after the pick (PlayerTuning.SendSetTasks: HUD teardown plus
 * the vanilla RpcSetTasks the server sees; an impostor carrier gets it as a fake task, the scan
 * still runs). For the uncured carrier the scanner prints its own diagnosis instead of the
 * height/weight lines (MedScanMinigame.completeString, set in a Begin postfix); when the scan
 * finishes (NormalPlayerTask.NextStep on a SubmitScan task) colour returns for good, ghost
 * included, and everyone is told (Sub 1 cured) so the host log shows it. Maps without a MedBay
 * scanner (Airship, Fungle) have no cure: the log says so, the modifier stays for the game.
 *
 * ARCHITECTURE: modifier over any role (the Gambler pattern), host-authoritative pick, custom RPC
 * module 224 on UCRpc.CallId = 230, gated on "everyone has the mod". Options 1685-1689, display
 * RoleId sentinel 235, no draft entry. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using UnityEngine.Rendering;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UnknownsCollection {
    public static class Colorblind {
        // ---- Theme ----
        // A cool slate: the one grey that is not the Follower's neutral grey.
        public static readonly Color Color = new Color(0.55f, 0.58f, 0.64f);

        // ---- Options (IDs 1685-1689) ----
        public static CustomOption SpawnRate;
        public static CustomOption SpawnMinPlayers;
        public static CustomOption WhoCanBe;            // 0 crew only, 1 anyone
        public static CustomOption TasksInColour;
        public static CustomOption ScanCures;
        public static CustomOption NonCrewCure;         // neutrals and impostors get the cure too
        public static CustomOption CureScanLength;      // 0 normal, 1 long, 2 very long (everyone alike)

        // ---- Runtime state ----
        public static PlayerControl carrier;
        public static bool active;
        public static bool cured;
        private static byte carrierId = byte.MaxValue;

        // ---- Custom RPC subtypes: module byte 224 in the shared UC channel (UCRpc.CallId = 230) ----
        private const byte RpcId = UnknownsCollectionPlugin.ColorblindRpcId;
        private const byte SubSet = 0;          // playerId (255 = clear)      host -> everyone
        private const byte SubCured = 1;        // playerId                    carrier -> everyone

        private static readonly System.Random rnd = new System.Random();

        // ---- Identity (display-only sentinel RoleId) ----
        private const RoleId ColorblindRoleId = (RoleId)235;
        private static RoleInfo info;
        public static RoleInfo Info() => info ??= new RoleInfo(
            "Colorblind", Color, "You see the world in black and white - a MedBay scan can cure you",
            "Sees everything in black and white", ColorblindRoleId, false, true);

        private static bool CureEnabled() => ScanCures?.getBool() ?? true;
        private static bool NonCrewCureOn() => NonCrewCure?.getBool() ?? true;
        private static int ExtraScanLines() => (CureScanLength?.getSelection() ?? 1) switch { 1 => 2, 2 => 3, _ => 0 };

        // Impostor or neutral: the scanner is not theirs by vanilla rules (impostors are refused by
        // the console, neutrals may use it but their tasks count for nothing).
        private static bool IsNonCrew(PlayerControl p) {
            try {
                if (p?.Data?.Role == null) return false;
                if (p.Data.Role.IsImpostor) return true;
                var ri = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                return ri != null && ri.isNeutral;
            } catch { return false; }
        }

        // Does the carrier get the cure at all (option 1689, and 1690 for non-crew)?
        private static bool CureFor(PlayerControl p) => CureEnabled() && (!IsNonCrew(p) || NonCrewCureOn());

        public static void CreateOptions() {
            try {
                SpawnRate = CustomOption.Create(1685, Types.Modifier, "Colorblind",
                    CustomOptionHolder.rates, null, true);
                SpawnMinPlayers = CustomOption.Create(1686, Types.Modifier, "Colorblind Minimum Players To Spawn",
                    5f, 4f, 15f, 1f, SpawnRate);
                WhoCanBe = CustomOption.Create(1687, Types.Modifier, "Colorblind Can Be",
                    new string[] { "Crew Only", "Anyone" }, SpawnRate);
                TasksInColour = CustomOption.Create(1688, Types.Modifier, "Colorblind Sees Tasks In Colour",
                    true, SpawnRate);
                ScanCures = CustomOption.Create(1689, Types.Modifier, "MedBay Scan Cures The Colorblind",
                    true, SpawnRate);
                NonCrewCure = CustomOption.Create(1690, Types.Modifier, "Neutral And Impostor Cure",
                    true, ScanCures);
                // One length for EVERY cure scan: observers can time a scan, so a per-faction length
                // would give the faction away just like a per-faction beam colour would.
                // Constructor form for a non-zero default (Create(string[]) always defaults to index 0).
                CureScanLength = new CustomOption(1691, Types.Modifier, "Cure Scan Length",
                    new object[] { "Normal", "Long", "Very Long" }, "Long", ScanCures, false);
                UnknownsCollectionPlugin.Logger?.LogInfo("[Colorblind] Options created.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Colorblind] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            UCRpc.Register(RpcId, HandleModuleRpc);
            UCFx.RegisterTick(Tick);
            UCFx.RegisterReset(DetachFx);
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static int LobbyPlayerCount() =>
            PlayerControl.AllPlayerControls.ToArray().Count(p => p != null && p.Data != null && !p.Data.Disconnected);
        public static bool IsLocalCarrier() =>
            active && carrier != null && PlayerControl.LocalPlayer != null
            && carrier.PlayerId == PlayerControl.LocalPlayer.PlayerId;

        // The grey applies to the carrier's view while the game runs (dead or alive: a colourblind
        // ghost stays colourblind), never in the lobby or the end screen.
        private static bool GreyWanted() {
            try {
                if (!IsLocalCarrier() || cured || ShipStatus.Instance == null) return false;
                if (AmongUsClient.Instance == null
                    || AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Started) return false;
                if ((TasksInColour?.getBool() ?? true) && Minigame.Instance != null) {
                    // The cure scan itself stays grey: colour has to come back on "RESTORED", not the
                    // moment the scanner opens.
                    bool cureScan = Minigame.Instance.TryCast<MedScanMinigame>() != null && CureFor(PlayerControl.LocalPlayer);
                    if (!cureScan) return false;
                }
                return true;
            } catch { return false; }
        }

        // ---- RPC ----
        private static MessageWriter BeginRpc(byte subtype) {
            var w = UCRpc.Begin(RpcId);
            w.Write(subtype);
            return w;
        }

        public static void SendSet(byte id) {
            try {
                var w = BeginRpc(SubSet);
                w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySet(id);
                // Host tooling (Role Control) assigns through this path too, before the intro or
                // mid-game. Tasks exist only once the game runs; before that the intro-end postfix
                // takes care of the scan task.
                if (active && AmHost() && ShipStatus.Instance != null && !IntroRunning()) HostCureTaskStep();
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Colorblind] SendSet failed: {e}"); }
        }

        private static bool IntroRunning() {
            try { return DestroyableSingleton<IntroCutscene>.InstanceExists; } catch { return false; }
        }

        // Host: give the carrier the cure task if they qualify (idempotent, logs why not).
        private static void HostCureTaskStep() {
            if (!active || carrier == null) return;
            if (!CureEnabled()) return;
            if (CureFor(carrier)) HostEnsureScanTask();
            else UnknownsCollectionPlugin.Logger?.LogInfo("[Colorblind] non-crew carrier and the non-crew cure is off - no scan task.");
        }

        private static void SendCured() {
            try {
                var w = BeginRpc(SubCured);
                w.Write(carrierId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyCured(carrierId);
            } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogError($"[Colorblind] SendCured failed: {e}"); }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                switch (subtype) {
                    case SubSet: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireHost("Colorblind.Set")) ApplySet(id);
                        break;
                    }
                    case SubCured: {
                        byte id = reader.ReadByte();
                        if (UCRpc.RequireOwnerOrHost(carrier, "Colorblind.Cured")) ApplyCured(id);
                        break;
                    }
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Colorblind] HandleRpc failed: {e}");
            }
        }

        private static void ApplySet(byte id) {
            carrier = Helpers.playerById(id);
            active = carrier != null;
            carrierId = active ? id : byte.MaxValue;
            cured = false;
            if (active) {
                if (IsLocalCarrier()) UCRevealFx.PlayReveal();
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Colorblind] Colorblind: {carrier.Data?.PlayerName}.");
            } else {
                DetachFx();
            }
        }

        private static void ApplyCured(byte id) {
            if (!active || id != carrierId || cured) return;
            cured = true;
            UnknownsCollectionPlugin.Logger?.LogInfo($"[Colorblind] {carrier?.Data?.PlayerName} was cured by the MedBay scan.");
        }

        // ---- Pick (host; the modifier has no draft entry) ----
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPickPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    // Forced by host tooling before the intro ended: no pick, but the cure task still
                    // has to be handed out here (the tasks did not exist when SendSet ran).
                    if (active) { HostCureTaskStep(); return; }
                    if (SpawnRate == null || SpawnRate.getSelection() <= 0) return;
                    if (!TeslaVersionHandshake.EveryoneHasMod()) return;
                    if (LobbyPlayerCount() < (SpawnMinPlayers?.getFloat() ?? 5f)) return;

                    int chance = SpawnRate.getSelection() * 10;
                    if (rnd.Next(1, 101) > chance) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray().Where(IsModifierCandidate).ToList();
                    if (candidates.Count == 0) return;
                    SendSet(candidates[rnd.Next(candidates.Count)].PlayerId);
                    // SendSet skips the task while it still sees the intro singleton (OnDestroy timing
                    // is not guaranteed either way); the step is idempotent, so run it here as well.
                    HostCureTaskStep();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Colorblind] IntroEnd pick failed: {e}");
                }
            }
        }

        // ---- The cure task (host): append the map's "Submit Scan" if the carrier does not have it ----
        private static byte ScanTaskIndex() {
            var ship = ShipStatus.Instance;
            if (ship == null) return byte.MaxValue;
            foreach (var arr in new[] { ship.CommonTasks, ship.ShortTasks, ship.LongTasks }) {
                if (arr == null) continue;
                for (int i = 0; i < arr.Count; i++) {
                    var t = arr[i];
                    if (t != null && t.TaskType == TaskTypes.SubmitScan) return (byte)t.Index;
                }
            }
            return byte.MaxValue;
        }

        private static void HostEnsureScanTask() {
            try {
                if (carrier?.Data?.Tasks == null) return;
                byte scan = ScanTaskIndex();
                if (scan == byte.MaxValue) {
                    UnknownsCollectionPlugin.Logger?.LogInfo("[Colorblind] this map has no MedBay scanner - no cure this game.");
                    return;
                }
                var ids = new List<byte>();
                for (int i = 0; i < carrier.Data.Tasks.Count; i++) {
                    var t = carrier.Data.Tasks[i];
                    if (t == null) continue;
                    if (t.TypeId == scan) {
                        UnknownsCollectionPlugin.Logger?.LogInfo("[Colorblind] the carrier already has the MedBay scan.");
                        return;
                    }
                    ids.Add(t.TypeId);
                }
                ids.Add(scan);
                if (PlayerTuning.SendSetTasks(carrierId, ids.ToArray()))
                    UnknownsCollectionPlugin.Logger?.LogInfo($"[Colorblind] MedBay scan appended to {carrier.Data.PlayerName}'s tasks ({ids.Count} now).");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Colorblind] scan task failed: {e}");
            }
        }

        // ---- The scanner's diagnosis (carrier's client, while uncured) ----
        // The scan lasts exactly as long as the typewriter needs for completeString, so the "long"
        // scans for neutrals and impostors are simply more lines: progress readouts between the
        // diagnosis and the restored verdict.
        [HarmonyPatch(typeof(MedScanMinigame), nameof(MedScanMinigame.Begin))]
        static class ScanTextPatch {
            public static void Postfix(MedScanMinigame __instance) {
                try {
                    var me = PlayerControl.LocalPlayer;
                    if (__instance == null || !IsLocalCarrier() || cured || !CureFor(me)) return;
                    string name = me?.Data?.PlayerName ?? "?";
                    var lines = new List<string>();
                    for (int i = 1; i <= 4; i++) lines.Add(string.Format(UCLocalization.Tr("uc.ui.colorblind.scan" + i), name));
                    // The scanner panel holds about eight lines: five of ours plus at most three
                    // progress lines, which are long on purpose (the typewriter pays per character).
                    // Same count for everyone, see CureScanLength.
                    int extra = ExtraScanLines();
                    string progress = UCLocalization.Tr("uc.ui.colorblind.scan_progress");
                    for (int k = 1; k <= extra; k++)
                        lines.Add(string.Format(progress, Mathf.RoundToInt(100f * k / (extra + 1))));
                    lines.Add(string.Format(UCLocalization.Tr("uc.ui.colorblind.scan5"), name));
                    // charStats is the TextMeshPro the typewriter writes into; completeString is
                    // the full text it types out.
                    __instance.completeString = string.Join("\n", lines);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Colorblind] scan text failed: {e.Message}");
                }
            }
        }

        // The scan's last step completes the task: that is the cure.
        [HarmonyPatch(typeof(NormalPlayerTask), nameof(NormalPlayerTask.NextStep))]
        static class ScanDonePatch {
            public static void Postfix(NormalPlayerTask __instance) {
                try {
                    if (__instance == null || __instance.TaskType != TaskTypes.SubmitScan) return;
                    if (!IsLocalCarrier() || cured || !CureFor(PlayerControl.LocalPlayer)) return;
                    SendCured();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Colorblind] scan-done failed: {e.Message}");
                }
            }
        }

        // The scan beam everyone sees lives on the scanning player (PlayerAnimations.scannersImages,
        // switched by PlayerControl.SetScanner). A cure scan is tinted violet on every client, for
        // EVERY Colorblind carrier alike - crew, neutral or impostor - so with visual tasks on the
        // beam says "that is the cure, not a clear" and never which faction is being cured.
        private static readonly Color CureBeam = new Color(0.85f, 0.35f, 1f);

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetScanner))]
        static class ScannerBeamPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] bool on) {
                try {
                    if (__instance == null || __instance.MyPhysics == null || __instance.MyPhysics.Animations == null) return;
                    var images = __instance.MyPhysics.Animations.scannersImages;
                    if (images == null) return;
                    bool cureScan = on && active && !cured && carrier != null
                                    && carrier.PlayerId == __instance.PlayerId && CureFor(carrier);
                    Color c = cureScan ? CureBeam : Color.white;
                    for (int i = 0; i < images.Count; i++)
                        if (images[i] != null) images[i].color = c;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Colorblind] scanner beam tint failed: {e.Message}");
                }
            }
        }

        // Impostors are refused by the scanner console outright (AllowImpostor is false on the MedBay
        // scanner: vanilla's impostor-proof visual task). An uncured impostor Colorblind with the
        // non-crew cure on gets exactly that console reopened, and only for his own uncompleted
        // SubmitScan task (the Auditor's Console.CanUse pattern).
        [HarmonyPatch(typeof(Console), nameof(Console.CanUse))]
        static class ScannerCanUsePatch {
            public static void Postfix(ref float __result, Console __instance,
                                       [HarmonyArgument(0)] NetworkedPlayerInfo pc,
                                       [HarmonyArgument(1)] ref bool canUse,
                                       [HarmonyArgument(2)] ref bool couldUse) {
                try {
                    if (canUse) return;
                    if (!IsLocalCarrier() || cured) return;
                    var me = PlayerControl.LocalPlayer;
                    if (pc == null || pc.Object == null || pc.Object != me) return;
                    if (me.Data?.Role == null || !me.Data.Role.IsImpostor) return;
                    if (!CureFor(me)) return;

                    bool mine = false;
                    if (me.myTasks != null)
                        for (int i = 0; i < me.myTasks.Count; i++) {
                            var t = me.myTasks[i]?.TryCast<NormalPlayerTask>();
                            if (t == null || t.IsComplete || t.TaskType != TaskTypes.SubmitScan) continue;
                            if (t.ValidConsole(__instance)) { mine = true; break; }
                        }
                    if (!mine) return;
                    if (me.Data.IsDead || !me.CanMove) return;

                    float dist = Vector2.Distance(me.GetTruePosition(), (Vector2)__instance.transform.position);
                    __result = dist;
                    couldUse = true;
                    canUse = dist <= __instance.UsableDistance;
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Colorblind] scanner CanUse failed: {e.Message}");
                }
            }
        }

        private static bool IsModifierCandidate(PlayerControl p) {
            try {
                if (!UCPromotion.IsAlive(p) || p.Data.Role == null) return false;
                if (UCPromotion.HasAnyModifier(p)) return false;
                if ((WhoCanBe?.getSelection() ?? 1) == 1) return true;
                if (p.Data.Role.IsImpostor) return false;
                var ri = RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault();
                return ri == null || !ri.isNeutral;
            } catch { return false; }
        }

        // ---- The post effect ----
        // No CommandBuffer: the runtime interop cannot build a RenderTargetIdentifier from
        // BuiltinRenderTextureType (the NuGet reference interop could, the game's generated one
        // throws "Method not found" - playtest 2026-09-12). So the main camera renders into a
        // screen-sized RenderTexture and a second, higher-depth camera (its own layer, nothing else
        // on it) draws one quad that shows that texture through the desaturation material. Screen
        // space UGUI overlays render after every camera and stay untouched.
        private static Material desatMaterial;
        private static bool shaderMissing;
        private static bool attachFailed;           // one failure per game, no retry spam
        private static Camera mainCam;               // Camera.main at attach time (change detection)
        private static readonly List<Camera> redirected = new();   // every screen camera we pointed at the RT
        private static RenderTexture frameRt;
        private static GameObject fxRoot;            // holds the quad camera and the quad
        private static Camera quadCam;
        private static MeshRenderer quadRenderer;
        private static float nextPoll;
        private const int FxLayer = 31;              // Nightfall isolates on 30; 31 is free
        private static readonly Vector3 FarAway = new Vector3(5000f, 5000f, 0f);

        // Unity destroys a script-made Material on a scene change (the InvertVision lesson in UTS):
        // HideAndDontSave + DontDestroyOnLoad keep it, and a fake-null one is simply rebuilt.
        private static void EnsureMaterial() {
            if (desatMaterial != null || shaderMissing) return;
            try {
                var shader = Shader.Find("Unlit/DesatShader");
                if (shader == null) {
                    // Fallback: the ability buttons carry that very shader on their material.
                    try {
                        var hud = HudManager.Instance;
                        var m = hud?.UseButton?.graphic?.material;
                        if (m != null && m.shader != null && m.shader.name.Contains("Desat")) shader = m.shader;
                    } catch { }
                }
                if (shader == null) {
                    shaderMissing = true;
                    UnknownsCollectionPlugin.Logger?.LogWarning("[Colorblind] Unlit/DesatShader not found - the black-and-white view is unavailable.");
                    return;
                }
                desatMaterial = new Material(shader);
                desatMaterial.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(desatMaterial);
                desatMaterial.SetFloat("_Desat", 1f);
                try { desatMaterial.SetColor("_Color", Color.white); } catch { }
                UnknownsCollectionPlugin.Logger?.LogInfo("[Colorblind] desaturation material built.");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Colorblind] material build failed: {e.Message}");
            }
        }

        private static Mesh BuildQuad(float halfW, float halfH) {
            var mesh = new Mesh();
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.vertices = new Vector3[] {
                new Vector3(-halfW, -halfH, 0f), new Vector3(halfW, -halfH, 0f),
                new Vector3(-halfW,  halfH, 0f), new Vector3(halfW,  halfH, 0f)
            };
            mesh.uv = new Vector2[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.colors = new Color[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AttachFx(Camera cam) {
            EnsureMaterial();
            if (desatMaterial == null || cam == null) return;
            try {
                int w = Mathf.Max(8, Screen.width), h = Mathf.Max(8, Screen.height);
                frameRt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                frameRt.name = "UCColorblindFrame";
                frameRt.Create();
                desatMaterial.mainTexture = frameRt;

                // The main camera must not see the quad: a layer outside its culling mask.
                int layer = FxLayer;
                for (int l = 31; l >= 24; l--)
                    if ((cam.cullingMask & (1 << l)) == 0) { layer = l; break; }

                fxRoot = new GameObject("UCColorblindFx");
                fxRoot.transform.position = FarAway;
                var camGo = new GameObject("QuadCam") { layer = layer };
                camGo.transform.SetParent(fxRoot.transform, false);
                camGo.transform.localPosition = new Vector3(0f, 0f, -5f);
                quadCam = camGo.AddComponent<Camera>();
                quadCam.orthographic = true;
                quadCam.orthographicSize = 1f;
                quadCam.nearClipPlane = 0.1f;
                quadCam.farClipPlane = 20f;
                quadCam.cullingMask = 1 << layer;
                quadCam.clearFlags = CameraClearFlags.SolidColor;
                quadCam.backgroundColor = Color.black;
                quadCam.depth = cam.depth + 1f;
                quadCam.allowHDR = false;
                quadCam.allowMSAA = false;

                var quadGo = new GameObject("Quad") { layer = layer };
                quadGo.transform.SetParent(fxRoot.transform, false);
                quadGo.transform.localPosition = Vector3.zero;
                float aspect = (float)w / h;
                quadGo.AddComponent<MeshFilter>().mesh = BuildQuad(aspect, 1f);
                quadRenderer = quadGo.AddComponent<MeshRenderer>();
                quadRenderer.sharedMaterial = desatMaterial;
                quadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                quadRenderer.receiveShadows = false;

                // Every enabled camera that draws to the screen (the world camera AND the HUD camera,
                // which is a separate one in Among Us) renders into the same RT in its own depth
                // order; cameras with their own target (lighting, Nightfall captures) are left alone.
                float topDepth = cam.depth;
                foreach (var c in ScreenCameras()) {
                    c.targetTexture = frameRt;
                    redirected.Add(c);
                    if (c.depth > topDepth) topDepth = c.depth;
                }
                quadCam.depth = topDepth + 1f;
                mainCam = cam;
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Colorblind] black-and-white view attached ({w}x{h}, layer {layer}): " +
                    string.Join(", ", redirected.Select(c => $"{c.name}@{c.depth}")) + ".");
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Colorblind] attach failed: {e.Message}");
                attachFailed = true;
                DetachFx();
            }
        }

        // Enabled cameras without a render target of their own, i.e. the ones that compose the screen.
        private static List<Camera> ScreenCameras() {
            var list = new List<Camera>();
            try {
                var all = Camera.allCameras;
                if (all == null) return list;
                for (int i = 0; i < all.Count; i++) {
                    var c = all[i];
                    if (c == null || !c.enabled || c == quadCam) continue;
                    if (c.targetTexture != null && c.targetTexture != frameRt) continue;
                    list.Add(c);
                }
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Colorblind] camera scan failed: {e.Message}");
            }
            return list;
        }

        private static void DetachFx() {
            foreach (var c in redirected) {
                try { if (c != null && c.targetTexture == frameRt) c.targetTexture = null; } catch { }
            }
            redirected.Clear();
            try { if (fxRoot != null) UnityEngine.Object.Destroy(fxRoot); } catch { }
            try { if (desatMaterial != null) desatMaterial.mainTexture = null; } catch { }
            try { if (frameRt != null) { frameRt.Release(); UnityEngine.Object.Destroy(frameRt); } } catch { }
            fxRoot = null;
            quadCam = null;
            quadRenderer = null;
            frameRt = null;
            mainCam = null;
        }

        private static bool FxAttached => fxRoot != null && frameRt != null && mainCam != null;

        // A screen camera appeared (the HUD camera is created after the world camera) or one of ours
        // lost the redirect (something reset its target): rebuild the whole redirect.
        private static bool CameraSetChanged() {
            try {
                var now = ScreenCameras();
                if (now.Count != redirected.Count) return true;
                foreach (var c in now)
                    if (c.targetTexture != frameRt) return true;
                return false;
            } catch { return false; }
        }

        private static void Tick() {
            try {
                if (Time.time < nextPoll) return;
                nextPoll = Time.time + 0.25f;
                bool want = GreyWanted();
                if (!want || attachFailed) {
                    if (FxAttached) DetachFx();
                    return;
                }
                var cam = Camera.main;
                if (cam == null) { if (FxAttached) DetachFx(); return; }
                // Same camera, same screen size, redirect still in place: nothing to do. A destroyed
                // camera compares equal to null through the Unity operator; a new camera object after
                // a scene change is simply a different reference. A resolution change needs a new RT.
                if (FxAttached && mainCam == cam && cam.targetTexture == frameRt
                    && frameRt.width == Screen.width && frameRt.height == Screen.height
                    && !CameraSetChanged()) return;
                DetachFx();
                AttachFx(cam);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Colorblind] tick failed: {e.Message}");
            }
        }

        // ---- Role identity: APPEND, never replace - a modifier rides on top of the real role ----
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, [HarmonyArgument(1)] bool showModifier,
                                        ref List<RoleInfo> __result) {
                try {
                    if (!active || carrier == null || p == null || p != carrier || __result == null) return;
                    if (!showModifier) return;
                    if (!__result.Contains(Info())) __result.Add(Info());
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[Colorblind] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ---- Resets ----
        private static void FullReset() {
            DetachFx();
            attachFailed = false;
            carrier = null;
            active = false;
            cured = false;
            carrierId = byte.MaxValue;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => UCResetGuard.Run("Colorblind", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => UCResetGuard.Run("Colorblind", FullReset);
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Postfix() => UCResetGuard.Run("Colorblind", DetachFx);
        }
    }
}
