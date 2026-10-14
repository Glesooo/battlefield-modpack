import _winapi, hashlib, json, os, pathlib, re, shutil, subprocess, sys, time, zipfile

HERE = pathlib.Path(__file__).resolve().parent
WORK = HERE.parents[2]
TEST = WORK / "LauncherTest"
SERVER, CLIENT, SOURCE = TEST / "auth_server", TEST / "auth", TEST / "source"
EXE = pathlib.Path(os.environ.get("MACE_EXE") or HERE.parent / "MaceLauncher/bin/Debug/net8.0-windows/win-x64/MACE.exe")
JAVA = TEST / "root/game/runtime/windows-x64/java-runtime-gamma/bin/java.exe"
FORGE = "libraries/net/minecraftforge/forge/1.20.1-47.4.26/win_args.txt"
BUILDS = WORK / "BattlefieldCore/build/libs"
NEW = max(BUILDS.glob("macecore-*.jar"), key=lambda jar: [int(n) for n in re.findall(r"\d+", jar.stem)])
OLD, NO_PACK_CHECK = BUILDS / "macecore-1.7.1.jar", BUILDS / "macecore-1.8.0.jar"
PORT = 25566
CONSOLE = SERVER / "console.log"
ACCOUNTS = SERVER / "config/mace-accounts.json"
AUTH = SERVER / "config/battlefieldcore-auth.toml"
PACK = SERVER / "config/battlefieldcore-pack.toml"
CACHE = SERVER / "config/mace-pack-manifest.json"
INSTANCE = CLIENT / "instance"
ALLOWED, FOREIGN, EXTRA_JAR = "allowed_pack.zip", "extra_pack.zip", "extra-test.jar"
SHOT = TEST / "auth_refused.png"
PROPERTIES = ["server-ip=127.0.0.1", f"server-port={PORT}", "online-mode=false", "level-type=minecraft\\:flat",
              "generate-structures=false", "max-players=4", "spawn-protection=0", "view-distance=4",
              "simulation-distance=4", "motd=MACE login check", ""]

results = []
server = None


def junction(link, target):
    if not link.exists():
        _winapi.CreateJunction(str(target), str(link))


def put_mod(folder, jar):
    mods = folder / "mods"
    mods.mkdir(parents=True, exist_ok=True)
    for old in mods.glob("macecore-*.jar"):
        old.unlink()
    shutil.copy2(jar, mods / jar.name)


def write_zip(path, name, text):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr(zipfile.ZipInfo(name, (2020, 1, 1, 0, 0, 0)), text)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def set_pack_mode(mode):
    PACK.write_text(f'[pack]\n\tmode = "{mode}"\n\tmanifest_url = "http://127.0.0.1:1/manifest.json"\n', encoding="ascii")


def prepare():
    (SERVER / "config").mkdir(parents=True, exist_ok=True)
    junction(SERVER / "libraries", WORK / "Server/libraries")
    (SERVER / "eula.txt").write_text("eula=true\n", encoding="ascii")
    (SERVER / "user_jvm_args.txt").write_text("-Xms512M\n-Xmx1500M\n", encoding="ascii")
    (SERVER / "server.properties").write_text("\n".join(PROPERTIES), encoding="ascii")
    ACCOUNTS.unlink(missing_ok=True)
    set_pack_mode("OFF")
    (INSTANCE / "resourcepacks").mkdir(parents=True, exist_ok=True)
    junction(CLIENT / "game", TEST / "root/game")
    (CLIENT / "launcher.json").write_text(json.dumps({"MaxMemoryMb": 2000}), encoding="ascii")
    (CLIENT / "secret.bin").unlink(missing_ok=True)
    for name in (ALLOWED, FOREIGN):
        write_zip(INSTANCE / "resourcepacks" / name, "pack.mcmeta", json.dumps({"pack": {"pack_format": 15, "description": name}}))
    client_state()


def client_state(extra_jar=False, packs=(ALLOWED,)):
    (INSTANCE / "mods").mkdir(parents=True, exist_ok=True)
    jar = INSTANCE / "mods" / EXTRA_JAR
    if extra_jar:
        write_zip(jar, "readme.txt", "not a mod")
    else:
        jar.unlink(missing_ok=True)
    selected = "".join(f',"file/{name}"' for name in packs)
    (INSTANCE / "options.txt").write_text(f'resourcePacks:["vanilla"{selected}]\nonboardAccessibility:false\n', encoding="ascii")


