namespace PyMCU.Backend.Targets;

internal static class PicDiagnostics
{
    internal static void WarnNoConfigWord(string chip) =>
        Console.Error.WriteLine(
            $"warning: no config word emitted for {chip}; a blank config word will not " +
            "boot -- default fuses pending");
}
