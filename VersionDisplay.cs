// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * Shared version-string formatting for all DaUnknown TOR mods.
 *
 * Version scheme: vX.Y.Z for a stable build, vX.Y.Z.W for a TEST build (W = 4th component, set by the
 * CI release workflow from a vX.Y.Z.W tag). A build is "test" iff System.Version.Revision > 0 (a plain
 * vX.Y.Z parses to Revision == -1). The 4th component is shown ONLY on test builds AND only while the
 * shared "show test versions" toggle is on (Mod Manager). The toggle is a process-wide AppDomain flag
 * (key below), identical contract across every mod so flipping it in one place affects all - no
 * cross-assembly references. This same small helper is duplicated verbatim into each mod.
 */

using System;

namespace UnknownsCollection {
    public static class VersionDisplay {
        // Shared across ALL DaUnknown mods - keep this string identical everywhere.
        public const string ShowTestVersionsKey = "TORMods.ShowTestVersions";

        // Default true: a test build wants to advertise its test number unless explicitly hidden.
        public static bool ShowTestVersions() {
            try { return !(AppDomain.CurrentDomain.GetData(ShowTestVersionsKey) is bool b) || b; }
            catch { return true; }
        }

        public static void SetShowTestVersions(bool value) {
            try { AppDomain.CurrentDomain.SetData(ShowTestVersionsKey, value); } catch { }
        }

        // Formats without a leading "v". Callers prepend "v" themselves.
        public static string Format(Version v) {
            if (v == null) return "?";
            bool isTest = v.Revision > 0;
            if (isTest && ShowTestVersions())
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
