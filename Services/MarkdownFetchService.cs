using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Linq;
using Markdig;
using Microsoft.JSInterop;

namespace DotNetReleaseNotesCombiner.Services;

public class MarkdownFetchService
{
    private const string GitHubApiRoot = "https://api.github.com/repos/dotnet/core/contents/release-notes";
    private const string GitHubRawRoot = "https://raw.githubusercontent.com/dotnet/core/main/release-notes";
    private const string MarkdownCachePrefix = "dotnet-release-notes:markdown:";
    private const string DirectoryCachePrefix = "dotnet-release-notes:directory:";

    private static readonly Regex MarkdownLinkRegex = new(@"\[(?<text>[^\]]+)\]\((?<url>[^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownImageRegex = new(@"!\[(?<alt>[^\]]*)\]\((?<url>[^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex IncludedPageRegex = new(@"\*Included from \[(?<text>[^\]]+)\]\((?<url>[^\)]+)\)\*", RegexOptions.Compiled);
    private static readonly Regex MajorVersionRegex = new(@"^\d+\.\d+$", RegexOptions.Compiled);
    private static readonly Regex StableVersionRegex = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);
    private static readonly Regex PreviewDirectoryRegex = new(@"^(?:preview(?<preview>\d*)|rc(?<rc>\d*))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly IJSRuntime _jsRuntime;
    private readonly ConcurrentDictionary<string, Task<DirectoryFetchResult>> _directoryContentsCache = new(StringComparer.OrdinalIgnoreCase);

    public MarkdownFetchService(HttpClient httpClient, IJSRuntime jsRuntime)
    {
        _httpClient = httpClient;
        _jsRuntime = jsRuntime;
    }

    public async Task<string> FetchAndCombineAsync(string relativePath, IProgress<string>? progress = null)
    {
        var rootFile = await ResolveMarkdownFileAsync(relativePath);
        if (rootFile is null)
        {
            return string.Empty;
        }

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
            {
                continue;
            }

            var canonicalSubpagePath = NormalizeReleasePath(subpageFile.RelativePath);
            if (string.IsNullOrWhiteSpace(canonicalSubpagePath) || !includedPaths.Add(canonicalSubpagePath))
            {
                continue;
            }

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
            var stable = await ResolveLatestStableLeafForVersionAsync(version.Name);
            var preview = await ResolveLatestPreviewLeafForVersionAsync(version.Name);
            var newestForVersion = PickNewerReleasePath(stable, preview) ?? stable ?? preview;
            if (!string.IsNullOrWhiteSpace(newestForVersion))
            {
                return newestForVersion;
            }
        }

        return null;
    }

    public async Task<string?> ResolveCanonicalReleasePathAsync(string relativePath)
    {
        var normalized = NormalizeReleasePath(relativePath);
        if (string.IsNullOrEmpty(normalized))
        {
            return await ResolveLatestReleaseNotesPathAsync();
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1 && MajorVersionRegex.IsMatch(segments[0]))
        {
            return await ResolveLatestStableOrPreviewLeafForVersionAsync(segments[0]);
        }

        if (segments.Length == 2 && MajorVersionRegex.IsMatch(segments[0]) && string.Equals(segments[1], "preview", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveLatestPreviewLeafForVersionAsync(segments[0]);
        }

        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var exact = await ResolveExactMarkdownFileAsync(normalized);
            if (exact is not null)
            {
                return exact.RelativePath;
            }

            return null;
        }

        var latest = await ResolveLatestMarkdownPathWithinDirectoryAsync(normalized);
        return latest;
    }

    public async Task<IReadOnlyList<string>> GetAvailableReleaseNoteVersionsAsync()
    {
        var versions = await GetAvailableVersionDirectoriesAsync();
        return versions.Select(item => item.Name).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetAvailablePreviewReleasesAsync(string version)
    {
        var items = await GetReleaseNotesDirectoryContentsAsync($"{version}/preview");
        return items
            .Where(item => item.Type == "dir")
            .OrderByDescending(item => GetPreviewSortKey(item.Name))
            .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Name)
            .ToArray();
    }

    public async Task<IReadOnlyList<GitHubDirectoryItem>> GetReleaseNotesDirectoryContentsAsync(string relativePath)
    {
        var normalizedPath = NormalizeReleasePath(relativePath);
        if (normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<GitHubDirectoryItem>();
        }

        var cachedDirectory = await TryGetCachedDirectoryAsync(normalizedPath);
        var parentSha = await TryGetParentDirectoryShaAsync(normalizedPath);

        if (!string.IsNullOrWhiteSpace(parentSha)
            && cachedDirectory is not null
            && string.Equals(cachedDirectory.ParentSha, parentSha, StringComparison.OrdinalIgnoreCase))
        {
            return cachedDirectory.Items;
        }

        var apiPath = string.IsNullOrEmpty(normalizedPath) ? GitHubApiRoot : $"{GitHubApiRoot}/{normalizedPath}?ref=main";
        var etag = cachedDirectory?.ETag;
        var fetchResult = await GetGitHubDirectoryContentsAsync(apiPath, etag);

        if (fetchResult.NotModified && cachedDirectory is not null)
        {
            return cachedDirectory.Items;
        }

        await SetCachedDirectoryAsync(normalizedPath, new DirectoryCacheEntry(fetchResult.ETag, parentSha, fetchResult.Items));
        return fetchResult.Items;
    }

    public string BuildReleaseNotesRawUrl(string relativePath)
    {
        var normalizedPath = NormalizeReleasePath(relativePath);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return $"{GitHubRawRoot}/README.md";
        }

        if (normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return $"{GitHubRawRoot}/{normalizedPath}";
        }

        return $"{GitHubRawRoot}/{normalizedPath}/README.md";
    }

    public async Task<string?> TryFetchReleaseNotesMarkdownAsync(string relativePath)
    {
        var file = await ResolveMarkdownFileAsync(relativePath);
        if (file is null)
        {
            return null;
        }

        return await GetMarkdownAsync(file.DownloadUrl, file.CacheKey, file.Sha);
    }

    public string ConvertMarkdownToHtml(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseAutoIdentifiers()
            .Build();

        return Markdown.ToHtml(markdown, pipeline);
    }

    public async Task<string?> TryFetchGitHubRenderedHtmlAsync(string sourceUrl)
    {
        try
        {
            var rawUrl = NormalizeToRawGithubReadmeUrl(sourceUrl);
            var renderUrl = $"https://render.githubusercontent.com/render?url={Uri.EscapeDataString(rawUrl)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, renderUrl);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync();
            return ExtractGitHubRenderedHtmlBody(html);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<(int Level, string Text, string Id)> ExtractHeadingsFromHtml(string html)
    {
        var headings = new List<(int Level, string Text, string Id)>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return headings;
        }

        var headingRegex = new Regex("<h(?<level>[1-4])[^>]*id=\\\"(?<id>[^\\\"\\s]+)\\\"[^>]*>(?<text>.*?)</h(?<level2>[1-4])>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in headingRegex.Matches(html))
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
                {
                    headings.Add((level, text, id));
                }
            }
        }

        return headings;
    }

    public IReadOnlyList<string> ExtractIncludedPageUrls(string markdown)
    {
        var urls = new List<string>();
        foreach (Match match in IncludedPageRegex.Matches(markdown))
        {
            var url = match.Groups["url"].Value.Trim();
            if (!string.IsNullOrEmpty(url))
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    private async Task<IReadOnlyList<GitHubDirectoryItem>> GetAvailableVersionDirectoriesAsync()
    {
        var items = await GetReleaseNotesDirectoryContentsAsync(string.Empty);
        return items
            .Where(item => item.Type == "dir" && MajorVersionRegex.IsMatch(item.Name))
            .OrderByDescending(item => Version.Parse(item.Name))
            .ToArray();
    }

    private async Task<string?> ResolveLatestStableLeafForVersionAsync(string version)
    {
        var items = await GetReleaseNotesDirectoryContentsAsync(version);
        var stableDirectories = items
            .Where(item => item.Type == "dir" && StableVersionRegex.IsMatch(item.Name))
            .OrderByDescending(item => Version.Parse(item.Name))
            .ToArray();

        foreach (var stableDirectory in stableDirectories)
        {
            var resolved = await ResolveLatestMarkdownPathWithinDirectoryAsync($"{version}/{stableDirectory.Name}");
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private async Task<string?> ResolveLatestPreviewLeafForVersionAsync(string version)
    {
        var items = await GetReleaseNotesDirectoryContentsAsync($"{version}/preview");
        var previewDirectories = items
            .Where(item => item.Type == "dir")
            .OrderByDescending(item => GetPreviewSortKey(item.Name))
            .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var previewDirectory in previewDirectories)
        {
            var resolved = await ResolveLatestMarkdownPathWithinDirectoryAsync($"{version}/preview/{previewDirectory.Name}");
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
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
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        if (!TryParseReleaseSortKey(first, out var firstKey))
        {
            return second;
        }

        if (!TryParseReleaseSortKey(second, out var secondKey))
        {
            return first;
        }

        return firstKey.CompareTo(secondKey) >= 0 ? first : second;
    }

    private static bool TryParseReleaseSortKey(string path, out ReleaseSortKey key)
    {
        key = default;

        var normalized = NormalizeReleasePath(path);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        if (!Version.TryParse(segments[0], out var majorMinor))
        {
            return false;
        }

        if (segments.Length >= 2 && StableVersionRegex.IsMatch(segments[1]) && Version.TryParse(segments[1], out var stableVersion))
        {
            key = new ReleaseSortKey(stableVersion.Major, stableVersion.Minor, Math.Max(stableVersion.Build, 0), 3, 0);
            return true;
        }

        if (segments.Length >= 3 && string.Equals(segments[1], "preview", StringComparison.OrdinalIgnoreCase))
        {
            var match = PreviewDirectoryRegex.Match(segments[2]);
            if (!match.Success)
            {
                key = new ReleaseSortKey(majorMinor.Major, majorMinor.Minor, 0, 0, 0);
                return true;
            }

            if (match.Groups["rc"].Success)
            {
                var rc = ParseOrdinal(match.Groups["rc"].Value, 0);
                key = new ReleaseSortKey(majorMinor.Major, majorMinor.Minor, 0, 2, rc);
                return true;
            }

            var preview = ParseOrdinal(match.Groups["preview"].Value, 0);
            key = new ReleaseSortKey(majorMinor.Major, majorMinor.Minor, 0, 1, preview);
            return true;
        }

        key = new ReleaseSortKey(majorMinor.Major, majorMinor.Minor, 0, 0, 0);
        return true;
    }

    private async Task<ResolvedMarkdownFile?> ResolveMarkdownFileAsync(string relativePath)
    {
        var normalizedPath = NormalizeReleasePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        if (normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveExactMarkdownFileAsync(normalizedPath);
        }

        var markdownPathInDirectory = await ResolveLatestMarkdownPathWithinDirectoryAsync(normalizedPath);
        if (string.IsNullOrWhiteSpace(markdownPathInDirectory))
        {
            return null;
        }

        return await ResolveExactMarkdownFileAsync(markdownPathInDirectory);
    }

    private async Task<ResolvedMarkdownFile?> ResolveExactMarkdownFileAsync(string relativePath)
    {
        var normalizedPath = NormalizeReleasePath(relativePath);
        if (string.IsNullOrEmpty(normalizedPath) || !normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parentPath = GetParentDirectory(normalizedPath);
        var fileName = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var items = await GetReleaseNotesDirectoryContentsAsync(parentPath);
        var file = items.FirstOrDefault(item => item.Type == "file" && string.Equals(item.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (file is null || string.IsNullOrWhiteSpace(file.DownloadUrl) || string.IsNullOrWhiteSpace(file.Sha))
        {
            return null;
        }

        return new ResolvedMarkdownFile(normalizedPath, file.DownloadUrl, file.Sha);
    }

    private async Task<string?> ResolveLatestMarkdownPathWithinDirectoryAsync(string directoryPath)
    {
        var normalizedPath = NormalizeReleasePath(directoryPath);
        var items = await GetReleaseNotesDirectoryContentsAsync(normalizedPath);
        if (items.Count == 0)
        {
            return null;
        }

        var fileItems = items
            .Where(item => item.Type == "file" && item.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (fileItems.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(normalizedPath))
        {
            var readme = fileItems.FirstOrDefault(item => string.Equals(item.Name, "README.md", StringComparison.OrdinalIgnoreCase));
            if (readme is not null)
            {
                return $"{normalizedPath}/{readme.Name}";
            }

            var leaf = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            var namedFile = fileItems.FirstOrDefault(item => string.Equals(item.Name, $"{leaf}.md", StringComparison.OrdinalIgnoreCase));
            if (namedFile is not null)
            {
                return $"{normalizedPath}/{namedFile.Name}";
            }
        }

        var fallbackMarkdown = fileItems[0];
        return string.IsNullOrEmpty(normalizedPath)
            ? fallbackMarkdown.Name
            : $"{normalizedPath}/{fallbackMarkdown.Name}";
    }

    private async Task<DirectoryFetchResult> GetGitHubDirectoryContentsAsync(string apiUrl, string? ifNoneMatch = null)
    {
        var cacheKey = string.IsNullOrWhiteSpace(ifNoneMatch)
            ? apiUrl.Trim()
            : $"{apiUrl.Trim()}|{ifNoneMatch.Trim()}";
        var fetchTask = _directoryContentsCache.GetOrAdd(cacheKey, url => FetchGitHubDirectoryContentsAsync(url));

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

    private async Task<DirectoryFetchResult> FetchGitHubDirectoryContentsAsync(string cacheKey)
    {
        var keySegments = cacheKey.Split('|', 2, StringSplitOptions.None);
        var apiUrl = keySegments[0];
        var ifNoneMatch = keySegments.Length > 1 ? keySegments[1] : null;

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd("DotNetReleaseNotesCombiner");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            var responseEtag = response.Headers.ETag?.Tag;
            return new DirectoryFetchResult(Array.Empty<GitHubDirectoryItem>(), responseEtag ?? ifNoneMatch, true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new DirectoryFetchResult(Array.Empty<GitHubDirectoryItem>(), response.Headers.ETag?.Tag, false);
        }

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
            if (response.StatusCode == HttpStatusCode.Forbidden && string.Equals(remainingValue, "0", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException($"GitHub API rate limit exceeded. {rateInfo}. Try again later.");
            }

            throw new HttpRequestException($"GitHub API request failed: {response.StatusCode} for {apiUrl}. Content: {content}. Rate: {rateInfo}");
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
        {
            return null;
        }

        var parentPath = GetParentDirectory(normalizedPath);
        var parentItems = await GetReleaseNotesDirectoryContentsAsync(parentPath);
        var childName = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(childName))
        {
            return null;
        }

        var childDirectory = parentItems.FirstOrDefault(item =>
            string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Name, childName, StringComparison.OrdinalIgnoreCase));

        return childDirectory?.Sha;
    }

    private async Task<DirectoryCacheEntry?> TryGetCachedDirectoryAsync(string normalizedPath)
    {
        try
        {
            var key = BuildDirectoryCacheKey(normalizedPath);
            var cached = await _jsRuntime.InvokeAsync<string?>("releaseNotesCache.get", key);
            if (string.IsNullOrWhiteSpace(cached))
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<DirectoryCacheEntry>(cached);
            if (entry is null)
            {
                return null;
            }

            return entry;
        }
        catch
        {
            return null;
        }
    }

    private async Task SetCachedDirectoryAsync(string normalizedPath, DirectoryCacheEntry entry)
    {
        try
        {
            var key = BuildDirectoryCacheKey(normalizedPath);
            var serialized = JsonSerializer.Serialize(entry);
            await _jsRuntime.InvokeVoidAsync("releaseNotesCache.set", key, serialized);
        }
        catch
        {
        }
    }

    private static string BuildDirectoryCacheKey(string normalizedPath)
    {
        var suffix = string.IsNullOrWhiteSpace(normalizedPath) ? "__root__" : normalizedPath;
        return $"{DirectoryCachePrefix}{suffix}";
    }

    private async Task<string> GetMarkdownAsync(string url, string? cacheKey = null, string? sha = null)
    {
        if (!string.IsNullOrWhiteSpace(cacheKey) && !string.IsNullOrWhiteSpace(sha))
        {
            var cached = await TryGetCachedMarkdownAsync(cacheKey, sha);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var markdown = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(cacheKey) && !string.IsNullOrWhiteSpace(sha))
        {
            await SetCachedMarkdownAsync(cacheKey, sha, markdown);
        }

        return markdown;
    }

    private static string NormalizeToRawGithubReadmeUrl(string sourceUrl)
    {
        if (sourceUrl.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            return sourceUrl.TrimEnd('/');
        }

        if (!sourceUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source URL must be a GitHub repository URL or raw.githubusercontent.com URL.", nameof(sourceUrl));
        }

        var normalized = sourceUrl.TrimEnd('/');
        normalized = normalized.Replace("https://github.com/", "https://raw.githubusercontent.com/");
        normalized = Regex.Replace(normalized, @"/(blob|tree)/", "/", RegexOptions.IgnoreCase);

        var uri = new Uri(normalized);
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
        {
            return $"https://raw.githubusercontent.com/{parts[0]}/{parts[1]}/{string.Join('/', parts.Skip(2))}";
        }

        throw new ArgumentException("Unable to normalize GitHub URL to raw content URL.", nameof(sourceUrl));
    }

    private static string ExtractGitHubRenderedHtmlBody(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var articleMatch = Regex.Match(html, "<article[^>]*>(.*?)</article>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (articleMatch.Success)
        {
            return articleMatch.Groups[1].Value;
        }

        var markdownBodyMatch = Regex.Match(html, "<div class=\"markdown-body[^>]*>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (markdownBodyMatch.Success)
        {
            return markdownBodyMatch.Groups[1].Value;
        }

        return html;
    }

    private static string GetBasePath(string rawUrl)
    {
        var index = rawUrl.LastIndexOf('/');
        return index >= 0 ? rawUrl[..(index + 1)] : rawUrl;
    }

    private static string GetParentDirectory(string relativePath)
    {
        var normalized = NormalizeReleasePath(relativePath);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        var index = normalized.LastIndexOf('/');
        return index >= 0 ? normalized[..index] : string.Empty;
    }

    private static string ConvertRawToGitHubUrl(string rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl))
        {
            return rawUrl;
        }

        if (rawUrl.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(rawUrl);
                var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    var owner = parts[0];
                    var repo = parts[1];
                    var branch = parts[2];
                    var path = string.Join('/', parts.Skip(3));
                    return $"https://github.com/{owner}/{repo}/blob/{branch}/{path}";
                }
            }
            catch
            {
            }
        }

        if (rawUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return rawUrl;
        }

        return rawUrl;
    }

    public IReadOnlyList<string> ExtractSubpageUrls(string markdown)
    {
        var urls = new List<string>();
        foreach (Match match in MarkdownLinkRegex.Matches(markdown))
        {
            var url = match.Groups["url"].Value.Trim();
            if (string.IsNullOrEmpty(url) || url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.StartsWith("#"))
            {
                continue;
            }

            if (url.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    private static string ResolveMarkdownRelativePath(string url, string relativeDirectory)
    {
        if (Uri.IsWellFormedUriString(url, UriKind.Absolute) || url.StartsWith("#", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var normalized = NormalizeReleasePath(url);
        if (string.IsNullOrEmpty(relativeDirectory) || string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        return NormalizeReleasePath($"{relativeDirectory}/{normalized}");
    }

    private static string NormalizeReleasePath(string path)
    {
        var normalized = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

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
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.Pop();
                }

                continue;
            }

            segments.Push(segment);
        }

        return string.Join('/', segments.Reverse());
    }

    private static string? TryExtractReleaseNotesPathFromAbsolutePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        var markers = new[]
        {
            "/release-notes/",
            "/contents/release-notes/"
        };

        foreach (var marker in markers)
        {
            var index = absolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var result = absolutePath[(index + marker.Length)..].Trim('/');
            return WebUtility.UrlDecode(result);
        }

        return null;
    }

    private async Task<string?> TryGetCachedMarkdownAsync(string cacheKey, string expectedSha)
    {
        try
        {
            var cached = await _jsRuntime.InvokeAsync<string?>("releaseNotesCache.get", $"{MarkdownCachePrefix}{cacheKey}");
            if (string.IsNullOrWhiteSpace(cached))
            {
                return null;
            }

            var cacheEntry = JsonSerializer.Deserialize<MarkdownCacheEntry>(cached);
            if (cacheEntry is null || !string.Equals(cacheEntry.Sha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return cacheEntry.Markdown;
        }
        catch
        {
            return null;
        }
    }

    private async Task SetCachedMarkdownAsync(string cacheKey, string sha, string markdown)
    {
        try
        {
            var serialized = JsonSerializer.Serialize(new MarkdownCacheEntry(sha, markdown));
            await _jsRuntime.InvokeVoidAsync("releaseNotesCache.set", $"{MarkdownCachePrefix}{cacheKey}", serialized);
        }
        catch
        {
        }
    }

    private static int GetPreviewSortKey(string name)
    {
        var match = PreviewDirectoryRegex.Match(name);
        if (!match.Success)
        {
            return 0;
        }

        if (match.Groups["rc"].Success && int.TryParse(match.Groups["rc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rcNumber))
        {
            return 2000 + rcNumber;
        }

        if (match.Groups["preview"].Success && int.TryParse(match.Groups["preview"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var previewNumber))
        {
            return 1000 + previewNumber;
        }

        return 0;
    }

    private static int ParseOrdinal(string value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static string RewriteRelativeUrls(string markdown, string basePath)
    {
        markdown = MarkdownImageRegex.Replace(markdown, match =>
        {
            var url = match.Groups["url"].Value;
            if (IsRelativeUrl(url))
            {
                return match.Value.Replace(url, new Uri(new Uri(basePath), url).ToString());
            }

            return match.Value;
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
                        var appHref = string.IsNullOrEmpty(routePath)
                            ? "/release-notes"
                            : $"/release-notes/{routePath}/";
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

    private static bool IsRelativeUrl(string url)
    {
        return !Uri.IsWellFormedUriString(url, UriKind.Absolute);
    }

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
        public int CompareTo(ReleaseSortKey other)
        {
            var majorComparison = Major.CompareTo(other.Major);
            if (majorComparison != 0)
            {
                return majorComparison;
            }

            var minorComparison = Minor.CompareTo(other.Minor);
            if (minorComparison != 0)
            {
                return minorComparison;
            }

            var patchComparison = Patch.CompareTo(other.Patch);
            if (patchComparison != 0)
            {
                return patchComparison;
            }

            var channelRankComparison = ChannelRank.CompareTo(other.ChannelRank);
            if (channelRankComparison != 0)
            {
                return channelRankComparison;
            }

            return ChannelNumber.CompareTo(other.ChannelNumber);
        }
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
