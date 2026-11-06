"""Einen Online-Hut fuer Unknown's Collection veroeffentlichen (UCOnlineHats, 05.11.2026): Bilder ins Asset-Repo, Liste anpassen.

Schreibt die Bilder als extra/extra_<key>_<rolle>_<sha8>.png nach ../UnknownsCollection-Assets/extra und traegt den Hut in
extra/hats.json ein (Format 1). Alte Bilder desselben Huts werden entfernt. Danach im Asset-Repo committen und auf main pushen:
jeder Spieler bekommt den Hut beim naechsten Lobby-Beitritt (oder sofort per /loadhats des Autors), ohne Mod-Update.

Aufruf: python tools/build_online_hat.py <key> "<Name>" <main.png> [--back b.png] [--climb c.png] [--flip f.png] [--backflip bf.png]
        [--behind] [--bounce] [--adaptive] [--author NAME] [--min-version 1.4.1] [--repo PFAD]
        python tools/build_online_hat.py --remove <key>
"""
import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ROLES = ["main", "back", "climb", "flip", "backflip"]


def sha(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def load(extra):
    f = extra / "hats.json"
    return json.loads(f.read_text(encoding="utf-8")) if f.exists() else {"format": 1, "hats": []}


def save(extra, data):
    (extra / "hats.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def drop_files(extra, key):
    for p in extra.glob(f"extra_{key}_*.png"):
        p.unlink()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("key", nargs="?")
    ap.add_argument("name", nargs="?")
    ap.add_argument("main", nargs="?")
    for r in ROLES[1:]:
        ap.add_argument(f"--{r}")
    ap.add_argument("--behind", action="store_true")
    ap.add_argument("--bounce", action="store_true")
    ap.add_argument("--adaptive", action="store_true")
    ap.add_argument("--author", default="DaUnknown-0")
    ap.add_argument("--min-version")
    ap.add_argument("--remove")
    ap.add_argument("--repo", default=str(ROOT.parent / "UnknownsCollection-Assets"))
    a = ap.parse_args()

    extra = Path(a.repo) / "extra"
    extra.mkdir(parents=True, exist_ok=True)
    data = load(extra)
    if a.remove:
        data["hats"] = [h for h in data["hats"] if h["key"] != a.remove]
        drop_files(extra, a.remove)
        save(extra, data)
        print(f"{a.remove} entfernt")
        return
    if not (a.key and a.name and a.main):
        ap.error("key, name und main.png sind noetig")
    if not re.fullmatch(r"[a-z][a-z0-9]{1,23}", a.key):
        ap.error("key: nur a-z und 0-9, 2-24 Zeichen, Buchstabe vorne")
    if not (3 <= len(a.name) <= 40) or "<" in a.name:
        ap.error("Name: 3-40 Zeichen, kein <")

    drop_files(extra, a.key)
    files = {}
    for role in ROLES:
        src = a.main if role == "main" else getattr(a, role)
        if not src:
            continue
        h = sha(src)
        name = f"extra_{a.key}_{role}_{h[:8]}.png"
        shutil.copyfile(src, extra / name)
        files[role] = {"file": name, "size": Path(src).stat().st_size, "sha256": h}

    entry = {"key": a.key, "name": a.name, "author": a.author, "files": files,
             "behind": a.behind, "bounce": a.bounce, "adaptive": a.adaptive}
    if a.min_version:
        entry["minVersion"] = a.min_version
    hats = [h for h in data["hats"] if h["key"] != a.key and h["name"] != a.name]
    hats.append(entry)
    data = {"format": 1, "hats": hats}
    save(extra, data)
    print(f"{a.key} ({a.name}): {len(files)} Bild(er), {len(hats)} Hut/Huete in {extra / 'hats.json'}")


if __name__ == "__main__":
    main()
