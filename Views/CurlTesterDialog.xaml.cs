using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MultiSSH.Views;

/// <summary>
/// A lightweight connectivity tester: type a URL or a curl-style command, send it,
/// and see the status, timing, response headers and a slice of the body — or a clear
/// error if the host can't be reached. Modeless so it can sit beside live sessions.
/// </summary>
public partial class CurlTesterDialog : Window
{
    private const int BodyCap = 64 * 1024;   // never download more than this for a test

    public CurlTesterDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => UrlBox.Focus();
    }

    private sealed class Request
    {
        public string Method = "GET";
        public string Url = "";
        public readonly List<string> Headers = new();
        public string? Body;
        public bool Insecure;
        public bool FollowRedirects;
        public string? UserAgent;
        public double? MaxTimeSec;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var input = UrlBox.Text.Trim();
        if (input.Length == 0) { OutBox.Text = "Enter a URL or curl command first."; return; }

        Request req;
        try { req = Parse(input); }
        catch (Exception ex) { OutBox.Text = "✗ " + ex.Message; return; }

        if (string.IsNullOrWhiteSpace(req.Url)) { OutBox.Text = "✗ No URL found in the command."; return; }

        SendBtn.IsEnabled = false;
        OutBox.Text = $"→ {req.Method} {req.Url}\nSending…";
        try
        {
            OutBox.Text = await RunAsync(req);
        }
        catch (Exception ex)
        {
            OutBox.Text = $"→ {req.Method} {req.Url}\n✗ {Describe(ex)}";
        }
        finally
        {
            SendBtn.IsEnabled = true;
        }
    }

    // ---- request execution ----

    private async System.Threading.Tasks.Task<string> RunAsync(Request req)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = req.FollowRedirects };
        if (req.Insecure)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        using var client = new HttpClient(handler)
        {
            Timeout = System.TimeSpan.FromSeconds(req.MaxTimeSec is > 0 ? req.MaxTimeSec.Value : 100),
        };

        using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Url);
        if (req.Body != null)
            msg.Content = new StringContent(req.Body, Encoding.UTF8);

        foreach (var h in req.Headers)
        {
            int idx = h.IndexOf(':');
            if (idx <= 0) continue;
            var name = h[..idx].Trim();
            var value = h[(idx + 1)..].Trim();
            if (string.Equals(name, "Content-Type", System.StringComparison.OrdinalIgnoreCase) && msg.Content != null)
                msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
            else if (!msg.Headers.TryAddWithoutValidation(name, value))
                msg.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        if (req.UserAgent != null)
            msg.Headers.TryAddWithoutValidation("User-Agent", req.UserAgent);

        var sw = Stopwatch.StartNew();
        using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);

        var sb = new StringBuilder();
        sb.Append('→').Append(' ').Append(req.Method).Append(' ').Append(req.Url).Append('\n');

        bool head = string.Equals(req.Method, "HEAD", System.StringComparison.OrdinalIgnoreCase);
        string body = head ? "" : await ReadCappedAsync(resp);
        sw.Stop();

        int code = (int)resp.StatusCode;
        string mark = code < 400 ? "✓" : "✗";
        sb.Append($"{mark} {code} {resp.ReasonPhrase}   ({sw.ElapsedMilliseconds} ms)\n\n");

        sb.Append("Response headers:\n");
        foreach (var h in resp.Headers)
            sb.Append("  ").Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append('\n');
        foreach (var h in resp.Content.Headers)
            sb.Append("  ").Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append('\n');

        if (!head)
        {
            sb.Append('\n');
            sb.Append(body.Length >= BodyCap ? $"Body (first {BodyCap} bytes):\n" : "Body:\n");
            sb.Append(body);
        }
        return sb.ToString();
    }

    private static async System.Threading.Tasks.Task<string> ReadCappedAsync(HttpResponseMessage resp)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int total = 0, n;
        while (total < BodyCap && (n = await stream.ReadAsync(buf.AsMemory(0, System.Math.Min(buf.Length, BodyCap - total)))) > 0)
        {
            ms.Write(buf, 0, n);
            total += n;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Turn transport exceptions into a plain-English connectivity message.</summary>
    private static string Describe(Exception ex)
    {
        if (ex is TaskCanceledException)
            return "Timed out — the host did not respond in time.";
        var msg = ex.Message;
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            msg += "  →  " + inner.Message;
        return msg;
    }

    // ---- parsing ----

    private Request Parse(string input)
    {
        var req = new Request();
        var tokens = Tokenize(input);

        // Bare URL (no curl command): use the method dropdown.
        bool isCurl = tokens.Count > 0 && tokens[0].Equals("curl", System.StringComparison.OrdinalIgnoreCase);
        if (!isCurl)
        {
            req.Method = (MethodCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "GET";
            req.Url = NormalizeUrl(input);
            return req;
        }

        bool methodSet = false;
        string? Next(ref int i) => (i + 1 < tokens.Count) ? tokens[++i] : null;

        for (int i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            switch (t)
            {
                case "-X": case "--request":
                    var m = Next(ref i); if (m != null) { req.Method = m.ToUpperInvariant(); methodSet = true; } break;
                case "-I": case "--head":
                    req.Method = "HEAD"; methodSet = true; break;
                case "-H": case "--header":
                    var h = Next(ref i); if (h != null) req.Headers.Add(h); break;
                case "-d": case "--data": case "--data-raw": case "--data-binary": case "--data-ascii":
                    req.Body = Next(ref i); if (!methodSet) req.Method = "POST"; break;
                case "-A": case "--user-agent":
                    req.UserAgent = Next(ref i); break;
                case "-e": case "--referer":
                    var r = Next(ref i); if (r != null) req.Headers.Add("Referer: " + r); break;
                case "-b": case "--cookie":
                    var c = Next(ref i); if (c != null) req.Headers.Add("Cookie: " + c); break;
                case "-m": case "--max-time": case "--connect-timeout":
                    var s = Next(ref i); if (double.TryParse(s, out var sec)) req.MaxTimeSec = sec; break;
                case "-L": case "--location":
                    req.FollowRedirects = true; break;
                case "-k": case "--insecure":
                    req.Insecure = true; break;
                case "--url":
                    var u = Next(ref i); if (u != null) req.Url = NormalizeUrl(u); break;
                // Flags we accept but ignore; skip their value if they take one.
                case "-o": case "--output": case "--cacert": case "-w": case "--write-out":
                    Next(ref i); break;
                default:
                    if (!t.StartsWith('-') && string.IsNullOrEmpty(req.Url))
                        req.Url = NormalizeUrl(t);
                    break;
            }
        }
        return req;
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim().Trim('"', '\'');
        if (!url.Contains("://")) url = "https://" + url;
        return url;
    }

    /// <summary>Split a command line into tokens, honouring single and double quotes.</summary>
    private static List<string> Tokenize(string s)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        bool inToken = false;

        foreach (var ch in s)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else cur.Append(ch);
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; inToken = true; continue; }
            if (char.IsWhiteSpace(ch))
            {
                if (inToken) { result.Add(cur.ToString()); cur.Clear(); inToken = false; }
                continue;
            }
            cur.Append(ch);
            inToken = true;
        }
        if (inToken) result.Add(cur.ToString());
        return result;
    }
}
