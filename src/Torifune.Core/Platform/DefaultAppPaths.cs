namespace Torifune.Core.Platform;

/// <summary>
/// 既定のパス解決。ポータブルモード(実行ファイル隣接に "portable.txt" が存在する場合)では
/// 実行ファイル隣接の data/tools/logs を使用し、それ以外は OS 標準のユーザーフォルダを使用する。
/// </summary>
public sealed class DefaultAppPaths : IAppPaths
{
    private const string AppName = "Torifune";

    public DefaultAppPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        var portableMarker = Path.Combine(baseDir, "portable.txt");

        if (File.Exists(portableMarker))
        {
            ConfigDirectory = Path.Combine(baseDir, "data");
            LocalDataDirectory = Path.Combine(baseDir, "data");
            ToolsDirectory = Path.Combine(baseDir, "tools");
            LogsDirectory = Path.Combine(baseDir, "logs");
        }
        else
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            ConfigDirectory = Path.Combine(roaming, AppName);
            LocalDataDirectory = Path.Combine(local, AppName);
            ToolsDirectory = Path.Combine(local, AppName, "tools");
            LogsDirectory = Path.Combine(local, AppName, "logs");
        }

        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LocalDataDirectory);
        Directory.CreateDirectory(ToolsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string ConfigDirectory { get; }
    public string LocalDataDirectory { get; }
    public string ToolsDirectory { get; }
    public string LogsDirectory { get; }
}
