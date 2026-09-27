using SnagItOpen.Windows.Shell;

namespace SnagItOpen.Windows.Tests;

public class SingleInstanceTests
{
    [Theory]
    [InlineData("activate", true)]
    [InlineData("open\nC:\\pics\\a.png", true)]
    [InlineData("open\nC:\\pics\\a.sio", true)]
    [InlineData("open\nrelative\\a.png", false)]
    [InlineData("open\nC:\\tools\\evil.exe", false)]
    [InlineData("run\ncalc.exe", false)]
    [InlineData("activate\nextra", false)]
    [InlineData("", false)]
    public void Only_whitelisted_messages_parse(string text, bool ok) =>
        Assert.Equal(ok, InstanceMessage.TryParse(text) is not null);

    [Fact]
    public void Oversized_message_is_rejected() =>
        Assert.Null(InstanceMessage.TryParse("open\nC:\\" + new string('a', 5000) + ".png"));

    [Fact]
    public async Task Second_instance_is_refused_and_can_forward_a_message()
    {
        var id = "SnagItOpenTest-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceService.TryAcquire(id);
        Assert.NotNull(first);
        Assert.Null(SingleInstanceService.TryAcquire(id));

        var got = new TaskCompletionSource<InstanceMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        first!.StartListening(m => got.TrySetResult(m));
        bool sent = false;
        for (int i = 0; i < 20 && !sent; i++)
        {
            sent = SingleInstanceService.SendToExisting(id, new InstanceMessage(InstanceMessage.Open, @"C:\x\y.png"), TimeSpan.FromMilliseconds(250));
            if (!sent) await Task.Delay(50);
        }
        Assert.True(sent);
        var msg = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(InstanceMessage.Open, msg.Kind);
        Assert.Equal(@"C:\x\y.png", msg.Path);
    }
}
