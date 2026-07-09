using System.Runtime.InteropServices;

namespace UsvfsSpike;

// USVFS staging spike harness. Three controller scenarios answer the mechanism
// questions from docs/design/0002-usvfs-spike.md, plus auxiliary modes.
//
// The scenarios spawn **cmd.exe** (native) as the enumerator, not a managed app.
// Reason: a managed process hangs under USVFS injection (the .NET runtime's
// file-API churn during startup interacts badly with USVFS's hooks). The real
// USVFS targets in this architecture are native (modificus_relay.exe + Darktide),
// so a native enumerator is both more representative and avoids the hang. cmd's
// `dir` exercises the same FindFirstFile / NtQueryDirectoryFile hooks that
// relay's LuaJIT loader and Darktide's engine ultimately call.
//
// Scenarios (controller):
//   enum       VFS + link, spawn `cmd /c dir staged` via usvfsCreateProcessHooked.
//   propagate  VFS + link, spawn a hooked cmd that enumerates AND spawns a child
//              cmd (via its own plain CreateProcess) that also enumerates.
//   lifetime   VFS + link, spawn a hooked cmd that waits 5s then enumerates;
//              the controller disconnects from the VFS before the wait ends.
// Auxiliary (kept for manual inspection; not used by scenarios):
//   target / relay-standin / version
//
// Build+run: see spike/usvfs/run.ps1
internal static class Program
{
    private const string ModName = "mymod"; // the virtual folder name under staged/

