// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherHats), GPL-3.0.

/*
 * UCHats - three OWN custom hats ("Virus", "Werbetafel", "Werewolf") added to TOR's hat shop
 * from the outside, WITHOUT a single change to The Other Roles.
 *
 * ---------------------------------------------------------------------------------------------
 * WHY THE DETOUR (disk extraction + reflection) IS NECESSARY
 * ---------------------------------------------------------------------------------------------
 * TOR builds every custom hat in CustomHatManager.CreateHatBehaviour(CustomHat), and the sprites
 * come from the PRIVATE CustomHatManager.CreateHatSprite(string):
 *
 *     var texture = Helpers.loadTextureFromDisk(Path.Combine(HatsDirectory, path));
 *     if (texture == null) texture = Helpers.loadTextureFromResources(path);
 *
 * There are exactly two sources, and only one of them is reachable for us:
 *
 *   a) loadTextureFromResources(path) does Assembly.GetExecutingAssembly().GetManifestResourceStream(path)
 *      - "executing assembly" is TheOtherRoles.dll. Our PNGs live in UnknownsCollection.dll, so this
 *      path can NEVER see them. (It also reverses the file bytes whenever the path contains
 *      "HorseHats", see Helpers.cs - another reason to stay away from it. Our folder is called
 *      "hats" precisely so nothing collides with that special case.)
 *
 *   b) loadTextureFromDisk(Path.Combine(HatsDirectory, path)) reads a plain file from
 *      <Among Us>/TheOtherHats. That directory is TOR's public contract with the hat repository -
 *      no assembly identity involved. So if OUR png simply lies there, TOR loads it happily
 *      without ever knowing that this mod exists.
 *
 * Hence: we embed the PNGs in our own DLL and extract them into TheOtherHats on startup (step 1),
 * then hand TOR CustomHat records that point at the extracted file names (step 2). Everything TOR
 * itself has to read (Resource, BackResource, ClimbResource) must be ON DISK; the extra blink
 * frames of the animated hats stay embedded-only and go through our own loader.
 *
 * The registration list, CustomHatManager.UnregisteredHats, is `internal static` - visible inside
 * TheOtherRoles.dll only - so it can only be reached by reflection. The CustomHat class itself is
 * public, so the records we put in are ordinary, fully typed objects; only the field access needs
 * reflection. HatManagerPatches.GetHatByIdPrefix later drains that list and turns every entry into
 * a real HatData, so our hats travel through TOR's completely untouched code path.
 *
 * Step 3 - the download guard: HatsLoader.CoFetchHats calls GenerateDownloadList(UnregisteredHats)
 * right after adding the repository hats. Our entries have no ResHash*, and
 * ResourceRequireDownload() treats "no hash" as "must download", so TOR would try to fetch
 * .../TheOtherHats/master/hats/UC_*.png and log a 404 per file. We therefore hang a Harmony
 * POSTFIX on GenerateDownloadList and remove our own file names from the returned list. A postfix
 * (instead of a prefix that hides the hats, or a transpiler) was chosen because it is the smallest
 * possible intervention: the original method runs completely unchanged, TOR's own hats keep their
 * normal hash check, and we only edit the strings that belong to us. It is also self-healing - if
 * the files are ever missing from disk, we still do not ask TOR to download them from a repository
 * that does not host them.
 *
 * Step 4 - the animations: HatData/HatViewData have no notion of frames or time (see CustomHat.cs
 * and HatExtension.cs - purely static sprite slots), so an animated hat cannot be expressed in
 * TOR's data model at all. Instead we own the last word per frame: HatParentPatches.LateUpdatePrefix
 * returns false for cached custom hats (it skips the original LateUpdate), but HarmonyX still runs
 * every POSTFIX afterwards - so our postfix on HatParent.LateUpdate is the final writer of the
 * animated layer's sprite each frame. Driven by Time.time, purely local and purely cosmetic: no
 * RPC, no host authority, and deliberately NO dependency on TeslaVersionHandshake - the hats must
 * work even when nobody else has the mod (everyone else simply sees the default hat, exactly like
 * with any other custom hat that a player has not downloaded).
 *
 * Layer mechanics that the HatDef table below leans on (all verified in TOR 4.8.0 sources):
 *   - PopulateFromViewData: InFront=true -> FrontLayer only. Behind WITH BackImage -> BOTH layers
 *     (Back = BackImage, Front = MainImage). Behind WITHOUT BackImage -> BackLayer only.
 *     CreateHatBehaviour forces Behind = true whenever BackResource is set.
 *   - SetClimbAnim/SetFloorAnim DISABLE the BackLayer and put ClimbImage/FloorImage on the
 *     FrontLayer. A climb sprite must therefore contain the WHOLE design in one image, and our
 *     animation postfix uses "BackLayer off although this hat has a BackImage" as the cheap,
 *     reflection-free signal that a climb/floor pose is active (pose guard 1). Pose guard 2
 *     compares against HatViewData.ClimbImage via a one-time reflection read of TOR's internal
 *     ViewDataCache - overwriting the climb sprite once would lose the pose until the next
 *     SetIdleAnim, because SetClimbAnim only fires on pose CHANGES, not per frame.
 *   - FloorImage is forced to MainImage by CreateHatBehaviour, so corpses show the static main
 *     sprite (without the back layer). Not fixable from outside; looks acceptable.
 *
 * TOR-internal reflection touch points (all soft-fail with a logged warning):
 *   CustomHatManager.HatsDirectory (property), CustomHatManager.UnregisteredHats (field),
 *   CustomHatManager.ViewDataCache (field, only for pose guard 2).
 *
 * Nothing in here writes to TheOtherRoles-main: no TOR file is added, changed or patched at build
 * time; every hook is a runtime Harmony patch plus reflection reads.
 */

