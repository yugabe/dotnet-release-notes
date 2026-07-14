using System.Text.RegularExpressions;
using DotNetReleaseNotesCombiner.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DotNetReleaseNotesCombiner.Pages;

public partial class Releases
{
    const string WideKey = "dotnet-release-notes:ui:wide-layout", ThemeKey = "dotnet-release-notes:ui:theme", FontScaleKey = "dotnet-release-notes:ui:font-scale", ReadPrefix = "dotnet-release-notes:included-read:";
    private static Regex IncludedFrom => GetIncludedFromRegex();
    private static Regex HeadingTitle => GetHeadingTitleRegex();
    private static Regex HeadingTrailingHash => GetHeadingTrailingHashRegex();
    private static Regex ReadmeSegment => GetReadmeSegmentRegex();
    [GeneratedRegex(@"\*Included from \[(?<text>[^\]]+)\]\((?<url>[^\)]+)\)\*")] private static partial Regex GetIncludedFromRegex();
    [GeneratedRegex(@"^\s*#{1,6}\s+(.+?)\s*$", RegexOptions.Multiline)] private static partial Regex GetHeadingTitleRegex();
    [GeneratedRegex(@"\s+#+\s*$")] private static partial Regex GetHeadingTrailingHashRegex();
    [GeneratedRegex(@"/README\.md(/|$)", RegexOptions.IgnoreCase)] private static partial Regex GetReadmeSegmentRegex();

    [Parameter] public string? ReleasePath { get; set; }
    [SupplyParameterFromQuery(Name = "path")] public string? QueryPath { get; set; }
    [SupplyParameterFromQuery(Name = "fromHome")] public string? FromHome { get; set; }

    string CurrentPath = "", SourceUrl = "", ParentPath = "", ThemeMode = "auto", ErrorMessage = "", _activeHeadingId = "";
    int FontScale = 2;
    bool IsNavCollapsed, IsWideLayout, _responsiveNavInitialized, _shouldHighlight, _shouldInitScrollSpy, ShowScrollToTop, IsLoading;
    bool IsDirectoryCollapsed = true;
    DotNetObjectReference<Releases>? _dotNetRef;
    IReadOnlyList<MarkdownFetchService.GitHubDirectoryItem>? DirectoryEntries;
    IReadOnlyList<CombinedSection> CombinedSections = [];
    readonly Dictionary<string, bool> IncludedReadByUrl = new(StringComparer.Ordinal);
    IReadOnlyList<HeadingNode> HeadingTree = [];
    IReadOnlyList<(string Label, string Path)> BreadcrumbSegments = [];
    IEnumerable<MarkdownFetchService.GitHubDirectoryItem> SortedDirectoryEntries => DirectoryEntries is null ? [] : DirectoryEntries.OrderBy(x => x.Type != "dir").ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    bool HasCombinedDocument => CombinedSections.Count > 0;
    bool ShowNavigation => HasCombinedDocument && HeadingTree.Count > 0;
    bool IsNavigationVisible => ShowNavigation && !IsNavCollapsed;
    bool ShouldShowDirectory => !HasCombinedDocument || !IsDirectoryCollapsed;

    protected override async Task OnInitializedAsync()
    {
        IsWideLayout = await CacheGet(WideKey) == "1";
        var theme = await CacheGet(ThemeKey);
        ThemeMode = theme is "light" or "dark" or "auto" ? theme : "auto";
        if (int.TryParse(await CacheGet(FontScaleKey), out var scale)) FontScale = Math.Clamp(scale, 0, 4);
        await JS("releaseNotesTheme.apply", ThemeMode);
    }

