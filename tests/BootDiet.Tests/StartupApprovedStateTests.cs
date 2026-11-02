using BootDiet.Core;

namespace BootDiet.Tests;

public class StartupApprovedStateTests
{
    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void ReadsFirstByte(byte[] data, bool expected)
    {
        Assert.Equal(expected, StartupApprovedState.IsEnabled(data));
    }

    [Fact]
    public void MissingValueMeansEnabled()
    {
        Assert.True(StartupApprovedState.IsEnabled(null));
        Assert.True(StartupApprovedState.IsEnabled([]));
    }
}
