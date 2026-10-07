import argparse, fnmatch, hashlib, json, os, pathlib, re, sys, zipfile

REPO = pathlib.Path(__file__).resolve().parent.parent
WORK = REPO.parent
INSTANCE = pathlib.Path(os.path.expandvars(r"%APPDATA%/PrismLauncher/instances/M.A.C.E/minecraft"))
STAGE = WORK / "PackStage"

PACK = {
    "format": 1,
    "name": "M.A.C.E",
    "minecraft": "1.20.1",
    "forge": "47.4.10",
    "java": 17,
    "sources": ["https://github.com/Glesooo/battlefield-modpack/releases/download/pack/"],
    "folders": {"mods": "strict", "resourcepacks": "strict", "tacz": "strict_zip", "shaderpacks": "additive"},
}

BINARY_FOLDERS = {"mods": "*.jar", "tacz": "*.zip", "resourcepacks": "*.zip", "shaderpacks": "*.zip"}

CONFIG_EXCLUDED = [
    "battlefieldcore-admin.toml", "battlefieldcore-auth.toml", "battlefieldhud-client.toml", "embeddium-options.json",
    "embeddium-fingerprint.json", "oculus.properties", "fml.toml", "e4mc/*",
]
CONFIG_MANAGED_CLIENT = ["jumpcooldown-client.toml", "foliagevision-client.toml", "casingsounds-client.toml"]
CONFIG_FIRST_INSTALL = [
    "*-client.toml", "*-client.json", "*-client.yaml", "viewmodel.json", "cameraoverhaul.toml", "autohud.json5",
    "invnaut_inventory.json", "chatanimation.json", "notenoughanimations.json", "sound_physics_remastered/*",
    "yacl.json5", "MouseTweaks.cfg", "packetfixer.properties", "invmove/unrecognized.json",
]

OPTION_KEYS = {
    "version", "autoJump", "invertYMouse", "mouseSensitivity", "mouseWheelSensitivity", "rawMouseInput",
    "discrete_mouse_scroll", "toggleCrouch", "toggleSprint", "lang", "resourcePacks", "incompatibleResourcePacks",
}
OPTION_OVERRIDES = {
    "tutorialStep": "none", "skipMultiplayerWarning": "true", "joinedFirstServer": "true",
    "onboardAccessibility": "false",
}
ZIP_DATE = (2020, 1, 1, 0, 0, 0)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def file_sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def matches(rel, patterns):
    return any(fnmatch.fnmatch(rel, p) for p in patterns)


def asset_name(digest, name):
    return digest[:16] + "-" + re.sub(r"[^A-Za-z0-9._-]", "_", name)


def binary_files():
    entries, sources = [], {}
    for folder, pattern in BINARY_FOLDERS.items():
        found = sorted((INSTANCE / folder).glob(pattern), key=lambda p: p.name.lower())
        if not found:
            sys.exit(f"{INSTANCE / folder}: no {pattern} files - refusing to build an empty pack")
        for path in found:
            digest = file_sha256(path)
            asset = asset_name(digest, path.name)
            entries.append({"path": f"{folder}/{path.name}", "size": path.stat().st_size, "sha256": digest,
                            "asset": asset})
            sources[asset] = str(path)
    return entries, sources


def default_options():
    kept = []
    for line in (INSTANCE / "options.txt").read_text(encoding="utf-8").splitlines():
        key = line.split(":", 1)[0]
        if key in OPTION_OVERRIDES:
            continue
        if key in OPTION_KEYS or key.startswith("key_"):
            kept.append(line)
    kept += [f"{k}:{v}" for k, v in OPTION_OVERRIDES.items()]
    return ("\n".join(kept) + "\n").encode("utf-8")


def config_mode(rel):
    if matches(rel, CONFIG_EXCLUDED):
        return None
    if matches(rel, CONFIG_MANAGED_CLIENT):
        return "managed"
    return "first_install" if matches(rel, CONFIG_FIRST_INSTALL) else "managed"


def bundle_members():
    members = []
    config = INSTANCE / "config"
    for path in sorted(p for p in config.rglob("*") if p.is_file()):
        rel = path.relative_to(config).as_posix()
        mode = config_mode(rel)
        if mode:
            members.append(("config/" + rel, path.read_bytes(), mode))
    members.append(("options.txt", default_options(), "first_install"))
    return members


def write_bundle(members):
    STAGE.mkdir(exist_ok=True)
    temp = STAGE / "base.zip"
    with zipfile.ZipFile(temp, "w", zipfile.ZIP_DEFLATED) as z:
        for path, data, _ in members:
            z.writestr(zipfile.ZipInfo(path, ZIP_DATE), data, zipfile.ZIP_DEFLATED)
    digest = file_sha256(temp)
    asset = asset_name(digest, "base.zip")
    for stale in STAGE.glob("*-base.zip"):
        stale.unlink()
    final = STAGE / asset
    temp.rename(final)
    return final, {"asset": asset, "size": final.stat().st_size, "sha256": digest,
                   "files": [{"path": p, "sha256": sha256(d), "mode": m} for p, d, m in members]}


def verify(manifest, sources, bundle_path):
    for entry in manifest["files"]:
        source = pathlib.Path(sources[entry["asset"]])
        assert source.stat().st_size == entry["size"], entry["path"]
    assert len({e["asset"] for e in manifest["files"]}) == len(manifest["files"]), "asset name collision"
    with zipfile.ZipFile(bundle_path) as z:
        for entry in manifest["bundle"]["files"]:
            assert sha256(z.read(entry["path"])) == entry["sha256"], entry["path"]
    shipped = {e["path"] for e in manifest["bundle"]["files"]}
    assert "config/battlefieldcore-admin.toml" not in shipped, "admin password must never ship"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", type=pathlib.Path, default=REPO / "manifest.json")
    args = ap.parse_args()

    files, sources = binary_files()
    bundle_path, bundle = write_bundle(bundle_members())
    sources[bundle["asset"]] = str(bundle_path)
    body = {**PACK, "files": files, "bundle": bundle}
    revision = sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode("utf-8"))[:12]
    manifest = {"revision": revision, **body}
    verify(manifest, sources, bundle_path)

    args.out.write_text(json.dumps(manifest, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")

    by_folder = {}
    for entry in files:
        folder = entry["path"].split("/")[0]
        count, size = by_folder.get(folder, (0, 0))
        by_folder[folder] = (count + 1, size + entry["size"])
    for folder, (count, size) in by_folder.items():
        print(f"{folder:14} {count:4} files {size / 2 ** 20:8.1f} MB")
    modes = [e["mode"] for e in bundle["files"]]
    print(f"{'bundle':14} {len(modes):4} files {bundle['size'] / 2 ** 20:8.1f} MB "
          f"(managed {modes.count('managed')}, first install {modes.count('first_install')})")
    print(f"revision {revision} -> {args.out}")


if __name__ == "__main__":
    main()