def console():
    return CONSOLE.read_text(encoding="utf-8", errors="replace") if CONSOLE.exists() else ""


def wait_for(found, seconds):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        result = found()
        if result:
            return result
        time.sleep(1)
    return None


def start_server(jar, auth_mode):
    global server
    put_mod(SERVER, jar)
    AUTH.write_text(f'[auth]\n\tmode = "{auth_mode}"\n', encoding="ascii")
    server = subprocess.Popen([str(JAVA), "@user_jvm_args.txt", "@" + FORGE, "nogui"], cwd=SERVER,
                              stdin=subprocess.PIPE, stdout=open(CONSOLE, "wb"), stderr=subprocess.STDOUT)
    started = time.monotonic()
    check(f"server with {jar.name} starts (password {auth_mode})", wait_for(lambda: "Done (" in console(), 300),
          console()[-1500:])
    print(f"     server ready in {time.monotonic() - started:.0f} s", flush=True)


def say(command):
    server.stdin.write((command + "\n").encode("ascii"))
    server.stdin.flush()


def stop_server():
    global server
    if server is None or server.poll() is not None:
        return
    say("stop")
    try:
        server.wait(60)
    except subprocess.TimeoutExpired:
        server.kill()
    server = None


def command_takes_effect(command, reply, file, value):
    seen = len(console())
    say(command)
    check(f"/{command} works at once and is saved", wait_for(lambda: reply in console()[seen:], 15)
          and f'"{value}"' in file.read_text(encoding="utf-8"), console()[seen:])


def outcome_of(text, nick):
    if f"{nick} joined the game" in text:
        return "joined"
    if "Disconnecting" in text and f"name={nick}," in text:
        return "refused"
    return None


def kill(pid):
    subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
    wait_for(lambda: str(pid) not in subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True,
                                                    text=True).stdout, 20)


def play(nick, password=None, jar=NEW, shot=None, extra_jar=False, packs=(ALLOWED,)):
    put_mod(INSTANCE, jar)
    client_state(extra_jar, packs)
    seen = len(console())
    command = [str(EXE), "--headless", "--dir", str(CLIENT), "--source", str(SOURCE), "--launch",
               "--join", f"127.0.0.1:{PORT}", "--nick", nick]
    started = time.monotonic()
    code = subprocess.run(command + (["--password", password] if password else [])).returncode
    launched = re.search(r"PID (\d+)$", (CLIENT / "launcher.log").read_text(encoding="utf-8"), re.M)
    if code != 0 or not launched:
        return "not started", 0, (CLIENT / "launcher.log").read_text(encoding="utf-8")[-1500:]
    try:
        outcome = wait_for(lambda: outcome_of(console()[seen:], nick), 300) or "nothing"
        seconds = time.monotonic() - started
        if shot:
            subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(HERE / "watch_game.ps1"),
                            "-Root", str(CLIENT), "-QuietSec", "5", "-TimeoutSec", "60", "-Shot", str(shot)])
    finally:
        kill(int(launched.group(1)))
    return outcome, seconds, console()[seen:]


def scenario(name, wanted, verdicts, **launch):
    outcome, seconds, text = play(**launch)
    found = re.findall(r"Nick check for \S+: (\w+)", text)
    check(f"{name} ({seconds:.0f} s)", outcome == wanted and found == verdicts, f"outcome {outcome}, verdicts {found}\n{text}")


def pack_scenario(name, wanted, mentions, **launch):
    outcome, seconds, text = play(**launch)
    lines = "\n".join(line for line in text.splitlines() if "Pack check for" in line)
    logged = all(word in lines for word in mentions) if mentions else not lines
    check(f"{name} ({seconds:.0f} s)", outcome == wanted and logged, f"outcome {outcome}\n{text}")


def accounts():
    return json.loads(ACCOUNTS.read_text(encoding="utf-8")) if ACCOUNTS.exists() else {}


def check(name, ok, details=""):
    results.append((name, bool(ok)))
    print(("OK   " if ok else "FAIL ") + name, flush=True)
    if not ok:
        print("\n".join("       " + line[:220] for line in str(details).splitlines()[-25:]), flush=True)


