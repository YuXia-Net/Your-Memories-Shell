using Microsoft.UI.Xaml.Media.Imaging;

namespace MemoryShell;

internal static class DistroLogo
{
    private static readonly Dictionary<string, SvgImageSource> sources = new();
    internal static string Name(string system)
    {
        var text = system.ToLowerInvariant();
        foreach (var name in new[] { "ubuntu", "debian", "alpine", "arch", "fedora", "mint", "rocky", "alma" })
            if (text.Contains(name)) return name switch { "alpine" => "alpinelinux", "arch" => "archlinux", "mint" => "linuxmint", "rocky" => "rockylinux", "alma" => "almalinux", _ => name };
        return "linux";
    }
    internal static SvgImageSource Source(string name)
    {
        if (!sources.TryGetValue(name, out var source)) sources[name] = source = new SvgImageSource(new Uri($"ms-appx:///Assets/Distros/{name}.svg"));
        return source;
    }
}
