using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using SnagItOpen.App.Infrastructure;

namespace SnagItOpen.Windows.Tests;

public sealed class SharedControlTests
{
    private static Type Require(string name)
    {
        var type = typeof(Dialogs).Assembly.GetType(name);
        Assert.NotNull(type);
        return type!;
    }

    private static object? Invoke(string name, string method, params object?[] args) =>
        Require(name).GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);

    [Theory]
    [InlineData("-4", 0d)]
    [InlineData("130", 100d)]
    [InlineData("25.5", 25.5d)]
    public void Numeric_commit_clamps_valid_input(string text, double expected)
    {
        object?[] args = [text, 0d, 100d, CultureInfo.InvariantCulture, null];
        Assert.True((bool)Invoke("SnagItOpen.App.Controls.NumberInput", "TryParse", args)!);
        Assert.Equal(expected, args[4]);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("—")]
    public void Numeric_commit_rejects_invalid_or_mixed_input(string text)
    {
        object?[] args = [text, 0d, 100d, CultureInfo.InvariantCulture, null];
        Assert.False((bool)Invoke("SnagItOpen.App.Controls.NumberInput", "TryParse", args)!);
    }

    [Fact]
    public void Numeric_shift_step_and_mixed_display_are_explicit()
    {
        Assert.Equal(45d, Invoke("SnagItOpen.App.Controls.NumberInput", "StepValue", 25d, 2d, 1, true, 0d, 100d));
        Assert.Equal("—", Invoke("SnagItOpen.App.Controls.NumberInput", "Format", 25d, true, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IconButton_accessible_name_and_tooltip_follow_label_and_shortcut() => ThemeTokenTests.RunSta(() =>
    {
        var button = (FrameworkElement)Activator.CreateInstance(Require("SnagItOpen.App.Controls.IconButton"))!;
        button.GetType().GetProperty("Label")!.SetValue(button, "Copy");
        button.GetType().GetProperty("Shortcut")!.SetValue(button, "Ctrl+C");
        Assert.Equal("Copy (Ctrl+C)", button.ToolTip);
        Assert.Equal("Copy", AutomationProperties.GetName(button));
        Assert.Equal("Ctrl+C", AutomationProperties.GetAcceleratorKey(button));
        return true;
    });

    [Fact]
    public void Escape_reverts_numeric_edit_without_a_commit() => ThemeTokenTests.RunSta(() =>
    {
        var box = (FrameworkElement)Activator.CreateInstance(Require("SnagItOpen.App.Controls.NumberBox"))!;
        box.GetType().GetProperty("Value")!.SetValue(box, 42d);
        var input = (System.Windows.Controls.TextBox)box.GetType().GetProperty("Input")!.GetValue(box)!;
        input.Text = "67";
        box.GetType().GetMethod("Revert")!.Invoke(box, null);
        Assert.Equal("42", input.Text);
        Assert.Equal(42d, box.GetType().GetProperty("Value")!.GetValue(box));
        return true;
    });
}