using AmongUs.Data;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TheOtherRoles.Modules.CustomHats;
using UnityEngine;

namespace UnknownsCollection {
    public static class UCHats {
        // The Werewolf hat's ProductId, exactly as CreateHatBehaviour derives it ("hat_" + Name,
        // CustomHatManager.cs:97). Shared with WerewolfFx, which puts this hat on the transformed
        // beast via setLook - which is also WHY the hat lock below exists.
        public const string WerewolfHatId = "hat_Werewolf";

        // Embedded PNGs: Resources\hats\*.png, pinned to this logical name in the csproj.
        private const string ResourcePrefix = "UnknownsCollection.Resources.hats.";

        private const string Author = "DaUnknown-0";
        // Own package so the hats get their own headline in the hat shop instead of being scattered
        // into TOR's "Misc." bucket (HatsTabPatches groups by HatExtension.Package).
        private const string Package = "Unknown's Collection";

        // Which renderer an animation writes to. This cannot be derived from HatData.InFront:
        // a hat with a BackImage has InFront == false but keeps its MainImage on the FRONT layer
        // (both layers active), so the layer carrying the animated part is a per-hat decision.
        private enum AnimTarget { Front, Back }

        // One declaration per hat; everything else (disk files, download strip list, animation
        // lookup) is derived from this table. File names use the "UC_" prefix so they can never
        // collide with a file of the official TheOtherHats repository (which would make our
        // download-strip below drop somebody else's hat). Hat NAMES were checked against the
        // official TheOtherHats manifest and are free.
        private sealed class HatDef {
            // --- declaration ---
            public string Name;
            public bool Behind;
            public string MainFile;                 // in <Among Us>\TheOtherHats (read by TOR)
            public string BackFile;                 // null = no back layer
            public string ClimbFile;                // null = vanishes on ladders (pre-fix behavior)
            public string MainRes;                  // embedded source (without ResourcePrefix)
            public string BackRes;
            public string ClimbRes;                 // null while ClimbFile is set = reuse an already
                                                    //   extracted file (no second copy on disk)
            public int AnimFrames;                  // 0 = static hat
            public float AnimFps;
            public string AnimPattern;              // "{0}" = 1-based frame index
            public AnimTarget Target;
            public bool LockFlip;                   // true = never mirror (pure-text hats)
            // --- runtime state ---
            public bool DiskOk, BackOk, ClimbOk;
            public Sprite[] Frames;
            public bool FramesTried;
        }

