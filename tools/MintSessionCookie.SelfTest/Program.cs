using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AgentForge.MintSessionCookie;
using AgentForge.MintSessionCookie.SelfTest;

// Drives the real tool - in process for the flow, out of process for what it prints - against a fake
// sidecar + OpenEMR on loopback. No real credential, host or patient is involved. The browser scenarios
// need Playwright's Chromium or --browser-channel msedge|chrome; --no-browser runs only the checks that
// need none. Exit 0 all passed, 1 any failed.
string? channel = null;
var verbose = false;
var noBrowser = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--browser-channel" && i + 1 < args.Length)
    {
        channel = args[++i];
    }
    else if (args[i] == "--verbose")
    {
        verbose = true;
    }
    else if (args[i] == "--no-browser")
    {
        noBrowser = true;
    }
}

await using var door = await StubFrontDoor.StartAsync();
var failures = new List<string>();
var passed = 0;
var log = verbose ? new TimestampedWriter(Console.Out) : TextWriter.Null;

MintOptions Options(LaunchKind launch, string? patientId = null, Uri? openEmr = null) => new()
{
    SidecarBaseUrl = door.SidecarBase,
    OpenEmrOrigin = MintOptions.OriginOf(openEmr ?? door.Primary),
    Launch = launch,
    PatientId = patientId,
    BrowserChannel = channel,
    Timeout = TimeSpan.FromSeconds(15),
};

async Task Scenario(string name, StubState state, Func<Task> body)
{
    door.Reset(state);
    log.WriteLine($"-- {name}");
    try
    {
        // A hang is a failure too, not a stalled run.
        var run = body();
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(90))) != run)
        {
            throw new TimeoutException("the scenario did not finish within 90s");
        }

        await run;
        log.WriteLine("(scenario done)");
        passed++;
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        failures.Add(name);
        Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
    }
}

void Expect(bool condition, string what)
{
    if (!condition)
    {
        throw new InvalidOperationException(what);
    }
}

async Task<string> ExpectRefusal(Func<Task<string>> mint, string messageFragment)
{
    try
    {
        await mint();
    }
    catch (MintException ex)
    {
        Expect(ex.Message.Contains(messageFragment, StringComparison.Ordinal), $"refusal said '{ex.Message}', expected it to mention '{messageFragment}'");
        return ex.Message;
    }

    throw new InvalidOperationException("the tool minted a cookie where it should have refused");
}

string SoleAuthenticatedSession(StubState s)
{
    var ids = s.AuthenticatedSessionIds.ToArray();
    Expect(ids.Length == 1, $"expected exactly one authenticated sidecar session, found {ids.Length}");
    return $"{StubFrontDoor.SessionCookie}={ids[0]}";
}

string Throwaway() => "stub-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

// ---- no browser: the guards themselves ----
MintOptions GuardOptions(string openEmr) => new()
{
    SidecarBaseUrl = new Uri(openEmr + "/agentforge/"),
    OpenEmrOrigin = MintOptions.OriginOf(new Uri(openEmr)),
};

void ExpectGuardRefuses(MintOptions options, string pageUrl, string? formTarget, string messageFragment)
{
    try
    {
        SessionMinter.EnsureCredentialsMayGoTo(options, pageUrl, formTarget);
    }
    catch (MintException ex)
    {
        Expect(ex.Message.Contains(messageFragment, StringComparison.Ordinal), $"refusal said '{ex.Message}', expected it to mention '{messageFragment}'");
        return;
    }

    throw new InvalidOperationException($"credentials were allowed into {pageUrl}, submitting to {formTarget ?? "(no form)"}");
}

const string GuardLogin = "/oauth2/default/provider/login";

await Scenario("credential guard: an https page on the expected origin, submitting to it -> allowed", new StubState(), () =>
{
    SessionMinter.EnsureCredentialsMayGoTo(
        GuardOptions("https://openemr.example"), "https://openemr.example" + GuardLogin + "?state=x", "https://openemr.example" + GuardLogin);
    return Task.CompletedTask;
});

