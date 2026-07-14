using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DotNetReleaseNotesCombiner.Services;

public partial class MarkdownFetchService
{
    private const string GitHubApiRoot = "https://api.github.com/repos/dotnet/core/contents/release-notes";
    private const string MarkdownCachePrefix = "dotnet-release-notes:markdown:";
    private const string DirectoryCachePrefix = "dotnet-release-notes:directory:";

    private static Regex MarkdownLinkRegex => GetMarkdownLinkRegex();
    private static Regex MarkdownImageRegex => GetMarkdownImageRegex();
    private static Regex HtmlHeadingRegex => GetHtmlHeadingRegex();
    private static Regex MajorVersionRegex => GetMajorVersionRegex();
    private static Regex StableVersionRegex => GetStableVersionRegex();
    private static Regex PreviewDirectoryRegex => GetPreviewDirectoryRegex();
    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\((?<url>[^\)]+)\)")] private static partial Regex GetMarkdownLinkRegex();
    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<url>[^\)]+)\)")] private static partial Regex GetMarkdownImageRegex();
    [GeneratedRegex("<h(?<level>[1-4])[^>]*id=\\\"(?<id>[^\\\"\\s]+)\\\"[^>]*>(?<text>.*?)</h(?<level2>[1-4])>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex GetHtmlHeadingRegex();
    [GeneratedRegex(@"^\d+\.\d+$")] private static partial Regex GetMajorVersionRegex();
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")] private static partial Regex GetStableVersionRegex();
    [GeneratedRegex(@"^(?:preview(?<preview>\d*)|rc(?<rc>\d*))$", RegexOptions.IgnoreCase)] private static partial Regex GetPreviewDirectoryRegex();
    private static readonly MarkdownPipeline HtmlPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UsePipeTables()
        .UseAutoIdentifiers()
        .Build();

    private readonly HttpClient _httpClient;
    private readonly IJSRuntime _jsRuntime;
    private readonly NavigationManager _navigation;
    private readonly ConcurrentDictionary<string, Task<DirectoryFetchResult>> _directoryContentsCache = new(StringComparer.OrdinalIgnoreCase);

    public MarkdownFetchService(HttpClient httpClient, IJSRuntime jsRuntime, NavigationManager navigation)
    {
        _httpClient = httpClient;
        _jsRuntime = jsRuntime;
        _navigation = navigation;
    }

    public async Task<string> FetchAndCombineAsync(string relativePath, IProgress<string>? progress = null)
    {
        var rootFile = await ResolveMarkdownFileAsync(relativePath);
        if (rootFile is null)
            return string.Empty;

        progress?.Report($"Fetching root README from {rootFile.DownloadUrl}");

        var rootMarkdown = await GetMarkdownAsync(rootFile.DownloadUrl, rootFile.CacheKey, rootFile.Sha);
        var rootBasePath = GetBasePath(rootFile.DownloadUrl);
        var rootDirectory = GetParentDirectory(rootFile.RelativePath);
        var includedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeReleasePath(rootFile.RelativePath)
        };

        var subpageRelativePaths = ExtractSubpageUrls(rootMarkdown)
            .Select(url => ResolveMarkdownRelativePath(url, rootDirectory))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var combinedBuilder = new StringBuilder();
        combinedBuilder.AppendLine(rootMarkdown);

        foreach (var subpagePath in subpageRelativePaths)
        {
            var subpageFile = await ResolveMarkdownFileAsync(subpagePath);
            if (subpageFile is null)
                continue;

            var canonicalSubpagePath = NormalizeReleasePath(subpageFile.RelativePath);
            if (string.IsNullOrWhiteSpace(canonicalSubpagePath) || !includedPaths.Add(canonicalSubpagePath))
                continue;

            progress?.Report($"Fetching subpage {subpageFile.DownloadUrl}");
            var subMarkdown = await GetMarkdownAsync(subpageFile.DownloadUrl, subpageFile.CacheKey, subpageFile.Sha);
            subMarkdown = RewriteRelativeUrls(subMarkdown, GetBasePath(subpageFile.DownloadUrl));

            var githubUrl = ConvertRawToGitHubUrl(subpageFile.DownloadUrl);
            combinedBuilder.AppendLine();
            combinedBuilder.AppendLine($"*Included from [{githubUrl}]({githubUrl})*");
            combinedBuilder.AppendLine();
            combinedBuilder.AppendLine(subMarkdown);
        }

        return RewriteRelativeUrls(combinedBuilder.ToString(), rootBasePath);
    }

    public async Task<string?> ResolveLatestReleaseNotesPathAsync()
    {
        var versions = await GetAvailableVersionDirectoriesAsync();
        foreach (var version in versions)
        {
            var newest = await ResolveLatestStableOrPreviewLeafForVersionAsync(version.Name);
            if (!string.IsNullOrWhiteSpace(newest))
                return newest;
        }

        return null;
    }

    public async Task<string?> ResolveCanonicalReleasePathAsync(string relativePath)
    {
        var normalized = NormalizeReleasePath(relativePath);
        if (string.IsNullOrEmpty(normalized))
            return await ResolveLatestReleaseNotesPathAsync();

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1 && MajorVersionRegex.IsMatch(segments[0]))
            return await ResolveLatestStableOrPreviewLeafForVersionAsync(segments[0]);

        if (segments.Length == 2 && MajorVersionRegex.IsMatch(segments[0]) && string.Equals(segments[1], "preview", StringComparison.OrdinalIgnoreCase))
            return await ResolveLatestPreviewLeafForVersionAsync(segments[0]);

        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var exact = await ResolveExactMarkdownFileAsync(normalized);
            if (exact is not null)
                return exact.RelativePath;

            return null;
        }

        return await ResolveLatestMarkdownPathWithinDirectoryAsync(normalized);
    }

    public async Task<IReadOnlyList<GitHubDirectoryItem>> GetReleaseNotesDirectoryContentsAsync(string relativePath)
    {
        var normalizedPath = NormalizeReleasePath(relativePath);
        if (normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<GitHubDirectoryItem>();

        var cachedDirectory = await TryGetCachedAsync<DirectoryCacheEntry>(BuildDirectoryCacheKey(normalizedPath));
        var parentSha = await TryGetParentDirectoryShaAsync(normalizedPath);

        if (!string.IsNullOrWhiteSpace(parentSha)
            && cachedDirectory is not null
            && string.Equals(cachedDirectory.ParentSha, parentSha, StringComparison.OrdinalIgnoreCase))
            return cachedDirectory.Items;

        var apiPath = string.IsNullOrEmpty(normalizedPath) ? GitHubApiRoot : $"{GitHubApiRoot}/{normalizedPath}?ref=main";
        var etag = cachedDirectory?.ETag;
        var fetchResult = await GetGitHubDirectoryContentsAsync(apiPath, etag);

        if (fetchResult.NotModified && cachedDirectory is not null)
            return cachedDirectory.Items;

        await SetCachedAsync(BuildDirectoryCacheKey(normalizedPath), new DirectoryCacheEntry(fetchResult.ETag, parentSha, fetchResult.Items));
        return fetchResult.Items;
    }

    public string ConvertMarkdownToHtml(string markdown) => Markdown.ToHtml(markdown, HtmlPipeline);

    public IReadOnlyList<(int Level, string Text, string Id)> ExtractHeadingsFromHtml(string html)
    {
        var headings = new List<(int Level, string Text, string Id)>();
        if (string.IsNullOrWhiteSpace(html))
            return headings;

        foreach (Match match in HtmlHeadingRegex.Matches(html))
        {
            if (match.Groups["level"].Value == match.Groups["level2"].Value)
            {
                var level = int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture);
                var text = Regex.Replace(match.Groups["text"].Value, "<.*?>", string.Empty).Trim();
                text = WebUtility.HtmlDecode(text);
                text = Regex.Replace(text, "\\\\([#&])", "$1");

                var id = WebUtility.HtmlDecode(match.Groups["id"].Value.Trim());
                id = Regex.Replace(id, "\\\\([#&])", "$1");

                if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(id))
                    headings.Add((level, text, id));
            }
        }

        return headings;
    }

    private async Task<IReadOnlyList<GitHubDirectoryItem>> GetAvailableVersionDirectoriesAsync() =>
        (await GetReleaseNotesDirectoryContentsAsync(string.Empty))
            .Where(item => item.Type == "dir" && MajorVersionRegex.IsMatch(item.Name))
            .OrderByDescending(item => Version.Parse(item.Name))
            .ToArray();

    private Task<string?> ResolveLatestStableLeafForVersionAsync(string version) =>
        ResolveLatestLeafAsync(
            version,
            item => StableVersionRegex.IsMatch(item.Name),
            directories => directories.OrderByDescending(item => Version.Parse(item.Name)));

    private Task<string?> ResolveLatestPreviewLeafForVersionAsync(string version) =>
        ResolveLatestLeafAsync(
            $"{version}/preview",
            _ => true,
            directories => directories
                .OrderByDescending(item => GetPreviewSortKey(item.Name))
                .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase));

    private async Task<string?> ResolveLatestLeafAsync(
        string parentPath,
        Func<GitHubDirectoryItem, bool> directoryFilter,
        Func<IEnumerable<GitHubDirectoryItem>, IOrderedEnumerable<GitHubDirectoryItem>> orderDirectories)
    {
        var items = await GetReleaseNotesDirectoryContentsAsync(parentPath);
        foreach (var directory in orderDirectories(items.Where(item => item.Type == "dir" && directoryFilter(item))))
        {
            var resolved = await ResolveLatestMarkdownPathWithinDirectoryAsync($"{parentPath}/{directory.Name}");
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        return null;
    }

    private async Task<string?> ResolveLatestStableOrPreviewLeafForVersionAsync(string version)
    {
        var stable = await ResolveLatestStableLeafForVersionAsync(version);
        var preview = await ResolveLatestPreviewLeafForVersionAsync(version);
        return PickNewerReleasePath(stable, preview) ?? stable ?? preview;
    }

    private static string? PickNewerReleasePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;

        if (string.IsNullOrWhiteSpace(second))
            return first;

        if (!TryParseReleaseSortKey(first, out var firstKey))
            return second;

        if (!TryParseReleaseSortKey(second, out var secondKey))
            return first;

        return firstKey.CompareTo(secondKey) >= 0 ? first : second;
    }

    private static bool TryParseReleaseSortKey(string path, out ReleaseSortKey key)
    {
        key = default;

        var segments = NormalizeReleasePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        if (!Version.TryParse(segments[0], out var majorMinor))
            return false;

        if (segments.Length >= 2 && StableVersionRegex.IsMatch(segments[1]) && Version.TryParse(segments[1], out var stableVersion))
        {
            key = new ReleaseSortKey(stableVersion.Major, stableVersion.Minor, Math.Max(stableVersion.Build, 0), 3, 0);
            return true;
        }

        if (segments.Length >= 3 && string.Equals(segments[1], "preview", StringComparison.OrdinalIgnoreCase))
        {
            var match = PreviewDirectoryRegex.Match(segments[2]);
            var (rank, number) = match.Groups["rc"].Success ? (2, ParseOrdinal(match.Groups["rc"].Value, 0))
                : match.Groups["preview"].Success ? (1, ParseOrdinal(match.Groups["preview"].Value, 0))
                : (0, 0);
            key = new ReleaseSortKey(majorMinor.Major, majorMinor.Minor, 0, rank, number);
            return true;
        }

        key = new ReleaseSortKey(majorMinor.Major, majorMinor.Minor, 0, 0, 0);
        return true;
    }

    private async Task<ResolvedMarkdownFile?> ResolveMarkdownFileAsync(string relativePath)
    {
        var normalizedPath = NormalizeReleasePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return null;

        if (normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return await ResolveExactMarkdownFileAsync(normalizedPath);

        var markdownPath = await ResolveLatestMarkdownPathWithinDirectoryAsync(normalizedPath);
        return string.IsNullOrWhiteSpace(markdownPath) ? null : await ResolveExactMarkdownFileAsync(markdownPath);
    }

    private async Task<ResolvedMarkdownFile?> ResolveExactMarkdownFileAsync(string relativePath)
    {
        var normalizedPath = NormalizeReleasePath(relativePath);
        if (string.IsNullOrEmpty(normalizedPath) || !normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return null;

        var fileName = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var items = await GetReleaseNotesDirectoryContentsAsync(GetParentDirectory(normalizedPath));
        var file = items.FirstOrDefault(item => item.Type == "file" && string.Equals(item.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (file is null || string.IsNullOrWhiteSpace(file.DownloadUrl) || string.IsNullOrWhiteSpace(file.Sha))
            return null;

        return new ResolvedMarkdownFile(normalizedPath, file.DownloadUrl, file.Sha);
    }

    private async Task<string?> ResolveLatestMarkdownPathWithinDirectoryAsync(string directoryPath)
    {
        var normalizedPath = NormalizeReleasePath(directoryPath);
        var items = await GetReleaseNotesDirectoryContentsAsync(normalizedPath);
        if (items.Count == 0)
            return null;

        var fileItems = items
            .Where(item => item.Type == "file" && item.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (fileItems.Length == 0)
            return null;

        if (!string.IsNullOrEmpty(normalizedPath))
        {
            var readme = fileItems.FirstOrDefault(item => string.Equals(item.Name, "README.md", StringComparison.OrdinalIgnoreCase));
            if (readme is not null)
                return $"{normalizedPath}/{readme.Name}";

            var leaf = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            var namedFile = fileItems.FirstOrDefault(item => string.Equals(item.Name, $"{leaf}.md", StringComparison.OrdinalIgnoreCase));
            if (namedFile is not null)
                return $"{normalizedPath}/{namedFile.Name}";
        }

        return CombinePath(normalizedPath, fileItems[0].Name);
    }

    private async Task<DirectoryFetchResult> GetGitHubDirectoryContentsAsync(string apiUrl, string? ifNoneMatch = null)
    {
        var cacheKey = string.IsNullOrWhiteSpace(ifNoneMatch)
            ? apiUrl.Trim()
            : $"{apiUrl.Trim()}|{ifNoneMatch.Trim()}";
        var fetchTask = _directoryContentsCache.GetOrAdd(cacheKey, _ => FetchGitHubDirectoryContentsAsync(apiUrl, ifNoneMatch));

        try
        {
            return await fetchTask;
        }
        catch
        {
            _directoryContentsCache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    private async Task<DirectoryFetchResult> FetchGitHubDirectoryContentsAsync(string apiUrl, string? ifNoneMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd("DotNetReleaseNotesCombiner");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            var responseEtag = response.Headers.ETag?.Tag;
            return new DirectoryFetchResult(Array.Empty<GitHubDirectoryItem>(), responseEtag ?? ifNoneMatch, true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new DirectoryFetchResult(Array.Empty<GitHubDirectoryItem>(), response.Headers.ETag?.Tag, false);

        if (!response.IsSuccessStatusCode)
        {
            var content = string.Empty;
            try
            {
                content = await response.Content.ReadAsStringAsync();
            }
            catch
            {
            }

            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining);
            response.Headers.TryGetValues("X-RateLimit-Reset", out var reset);
            var rateInfo = $"Remaining={remaining?.FirstOrDefault() ?? "?"}, Reset={reset?.FirstOrDefault() ?? "?"}";

            var remainingValue = remaining?.FirstOrDefault();
            throw new HttpRequestException(
                response.StatusCode == HttpStatusCode.Forbidden && string.Equals(remainingValue, "0", StringComparison.OrdinalIgnoreCase)
                    ? $"GitHub API rate limit exceeded. {rateInfo}. Try again later."
                    : $"GitHub API request failed: {response.StatusCode} for {apiUrl}. Content: {content}. Rate: {rateInfo}");
        }

        try
        {
            var items = await response.Content.ReadFromJsonAsync<List<GitHubDirectoryItem>>();
            return new DirectoryFetchResult(items ?? new List<GitHubDirectoryItem>(), response.Headers.ETag?.Tag, false);
        }
        catch (JsonException)
        {
            return new DirectoryFetchResult(Array.Empty<GitHubDirectoryItem>(), response.Headers.ETag?.Tag, false);
        }
    }

    private async Task<string?> TryGetParentDirectoryShaAsync(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return null;

        var parentPath = GetParentDirectory(normalizedPath);
        var parentItems = await GetReleaseNotesDirectoryContentsAsync(parentPath);
        var childName = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(childName))
            return null;

        var childDirectory = parentItems.FirstOrDefault(item =>
            string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Name, childName, StringComparison.OrdinalIgnoreCase));

        return childDirectory?.Sha;
    }

    private static string BuildDirectoryCacheKey(string path) => $"{DirectoryCachePrefix}{(string.IsNullOrWhiteSpace(path) ? "__root__" : path)}";

    private async Task<T?> TryGetCachedAsync<T>(string key)
    {
        try
        {
            var cached = await _jsRuntime.InvokeAsync<string?>("releaseNotesCache.get", key);
            return string.IsNullOrWhiteSpace(cached) ? default : JsonSerializer.Deserialize<T>(cached);
        }
        catch
        {
            return default;
        }
    }

    private async Task SetCachedAsync<T>(string key, T value)
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("releaseNotesCache.set", key, JsonSerializer.Serialize(value));
        }
        catch
        {
        }
    }

    private async Task<string> GetMarkdownAsync(string url, string? cacheKey = null, string? sha = null)
    {
        var canCache = !string.IsNullOrWhiteSpace(cacheKey) && !string.IsNullOrWhiteSpace(sha);
        var storageKey = $"{MarkdownCachePrefix}{cacheKey}";

        if (canCache)
        {
            var cached = await TryGetCachedAsync<MarkdownCacheEntry>(storageKey);
            if (cached is not null && string.Equals(cached.Sha, sha, StringComparison.OrdinalIgnoreCase))
                return cached.Markdown;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var markdown = await response.Content.ReadAsStringAsync();

        if (canCache)
            await SetCachedAsync(storageKey, new MarkdownCacheEntry(sha!, markdown));

        return markdown;
    }

    private static string GetBasePath(string url) => url[..(url.LastIndexOf('/') + 1)];

    private static string GetParentDirectory(string relativePath)
    {
        var normalized = NormalizeReleasePath(relativePath);
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;

        var index = normalized.LastIndexOf('/');
        return index >= 0 ? normalized[..index] : string.Empty;
    }

    private static string ConvertRawToGitHubUrl(string rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl) || !rawUrl.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
            return rawUrl;

        try
        {
            var parts = new Uri(rawUrl).AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
                return $"https://github.com/{parts[0]}/{parts[1]}/blob/{parts[2]}/{string.Join('/', parts.Skip(3))}";
        }
        catch
        {
        }

        return rawUrl;
    }

    private static IReadOnlyList<string> ExtractSubpageUrls(string markdown)
    {
        return MarkdownLinkRegex.Matches(markdown)
            .Select(match => match.Groups["url"].Value.Trim())
            .Where(url => url.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("#"))
            .ToArray();
    }

    private static string ResolveMarkdownRelativePath(string url, string relativeDirectory)
    {
        if (Uri.IsWellFormedUriString(url, UriKind.Absolute) || url.StartsWith("#", StringComparison.Ordinal))
            return string.Empty;

        var normalized = NormalizeReleasePath(url);
        if (string.IsNullOrEmpty(relativeDirectory) || string.IsNullOrEmpty(normalized))
            return normalized;

        return NormalizeReleasePath($"{relativeDirectory}/{normalized}");
    }

    private static string NormalizeReleasePath(string path)
    {
        var normalized = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absoluteUri))
        {
            var absolutePath = absoluteUri.AbsolutePath.Replace('\\', '/');
            normalized = TryExtractReleaseNotesPathFromAbsolutePath(absolutePath) ?? absolutePath;
        }
        else if (normalized.StartsWith("https:/", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("http:/", StringComparison.OrdinalIgnoreCase))
        {
            var recovered = normalized
                .Replace("https:/", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("http:/", "http://", StringComparison.OrdinalIgnoreCase);

            if (Uri.TryCreate(recovered, UriKind.Absolute, out var recoveredUri))
            {
                var absolutePath = recoveredUri.AbsolutePath.Replace('\\', '/');
                normalized = TryExtractReleaseNotesPathFromAbsolutePath(absolutePath) ?? absolutePath;
            }
        }

        normalized = normalized.Trim('/').Replace('\\', '/');
        normalized = Regex.Replace(normalized, @"/{2,}", "/");
        var segments = new Stack<string>();

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.Pop();
                continue;
            }

            segments.Push(segment);
        }

        return string.Join('/', segments.Reverse());
    }

    private static string? TryExtractReleaseNotesPathFromAbsolutePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;

        foreach (var marker in new[] { "/release-notes/", "/contents/release-notes/" })
        {
            var index = absolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            return WebUtility.UrlDecode(absolutePath[(index + marker.Length)..].Trim('/'));
        }

        return null;
    }

    private static int GetPreviewSortKey(string name)
    {
        var match = PreviewDirectoryRegex.Match(name);
        if (match.Groups["rc"].Success && int.TryParse(match.Groups["rc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rcNumber))
            return 2000 + rcNumber;

        if (match.Groups["preview"].Success && int.TryParse(match.Groups["preview"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var previewNumber))
            return 1000 + previewNumber;

        return 0;
    }

    private static int ParseOrdinal(string value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private string RewriteRelativeUrls(string markdown, string basePath)
    {
        markdown = MarkdownImageRegex.Replace(markdown, match =>
        {
            var url = match.Groups["url"].Value;
            return IsRelativeUrl(url)
                ? match.Value.Replace(url, new Uri(new Uri(basePath), url).ToString())
                : match.Value;
        });

        markdown = MarkdownLinkRegex.Replace(markdown, match =>
        {
            var url = match.Groups["url"].Value;
            if (IsRelativeUrl(url) && !url.StartsWith("#", StringComparison.Ordinal))
            {
                var resolved = new Uri(new Uri(basePath), url).ToString();

                try
                {
                    var uri = new Uri(resolved);
                    var path = uri.AbsolutePath ?? string.Empty;
                    var marker = "/release-notes/";
                    var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var remainder = path[(idx + marker.Length)..].Trim('/');
                        var routePath = NormalizeReleasePath(remainder);
                        var route = string.IsNullOrEmpty(routePath) ? "release-notes" : $"release-notes/{routePath}/";
                        var appHref = _navigation.ToAbsoluteUri(route).ToString();
                        return match.Value.Replace(url, appHref);
                    }
                }
                catch
                {
                }

                return match.Value.Replace(url, resolved);
            }

            return match.Value;
        });

        return markdown;
    }

    private static string CombinePath(string parent, string child) => string.IsNullOrEmpty(parent) ? child : $"{parent}/{child}";
    private static bool IsRelativeUrl(string url) => !Uri.IsWellFormedUriString(url, UriKind.Absolute);

    private sealed record MarkdownCacheEntry(
        [property: JsonPropertyName("sha")] string Sha,
        [property: JsonPropertyName("markdown")] string Markdown);

    private sealed record DirectoryCacheEntry(
        [property: JsonPropertyName("etag")] string? ETag,
        [property: JsonPropertyName("parentSha")] string? ParentSha,
        [property: JsonPropertyName("items")] IReadOnlyList<GitHubDirectoryItem> Items);

    private sealed record DirectoryFetchResult(IReadOnlyList<GitHubDirectoryItem> Items, string? ETag, bool NotModified);

    private readonly record struct ReleaseSortKey(int Major, int Minor, int Patch, int ChannelRank, int ChannelNumber) : IComparable<ReleaseSortKey>
    {
        public int CompareTo(ReleaseSortKey other) =>
            (Major, Minor, Patch, ChannelRank, ChannelNumber).CompareTo((other.Major, other.Minor, other.Patch, other.ChannelRank, other.ChannelNumber));
    }

    private sealed record ResolvedMarkdownFile(string RelativePath, string DownloadUrl, string Sha)
    {
        public string CacheKey => RelativePath;
    }

    public sealed record GitHubDirectoryItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("sha")] string? Sha = null,
        [property: JsonPropertyName("download_url")] string? DownloadUrl = null);
}
