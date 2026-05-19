using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services
{
    internal sealed class ZinkConnectAdBlockEngine
    {
        private const string CacheFileName = "ZinkConnectFilters.txt";
        private const string MetaFileName = "ZinkConnectFilters.meta.json";
        private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(3);
        private static readonly SemaphoreSlim LoadLock = new(1, 1);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private static readonly string[] FilterListUrls =
        {
            "https://easylist.to/easylist/easylist.txt",
            "https://easylist.to/easylist/easyprivacy.txt",
            "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters.txt"
        };

        private static CompiledFilters? _filters;

        public int BlockedCount { get; private set; }
        public bool IsReady => _filters?.IsReady == true;
        public int NetworkRuleCount => _filters?.NetworkRules.Count ?? 0;
        public int CosmeticRuleCount => _filters?.CosmeticRules.Count ?? 0;

        public void Attach(CoreWebView2 core)
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Core_WebResourceRequested;
            _ = EnsureLoadedAsync();
        }

        public async Task InjectCosmeticRulesAsync(CoreWebView2 core, string? source)
        {
            try
            {
                await EnsureLoadedAsync();

                if (_filters?.IsReady != true ||
                    string.IsNullOrWhiteSpace(source) ||
                    !Uri.TryCreate(source, UriKind.Absolute, out var uri))
                {
                    return;
                }

                var selectors = _filters.GetCosmeticSelectors(uri.Host).Take(900).ToArray();
                if (selectors.Length == 0)
                    return;

                string css = string.Join(",\n", selectors) + "\n{ display: none !important; visibility: hidden !important; }";
                string cssJson = JsonSerializer.Serialize(css);
                string script = $@"
(() => {{
    try {{
        const id = 'zink-connect-filter-css';
        let style = document.getElementById(id);
        if (!style) {{
            style = document.createElement('style');
            style.id = id;
            document.documentElement.appendChild(style);
        }}
        style.textContent = {cssJson};
    }} catch {{ }}
}})();";

                await core.ExecuteScriptAsync(script);
            }
            catch
            {
            }
        }

        private void Core_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                string uri = args.Request.Uri ?? string.Empty;
                string initiator = args.Request.Headers.GetHeader("Referer") ?? string.Empty;
                var context = args.ResourceContext;

                if (_filters?.ShouldBlock(uri, initiator, context) != true)
                    return;

                BlockedCount++;
                args.Response = sender.Environment.CreateWebResourceResponse(
                    new global::Windows.Storage.Streams.InMemoryRandomAccessStream(),
                    204,
                    "No Content",
                    "Content-Type: text/plain");
            }
            catch
            {
            }
        }

        private static async Task EnsureLoadedAsync()
        {
            if (_filters?.IsReady == true)
                return;

            await LoadLock.WaitAsync();
            try
            {
                if (_filters?.IsReady == true)
                    return;

                string filterText = await ReadOrDownloadFiltersAsync();
                _filters = Compile(filterText);
            }
            catch
            {
                _filters = Compile(FallbackFilters);
            }
            finally
            {
                LoadLock.Release();
            }
        }

        private static async Task<string> ReadOrDownloadFiltersAsync()
        {
            string folder = ApplicationData.Current.LocalFolder.Path;
            string cachePath = Path.Combine(folder, CacheFileName);
            string metaPath = Path.Combine(folder, MetaFileName);

            try
            {
                if (File.Exists(cachePath) && File.Exists(metaPath))
                {
                    var meta = JsonSerializer.Deserialize<FilterCacheMeta>(await File.ReadAllTextAsync(metaPath));
                    if (meta != null && DateTimeOffset.UtcNow - meta.DownloadedAtUtc < CacheMaxAge)
                    {
                        string cached = await File.ReadAllTextAsync(cachePath);
                        if (!string.IsNullOrWhiteSpace(cached))
                            return cached;
                    }
                }
            }
            catch
            {
            }

            var parts = new List<string>();
            foreach (string url in FilterListUrls)
            {
                try
                {
                    parts.Add(await Http.GetStringAsync(url));
                }
                catch
                {
                }
            }

            string merged = string.Join(Environment.NewLine, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            if (string.IsNullOrWhiteSpace(merged))
            {
                if (File.Exists(cachePath))
                    return await File.ReadAllTextAsync(cachePath);

                return FallbackFilters;
            }

            try
            {
                await File.WriteAllTextAsync(cachePath, merged);
                await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(new FilterCacheMeta(DateTimeOffset.UtcNow)));
            }
            catch
            {
            }

            return merged;
        }

        private static CompiledFilters Compile(string text)
        {
            var networkRules = new List<NetworkRule>();
            var exceptionRules = new List<NetworkRule>();
            var cosmeticRules = new List<CosmeticRule>();

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '!' || line[0] == '[')
                    continue;

                if (TryParseCosmeticRule(line, out var cosmetic))
                {
                    cosmeticRules.Add(cosmetic);
                    continue;
                }

                if (TryParseNetworkRule(line, out var network))
                {
                    if (network.IsException)
                        exceptionRules.Add(network);
                    else
                        networkRules.Add(network);
                }
            }

            return new CompiledFilters(networkRules, exceptionRules, cosmeticRules);
        }

        private static bool TryParseNetworkRule(string line, out NetworkRule rule)
        {
            rule = default;

            bool isException = line.StartsWith("@@", StringComparison.Ordinal);
            if (isException)
                line = line.Substring(2);

            if (line.Contains("##", StringComparison.Ordinal) ||
                line.Contains("#@#", StringComparison.Ordinal) ||
                line.Contains("#?#", StringComparison.Ordinal) ||
                line.StartsWith("/", StringComparison.Ordinal) && line.EndsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            string[] optionParts = line.Split('$', 2);
            string pattern = optionParts[0].Trim();
            string options = optionParts.Length == 2 ? optionParts[1].ToLowerInvariant() : "";

            if (pattern.Length < 3 ||
                options.Contains("csp", StringComparison.Ordinal) ||
                options.Contains("redirect", StringComparison.Ordinal) ||
                options.Contains("removeparam", StringComparison.Ordinal))
            {
                return false;
            }

            var domains = ParseDomains(options);
            var blockedTypes = ParseResourceTypes(options);
            bool thirdPartyOnly = options.Split(',').Any(o => o == "third-party");

            if (pattern.StartsWith("||", StringComparison.Ordinal))
            {
                string body = pattern.Substring(2);
                int stop = body.IndexOfAny(new[] { '^', '/', '*', '?' });
                string host = stop >= 0 ? body.Substring(0, stop) : body;
                string rest = stop >= 0 ? body.Substring(stop).Trim('^', '*') : "";

                if (host.Length < 3 || host.Contains("*", StringComparison.Ordinal))
                    return false;

                rule = new NetworkRule(isException, NetworkRuleKind.HostSuffix, host.ToLowerInvariant(), rest.ToLowerInvariant(), domains, blockedTypes, thirdPartyOnly);
                return true;
            }

            if (pattern.StartsWith("|", StringComparison.Ordinal))
            {
                rule = new NetworkRule(isException, NetworkRuleKind.Prefix, pattern.TrimStart('|').ToLowerInvariant(), "", domains, blockedTypes, thirdPartyOnly);
                return true;
            }

            var tokens = pattern
                .ToLowerInvariant()
                .Split(new[] { '*', '^' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim('|'))
                .Where(t => t.Length >= 3)
                .Take(6)
                .ToArray();

            if (tokens.Length == 0)
                return false;

            rule = new NetworkRule(isException, NetworkRuleKind.TokenSet, string.Join("\n", tokens), "", domains, blockedTypes, thirdPartyOnly);
            return true;
        }

        private static bool TryParseCosmeticRule(string line, out CosmeticRule rule)
        {
            rule = default;

            int marker = line.IndexOf("##", StringComparison.Ordinal);
            if (marker < 0 || line.Contains("#@#", StringComparison.Ordinal) || line.Contains("#?#", StringComparison.Ordinal))
                return false;

            string domainPart = line.Substring(0, marker);
            string selector = line.Substring(marker + 2).Trim();

            if (selector.Length < 2 ||
                selector.Contains("##", StringComparison.Ordinal) ||
                selector.Contains("+js", StringComparison.OrdinalIgnoreCase) ||
                selector.Contains(":xpath", StringComparison.OrdinalIgnoreCase) ||
                selector.Contains(":has-text", StringComparison.OrdinalIgnoreCase) ||
                selector.Contains(":matches-css", StringComparison.OrdinalIgnoreCase) ||
                selector.Contains(":-abp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var includeDomains = new List<string>();
            var excludeDomains = new List<string>();
            foreach (string raw in domainPart.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string domain = raw.Trim().ToLowerInvariant();
                if (domain.Length == 0)
                    continue;

                if (domain.StartsWith("~", StringComparison.Ordinal))
                    excludeDomains.Add(domain.Substring(1));
                else
                    includeDomains.Add(domain);
            }

            rule = new CosmeticRule(selector, includeDomains, excludeDomains);
            return true;
        }

        private static DomainOptions ParseDomains(string options)
        {
            foreach (string option in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!option.StartsWith("domain=", StringComparison.Ordinal))
                    continue;

                var include = new List<string>();
                var exclude = new List<string>();
                foreach (string raw in option.Substring("domain=".Length).Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    string domain = raw.Trim().ToLowerInvariant();
                    if (domain.StartsWith("~", StringComparison.Ordinal))
                        exclude.Add(domain.Substring(1));
                    else
                        include.Add(domain);
                }

                return new DomainOptions(include, exclude);
            }

            return DomainOptions.Empty;
        }

        private static HashSet<CoreWebView2WebResourceContext> ParseResourceTypes(string options)
        {
            var result = new HashSet<CoreWebView2WebResourceContext>();
            foreach (string option in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (option)
                {
                    case "script":
                        result.Add(CoreWebView2WebResourceContext.Script);
                        break;
                    case "image":
                        result.Add(CoreWebView2WebResourceContext.Image);
                        break;
                    case "stylesheet":
                        result.Add(CoreWebView2WebResourceContext.Stylesheet);
                        break;
                    case "xmlhttprequest":
                        result.Add(CoreWebView2WebResourceContext.XmlHttpRequest);
                        break;
                    case "subdocument":
                        result.Add(CoreWebView2WebResourceContext.Document);
                        break;
                    case "media":
                        result.Add(CoreWebView2WebResourceContext.Media);
                        break;
                    case "font":
                        result.Add(CoreWebView2WebResourceContext.Font);
                        break;
                }
            }

            return result;
        }

        private enum NetworkRuleKind
        {
            HostSuffix,
            Prefix,
            TokenSet
        }

        private readonly record struct NetworkRule(
            bool IsException,
            NetworkRuleKind Kind,
            string Pattern,
            string Extra,
            DomainOptions Domains,
            HashSet<CoreWebView2WebResourceContext> ResourceTypes,
            bool ThirdPartyOnly)
        {
            public bool Matches(string uri, Uri parsed, string initiatorHost, CoreWebView2WebResourceContext type)
            {
                if (ResourceTypes.Count > 0 && !ResourceTypes.Contains(type))
                    return false;

                if (!Domains.Allows(initiatorHost.Length > 0 ? initiatorHost : parsed.Host))
                    return false;

                if (ThirdPartyOnly && !IsThirdParty(parsed.Host, initiatorHost))
                    return false;

                string lowerUri = uri.ToLowerInvariant();
                string host = parsed.Host.ToLowerInvariant();
                string pathAndQuery = (parsed.AbsolutePath + parsed.Query).ToLowerInvariant();

                return Kind switch
                {
                    NetworkRuleKind.HostSuffix => (host == Pattern || host.EndsWith("." + Pattern, StringComparison.Ordinal)) &&
                                                  (string.IsNullOrEmpty(Extra) || pathAndQuery.Contains(Extra, StringComparison.Ordinal)),
                    NetworkRuleKind.Prefix => lowerUri.StartsWith(Pattern, StringComparison.Ordinal),
                    NetworkRuleKind.TokenSet => Pattern.Split('\n').All(t => lowerUri.Contains(t, StringComparison.Ordinal)),
                    _ => false
                };
            }
        }

        private readonly record struct CosmeticRule(string Selector, List<string> IncludeDomains, List<string> ExcludeDomains)
        {
            public bool AppliesTo(string host)
            {
                host = host.ToLowerInvariant();
                if (ExcludeDomains.Any(d => HostMatches(host, d)))
                    return false;

                return IncludeDomains.Count == 0 || IncludeDomains.Any(d => HostMatches(host, d));
            }
        }

        private readonly record struct DomainOptions(List<string> IncludeDomains, List<string> ExcludeDomains)
        {
            public static DomainOptions Empty { get; } = new(new List<string>(), new List<string>());

            public bool Allows(string host)
            {
                host = (host ?? "").ToLowerInvariant();
                if (ExcludeDomains.Any(d => HostMatches(host, d)))
                    return false;

                return IncludeDomains.Count == 0 || IncludeDomains.Any(d => HostMatches(host, d));
            }
        }

        private sealed class CompiledFilters
        {
            public CompiledFilters(List<NetworkRule> networkRules, List<NetworkRule> exceptionRules, List<CosmeticRule> cosmeticRules)
            {
                NetworkRules = networkRules;
                ExceptionRules = exceptionRules;
                CosmeticRules = cosmeticRules;
                IsReady = true;
            }

            public List<NetworkRule> NetworkRules { get; }
            public List<NetworkRule> ExceptionRules { get; }
            public List<CosmeticRule> CosmeticRules { get; }
            public bool IsReady { get; }

            public bool ShouldBlock(string uri, string initiator, CoreWebView2WebResourceContext type)
            {
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                    return false;

                string initiatorHost = Uri.TryCreate(initiator, UriKind.Absolute, out var initiatorUri)
                    ? initiatorUri.Host.ToLowerInvariant()
                    : "";

                if (ExceptionRules.Any(rule => rule.Matches(uri, parsed, initiatorHost, type)))
                    return false;

                return NetworkRules.Any(rule => rule.Matches(uri, parsed, initiatorHost, type));
            }

            public IEnumerable<string> GetCosmeticSelectors(string host)
            {
                return CosmeticRules
                    .Where(rule => rule.AppliesTo(host))
                    .Select(rule => rule.Selector)
                    .Distinct(StringComparer.Ordinal);
            }
        }

        private static bool HostMatches(string host, string domain)
        {
            return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        }

        private static bool IsThirdParty(string requestHost, string initiatorHost)
        {
            if (string.IsNullOrWhiteSpace(initiatorHost))
                return false;

            return !requestHost.Equals(initiatorHost, StringComparison.OrdinalIgnoreCase) &&
                   !requestHost.EndsWith("." + initiatorHost, StringComparison.OrdinalIgnoreCase) &&
                   !initiatorHost.EndsWith("." + requestHost, StringComparison.OrdinalIgnoreCase);
        }

        private sealed record FilterCacheMeta(DateTimeOffset DownloadedAtUtc);

        private const string FallbackFilters = @"
||doubleclick.net^
||googlesyndication.com^
||googleadservices.com^
||adservice.google.com^
||pagead2.googlesyndication.com^
||securepubads.g.doubleclick.net^
||static.doubleclick.net^
||ads.youtube.com^
/pagead/
/api/stats/ads
##.ytp-ad-overlay-container
##.ytp-ad-player-overlay
##ytd-ad-slot-renderer
##ytd-promoted-sparkles-web-renderer
##ytd-display-ad-renderer
";
    }
}