await Scenario("credential guard: plain http off loopback -> refused, even on the named origin", new StubState(), () =>
{
    ExpectGuardRefuses(GuardOptions("http://openemr.example"), "http://openemr.example" + GuardLogin, "http://openemr.example" + GuardLogin, "plain http");
    return Task.CompletedTask;
});

await Scenario("credential guard: a form submitting off-origin, to a non-http URL, or no form at all -> refused", new StubState(), () =>
{
    var options = GuardOptions("https://openemr.example");
    const string page = "https://openemr.example" + GuardLogin;
    ExpectGuardRefuses(options, page, "https://collector.example/login", "the login form submits to https://collector.example/");
    ExpectGuardRefuses(options, page, "http://openemr.example/login", "the login form submits to http://openemr.example/");
    ExpectGuardRefuses(options, page, "javascript:void(0)", "a javascript: URL");
    ExpectGuardRefuses(options, page, null, "not inside a form");
    return Task.CompletedTask;
});

await Scenario("browser errors: only the first line is printed, with the password masked", new StubState(), () =>
{
    var password = Throwaway();
    var message = $"Error: fill failed near \"{password}\"\r\nCall log:\n  - fill(\"{password}\")";
    var scrubbed = SessionMinter.ScrubBrowserError(message, password);
    Expect(!scrubbed.Contains(password, StringComparison.Ordinal), "the password survived the scrub");
    Expect(scrubbed == "Error: fill failed near \"***\"", $"expected only the masked first line, got '{scrubbed}'");
    return Task.CompletedTask;
});

await Scenario("--out over an existing file with wider access: left to the current user alone", new StubState(), async () =>
{
    var file = Path.Combine(Path.GetTempPath(), $"mint-session-selftest-{Guid.NewGuid():N}.txt");
    try
    {
        await File.WriteAllTextAsync(file, "stale");
        WidenAccess(file);
        await CookieFile.WriteAsync(file, "Cookie=value");
        Expect(await File.ReadAllTextAsync(file) == "Cookie=value", "the file does not hold exactly the pair");
        ExpectCurrentUserOnly(file);
    }
    finally
    {
        File.Delete(file);
    }
});

await Scenario(
    OperatingSystem.IsLinux()
        ? "the password variable is gone before the Playwright driver starts (the driver's own environment, from /proc)"
        : "the password variable is gone from this process once the Playwright driver has started (the driver's own environment is read on Linux only)",
    new StubState(),
    async () =>
    {
        var secret = Throwaway();
        Environment.SetEnvironmentVariable(LoginCredentials.PasswordVariable, secret);
        try
        {
            using var playwright = await SessionMinter.StartPlaywrightAsync();
            Expect(Environment.GetEnvironmentVariable(LoginCredentials.PasswordVariable) is null, "the variable is still set in this process");
            if (OperatingSystem.IsLinux())
            {
                var children = LinuxChildProcessIds(Environment.ProcessId);
                Expect(children.Count > 0, "no Playwright driver process was found to inspect");
                foreach (var pid in children)
                {
                    var environ = await File.ReadAllTextAsync($"/proc/{pid}/environ");
                    Expect(!environ.Contains(secret, StringComparison.Ordinal), $"the driver (pid {pid}) inherited the password");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(LoginCredentials.PasswordVariable, null);
        }
    });

if (noBrowser)
{
    return Summary();
}

await Scenario("patient launch: login, picker, consent, callback -> the authenticated cookie, verified by GET /patient", new StubState(), async () =>
{
    var s = door.State;
    var pair = await SessionMinter.MintAsync(Options(LaunchKind.Patient, StubFrontDoor.PatientIds[1]), new LoginCredentials(s.Username, s.Password), log);
    Expect(pair == SoleAuthenticatedSession(s), "the minted pair is not the session the stub authenticated");
    Expect(s.SidecarSessions.Values.Single(v => v.Authenticated).PatientId == StubFrontDoor.PatientIds[1], "the wrong patient was picked");
});

await Scenario("agenda launch: no picker, consent, callback -> the authenticated cookie", new StubState(), async () =>
{
    var s = door.State;
    var pair = await SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, s.Password), log);
    Expect(pair == SoleAuthenticatedSession(s), "the minted pair is not the session the stub authenticated");
});

