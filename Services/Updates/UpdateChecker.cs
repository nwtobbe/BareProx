/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using Markdig;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BareProx.Services.Updates
{
    public sealed class UpdateChecker : IUpdateChecker
    {
        private readonly ILogger<UpdateChecker> _log;
        private readonly IHttpClientFactory _http;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _cfg;

        private const string VersionInfoRawUrl =
            "https://raw.githubusercontent.com/nwtobbe/BareProx/master/Generated/VersionInfo.cs";
        private const string ChangelogRawUrl =
            "https://raw.githubusercontent.com/nwtobbe/BareProx/master/CHANGELOG.md";

        public UpdateChecker(
            ILogger<UpdateChecker> log,
            IHttpClientFactory http,
            IMemoryCache cache,
            IConfiguration cfg)
        {
            _log = log;
            _http = http;
            _cache = cache;
            _cfg = cfg;
        }

        public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            // Read settings from appsettings.json; if missing/disabled -> no action.
            bool enabled = _cfg.GetValue<bool?>("Updates:Enabled") ?? false;
            int freqMin = _cfg.GetValue<int?>("Updates:FrequencyMinutes") ?? 0;

            var current = GetCurrentVersionString();

            if (!enabled || freqMin <= 0)
            {
                _log.LogDebug("UpdateChecker: disabled or missing settings (Enabled={Enabled}, FrequencyMinutes={Freq}). No action.", enabled, freqMin);
                return new UpdateInfo(false, current, current, string.Empty, null);
            }

            // Clamp TTL to sensible bounds (10 minutes .. 7 days)
            var ttlMinutes = Math.Clamp(freqMin, 10, 7 * 24 * 60);
            var cacheTtl = TimeSpan.FromMinutes(ttlMinutes);

            if (!TryParseVersion(current, out var currentV))
                currentV = new Version(0, 0, 0, 0);

            // Fetch latest (cached)
            string? latest = await _cache.GetOrCreateAsync($"updates.latest:{VersionInfoRawUrl}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = cacheTtl;
                _log.LogDebug("UpdateChecker: fetching latest version (TTL {TTL}).", cacheTtl);
                return await FetchLatestVersionAsync(ct); // may return null when fetch/parse fails
            });

            if (string.IsNullOrWhiteSpace(latest))
            {
                _log.LogWarning("UpdateChecker: latest version unresolved (fetch/parse failed). Using current={Current} for UI continuity.", current);
                latest = current;
            }

            if (!TryParseVersion(latest, out var latestV))
                latestV = currentV;

            var cmp = CompareVersions(latestV, currentV);
            var isNewer = cmp > 0;

            _log.LogInformation("UpdateChecker: compare current={Curr} latest={Lat} -> cmp={Cmp}",
                current, latest, cmp);

            string whatsNewHtml = string.Empty;
            string? latestDate = null;

            if (isNewer)
            {
                var md = await _cache.GetOrCreateAsync($"updates.changelog:{ChangelogRawUrl}", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = cacheTtl;
                    _log.LogDebug("UpdateChecker: fetching changelog (TTL {TTL}).", cacheTtl);
                    return await FetchStringAsync(ChangelogRawUrl, ct);
                }) ?? string.Empty;

                (whatsNewHtml, latestDate) = BuildWhatsNewHtml(md, currentV, latestV);
            }

            return new UpdateInfo(isNewer, current, latest, whatsNewHtml, latestDate);
        }

        private string GetCurrentVersionString()
        {
            var asm = typeof(UpdateChecker).Assembly;

            // Preferred: AssemblyInformationalVersion
            var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (info.Length > 0)
            {
                var val = ((System.Reflection.AssemblyInformationalVersionAttribute)info[0]).InformationalVersion;
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            // Fallback: assembly version
            var v = asm.GetName().Version?.ToString();
            return string.IsNullOrWhiteSpace(v) ? "0.0.0" : v;
        }

        private async Task<string?> FetchLatestVersionAsync(CancellationToken ct)
        {
            var content = await FetchStringAsync(VersionInfoRawUrl, ct);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var m = Regex.Match(content,
                @"Assembly(?:File)?Version\(\s*""(?<v>\d+\.\d+\.\d+(?:\.\d+)?)""\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

            if (!m.Success)
            {
                _log.LogWarning("UpdateChecker: version attribute not found in VersionInfo.cs (len={Len}). First 120 chars: {Preview}",
                    content.Length, (content.Length > 120 ? content.Substring(0, 120) + "…" : content).Replace("\r", "\\r").Replace("\n", "\\n"));
                return null;
            }

            var latest = m.Groups["v"].Value;
            _log.LogInformation("UpdateChecker: parsed latest={Latest} from VersionInfo.cs", latest);
            return latest;
        }

        private async Task<string> FetchStringAsync(string url, CancellationToken ct)
        {
            try
            {
                var client = _http.CreateClient(nameof(UpdateChecker));
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BareProx-UpdateChecker/1.0");
                client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true
                };

                // Force HTTP/1.1 to avoid occasional HTTP/2 quirks behind proxies/load balancers
                using var req = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = new Version(1, 1)
                };

                using var resp = await client.SendAsync(req, ct);

                var code = (int)resp.StatusCode;
                var etag = resp.Headers.ETag?.Tag;
                resp.Headers.TryGetValues("Age", out var ageVals);
                var age = ageVals?.FirstOrDefault();
                resp.Headers.TryGetValues("Date", out var dateVals);
                var srvDate = dateVals?.FirstOrDefault();

                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("UpdateChecker: HTTP {Code} for {Url} (ETag={ETag}, Age={Age}, Date={Date})",
                        code, url, etag ?? "-", age ?? "-", srvDate ?? "-");
                    return string.Empty;
                }

                var s = await resp.Content.ReadAsStringAsync(ct);
                var firstLine = (s.Split('\n').FirstOrDefault() ?? string.Empty).Trim();
                _log.LogInformation("UpdateChecker: fetched {Bytes} bytes from {Url} (ETag={ETag}, Age={Age}, Date={Date}). First line: {Line}",
                    s.Length, url, etag ?? "-", age ?? "-", srvDate ?? "-", firstLine);
                return s;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "UpdateChecker: fetch failed: {Url}", url);
                return string.Empty;
            }
        }

        private static bool TryParseVersion(string? s, out Version v)
        {
            v = new Version(0, 0, 0, 0);
            if (string.IsNullOrWhiteSpace(s)) return false;

            // Support informational versions with suffixes by taking the numeric prefix.
            var core = Regex.Match(s, @"^\s*(\d+\.\d+\.\d+(?:\.\d+)?)").Groups[1].Value;
            if (string.IsNullOrEmpty(core)) return false;

            var p = core.Split('.');
            if (p.Length == 3) { v = new Version(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), 0); return true; }
            if (p.Length == 4) { v = new Version(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3])); return true; }
            return false;
        }

        private static int CompareVersions(Version a, Version b)
        {
            int c;
            if ((c = a.Major - b.Major) != 0) return c;
            if ((c = a.Minor - b.Minor) != 0) return c;
            if ((c = a.Build - b.Build) != 0) return c;
            return a.Revision - b.Revision;
        }

        private sealed record ChangelogSection(Version Ver, string Heading, string? Date, string Body);

        private static List<ChangelogSection> ParseChangelogSections(string md)
        {
            var text = md.Replace("\r\n", "\n");
            var rx = new Regex(
                @"(?m)^(?:\s*#{1,6}\s*)?(?:\[\s*(?<ver>\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z\.-]+)?)\s*\]|(?<ver>\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z\.-]+)?))(?<rest>.*)$",
                RegexOptions.Compiled);
            var rxDate = new Regex(@"\b(20\d{2}-\d{2}-\d{2})\b");

            var matches = rx.Matches(text);
            var list = new List<ChangelogSection>();

            for (int i = 0; i < matches.Count; i++)
            {
                var verToken = matches[i].Groups["ver"].Value;
                if (!TryParseVersion(verToken, out var ver)) continue;

                var headingLine = matches[i].Value.TrimEnd();
                string? date = null;
                var rest = matches[i].Groups["rest"].Value;
                var dm = rxDate.Match(rest);
                if (dm.Success) date = dm.Groups[1].Value;

                int startOfBody = matches[i].Index + matches[i].Length;
                int endOfBody = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;

                var body = text.Substring(startOfBody, Math.Max(0, endOfBody - startOfBody));
                list.Add(new ChangelogSection(ver, headingLine, date, body));
            }

            return list;
        }

        private static string NormalizeChangelogSlice(string slice)
        {
            var lines = slice.Replace("\r\n", "\n").Split('\n');
            var sb = new System.Text.StringBuilder(slice.Length + 256);

            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Added","Changed","Fixed","Removed","Deprecated","Security","Performance","DB improvements","Breaking","Improved","Misc" };

            var versionHeadRx = new Regex(@"^\s*(?:#{1,6}\s*)?(?:\[\s*)?\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z\.-]+)?(?:\s*\])?(?:\s*[-\u2013]\s*.*)?\s*$");

            bool inBullets = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();

                if (versionHeadRx.IsMatch(line))
                {
                    inBullets = false;
                    sb.AppendLine(line);
                    continue;
                }

                if (categories.Contains(line.Trim().TrimEnd(':')))
                {
                    inBullets = true;
                    sb.AppendLine("#### " + line.Trim().TrimEnd(':'));
                    continue;
                }

                if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("#"))
                {
                    sb.AppendLine(line);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    inBullets = false;
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine(inBullets ? "- " + line.Trim() : line);
            }

            return sb.ToString();
        }

        private static (string html, string? latestDate) BuildWhatsNewHtml(string changelogMd, Version current, Version latest)
        {
            if (string.IsNullOrWhiteSpace(changelogMd))
                return (string.Empty, null);

            var sections = ParseChangelogSections(changelogMd);
            if (sections.Count == 0) return (string.Empty, null);

            var wanted = sections
                .Where(s => CompareVersions(s.Ver, current) > 0 && CompareVersions(s.Ver, latest) <= 0)
                .OrderBy(s => s.Ver)
                .ToList();

            if (wanted.Count == 0) return (string.Empty, null);

            var md = new System.Text.StringBuilder();
            foreach (var s in wanted)
            {
                md.AppendLine(s.Heading);
                md.Append(s.Body);
                if (!s.Body.EndsWith("\n")) md.AppendLine();
                md.AppendLine();
            }

            var normalized = NormalizeChangelogSlice(md.ToString().Trim());
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            var html = Markdown.ToHtml(normalized, pipeline);
            var latestDate = wanted.Last().Date;

            return (html, latestDate);
        }
    }
}
