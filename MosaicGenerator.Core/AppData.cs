using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MosaicGenerator.Core;

/// <summary>
/// The app's own data folder, where everything the engine keeps between runs lives (card color
/// data, and later cached card pixels). Nothing is ever written next to the user's files.
/// <para>
/// The folder is the OS's local app data folder plus the app's identifier:
/// <c>%LOCALAPPDATA%\com.mosaicgenerator.gui</c> on Windows,
/// <c>~/Library/Application Support/com.mosaicgenerator.gui</c> on macOS and
/// <c>~/.local/share/com.mosaicgenerator.gui</c> on Linux. That's the same folder Tauri uses
/// for the GUI, so on Windows the uninstaller's "Delete the application data" option removes
/// it too. The CLI uses it as well, so both share the same data.
/// </para>
/// </summary>
public static class AppData
{
    /// <summary>Must match "identifier" in MosaicGeneratorGUI/src-tauri/tauri.conf.json.</summary>
    public const string Identifier = "com.mosaicgenerator.gui";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Identifier);

    /// <summary>
    /// The data folder for one card folder: <c>cards/&lt;hash of its full path&gt;</c>.
    /// Doesn't create anything; see <see cref="EnsureCardFolderData"/>.
    /// </summary>
    public static string CardFolderData(string cardFolderPath) =>
        Path.Combine(Root, "cards", FolderKey(cardFolderPath));

    /// <summary>
    /// <see cref="CardFolderData"/>, created if needed, with a <c>folder.json</c> recording
    /// which card folder it belongs to.
    /// </summary>
    public static string EnsureCardFolderData(string cardFolderPath)
    {
        string dataFolder = CardFolderData(cardFolderPath);
        string infoPath   = Path.Combine(dataFolder, "folder.json");

        if (!File.Exists(infoPath))
        {
            Directory.CreateDirectory(dataFolder);
            WriteAllTextSafely(infoPath, JsonSerializer.Serialize(new { Path = NormalizePath(cardFolderPath) }));
        }

        return dataFolder;
    }

    /// <summary>
    /// Writes to a temporary file first and then replaces <paramref name="path"/>, so a crash
    /// or a second run writing at the same time never leaves a half-written file behind.
    /// </summary>
    public static void WriteAllTextSafely(string path, string contents)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);   // only still there if writing or moving failed
        }
    }

    // A short, stable name for a card folder. Windows and macOS paths are case-insensitive,
    // so there the path is lowercased first: "D:\Cards" and "d:\cards" share their data.
    private static string FolderKey(string cardFolderPath)
    {
        string path = NormalizePath(cardFolderPath);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            path = path.ToLowerInvariant();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexStringLower(hash, 0, 8);
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
