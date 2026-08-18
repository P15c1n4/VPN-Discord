using System.Globalization;
using System.Windows.Data;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Presentation.Wpf.Converters;

public sealed class VpnProtocolNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is VpnProtocol protocol ? protocol.DisplayName() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A conversão é apenas de exibição.");
}
