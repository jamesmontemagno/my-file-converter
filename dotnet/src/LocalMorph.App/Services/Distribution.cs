namespace LocalMorph.App.Services;

public static class Distribution
{
#if LOCALMORPH_STORE_BUILD
    public static bool IsStoreBuild { get; } = true;
#else
    public static bool IsStoreBuild { get; } = false;
#endif

    public static Uri StoreUpdates => new("ms-windows-store://downloadsandupdates");
}
