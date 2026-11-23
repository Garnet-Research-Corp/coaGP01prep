// coaGP01prep - one-time setup check for any PC in the TugAssist backup/restore fleet: GP01 itself
// (runs cwaGP01msql, the one central restore target) and each of the 6 live TugAssist/MSSQL servers
// (each runs cwaGP01baks, pushing its backups to \\garnet.lan\tugit for GP01 to restore from).
// cwaGP01baks and cwaGP01msql never run on the same machine, and both are configured to listen on
// the SAME port (see Gp01AgentPort below) - so one port check, run unchanged on all 7 machines,
// covers whichever of the two is actually installed there. Also confirms the .NET Desktop Runtime
// (needed by the mauGP01wrap launcher) and the ASP.NET Core Runtime (needed by either app) are in
// place - installing/fixing whatever is missing.
// Meant to be copied to each machine and double-clicked before mauGP01wrap is installed there: no
// prompts, it just runs, fixes what it can, and reports pass/fail.
//
// Double-click coaGP01prep.exe:
//   - all GOOD already, or everything got fixed cleanly: shows the checklist for 10 seconds and closes.
//   - anything could not be fixed: shows the result and waits for a key press (no timeout).
// Command line: coaGP01prep [apply | status]   (no parameter = apply, interactive)
// Build:  dotnet publish coaGP01prep.cs -o C:\deploy.g4l\tools\coaGP01prep   (single self-contained coaGP01prep.exe)
//
// Admin rights are needed to install a runtime or add the firewall rule; a double-click asks for
// them (only if something actually needs fixing - a clean PC never prompts). Internet access to
// aka.ms/dotnet is needed to download a missing runtime - reports clearly if that fails rather than
// hanging, so a run on an offline PC still ends with a readable result.
//
// Every run is appended to coaGP01prep.log next to the exe (or in %TEMP%).
//
// NOTE: cwaGP01baks's AND cwaGP01msql's actual configured port is 38081 (both apps' own
// appsettings.json, and mauGP01wrap's apps.json - the launcher starts each with --urls overriding
// to the apps.json port, so that's the one that actually matters at runtime). Deliberately the same
// port for both, since they never coexist on one machine. Change Gp01AgentPort below if that's ever
// meant to differ. (Was 38110/38111 before 2026-11-22 - renumbered, not a typo.)

#:property TargetFramework=net10.0-windows
#:property PublishAot=false
#:property PublishSingleFile=true
#:property SelfContained=true
#:property IncludeNativeLibrariesForSelfExtract=true
#:property RuntimeIdentifier=win-x64
#:property DebugType=embedded
#:property Version=1.1.0
#:property SourceRevisionId=$([System.DateTime]::Now.ToString('yyyy-MM-dd HH:mm'))

using System.Diagnostics;
using System.Security.Principal;

const int RequiredMajor = 10;
const int Gp01AgentPort = 38081;
const string FirewallRuleName = "coaGP01prep - TugAssist Agent (TCP 38081)";
const string DesktopRuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
const string AspNetRuntimeUrl = "https://aka.ms/dotnet/10.0/aspnetcore-runtime-win-x64.exe";

