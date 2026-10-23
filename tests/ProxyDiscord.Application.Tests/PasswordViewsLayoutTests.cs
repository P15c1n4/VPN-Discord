using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Presentation.Wpf.Views;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class PasswordViewsLayoutTests
{
    [Theory]
    [InlineData("SavedCredentialsWindow.xaml", 720, 450)]
    [InlineData("SavedCredentialsWindow.xaml", 540, 350)]
    [InlineData("MainWindow.xaml", 900, 720)]
    public Task ExistingXamlFitsPasswordControlsAndManagerActions(string fileName, int width, int height) => StaTest.RunAsync(() =>
    {
        var root = FindProjectRoot();
        // Load the actual layout without its startup/network or modal event handlers.
        var document = XDocument.Load(Path.Combine(root, "src", "ProxyDiscord.Presentation.Wpf", "Views", fileName));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var windowElement = document.Root!;
        windowElement.Attribute(xaml + "Class")!.Remove();
        foreach (var element in windowElement.DescendantsAndSelf().Where(element => element.Name.NamespaceName.StartsWith("clr-namespace:")).ToArray())
            element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Discord-VPN");
        foreach (var declaration in windowElement.Attributes().Where(attribute => attribute.IsNamespaceDeclaration && attribute.Value.StartsWith("clr-namespace:")).ToArray())
            declaration.Value += ";assembly=Discord-VPN";
        string[] events = ["Click", "SelectionChanged", "PasswordChanged", "LostFocus", "GridViewColumnHeader.Click"];
        foreach (var attribute in windowElement.DescendantsAndSelf().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).ToArray())
            attribute.Remove();
        foreach (var setter in windowElement.Descendants(presentation + "EventSetter").ToArray()) setter.Remove();
        var resources = windowElement.Element(presentation + "Window.Resources");
        var existingResources = resources?.Elements().ToArray() ?? [];
        resources?.Remove();
        var colorsUri = new Uri(Path.Combine(root, "src", "ProxyDiscord.Presentation.Wpf", "Styles", "Colors.xaml")).AbsoluteUri;
        windowElement.AddFirst(new XElement(presentation + "Window.Resources",
            new XElement(presentation + "ResourceDictionary",
                new XElement(presentation + "ResourceDictionary.MergedDictionaries",
                    new XElement(presentation + "ResourceDictionary", new XAttribute("Source", colorsUri))), existingResources)));
        var window = (Window)XamlReader.Parse(document.ToString());
        window.Style = (Style)window.Resources[typeof(Window)];
        window.Width = width;
        window.Height = height;
        if (fileName == "SavedCredentialsWindow.xaml")
        {
            var grid = (DataGrid)window.FindName("EntriesGrid");
            grid.ItemsSource = new[]
            {
                new SavedServerCredential("OpenVpn:servidor-com-nome-longo.example:443", "alice", true),
                new SavedServerCredential("Sstp:public.example:443", "vpn", true)
            };
            ((PasswordField)window.FindName("SavedPassword")).Password = "test-secret";
        }
        var layout = (FrameworkElement)window.Content;
        window.Content = null;
        layout.Resources = window.Resources;
        TextElement.SetFontFamily(layout, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(layout, 13);
        layout.Measure(new Size(width, height));
        layout.Arrange(new Rect(0, 0, width, height));
        layout.UpdateLayout();
        if (fileName == "SavedCredentialsWindow.xaml")
        {
            var delete = (Button)window.FindName("DeleteButton");
            var clear = (Button)window.FindName("ClearButton");
            Assert.True(((DataGrid)window.FindName("EntriesGrid")).Columns[0].ActualWidth >= 240);
            Assert.True(delete.ActualWidth > 0 && clear.ActualWidth > 0);
            var deleteRight = delete.TranslatePoint(new Point(delete.ActualWidth, 0), layout).X;
            var clearLeft = clear.TranslatePoint(new Point(), layout).X;
            Assert.True(clearLeft >= deleteRight);
            Assert.True(clear.TranslatePoint(new Point(clear.ActualWidth, 0), layout).X < width - 90);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(layout);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Assert.True(Enumerable.Range(0, width * height).Count(index => pixels[index * 4] < 150) > 100,
            "A renderização deve conter os textos e controles, não apenas uma imagem vazia.");
        var output = Path.Combine(root, "artifacts", "ui-validation");
        Directory.CreateDirectory(output);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, $"{Path.GetFileNameWithoutExtension(fileName)}-{width}.png"))) encoder.Save(stream);
        window.Close();
        return Task.CompletedTask;
    });

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "ProxyDiscord.Presentation.Wpf", "Views", "MainWindow.xaml"))) return directory.FullName;
        throw new DirectoryNotFoundException("Pasta do projeto não encontrada para validar o XAML.");
    }
}