    protected override async Task OnParametersSetAsync()
    {
        var uri = new Uri(NavigationManager.Uri);
        if (uri.AbsolutePath.StartsWith("/releases", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = uri.AbsolutePath["/releases".Length..];
            NavigationManager.NavigateTo($"release-notes{suffix}{uri.Query}", replace: true);
            return;
        }

        CurrentPath = NormalizePath(!string.IsNullOrWhiteSpace(ReleasePath) ? ReleasePath : QueryPath ?? "");
        if (string.IsNullOrWhiteSpace(CurrentPath) && FromHome == "1")
        {
            var canonical = NormalizePath(await MarkdownFetchService.ResolveCanonicalReleasePathAsync(CurrentPath) ?? "");
            if (!canonical.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                NavigationManager.NavigateTo(BuildReleaseUrl(canonical), replace: true);
                return;
            }
        }

        SourceUrl = "";
        ParentPath = GetParentPath(CurrentPath);
        await LoadPathAsync();
    }

    async Task LoadPathAsync()
    {
        IsLoading = true;
        var path = NormalizePath(CurrentPath);
        ParentPath = GetParentPath(path);
        ErrorMessage = "";
        try
        {
            DirectoryEntries = await MarkdownFetchService.GetReleaseNotesDirectoryContentsAsync(path);
            SourceUrl = GetSourceUrlForPath(path, DirectoryEntries);
            var markdown = await MarkdownFetchService.FetchAndCombineAsync(path);
            if (string.IsNullOrEmpty(markdown))
            {
                CombinedSections = [];
                IncludedReadByUrl.Clear();
                _activeHeadingId = "";
                HeadingTree = [];
            }
            else
            {
                CombinedSections = BuildCombinedSections(markdown).Select(x => x with { Html = MarkdownFetchService.ConvertMarkdownToHtml(x.Markdown) }).ToArray();
                await LoadReadStates(CombinedSections);
                HeadingTree = BuildHeadingTree(MarkdownFetchService.ExtractHeadingsFromHtml(string.Join(Environment.NewLine, CombinedSections.Select(x => x.Html))));
                _shouldHighlight = _shouldInitScrollSpy = true;
            }
            BreadcrumbSegments = BuildBreadcrumbSegments(path);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    string GetEntryHref(MarkdownFetchService.GitHubDirectoryItem entry)
    {
        var path = CurrentPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? GetParentPath(NormalizePath(CurrentPath)) : CurrentPath;
        return BuildReleaseUrl(string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}");
    }

    static string BuildReleaseUrl(string? path) => string.IsNullOrEmpty(path?.Trim('/')) ? "release-notes" : $"release-notes/{path.Trim('/')}/";
    static string NormalizePath(string path)
    {
        var result = path?.Trim('/') ?? "";
        if (result.Equals("README.md", StringComparison.OrdinalIgnoreCase)) return "";
        return ReadmeSegment.Replace(result, "/").Trim('/');
    }
    static string GetParentPath(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    static string DisplaySourceUrl(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url[8..] : url;
    static string GetSourceUrlForPath(string path, IReadOnlyList<MarkdownFetchService.GitHubDirectoryItem> entries)
    {
        const string root = "https://github.com/dotnet/core";
        if (string.IsNullOrWhiteSpace(path)) return $"{root}/tree/main/release-notes";
        if (entries.Count > 0) return $"{root}/tree/main/release-notes/{path}";
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? $"{root}/blob/main/release-notes/{path}" : $"{root}/blob/main/release-notes/{path}/README.md";
    }
    string GetParentHref() => BuildReleaseUrl(ParentPath);
    static string GetBreadcrumbHref(string path) => BuildReleaseUrl(path);
    static IReadOnlyList<(string Label, string Path)> BuildBreadcrumbSegments(string path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        var parts = path.Split('/');
        return parts.Select((label, i) => (label, string.Join('/', parts.Take(i + 1)))).ToArray();
    }

    void ToggleNavigation() => IsNavCollapsed = !IsNavCollapsed;
    void CloseNavigation() => IsNavCollapsed = true;
    async Task OnHeadingLinkClickAsync() { if (await IsNarrowViewport()) IsNavCollapsed = true; }
    static string ReadKey(string url) => ReadPrefix + Uri.EscapeDataString(url);
    bool IsIncludedSectionRead(string? url) => url is not null && IncludedReadByUrl.ContainsKey(url);
    async Task LoadReadStates(IEnumerable<CombinedSection> sections)
    {
        IncludedReadByUrl.Clear();
        foreach (var url in sections.Select(x => x.SourceUrl).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Cast<string>())
            if (await CacheGet(ReadKey(url)) == "1") IncludedReadByUrl[url] = true;
    }
    async Task OnIncludedReadChangedAsync(string? url, ChangeEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var read = args.Value is bool value ? value : args.Value is string text && text is "true" or "on";
        if (read) IncludedReadByUrl[url] = true; else IncludedReadByUrl.Remove(url);
        await JS(read ? "releaseNotesCache.set" : "releaseNotesCache.remove", read ? [ReadKey(url), "1"] : [ReadKey(url)]);
    }

    void ToggleDirectory() => IsDirectoryCollapsed = !IsDirectoryCollapsed;
    async Task ToggleWideLayout() { IsWideLayout = !IsWideLayout; await JS("releaseNotesCache.set", WideKey, IsWideLayout ? "1" : "0"); }
    async Task CycleFontScale() { FontScale = (FontScale + 1) % 5; await JS("releaseNotesCache.set", FontScaleKey, FontScale.ToString()); }
    string GetFontScaleTitle() => $"Text size {FontScale + 1} of 5";
    async Task CycleThemeMode()
    {
        ThemeMode = ThemeMode switch { "auto" => "light", "light" => "dark", _ => "auto" };
        await JS("releaseNotesCache.set", ThemeKey, ThemeMode);
        await JS("releaseNotesTheme.apply", ThemeMode);
    }
    string GetThemeButtonIcon() => ThemeMode switch { "light" => "☼", "dark" => "◐", _ => "A" };
    string GetThemeButtonTitle() => ThemeMode switch { "light" => "Theme: light (click to switch to dark)", "dark" => "Theme: dark (click to switch to auto)", _ => "Theme: auto (click to switch to light)" };
    Task ScrollToTop() => JS("releaseNotes.scrollToTop");
    string GetHeadingHref(string id) { var uri = new Uri(NavigationManager.Uri); return $"{uri.AbsolutePath}{uri.Query}#{Uri.EscapeDataString(id)}"; }

    static IReadOnlyList<CombinedSection> BuildCombinedSections(string markdown)
    {
        var matches = IncludedFrom.Matches(markdown);
        if (matches.Count == 0) return [new(null, ExtractSectionTitle(markdown) ?? "Release notes", markdown, "")];
        var sections = new List<CombinedSection>();
        var root = markdown[..matches[0].Index].Trim();
        if (root.Length > 0) sections.Add(new(null, ExtractSectionTitle(root) ?? "Release notes", root, ""));
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            while (start < markdown.Length && markdown[start] is '\r' or '\n') start++;
            var body = markdown[start..(i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length)].Trim();
            if (body.Length == 0) continue;
            var url = matches[i].Groups["url"].Value.Trim();
            sections.Add(new(url, ExtractSectionTitle(body) ?? SectionTitle(url), body, ""));
        }
        return sections;
    }
    static string? ExtractSectionTitle(string markdown)
    {
        var match = HeadingTitle.Match(markdown);
        if (!match.Success) return null;
        var title = HeadingTrailingHash.Replace(match.Groups[1].Value.Trim(), "");
        return title.Length == 0 ? null : title;
    }
    static string SectionTitle(string url)
    {
        try { return new Uri(url).Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } name ? name : url; }
        catch { return url; }
    }

    void ToggleHeadingNode(string id)
    {
        static bool Toggle(IEnumerable<HeadingNode> nodes, string id)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id) { node.IsExpanded = !node.IsExpanded; return true; }
                if (Toggle(node.Children, id)) return true;
            }
            return false;
        }
        Toggle(HeadingTree, id);
    }
    static bool IsNodeInActivePath(HeadingNode node, string id) => node.Id == id || node.Children.Any(x => IsNodeInActivePath(x, id));
    [JSInvokable] public void NotifyActiveHeading(string id)
    {
        if (string.IsNullOrEmpty(id) || _activeHeadingId == id) return;
        _activeHeadingId = id;
        ExpandPath(HeadingTree, id);
        StateHasChanged();
    }
    [JSInvokable] public void NotifyScrollTopVisibility(bool visible)
    {
        if (ShowScrollToTop == visible) return;
        ShowScrollToTop = visible;
        StateHasChanged();
    }
    static bool ExpandPath(IEnumerable<HeadingNode> nodes, string id)
    {
        var any = false;
        foreach (var node in nodes)
        {
            var found = node.Id == id || ExpandPath(node.Children, id);
            node.IsExpanded = found && node.Children.Count > 0;
            any |= found;
        }
        return any;
    }
    static IReadOnlyList<HeadingNode> BuildHeadingTree(IEnumerable<(int Level, string Text, string Id)> headings)
    {
        var roots = new List<HeadingNode>();
        var stack = new Stack<HeadingNode>();
        foreach (var heading in headings)
        {
            var node = new HeadingNode(Math.Clamp(heading.Level, 1, 6), heading.Text, heading.Id);
            while (stack.TryPeek(out var parent) && parent.Level >= node.Level) stack.Pop();
            if (stack.TryPeek(out var currentParent)) currentParent.Children.Add(node); else roots.Add(node);
            stack.Push(node);
        }
        return roots;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_responsiveNavInitialized && ShowNavigation)
        {
            _responsiveNavInitialized = true;
            if (await IsNarrowViewport() && !IsNavCollapsed) { IsNavCollapsed = true; StateHasChanged(); }
        }
        if (_shouldHighlight) { _shouldHighlight = false; await JSRuntime.InvokeVoidAsync("releaseNotes.highlightCodeBlocks"); }
        if (_shouldInitScrollSpy)
        {
            _shouldInitScrollSpy = false;
            _dotNetRef ??= DotNetObjectReference.Create(this);
            await JSRuntime.InvokeVoidAsync("releaseNotes.initScrollSpy", _dotNetRef, ".overview-link", ".combined-content h1, .combined-content h2, .combined-content h3, .combined-content h4");
        }
    }
    async Task<string?> CacheGet(string key) { try { return await JSRuntime.InvokeAsync<string?>("releaseNotesCache.get", key); } catch { return null; } }
    async Task<bool> IsNarrowViewport() { try { return await JSRuntime.InvokeAsync<bool>("releaseNotes.isNarrowViewport", 991); } catch { return false; } }
    async Task JS(string id, params object?[] args) { try { await JSRuntime.InvokeVoidAsync(id, args); } catch { } }

    public sealed class HeadingNode(int level, string text, string id)
    {
        public int Level { get; } = level;
        public string Text { get; } = text;
        public string Id { get; } = id;
        public List<HeadingNode> Children { get; } = [];
        public bool IsExpanded { get; set; }
    }
    sealed record CombinedSection(string? SourceUrl, string Title, string Markdown, string Html);
}