await Scenario("wrong password: fails with OpenEMR's own message and never retries", new StubState(), async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, "not-the-password"), log),
        "Invalid username or password");
    Expect(s.LoginPostCount == 1, $"expected exactly one login attempt, saw {s.LoginPostCount}");
    Expect(!s.AuthenticatedSessionIds.Any(), "a session was authenticated");
});

await Scenario("login page on an unexpected origin: credentials are never typed", new StubState { AuthorizeOrigin = door.Secondary }, async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, s.Password), log),
        "Refusing to enter credentials");
    Expect(s.LoginPostCount == 0, $"credentials were posted {s.LoginPostCount} time(s) to the unexpected origin");
});

await Scenario("login page on a second origin named by --openemr-base-url: allowed", new StubState { AuthorizeOrigin = door.Secondary }, async () =>
{
    var s = door.State;
    var pair = await SessionMinter.MintAsync(Options(LaunchKind.Agenda, openEmr: door.Secondary), new LoginCredentials(s.Username, s.Password), log);
    Expect(pair == SoleAuthenticatedSession(s), "the minted pair is not the session the stub authenticated");
});

await Scenario("sidecar callback refuses (403): reported, no cookie", new StubState { RefuseCallback = true }, async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, s.Password), log), "answered 403");
});

await Scenario("login form submitting to another origin: nothing typed, nothing posted", new StubState { LoginFormPostsTo = door.Secondary }, async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, s.Password), log),
        "the login form submits to");
    Expect(s.PasswordTypedCount == 0, "the password was typed");
    Expect(s.LoginPostCount == 0, $"credentials were posted {s.LoginPostCount} time(s)");
});

await Scenario("form repointed off-origin while the username is typed: the password is never typed",
    new StubState { RetargetWhenTyped = "username", RetargetTo = door.Secondary }, async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, s.Password), log),
        "the login form submits to");
    Expect(s.PasswordTypedCount == 0, "the password was typed into the repointed form");
    Expect(s.LoginPostCount == 0, $"credentials were posted {s.LoginPostCount} time(s)");
});

await Scenario("form repointed off-origin while the password is typed: never submitted",
    new StubState { RetargetWhenTyped = "password", RetargetTo = door.Secondary }, async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, s.Password), log),
        "the login form submits to");
    Expect(s.LoginPostCount == 0, $"credentials were posted {s.LoginPostCount} time(s) to the repointed form");
});

await Scenario("TOTP page: stops and says so, submits nothing further", new StubState { Mfa = MfaKind.Totp }, async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, s.Password), log), "MFA");
    Expect(s.LoginPostCount == 1, $"expected exactly one login post, saw {s.LoginPostCount}");
});

await Scenario("U2F page: the MFA message, not 'rejected the login'", new StubState { Mfa = MfaKind.U2f }, async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Agenda), new LoginCredentials(s.Username, s.Password), log), "MFA");
    Expect(s.LoginPostCount == 1, $"expected exactly one login post, saw {s.LoginPostCount}");
});

await Scenario("GET /patient drops the connection: a refusal (exit 1), not an unhandled exception", new StubState { DropPatientCheck = true }, async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Patient, StubFrontDoor.PatientIds[0]), new LoginCredentials(s.Username, s.Password), log),
        "could not be checked");
});

await Scenario("patient picker with no --patient-id: stops and asks for one", new StubState(), async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Patient), new LoginCredentials(s.Username, s.Password), log), "--patient-id");
    Expect(s.OpenEmrSessions.Values.All(v => v.PatientId is null), "a patient was picked anyway");
});

