import _winapi, json, os, pathlib, re, shutil, subprocess, sys, time

HERE = pathlib.Path(__file__).resolve().parent
WORK = HERE.parents[2]
TEST = WORK / "LauncherTest"
SERVER, CLIENT, SOURCE = TEST / "auth_server", TEST / "auth", TEST / "source"
EXE = pathlib.Path(os.environ.get("MACE_EXE") or HERE.parent / "MaceLauncher/bin/Debug/net8.0-windows/win-x64/MACE.exe")
JAVA = TEST / "root/game/runtime/windows-x64/java-runtime-gamma/bin/java.exe"
FORGE = "libraries/net/minecraftforge/forge/1.20.1-47.4.26/win_args.txt"
BUILDS = WORK / "BattlefieldCore/build/libs"
NEW, OLD = BUILDS / "macecore-1.8.0.jar", BUILDS / "macecore-1.7.1.jar"
PORT = 25566
CONSOLE = SERVER / "console.log"
ACCOUNTS = SERVER / "config/mace-accounts.json"
MODE = SERVER / "config/battlefieldcore-auth.toml"
SHOT = TEST / "auth_refused.png"
PROPERTIES = ["server-ip=127.0.0.1", f"server-port={PORT}", "online-mode=false", "level-type=minecraft\\:flat",
              "generate-structures=false", "max-players=4", "spawn-protection=0", "view-distance=4",
              "simulation-distance=4", "motd=MACE auth check", ""]

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


def prepare():
    (SERVER / "config").mkdir(parents=True, exist_ok=True)
    junction(SERVER / "libraries", WORK / "Server/libraries")
    (SERVER / "eula.txt").write_text("eula=true\n", encoding="ascii")
    (SERVER / "user_jvm_args.txt").write_text("-Xms512M\n-Xmx1500M\n", encoding="ascii")
    (SERVER / "server.properties").write_text("\n".join(PROPERTIES), encoding="ascii")
    ACCOUNTS.unlink(missing_ok=True)
    (CLIENT / "instance").mkdir(parents=True, exist_ok=True)
    junction(CLIENT / "game", TEST / "root/game")
    (CLIENT / "launcher.json").write_text(json.dumps({"MaxMemoryMb": 2000}), encoding="ascii")
    (CLIENT / "secret.bin").unlink(missing_ok=True)


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


def set_mode(mode):
    MODE.write_text(f'[auth]\n\tmode = "{mode}"\n', encoding="ascii")


def start_server(jar, mode):
    global server
    put_mod(SERVER, jar)
    set_mode(mode)
    server = subprocess.Popen([str(JAVA), "@user_jvm_args.txt", "@" + FORGE, "nogui"], cwd=SERVER,
                              stdin=subprocess.PIPE, stdout=open(CONSOLE, "wb"), stderr=subprocess.STDOUT)
    started = time.monotonic()
    check(f"server with {jar.name} starts ({mode})", wait_for(lambda: "Done (" in console(), 300), console()[-1500:])
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


def switch_mode(mode):
    seen = len(console())
    say("account mode " + mode.lower())
    check(f"/account mode {mode.lower()} works at once and is saved", wait_for(lambda: f": {mode}." in console()[seen:], 15)
          and f'"{mode}"' in MODE.read_text(encoding="utf-8"), console()[seen:])


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


def play(nick, password=None, jar=NEW, shot=None):
    put_mod(CLIENT / "instance", jar)
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
    switch_mode("SOFT")
    scenario("soft: a free nick without a key gets in", "joined", [], nick="Tester_2")
    scenario("soft: a 1.7.1 client gets in after the wait", "joined", [], nick="Tester_3", jar=OLD)
    check("joining without a key records nothing", accounts() == first)
    say("account reset Tester_1")
    check("the admin resets a nick", wait_for(lambda: accounts() == {}, 15))
    scenario("soft: after the reset a new password takes the nick", "joined", [], nick="Tester_1", password="new-password")
    check("the nick now has another key", accounts().get("tester_1", {}).get("key") not in (None, first["tester_1"]["key"]))


def off():
    switch_mode("OFF")
    scenario("off: the new client gets in without a key", "joined", [], nick="Tester_1")
    scenario("off: a 1.7.1 client gets in", "joined", [], nick="Tester_3", jar=OLD)


prepare()
try:
    start_server(NEW, "STRICT")
    soft(strict())
    off()
    stop_server()
    start_server(OLD, "OFF")
    scenario("the new client with a key gets into a 1.7.1 server", "joined", [], nick="Tester_1", password="secret-pass")
finally:
    stop_server()

failed = [name for name, ok in results if not ok]
print(f"\n{len(results) - len(failed)} of {len(results)} passed" + ("" if not failed else "; FAILED: " + "; ".join(failed)))
sys.exit(1 if failed else 0)