// InformationalVersion is "<Version>+<build time>" (see the #:property lines above).
var info = (System.Reflection.Assembly.GetEntryAssembly()!
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+') ?? ["?"];
var log = OpenLog();
if (log != null) Console.SetOut(new Tee(Console.Out, log.Value.writer));
Console.WriteLine($"===== coaGP01prep v{info[0]} (built {(info.Length > 1 ? info[1] : "?")})");
Console.WriteLine($"      {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Environment.MachineName}  user={WindowsIdentity.GetCurrent().Name}  args=\"{string.Join(' ', args)}\"");

// A double-click has no parameters: apply, then keep the window open (see the end of this block).
var opts = args.Select(a => a.TrimStart('/', '-').ToLowerInvariant()).ToList();
var interactive = opts.Count == 0 || opts.Remove("linger");
var cmd = opts.FirstOrDefault() ?? "apply";
var good = false;
var rc = 0;
try
{
    switch (cmd)
    {
        case "apply":
            var needDesktop = !SharedFxInstalled("Microsoft.WindowsDesktop.App");
            var needAspNet = !SharedFxInstalled("Microsoft.AspNetCore.App");
            var needFirewall = !FirewallRuleExists(FirewallRuleName);
            if (needDesktop || needAspNet || needFirewall)
            {
                if (!EnsureAdmin(cmd, interactive)) return 0;
                if (needDesktop) DownloadAndInstall(DesktopRuntimeUrl, ".NET Desktop Runtime");
                if (needAspNet) DownloadAndInstall(AspNetRuntimeUrl, "ASP.NET Core Runtime");
                if (needFirewall) AddFirewallRule(FirewallRuleName, Gp01AgentPort);
            }
            break;
        case "status": interactive = false; break;
        default: Console.WriteLine("Usage: coaGP01prep [apply | status]"); return 1;
    }
    good = Checklist();
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
}
if (!good) rc = 1;
Console.WriteLine($"Result: {(good ? "GOOD" : "NOT FINISHED")} (exit code {rc})");
if (log != null) Console.WriteLine($"Log: {log.Value.path}");
Console.Out.Flush();
if (interactive)
{
    Console.WriteLine();
    if (good)
    {
        Console.WriteLine("This window closes in 10 seconds.");
        Thread.Sleep(10000);
    }
    else if (!Console.IsInputRedirected)
    {
        Console.WriteLine("Press any key to close.");
        Console.ReadKey(true);
    }
}
return rc;

// ---------------------------------------------------------------- checklist

// Re-checks all three items in their CURRENT state (after any fixes above) and prints them.
// Returns true only when every item is GOOD.
static bool Checklist()
{
    var items = new List<(bool ok, string name, string detail)>
    {
        FxItem("Microsoft.WindowsDesktop.App", ".NET Desktop Runtime (for mauGP01wrap)"),
        FxItem("Microsoft.AspNetCore.App", "ASP.NET Core Runtime (for cwaGP01baks/msql)"),
        (FirewallRuleExists(FirewallRuleName), $"Firewall: TCP {Gp01AgentPort} inbound (baks/msql)",
            FirewallRuleExists(FirewallRuleName) ? "allowed" : "BLOCKED - no inbound rule"),
    };
    var good = items.All(i => i.ok);

    Console.WriteLine();
    Console.WriteLine("coaGP01prep - GP01 backup agent prerequisites");
    Console.WriteLine();
    foreach (var (ok, name, detail) in items)
    {
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "  [GOOD]  " : "  [FAIL]  ");
        Console.ResetColor();
        Console.WriteLine($"{name,-44} {detail}");
    }
    Console.WriteLine();
    Console.ForegroundColor = good ? ConsoleColor.Green : ConsoleColor.Yellow;
    Console.WriteLine(good
        ? "STATUS: GOOD - this PC is ready for mauGP01wrap / cwaGP01baks / cwaGP01msql."
        : "STATUS: NOT FINISHED - see the items marked FAIL above (re-run coaGP01prep, or fix by hand).");
    Console.ResetColor();
    return good;

    static (bool, string, string) FxItem(string fxName, string label)
    {
        var ok = SharedFxInstalled(fxName);
        var ver = ok ? HighestInstalledVersion(fxName) : null;
        return (ok, label, ok ? $"installed ({ver})" : "MISSING");
    }
}

// ---------------------------------------------------------------- runtime checks

// Same filesystem check cwaGP01baks's own launcher (mauGP01wrap/Services/RuntimeCheck.cs) uses:
// looking directly at the shared-framework folder works even on a brand-new PC with no dotnet.exe
// to run "dotnet --list-runtimes" with yet - the exact case this tool exists to fix.
static bool SharedFxInstalled(string fxName) => HighestInstalledVersion(fxName) != null;