def strict():
    scenario("strict: the first login with a password gets in", "joined", [], nick="Tester_1", password="secret-pass")
    first = accounts()
    check("the nick is recorded with its public key only", list(first) == ["tester_1"]
          and first["tester_1"]["name"] == "Tester_1" and set(first["tester_1"]) == {"name", "key"})
    scenario("strict: the same password gets in again", "joined", [], nick="Tester_1", password="secret-pass")
    scenario("strict: another password is refused", "refused", ["WRONG_PASSWORD"], nick="Tester_1",
             password="wrong-pass", shot=SHOT)
    scenario("strict: no key on a protected nick is refused", "refused", ["PROTECTED_NICK"], nick="Tester_1")
    scenario("strict: a 1.7.1 client is refused after the wait", "refused", ["NEED_LAUNCHER"], nick="Tester_2", jar=OLD)
    check("refusals did not change the list", accounts() == first)
    return first


def soft(first):
    command_takes_effect("account mode soft", ": SOFT.", AUTH, "SOFT")
    scenario("soft: a free nick without a key gets in", "joined", [], nick="Tester_2")
    scenario("soft: a 1.7.1 client gets in after the wait", "joined", [], nick="Tester_3", jar=OLD)
    check("joining without a key records nothing", accounts() == first)
    say("account reset Tester_1")
    check("the admin resets a nick", wait_for(lambda: accounts() == {}, 15))
    scenario("soft: after the reset a new password takes the nick", "joined", [], nick="Tester_1", password="new-password")
    check("the nick now has another key", accounts().get("tester_1", {}).get("key") not in (None, first["tester_1"]["key"]))


def off():
    command_takes_effect("account mode off", ": OFF.", AUTH, "OFF")
    scenario("off: the new client gets in without a key", "joined", [], nick="Tester_1")
    scenario("off: a 1.7.1 client gets in", "joined", [], nick="Tester_3", jar=OLD)


def auth_phase():
    start_server(NEW, "STRICT")
    soft(strict())
    off()
    stop_server()
    start_server(OLD, "OFF")
    scenario("the new client with a key gets into a 1.7.1 server", "joined", [], nick="Tester_1", password="secret-pass")
    stop_server()


def pack_phase():
    manifest = {"revision": "login-check", "files": [
        {"path": "mods/" + NEW.name, "sha256": sha256(NEW)},
        {"path": "resourcepacks/" + ALLOWED, "sha256": sha256(INSTANCE / "resourcepacks" / ALLOWED)}]}
    CACHE.write_text(json.dumps(manifest), encoding="utf-8")
    set_pack_mode("REFUSE")
    start_server(NEW, "OFF")
    pack_scenario("pack refuse: the pack as listed gets in", "joined", [], nick="Tester_1")
    pack_scenario("pack refuse: a foreign jar is refused", "refused", [EXTRA_JAR], nick="Tester_1", extra_jar=True)
    pack_scenario("pack refuse: a foreign resource pack is refused", "refused", [FOREIGN], nick="Tester_1",
                  packs=(ALLOWED, FOREIGN))
    pack_scenario("pack refuse: a 1.8.0 client is refused after the wait", "refused", ["no report"], nick="Tester_1",
                  jar=NO_PACK_CHECK)
    command_takes_effect("packcheck mode log", ": LOG.", PACK, "LOG")
    pack_scenario("pack log: a foreign jar gets in and is written down", "joined", [EXTRA_JAR], nick="Tester_1",
                  extra_jar=True)
    seen = len(console())
    say("packcheck")
    check("/packcheck shows the saved list", wait_for(lambda: "login-check" in console()[seen:], 15), console()[seen:])
    command_takes_effect("packcheck mode off", ": OFF.", PACK, "OFF")
    stop_server()


PHASES = {"auth": auth_phase, "pack": pack_phase}

prepare()
try:
    for phase in sys.argv[1:] or PHASES:
        print(f"== {phase}", flush=True)
        PHASES[phase]()
finally:
    stop_server()

failed = [name for name, ok in results if not ok]
print(f"\n{len(results) - len(failed)} of {len(results)} passed" + ("" if not failed else "; FAILED: " + "; ".join(failed)))
sys.exit(1 if failed else 0)
