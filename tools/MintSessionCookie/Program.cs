using AgentForge.MintSessionCookie;
using Microsoft.Playwright;

// stdout carries the cookie pair and nothing else, so `$(...)` captures exactly it; every message goes to
// stderr. Exit 0 minted, 1 the login did not produce a (verified) session, 2 usage or configuration error,
// including an --out path that cannot be written.
// tests/bruno-collection/README.md "Getting a session cookie"
var stderr = Console.Error;

if (args.Any(a => a is "--help" or "-h"))
{
    Console.Out.WriteLine(MintOptions.Usage);
    return 0;
}

var (options, usageError) = MintOptions.Parse(args, Environment.GetEnvironmentVariable);
if (options is null)
{
    stderr.WriteLine(usageError);
    return 2;
}

var (credentials, credentialError) = LoginCredentials.Resolve(
    Environment.GetEnvironmentVariable,
    interactive: !Console.IsInputRedirected,
    prompt: stderr,
    readLine: Console.ReadLine,
    readSecret: LoginCredentials.ReadSecretFromConsole);
if (credentials is null)
{
    stderr.WriteLine(credentialError);
    return 2;
}

string pair;
try
{
    pair = await SessionMinter.MintAsync(options, credentials, stderr);
}
catch (MintException ex)
{
    stderr.WriteLine($"No session minted: {ex.Message}");
    return 1;
}
catch (PlaywrightException ex)
{
    // Typically a missing browser: `pwsh tools/MintSessionCookie/bin/Debug/net10.0/playwright.ps1 install chromium`
    // or --browser-channel msedge|chrome. Playwright's messages carry no page content.
    stderr.WriteLine($"No session minted - the browser failed: {ex.Message}");
    return 1;
}

if (options.OutputPath is null)
{
    Console.Out.WriteLine(pair);
}
else
{
    try
    {
        await CookieFile.WriteAsync(options.OutputPath, pair);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // The pair is not printed instead: --out was asked for precisely to keep it off stdout.
        stderr.WriteLine($"No cookie written - --out {options.OutputPath} could not be written: {ex.Message}");
        return 2;
    }

    stderr.WriteLine($"Session cookie written to {Path.GetFullPath(options.OutputPath)}.");
}

stderr.WriteLine("It lasts until the OpenEMR access token behind it expires (about an hour) or the sidecar restarts.");
return 0;
