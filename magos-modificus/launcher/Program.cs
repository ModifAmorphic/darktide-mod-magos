// Slim profile launcher, entry point for Steam non-steam shortcuts.
//
// Scaffold: the launch logic (resolve profile, write mods.lst, invoke
// the Enginseer launcher via the Steam + Enginseer-client libraries) is
// not yet implemented. This stub proves the project builds as a standalone
// executable so the layout matches the architecture doc.

namespace Magos.Modificus.Launcher;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine(
            "magos profile launcher: not yet implemented (scaffold).");
        return 0;
    }
}
