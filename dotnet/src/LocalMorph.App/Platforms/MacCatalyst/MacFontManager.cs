using UIKit;
using Font = Microsoft.Maui.Font;

namespace LocalMorph.App.Platforms.MacCatalyst;

/// <summary>
/// Mac Catalyst runs in the iPad idiom (Picker is unsupported in the Mac idiom), which renders the
/// whole UI at 77%. Shared XAML sizes are tuned for Windows, so explicit font sizes are scaled back up
/// here instead of forking every style. The bundled Open Sans text faces map to the native system
/// font (SF) so text matches other Mac apps; icon and monospace families are only scaled.
/// </summary>
internal sealed class MacFontManager(IFontRegistrar registrar, IServiceProvider services) : IFontManager
{
	internal const double Scale = 1.25;

	private readonly FontManager inner = new(registrar, services);

	public UIFont DefaultFont => inner.DefaultFont;

	public double DefaultFontSize => inner.DefaultFontSize;

	public UIFont GetFont(Font font, double defaultFontSize = 0)
	{
		var size = font.Size > 0 ? font.Size * Scale : font.Size;
		var mapped = font.Family switch
		{
			"OpenSansRegular" => Font.SystemFontOfSize(size, font.Weight, font.Slant, font.AutoScalingEnabled),
			"OpenSansSemibold" => Font.SystemFontOfSize(size, FontWeight.Semibold, font.Slant, font.AutoScalingEnabled),
			_ => font.WithSize(size)
		};
		return inner.GetFont(mapped, defaultFontSize);
	}
}
