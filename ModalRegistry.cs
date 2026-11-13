// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ModalRegistry - the list of open menus that every mod shares (AppDomain "TORMods.ModalRoots", 2026-11-13). Copy of
 * TOR - Forgotten Fixes' ModalGuard.cs registry: UC's panels add themselves (the game's buttons under them stop
 * reacting through Forgotten Fixes' guard), and UC's "?" does not open while another mod's menu is open.
 */
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnknownsCollection {

    /// <summary>The menu roots of all mods (AppDomain "TORMods.ModalRoots"); same code in every mod that uses it.</summary>
    internal static class ModalRegistry {
        private const string Key = "TORMods.ModalRoots";
        private static List<GameObject> list;

        private static List<GameObject> L {
            get {
                if (list != null) return list;
                var d = AppDomain.CurrentDomain.GetData(Key) as List<GameObject>;
                if (d == null) { d = new List<GameObject>(); AppDomain.CurrentDomain.SetData(Key, d); }
                return list = d;
            }
        }

        /// <summary>A menu root that blocks the game while it is active (kept until it is destroyed).</summary>
        internal static void Add(GameObject root) {
            if (root == null) return;
            var l = L;
            for (int i = 0; i < l.Count; i++) if (l[i] != null && l[i].Pointer == root.Pointer) return;
            l.Add(root);
            frame = -1;
        }

        // Panels that block only the game's buttons under them and leave walking and hotkeys alone (UC's role guide:
        // "the player can keep walking with the guide open"). AppDomain "TORMods.ClickBlockRoots".
        private const string ClickKey = "TORMods.ClickBlockRoots";
        private static List<GameObject> clickList;

        private static List<GameObject> C {
            get {
                if (clickList != null) return clickList;
                var d = AppDomain.CurrentDomain.GetData(ClickKey) as List<GameObject>;
                if (d == null) { d = new List<GameObject>(); AppDomain.CurrentDomain.SetData(ClickKey, d); }
                return clickList = d;
            }
        }

        /// <summary>A panel that blocks clicks on the game's buttons only (kept until it is destroyed).</summary>
        internal static void AddClickOnly(GameObject root) {
            if (root == null) return;
            var l = C;
            for (int i = 0; i < l.Count; i++) if (l[i] != null && l[i].Pointer == root.Pointer) return;
            l.Add(root);
        }

        /// <summary>Is a menu or a click-only panel open (clicks on the game's buttons are swallowed)?</summary>
        internal static bool ClicksBlocked() => AnyOpen() || Scan(C, null);

        private static int frame = -1;
        private static bool cached;

        /// <summary>Is a menu open (other than `except`, a mod's own panel that asks)?</summary>
        internal static bool AnyOpen(GameObject except = null) {
            if (except == null && Time.frameCount == frame) return cached;
            bool open = Scan(L, except);
            if (except == null) { frame = Time.frameCount; cached = open; }
            return open;
        }

        /// <summary>Is a menu or click-only panel open other than `except` (for a mod's own panel: "someone else's is on top")?</summary>
        internal static bool OtherOpen(GameObject except) => Scan(L, except) || Scan(C, except);

        private static bool Scan(List<GameObject> l, GameObject except) {
            bool open = false;
            for (int i = l.Count - 1; i >= 0; i--) {
                var go = l[i];
                if (go == null) { l.RemoveAt(i); continue; }
                if (except != null && go.Pointer == except.Pointer) continue;
                try {
                    if (!go.activeInHierarchy) continue;
                    var cv = go.GetComponent<Canvas>();
                    if (cv != null && !cv.enabled) continue;
                    open = true;
                } catch { }
            }
            return open;
        }
    }

}
