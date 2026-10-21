/**
 * ModelLocator.cs
 * Finds bundled model directories (models/ner, models/face, ...) in the published app, the
 * macOS .app bundle, or the source tree, so every model loader uses the same search order.
 */

namespace ScreenGuard.App.Services;

public static class ModelLocator
{
    /// <summary>
    /// Returns the first <c>models/&lt;name&gt;</c> directory that contains all
    /// <paramref name="requiredFiles"/>, or null if none does.
    /// </summary>
    public static string? Find(string name, params string[] requiredFiles)
    {
        string baseDir = AppContext.BaseDirectory;
        string cwd = Directory.GetCurrentDirectory();
        string[] candidates =
        [
            Path.Combine(baseDir, "models", name),
            Path.Combine(baseDir, "..", "Resources", "models", name),
            Path.Combine(cwd, "models", name),
            Path.Combine(cwd, "..", "models", name)
        ];

        return candidates.FirstOrDefault(dir => requiredFiles.All(f => File.Exists(Path.Combine(dir, f))));
    }
}
