// Slim profile launcher — entry point for Steam non-steam shortcuts.
//
// Phase 0 scaffold: the launch logic (resolve profile, write mods.lst, invoke
// the Enginseer launcher via the Steam + Enginseer-client libraries) is a
// later phase. This stub proves the project builds as a standalone executable
// so the layout matches the architecture doc.

namespace Magos.Modificus.Launcher;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine(
            "magos profile launcher: not yet implemented (Phase 0 scaffold).");
        return 0;
    }
}
