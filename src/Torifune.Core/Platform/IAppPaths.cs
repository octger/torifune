namespace Torifune.Core.Platform;

/// <summary>
/// アプリケーションが使用する各種ディレクトリの解決を抽象化する。
/// (インストール版/ポータブル版/他OS の差異を吸収する)
/// </summary>
public interface IAppPaths
{
    /// <summary>設定ファイル(settings.json 等)の保存先。</summary>
    string ConfigDirectory { get; }

    /// <summary>ローカルデータ(キャッシュ等)の保存先。</summary>
    string LocalDataDirectory { get; }

    /// <summary>yt-dlp / ffmpeg 等の外部ツールの配置先。</summary>
    string ToolsDirectory { get; }

    /// <summary>ログファイルの保存先。</summary>
    string LogsDirectory { get; }
}
