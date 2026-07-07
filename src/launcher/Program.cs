// Slim profile launcher, entry point for Steam non-steam shortcuts.
//
// Scaffold: the launch logic (resolve profile, write mods.lst, invoke
// the Relay launcher via the Steam + Relay-client libraries) is
// not yet implemented. This stub proves the project builds as a standalone
// executable so the layout matches the architecture doc.

namespace Modificus.Curator.Launcher;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine(
            "curator profile launcher: not yet implemented (scaffold).");
        return 0;
    }
}
