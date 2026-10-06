import argparse, json, os, pathlib, shutil, subprocess, sys

from make_manifest import REPO, STAGE, binary_files, bundle_members, file_sha256, sha256

SLUG, TAG = "Glesooo/battlefield-modpack", "pack"
MANIFEST = "manifest.json"
BATCH = 20


def gh(*args, quiet=False):
    done = subprocess.run(["gh", *args, "--repo", SLUG], capture_output=quiet, text=True, encoding="utf-8")
    if done.returncode:
        sys.exit(f"gh {' '.join(args[:3])} failed: {(done.stderr or '').strip()}")
    return done.stdout


def pack():
    manifest = json.loads((REPO / MANIFEST).read_text(encoding="utf-8"))
    files, sources = binary_files()
    members = [{"path": path, "sha256": sha256(data), "mode": mode} for path, data, mode in bundle_members()]
    bundle = STAGE / manifest["bundle"]["asset"]
    fresh = (files == manifest["files"] and members == manifest["bundle"]["files"]
             and bundle.is_file() and file_sha256(bundle) == manifest["bundle"]["sha256"])
    if not fresh:
        sys.exit("the game folder changed since make_manifest.py ran: run it again and look at what changed")
    sources[bundle.name] = str(bundle)
    sizes = {entry["asset"]: entry["size"] for entry in manifest["files"] + [manifest["bundle"]]}
    return manifest["revision"], {asset: (pathlib.Path(path), sizes[asset]) for asset, path in sources.items()}


def stage(folder, assets):
    folder.mkdir(parents=True, exist_ok=True)
    staged = []
    for asset, (source, _) in assets.items():
        target = folder / asset
        target.unlink(missing_ok=True)
        try:
            os.link(source, target)
        except OSError:
            shutil.copy2(source, target)
        staged.append(target)
    return staged


def released():
    done = subprocess.run(["gh", "release", "view", TAG, "--repo", SLUG, "--json", "assets"],
                          capture_output=True, text=True, encoding="utf-8")
    if done.returncode:
        if "release not found" in done.stderr:
            return None
        sys.exit(f"gh release view failed: {done.stderr.strip()}")
    return {a["name"]: a["size"] for a in json.loads(done.stdout)["assets"] if a.get("state", "uploaded") == "uploaded"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stage", type=pathlib.Path)
    ap.add_argument("--publish", action="store_true")
    ap.add_argument("--prune", action="store_true")
    args = ap.parse_args()

    revision, assets = pack()
    if args.stage:
        stage(args.stage, assets)
        shutil.copy2(REPO / MANIFEST, args.stage / MANIFEST)
        print(f"revision {revision}: {len(assets)} files + {MANIFEST} -> {args.stage}")
        return

    online = released()
    known = online or {}
    missing = {asset: entry for asset, entry in assets.items() if known.get(asset) != entry[1]}
    obsolete = sorted(set(known) - set(assets) - {MANIFEST})
    size = sum(entry[1] for entry in missing.values()) / 2 ** 20
    print(f"revision {revision}, release {TAG} {'exists' if online is not None else 'does not exist yet'}: "
          f"to upload {len(missing)} files ({size:.1f} MB), already there {len(assets) - len(missing)}, "
          f"no longer needed {len(obsolete)}")
    if not args.publish:
        print("dry run, nothing changed; --publish uploads, --prune also deletes files that are no longer needed")
        return

    if online is None:
        gh("release", "create", TAG, "--title", "M.A.C.E pack", "--latest=false",
           "--notes", "Files of the M.A.C.E pack. The launcher downloads them by manifest.json.")
    staged = stage(STAGE / "upload", missing)
    for start in range(0, len(staged), BATCH):
        gh("release", "upload", TAG, *map(str, staged[start:start + BATCH]), "--clobber")
        print(f"uploaded {min(start + BATCH, len(staged))} of {len(staged)}")
    online = released() or {}
    absent = [asset for asset, entry in assets.items() if online.get(asset) != entry[1]]
    if absent:
        sys.exit(f"not in the release after upload, {MANIFEST} left untouched: {', '.join(absent)}")
    gh("release", "upload", TAG, str(REPO / MANIFEST), "--clobber")
    if args.prune:
        for name in obsolete:
            gh("release", "delete-asset", TAG, name, "--yes", quiet=True)
    shutil.rmtree(STAGE / "upload", ignore_errors=True)
    print(f"revision {revision} is published")


if __name__ == "__main__":
    main()
