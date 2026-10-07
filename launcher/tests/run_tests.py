import hashlib, http.server, json, os, pathlib, shutil, stat, subprocess, sys, threading, time

HERE = pathlib.Path(__file__).resolve().parent
TEST = HERE.parents[2] / "LauncherTest"
ROOT, SOURCE = TEST / "root", TEST / "source"
INST = ROOT / "instance"
SETTINGS = ROOT / "launcher.json"
SECRET = ROOT / "secret.bin"
KEY_LINE = "ключ ника: "
PUBLIC_KEY = ("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwji7gAhb8RbKDbkDPHFNq/ZcQz0NgdLx2GKyM17uvzeAOuE/JQfVmq0FhZrS5mbcPZRxcDJj"
              "b2z6Qxd9mdVdLw==")
PRIVATE_KEY_START = "MIGHAgEAMBMG"
EXE = pathlib.Path(os.environ.get("MACE_EXE") or HERE.parent / "MaceLauncher/bin/Debug/net8.0-windows/win-x64/MACE.exe")
PART = ".mace-part"
SMALL, LARGE, LOCKED = "mods/BetterDeath-1.1.3.jar", "mods/tacz-1.20.1-1.1.8-hotfix.jar", "mods/disablef5-1.2.jar"
CONFIG = "config/tacz-common.toml"

manifest = json.loads((SOURCE / "manifest.json").read_text(encoding="utf-8"))
expected = {f["path"]: f["sha256"] for f in manifest["files"]}
assets = {f["path"]: f["asset"] for f in manifest["files"]}
configs = {f["path"]: f["sha256"] for f in manifest["bundle"]["files"]}
CHUNK = (SOURCE / assets[LARGE]).stat().st_size // 4
results, requests = [], []
last_log = ""


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def intact(path):
    return sha(INST / path) == expected[path]


def run(*args, sources=(SOURCE,)):
    global last_log
    command = [str(EXE), "--headless", "--dir", str(ROOT)]
    for source in sources:
        command += ["--source", str(source)]
    code = subprocess.run(command + list(args)).returncode
    last_log = (ROOT / "launcher.log").read_text(encoding="utf-8")
    return code, last_log


def check(name, ok):
    results.append((name, bool(ok)))
    print(("OK   " if ok else "FAIL ") + name)
    if not ok:
        lines = last_log.splitlines()
        shown = [line for line in lines[:-8] if "ОШИБКА" in line] + lines[-8:]
        print("\n".join("       " + line[:240] for line in shown))


def removed_entries():
    base = ROOT / "removed"
    return {p.relative_to(base).as_posix() for p in base.glob("*/*/*")}


def edit_config():
    os.chmod(INST / CONFIG, stat.S_IWRITE)
    with open(INST / CONFIG, "a", encoding="utf-8") as f:
        f.write("\nplayer edit\n")


def unchanged():
    code, log = run("--sync")
    check("exit 0, nothing downloaded", code == 0 and "скачано 0 " in log and "конфигов 0" in log)


def tampering():
    before = removed_entries()
    shutil.copy2(INST / LOCKED, INST / "mods/cheat.jar")
    (INST / "resourcepacks/MyPack").mkdir(exist_ok=True)
    (INST / "resourcepacks/MyPack/pack.mcmeta").write_text("{}", encoding="utf-8")
    (INST / "tacz/note.txt").write_text("player note", encoding="utf-8")
    (INST / SMALL).unlink()
    data = (INST / LARGE).read_bytes()
    (INST / LARGE).unlink()
    (INST / (LARGE + PART)).write_bytes(data[:2 * CHUNK])
    (INST / ("mods/gone-1.0.jar" + PART)).write_bytes(b"orphan")
    edit_config()
    os.chmod(INST / CONFIG, stat.S_IREAD)
    with open(INST / "options.txt", "a", encoding="utf-8") as f:
        f.write("fov:1.0\n")
    options = sha(INST / "options.txt")
    code, log = run("--sync")
    moved = {entry.split("/", 1)[1] for entry in removed_entries() - before}
    check("exit 0", code == 0)
    check("foreign mod and resource pack moved to removed", moved == {"mods/cheat.jar", "resourcepacks/MyPack"}
          and not (INST / "mods/cheat.jar").exists() and not (INST / "resourcepacks/MyPack").exists())
    check("tacz/note.txt kept", (INST / "tacz/note.txt").exists())
    check("deleted mod restored", intact(SMALL))
    check("interrupted download resumed and correct", intact(LARGE))
    check("no part files left, orphan part deleted", not list(INST.rglob("*" + PART)))
    check("read-only managed config restored", sha(INST / CONFIG) == configs[CONFIG])
    check("options.txt kept", sha(INST / "options.txt") == options)
    check("summary: 2 downloaded, 2 moved, 1 config", "скачано 2 " in log and "убрано 2," in log and "конфигов 1" in log)


