using Arisen.Native.RHI;

namespace ArisenEngine.Rendering;

internal static class RenderOutputEncoding
{
    public static bool RequiresExplicitSrgbEncoding(EFormat format)
    {
        return format is EFormat.FORMAT_R8G8B8A8_UNORM or EFormat.FORMAT_B8G8R8A8_UNORM;
    }
}
