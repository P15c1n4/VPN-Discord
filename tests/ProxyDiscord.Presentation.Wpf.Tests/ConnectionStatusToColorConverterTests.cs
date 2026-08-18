using System.Windows.Media;
using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Presentation.Wpf.Converters;

namespace ProxyDiscord.Presentation.Wpf.Tests;

public class ConnectionStatusToColorConverterTests
{
    private readonly ConnectionStatusToColorConverter _converter = new();

    [Theory]
    [InlineData(ConnectionStatus.Idle, "#FF1A1A1A")]
    [InlineData(ConnectionStatus.Connecting, "#FFE6B800")]
    [InlineData(ConnectionStatus.Connected, "#FF2E9E4C")]
    [InlineData(ConnectionStatus.Error, "#FFD93438")]
    public void Convert_EachStatus_MapsToExpectedColor(ConnectionStatus status, string expectedHex)
    {
        var brush = (SolidColorBrush)_converter.Convert(status, typeof(Brush), null, null!);

        Assert.Equal(expectedHex, brush.Color.ToString());
    }

    [Fact]
    public void Convert_DifferentStatuses_ProduceDifferentColors()
    {
        var colors = Enum.GetValues<ConnectionStatus>()
            .Select(s => ((SolidColorBrush)_converter.Convert(s, typeof(Brush), null, null!)).Color)
            .ToList();

        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => _converter.ConvertBack(null, typeof(ConnectionStatus), null, null!));
    }
}
