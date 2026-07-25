// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHats - two OWN custom hats ("Virus", "Werbetafel") added to TOR's hat shop from the outside,
 * WITHOUT a single change to The Other Roles.
 *
 * ---------------------------------------------------------------------------------------------
 * WHY THE DETOUR (disk extraction + reflection) IS NECESSARY
 * ---------------------------------------------------------------------------------------------
 * TOR builds every custom hat in CustomHatManager.CreateHatBehaviour(CustomHat), and the sprite
 * itself comes from the PRIVATE CustomHatManager.CreateHatSprite(string):
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
 * then hand TOR a CustomHat record that points at the extracted file name (step 2).
 *
 * The registration list, CustomHatManager.UnregisteredHats, is `internal static` - visible inside
 * TheOtherRoles.dll only - so it can only be reached by reflection. The CustomHat class itself is
 * public, so the records we put in are ordinary, fully typed objects; only the field access needs
 * reflection. HatManagerPatches.GetHatByIdPrefix later drains that list and turns every entry into
 * a real HatData, so our hats travel through TOR's completely untouched code path.
 *
 * Step 3 - the download guard: HatsLoader.CoFetchHats calls GenerateDownloadList(UnregisteredHats)
 * right after adding the repository hats. Our two entries have no ResHash*, and
 * ResourceRequireDownload() treats "no hash" as "must download", so TOR would try to fetch
 * .../TheOtherHats/master/hats/UC_Virus.png and log a 404. We therefore hang a Harmony POSTFIX on
 * GenerateDownloadList and remove our own file names from the returned list. A postfix (instead of
 * a prefix that hides the hats, or a transpiler) was chosen because it is the smallest possible
 * intervention: the original method runs completely unchanged, TOR's own hats keep their normal
 * hash check, and we only edit the two strings that belong to us. It is also self-healing - if the
 * files are ever missing from disk, we still do not ask TOR to download them from a repository
 * that does not host them.
 *
 * Step 4 - the animation: HatData/HatViewData have no notion of frames or time (see CustomHat.cs
 * and HatExtension.cs - purely static sprite slots), so a blinking hat cannot be expressed in
 * TOR's data model at all. Instead we own the last word per frame: HatParentPatches.LateUpdatePrefix
 * returns false for cached custom hats (it skips the original LateUpdate), but HarmonyX still runs
 * every POSTFIX afterwards - so our postfix on HatParent.LateUpdate is the final writer of
 * FrontLayer.sprite each frame. Six frames at 6 fps, driven by Time.time, purely local and purely
 * cosmetic: no RPC, no host authority, and deliberately NO dependency on TeslaVersionHandshake -
 * the hats must work even when nobody else has the mod (everyone else simply sees the default hat,
 * exactly like with any other custom hat that a player has not downloaded).
 *
 * Nothing in here writes to TheOtherRoles-main: no TOR file is added, changed or patched at build
 * time; every hook is a runtime Harmony patch plus one reflection read.
 */

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
        // Embedded PNGs: Resources\hats\*.png, pinned to this logical name in the csproj.
        private const string ResourcePrefix = "UnknownsCollection.Resources.hats.";

        // File names used INSIDE <Among Us>/TheOtherHats. Deliberately prefixed "UC_" so they can
        // never collide with a file of the official TheOtherHats repository (which would make our
        // download-strip below drop somebody else's hat).
        private const string VirusFile = "UC_Virus.png";
        private const string WerbetafelFile = "UC_Werbetafel.png";

        // Shop entries. The names were checked against the official TheOtherHats manifest and are free.
        private const string VirusName = "Virus";
        private const string WerbetafelName = "Werbetafel";
        private const string Author = "DaUnknown-0";
        // Own package so both hats get their own headline in the hat shop instead of being scattered
        // into TOR's "Misc." bucket (HatsTabPatches groups by HatExtension.Package).
        private const string Package = "Unknown's Collection";

        // Blink animation (Werbetafel only): 6 frames at 6 fps -> a full cycle every second.
        private const int WerbetafelFrames = 6;
        private const float WerbetafelFps = 6f;

        // Only the files TOR itself has to read need to exist on disk: the hat's `Resource`.
        // The five remaining blink frames are never seen by TOR - our own postfix loads them
        // straight out of this assembly - so we do not litter the player's game folder with them.
        private static readonly string[] OwnDiskFiles = { VirusFile, WerbetafelFile };

        private static Sprite[] frames;
        private static bool framesTried;
        private static bool loggedAnimError;

        public static void TryPatch(Harmony harmony) {
            try {
                string dir = ResolveHatsDirectory();
                if (dir == null) {
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        "[Hats] could not resolve TOR's hats directory - custom hats skipped.");
                    return;
                }

                // Step 1: put the two PNGs TOR needs next to the repository hats.
                bool virusOk = ExtractIfNeeded(dir, ResourcePrefix + "virus.png", VirusFile);
                bool tafelOk = ExtractIfNeeded(dir, ResourcePrefix + "werbetafel_1.png", WerbetafelFile);

                // Step 2: register - but only hats whose file actually made it to disk. Registering a
                // hat without its file would make TOR's CreateHatBehaviour throw on every GetHatById
                // (it treats that as "not downloaded yet") and keep its loader loop alive forever.
                var pending = new List<CustomHat>();
                if (virusOk) {
                    pending.Add(new CustomHat {
                        Name = VirusName, Author = Author, Package = Package,
                        Resource = VirusFile, Adaptive = false, Bounce = false, Behind = false
                    });
                }
                if (tafelOk) {
                    pending.Add(new CustomHat {
                        Name = WerbetafelName, Author = Author, Package = Package,
                        // Behind: the billboard is mounted BEHIND the player, so the crewmate stands in
                        // front of its own advertisement instead of being covered by it. TOR renders a
                        // "behind" hat through BackLayer (CreateHatBehaviour sets InFront = !Behind).
                        Resource = WerbetafelFile, Adaptive = false, Bounce = false, Behind = true
                    });
                }

                if (pending.Count > 0 && !Register(pending)) return;

                // Step 3 + 4: the two Harmony hooks. Patched manually (not via [HarmonyPatch]
                // attributes) so a missing target logs a clear line instead of blowing up PatchAll.
                PatchDownloadGuard(harmony);
                PatchAnimation(harmony);

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
                    "[Hats] GenerateDownloadList not found - TOR may log a 404 for our hat files.");
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

        // ---- Step 4: the blinking Werbetafel -------------------------------------------------

        private static void PatchAnimation(Harmony harmony) {
            var target = AccessTools.Method(typeof(HatParent), nameof(HatParent.LateUpdate));
            if (target == null) {
                UnknownsCollectionPlugin.Logger?.LogWarning(
                    "[Hats] HatParent.LateUpdate not found - Werbetafel stays on its first frame.");
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
                if (hat == null || hat.name != WerbetafelName) return;

                var strip = Frames;
                if (strip == null) return;

                // Our hat is InFront (no back resource), but stay generic: TOR renders a "behind"
                // hat through BackLayer, so follow the same rule PopulateFromViewData uses.
                var renderer = hat.InFront ? __instance.FrontLayer : __instance.BackLayer;
                if (renderer == null || !renderer.enabled) return;

                // Ladder/climb pose: SetClimbAnimPrefix writes hatViewData.ClimbImage, which is null
                // for a hat without a climb resource. Leaving a null sprite alone means we never
                // force the billboard back on while the player is climbing, and never fight
                // SetClimbAnim over the same renderer.
                if (renderer.sprite == null) return;

                // Never mirror the billboard. Cosmetics follow the player's facing, which turned the
                // advertisement into unreadable mirror writing whenever the player walked left. A hat
                // that is pure text has no "left version" - it must always read the same way. (TOR's
                // own answer to this is an extra flipresource PNG, but a second pre-mirrored copy of
                // six blink frames would double the asset count for something one flag fixes.)
                if (renderer.flipX) renderer.flipX = false;

                int index = Mathf.FloorToInt(Time.time * WerbetafelFps) % strip.Length;
                var frame = strip[index];
                if (frame != null && renderer.sprite != frame) renderer.sprite = frame;
            } catch (Exception ex) {
                if (loggedAnimError) return;
                loggedAnimError = true;
                UnknownsCollectionPlugin.Logger?.LogError($"[Hats] blink animation failed (logged once): {ex}");
            }
        }

        // Lazily built on the first rendered frame (Unity is definitely up by then). All six frames
        // or none: a half-loaded strip would blink with holes, so we fall back to the static frame 1
        // that TOR already loaded from disk.
        private static Sprite[] Frames {
            get {
                if (framesTried) return frames;
                framesTried = true;
                var built = new Sprite[WerbetafelFrames];
                for (int i = 0; i < built.Length; i++) {
                    built[i] = LoadHatSprite($"{ResourcePrefix}werbetafel_{i + 1}.png");
                    if (built[i] != null) continue;
                    UnknownsCollectionPlugin.Logger?.LogWarning(
                        $"[Hats] blink frame {i + 1} missing - Werbetafel stays static.");
                    return null;
                }
                frames = built;
                return frames;
            }
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
    }
}
