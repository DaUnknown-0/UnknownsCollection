// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Unknown's Collection - a separate BepInEx plugin that layers brand-new "roles" on top of
 * The Other Roles WITHOUT modifying TOR's source. Like the Revenger in "Useful TOR Stuff",
 * each role is built from Harmony patches: own RoleInfo (display tag), CustomButton/meeting UI,
 * a small custom RPC, and host-authoritative game logic.
 *
 * First role: The Tesla (Impostor) - charges two players (+ / -). While the charged pair is too
 * close a hidden countdown drains; it pauses when they separate and only refills in meetings. At
 * zero, both die. See Tesla.cs.
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
    public const string PluginVersion = "1.0.0.2";
    public static readonly System.Version Version = System.Version.Parse(PluginVersion);

    // Custom RPC ids. TOR's CustomRPC enum runs 100-183; other DaUnknown mods use 104/105/139/167,
    // 200-202, 246-253. 190 / 191 are free across all of them. Keep these globally unique.
    public const byte TeslaRpcId = 190;
    public const byte VersionHandshakeRpcId = 191;

    public static ManualLogSource Logger { get; private set; }

    internal static Assembly TORAssembly;

    public override void Load()
    {
        Logger = Log;
        Logger.LogInfo($"{PluginName} v{PluginVersion} loading...");

        var enabled = Config.Bind("General", "Enabled", true, "Enable this mod");
        if (!enabled.Value) {
            Logger.LogInfo($"{PluginName} is disabled in config - skipping load.");
            return;
        }

        TORAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");

        var harmony = new Harmony(PluginGuid);

        // The Tesla role. CreateOptions must run after TOR's CustomOptionHolder.Load() (guaranteed
        // by the hard dependency). Most patches are attribute-based and picked up by PatchAll below;
        // TryPatch adds the reflection patch on TOR's resetVariables + resolves the UncheckedMurder RPC.
        Tesla.CreateOptions();
        Tesla.TryPatch(harmony);

        // All attribute-based [HarmonyPatch] classes in this assembly (Tesla patches + handshake +
        // the PingTracker version line below).
        harmony.PatchAll(typeof(UnknownsCollectionPlugin).Assembly);

        // Self-updater: checks GitHub releases and offers an in-game update (channel-aware: follows the
        // shared test-versions toggle). Must exist before registration so the repo fields resolve.
        AddComponent<UnknownsCollectionUpdater>();

        // Register in the shared Mod Manager registry (cross-plugin, via AppDomain - no hard reference
        // to Useful TOR Stuff). Mirrors how ForceImpostorMod registers itself.
        RegisterInModManager(enabled);

        Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
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
                { "RuntimeEnabled", true }
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
        private const string LinkId = "unknownsCollectionVersion";
        // Shared with the other DaUnknown mods - keep this string identical everywhere. Clicking any
        // mod's name flips the same flag, so the "Modded by DaUnknown" credit appears at most once.
        private const string CreditKey = "TORMods.DaUnknownCreditVisible";

        private static bool CreditVisible() =>
            AppDomain.CurrentDomain.GetData(CreditKey) is bool b && b;

        public static void Postfix(PingTracker __instance)
        {
            if (__instance == null || __instance.text == null) return;
            string text = __instance.text.text;
            if (string.IsNullOrEmpty(text)) return;

            // Click the mod name to toggle the shared credit line. PingTracker.text is a world-space
            // TextMeshPro (no canvas), so the link raycast needs the rendering camera.
            if (Input.GetMouseButtonDown(0))
            {
                Camera cam = Camera.main;
                var canvas = __instance.text.canvas;
                if (canvas != null)
                    cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null
                        : (canvas.worldCamera != null ? canvas.worldCamera : Camera.main);
                int link = TMPro.TMP_TextUtilities.FindIntersectingLink(__instance.text, Input.mousePosition, cam);
                if (link != -1 && __instance.text.textInfo.linkInfo[link].GetLinkID() == LinkId)
                    AppDomain.CurrentDomain.SetData(CreditKey, !CreditVisible());
            }

            // Insert our version line once (guarded so it doesn't stack per frame).
            if (!text.Contains(LinkId))
            {
                string line = $"<link=\"{LinkId}\"><color=#1FB8FF>Unknown's Collection</color> v{VersionDisplay.Format(Version)}</link>";
                int nl = text.IndexOf('\n');
                text = nl >= 0
                    ? text.Substring(0, nl + 1) + line + "\n" + text.Substring(nl + 1)
                    : text + "\n" + line;
            }

            // Insert the shared credit under TOR's "Design by Bavari" line - but only if no other mod
            // already added it this frame, so "Modded by DaUnknown" appears at most once.
            if (CreditVisible() && !text.Contains("DaUnknown"))
            {
                string credit = "\n<size=70%>Modded by <color=#FCCE03FF>DaUnknown</color></size>";
                int anchor = text.IndexOf("Bavari");
                if (anchor >= 0)
                {
                    int lineEnd = text.IndexOf('\n', anchor);
                    text = lineEnd >= 0 ? text.Substring(0, lineEnd) + credit + text.Substring(lineEnd) : text + credit;
                }
                else text += credit;
            }

            __instance.text.text = text;
        }
    }
}