    private static int Main(string[] args)
    {
        try
        {
            UsvfsInterop.usvfsInitLogging(true); // log locally so failures leave a trail
            var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            var opts = ParseOpts(args.AsSpan(1));
            return cmd switch
            {
                "version" => DoVersion(),
                "target" => RunTarget(opts),
                "relay-standin" => RunRelayStandin(opts),
                "enum" => RunEnum(opts),
                "propagate" => RunPropagate(opts),
                "lifetime" => RunLifetime(opts),
                "spawn" => RunSpawn(opts),
                _ => PrintHelp(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[spike] FATAL: {ex}");
            return 99;
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine("usvfs-spike <command> [opts]");
        Console.WriteLine("  version");
        Console.WriteLine("  enum [--work <p>] | propagate [--work <p>] | lifetime [--work <p>]");
        Console.WriteLine("  (auxiliary) target --dir <p> --expect <name> [--stay ms] [--tag t]");
        Console.WriteLine("  (auxiliary) relay-standin --staged <p> --expect <name> --exe <self>");
        return 0;
    }

    private static string CmdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    // ---- scenario: enum (Q: does a usvfs-spawned native process see the virtual dir?) ----

    private static int RunEnum(Dictionary<string, string> opts)
    {
        using var fx = ScenarioFixture.Create(opts);
        var outFile = Path.Combine(fx.Work, "out.txt");
        var started = Path.Combine(fx.Work, "started.txt");
        var done = Path.Combine(fx.Work, "done.txt");
        var bat = Path.Combine(fx.Work, "run.bat");
        // Diagnostics markers: STARTED before the dir, DONE after. Tells us where it hangs.
        WriteBat(bat,
            $"echo STARTED > \"{started}\"",
            $"dir /b \"{fx.Staged}\" > \"{outFile}\"",
            $"echo DONE > \"{done}\"");

        using (fx.Vfs())
        {
            if (!RunHookedBat(bat, 15_000)) { Console.WriteLine("[enum] >>> FAIL: cmd did not complete."); return 1; }
        }
        bool saw = OutContains(outFile, ModName);
        Console.WriteLine(saw
            ? "[enum] >>> PASS: virtual mod visible to a usvfs-spawned (native) process."
            : "[enum] >>> FAIL: virtual mod NOT visible to the spawned process.");
        return saw ? 0 : 1;
    }

    // ---- scenario: propagate (Q: does USVFS reach a grandchild created by a hooked proc?) ----

    private static int RunPropagate(Dictionary<string, string> opts)
    {
        using var fx = ScenarioFixture.Create(opts);
        var p1 = Path.Combine(fx.Work, "p1.txt"); // the hooked cmd's own enumeration
        var p2 = Path.Combine(fx.Work, "p2.txt"); // a CHILD cmd the hooked cmd spawns itself
        var bat = Path.Combine(fx.Work, "run.bat");
        // Line 1: hooked cmd enumerates. Line 2: hooked cmd spawns a child cmd (plain
        // CreateProcess, no usvfs call) that enumerates. If USVFS propagated via its
        // CreateProcess hook, the child sees the virtual dir too.
        WriteBat(bat,
            $"dir /b \"{fx.Staged}\" > \"{p1}\"",
            $"cmd /c dir /b \"{fx.Staged}\" > \"{p2}\"");

        using (fx.Vfs())
        {
            if (!RunHookedBat(bat, 15_000)) { Console.WriteLine("[propagate] >>> FAIL: cmd did not complete."); return 1; }
        }
        bool saw1 = OutContains(p1, ModName);
        bool saw2 = OutContains(p2, ModName);
        Console.WriteLine($"[propagate] P1 (hooked cmd) saw virtual:                       {saw1}");
        Console.WriteLine($"[propagate] P2 (child of hooked cmd, plain CreateProcess):    {saw2}");
        if (!saw1) { Console.WriteLine("[propagate] >>> FAIL: the directly-hooked process didn't see the virtual dir (enum-level failure)."); return 1; }
        Console.WriteLine(saw2
            ? "[propagate] >>> PASS: USVFS propagated to a grandchild via the hooked process's own CreateProcess."
            : "[propagate] >>> FAIL: no propagation to the grandchild.");
        return saw2 ? 0 : 1;
    }

    // ---- scenario: lifetime (Q: does the virtual view survive controller disconnect?) ----

    private static int RunLifetime(Dictionary<string, string> opts)
    {
        using var fx = ScenarioFixture.Create(opts);
        var outFile = Path.Combine(fx.Work, "out.txt");
        var bat = Path.Combine(fx.Work, "run.bat");
        // ~5s delay (ping -n 6), THEN enumerate. The controller disconnects during the wait.
        WriteBat(bat, "ping -n 6 127.0.0.1 > nul", $"dir /b \"{fx.Staged}\" > \"{outFile}\"");

        var vfs = fx.Vfs();
        var si = new UsvfsInterop.STARTUPINFOW { cb = (uint)Marshal.SizeOf<UsvfsInterop.STARTUPINFOW>() };
        var cmd = CmdPath;
        var cmdLine = $"\"{cmd}\" /c \"{bat}\"";
        Console.WriteLine($"[lifetime] spawning 5s-delayed enumerator via usvfsCreateProcessHooked: {cmdLine}");
        if (!UsvfsInterop.usvfsCreateProcessHooked(cmd, cmdLine, default, default, false, UsvfsInterop.CREATE_NEW_CONSOLE, default, null, ref si, out var pi))
        {
            Console.WriteLine($"[lifetime] usvfsCreateProcessHooked FAILED (err={Marshal.GetLastWin32Error()})");
            DumpUsvfsLog();
            vfs.Dispose();
            return 1;
        }
        Console.WriteLine($"[lifetime] spawned pid={pi.dwProcessId}; DISCONNECTING controller from VFS now (before the 5s delay ends)...");
        UsvfsInterop.usvfsDisconnectVFS();
        vfs.SuppressDisconnect();
        vfs.Dispose();

        uint w = UsvfsInterop.WaitForSingleObject(pi.hProcess, 15_000);
        if (w == UsvfsInterop.WAIT_TIMEOUT)
        {
            Console.WriteLine($"[lifetime] cmd TIMED OUT; terminating.");
            UsvfsInterop.TerminateProcess(pi.hProcess, 1);
            UsvfsInterop.WaitForSingleObject(pi.hProcess, 2000);
        }
        else { UsvfsInterop.GetExitCodeProcess(pi.hProcess, out _); }
        UsvfsInterop.CloseHandle(pi.hProcess); UsvfsInterop.CloseHandle(pi.hThread);

        bool saw = OutContains(outFile, ModName);
        Console.WriteLine(saw
            ? "[lifetime] >>> PASS: virtual view survived controller disconnect (close-Curator-after-launch stays valid)."
            : "[lifetime] >>> NOTE: virtual view did NOT survive controller disconnect (Curator would have to stay alive for the session).");
        return 0; // informational either way; not a harness error
    }

    // ---- spawn test: does ANY process exit after a hooked spawn? ----
    // No VFS, no bat, no redirect. Just: can usvfsCreateProcessHooked launch a
    // trivial native exe that then exits on its own? Distinguishes a general
    // USVFS-child hang from cmd/.NET-specific problems.

    private static int RunSpawn(Dictionary<string, string> opts)
    {
        var exePath = Require(opts, "exe");
        var args = opts.GetValueOrDefault("args", "");
        var timeout = (uint)ParseInt(opts, "timeout", 8000);
        // Create an (empty) VFS so usvfsCreateProcessHooked has a session to attach to.
        var session = new VfsSession("curator-spawn-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var si = new UsvfsInterop.STARTUPINFOW { cb = (uint)Marshal.SizeOf<UsvfsInterop.STARTUPINFOW>() };
            var cmdLine = string.IsNullOrEmpty(args) ? $"\"{exePath}\"" : $"\"{exePath}\" {args}";
            Console.WriteLine($"[spawn-test] usvfsCreateProcessHooked: {cmdLine}");
            if (!UsvfsInterop.usvfsCreateProcessHooked(exePath, cmdLine, default, default, false, UsvfsInterop.CREATE_NEW_CONSOLE, default, null, ref si, out var pi))
            {
                Console.WriteLine($"[spawn-test] usvfsCreateProcessHooked FAILED (err={Marshal.GetLastWin32Error()})");
                DumpUsvfsLog();
                return 1;
            }
            Console.WriteLine($"[spawn-test] injected pid={pi.dwProcessId}; polling up to {timeout} ms for exit...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool exited = false; uint code = 0;
            while (sw.ElapsedMilliseconds < timeout)
            {
                if (UsvfsInterop.GetExitCodeProcess(pi.hProcess, out code) && code != 259) { exited = true; break; }
                Thread.Sleep(100);
            }
            UsvfsInterop.CloseHandle(pi.hProcess); UsvfsInterop.CloseHandle(pi.hThread);
            Console.WriteLine(exited
                ? $"[spawn-test] >>> EXITED on its own (code={code}) after {sw.ElapsedMilliseconds} ms."
                : $"[spawn-test] >>> HUNG: did not exit within {timeout} ms.");
            return exited ? 0 : 1;
        }
        finally { session.Dispose(); }
    }

    // ---- spawn helper --------------------------------------------------------

    // Spawns `cmd /c <bat>` via usvfsCreateProcessHooked with a bounded wait.
    // Returns true only if cmd ran to completion within timeoutMs.
    private static bool RunHookedBat(string batPath, uint timeoutMs)
    {
        var cmd = CmdPath;
        var cmdLine = $"\"{cmd}\" /c \"{batPath}\"";
        var si = new UsvfsInterop.STARTUPINFOW { cb = (uint)Marshal.SizeOf<UsvfsInterop.STARTUPINFOW>() };
        Console.WriteLine($"[spawn] usvfsCreateProcessHooked: {cmdLine}");
        if (!UsvfsInterop.usvfsCreateProcessHooked(cmd, cmdLine, default, default, false, UsvfsInterop.CREATE_NEW_CONSOLE, default, null, ref si, out var pi))
        {
            Console.WriteLine($"[spawn] usvfsCreateProcessHooked FAILED (err={Marshal.GetLastWin32Error()})");
            DumpUsvfsLog();
            return false;
        }
        // Diagnostic: the main thread may be left suspended. ResumeThread returns
        // the previous suspend count (0 = was running; >0 = was suspended; -1 = error).
        uint prior = UsvfsInterop.ResumeThread(pi.hThread);
        Console.WriteLine($"[spawn] injected pid={pi.dwProcessId}; ResumeThread prior-suspend-count={prior}");
        // Responsive poll instead of a hard WaitForSingleObject: USVFS coordinates
        // via shared memory; a controller wedged in a native wait can starve it.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool exited = false;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (UsvfsInterop.GetExitCodeProcess(pi.hProcess, out uint code) && code != 259) { exited = true; break; }
            Thread.Sleep(100);
        }
        if (!exited)
        {
            Console.WriteLine($"[spawn] TIMED OUT after {timeoutMs} ms; terminating pid={pi.dwProcessId}");
            UsvfsInterop.TerminateProcess(pi.hProcess, 1);
            UsvfsInterop.WaitForSingleObject(pi.hProcess, 2000);
            UsvfsInterop.CloseHandle(pi.hProcess); UsvfsInterop.CloseHandle(pi.hThread);
            return false;
        }
        UsvfsInterop.GetExitCodeProcess(pi.hProcess, out uint exitCode);
        UsvfsInterop.CloseHandle(pi.hProcess); UsvfsInterop.CloseHandle(pi.hThread);
        Console.WriteLine($"[spawn] cmd exited code={exitCode}");
        return true;
    }

    private static void WriteBat(string path, params string[] lines)
        => File.WriteAllText(path, "@echo off\r\n" + string.Join("\r\n", lines) + "\r\n");

    private static bool OutContains(string file, string needle)
    {
        if (!File.Exists(file)) { Console.WriteLine($"[check] {Path.GetFileName(file)}: MISSING (cmd produced no output)"); return false; }
        var text = File.ReadAllText(file);
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        bool saw = lines.Any(l => l.Trim().Equals(needle, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"[check] {Path.GetFileName(file)}: {(saw ? "CONTAINS" : "does NOT contain")} '{needle}'");
        foreach (var l in lines) Console.WriteLine($"        | {l.TrimEnd()}");
        return saw;
    }

    // ---- auxiliary modes (documented; scenarios use cmd instead) -------------

    private static int DoVersion()
    {
        var p = UsvfsInterop.usvfsVersionString();
        if (p == IntPtr.Zero) { Console.WriteLine("usvfsVersionString returned NULL"); return 1; }
        Console.WriteLine($"usvfs version: {Marshal.PtrToStringAnsi(p)}");
        return 0;
    }

    // enumerate <dir>, report. Hangs under USVFS injection (managed runtime); kept
    // for running WITHOUT usvfs (e.g. `target --dir <real dir>`) as a sanity check.
    private static int RunTarget(Dictionary<string, string> opts)
    {
        var dir = Require(opts, "dir");
        var expect = Require(opts, "expect");
        var tag = opts.GetValueOrDefault("tag", "target");
        var stayMs = ParseInt(opts, "stay", 0);
        if (stayMs > 0) { Console.WriteLine($"[{tag}] sleeping {stayMs} ms..."); Thread.Sleep(stayMs); }
        Console.WriteLine($"[{tag}] enumerating: {dir}");
        string[] top;
        try { top = Directory.GetFileSystemEntries(dir); }
        catch (Exception ex) { Console.WriteLine($"[{tag}] ENUMERATE FAILED: {ex.Message}"); return 3; }
        Array.Sort(top, StringComparer.OrdinalIgnoreCase);
        foreach (var e in top) Console.WriteLine($"[{tag}]   entry: {Path.GetFileName(e)}");
        bool saw = top.Any(p => string.Equals(Path.GetFileName(p), expect, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(saw ? $"[{tag}] RESULT: '{expect}' visible." : $"[{tag}] RESULT: '{expect}' NOT visible.");
        return saw ? 0 : 1;
    }

    // plain CreateProcess(suspended)+resume of `target`. Faithful to relay launcher.c's
    // creation mode, but because it is a managed process it hangs under USVFS injection.
    // Kept for reference; the `propagate` scenario tests the same mechanism via native cmd.
    private static int RunRelayStandin(Dictionary<string, string> opts)
    {
        var staged = Require(opts, "staged");
        var expect = opts.GetValueOrDefault("expect", ModName);
        var self = Require(opts, "exe");
        var tag = opts.GetValueOrDefault("tag", "relay");
        var cmdline = $"{self} --mode target --dir \"{staged}\" --expect {expect} --tag game";
        Console.WriteLine($"[{tag}] relay-standin spawning target via PLAIN CreateProcessW(SUSPENDED): {cmdline}");
        var si = new UsvfsInterop.STARTUPINFOW { cb = (uint)Marshal.SizeOf<UsvfsInterop.STARTUPINFOW>() };
        if (!UsvfsInterop.CreateProcessW(self, cmdline, IntPtr.Zero, IntPtr.Zero, false, UsvfsInterop.CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out var pi))
        { Console.WriteLine($"[{tag}] CreateProcessW FAILED (err={Marshal.GetLastWin32Error()})"); return 5; }
        Console.WriteLine($"[{tag}] created target pid={pi.dwProcessId} (suspended); resuming");
        if (UsvfsInterop.ResumeThread(pi.hThread) == uint.MaxValue)
        { Console.WriteLine($"[{tag}] ResumeThread FAILED"); UsvfsInterop.CloseHandle(pi.hProcess); UsvfsInterop.CloseHandle(pi.hThread); return 6; }
        UsvfsInterop.WaitForSingleObject(pi.hProcess, UsvfsInterop.INFINITE);
        UsvfsInterop.GetExitCodeProcess(pi.hProcess, out uint code);
        UsvfsInterop.CloseHandle(pi.hProcess); UsvfsInterop.CloseHandle(pi.hThread);
        Console.WriteLine($"[{tag}] target exited code={code}");
        return (int)code;
    }

    // ---- diagnostics + small helpers ----------------------------------------

    private static void DumpUsvfsLog()
    {
        var buf = new byte[8192];
        while (UsvfsInterop.usvfsGetLogMessages(buf, (ulong)buf.Length, blocking: false))
        {
            int end = Array.IndexOf(buf, (byte)0);
            int len = end < 0 ? buf.Length : end;
            if (len == 0) break;
            Console.WriteLine($"[usvfs-log] {System.Text.Encoding.ASCII.GetString(buf, 0, len).Trim()}");
            Array.Clear(buf);
        }
    }

    private static Dictionary<string, string> ParseOpts(ReadOnlySpan<string> args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < args.Length; i += 2) d[args[i].TrimStart('-')] = args[i + 1];
        return d;
    }

    private static string Require(Dictionary<string, string> opts, string key)
        => opts.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} required");

    private static int ParseInt(Dictionary<string, string> opts, string key, int def)
        => opts.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;

    // ---- fixture: scratch dirs + a real "source mod" + VFS linked onto staged --

    private sealed class ScenarioFixture : IDisposable
    {
        public string Work { get; }
        public string Staged { get; }
        private readonly string _source;

        private ScenarioFixture(string work)
        {
            Work = work;
            Staged = Path.Combine(work, "staged");
            _source = Path.Combine(work, "source", ModName);
            if (Directory.Exists(Work)) Directory.Delete(Work, recursive: true);
            Directory.CreateDirectory(Staged);
            Directory.CreateDirectory(_source);
            File.WriteAllText(Path.Combine(_source, "marker.txt"), "hello from the REAL source mod");
            Directory.CreateDirectory(Path.Combine(_source, "scripts"));
            File.WriteAllText(Path.Combine(_source, "scripts", "init.lua"), "-- mod init");
        }

        public static ScenarioFixture Create(Dictionary<string, string> opts)
        {
            var work = opts.GetValueOrDefault("work",
                Path.Combine(Path.GetTempPath(), "usvfs-spike-" + Guid.NewGuid().ToString("N")[..8]));
            var fx = new ScenarioFixture(work);
            Console.WriteLine($"[spike] work dir:      {work}");
            Console.WriteLine($"[spike] real source:   {fx._source}");
            Console.WriteLine($"[spike] virtual dest:  {Path.Combine(fx.Staged, ModName)} (must NOT exist physically)");
            return fx;
        }

        // Caller owns the returned session; the fixture does not track it.
        public VfsSession Vfs()
        {
            var session = new VfsSession("curator-spike-" + Guid.NewGuid().ToString("N")[..8]);
            session.LinkDirectory(_source, Path.Combine(Staged, ModName));
            return session;
        }

        public void Dispose()
        {
            if (Environment.GetEnvironmentVariable("USVFS_SPIKE_KEEP") == "1")
            {
                Console.WriteLine($"[spike] USVFS_SPIKE_KEEP=1: leaving work dir {Work}");
                return;
            }
            try { if (Directory.Exists(Work)) Directory.Delete(Work, recursive: true); } catch { }
        }
    }

    private sealed class VfsSession : IDisposable
    {
        private IntPtr _params;
        private bool _suppressDisconnect;
        public string Instance { get; }

        public VfsSession(string instance)
        {
            Instance = instance;
            _params = UsvfsInterop.usvfsCreateParameters();
            if (_params == IntPtr.Zero) throw new InvalidOperationException("usvfsCreateParameters returned NULL");
            UsvfsInterop.usvfsSetInstanceName(_params, instance);
            UsvfsInterop.usvfsSetDebugMode(_params, true);
            UsvfsInterop.usvfsSetLogLevel(_params, 0); // Debug
            if (!UsvfsInterop.usvfsCreateVFS(_params)) throw new InvalidOperationException("usvfsCreateVFS returned false");
            Console.WriteLine($"[vfs] created VFS instance '{instance}'");
        }

        public void LinkDirectory(string source, string destination)
        {
            if (!UsvfsInterop.usvfsVirtualLinkDirectoryStatic(source, destination, UsvfsInterop.LINKFLAG_FAILIFEXISTS))
                throw new InvalidOperationException($"usvfsVirtualLinkDirectoryStatic failed: '{source}' -> '{destination}'");
            Console.WriteLine($"[vfs] linked '{source}' -> '{destination}'");
        }

        public void SuppressDisconnect() => _suppressDisconnect = true;

        public void Dispose()
        {
            if (!_suppressDisconnect)
            {
                UsvfsInterop.usvfsDisconnectVFS();
                Console.WriteLine($"[vfs] disconnected '{Instance}'");
            }
            if (_params != IntPtr.Zero) { UsvfsInterop.usvfsFreeParameters(_params); _params = IntPtr.Zero; }
        }
    }
}
