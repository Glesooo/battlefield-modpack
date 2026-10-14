import hashlib, json, os, pathlib, re, shutil, subprocess, sys

HERE = pathlib.Path(__file__).resolve().parent
PROJECT = HERE.parent / "MaceLauncher"
TEST = HERE.parents[2] / "LauncherTest"
ROOT, SOURCE = TEST / "root", TEST / "source"
WORK, NEWER, OFFER = TEST / "restart", TEST / "restart_newer", TEST / "source_restart"
EXE = pathlib.Path(os.environ.get("MACE_EXE") or PROJECT / "bin/Release/net8.0-windows/win-x64/publish/MACE.exe")

current = re.search(r"<Version>(\d+)\.(\d+)\.(\d+)</Version>", (PROJECT / "MaceLauncher.csproj").read_text()).groups()
newer = f"{current[0]}.{current[1]}.{int(current[2]) + 1}"
for folder in (WORK, NEWER, OFFER):
    shutil.rmtree(folder, ignore_errors=True)
subprocess.run(["dotnet", "publish", str(PROJECT), "-c", "Release", "--nologo", "-v", "q", f"-p:Version={newer}",
                "-o", str(NEWER)], check=True)
built = NEWER / "MACE.exe"
digest = hashlib.sha256(built.read_bytes()).hexdigest()
asset = digest[:16] + "-MACE.exe"
OFFER.mkdir()
shutil.copy2(built, OFFER / asset)
manifest = json.loads((SOURCE / "manifest.json").read_text(encoding="utf-8"))
release = {"version": newer, "size": built.stat().st_size, "sha256": digest, "asset": asset}
(OFFER / "manifest.json").write_text(json.dumps(dict(manifest, launcher=release, sources=[])), encoding="utf-8")
WORK.mkdir()
shutil.copy2(EXE, WORK / "MACE.exe")

report_file = TEST / "restart_report.json"
report_file.unlink(missing_ok=True)
subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(HERE / "restart_check.ps1"),
                str(EXE), str(built), str(ROOT), str(OFFER), str(WORK), str(report_file)])
report = json.loads(report_file.read_text(encoding="utf-8")) if report_file.exists() else {}
checks = {
    "the old launcher closes after updating itself": report.get("firstExited") is True,
    "its file is replaced by the newer build": report.get("replaced") is True,
    "the newer build starts by itself": report.get("restarted") is True,
    f"it shows v{newer} and does not update again": report.get("version") == f"v{newer}"
                                                     and report.get("status") == "Готов к запуску",
    "the newer build deletes the old file": report.get("oldRemoved") is True,
}
for name, ok in checks.items():
    print(("OK   " if ok else "FAIL ") + name)
print(f"\n{sum(checks.values())} of {len(checks)} passed" + ("" if all(checks.values()) else f"; report: {report}"))
for folder in (WORK, NEWER, OFFER):
    shutil.rmtree(folder, ignore_errors=True)
sys.exit(0 if all(checks.values()) else 1)