static string? HighestInstalledVersion(string fxName)
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", fxName);
    if (!Directory.Exists(dir)) return null;
    return Directory.EnumerateDirectories(dir)
        .Select(Path.GetFileName)
        .Where(n => int.TryParse(n?.Split('.')[0], out var major) && major >= RequiredMajor)
        .OrderDescending()
        .FirstOrDefault();
}

// Downloads a Microsoft .NET runtime installer (a Burn bootstrapper .exe) and runs it silently.
// Never throws - reports success/failure to the console and moves on, so one failed download
// doesn't stop the other checks from running.
static void DownloadAndInstall(string url, string friendlyName)
{
    var tmp = Path.Combine(Path.GetTempPath(), $"coaGP01prep-{Path.GetFileName(url)}");
    try
    {
        Console.WriteLine($"  Downloading {friendlyName}...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using (var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            resp.EnsureSuccessStatusCode();
            using var fs = File.Create(tmp);
            resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
        }

        var installLog = Path.Combine(Path.GetTempPath(), $"coaGP01prep-{Path.GetFileNameWithoutExtension(url)}-install.log");
        Console.WriteLine($"  Installing {friendlyName} (silent)...");
        var psi = new ProcessStartInfo(tmp, $"/install /quiet /norestart /log \"{installLog}\"") { UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();

        // Burn bundle exit codes: 0 = success; 3010 / 1641 = success, reboot required/initiated.
        if (p.ExitCode is 0 or 3010 or 1641)
        {
            Console.WriteLine(p.ExitCode == 0
                ? $"  {friendlyName} installed."
                : $"  {friendlyName} installed - a reboot is recommended (exit code {p.ExitCode}).");
        }
        else
        {
            Console.WriteLine($"  {friendlyName} installer exited with code {p.ExitCode}. See {installLog}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Could not install {friendlyName}: {ex.Message}");
    }
    finally
    {
        try { File.Delete(tmp); } catch { }
    }
}

// ---------------------------------------------------------------- firewall

static bool FirewallRuleExists(string name)
{
    try
    {
        var psi = new ProcessStartInfo("netsh.exe", $"advfirewall firewall show rule name=\"{name}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && !output.Contains("No rules match", StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}

static void AddFirewallRule(string name, int port)
{
    try
    {
        Console.WriteLine($"  Adding firewall rule for TCP {port}...");
        var psi = new ProcessStartInfo("netsh.exe",
            $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port}")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) Console.WriteLine($"  netsh exited with code {p.ExitCode} adding the firewall rule.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Could not add the firewall rule: {ex.Message}");
    }
}

// ---------------------------------------------------------------- plumbing

static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

// Returns true when already elevated. Otherwise restarts itself through the Windows admin prompt
// (the elevated copy does the work in its own window) and returns false.
static bool EnsureAdmin(string cmd, bool interactive)
{
    if (IsAdmin()) return true;
    try
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, interactive ? $"{cmd} linger" : cmd) { UseShellExecute = true, Verb = "runas" });
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Console.WriteLine("Admin rights are needed to fix this PC. Nothing was changed.");
        if (interactive && !Console.IsInputRedirected) { Console.WriteLine("Press any key to close."); Console.ReadKey(true); }
    }
    return false;
}

static (StreamWriter writer, string path)? OpenLog()
{
    foreach (var dir in new[] { Path.GetDirectoryName(Environment.ProcessPath)!, Path.GetTempPath() })
    {
        try
        {
            var path = Path.Combine(dir, "coaGP01prep.log");
            return (new StreamWriter(path, append: true) { AutoFlush = true }, path);
        }
        catch { }
    }
    return null;
}

// Writes to the screen and the log file at the same time.
sealed class Tee(TextWriter screen, TextWriter file) : TextWriter
{
    public override System.Text.Encoding Encoding => screen.Encoding;
    public override void Write(char value) { screen.Write(value); file.Write(value); }
    public override void Write(string? value) { screen.Write(value); file.Write(value); }
    public override void WriteLine(string? value) { screen.WriteLine(value); file.WriteLine(value); }
    public override void Flush() { screen.Flush(); file.Flush(); }
}
