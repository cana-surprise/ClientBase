using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClientBase;

/// <summary>Новая версия, найденная в релизах на GitHub.</summary>
public record UpdateInfo(Version Version, string Tag, string SetupName, string SetupUrl, string? Sha256, string Notes, string PageUrl);

/// <summary>
/// Проверка обновлений: смотрит последний релиз в открытом репозитории на GitHub и, если он новее,
/// скачивает установщик. Скачивание — только с github.com из этого репозитория, целостность файла
/// проверяется по контрольной сумме (SHA-256), которую публикует сам GitHub.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "cana-surprise/ClientBase";

    public static string ReleasePage => $"https://github.com/{Repository}/releases";

    static string LatestReleaseApi => $"https://api.github.com/repos/{Repository}/releases/latest";
    static string TrustedDownloadPrefix => $"https://github.com/{Repository}/releases/download/";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // сроки задаются на каждый запрос отдельно
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClientBase-updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Версия запущенной программы (из ClientBase.csproj, поле Version).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>
    /// Возвращает описание новой версии или null, если установлена последняя.
    /// Ошибки сети и разбора не скрываются — вызывающий решает, показывать ли их.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, TimeSpan timeout, string? apiUrl = null, CancellationToken cancellation = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeout);
        using var response = await Http.GetAsync(apiUrl ?? LatestReleaseApi, cts.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cts.Token);

        var info = ParseRelease(json);
        return info != null && info.Version > current ? info : null;
    }

    /// <summary>Разбор ответа GitHub API о релизе. Null — релиз не подходит (нет установщика или непонятная версия).</summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!TryParseTag(root.GetProperty("tag_name").GetString(), out var version)) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("BazaKlientov-Setup-", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha = digest is { } s && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? s[7..] : null;
            return new UpdateInfo(
                version,
                root.GetProperty("tag_name").GetString()!,
                name,
                url,
                sha,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "");
        }
        return null;
    }

    /// <summary>«v1.4.0» или «1.4.0» → Version(1, 4, 0).</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>Скачивать разрешено только релизные файлы этого репозитория на github.com.</summary>
    public static bool IsTrustedUrl(string? url) =>
        url != null && url.StartsWith(TrustedDownloadPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Скачивает установщик в файл и проверяет контрольную сумму. При любой ошибке файл удаляется.
    /// </summary>
    public static async Task DownloadAsync(UpdateInfo info, string targetFile, IProgress<(long Done, long Total)>? progress, CancellationToken cancellation)
    {
        if (!IsTrustedUrl(info.SetupUrl))
            throw new InvalidOperationException("Адрес обновления не относится к репозиторию программы — загрузка отменена.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        try
        {
            using (var response = await Http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var target = File.Create(targetFile);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    done += read;
                    progress?.Report((done, total));
                }
            }

            if (info.Sha256 != null)
            {
                await using var stream = File.OpenRead(targetFile);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
                if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма скачанного файла не совпала — файл повреждён и не будет запущен.");
            }
        }
        catch
        {
            try { File.Delete(targetFile); } catch (IOException) { }
            throw;
        }
    }
}
