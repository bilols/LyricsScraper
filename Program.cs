using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 1;
        }

        string? url = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("Error: You must provide a URL.");
            PrintHelp();
            return 2;
        }

        string? outPath = GetArgValue(args, "--out") ?? GetArgValue(args, "-o");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            // default output filename based on host + path
            try
            {
                var u = new Uri(url);
                var safeName = (u.Host + u.AbsolutePath.Replace('/', '_')).Trim('_');
                outPath = $"{safeName}.txt";
            }
            catch
            {
                outPath = "lyrics.txt";
            }
        }

        try
        {
            var html = Fetch(url).GetAwaiter().GetResult();
            var text = ExtractLyrics(html);

            if (string.IsNullOrWhiteSpace(text))
            {
                Console.Error.WriteLine("Warning: Could not confidently locate lyrics. Dumping best-effort page text.");
                text = BestEffortMainContent(html);
            }

            text = PostProcess(text);

            File.WriteAllText(outPath!, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"Saved: {Path.GetFullPath(outPath!)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed: " + ex.Message);
            return 3;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
@"Usage:
  dotnet run -- ""https://example.com/lyrics-page"" --out ""hey-joe.txt""

Description:
  Downloads a web page and extracts the likely lyrics block into a clean, printable .txt.

Notes:
  • For personal use. Respect the site's Terms of Service and copyright.
  • If automatic detection fails, the tool falls back to best-effort main content.

Options:
  --out, -o   Output text file path (default: derived from URL)
  --help, -h  Show help
");
    }

    private static string? GetArgValue(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        if (i >= 0 && i < args.Length - 1) return args[i + 1];
        var kv = args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (kv is not null) return kv[(key.Length + 1)..];
        return null;
    }

    private static async Task<string> Fetch(string url)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LyricsScraper/1.0 (+https://localhost)");

        using var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static string ExtractLyrics(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove common non-content nodes
        RemoveNodes(doc, "//script|//style|//noscript|//header|//footer|//form|//nav");

        // Heuristic 1: Known-ish patterns on popular sites (keep generic to avoid chasing CSS changes)
        var candidates = new List<HtmlNode>();

        // data-lyrics-container="true" (Genius often uses this)
        candidates.AddRange(doc.DocumentNode.SelectNodes("//*[@data-lyrics-container='true']") ?? Enumerable.Empty<HtmlNode>());

        // class contains 'lyrics' or 'lyric' or 'Lyrics__Container' (various sites)
        candidates.AddRange(doc.DocumentNode.SelectNodes("//*[contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'lyrics')]")
                            ?? Enumerable.Empty<HtmlNode>());

        candidates.AddRange(doc.DocumentNode.SelectNodes("//*[contains(@class,'Lyrics__Container')]")
                            ?? Enumerable.Empty<HtmlNode>());

        // Heuristic 2: Longest text block with lots of line breaks or bracketed section tags
        var allBlocks = doc.DocumentNode
            .SelectNodes("//*")
            ?.Where(n => n.NodeType == HtmlNodeType.Element && !n.Name.Equals("html", StringComparison.OrdinalIgnoreCase)
                                                      && !n.Name.Equals("body", StringComparison.OrdinalIgnoreCase))
            ?? Enumerable.Empty<HtmlNode>();

        var scoredBlocks = new List<(HtmlNode Node, int Score, string Text)>();

        foreach (var n in allBlocks.Concat(candidates).Distinct())
        {
            string t = CleanInnerText(n);
            if (string.IsNullOrWhiteSpace(t)) continue;

            int length = t.Length;
            int newlines = t.Count(ch => ch == '\n');
            int bracketSections = Regex.Matches(t, @"\[[^\]\r\n]{3,40}\]").Count;
            int avgLineLenPenalty = Math.Abs((int)(AverageLineLength(t) - 45)); // favor ~song-ish line lengths

            int score = length / 5 + newlines * 3 + bracketSections * 50 - avgLineLenPenalty;

            // Penalize if it contains a lot of navigation/site cruft keywords
            if (Regex.IsMatch(t, @"(cookie|privacy|sign in|subscribe|newsletter|advert|terms|policy)", RegexOptions.IgnoreCase))
                score -= 200;

            // Reward if it looks like repeated verses or [Chorus] tags
            if (Regex.IsMatch(t, @"\[(chorus|verse|bridge|outro|intro|solo)\]", RegexOptions.IgnoreCase))
                score += 120;

            // Reward if many short lines (lyrics-like)
            if (AverageLineLength(t) < 80 && newlines > 5) score += 50;

            scoredBlocks.Add((n, score, t));
        }

        var best = scoredBlocks.OrderByDescending(s => s.Score).FirstOrDefault();
        return best.Text ?? string.Empty;
    }

    private static void RemoveNodes(HtmlDocument doc, string xpath)
    {
        var nodes = doc.DocumentNode.SelectNodes(xpath);
        if (nodes is null) return;
        foreach (var n in nodes) n.Remove();
    }

    private static string CleanInnerText(HtmlNode node)
    {
        // Convert <br> to newlines to preserve line structure
        foreach (var br in node.SelectNodes(".//br") ?? Enumerable.Empty<HtmlNode>())
            br.ParentNode.ReplaceChild(HtmlTextNode.CreateNode("\n"), br);

        // For block-level elements, ensure newlines to separate lines
        var blockTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "p","div","section","article","pre","li","ul","ol","h1","h2","h3","h4","h5","h6" };

        var sb = new StringBuilder();
        void Recurse(HtmlNode n)
        {
            if (n.NodeType == HtmlNodeType.Text)
            {
                var t = WebUtility.HtmlDecode(n.InnerText);
                sb.Append(t);
                return;
            }

            if (n.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append('\n');
                return;
            }

            foreach (var child in n.ChildNodes)
            {
                Recurse(child);
            }

            if (blockTags.Contains(n.Name))
                sb.Append('\n');
        }

        Recurse(node);
        var raw = sb.ToString();

        // Normalize whitespace and collapse excessive blank lines
        raw = Regex.Replace(raw, @"\r", "");
        raw = Regex.Replace(raw, @"[ \t]+\n", "\n");
        raw = Regex.Replace(raw, @"\n{3,}", "\n\n");

        // Trim common cruft lines
        var lines = raw.Split('\n');
        var cleaned = lines
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // De-duplicate repeated site watermark lines if present
        cleaned = cleaned.Where(l => !Regex.IsMatch(l, @"(Lyrics provided by|All rights reserved|More on .+)", RegexOptions.IgnoreCase)).ToList();

        return string.Join('\n', cleaned);
    }

    private static double AverageLineLength(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return 0;
        return lines.Average(l => l.Length);
    }

    private static string BestEffortMainContent(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        RemoveNodes(doc, "//script|//style|//noscript|//header|//footer|//form|//nav");
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;

        // Pick the largest text block
        var blocks = body
            .SelectNodes(".//*")
            ?.Select(n => CleanInnerText(n))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .OrderByDescending(t => t.Length)
            .ToList() ?? new();

        var best = blocks.FirstOrDefault() ?? string.Empty;
        return PostProcess(best);
    }

    private static string PostProcess(string text)
    {
        // Keep square-bracket section headers if present; otherwise do nothing special.
        // Normalize Windows newlines for printing.
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

        // Optional: strip residual HTML entities
        text = WebUtility.HtmlDecode(text);

        // Optional: ensure UTF-8 friendly punctuation
        return text;
    }
}
