# Unknown's Collection

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin that adds **17 custom roles** to
[The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (TOR) for *Among Us*.

Each role is layered on top of TOR purely through Harmony patches — **the original TOR source is never
modified**. The plugin only takes a hard dependency on TheOtherRoles.

> The roles are client-side, so the lobby can only start when **every player runs the same Unknown's
> Collection version** (the host gets a warning otherwise). All Impostor roles are also pickable in TOR's
> **Role Draft**.

---

## Roles

### Impostor

- **The Tesla** — Charges exactly two players during a meeting (one **+**, one **−**). While the charged
  pair stays too close, a hidden countdown drains; separating pauses it, a meeting refills it. At zero,
  both die. Victims see a `⚡ charged` / pulsing `⚡ danger` warning but never the exact timer.
  *Options:* trigger distance, countdown seconds, min alive players, can-charge-self, self-charge-kills,
  grace after meeting.
- **The Saboteur** — Once per round: sabotage a task console (lethal when the victim completes it, with a
  crew search/defuse counterplay) or lay an invisible stun trap. *Options:* tokens per round, task/trap
  costs, extra kill cooldown, trap count/stun/limp, crew search & defuse, min-alive gates.
- **The Silencer** — Marks a victim with the SILENCE button; the marked player is **muted** in the next
  meeting (no vote, no chat) and shown a red `[MUTED]` tag in-game and on their vote area so everyone can
  also mute their voice client. *Options:* mark cooldown, targets per round, muted-can-still-skip,
  show-in-game-marker.
- **The Poisoner** — Kills poison the victim's body; the next player to **report** a poisoned body becomes
  poisoned and dies after **X meetings** unless cured by the **Medic's Antidote**. The doomed reporter
  gets a private "you don't feel so good" message in the meeting. *Options:* poison death after meetings
  (min 2), Medic antidote uses per round, max poisoned bodies per round.
- **The Illusionist** — Records a movement path and replays it as an **unkillable, shielded clone** that
  fools would-be killers. *Options:* max recording length, playback cooldown, blocked-kill-costs-cooldown,
  clone-shield-visible-to-everyone.
- **The Maniac** — Plants a bomb on a player; the bomb can be **passed** to a nearby player before it
  detonates, killing whoever holds it at the end. *Options:* bomb cooldown, unaware delay, pass window,
  explosion range.
- **The Shade** — Kills make the victim's body **vanish**; others find it by walking close, then it
  auto-reports. Bait victims stay visible so their own report still exposes the killer. *Options:* find
  distance.
- **The Manipulator** — Makes the ship's **security devices lie** for a while, for everyone: the admin
  table shows a fabricated (but synced and believable) player distribution, and vitals shows dead
  players as alive. Works with whatever devices the map has (Skeld/Mira: admin; Fungle: vitals;
  Polus/Airship: both); comms sabotage keeps its normal "signal lost" look. *Options:* cooldown,
  duration, fake admin, fake vitals.

### Crewmate

- **The Siphoner** — Toggles a DRAIN aura: a nearby Impostor's kill cooldown is held at full (and, for a
  while after, a lingering deficit) so they can't kill; optionally also holds the sabotage cooldown.
  *Options:* drain range, penalty per tick, tick interval, scale-with-distance, warn drained impostor,
  also-drain-sabotage + block seconds, drain cooldown.
- **The Witness** — If the Witness is the **sole living crewmate who sees a kill** (in range + clear line
  of sight), the killer is noted: their name glows red for the Witness; if the Witness dies and their body
  is reported, everyone sees the note; if the Witness survives to the meeting, anonymous notes go to a few
  random players. *Options:* sight range factor, red name permanent, note recipients.
- **The Scout** — Activatable ability: go **transparent and fast**, and lights/sabotage don't reduce
  vision while it's active. *Options:* ability duration, cooldown, speed multiplier, transparency.
- **The Beacon** — Lights never reduce the Beacon's vision, and nearby crewmates **share the Beacon's full
  vision**. *Options:* share radius, not-guessable.

### Neutral

- **The Bug** — **Survive to the end to win alone**: when a **team** win (Crewmate, Impostor or Jackal)
  triggers while the Bug is alive, the Bug hijacks it. Neutral solo wins (Jester, Arsonist, Vulture, …)
  are left alone. *Options:* min players; optional glitchy win-screen effects.
