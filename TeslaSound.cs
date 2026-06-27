// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * Tesla warning sound. Plays a short cue when a charged victim enters the danger zone. The clip is an
 * embedded raw-PCM resource ("headerless 2-channel signed 32-bit PCM LE, 48 kHz"), loaded from THIS
 * assembly (TOR's own loader reads TOR's assembly, so we bring our own). Until a sound asset is
 * embedded (UnknownsCollection.Resources.tesla_warning.raw + an <EmbeddedResource> in the .csproj),
 * PlayWarning() simply no-ops. Drop-in once a copyright-free clip is chosen.
 */

using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace UnknownsCollection {
    public static class TeslaSound {
        private const string WarningResource = "UnknownsCollection.Resources.tesla_warning.raw";
        private static AudioClip warningClip;
        private static bool warningTried;

        public static void PlayWarning() {
            try {
                var clip = GetWarning();
                if (clip == null) return;
                if (SoundManager.Instance != null)
                    SoundManager.Instance.PlaySound(clip, false, 0.85f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogWarning($"[Tesla] PlayWarning failed: {e.Message}");
            }
        }

        private static AudioClip GetWarning() {
            if (warningTried) return warningClip;
            warningTried = true;
            warningClip = LoadRawClip(WarningResource, "teslaWarning");
            return warningClip;
        }

        // Raw (headerless) 2-channel signed 32-bit PCM (LE), 48 kHz - same format TOR historically used.
        private static AudioClip LoadRawClip(string path, string clipName) {
            try {
                Assembly assembly = Assembly.GetExecutingAssembly();
                Stream stream = assembly.GetManifestResourceStream(path);
                if (stream == null) return null; // asset not embedded yet -> silent
                var bytes = new byte[stream.Length];
                _ = stream.Read(bytes, 0, (int)stream.Length);
                float[] samples = new float[bytes.Length / 4];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (float)BitConverter.ToInt32(bytes, i * 4) / int.MaxValue;
                AudioClip clip = AudioClip.Create(clipName, samples.Length / 2, 2, 48000, false);
                clip.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
                clip.SetData(samples, 0);
                return clip;
            } catch {
                return null;
            }
        }
    }
}