def update():
    folder = TEST / "source_update"
    folder.mkdir(exist_ok=True)
    smaller = dict(manifest, files=[f for f in manifest["files"] if f["path"] != SMALL])
    (folder / "manifest.json").write_text(json.dumps(smaller, ensure_ascii=False), encoding="utf-8")
    before = removed_entries()
    code, log = run("--sync", sources=(folder,))
    check("old pack file deleted silently", code == 0 and not (INST / SMALL).exists() and removed_entries() == before
          and "убрано 0," in log and f"удалён файл прошлой версии сборки {SMALL}" in log)
    code, log = run("--sync")
    check("mod comes back with the full pack", code == 0 and intact(SMALL))


def busy():
    target = INST / LOCKED
    part = target.with_name(target.name + PART)
    with open(target, "ab") as f:
        f.write(b"x")
    hold = "import sys,time; f=open(sys.argv[1],'rb'); print('locked', flush=True); time.sleep(120)"
    locker = subprocess.Popen([sys.executable, "-c", hold, str(target)], stdout=subprocess.PIPE, text=True)
    locker.stdout.readline()
    code, log = run("--sync")
    check("busy file: exit 1, the message names the program holding it", code == 1
          and f"Файл {target.name} занят программой python" in log)
    check("verified download kept for the next run", part.exists() and sha(part) == expected[LOCKED])
    locker.kill()
    locker.wait()
    code, log = run("--sync")
    check("after the lock is gone the file is replaced", code == 0 and intact(LOCKED) and not part.exists())


def fallback():
    folder = TEST / "source_bad"
    folder.mkdir(exist_ok=True)
    (folder / "manifest.json").write_text("<html>blocked</html>", encoding="utf-8")
    code, log = run("--sync", sources=(folder, SOURCE))
    check("falls back to the next source", code == 0 and "source_bad" in log and "скачано 0 " in log)


def nick_key(log):
    return [line.split(KEY_LINE, 1)[1] for line in log.splitlines() if KEY_LINE in line]


def launch():
    SETTINGS.unlink(missing_ok=True)
    SECRET.unlink(missing_ok=True)
    code, log = run("--selftest")
    check("self-test", code == 0 and "selftest: ok" in log)
    code, log = run("--print-launch", "--nick", "Gleso")
    check("launch command built", code == 0 and "--username Gleso" in log and "--uuid 9cfa60c82fc135eeb837cb98cd12c0d3" in log
          and "-Xmx4000m" in log and "-Xss1024k" in log and nick_key(log) == ["нет"])
    code, log = run("--print-launch", "--nick", "gleso", "--password", "correct horse")
    check("password becomes the nick key; password and private key stay out of the log", code == 0
          and nick_key(log) == [PUBLIC_KEY] and "correct horse" not in log and PRIVATE_KEY_START not in log)
    code, log = run("--print-launch", "--nick", "Gleso", "--password", "12345")
    check("short password is refused", code == 1 and "Пароль слишком короткий" in log)


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_GET(self):
        mode, _, name = self.path.lstrip("/").partition("/")
        asset = name != "manifest.json"
        file = SOURCE / name
        header = self.headers.get("Range") if mode != "norange" else None
        data = file.read_bytes() if file.is_file() and not (asset and mode == "missing") else None
        if not asset and mode == "lonely":
            data = json.dumps(dict(manifest, sources=[]), ensure_ascii=False).encode("utf-8")
        start = int(header.split("=")[1].split("-")[0]) if header else 0
        status = 404 if data is None else 416 if header and start >= len(data) else 206 if header else 200
        requests.append((mode, name, self.headers.get("Range"), status, self.headers.get("User-Agent")))
        damaged = asset and mode in ("corrupt", "lonely")
        body = b"" if status in (404, 416) else bytes(len(data) - start) if damaged else data[start:]
        self.send_response(status)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if not asset or mode not in ("cut", "stall") or len(body) <= CHUNK:
            self.wfile.write(body)
            return
        self.wfile.write(body[:CHUNK])
        self.wfile.flush()
        self.close_connection = True
        if mode == "stall":
            time.sleep(45)


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        pass


