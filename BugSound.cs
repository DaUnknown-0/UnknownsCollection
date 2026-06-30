// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Bug glitch sound. Headerless 2-channel signed 32-bit PCM LE at 48 kHz (same format as TeslaSound).
 * Embedded as UnknownsCollection.Resources.bug_effect.raw.
 */

using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace UnknownsCollection {
    public static class BugSound {
        private const string ResourceName = "UnknownsCollection.Resources.bug_effect.raw";
        private static AudioClip clip;
        private static bool tried;

        public static AudioClip LoadClip() {
            if (tried) return clip;
            tried = true;
            try {
                Assembly asm = Assembly.GetExecutingAssembly();
                Stream stream = asm.GetManifestResourceStream(ResourceName);
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                _ = stream.Read(bytes, 0, (int)stream.Length);
                float[] samples = new float[bytes.Length / 4];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (float)BitConverter.ToInt32(bytes, i * 4) / int.MaxValue;
                clip = AudioClip.Create("BugGlitch", samples.Length / 2, 2, 48000, false);
                clip.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
                clip.SetData(samples, 0);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[BugSound] Failed to load: {e.Message}");
            }
            return clip;
        }
    }
}