        private static readonly HatDef[] Defs = {
            new HatDef {
                Name = "Virus",
                MainFile = "UC_Virus.png", MainRes = "virus.png",
                // Ladder pose: deliberately the SAME png as idle. The spike wreath sits AROUND the
                // body and fits the climb silhouette too; before this, the hat vanished on ladders
                // (ClimbImage == null). No ClimbRes -> no second copy on disk.
                ClimbFile = "UC_Virus.png", ClimbRes = null,
            },
            new HatDef {
                Name = "Werbetafel", Behind = true,
                MainFile = "UC_Werbetafel.png", MainRes = "werbetafel_1.png",
                // Ladder pose shows the player from behind, so this is the BACK of the billboard
                // (plain metal, no text - also sidesteps every mirrored-text problem).
                ClimbFile = "UC_Werbetafel_climb.png", ClimbRes = "werbetafel_climb.png",
                AnimFrames = 6, AnimFps = 6f, AnimPattern = "werbetafel_{0}.png",
                // Behind without BackImage -> TOR renders the whole hat through the BackLayer.
                Target = AnimTarget.Back,
                // Never mirror the billboard: a hat that is pure text has no "left version" - it
                // must always read the same way. (TOR's own answer would be a flipresource PNG,
                // but a second pre-mirrored copy of six blink frames for one flag is not worth it.)
                LockFlip = true,
            },
            new HatDef {
                Name = "Werewolf", Behind = true,   // forced by BackResource anyway
                MainFile = "UC_Werewolf.png", MainRes = "werewolf_1.png",
                BackFile = "UC_Werewolf_back.png", BackRes = "werewolf_back.png",
                ClimbFile = "UC_Werewolf_climb.png", ClimbRes = "werewolf_climb.png",
                // Full-body beast in side profile (crewmates stand sideways; the snout points the
                // same way as the visor). Frames 2..6 only vary the glowing eye.
                AnimFrames = 6, AnimFps = 6f, AnimPattern = "werewolf_{0}.png",
                // BackImage present -> BOTH layers active; the eye lives on the FrontLayer.
                Target = AnimTarget.Front,
                LockFlip = false,                   // the profile mirrors with the walk direction
            },
        };

        // Every file we ever put into TheOtherHats - the download guard strips exactly these.
        private static readonly string[] OwnDiskFiles = Defs
            .SelectMany(d => new[] { d.MainFile, d.BackFile, d.ClimbFile })
            .Where(f => f != null).Distinct().ToArray();

        // Animated hats by HatData.name, filled in TryPatch (only hats that actually registered).
        private static readonly Dictionary<string, HatDef> AnimByName = new();

        private static bool loggedAnimError;