def web():
    server = Server(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    content = (INST / LARGE).read_bytes()

    def at(*modes):
        return tuple(f"http://127.0.0.1:{server.server_port}/{mode}" for mode in modes)

    def asked(mode, path):
        return [(header, status) for m, name, header, status, _ in requests if m == mode and name == assets[path]]

    def fresh(path):
        requests.clear()
        (INST / path).unlink()

    fresh(SMALL)
    (INST / LARGE).unlink()
    (INST / (LARGE + PART)).write_bytes(content[:3 * CHUNK])
    for old in (ROOT / "cache").glob("*-base.zip"):
        old.unlink()
    edit_config()
    code, log = run("--sync", sources=at("good"))
    check("download, resume and config archive", code == 0 and intact(SMALL) and intact(LARGE)
          and sha(INST / CONFIG) == configs[CONFIG] and asked("good", SMALL) == [(None, 200)]
          and asked("good", LARGE) == [(f"bytes={3 * CHUNK}-", 206)]
          and ("good", manifest["bundle"]["asset"], None, 200) in {r[:4] for r in requests})
    check("launcher names itself in requests", {agent for *_, agent in requests} == {"MACE-Launcher/0.1.0"})

    fresh(LARGE)
    code, log = run("--sync", sources=at("cut", "good"))
    check("dropped connection: resumed on the same and then on the next address", code == 0 and intact(LARGE)
          and asked("cut", LARGE) == [(None, 200), (f"bytes={CHUNK}-", 206)]
          and asked("good", LARGE) == [(f"bytes={2 * CHUNK}-", 206)])

    fresh(LARGE)
    (INST / (LARGE + PART)).write_bytes(content[:CHUNK])
    code, log = run("--sync", sources=at("norange"))
    check("server without resume support: file fetched from the start", code == 0 and intact(LARGE)
          and asked("norange", LARGE) == [(f"bytes={CHUNK}-", 200)])

    fresh(SMALL)
    code, log = run("--sync", sources=at("corrupt", "good"))
    check("damaged file from the first address is rejected", code == 0 and intact(SMALL)
          and asked("corrupt", SMALL) == [(None, 200)] * 2 and "контрольная сумма не совпала" in log)

    requests.clear()
    os.replace(INST / SMALL, INST / (SMALL + PART))
    code, log = run("--sync", sources=at("good"))
    check("finished download is not fetched twice", code == 0 and intact(SMALL)
          and [status for _, status in asked("good", SMALL)] == [416])

    size = (INST / SMALL).stat().st_size
    fresh(SMALL)
    (INST / (SMALL + PART)).write_bytes(bytes(size + 100))
    code, log = run("--sync", sources=at("good"))
    check("garbage part is thrown away", code == 0 and intact(SMALL)
          and [status for _, status in asked("good", SMALL)] == [416, 200])

    fresh(SMALL)
    code, log = run("--sync", sources=at("missing", "good"))
    check("missing file is not retried on the same address", code == 0 and intact(SMALL)
          and asked("missing", SMALL) == [(None, 404)] and asked("good", SMALL) == [(None, 200)])

    fresh(LARGE)
    started = time.monotonic()
    code, log = run("--sync", sources=at("stall", "good"))
    elapsed = time.monotonic() - started
    check(f"stalled connection is dropped ({elapsed:.0f} s)", code == 0 and intact(LARGE) and 55 < elapsed < 100
          and asked("stall", LARGE) == [(None, 200), (f"bytes={CHUNK}-", 206)]
          and asked("good", LARGE) == [(f"bytes={2 * CHUNK}-", 206)])

    fresh(SMALL)
    code, log = run("--sync", sources=at("lonely"))
    check("no address has the file: clear error naming the last address", code == 1
          and f"Не удалось скачать {assets[SMALL]} (последний адрес: {at('lonely')[0]}/)" in log)
    code, log = run("--sync", sources=("http://127.0.0.1:1",))
    check("no address answers: clear error", code == 1 and "Не удалось получить manifest.json ни с одного адреса" in log)
    code, log = run("--sync")
    check("everything back in place", code == 0 and intact(SMALL) and "скачано 1 " in log)
    code, log = run("--sync", "--verify")
    check("full verification finds nothing to fix", code == 0 and "скачано 0 " in log and "конфигов 0" in log)


def window():
    empty, report = TEST / "source_empty", TEST / "ui_report.json"
    empty.mkdir(exist_ok=True)
    report.unlink(missing_ok=True)
    SETTINGS.unlink(missing_ok=True)
    SECRET.unlink(missing_ok=True)
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(HERE / "ui_test.ps1"),
                    str(EXE), str(ROOT), str(SOURCE), str(empty), str(report), str(TEST / "settings.png")])
    ui = json.loads(report.read_text(encoding="utf-8")) if report.exists() else {}
    first_run = (ROOT / "launcher.old.log").read_text(encoding="utf-8")
    saved = json.loads(SETTINGS.read_text(encoding="utf-8")) if SETTINGS.exists() else {}
    stored = nick_key(run("--print-launch", "--nick", "tester_1")[1])
    typed = nick_key(run("--print-launch", "--nick", "Tester_1", "--password", "secret-pass")[1])
    code, log = run("--print-launch", "--nick", "Gleso")
    SETTINGS.unlink(missing_ok=True)
    SECRET.unlink(missing_ok=True)
    check("password: an empty one is refused before any work", str(ui.get("password")).startswith("Пароль:"))
    check("password: the box can show what was typed", ui.get("shown") == "secret-pass")
    check("password: after ИГРАТЬ the key is kept and the field is gone", ui.get("saved")
          and str(ui.get("afterPlay")).startswith("Не удалось получить manifest.json"))
    check("password: the kept key is the one the password gives, in any case of the nick",
          len(stored) == 1 and stored == typed and stored != ["нет"])
    check("password: the kept key is not used for another nick", nick_key(log) == ["нет"])
    check("password: it can be entered again", ui.get("reset"))
    check("settings: a wrong Java parameter is refused", str(ui.get("settingsError")).startswith("Каждый параметр Java"))
    check("settings: the defaults button restores the values", ui.get("defaultsShown"))
    check("settings are saved and used for the launch", ui.get("settingsClosed") and saved.get("MaxMemoryMb") == 3500
          and saved.get("FullScreen") is True and code == 0 and "-Xmx3500m" in log and "--fullscreen" in log
          and "-Dmace.test=1" in log)
    check("window opens ready", ui.get("ready") == "Готов к запуску")
    check("file check from the window", ui.get("busy") and ui.get("idle") and ui.get("verified") == "Файлы сборки в порядке")
    check("closing the window during work stops it and exits", ui.get("closedDuringWork")
          and first_run.count("сборка M.A.C.E") == 2 and first_run.count("скачать 0 файлов") == 1)
    check("unreachable source shows an error", str(ui.get("error")).startswith("Не удалось получить manifest.json"))
    check("short nick is refused before any work", str(ui.get("nick")).startswith("Ник:"))
    check("second copy refuses to start", ui.get("second") == "Лаунчер M.A.C.E уже запущен." and ui.get("secondExited")
          and ui.get("firstAlive") and ui.get("firstExited"))


SECTIONS = {
    "A": ("nothing changed", unchanged),
    "B": ("tampering", tampering),
    "C": ("pack update drops a mod", update),
    "D": ("file held open by another program", busy),
    "E": ("first source serves garbage", fallback),
    "F": ("self-test and launch command", launch),
    "G": ("downloads over HTTP", web),
    "H": ("window", window),
}

for key in [arg.upper() for arg in sys.argv[1:]] or SECTIONS:
    title, section = SECTIONS[key]
    print(f"== {key}. {title}")
    section()

failed = [name for name, ok in results if not ok]
print(f"\n{len(results) - len(failed)} of {len(results)} passed" + ("" if not failed else "; FAILED: " + "; ".join(failed)))
sys.exit(1 if failed else 0)
