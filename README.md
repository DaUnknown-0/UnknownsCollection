# Unknown's Collection

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin that adds **custom roles** to
[The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (TOR) for *Among Us*.

Each role is layered on top of TOR purely through Harmony patches — **the original TOR source is never
modified**. The plugin only takes a hard dependency on TheOtherRoles.

> First role: **The Tesla** (Impostor).

---

## ⚡ The Tesla (Impostor)

A normal Impostor is secretly promoted to *The Tesla* at the start of the game. Instead of (or in
addition to) sneaking around for kills, the Tesla can **charge two players** and lethally pull them
together.

**How it works**

- During a **meeting**, the Tesla opens a Swapper-style selection and charges exactly **two** players:
  one **positive (+)**, one **negative (−)**. Never the same person twice, and never more than two
  charged players at once.
- While the charged pair stays **too close together**, a hidden **countdown** drains.
  - Moving apart **pauses** the countdown (it does **not** refill).
  - It only resets to full **in a meeting**.
- If the countdown hits **zero**, **both charged players die**.

**What the victims see** (client-side, no exact timer by design)

- A persistent **`⚡ charged`** indicator while they hold a charge.
- When they get within trigger distance of their partner: a **pulsing red `⚡ danger`** warning,
  electric **spark particles**, and a **warning sound** — but never the remaining seconds.

### Options (Impostor tab)

| Option | Default | Description |
|---|---|---|
| Tesla | Off | Spawn chance for the role. |
| Tesla Minimum Players To Spawn | 6 | The role won't be assigned below this lobby size. |
| Tesla Charge Trigger Distance | 1.5 | How close (world units) the pair must be to drain the countdown. |
| Tesla Charge Countdown (sec) | 5 | Time the pair can stay close before they die. |
| Tesla Minimum Alive Players For Charges | 4 | Below this many alive players, charges become harmless. |
| Tesla Can Charge Itself | Off | Allow the Tesla to pick its own row as one pole. |
| Self-Charge Also Kills The Tesla | On | If self-charged and triggered, whether the Tesla dies too. |

> The Tesla is a client-side role: the lobby can only be started when **all players run the same
> Unknown's Collection version** (host gets a warning otherwise).

---

## Installation

1. Install [BepInEx (IL2CPP)](https://github.com/BepInEx/BepInEx) and
   [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles).
2. Drop `UnknownsCollection.dll` into `Among Us/BepInEx/plugins/` (next to `TheOtherRoles.dll`).
3. Launch the game. Every player who should see the role needs the mod (same version).

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
