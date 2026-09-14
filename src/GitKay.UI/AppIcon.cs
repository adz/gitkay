using Avalonia.Controls;

namespace GitKay.UI;

/// <summary>
/// The GitKay logo for window icons. Loaded from a manifest resource rather than avares://, whose lookup fails in
/// NativeAOT builds and would stop windows from being created.
/// </summary>
public static class AppIcon {
    private static WindowIcon? _window;
    private static bool _loaded;

    public static WindowIcon? Window {
        get {
            if (_loaded) return _window;
            _loaded = true;
            try {
                using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("GitKay.UI.gitkay.png");
                _window = stream == null ? null : new WindowIcon(stream);
            }
            catch (System.Exception) {
                _window = null;
            }
            return _window;
        }
    }
}
