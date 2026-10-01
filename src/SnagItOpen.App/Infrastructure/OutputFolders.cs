using System.Diagnostics;
using System.IO;
namespace SnagItOpen.App.Infrastructure;
public static class OutputFolders
{
    public static void Show(string path)
    {
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Path.GetFullPath(path) + "\"") { UseShellExecute = true });
    }
}