await Scenario("patient not offered by the picker: stops", new StubState(), async () =>
{
    var s = door.State;
    await ExpectRefusal(() => SessionMinter.MintAsync(Options(LaunchKind.Patient, "00000000-0000-4000-8000-00000000ffff"), new LoginCredentials(s.Username, s.Password), log),
        "is not among the patients");
});

// ---- out of process: what the real executable prints, and where ----
var toolDll = Path.Combine(AppContext.BaseDirectory, "MintSessionCookie.dll");
var channelArgs = channel is null ? Array.Empty<string>() : ["--browser-channel", channel];

async Task<(int Exit, string Stdout, string Stderr)> RunTool(IEnumerable<string> toolArgs, IDictionary<string, string?> environment)
{
    var psi = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(toolDll);
    foreach (var a in toolArgs.Concat(channelArgs))
    {
        psi.ArgumentList.Add(a);
    }

    psi.Environment.Remove(LoginCredentials.UsernameVariable);
    psi.Environment.Remove(LoginCredentials.PasswordVariable);
    psi.Environment.Remove(MintOptions.BaseUrlVariable);
    foreach (var (k, v) in environment)
    {
        psi.Environment[k] = v;
    }

    using var process = Process.Start(psi)!;
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await stdout, await stderr);
}

await Scenario("executable, stdout: exactly the pair; the password appears on neither stream", new StubState(), async () =>
{
    var s = door.State;
    var (exit, stdout, stderr) = await RunTool(
        ["--base-url", door.SidecarBase.ToString(), "--launch", "patient", "--patient-id", StubFrontDoor.PatientIds[0]],
        new Dictionary<string, string?> { [LoginCredentials.UsernameVariable] = s.Username, [LoginCredentials.PasswordVariable] = s.Password });
    Expect(exit == 0, $"exit {exit}: {stderr}");
    var pair = SoleAuthenticatedSession(s);
    Expect(stdout == pair + Environment.NewLine, "stdout is not exactly the cookie pair");
    Expect(!stdout.Contains(s.Password, StringComparison.Ordinal) && !stderr.Contains(s.Password, StringComparison.Ordinal), "the password was printed");
    Expect(!stderr.Contains(pair.Split('=', 2)[1], StringComparison.Ordinal), "the cookie value leaked onto stderr");
});

await Scenario("executable, --out: the pair goes to the named file only", new StubState(), async () =>
{
    var s = door.State;
    var file = Path.Combine(Path.GetTempPath(), $"mint-session-selftest-{Guid.NewGuid():N}.txt");
    try
    {
        var (exit, stdout, stderr) = await RunTool(
            ["--base-url", door.SidecarBase.ToString(), "--out", file],
            new Dictionary<string, string?> { [LoginCredentials.UsernameVariable] = s.Username, [LoginCredentials.PasswordVariable] = s.Password });
        Expect(exit == 0, $"exit {exit}: {stderr}");
        var pair = SoleAuthenticatedSession(s);
        Expect(stdout.Length == 0, "stdout was not empty with --out");
        Expect(await File.ReadAllTextAsync(file) == pair, "the file does not hold exactly the cookie pair");
        Expect(!stderr.Contains(pair.Split('=', 2)[1], StringComparison.Ordinal), "the cookie value leaked onto stderr");
    }
    finally
    {
        File.Delete(file);
    }
});

await Scenario("executable, --out that cannot be written: exit 2 and a message, no stack trace, nothing on stdout", new StubState(), async () =>
{
    var s = door.State;
    var file = Path.Combine(Path.GetTempPath(), $"mint-session-selftest-{Guid.NewGuid():N}", "missing", "cookie.txt");
    var (exit, stdout, stderr) = await RunTool(
        ["--base-url", door.SidecarBase.ToString(), "--out", file],
        new Dictionary<string, string?> { [LoginCredentials.UsernameVariable] = s.Username, [LoginCredentials.PasswordVariable] = s.Password });
    Expect(exit == 2, $"exit {exit}, expected 2: {stderr}");
    Expect(stderr.Contains("could not be written", StringComparison.Ordinal), "no explanation");
    Expect(!stderr.Contains("   at ", StringComparison.Ordinal), "a stack trace was printed");
    Expect(stdout.Length == 0, "the pair fell back to stdout");
    Expect(!stderr.Contains(SoleAuthenticatedSession(s).Split('=', 2)[1], StringComparison.Ordinal), "the cookie value leaked onto stderr");
});

