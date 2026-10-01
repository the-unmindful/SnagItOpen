using System.Reflection;
using SnagItOpen.App.Infrastructure;

namespace SnagItOpen.Windows.Tests;

public sealed class StructuredDialogTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(60, 200, true)]
    [InlineData(0, 1, false)]
    [InlineData(61, 20, false)]
    [InlineData(5, 201, false)]
    public void Interval_validation_enforces_seconds_and_frame_limits(int seconds, int frames, bool valid)
    {
        var type = typeof(Dialogs).Assembly.GetType("SnagItOpen.App.Shell.DialogWindows.DialogValidation");
        Assert.NotNull(type);
        var result = type!.GetMethod("IntervalError", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [seconds, frames]);
        Assert.Equal(valid, result is null);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void Seam_validation_requires_some_content_to_remain(int overlap, bool valid)
    {
        var type = typeof(Dialogs).Assembly.GetType("SnagItOpen.App.Shell.DialogWindows.DialogValidation");
        Assert.NotNull(type);
        var result = type!.GetMethod("SeamError")!.Invoke(null, [overlap, 100]);
        Assert.Equal(valid, result is null);
    }
}