- **The Follower** — Takes over the **full role of the first player to die** (team, ability and win
  condition — including Impostor/Neutral). *Options:* min players.
- **The Collector** — The host scatters **relics** across the map (anchored near task consoles, on
  every map). Only the Collector sees them; impostors can optionally **sense** a faint shimmer nearby.
  Collecting takes a few seconds of **channeling** (moving cancels; nearby players hear a quiet
  glitter). Enough relics win — instantly or, per option, only if the Collector also survives to the
  end (then it hijacks the next team win, like the Bug). *Options:* relics spawned/needed, channel
  duration, win mode, impostor sense + radius, has tasks.
- **The Copycat** — **Learns abilities by witnessing them** — Camouflage, Morph, Shield (unkillable),
  Shoot (Sheriff-style, backfires on Crew) and Vent (vent access). Each learned ability is a button; the
  Copycat wins **with the winning team** if alive and it used enough abilities. *Options:* max stored
  abilities, has tasks, abilities needed to win.

### Ghost

- **The Poltergeist** — not a start role: the **first player to die** (kills always; exile too, per
  option) rises as the Poltergeist and **keeps its original team** — a dead Crewmate haunts for the
  crew, a dead Impostor for the impostors. Abilities share an **energy pool** (regenerates over time,
  never in meetings): **Door Haunt** slams a single door shut; **Hex** curses the nearest living player
  (speed boost / blindness / night vision); **Ghost Hand** counts as one hand on a Reactor/Seismic
  console while channeling; **Manifest** appears as a copy of a nearby living player (can vent, per
  option) — killing the manifest poofs it without a body and refunds the killer's cooldown per option.
  *Options:* exile counts, energy max/regen/start, per-ability costs & durations, hex effect toggles,
  manifest vent & kill refund, keeps tasks.

Every role also has the two standard options: a **spawn chance** and a **minimum lobby size to spawn**.

---

## Installation

1. Install [BepInEx (IL2CPP)](https://github.com/BepInEx/BepInEx) and
   [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles).
2. Drop `UnknownsCollection.dll` into `Among Us/BepInEx/plugins/` (next to `TheOtherRoles.dll`).
3. Launch the game. Every player who should see the roles needs the mod (same version).

---

## Versioning

| Form | Meaning |
|---|---|
| `vX.Y.Z` | Stable release (published as GitHub *latest*). |
| `vX.Y.Z.W` | Test build — the 4th component `W` is the test number (published as a GitHub *pre-release*). |

The version line in-game shows `vX.Y.Z` for stable builds and `vX.Y.Z.W` for test builds. The `.W`
suffix is only shown while the shared **"Test Versions"** toggle (top-right of the Mod Manager) is
**on** — it is **off by default**, so normal players see a clean `vX.Y.Z`.

A stable `vX.Y.Z` always **supersedes** its own test builds `vX.Y.Z.W` (semantic ordering).

## Mod Manager & self-updater

When *Useful TOR Stuff* (the Mod Manager owner) is installed, Unknown's Collection registers itself in
the Mod Manager and exposes a **channel-aware self-updater**:

- **Test Versions OFF** → updates to / stays on the newest **stable** release.
- **Test Versions ON** → updates to the newest **test build**, but only when it is genuinely ahead of
  the latest stable.
- Toggling the switch can download/install the matching channel build (with a confirmation prompt); it
  only downloads when the target is actually a different version.

The updater only ever talks to the GitHub Releases API (subject to GitHub's unauthenticated
60 requests/hour limit); gameplay never does.

---

## Building from source

Requires the .NET 6 SDK. The project references `TheOtherRoles.dll`:

```bash
dotnet build -c Release -p:TheOtherRolesDll=/path/to/TheOtherRoles.dll
```

`nuget.config` adds the BepInEx and Reactor package feeds. Releases are built automatically by the
GitHub Actions workflow on a `vX.Y.Z` / `vX.Y.Z.W` tag.

---

## Credits & license

- Created by **DaUnknown-0**.
- Based on [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (GPL-3.0).
- Licensed under **GPL-3.0-or-later** — see [LICENSE](LICENSE).