        public static void TryPatch(Harmony harmony) {
            try {
                string dir = ResolveHatsDirectory();
                if (dir == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[Hats] could not resolve TOR's hats directory - custom hats skipped.");
                    return;
                }

                // Step 1: put every PNG TOR itself reads next to the repository hats.
                foreach (var def in Defs) {
                    def.DiskOk = ExtractIfNeeded(dir, ResourcePrefix + def.MainRes, def.MainFile);
                    def.BackOk = def.BackRes != null
                        && ExtractIfNeeded(dir, ResourcePrefix + def.BackRes, def.BackFile);
                    def.ClimbOk = def.ClimbRes != null
                        ? ExtractIfNeeded(dir, ResourcePrefix + def.ClimbRes, def.ClimbFile)
                        : def.ClimbFile != null && def.DiskOk;   // climb reuses the main file
                }

                // Step 2: register - but only hats whose main file actually made it to disk.
                // Registering a hat without its file would make TOR's CreateHatBehaviour throw on
                // every GetHatById (it treats that as "not downloaded yet") and keep its loader
                // loop alive forever.
                var pending = new List<CustomHat>();
                foreach (var def in Defs) {
                    if (!def.DiskOk) continue;

                    // A BackResource whose file is missing would be WORSE than none at all:
                    // CreateHatBehaviour forces Behind = true without checking the sprite, and
                    // PopulateFromViewData then renders the MainImage on the BackLayer - the whole
                    // hat would hide behind the player. Degrade to front-only instead.
                    bool back = def.BackRes != null && def.BackOk;
                    if (def.BackRes != null && !back) {
                        UnknownsCollectionPlugin.Logger?.LogWarning(
                            $"[Hats] {def.Name}: back sprite missing - falling back to front-only.");
                    }

                    var ch = new CustomHat {
                        Name = def.Name, Author = Author, Package = Package,
                        Resource = def.MainFile, Adaptive = false, Bounce = false,
                        Behind = back || (def.BackRes == null && def.Behind),
                    };
                    if (back) ch.BackResource = def.BackFile;
                    if (def.ClimbOk) ch.ClimbResource = def.ClimbFile;
                    pending.Add(ch);

                    if (def.AnimFrames > 0) AnimByName[def.Name] = def;
                }

                if (pending.Count > 0 && !Register(pending)) return;

                // Step 3 + 4: the two Harmony hooks. Patched manually (not via [HarmonyPatch]
                // attributes) so a missing target logs a clear line instead of blowing up PatchAll.
                PatchDownloadGuard(harmony);
                if (AnimByName.Count > 0) PatchAnimation(harmony);

                // Step 5: the Werewolf hat lock (see the section at the bottom of this file). Only
                // armed when the Werewolf hat actually made it into the shop.
                if (pending.Any(h => h?.Name == "Werewolf")) UCFx.RegisterTick(TickHatLock);

                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Hats] registered {pending.Count} custom hat(s) in {dir}.");
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hats] setup failed: {ex}");
            }
        }

        // ---- Step 1: extraction -------------------------------------------------------------

        // CustomHatManager.HatsDirectory is `internal static` -> reflection. The fallback rebuilds
        // the exact same expression from the PUBLIC const CustomHatManager.ResourcesDirectory
        // ("TheOtherHats"), so a renamed/removed property can never make us write somewhere else.
        private static string ResolveHatsDirectory() {
            try {
                var prop = typeof(CustomHatManager).GetProperty(
                    "HatsDirectory", BindingFlags.NonPublic | BindingFlags.Static);
                if (prop?.GetValue(null) is string fromTor && !string.IsNullOrEmpty(fromTor)) return fromTor;
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    $"[Hats] HatsDirectory reflection failed ({ex.Message}) - using the computed path.");
            }
            try {
                string root = Path.GetDirectoryName(Application.dataPath);
                if (string.IsNullOrEmpty(root)) return null;
                return Path.Combine(root, CustomHatManager.ResourcesDirectory);
            } catch {
                return null;
            }
        }

        // Writes the embedded PNG into TheOtherHats. Only touches the disk when the file is missing
        // or its content differs, so a normal start does no I/O beyond one read, and a mod update
        // with new artwork still refreshes the file. Creates the directory if TOR has not yet
        // (HatsLoader does it only after the manifest download succeeds - which fails offline).
        private static bool ExtractIfNeeded(string dir, string resourceName, string fileName) {
            try {
                byte[] data = ReadResource(resourceName);
                if (data == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Hats] embedded resource missing: {resourceName}");
                    return false;
                }
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileName);
                if (File.Exists(path)) {
                    var existing = File.ReadAllBytes(path);
                    if (existing.Length == data.Length && existing.SequenceEqual(data)) return true;
                }
                File.WriteAllBytes(path, data);
                UnknownsCollectionPlugin.Logger?.LogInfo($"[Hats] wrote {fileName}.");
                return true;
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hats] could not extract {fileName}: {ex.Message}");
                return false;
            }
        }

        private static byte[] ReadResource(string logicalName) {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
            if (stream == null) return null;
            var data = new byte[stream.Length];
            int read = 0;
            while (read < data.Length) {
                int n = stream.Read(data, read, data.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == data.Length ? data : null;
        }

        // ---- Step 2: registration -----------------------------------------------------------

        // Appends our records to the internal CustomHatManager.UnregisteredHats. Runs during our
        // plugin Load(), i.e. right after TOR's Load() (hard dependency) and therefore long before
        // HatsLoader's coroutine has finished downloading the manifest - our hats are in the list
        // from the very first moment, whatever the network does afterwards.
        private static bool Register(List<CustomHat> hats) {
            try {
                var field = typeof(CustomHatManager).GetField(
                    "UnregisteredHats", BindingFlags.NonPublic | BindingFlags.Static);
                if (field?.GetValue(null) is not List<CustomHat> list) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[Hats] CustomHatManager.UnregisteredHats not found - custom hats skipped.");
                    return false;
                }
                foreach (var hat in hats) {
                    if (list.Any(h => h?.Name == hat.Name)) continue;
                    list.Add(hat);
                }
                return true;
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogError($"[Hats] registration failed: {ex}");
                return false;
            }
        }

        // ---- Step 3: keep our files out of TOR's download queue ------------------------------

        private static void PatchDownloadGuard(Harmony harmony) {
            var target = AccessTools.Method(typeof(CustomHatManager), "GenerateDownloadList");
            if (target == null) {
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    "[Hats] GenerateDownloadList not found - TOR may log 404s for our hat files.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(UCHats), nameof(StripOwnFiles))));
        }

        // The returned list is the very object HatsLoader iterates over, so removing in place is
        // enough. Our files are already on disk - there is nothing to fetch, and the repository
        // does not host them.
        private static void StripOwnFiles(List<string> __result) {
            try {
                __result?.RemoveAll(f => OwnDiskFiles.Contains(f));
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Hats] download strip failed: {ex.Message}");
            }
        }

        // ---- Step 4: the frame animations ----------------------------------------------------

        private static void PatchAnimation(Harmony harmony) {
            var target = AccessTools.Method(typeof(HatParent), nameof(HatParent.LateUpdate));
            if (target == null) {
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    "[Hats] HatParent.LateUpdate not found - animated hats stay on their first frame.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(UCHats), nameof(LateUpdatePostfix))));
        }

        // Runs AFTER TOR's LateUpdatePrefix, which returns false for cached custom hats. HarmonyX
        // still executes postfixes when a prefix skips the original, so this is the last writer of
        // the sprite in the frame and cannot be overwritten by TOR's flip handling.
        private static void LateUpdatePostfix(HatParent __instance) {
            try {
                if (__instance == null) return;
                var hat = __instance.Hat;
                if (hat == null || !AnimByName.TryGetValue(hat.name, out var def)) return;

                var renderer = def.Target == AnimTarget.Front ? __instance.FrontLayer : __instance.BackLayer;
                if (renderer == null || !renderer.enabled) return;

                // Ladder/climb pose of BackLayer-hats: SetClimbAnim disables the BackLayer, so a
                // disabled renderer already returned above. A null sprite covers hats without a
                // climb resource (nothing to animate over).
                if (renderer.sprite == null) return;

                // Pose guard 1 (reflection-free): a hat whose BackImage is registered has BOTH
                // layers enabled in the idle pose; SetClimbAnim/SetFloorAnim switch the BackLayer
                // off. Skipping here also keeps corpses on the static FloorImage (= MainImage).
                if (def.BackOk && (__instance.BackLayer == null || !__instance.BackLayer.enabled)) return;

                // Pose guard 2 (exact): never write over the climb sprite. SetClimbAnim fires only
                // on pose CHANGES - overwrite it once and the pose is gone until SetIdleAnim.
                if (TryGetViewData(hat.name, out var view) && view != null &&
                    (renderer.sprite == view.ClimbImage || renderer.sprite == view.LeftClimbImage)) return;

                // Never mirror pure-text hats. Cosmetics follow the player's facing, which turned
                // the billboard into unreadable mirror writing whenever the player walked left.
                if (def.LockFlip && renderer.flipX) renderer.flipX = false;

                var strip = GetFrames(def);
                if (strip == null) return;

                int index = Mathf.FloorToInt(Time.time * def.AnimFps) % strip.Length;
                var frame = strip[index];
                if (frame != null && renderer.sprite != frame) renderer.sprite = frame;
            } catch (Exception ex) {
                if (loggedAnimError) return;
                loggedAnimError = true;
                UnknownsCollectionPlugin.Logger?.LogError($"[Hats] hat animation failed (logged once): {ex}");
            }
        }

        // One-time reflection read of TOR's internal ViewDataCache (Dictionary<string, HatViewData>,
        // readonly - the reference never changes, so caching the dictionary object is safe).
        // Deliberately NOT CosmeticsCache.GetHat: TOR has a prefix on it that logs on every call,
        // which would flood the log once per frame. If the reflection breaks with a TOR update,
        // pose guard 1 still protects every hat that has a back layer.
        private static Dictionary<string, HatViewData> viewCache;
        private static bool viewCacheTried;

        private static bool TryGetViewData(string name, out HatViewData view) {
            view = null;
            if (!viewCacheTried) {
                viewCacheTried = true;
                try {
                    var field = typeof(CustomHatManager).GetField(
                        "ViewDataCache", BindingFlags.NonPublic | BindingFlags.Static);
                    viewCache = field?.GetValue(null) as Dictionary<string, HatViewData>;
                } catch (Exception ex) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        $"[Hats] ViewDataCache reflection failed ({ex.Message}).");
                }
                if (viewCache == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[Hats] ViewDataCache unreachable - climb-pose guard runs on layer states only.");
                }
            }
            return viewCache != null && viewCache.TryGetValue(name, out view);
        }

        // Lazily built on the first rendered frame (Unity is definitely up by then). All frames or
        // none: a half-loaded strip would blink with holes, so we fall back to the static frame 1
        // that TOR already loaded from disk.
        private static Sprite[] GetFrames(HatDef def) {
            if (def.FramesTried) return def.Frames;
            def.FramesTried = true;
            var built = new Sprite[def.AnimFrames];
            for (int i = 0; i < built.Length; i++) {
                built[i] = LoadHatSprite(ResourcePrefix + string.Format(def.AnimPattern, i + 1));
                if (built[i] != null) continue;
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    $"[Hats] {def.Name}: anim frame {i + 1} missing - hat stays static.");
                return null;
            }
            def.Frames = built;
            return built;
        }

        // Same calibration TOR's private CreateHatSprite uses (CustomHatManager.cs): pivot
        // (0.53, 0.575) and pixelsPerUnit = texture.width * 0.375f. Computed from the texture, not
        // hardcoded, so a re-exported asset at a different resolution still lines up with frame 1.
        private static Sprite LoadHatSprite(string logicalName) {
            try {
                byte[] data = ReadResource(logicalName);
                if (data == null) return null;
                var texture = new Texture2D(2, 2, TextureFormat.ARGB32, true);
                if (!ImageConversion.LoadImage(texture, data, false)) return null;
                var sprite = Sprite.Create(texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.53f, 0.575f),
                    texture.width * 0.375f);
                if (sprite == null) return null;
                texture.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
                sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
                return sprite;
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Hats] sprite load failed ({logicalName}): {ex.Message}");
                return null;
            }
        }

        // ---- Step 5: the Werewolf hat lock (user decision 2026-07-28) ----------------------
        //
        // While the Werewolf ROLE is enabled (spawn rate > 0), the full-body Werewolf hat is the
        // beast's transformation look (WerewolfFx puts it on via setLook) - so nobody may WEAR it
        // as an ordinary cosmetic: a crewmate walking around as the beast would fake (or mask) a
        // transformation. Three layers, from soft to hard:
        //
        //   1. The wardrobe chip is greyed out and its button disabled (HatsTab.OnEnable postfix -
        //      it must be a postfix because TOR's own OnEnablePrefix returns false and rebuilds the
        //      whole tab itself, HatsTabPatches.cs:21; the chips only exist after it ran).
        //   2. ClickEquip is blocked as a backstop (controller flow selects with A and equips with
        //      a separate button, so a disabled chip alone does not cover every input path).
        //   3. TickHatLock runs on the shared UCFx tick (HudManager.Update - main menu wardrobes
        //      have no HUD, but there LocalPlayer is null and only the saved value matters, which
        //      layer 1+2 already protect): if the SAVED hat is the Werewolf hat while the role is
        //      on, it is swapped back to the hat the player wore BEFORE - remembered in the plugin
        //      config every time we see a different hat, so it survives restarts - and announced
        //      via RpcSetHat so the lobby sees the swap immediately.
        //
        // The role option is read live: the host's own value is always current, a client's value is
        // whatever the lobby last synced - exactly the scope in which the hat matters.

        private const string NoHatId = "hat_NoHat";   // vanilla "empty" hat, verified in the 4.7.0 metadata
        private static float lastHatFixTime;          // throttle: never spam RpcSetHat if a swap cannot stick

        private static bool WerewolfHatLocked() {
            try { return Werewolf.SpawnRate != null && Werewolf.SpawnRate.getSelection() > 0; }
            catch { return false; }
        }

        private static string PreviousHat() {
            var entry = UnknownsCollectionPlugin.WerewolfPreviousHat;
            string prev = entry != null ? entry.Value : null;
            return string.IsNullOrEmpty(prev) || prev == WerewolfHatId ? NoHatId : prev;
        }

        private static void RememberHat(string hatId) {
            try {
                var entry = UnknownsCollectionPlugin.WerewolfPreviousHat;
                if (entry == null || string.IsNullOrEmpty(hatId) || hatId == WerewolfHatId) return;
                if (entry.Value != hatId) entry.Value = hatId;   // ConfigEntry setters persist to disk
            } catch { }
        }

        private static void TickHatLock() {
            try {
                var customization = DataManager.Player?.Customization;
                if (customization == null) return;
                string saved = customization.Hat;
                if (string.IsNullOrEmpty(saved)) return;
                if (saved != WerewolfHatId) { RememberHat(saved); return; }
                if (!WerewolfHatLocked()) return;
                if (Time.time - lastHatFixTime < 1f) return;
                lastHatFixTime = Time.time;

                string prev = PreviousHat();
                customization.Hat = prev;
                var lp = PlayerControl.LocalPlayer;
                if (lp != null) lp.RpcSetHat(prev);
                UnknownsCollectionPlugin.Logger?.LogInfo(
                    $"[Hats] Werewolf role is enabled - the Werewolf hat was swapped back to '{prev}'.");
            } catch (Exception ex) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Hats] hat lock tick failed: {ex.Message}");
            }
        }

        // Layer 1: grey the chip out. SetUnavailable is the vanilla "locked cosmetic" treatment;
        // disabling the PassiveButton kills the hover/click listeners TOR wired up in its own
        // OnEnable prefix (HatsTabPatches.cs:100-109).
        [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.OnEnable))]
        private static class HatsTabLockPatch {
            public static void Postfix(HatsTab __instance) {
                try {
                    if (!WerewolfHatLocked() || __instance == null || __instance.ColorChips == null) return;
                    foreach (ColorChip chip in __instance.ColorChips) {
                        var hat = chip != null && chip.Inner != null ? chip.Inner.Hat : null;
                        if (hat == null || hat.ProductId != WerewolfHatId) continue;
                        try { chip.SetUnavailable(); } catch { }
                        try { if (chip.Button != null) chip.Button.enabled = false; } catch { }
                    }
                } catch (Exception ex) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"[Hats] hat lock (tab) failed: {ex.Message}");
                }
            }
        }

        // Layer 2: the equip itself. currentHat is what SelectHat last previewed - exactly what
        // ClickEquip would write into the save and the RpcSetHat.
        [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.ClickEquip))]
        private static class HatsTabEquipPatch {
            public static bool Prefix(HatsTab __instance) {
                try {
                    if (!WerewolfHatLocked() || __instance == null) return true;
                    var hat = __instance.currentHat;
                    if (hat != null && hat.ProductId == WerewolfHatId) return false;
                } catch { }
                return true;
            }
        }
    }
}
