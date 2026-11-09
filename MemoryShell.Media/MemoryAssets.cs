namespace MemoryShell.Media;

public static class MemoryAssets
{
    public static bool IsAvailable => typeof(MemoryAssets).Assembly.GetManifestResourceInfo("MemoryShell.Media.UnknownX.mp3") is not null;
    public const string AudioMediaType = "audio/mpeg";

    public static Stream OpenAudio() => Open("UnknownX.mp3");
    public static Stream OpenLyrics() => Open("UnknownX.lrc");

    private static Stream Open(string name) =>
        typeof(MemoryAssets).Assembly.GetManifestResourceStream("MemoryShell.Media." + name)
        ?? throw new InvalidDataException($"未找到内置彩蛋资源 {name}。");
}
