// Unknown's Collection - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UCHelpMenu - the "?" help button (top right, lobby AND in-game) plus the role overview panel.
 *
 * The panel lists every Unknown's Collection role that COULD be active this game (spawn rate > 0 -
 * option-based, so it never leaks which roles actually spawned; same anti-leak rule as the crew
 * SEARCH button). Clicking a role shows its explanation; a language row toggles between Deutsch and
 * English (persisted via BepInEx config).
 *
 * UI mechanics: everything is plain SpriteRenderer/TextMeshPro objects parented to the HudManager
 * (world-space, sortingOrder 500+ - above world/HUD, below Helpers.showFlash's 999 flashes, see
 * BeaconFx). Clicks are resolved MANUALLY each frame (Camera.main.ScreenToWorldPoint vs. stored
 * hit boxes) instead of PassiveButtons - no collider/layer wrangling, works identically in the
 * lobby and in-game. The "?" button is anchored to the top-right screen edge via AspectPosition
 * (the same component TOR's draft uses), sitting below the vanilla settings/mod buttons.
 *
 * Visibility follows hud.UseButton.isActiveAndEnabled - exactly the gate CustomButton uses - so the
 * button shows in the lobby and during rounds but not in meetings/intro; an open panel force-closes
 * when the gate drops.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TMPro;

namespace UnknownsCollection {
    public static class UCHelpMenu {
        // ---- role table ----
        private enum Faction { Impostor, Crew, Neutral, Ghost }

        private sealed class Entry {
            public string name;
            public Faction faction;
            public Func<Color> color;
            public Func<CustomOption> rate;
            public string de;
            public string en;
        }

        private static List<Entry> entries;
        private static List<Entry> Entries() {
            if (entries != null) return entries;
            entries = new List<Entry> {
                E("Tesla", Faction.Impostor, () => Tesla.Color, () => Tesla.SpawnRate,
                  "Lädt im Meeting zwei Spieler entgegengesetzt auf (+/−). Stehen die beiden zu nah beieinander, läuft ein versteckter Countdown – bei null sterben beide. Trennung pausiert ihn, nur Meetings füllen ihn wieder auf.",
                  "Charges two players in a meeting (+/−). While the pair stands too close together, a hidden countdown drains – at zero both die. Separating pauses it; only meetings refill it."),
                E("Saboteur", Faction.Impostor, () => Saboteur.Color, () => Saboteur.SpawnRate,
                  "Sabotiert eine Task-Konsole – der erste Crewmate, der sie abschließt, stirbt. Alternativ legt er unsichtbare Stun-Fallen. Die Crew kann Konsolen per SEARCH prüfen und entschärfen.",
                  "Sabotages a task console – the first crewmate to finish it dies. Can also lay invisible stun traps. The crew can SEARCH consoles and defuse them."),
                E("Poisoner", Faction.Impostor, () => Palette.ImpostorRed, () => Poisoner.SpawnRate,
                  "Seine Kills vergiften die Leiche: Wer sie meldet, wird vergiftet und stirbt nach einigen Meetings – außer der Medic verabreicht rechtzeitig das Gegenmittel.",
                  "Their kills poison the body: whoever reports it gets poisoned and dies after a few meetings – unless the Medic administers the antidote in time."),
                E("Silencer", Faction.Impostor, () => Palette.ImpostorRed, () => Silencer.SpawnRate,
                  "Markiert einen Spieler und bringt ihn zum Schweigen: Im nächsten Meeting kann das Opfer weder chatten noch abstimmen.",
                  "Marks a player and silences them: in the next meeting the victim can neither chat nor vote."),
                E("Illusionist", Faction.Impostor, () => Palette.ImpostorRed, () => Illusionist.SpawnRate,
                  "Zeichnet einen eigenen Laufweg auf und spielt ihn später als unverwundbaren Klon ab – das perfekte falsche Alibi.",
                  "Records their own path and replays it later as an unkillable clone – the perfect fake alibi."),
                E("Maniac", Faction.Impostor, () => Maniac.Color, () => Maniac.SpawnRate,
                  "Platziert unbemerkt eine Bombe an einem Spieler. Nach kurzer Zeit bemerkt der Träger sie und kann sie per Berührung weitergeben – bis sie explodiert.",
                  "Secretly plants a bomb on a player. After a short delay the carrier notices it and can pass it on by touch – until it explodes."),
                E("Shade", Faction.Impostor, () => Palette.ImpostorRed, () => Shade.SpawnRate,
                  "Die Leichen seiner Opfer verschwinden. Andere finden sie nur, wenn sie nah genug an der Stelle vorbeilaufen.",
                  "Their victims' bodies vanish. Others only find them by walking close enough to the spot."),
                E("Manipulator", Faction.Impostor, () => Palette.ImpostorRed, () => Manipulator.SpawnRate,
                  "Lässt die Sicherheitssysteme lügen: Admin-Tisch und Vitals zeigen für eine Weile gefälschte, synchronisierte Daten.",
                  "Makes the ship's security lie: the admin table and vitals show fake, synced data for a while."),

                E("Siphoner", Faction.Crew, () => Siphoner.Color, () => Siphoner.SpawnRate,
                  "Saugt Impostorn in der Nähe Kill-Kraft ab – deren Kill-Cooldown steigt, solange der Siphoner nah dranbleibt.",
                  "Drains nearby Impostors' kill power – their kill cooldown keeps rising while the Siphoner stays close."),
                E("Witness", Faction.Crew, () => Witness.Color, () => Witness.SpawnRate,
                  "Wird er einziger Zeuge eines Kills, sieht er den Mörder rot markiert und kann ihn überführen.",
                  "When they are the sole witness of a kill, they see the killer marked red and can expose them."),
                E("Scout", Faction.Crew, () => Scout.Color, () => Scout.SpawnRate,
                  "Kann sich kurzzeitig fast unsichtbar und schneller machen; Licht-Sabotage schränkt ihn dabei nicht ein.",
                  "Can briefly turn near-invisible and faster; light sabotage doesn't affect them while scouting."),
                E("Beacon", Faction.Crew, () => Beacon.Color, () => Beacon.SpawnRate,
                  "Licht-Sabotage betrifft ihn nie – und Crewmates in seiner Nähe teilen seine volle Sicht.",
                  "Light sabotage never affects them – and nearby crewmates share their full vision."),

                E("Bug", Faction.Neutral, () => Bug.Color, () => Bug.SpawnRate,
                  "Gewinnt, indem er einfach bis zum Ende überlebt: Egal welches Team gewinnt, der Bug gewinnt mit.",
                  "Wins by simply surviving to the end: whichever team wins, the Bug wins with them."),
                E("Follower", Faction.Neutral, () => Follower.Color, () => Follower.SpawnRate,
                  "Übernimmt die Rolle des ersten Spielers, der stirbt – und spielt ab dann für dessen Team.",
                  "Takes over the role of the first player to die – and plays for that team from then on."),
                E("Copycat", Faction.Neutral, () => Copycat.Color, () => Copycat.SpawnRate,
                  "Kopiert Fähigkeiten, die er bei anderen beobachtet. Sammelt er genug und überlebt, gewinnt er mit dem Sieger-Team.",
                  "Copies abilities they witness. Collect enough and survive to win alongside the winning team."),
                E("Collector", Faction.Neutral, () => Collector.Color, () => Collector.SpawnRate,
                  "Jagt versteckte Relikte auf der Map. Sammelt er die nötige Anzahl, gewinnt er allein (je nach Option sofort oder am Rundenende).",
                  "Hunts hidden relics across the map. Collecting the required number wins the game alone (instantly or at the end, per option)."),

                E("Poltergeist", Faction.Ghost, () => Poltergeist.Color, () => Poltergeist.SpawnRate,
                  "Der erste Tote erhebt sich als Geist und spukt für sein Team weiter: Türen zuschlagen, Spieler verhexen, am Reaktor eine Geisterhand wirken oder sich kurz manifestieren.",
                  "The first player to die rises as a ghost and keeps haunting for their team: slam doors, hex players, work a ghost hand at the reactor or briefly manifest."),
            };
            return entries;
        }

        private static Entry E(string name, Faction f, Func<Color> color, Func<CustomOption> rate, string de, string en)
            => new Entry { name = name, faction = f, color = color, rate = rate, de = de, en = en };

        private static bool German() {
            try { return UnknownsCollectionPlugin.HelpMenuGerman == null || UnknownsCollectionPlugin.HelpMenuGerman.Value; }
            catch { return true; }
        }

        private static void SetGerman(bool value) {
            try { if (UnknownsCollectionPlugin.HelpMenuGerman != null) UnknownsCollectionPlugin.HelpMenuGerman.Value = value; }
            catch { }
        }

        private static string L(string de, string en) => German() ? de : en;

        private static string FactionHeader(Faction f) => f switch {
            Faction.Impostor => "IMPOSTOR",
            Faction.Crew => L("CREWMATE", "CREWMATE"),
            Faction.Neutral => "NEUTRAL",
            _ => L("GEIST", "GHOST"),
        };

        private static Color FactionColor(Faction f) => f switch {
            Faction.Impostor => Palette.ImpostorRed,
            Faction.Crew => new Color(0.55f, 0.85f, 1f),
            Faction.Neutral => new Color(0.75f, 0.75f, 0.78f),
            _ => new Color(0.72f, 0.55f, 1f),
        };

        // ---- UI state ----
        private const int SortBg = 500;
        private const int SortText = 501;

        private sealed class HitBox {
            public Transform anchor;   // world-space centre
            public float w, h;         // world-space extents
            public Action onClick;
        }

        private static GameObject button;          // the "?" top-right button
        private static TextMeshPro buttonText;
        private static GameObject panel;           // the open overview panel (null = closed)
        private static TextMeshPro detailTitle;
        private static TextMeshPro detailBody;
        private static readonly List<HitBox> hits = new List<HitBox>();
        private static Entry selected;

        private static Sprite whiteSprite;
        private static Sprite WhiteSprite() {
            if (whiteSprite != null) return whiteSprite;
            var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave;
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            whiteSprite.hideFlags |= HideFlags.HideAndDontSave;
            return whiteSprite;
        }

        // TMP factory: clone the kill-button cooldown text (same trick Helpers.showFlash uses) so we
        // inherit AU's font/material, then normalise it.
        private static TextMeshPro NewText(Transform parent, string text, float fontSize, Color color,
                                           TextAlignmentOptions alignment = TextAlignmentOptions.Left) {
            var template = HudManager.Instance.KillButton.cooldownTimerText;
            var tmp = UnityEngine.Object.Instantiate(template, parent);
            tmp.gameObject.SetActive(true);
            tmp.transform.localScale = Vector3.one;
            tmp.transform.localPosition = Vector3.zero;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.enableAutoSizing = false;
            tmp.enableWordWrapping = false;
            tmp.alignment = alignment;
            tmp.color = color;
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = SortText;
            return tmp;
        }

        private static SpriteRenderer NewRect(Transform parent, Vector3 localPos, Vector2 size, Color color) {
            var go = new GameObject("UCHelpRect");
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSprite();
            sr.color = color;
            sr.sortingOrder = SortBg;
            return sr;
        }

        // ====================================================================
        // "?" button (created once per HUD)
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    panel = null; // stale reference from the previous HUD (its objects died with it)
                    hits.Clear();
                    selected = null;

                    button = new GameObject("UCHelpButton");
                    button.layer = __instance.gameObject.layer;
                    button.transform.SetParent(__instance.transform, false);

                    // Subtle round backdrop (UCFx ring) + the "?" glyph.
                    var ring = new GameObject("ring");
                    ring.layer = button.layer;
                    ring.transform.SetParent(button.transform, false);
                    var rr = ring.AddComponent<SpriteRenderer>();
                    rr.sprite = UCFx.Ring;
                    rr.color = new Color(1f, 1f, 1f, 0.55f);
                    rr.sortingOrder = SortBg;
                    ring.transform.localScale = Vector3.one * 0.5f;

                    buttonText = NewText(button.transform, "?", 3.2f, new Color(1f, 1f, 1f, 0.85f),
                        TextAlignmentOptions.Center);
                    buttonText.transform.localPosition = new Vector3(0f, 0f, -0.1f);

                    // Anchor to the top-right edge, below the vanilla settings/mod buttons.
                    var ap = button.AddComponent<AspectPosition>();
                    ap.Alignment = AspectPosition.EdgeAlignments.RightTop;
                    ap.DistanceFromEdge = new Vector3(0.5f, 2.3f, -10f);
                    ap.AdjustPosition();
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCHelpMenu] button creation failed: {e}");
                }
            }
        }

        // ====================================================================
        // Panel build / teardown
        // ====================================================================
        private static void TogglePanel() {
            if (panel != null) ClosePanel();
            else OpenPanel();
        }

        private static void ClosePanel() {
            try { if (panel != null) UnityEngine.Object.Destroy(panel); } catch { }
            panel = null;
            hits.Clear();
            selected = null;
        }

        private static void OpenPanel() {
            try {
                var hud = HudManager.Instance;
                if (hud == null) return;
                ClosePanel();

                panel = new GameObject("UCHelpPanel");
                panel.layer = hud.gameObject.layer;
                panel.transform.SetParent(hud.transform, false);
                panel.transform.localPosition = new Vector3(0f, 0f, -30f);

                // Backdrop + frame line
                NewRect(panel.transform, Vector3.zero, new Vector2(9.6f, 5.3f), new Color(0.03f, 0.04f, 0.08f, 0.96f));
                NewRect(panel.transform, new Vector3(0f, 2.52f, -0.05f), new Vector2(9.6f, 0.04f), new Color(1f, 1f, 1f, 0.25f));
                NewRect(panel.transform, new Vector3(-0.05f, -0.14f, -0.05f), new Vector2(0.03f, 4.9f), new Color(1f, 1f, 1f, 0.12f));

                // Title + language toggle + close
                var title = NewText(panel.transform, L("Unknown's Collection – Mögliche Rollen", "Unknown's Collection – Possible Roles"),
                    1.9f, Color.white);
                title.transform.localPosition = new Vector3(-4.6f, 2.32f, -0.1f);

                var lang = NewText(panel.transform, German() ? "Sprache: DE  (klick: EN)" : "Language: EN  (click: DE)",
                    1.3f, new Color(1f, 0.82f, 0.35f), TextAlignmentOptions.Right);
                lang.transform.localPosition = new Vector3(3.05f, 2.32f, -0.1f);
                hits.Add(new HitBox { anchor = lang.transform, w = 2.6f, h = 0.35f, onClick = () => {
                    SetGerman(!German());
                    var reopen = selected;
                    OpenPanel();          // rebuild in the new language
                    Select(reopen);
                } });

                var close = NewText(panel.transform, "✕", 2.0f, new Color(1f, 0.45f, 0.45f), TextAlignmentOptions.Center);
                close.transform.localPosition = new Vector3(4.55f, 2.32f, -0.1f);
                hits.Add(new HitBox { anchor = close.transform, w = 0.5f, h = 0.5f, onClick = ClosePanel });

                // Role rows, grouped by faction: Impostor in the left column, everything else right.
                float rowH = 0.365f;
                float top = 1.95f;
                float colImp = -4.55f, colRest = -2.55f;

                float yImp = top, yRest = top;
                Faction? lastLeft = null, lastRight = null;
                bool any = false;

                foreach (var e in Entries()) {
                    var rate = e.rate?.Invoke();
                    if (rate == null || rate.getSelection() <= 0) continue; // only possibly-active roles
                    any = true;
                    bool left = e.faction == Faction.Impostor;
                    float x = left ? colImp : colRest;

                    ref float y = ref yImp;
                    if (!left) y = ref yRest;

                    var last = left ? lastLeft : lastRight;
                    if (last != e.faction) {
                        var header = NewText(panel.transform, FactionHeader(e.faction), 1.25f,
                            new Color(FactionColor(e.faction).r, FactionColor(e.faction).g, FactionColor(e.faction).b, 0.75f));
                        header.transform.localPosition = new Vector3(x, y, -0.1f);
                        y -= rowH * 0.85f;
                        if (left) lastLeft = e.faction; else lastRight = e.faction;
                    }

                    var row = NewText(panel.transform, e.name, 1.55f, e.color());
                    row.transform.localPosition = new Vector3(x + 0.15f, y, -0.1f);
                    var entry = e;
                    // Hit box is centred on the text anchor (left-aligned text -> shift right).
                    var box = new GameObject("rowbox");
                    box.layer = panel.layer;
                    box.transform.SetParent(panel.transform, false);
                    box.transform.localPosition = new Vector3(x + 0.95f, y, -0.1f);
                    hits.Add(new HitBox { anchor = box.transform, w = 1.9f, h = rowH * 0.95f, onClick = () => Select(entry) });
                    y -= rowH;
                }

                if (!any) {
                    var none = NewText(panel.transform,
                        L("Keine Unknown's-Collection-Rolle ist aktuell aktiviert (alle Raten auf 0).",
                          "No Unknown's Collection role is currently enabled (all rates at 0)."),
                        1.4f, new Color(1f, 1f, 1f, 0.7f));
                    none.transform.localPosition = new Vector3(-4.55f, top, -0.1f);
                }

                // Detail pane (right half)
                detailTitle = NewText(panel.transform, "", 1.8f, Color.white);
                detailTitle.transform.localPosition = new Vector3(0.25f, 1.95f, -0.1f);

                detailBody = NewText(panel.transform,
                    L("Klicke links eine Rolle an, um ihre Erklärung zu sehen.",
                      "Click a role on the left to see its explanation."),
                    1.45f, new Color(1f, 1f, 1f, 0.85f));
                detailBody.enableWordWrapping = true;
                detailBody.alignment = TextAlignmentOptions.TopLeft;
                detailBody.rectTransform.sizeDelta = new Vector2(4.35f, 4.0f);
                detailBody.transform.localPosition = new Vector3(2.45f, -0.45f, -0.1f);
            } catch (Exception e) {
                UnknownsCollectionPlugin.Logger?.LogError($"[UCHelpMenu] panel build failed: {e}");
                ClosePanel();
            }
        }

        private static void Select(Entry e) {
            try {
                selected = e;
                if (e == null || detailTitle == null || detailBody == null) return;
                detailTitle.text = e.name;
                detailTitle.color = e.color();
                detailBody.text = German() ? e.de : e.en;
            } catch { }
        }

        // ====================================================================
        // Per-frame: visibility gate + manual click resolution.
        // ====================================================================
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class HudUpdatePatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (button == null) return;

                    // Same visibility gate as CustomButton: lobby + rounds, not meetings/intro.
                    bool visible = __instance.UseButton != null && __instance.UseButton.isActiveAndEnabled
                                   && !MeetingHud.Instance && !ExileController.Instance;
                    if (button.activeSelf != visible) button.SetActive(visible);
                    if (!visible) { if (panel != null) ClosePanel(); return; }

                    if (panel != null && Input.GetKeyDown(KeyCode.Escape)) { ClosePanel(); return; }
                    if (!Input.GetMouseButtonDown(0)) return;

                    var cam = Camera.main;
                    if (cam == null) return;
                    Vector3 world = cam.ScreenToWorldPoint(Input.mousePosition);

                    // "?" button (round-ish, generous box)
                    Vector3 bp = button.transform.position;
                    if (Mathf.Abs(world.x - bp.x) < 0.35f && Mathf.Abs(world.y - bp.y) < 0.35f) {
                        TogglePanel();
                        return;
                    }

                    if (panel == null) return;
                    // Snapshot: a hit action (language toggle) may rebuild the list mid-iteration.
                    foreach (var h in new List<HitBox>(hits)) {
                        if (h?.anchor == null) continue;
                        Vector3 c = h.anchor.position;
                        if (Mathf.Abs(world.x - c.x) < h.w / 2f && Mathf.Abs(world.y - c.y) < h.h / 2f) {
                            h.onClick?.Invoke();
                            return;
                        }
                    }
                } catch (Exception e) {
                    UnknownsCollectionPlugin.Logger?.LogError($"[UCHelpMenu] update failed: {e}");
                }
            }
        }
    }
}
