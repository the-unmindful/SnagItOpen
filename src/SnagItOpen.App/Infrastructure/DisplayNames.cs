using System.Text.RegularExpressions;
using SnagItOpen.Core.Capture;

namespace SnagItOpen.App.Infrastructure;

public static class DisplayNames
{
    public static string Destination(CaptureDestination destination) => destination switch
    {
        CaptureDestination.AppendBelow => "Add below current image",
        CaptureDestination.AppendRight => "Add to the right",
        CaptureDestination.AddToCanvas => "Place on the free canvas",
        CaptureDestination.NewDocument => "Start a new composition",
        CaptureDestination.CopyOnly => "Copy to clipboard only",
        _ => "Start a new composition",
    };
    public static string For(Enum value) => value is CaptureDestination destination ? Destination(destination) : Humanize(value.ToString());
    public static string Get(Enum value) => For(value);
    public static string Humanize(string value) => Regex.Replace(Regex.Replace(value, "([A-Z]+)([A-Z][a-z])", "$1 $2"), "([a-z0-9])([A-Z])", "$1 $2");
}
