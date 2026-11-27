// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCLobbyCommands - the mod author's lobby gags (User 2026-11-27, list picked on the "Lobby-Befehle" page).
 *
 * The author types "<role>.trigger.<effect>(<args>)" into the lobby chat, for example
 * "bug.trigger.bluescreen(all)", "maniac.trigger.bomb(drsus)" or "werewolf.trigger.howl()". The chat line stays visible;
 * every client reacts when it shows the line (ChatController.AddChat, the author's own client included).
 *
 *   - Only in the lobby: no round runs, so nothing here can carry game information.
 *   - Only from the author's account (UCOnlineHats.IsOwner, PUID / friend-code hashes); every client checks the sender.
 *   - Arguments: "(all)" or "()" for everybody, "(Name)" for one player (case does not matter, a unique start of the
 *     name is enough, an ambiguous start hits nobody), "(Name, Name)" for the swap. Effects that hit everybody anyway
 *     take "()".
 *   - Purely cosmetic and local: no RPC of its own. An effect "on the target's screen" runs only on the target's
 *     client; an effect "at the target" runs on every client at that player's position. Nothing survives the lobby:
 *     looks, sizes, alpha and tints go back when the effect ends, at the latest when a game starts or the lobby is left.
 *   - The Bug crash and the Maniac's defuse puzzle run in their gag mode (no Skip vote owed, no result sent).
 *   - A command repeats at most every 3 s per client (the blue screen every 20 s).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;

namespace UnknownsCollection {
    internal static class UCLobbyCommands {
        private const string LogPrefix = "[LobbyCmd]";
        private enum Arg { Players, None, TwoPlayers }

        private sealed class Cmd {
            public Arg Arg;
            public float Cooldown = 3f;
            public Action<List<PlayerControl>, PlayerControl> Run;   // targets, author
        }

        private static readonly Dictionary<string, Cmd> cmds = new Dictionary<string, Cmd>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> lastRun = new Dictionary<string, float>();
        private static readonly Regex Rx = new Regex(@"^\s*([a-z]+)\.trigger\.([a-z]+)\s*\((.*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void Add(string name, Arg arg, Action<List<PlayerControl>, PlayerControl> run, float cooldown = 3f) =>
            cmds[name] = new Cmd { Arg = arg, Run = run, Cooldown = cooldown };

        // =========================================================== dispatch

        private static bool InLobby() {
            try {
                var c = AmongUsClient.Instance;
                return c != null && c.GameState == InnerNet.InnerNetClient.GameStates.Joined && LobbyBehaviour.Instance != null;
            } catch { return false; }
        }

        [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
        static class ChatPatch {
            public static void Postfix([HarmonyArgument(0)] PlayerControl sourcePlayer, [HarmonyArgument(1)] string chatText) {
                try {
                    if (string.IsNullOrEmpty(chatText) || chatText.IndexOf(".trigger.", StringComparison.OrdinalIgnoreCase) < 0) return;
                    var m = Rx.Match(chatText);
                    if (!m.Success || !InLobby() || !UCOnlineHats.IsOwner(sourcePlayer)) return;
                    if (cmds.Count == 0) Register();
                    string key = (m.Groups[1].Value + "." + m.Groups[2].Value).ToLowerInvariant();
                    if (!cmds.TryGetValue(key, out var cmd)) return;
                    if (lastRun.TryGetValue(key, out float last) && Time.time - last < cmd.Cooldown) return;
                    var targets = Resolve(cmd.Arg, m.Groups[3].Value);
                    if (targets == null) { UnknownsCollectionPlugin.Logger?.LogInfo($"{LogPrefix} {key}: no matching player."); return; }
                    lastRun[key] = Time.time;
                    UnknownsCollectionPlugin.Logger?.LogInfo($"{LogPrefix} {key} on {(targets.Count == 0 ? "everybody" : string.Join(", ", targets.Select(t => t.Data?.PlayerName)))}.");
                    cmd.Run(targets, sourcePlayer);
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogWarning($"{LogPrefix} failed: {e.Message}");
                }
            }
        }

        /// <summary>The players an argument names; null when a name matches nobody (or more than one player).</summary>
        private static List<PlayerControl> Resolve(Arg arg, string raw) {
            var all = PlayerControl.AllPlayerControls.ToArray().Where(p => p != null && p.Data != null && !p.Data.Disconnected).ToList();
            string s = (raw ?? "").Trim();
            if (arg == Arg.None) return new List<PlayerControl>();
            if (s.Length == 0 || s.Equals("all", StringComparison.OrdinalIgnoreCase)) return arg == Arg.TwoPlayers ? null : all;
            var parts = s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (arg == Arg.TwoPlayers && parts.Count != 2) return null;
            if (arg == Arg.Players && parts.Count != 1) return null;
            var res = new List<PlayerControl>();
            foreach (var part in parts) {
                var p = FindPlayer(all, part);
                if (p == null) return null;
                res.Add(p);
            }
            return res;
        }

        private static PlayerControl FindPlayer(List<PlayerControl> all, string name) {
            var exact = all.Where(p => string.Equals(p.Data.PlayerName?.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var start = all.Where(p => (p.Data.PlayerName ?? "").Trim().StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
            return start.Count == 1 ? start[0] : null;
        }

        // =========================================================== timing and clean-up

        private sealed class Job { public float until; public Action<float> tick; public Action end; public float start; }
        private static readonly List<Job> jobs = new List<Job>();
        private static readonly List<(float at, Action act)> later = new List<(float, Action)>();

        /// <summary>Runs tick(p) every frame for seconds (p 0..1), then end once. end also runs when the lobby goes.</summary>
        private static void During(float seconds, Action<float> tick, Action end = null) =>
            jobs.Add(new Job { start = Time.time, until = Time.time + seconds, tick = tick, end = end });

        private static void After(float seconds, Action act) => later.Add((Time.time + seconds, act));

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (jobs.Count == 0 && later.Count == 0) { Record(); return; }
                if (!InLobby()) { EndAll(); return; }
                Record();
                float now = Time.time;
                for (int i = later.Count - 1; i >= 0; i--) {
                    if (later[i].at > now) continue;
                    var a = later[i].act;
                    later.RemoveAt(i);
                    try { a(); } catch (Exception e) { UnknownsCollectionPlugin.Logger?.LogWarning($"{LogPrefix} step failed: {e.Message}"); }
                }
                for (int i = jobs.Count - 1; i >= 0; i--) {
                    var j = jobs[i];
                    float p = Mathf.Clamp01((now - j.start) / Mathf.Max(0.01f, j.until - j.start));
                    try { j.tick?.Invoke(p); } catch { }
                    if (now < j.until) continue;
                    jobs.RemoveAt(i);
                    try { j.end?.Invoke(); } catch { }
                }
            }
        }

        private static void EndAll() {
            later.Clear();
            var copy = jobs.ToList();
            jobs.Clear();
            foreach (var j in copy) { try { j.end?.Invoke(); } catch { } }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class JoinPatch { public static void Postfix() => EndAll(); }
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
        static class StartPatch { public static void Prefix() => EndAll(); }

        // Every player's last 4 s of positions, for the clone (lobby only, ten samples a second).
        private static readonly Dictionary<byte, List<Vector2>> trail = new Dictionary<byte, List<Vector2>>();
        private static float nextSample;
        private static void Record() {
            if (Time.time < nextSample || !InLobby()) return;
            nextSample = Time.time + 0.1f;
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (p == null) continue;
                if (!trail.TryGetValue(p.PlayerId, out var l)) trail[p.PlayerId] = l = new List<Vector2>();
                l.Add(p.transform.position);
                if (l.Count > 40) l.RemoveAt(0);
            }
        }

        // =========================================================== helpers

        private static bool IsLocal(PlayerControl p) => p != null && PlayerControl.LocalPlayer != null && p.PlayerId == PlayerControl.LocalPlayer.PlayerId;
        private static Vector2 Pos(PlayerControl p) => p.GetTruePosition();
        private static bool Near(PlayerControl p, float r) => PlayerControl.LocalPlayer != null && Vector2.Distance(Pos(PlayerControl.LocalPlayer), Pos(p)) <= r;

        private static void Flash(Color c, float seconds = 1f, string msg = "") { try { Helpers.showFlash(c, seconds, msg); } catch { } }

        private static void SetAlpha(PlayerControl p, float a) {
            try {
                var c = p.cosmetics;
                c.currentBodySprite.BodySprite.color = c.currentBodySprite.BodySprite.color.SetAlpha(a);
                p.SetHatAndVisorAlpha(a);
                if (c.skin != null && c.skin.layer != null) c.skin.layer.color = c.skin.layer.color.SetAlpha(a);
                c.nameText.color = c.nameText.color.SetAlpha(a);
                if (c.currentPet != null) {
                    foreach (var r in c.currentPet.renderers) r.color = r.color.SetAlpha(a);
                    foreach (var r in c.currentPet.shadows) r.color = r.color.SetAlpha(a);
                }
            } catch { }
        }

        private static void SetTint(PlayerControl p, Color c) {
            try { p.cosmetics.currentBodySprite.BodySprite.color = c; } catch { }
        }

        private static void Restore(PlayerControl p) {
            if (p == null) return;
            try { p.setDefaultLook(); } catch { }
            SetAlpha(p, 1f);
            SetTint(p, Color.white);
            SetScale(p, VanillaScale);
            SetOutline(p, false, Color.clear);
        }

        private const float VanillaScale = 0.7f;
        private static void SetScale(PlayerControl p, float s) {
            try { if (p != null) p.transform.localScale = new Vector3(s, s, 1f); } catch { }
        }

        private static void SetOutline(PlayerControl p, bool on, Color c) {
            try {
                var mat = p.cosmetics.currentBodySprite.BodySprite.material;
                mat.SetFloat("_Outline", on ? 1f : 0f);
                mat.SetColor("_OutlineColor", c);
            } catch { }
        }

        /// <summary>A world sprite at a spot (or following a player), removed after seconds.</summary>
        private static SpriteRenderer WorldSprite(string name, Sprite sprite, Vector2 at, float seconds, float scale = 1f) {
            var go = new GameObject(name) { layer = 11 };
            go.transform.position = new Vector3(at.x, at.y, at.y / 1000f - 0.5f);
            go.transform.localScale = Vector3.one * scale;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            During(seconds, null, () => { if (go != null) UnityEngine.Object.Destroy(go); });
            return sr;
        }

        /// <summary>A sprite in front of the camera covering the screen (the HUD is world space under the camera).</summary>
        private static SpriteRenderer ScreenSprite(string name, Sprite sprite, float seconds, float size) {
            var hud = HudManager.Instance;
            var go = new GameObject(name) { layer = hud.gameObject.layer };
            go.transform.SetParent(hud.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, -60f);
            go.transform.localScale = Vector3.one * size;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            During(seconds, null, () => { if (go != null) UnityEngine.Object.Destroy(go); });
            return sr;
        }

        private static Sprite spiral, cone;
        private static Sprite Spiral() {
            if (spiral != null) return spiral;
            const int n = 256;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) {
                float dx = (x - n / 2f) / (n / 2f), dy = (y - n / 2f) / (n / 2f);
                float r = Mathf.Sqrt(dx * dx + dy * dy), a = Mathf.Atan2(dy, dx);
                float band = Mathf.Repeat(r * 6f - a / (2f * Mathf.PI), 1f);
                bool purple = band < 0.5f;
                byte alpha = r > 1f ? (byte)0 : (byte)(Mathf.Clamp01(1.1f - r) * 230);
                px[y * n + x] = purple ? new Color32(120, 40, 200, alpha) : new Color32(240, 235, 255, alpha);
            }
            tex.SetPixels32(px); tex.Apply();
            return spiral = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite Cone() {
            if (cone != null) return cone;
            const int w = 256, h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
                float fx = x / (float)w, fy = Mathf.Abs(y - h / 2f) / (h / 2f);
                bool inside = fy <= fx * 0.9f;
                byte a = inside ? (byte)(Mathf.Clamp01(1f - fx) * 150 + 30) : (byte)0;
                px[y * w + x] = new Color32(255, 230, 120, a);
            }
            tex.SetPixels32(px); tex.Apply();
            return cone = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0f, 0.5f), 50f);
        }

        // =========================================================== the commands

        private static void Register() {
            // ---- Unknown's Collection ----
            Add("bug.bluescreen", Arg.Players, (ts, a) => { if (ts.Any(IsLocal)) BugCrashScreen.Play(12f, gag: true); }, 20f);

            Add("maniac.bomb", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    var at = Pos(t);
                    foreach (float d in new[] { 0f, 0.7f, 1.25f, 1.65f, 1.95f, 2.15f, 2.3f }) After(d, () => UCAssets.PlayFuse(t != null ? Pos(t) : at));
                    After(2.55f, () => {
                        var p = t != null ? Pos(t) : at;
                        ManiacFx.SpawnExplosion(p);
                        UCAssets.PlayExplosion(p);
                        if (t != null && Near(t, 3.5f)) Flash(new Color(1f, 0.85f, 0.6f), 0.6f);
                    });
                }
            });

            Add("maniac.defuse", Arg.Players, (ts, a) => { if (ts.Any(IsLocal)) ManiacDefuse.Open(30f, gag: true); }, 10f);

            Add("werewolf.transform", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    WerewolfFx.SpawnFlare(Pos(t));
                    UCAssets.PlayWerewolfTransformAt(Pos(t));
                    try { t.setLook("", 6, "", "", "", ""); } catch { }
                    After(0.6f, () => {
                        try { t.setLook(t.Data.PlayerName, 6, UCHats.WerewolfHatId, "", "", ""); } catch { }
                        UCAssets.PlayWerewolfHowlAt(Pos(t));
                    });
                    During(8f, null, () => {
                        Restore(t);
                        WerewolfFx.SpawnFlare(Pos(t), 0.4f);
                        UCAssets.PlayWerewolfRevertAt(Pos(t));
                    });
                }
            });

            Add("werewolf.howl", Arg.None, (ts, a) => UCAssets.PlayWerewolfHowl());

            Add("tesla.discharge", Arg.Players, (ts, a) => {
                foreach (var t in ts) { TeslaKillFx.Play(Pos(t), null); UCAssets.PlayTeslaDischargeAt(Pos(t)); }
            });

            Add("poltergeist.poof", Arg.Players, (ts, a) => {
                foreach (var t in ts) { PoltergeistFx.SpawnPoof(Pos(t)); UCAssets.PlayPoof(Pos(t)); }
            });

            Add("poltergeist.hex", Arg.Players, (ts, a) => {
                foreach (var t in ts) { PoltergeistFx.SpawnHexBurst(t); UCAssets.PlayHexAt(Pos(t)); }
                if (!ts.Any(IsLocal)) return;
                bool blind = UnityEngine.Random.value < 0.5f;
                Flash(blind ? new Color(0.05f, 0f, 0.1f) : new Color(0.2f, 1f, 0.3f), 4f);
            });

            Add("saboteur.trap", Arg.Players, (ts, a) => {
                foreach (var t in ts) UCAssets.PlayTrapSnap(Pos(t));
                var me = PlayerControl.LocalPlayer;
                if (me == null || !ts.Any(IsLocal)) return;
                bool froze = me.moveable;
                me.moveable = false;
                Flash(new Color(1f, 0.6f, 0.1f), 0.5f);
                During(2f, p => { try { me.MyPhysics.body.velocity = Vector2.zero; } catch { } }, () => { if (froze && me != null) me.moveable = true; });
            });

            Add("illusionist.clone", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    if (!trail.TryGetValue(t.PlayerId, out var path) || path.Count < 2) continue;
                    var pts = path.ToList();
                    var go = new GameObject("LobbyClone") { layer = 8 };
                    var sr = go.AddComponent<SpriteRenderer>();
                    try {
                        sr.material = t.cosmetics.currentBodySprite.BodySprite.material;
                    } catch { }
                    UCAssets.PlayCloneShimmer(pts[0]);
                    During(pts.Count * 0.1f, p => {
                        if (go == null || t == null) return;
                        int i = Mathf.Min(pts.Count - 1, Mathf.FloorToInt(p * (pts.Count - 1)));
                        var at = pts[i];
                        go.transform.position = new Vector3(at.x, at.y, at.y / 1000f);
                        go.transform.localScale = t.transform.localScale;
                        try {
                            sr.sprite = t.cosmetics.currentBodySprite.BodySprite.sprite;
                            sr.flipX = i + 1 < pts.Count && pts[i + 1].x < at.x;
                        } catch { }
                        sr.color = new Color(1f, 1f, 1f, 0.55f);
                    }, () => { if (go != null) UnityEngine.Object.Destroy(go); });
                }
            });

            Add("faker.body", Arg.Players, (ts, a) => {
                var prefab = GameManager.Instance != null ? GameManager.Instance.DeadBodyPrefab : null;
                if (prefab == null) return;
                foreach (var t in ts) {
                    var db = UnityEngine.Object.Instantiate(prefab);
                    db.enabled = false;
                    db.ParentId = byte.MaxValue - 1;   // no real player: nobody can report it, nothing looks it up
                    if (db.bodyRenderers != null) foreach (var r in db.bodyRenderers) if (r != null) t.SetPlayerMaterialColors(r);
                    if (db.bloodSplatter != null) t.SetPlayerMaterialColors(db.bloodSplatter);
                    var at = Pos(t) + new Vector2(0.7f, 0f);
                    db.transform.position = new Vector3(at.x, at.y, at.y / 1000f);
                    db.enabled = true;
                    During(6f, null, () => { if (db != null) UnityEngine.Object.Destroy(db.gameObject); });
                }
            });

            Add("skinwalker.skin", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    if (a == null || t.PlayerId == a.PlayerId) continue;
                    try {
                        var o = a.Data.DefaultOutfit;
                        t.setLook(a.Data.PlayerName, o.ColorId, o.HatId, o.VisorId, o.SkinId, o.PetId);
                    } catch { }
                    During(8f, null, () => Restore(t));
                }
            });

            Add("giant.grow", Arg.Players, (ts, a) => {
                foreach (var t in ts) During(8f, p => SetScale(t, Mathf.Lerp(VanillaScale, 1.05f, Mathf.Clamp01(Mathf.Min(p, 1f - p) * 12f))), () => SetScale(t, VanillaScale));
            });

            Add("hunter.silver", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    WerewolfFx.PlaySilverDeath(Pos(t));
                    SetAlpha(t, 0f);
                    During(1.6f, p => SetAlpha(t, 0f), () => SetAlpha(t, 1f));
                }
            });

            Add("poltergeist.manifest", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    var at = Pos(t) + new Vector2(-0.8f, 0f);
                    PoltergeistFx.SpawnManifestReveal(at);
                    UCAssets.PlayManifestAt(at);
                    var sr = WorldSprite("LobbyManifest", t.cosmetics.currentBodySprite.BodySprite.sprite, at, 1.6f, t.transform.localScale.x);
                    try { sr.material = t.cosmetics.currentBodySprite.BodySprite.material; } catch { }
                    During(1.6f, p => { if (sr != null) sr.color = new Color(0.8f, 0.7f, 1f, 0.6f * (1f - p)); });
                }
            });

            Add("shade.vanish", Arg.Players, (ts, a) => {
                UCAssets.PlayShadeVanish();
                foreach (var t in ts) During(2.8f, p => SetAlpha(t, p < 0.15f ? 1f - p / 0.15f : p > 0.85f ? (p - 0.85f) / 0.15f : 0f),
                    () => { SetAlpha(t, 1f); UCAssets.PlayShadeRevealAt(Pos(t)); });
            });

            Add("pelican.swallow", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    UCAssets.PlayPelicanSwallowAt(Pos(t));
                    During(3f, p => SetAlpha(t, 0f), () => { SetAlpha(t, 1f); UCAssets.PlayPelicanReleaseAt(Pos(t)); PoltergeistFx.SpawnPoof(Pos(t)); });
                }
            });

            Add("collector.relic", Arg.Players, (ts, a) => {
                foreach (var t in ts) { CollectorRelics.SpawnRelicArrival(Pos(t)); UCAssets.PlayRelicPickup(Pos(t)); }
            });

            Add("necromancer.raise", Arg.Players, (ts, a) => {
                foreach (var t in ts) During(5f, p => SetTint(t, Color.Lerp(Color.white, new Color(0.45f, 1f, 0.55f), 0.55f + 0.25f * Mathf.Sin(Time.time * 6f))),
                    () => SetTint(t, Color.white));
            });

            Add("hypnotist.spiral", Arg.Players, (ts, a) => {
                if (!ts.Any(IsLocal) || HudManager.Instance == null) return;
                var sr = ScreenSprite("LobbySpiral", Spiral(), 3.5f, 9f);
                During(3.5f, p => {
                    if (sr == null) return;
                    sr.transform.localRotation = Quaternion.Euler(0f, 0f, -p * 900f);
                    sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(Mathf.Min(p, 1f - p) * 5f) * 0.75f);
                });
            });

            Add("pirate.spyglass", Arg.Players, (ts, a) => Arrows(ts, new Color(1f, 0.78f, 0.25f), 5f));

            Add("stalker.cone", Arg.Players, (ts, a) => {
                if (a == null) return;
                foreach (var t in ts) {
                    if (t.PlayerId == a.PlayerId) continue;
                    var sr = WorldSprite("LobbyCone", Cone(), Pos(a), 5f);
                    During(5f, p => {
                        if (sr == null || a == null || t == null) return;
                        Vector2 from = Pos(a), to = Pos(t), d = to - from;
                        sr.transform.position = new Vector3(from.x, from.y, from.y / 1000f - 0.4f);
                        sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                        float len = Mathf.Max(1f, d.magnitude);
                        sr.transform.localScale = new Vector3(len / 5.12f, Mathf.Max(0.6f, len / 6f), 1f);
                    });
                    if (IsLocal(t)) Flash(new Color(1f, 0.9f, 0.4f), 1.2f, "...");
                }
            });

            Add("king.flash", Arg.Players, (ts, a) => {
                if (ts.Count != 1) return;
                UCAssets.PlayUcReveal();
                Flash(new Color(1f, 0.82f, 0.2f), 2.2f, "Long live " + ts[0].Data.PlayerName + "!");
            });

            Add("void.exile", Arg.Players, (ts, a) => {
                foreach (var t in ts) if (IsLocal(t)) Flash(new Color(0.08f, 0f, 0.14f), 3.2f, t.Data.PlayerName + " was swallowed by the Void.");
            });

            Add("sleepwalker.sleep", Arg.Players, (ts, a) => {
                var me = PlayerControl.LocalPlayer;
                if (me == null || !ts.Any(IsLocal)) return;
                bool froze = me.moveable;
                me.moveable = false;
                var dir = UnityEngine.Random.insideUnitCircle.normalized;
                During(3f, p => {
                    try {
                        if (Mathf.Repeat(p * 3f, 1f) < 0.02f) dir = (dir + UnityEngine.Random.insideUnitCircle * 0.8f).normalized;
                        me.MyPhysics.body.velocity = dir * 1.6f;
                    } catch { }
                }, () => { try { me.MyPhysics.body.velocity = Vector2.zero; } catch { } if (froze && me != null) me.moveable = true; });
            });

            // ---- The Other Roles ----
            Add("camouflager.camo", Arg.Players, (ts, a) => {
                foreach (var t in ts) { try { t.setLook("", 6, "", "", "", ""); } catch { } During(8f, null, () => Restore(t)); }
            });

            Add("morphling.morph", Arg.Players, (ts, a) => {
                var all = PlayerControl.AllPlayerControls.ToArray().Where(p => p != null && p.Data != null && !p.Data.Disconnected).ToList();
                foreach (var t in ts) {
                    var others = all.Where(p => p.PlayerId != t.PlayerId).ToList();
                    if (others.Count == 0) continue;
                    var o = others[UnityEngine.Random.Range(0, others.Count)];
                    var oo = o.Data.DefaultOutfit;
                    try { t.setLook(o.Data.PlayerName, oo.ColorId, oo.HatId, oo.VisorId, oo.SkinId, oo.PetId); } catch { }
                    During(8f, null, () => Restore(t));
                }
            });

            Add("ninja.vanish", Arg.Players, (ts, a) => {
                foreach (var t in ts) During(5f, p => SetAlpha(t, IsLocal(t) ? 0.25f : 0f), () => SetAlpha(t, 1f));
            });

            Add("mini.shrink", Arg.Players, (ts, a) => {
                foreach (var t in ts) During(8f, p => SetScale(t, Mathf.Lerp(VanillaScale, 0.35f, Mathf.Clamp01(Mathf.Min(p, 1f - p) * 12f))), () => SetScale(t, VanillaScale));
            });

            Add("medic.shield", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    During(4f, p => SetOutline(t, true, Medic.shieldedColor), () => SetOutline(t, false, Color.clear));
                    if (IsLocal(t)) Flash(Medic.shieldedColor, 0.5f);
                }
            });

            Add("timemaster.rewind", Arg.Players, (ts, a) => { if (ts.Any(IsLocal)) Flash(TimeMaster.color, 1.4f); });

            Add("vampire.bite", Arg.Players, (ts, a) => {
                if (!ts.Any(IsLocal)) return;
                try { SoundEffectsManager.play("vampireBite"); } catch { }
                Flash(new Color(0.75f, 0f, 0.05f), 0.7f);
            });

            Add("trickster.box", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    var sr = WorldSprite("LobbyBox", JackInTheBox.getBoxAnimationSprite(0), Pos(t) + new Vector2(0.7f, 0.1f), 3f);
                    During(3f, p => { if (sr != null) sr.sprite = JackInTheBox.getBoxAnimationSprite(Mathf.Min(17, Mathf.FloorToInt(p * 3f * 18f / 1.2f))); });
                }
            });

            Add("portalmaker.portal", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    var sr = WorldSprite("LobbyPortal", Portal.getFgAnimationSprite(0), Pos(t) + new Vector2(0.8f, 0.2f), 3.4f);
                    try { sr.material = FastDestroyableSingleton<HatManager>.Instance.PlayerMaterial; PlayerMaterial.SetColors(t.Data.DefaultOutfit.ColorId, sr); } catch { }
                    During(3.4f, p => { if (sr != null) sr.sprite = Portal.getFgAnimationSprite(Mathf.Min(204, Mathf.FloorToInt(p * 205f))); });
                }
            });

            Add("detective.footprints", Arg.Players, (ts, a) => {
                float next = 0f;
                During(10f, p => {
                    if (Time.time < next) return;
                    next = Time.time + 0.15f;
                    foreach (var t in ts) { try { FootprintHolder.Instance.MakeFootprint(t); } catch { } }
                });
            });

            Add("tracker.arrow", Arg.Players, (ts, a) => Arrows(ts, Tracker.color, 5f));

            Add("warlock.curse", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    var sr = WorldSprite("LobbyCurse", Warlock.getCurseButtonSprite(), Pos(t), 5f, 0.35f);
                    During(5f, p => {
                        if (sr == null || t == null) return;
                        var at = Pos(t) + new Vector2(0f, 1.1f + 0.08f * Mathf.Sin(Time.time * 4f));
                        sr.transform.position = new Vector3(at.x, at.y, -5f);
                        sr.color = new Color(0.75f, 0.4f, 1f, 0.9f);
                    });
                }
            });

            Add("eraser.erase", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    bool hidden = false; float next = 0f;
                    During(2f, p => {
                        if (Time.time < next) return;
                        next = Time.time + 0.12f;
                        hidden = !hidden;
                        try { if (hidden) t.setLook("", 6, "", "", "", ""); else t.setDefaultLook(); } catch { }
                    }, () => Restore(t));
                }
            });

            Add("swapper.swap", Arg.TwoPlayers, (ts, a) => {
                if (ts.Count != 2 || ts[0].PlayerId == ts[1].PlayerId) return;
                Vector2 p0 = ts[0].transform.position, p1 = ts[1].transform.position;
                foreach (var t in ts) PoltergeistFx.SpawnPoof(Pos(t));
                var me = PlayerControl.LocalPlayer;
                if (me == null) return;
                try {
                    if (me.PlayerId == ts[0].PlayerId) me.NetTransform.RpcSnapTo(p1);
                    else if (me.PlayerId == ts[1].PlayerId) me.NetTransform.RpcSnapTo(p0);
                } catch { }
                if (ts.Any(IsLocal)) Flash(Swapper.color, 0.5f);
            });

            // ---- the game's own screens ----
            Add("vanilla.meeting", Arg.Players, (ts, a) => {
                if (!ts.Any(IsLocal)) return;
                try {
                    var ko = HudManager.Instance.KillOverlay;
                    // the emergency splash is a MeetingCalledAnimation member of the overlay (name differs by version)
                    MeetingCalledAnimation anim = null;
                    foreach (var pr in typeof(KillOverlay).GetProperties())
                        if (pr.PropertyType == typeof(MeetingCalledAnimation) && pr.Name.IndexOf("Emergency", StringComparison.OrdinalIgnoreCase) >= 0) { anim = pr.GetValue(ko) as MeetingCalledAnimation; break; }
                    if (anim == null)
                        foreach (var pr in typeof(KillOverlay).GetProperties())
                            if (pr.PropertyType == typeof(MeetingCalledAnimation)) { anim = pr.GetValue(ko) as MeetingCalledAnimation; if (anim != null) break; }
                    if (anim != null) ko.ShowMeeting(anim, (a ?? PlayerControl.LocalPlayer).Data.DefaultOutfit);
                } catch { }
            }, 6f);

            Add("vanilla.eject", Arg.Players, (ts, a) => {
                foreach (var t in ts) if (IsLocal(t)) Flash(Color.black, 3.5f, t.Data.PlayerName + " was not The Impostor.");
            }, 6f);

            Add("vanilla.kill", Arg.Players, (ts, a) => {
                foreach (var t in ts) {
                    if (!IsLocal(t) || a == null) continue;
                    try { HudManager.Instance.KillOverlay.ShowKillAnimation(a.Data, t.Data); } catch { }
                }
            }, 6f);

            Add("vanilla.reactor", Arg.None, (ts, a) => {
                float next = 0f;
                UCAssets.PlaySaboteurAlarm();
                During(5f, p => {
                    if (Time.time < next) return;
                    next = Time.time + 0.9f;
                    Flash(new Color(1f, 0.1f, 0.05f), 0.8f);
                });
            }, 8f);

            Add("vanilla.shapeshift", Arg.Players, (ts, a) => {
                foreach (var t in ts) { PoltergeistFx.SpawnPoof(Pos(t)); UCAssets.PlayPoof(Pos(t)); }
            });

            Add("vanilla.victory", Arg.Players, (ts, a) => {
                if (!ts.Any(IsLocal)) return;
                bool imp = UnityEngine.Random.value < 0.5f;
                Flash(imp ? Palette.ImpostorRed : Palette.CrewmateBlue, 3f, imp ? "Victory: Impostors win" : "Victory: Crewmates win");
            }, 6f);
        }

        private static void Arrows(List<PlayerControl> ts, Color color, float seconds) {
            var me = PlayerControl.LocalPlayer;
            foreach (var t in ts) {
                if (t == null || IsLocal(t)) continue;
                var arrow = new Arrow(color);
                During(seconds, p => { try { if (t != null) arrow.Update(t.transform.position); } catch { } },
                       () => { try { if (arrow.arrow != null) UnityEngine.Object.Destroy(arrow.arrow); } catch { } });
            }
        }
    }
}
