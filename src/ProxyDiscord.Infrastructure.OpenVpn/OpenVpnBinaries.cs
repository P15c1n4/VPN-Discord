using System.Reflection;

namespace ProxyDiscord.Infrastructure.OpenVpn;

internal sealed class OpenVpnBinaries
{
    private readonly string _root;

    public OpenVpnBinaries()
    {
        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                                ?? AppContext.BaseDirectory;
        _root = Path.Combine(assemblyDirectory, "openvpn");
    }

    public string OpenVpnExe => Path.Combine(_root, "bin", "openvpn.exe");

    public string TapCtlExe => Path.Combine(_root, "bin", "tapctl.exe");

    public string DriverInf => Path.Combine(_root, "driver", "OemVista.inf");

    public bool IsAvailable => File.Exists(OpenVpnExe) && File.Exists(TapCtlExe) && File.Exists(DriverInf);

    public string DescribeMissing()
    {
        var missing = new List<string>();
        if (!File.Exists(OpenVpnExe))
        {
            missing.Add(OpenVpnExe);
        }

        if (!File.Exists(TapCtlExe))
        {
            missing.Add(TapCtlExe);
        }

        if (!File.Exists(DriverInf))
        {
            missing.Add(DriverInf);
        }

        return string.Join(", ", missing);
    }
}