await Scenario("executable: a credential passed as an argument is refused before anything runs", new StubState(), async () =>
{
    var (exit, _, stderr) = await RunTool(["--base-url", door.SidecarBase.ToString(), "--password=x"], new Dictionary<string, string?>());
    Expect(exit == 2, $"exit {exit}, expected 2");
    Expect(stderr.Contains("never accepted as arguments", StringComparison.Ordinal), "no explanation");
    Expect(door.State.SidecarSessions.IsEmpty, "the tool launched anyway");
});

await Scenario("executable: no credentials and no terminal -> usage error, no launch", new StubState(), async () =>
{
    var (exit, _, stderr) = await RunTool(["--base-url", door.SidecarBase.ToString()], new Dictionary<string, string?>());
    Expect(exit == 2, $"exit {exit}, expected 2");
    Expect(stderr.Contains(LoginCredentials.UsernameVariable, StringComparison.Ordinal), "the error does not name the variable");
    Expect(door.State.SidecarSessions.IsEmpty, "the tool launched anyway");
});

return Summary();

int Summary()
{
    Console.WriteLine();
    Console.WriteLine(failures.Count == 0 ? $"All {passed} scenarios passed." : $"{failures.Count} of {passed + failures.Count} scenario(s) failed.");
    return failures.Count == 0 ? 0 : 1;
}

// A mode or rule another user could read through, as a stale --out file might carry.
static void WidenAccess(string file)
{
    if (OperatingSystem.IsWindows())
    {
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
    else
    {
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
}

void ExpectCurrentUserOnly(string file)
{
    if (OperatingSystem.IsWindows())
    {
        var security = new FileInfo(file).GetAccessControl();
        Expect(security.AreAccessRulesProtected, "the file still inherits its directory's access rules");
        var others = GrantsBeyondCurrentUser(security);
        Expect(others.Count == 0, $"access is granted beyond the current user: {string.Join(", ", others)}");
    }
    else
    {
        var mode = File.GetUnixFileMode(file);
        Expect(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), $"mode is {mode}, expected UserRead, UserWrite (0600)");
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static List<string> GrantsBeyondCurrentUser(FileSecurity security)
{
    var user = WindowsIdentity.GetCurrent().User!;
    var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
    var others = new List<string>();
    foreach (FileSystemAccessRule rule in rules)
    {
        if (!rule.IdentityReference.Equals(user) || rule.AccessControlType != AccessControlType.Allow)
        {
            others.Add($"{rule.AccessControlType} {rule.IdentityReference.Value}");
        }
    }

    return rules.Count == 0 ? ["(no rule at all)"] : others;
}

static List<int> LinuxChildProcessIds(int parent)
{
    var children = new List<int>();
    foreach (var dir in Directory.EnumerateDirectories("/proc"))
    {
        if (!int.TryParse(Path.GetFileName(dir), out var pid))
        {
            continue;
        }

        try
        {
            // "pid (comm) state ppid ...": comm may hold spaces or parentheses, so read after the last ')'.
            var stat = File.ReadAllText(Path.Combine(dir, "stat"));
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            if (int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture) == parent)
            {
                children.Add(pid);
            }
        }
        catch (IOException)
        {
            // The process exited while being read.
        }
    }

    return children;
}

internal sealed class TimestampedWriter(TextWriter inner) : TextWriter
{
    public override System.Text.Encoding Encoding => inner.Encoding;

    public override void WriteLine(string? value) => inner.WriteLine($"   {DateTime.Now:HH:mm:ss.fff} {value}");
}
