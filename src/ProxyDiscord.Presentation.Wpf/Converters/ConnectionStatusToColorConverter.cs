using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Presentation.Wpf.Converters;

public sealed class ConnectionStatusToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value switch
        {
            ConnectionStatus.Idle => "#1A1A1A",
            ConnectionStatus.Connecting => "#E6B800",
            ConnectionStatus.Connected => "#2E9E4C",
            ConnectionStatus.Error => "#D93438",
            _ => "#1A1A1A"
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ConnectionStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionStatus.Idle => "Inativo",
        ConnectionStatus.Connecting => "Conectando...",
        ConnectionStatus.Connected => "Conectado",
        ConnectionStatus.Error => "Erro",
        _ => "Inativo"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
