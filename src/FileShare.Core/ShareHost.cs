using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileShare;

public static class ShareHost
{
    public static Task<IAsyncDisposable> StartAsync(ShareHostOptions options, CancellationToken cancellationToken = default)
        => ShareApplication.StartAsync(options, cancellationToken);

    public static WebApplication Build(ShareHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = Path.GetFullPath(options.Root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"分享目录不存在：{root}");
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "端口需要是 1 到 65535 的数字。");

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(ShareHost).Assembly.GetName().Name
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = options.MaxUploadMb * 1024L * 1024L;
            server.AddServerHeader = false;
        });

        var app = builder.Build();
        var store = new ShareStore(root, options.ToOptions());

        if (!string.IsNullOrEmpty(options.Token))
        {
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path == "/__auth") { await next(); return; }
                var accessKey = ctx.Request.Query["ak"].ToString();
                if (!string.IsNullOrEmpty(accessKey) && accessKey == options.Token)
                {
                    ctx.Response.Cookies.Append("fs_auth", options.Token!, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Lax,
                        MaxAge = TimeSpan.FromDays(7),
                        Path = "/"
                    });
                }
                if (store.IsAuthorized(ctx)) { await next(); return; }
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(store.RenderLogin(null));
            });
        }

        app.MapGet("/", store.ServeListingAsync);
        app.MapGet("/__zip", store.HandleZipAsync);
        app.MapPost("/__auth", store.HandleAuthAsync);
        app.Map("{**path}", store.ServePathAsync);
        return app;
    }
}

public sealed class ShareHostOptions
{
    public string Root { get; init; } = "";
    public int Port { get; init; } = 8080;
    public string? Token { get; init; }
    public bool ReadOnly { get; init; } = true;
    public bool AllowUpload { get; init; }
    public bool AllowZip { get; init; } = true;
    public int MaxUploadMb { get; init; } = 2048;
    public string ServerName { get; init; } = "文件分享";
    public bool ShowHidden { get; init; }

    public Options ToOptions() => new()
    {
        Root = Root,
        Port = Port,
        Token = Token,
        ReadOnly = ReadOnly,
        AllowUpload = AllowUpload,
        AllowZip = AllowZip,
        MaxUploadMb = MaxUploadMb,
        ServerName = ServerName,
        ShowHidden = ShowHidden
    };
}

internal sealed class ShareApplication : IAsyncDisposable
{
    private readonly WebApplication _application;

    private ShareApplication(WebApplication application) => _application = application;

    public static async Task<IAsyncDisposable> StartAsync(ShareHostOptions options, CancellationToken cancellationToken)
    {
        var application = ShareHost.Build(options);
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return new ShareApplication(application);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _application.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        finally { await _application.DisposeAsync().ConfigureAwait(false); }
    }
}

internal static class EmbeddedTemplates
{
    public static string Read(string name)
    {
        var assembly = typeof(EmbeddedTemplates).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(item => item.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"缺少页面模板：{name}");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

// ══════════════════════════ 配置 ══════════════════════════

public sealed class Options
{
    public string Root { get; set; } = "";
    public int Port { get; set; } = 8080;
    public string? Token { get; set; }
    public bool ReadOnly { get; set; } = true;
    public bool AllowUpload { get; set; }
    public bool AllowZip { get; set; } = true;
    public int MaxUploadMb { get; set; } = 2048;
    public string ServerName { get; set; } = "文件分享";
    public bool ShowHidden { get; set; }
}

// ══════════════════════════ 核心服务 ══════════════════════════

public sealed class ShareStore
{
    private readonly string _root;
    private readonly Options _opt;
    private readonly string _tplListing;
    private readonly string _tplLogin;
    private readonly string _tplUploadJs;
    private static readonly FileExtensionContentTypeProvider Mime = new();
    private static readonly DateTime Started = DateTime.Now;
    private static long _servedBytes;
    private static long _servedFiles;

    public string Token => _opt.Token ?? "";

    public ShareStore(string root, Options opt)
    {
        _root = root;
        _opt = opt;
        _tplListing = EmbeddedTemplates.Read("listing.html");
        _tplLogin = EmbeddedTemplates.Read("login.html");
        _tplUploadJs = EmbeddedTemplates.Read("upload.js.html");
    }

    public string PermissionSummary
    {
        get
        {
            var parts = new List<string> { "浏览", "下载" };
            if (_opt.AllowUpload && !_opt.ReadOnly) parts.Add("上传");
            if (_opt.AllowZip) parts.Add("打包下载");
            return string.Join(" + ", parts);
        }
    }

    public bool IsAuthorized(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(_opt.Token)) return true;
        if (ctx.Request.Cookies.TryGetValue("fs_auth", out var c) && c == _opt.Token) return true;
        return ctx.Request.Query["token"] == _opt.Token
            || ctx.Request.Query["ak"] == _opt.Token;
    }

    public string RenderLogin(string? error) =>
        _tplLogin.Replace("__ERROR__", error == null ? "" : $"<div class=\"err\">{Esc(error)}</div>");

    // ───────── 路径解析（越界防护） ─────────

    private bool TryResolve(string? rawPath, out string full, out List<string> segs)
    {
        full = _root;
        segs = new List<string>();
        var raw = rawPath ?? "/";

        string decoded;
        try { decoded = Uri.UnescapeDataString(raw); }
        catch { return false; }

        foreach (var seg in decoded.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg.Contains("..")) return false;
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            segs.Add(seg);
        }

        var parts = new List<string> { _root };
        parts.AddRange(segs);
        try { full = Path.GetFullPath(Path.Combine(parts.ToArray())); }
        catch { return false; }

        var rootSep = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.Equals(_root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveDir(HttpContext ctx, out string dir, out List<string> segs)
    {
        var rel = ctx.Request.Query["dir"].ToString();
        var p = string.IsNullOrEmpty(rel) ? ctx.Request.Path.Value : "/" + rel.TrimStart('/');
        return TryResolve(p, out dir, out segs);
    }

    // ───────── 统一入口 ─────────

    public async Task ServePathAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";

        // 上传请求
        if (HttpMethods.IsPut(ctx.Request.Method) || HttpMethods.IsPost(ctx.Request.Method))
        {
            await HandleUploadAsync(ctx);
            return;
        }

        if (!TryResolve(path, out var full, out _))
        {
            await StatusPage(ctx, 403, "403 禁止访问", "请求的路径不合法。");
            return;
        }

        if (Directory.Exists(full))
        {
            if (!path.EndsWith('/'))
            {
                ctx.Response.Redirect(path + "/" + ctx.Request.QueryString.Value, permanent: false);
                return;
            }
            await ServeListingAsync(ctx);
            return;
        }

        if (File.Exists(full)) { await ServeFileAsync(ctx, full); return; }

        await StatusPage(ctx, 404, "404 未找到", "该文件或目录不存在。");
    }

    // ───────── 目录列表 ─────────

    public async Task ServeListingAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        if (!TryResolve(req.Path.Value, out var dir, out var segs))
        {
            await StatusPage(ctx, 403, "403 禁止访问", "路径不合法。");
            return;
        }
        if (!Directory.Exists(dir))
        {
            await StatusPage(ctx, 404, "404 未找到", "目录不存在。");
            return;
        }

        string? q = req.Query["q"];
        string ftype = req.Query["type"].ToString();
        if (string.IsNullOrEmpty(ftype)) ftype = "all";
        string sort = req.Query["sort"].ToString();
        if (string.IsNullOrEmpty(sort)) sort = "name";
        bool desc = req.Query["dir"] == "desc";
        int page = int.TryParse(req.Query["p"], out var pv) ? Math.Max(1, pv) : 1;
        const int pageSize = 100;
        string? search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var items = new List<FileItem>();
        try
        {
            var enumOpt = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = _opt.ShowHidden ? 0 : (FileAttributes.Hidden | FileAttributes.System),
                RecurseSubdirectories = false
            };
            var di = new DirectoryInfo(dir);
            foreach (var d in di.EnumerateDirectories("*", enumOpt))
                items.Add(new FileItem { Name = d.Name, FullPath = d.FullName, IsDir = true, Modified = SafeTime(d), Size = 0 });
            foreach (var f in di.EnumerateFiles("*", enumOpt))
                items.Add(new FileItem { Name = f.Name, FullPath = f.FullName, IsDir = false, Size = SafeLen(f), Modified = SafeTime(f) });
        }
        catch (UnauthorizedAccessException) { await StatusPage(ctx, 403, "403 拒绝访问", "系统拒绝访问该目录。"); return; }
        catch (DirectoryNotFoundException) { await StatusPage(ctx, 404, "404 未找到", "目录不存在。"); return; }
        catch (Exception ex) { await StatusPage(ctx, 500, "500 读取失败", Esc(ex.Message)); return; }

        foreach (var it in items.Where(x => x.IsDir))
            it.SubCount = CountEntries(it.FullPath);

        IEnumerable<FileItem> filtered = items;
        if (search != null) filtered = filtered.Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (ftype == "dir") filtered = filtered.Where(e => e.IsDir);
        else if (ftype != "all") filtered = filtered.Where(e => !e.IsDir && MatchType(e.Name, ftype));

        var list = sort switch
        {
            "size" => (desc ? filtered.OrderByDescending(e => e.IsDir).ThenByDescending(e => e.Size)
                             : filtered.OrderByDescending(e => e.IsDir).ThenBy(e => e.Size)).ToList(),
            "time" => (desc ? filtered.OrderByDescending(e => e.IsDir).ThenByDescending(e => e.Modified)
                             : filtered.OrderByDescending(e => e.IsDir).ThenBy(e => e.Modified)).ToList(),
            _ => (desc ? filtered.OrderByDescending(e => e.IsDir).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
                       : filtered.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)).ToList()
        };

        int dirCount = list.Count(e => e.IsDir);
        int fileCount = list.Count(e => !e.IsDir);
        long totalSize = list.Where(e => !e.IsDir).Sum(e => e.Size);

        int pages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)pageSize));
        page = Math.Min(page, pages);
        var pageItems = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        string tokenQs = string.IsNullOrEmpty(_opt.Token) ? "" : "?token=" + Url(_opt.Token);

        // 面包屑
        var crumb = new StringBuilder();
        crumb.Append($"<a href=\"/{tokenQs}\">🏠 根目录</a>");
        var acc = "/";
        for (int i = 0; i < segs.Count; i++)
        {
            acc += Seg(segs[i]) + "/";
            crumb.Append(" <span class=\"sep\">/</span> ");
            crumb.Append(i == segs.Count - 1
                ? $"<span class=\"crumb-cur\">{Esc(segs[i])}</span>"
                : $"<a href=\"{Attr(acc + tokenQs)}\">{Esc(segs[i])}</a>");
        }

        // 类型下拉
        var typeOpts = new StringBuilder();
        foreach (var (val, label) in new[]
        {
            ("all", "全部类型"), ("dir", "仅文件夹"), ("video", "视频"), ("audio", "音频"),
            ("image", "图片"), ("archive", "压缩包"), ("doc", "文档"), ("code", "代码")
        })
            typeOpts.Append($"<option value=\"{val}\"{(ftype == val ? " selected" : "")}>{label}</option>");

        string SelfUrl(string? s2 = null, bool? d2 = null, int? p2 = null)
        {
            var ps = new List<string>();
            if (search != null) ps.Add("q=" + Url(search));
            if (ftype != "all") ps.Add("type=" + Url(ftype));
            var ss = s2 ?? sort;
            if (ss != "name") ps.Add("sort=" + ss);
            if (d2 ?? desc) ps.Add("dir=desc");
            var pp = p2 ?? page;
            if (pp > 1) ps.Add("p=" + pp);
            if (!string.IsNullOrEmpty(_opt.Token)) ps.Add("token=" + Url(_opt.Token));
            return ps.Count == 0 ? "./" : "./?" + string.Join("&", ps);
        }

        string SortHead(string key, string label)
        {
            bool active = sort == key;
            string arrow = active ? (desc ? " ↓" : " ↑") : "";
            return $"<th class=\"{(active ? "sorted" : "")}\"><a href=\"{Attr(SelfUrl(key, active && !desc, 1))}\">{label}{arrow}</a></th>";
        }

        // 行
        var rows = new StringBuilder();
        if (segs.Count > 0)
        {
            var parentSegs = segs.Take(segs.Count - 1).ToList();
            var parent = "/" + string.Join("/", parentSegs.Select(Seg)) + (parentSegs.Count > 0 ? "/" : "");
            rows.Append($"<tr class=\"row-dir\"><td class=\"c-icon\">↩</td><td class=\"c-name\"><a href=\"{Attr(parent + tokenQs)}\">返回上级目录</a></td><td class=\"c-size\"></td><td class=\"c-time\"></td></tr>");
        }

        foreach (var e in pageItems)
        {
            var hrefPath = "/" + string.Join("/", segs.Concat(new[] { e.Name }).Select(Seg));
            string icon, sizeText, dlLink;
            if (e.IsDir)
            {
                hrefPath += "/";
                icon = "📁";
                sizeText = e.SubCount >= 0 ? $"{e.SubCount} 项" : "";
                dlLink = "";
            }
            else
            {
                icon = IconFor(e.Name);
                sizeText = Size(e.Size);
                var dlHref = hrefPath + "?dl=1" + (string.IsNullOrEmpty(_opt.Token) ? "" : "&token=" + Url(_opt.Token));
                dlLink = $" <a class=\"dl\" href=\"{Attr(dlHref)}\" title=\"下载\">⬇</a>";
            }
            var target = e.IsDir ? "" : " target=\"_blank\" rel=\"noopener\"";
            rows.Append($"<tr class=\"{(e.IsDir ? "row-dir" : "row-file")}\">"
                + $"<td class=\"c-icon\">{icon}</td>"
                + $"<td class=\"c-name\"><a href=\"{Attr(hrefPath + tokenQs)}\"{target}>{Esc(e.Name)}</a>{dlLink}</td>"
                + $"<td class=\"c-size\">{Esc(sizeText)}</td>"
                + $"<td class=\"c-time\">{e.Modified:yyyy-MM-dd HH:mm}</td></tr>");
        }

        if (pageItems.Count == 0)
            rows.Append($"<tr><td colspan=\"4\" class=\"empty\">{(search != null ? "没有找到匹配的文件" : "这个目录是空的")}</td></tr>");

        // 分页
        var pager = new StringBuilder();
        if (pages > 1)
        {
            pager.Append("<div class=\"pager\">");
            if (page > 1) pager.Append($"<a href=\"{Attr(SelfUrl(null, null, page - 1))}\">‹ 上一页</a>");
            int from = Math.Max(1, page - 2), to = Math.Min(pages, page + 2);
            if (from > 1) pager.Append($"<a href=\"{Attr(SelfUrl(null, null, 1))}\">1</a>");
            if (from > 2) pager.Append("<span class=\"dots\">…</span>");
            for (int i = from; i <= to; i++)
                pager.Append(i == page ? $"<span class=\"cur\">{i}</span>" : $"<a href=\"{Attr(SelfUrl(null, null, i))}\">{i}</a>");
            if (to < pages - 1) pager.Append("<span class=\"dots\">…</span>");
            if (to < pages) pager.Append($"<a href=\"{Attr(SelfUrl(null, null, pages))}\">{pages}</a>");
            if (page < pages) pager.Append($"<a href=\"{Attr(SelfUrl(null, null, page + 1))}\">下一页 ›</a>");
            pager.Append("</div>");
        }

        var curRel = string.Join("/", segs.Select(Seg));
        var sep = tokenQs.Length > 0 ? "&" : "?";
        var zipBtn = _opt.AllowZip
            ? $"<a class=\"btn\" href=\"/__zip{tokenQs}{sep}dir={Url(curRel)}\">📦 打包下载本目录</a>"
            : "";
        bool canUpload = _opt.AllowUpload && !_opt.ReadOnly;
        var uploadBtn = canUpload
            ? "<button class=\"btn btn-primary\" id=\"pick\" type=\"button\">⬆ 上传文件</button><input type=\"file\" id=\"fileInput\" multiple hidden>"
            : "";
        var tokenInput = string.IsNullOrEmpty(_opt.Token) ? "" : $"<input type=\"hidden\" name=\"token\" value=\"{Attr(_opt.Token)}\">";
        var script = canUpload
            ? _tplUploadJs
                .Replace("/*__TOKEN__*/''", JsonSerializer.Serialize(_opt.Token ?? ""))
                .Replace("/*__DIR__*/''", JsonSerializer.Serialize(curRel))
            : "";

        // 根目录时标题不重复显示站点名
        var pageTitle = segs.Count == 0 ? _opt.ServerName : segs[^1] + " · " + _opt.ServerName;

        var html = _tplListing
            .Replace("__PAGE_TITLE__", Esc(pageTitle))
            .Replace("__SERVER_NAME__", Esc(_opt.ServerName))
            .Replace("__STATS__", Esc($"{dirCount} 个文件夹 · {fileCount} 个文件 · {Size(totalSize)}"))
            .Replace("__CRUMBS__", crumb.ToString())
            .Replace("__QUERY__", Attr(search ?? ""))
            .Replace("__TOKEN_INPUT__", tokenInput)
            .Replace("__TYPE_OPTIONS__", typeOpts.ToString())
            .Replace("__ZIP_BUTTON__", zipBtn)
            .Replace("__UPLOAD_BUTTON__", uploadBtn)
            .Replace("__SORT_NAME__", SortHead("name", "名称"))
            .Replace("__SORT_SIZE__", SortHead("size", "大小"))
            .Replace("__SORT_TIME__", SortHead("time", "修改时间"))
            .Replace("__ROWS__", rows.ToString())
            .Replace("__PAGER__", pager.ToString())
            .Replace("__MACHINE__", Esc(Environment.MachineName))
            .Replace("__UPTIME__", (DateTime.Now - Started).ToString(@"d\d\ hh\:mm\:ss"))
            .Replace("__SERVED__", Size(Interlocked.Read(ref _servedBytes)))
            .Replace("__HOST__", Esc(ctx.Request.Host.Value))
            .Replace("__UPLOAD_SCRIPT__", script);

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(html);
    }

    // ───────── 文件下载 ─────────

    private async Task ServeFileAsync(HttpContext ctx, string full)
    {
        var fi = new FileInfo(full);
        if (!Mime.TryGetContentType(full, out var mime)) mime = "application/octet-stream";
        bool asAttachment = ctx.Request.Query["dl"] == "1" || !IsInlineSafe(full);

        var req = ctx.Request;
        var res = ctx.Response;
        var headers = res.Headers;

        long length = fi.Length;
        string lastMod = fi.LastWriteTimeUtc.ToString("R");
        string etag = "\"" + fi.LastWriteTimeUtc.Ticks.ToString("x") + "-" + length.ToString("x") + "\"";

        headers.AcceptRanges = "bytes";
        headers.LastModified = lastMod;
        headers.ETag = etag;
        headers.CacheControl = "private, max-age=0, must-revalidate";

        // 条件请求：未修改直接 304
        var inm = req.Headers.IfNoneMatch.ToString();
        var ims = req.Headers.IfModifiedSince.ToString();
        bool noRange = string.IsNullOrEmpty(req.Headers.Range);
        if (noRange)
        {
            bool fresh = false;
            if (!string.IsNullOrEmpty(inm)) fresh = inm.Split(',').Any(t => t.Trim() == etag);
            else if (!string.IsNullOrEmpty(ims) && DateTime.TryParse(ims, out var since))
                fresh = fi.LastWriteTimeUtc <= since.ToUniversalTime().AddSeconds(1);
            if (fresh) { res.StatusCode = 304; return; }
        }

        // Range 解析（视频拖动进度 / 断点续传）
        long start = 0, end = length - 1;
        bool partial = false;
        var rangeHeader = req.Headers.Range.ToString();
        if (rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var sPart = spec[..dash].Trim();
                var ePart = spec[(dash + 1)..].Trim();
                if (sPart.Length == 0 && long.TryParse(ePart, out var suffix))
                {
                    start = Math.Max(0, length - suffix);
                    partial = length > 0;
                }
                else if (long.TryParse(sPart, out var s))
                {
                    start = s;
                    end = long.TryParse(ePart, out var e) ? Math.Min(e, length - 1) : length - 1;
                    partial = true;
                }
            }
            if (partial && (start > end || start >= length))
            {
                res.StatusCode = 416;
                headers.ContentRange = $"bytes */{length}";
                return;
            }
        }

        long sendLen = end - start + 1;
        if (sendLen <= 0) { res.StatusCode = 200; res.ContentLength = 0; return; }

        res.StatusCode = partial ? 206 : 200;
        res.ContentType = mime;
        if (partial) headers.ContentRange = $"bytes {start}-{end}/{length}";
        if (asAttachment)
            headers.ContentDisposition =
                $"attachment; filename=\"{AsciiFallback(fi.Name)}\"; filename*=UTF-8''{Uri.EscapeDataString(fi.Name)}";
        res.ContentLength = sendLen;

        Interlocked.Add(ref _servedBytes, sendLen);
        Interlocked.Increment(ref _servedFiles);

        // 用 SendFileAsync 分段发送，由 Kestrel 负责零拷贝式写盘
        const int chunk = 1024 * 1024;
        long offset = start;
        long remaining = sendLen;
        while (remaining > 0)
        {
            int len = (int)Math.Min(chunk, remaining);
            await res.SendFileAsync(full, offset, len, ctx.RequestAborted);
            offset += len;
            remaining -= len;
        }
    }

    private static string AsciiFallback(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(ch < 128 && ch != '"' && ch != '\\' ? ch : '_');
        return sb.ToString();
    }

    private static bool IsInlineSafe(string path)
    {
        if (!Mime.TryGetContentType(path, out var m))
            return false;
        return m.StartsWith("image/", StringComparison.Ordinal)
            || m.StartsWith("video/", StringComparison.Ordinal)
            || m.StartsWith("audio/", StringComparison.Ordinal)
            || m.StartsWith("text/plain", StringComparison.Ordinal)
            || m == "application/pdf";
    }

    // ───────── 打包下载 ─────────

    public async Task HandleZipAsync(HttpContext ctx)
    {
        if (!_opt.AllowZip) { await StatusPage(ctx, 403, "403 禁止", "打包下载已关闭。"); return; }
        if (!TryResolveDir(ctx, out var dir, out var segs)) { await StatusPage(ctx, 403, "403", "路径不合法。"); return; }
        if (!Directory.Exists(dir)) { await StatusPage(ctx, 404, "404 未找到", "目录不存在。"); return; }

        var files = new List<(string Full, string Rel)>();
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = _opt.ShowHidden ? 0 : (FileAttributes.Hidden | FileAttributes.System),
                RecurseSubdirectories = true
            }))
                files.Add((f.FullName, Path.GetRelativePath(dir, f.FullName)));
        }
        catch (Exception ex) { await StatusPage(ctx, 500, "500", Esc(ex.Message)); return; }

        var baseName = segs.Count > 0
            ? segs[^1]
            : (Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : "files");
        var zipName = baseName + ".zip";

        // ZipArchive 在 Dispose 时会写中央目录，Kestrel 禁止同步 IO，
        // 因此先打包到临时文件，再以流式返回，既能规避该限制又不占用内存。
        var tempFile = Path.Combine(Path.GetTempPath(), $"fileshare_{Guid.NewGuid():N}.zip");
        try
        {
            await Task.Run(() =>
            {
                using var fs = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, System.IO.FileShare.None, 128 * 1024);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
                foreach (var (f, r) in files)
                {
                    try
                    {
                        var entry = zip.CreateEntry(r.Replace('\\', '/'), CompressionLevel.Fastest);
                        using var src = new FileStream(f, FileMode.Open, FileAccess.Read,
                            System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, 64 * 1024);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                        Interlocked.Add(ref _servedBytes, src.Length);
                    }
                    catch { /* 单文件失败不中断整体 */ }
                }
            });

            var zipInfo = new FileInfo(tempFile);
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition =
                $"attachment; filename*=UTF-8''{Uri.EscapeDataString(zipName)}";
            ctx.Response.ContentLength = zipInfo.Length;
            await ctx.Response.SendFileAsync(tempFile, 0, zipInfo.Length, ctx.RequestAborted);
            Interlocked.Increment(ref _servedFiles);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // ───────── 上传 ─────────

    public async Task HandleUploadAsync(HttpContext ctx)
    {
        if (!_opt.AllowUpload || _opt.ReadOnly) { await StatusPage(ctx, 403, "403 禁止上传", "服务器已关闭上传。"); return; }

        // 上传目标目录就是请求路径本身（末尾斜杠可有可无）
        var p = ctx.Request.Path.Value ?? "/";
        if (!p.EndsWith('/')) p += "/";
        if (!TryResolve(p, out var dir, out _)) { await StatusPage(ctx, 403, "403", "路径不合法。"); return; }
        if (!Directory.Exists(dir)) { await StatusPage(ctx, 404, "404", "目标目录不存在。"); return; }

        var name = ctx.Request.Query["name"].ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var form = await ctx.Request.ReadFormAsync();
                name = form["name"].ToString();
            }
            catch { /* 非表单请求 */ }
        }
        if (string.IsNullOrWhiteSpace(name)) { await StatusPage(ctx, 400, "400", "缺少文件名。"); return; }

        var safeName = Path.GetFileName(name);
        if (string.IsNullOrEmpty(safeName) || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { await StatusPage(ctx, 400, "400", "文件名不合法。"); return; }

        var target = Path.Combine(dir, safeName);
        if (File.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(safeName);
            var ext = Path.GetExtension(safeName);
            for (int i = 1; i < 1000; i++)
            {
                var cand = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(cand)) { target = cand; break; }
            }
        }

        try
        {
            await using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                System.IO.FileShare.None, 128 * 1024, useAsync: true))
            {
                await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
            }
            var len = new FileInfo(target).Length;
            Interlocked.Add(ref _servedBytes, len);
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync($"OK {len}");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            await StatusPage(ctx, 500, "500 上传失败", Esc(ex.Message));
        }
    }

    // ───────── 登录 ─────────

    public async Task HandleAuthAsync(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        var pwd = form["token"].ToString();

        if (!string.IsNullOrEmpty(_opt.Token) && pwd == _opt.Token)
        {
            ctx.Response.Cookies.Append("fs_auth", _opt.Token!, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromDays(7),
                Path = "/"
            });
            ctx.Response.Redirect("/");
            return;
        }

        ctx.Response.StatusCode = 401;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(RenderLogin("口令不正确，请重新输入"));
    }

    // ───────── 错误页 ─────────

    private static async Task StatusPage(HttpContext ctx, int code, string title, string message)
    {
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var html = "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + $"<title>{Esc(title)}</title><style>"
            + "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;"
            + "font-family:-apple-system,'Segoe UI','Microsoft YaHei',sans-serif;background:#f6f7f9;"
            + "color:#1f2328;text-align:center;padding:20px}"
            + "h1{font-size:20px;margin:0 0 10px}p{color:#6b7280;font-size:14px;margin:0 0 20px;line-height:1.6}"
            + "a{color:#2563eb;text-decoration:none;font-size:14px}"
            + "@media (prefers-color-scheme:dark){body{background:#0f1115;color:#e6e9ee}"
            + "p{color:#9aa3b2}a{color:#5b9dff}}"
            + "</style></head><body><div>"
            + $"<h1>{Esc(title)}</h1><p>{message}</p><a href=\"/\">← 返回首页</a>"
            + "</div></body></html>";
        await ctx.Response.WriteAsync(html);
    }

    // ══════════ 工具 ══════════

    private static string Esc(string? s) => HttpUtility.HtmlEncode(s ?? "");
    private static string Attr(string? s) => HttpUtility.HtmlAttributeEncode(s ?? "");
    private static string Url(string? s) => HttpUtility.UrlEncode(s ?? "");
    private static string Seg(string s) => Uri.EscapeDataString(s);

    private static string Size(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }

    private static DateTime SafeTime(FileSystemInfo f) { try { return f.LastWriteTime; } catch { return DateTime.MinValue; } }
    private static long SafeLen(FileInfo f) { try { return f.Length; } catch { return 0; } }

    private static int CountEntries(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Take(9999).Count(); }
        catch { return -1; }
    }

    private static bool MatchType(string name, string type)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return type switch
        {
            "video" => ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v" or ".ts" or ".rmvb" or ".mpg" or ".mpeg" or ".3gp",
            "audio" => ext is ".mp3" or ".flac" or ".wav" or ".aac" or ".ogg" or ".m4a" or ".wma" or ".opus" or ".ape",
            "image" => ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".tif" or ".tiff" or ".heic" or ".avif",
            "archive" => ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" or ".tgz",
            "doc" => ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".rtf" or ".csv" or ".epub",
            "code" => ext is ".cs" or ".js" or ".ts" or ".py" or ".java" or ".go" or ".rs" or ".cpp" or ".c" or ".h" or ".html" or ".css" or ".json" or ".xml" or ".yml" or ".yaml" or ".sh" or ".ps1" or ".sql",
            _ => true
        };
    }

    private static string IconFor(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v" or ".ts" or ".rmvb" => "🎬",
            ".mp3" or ".flac" or ".wav" or ".aac" or ".ogg" or ".m4a" or ".wma" or ".opus" => "🎵",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".heic" or ".avif" => "🖼️",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".xz" or ".iso" or ".tgz" => "📦",
            ".pdf" => "📕",
            ".doc" or ".docx" or ".rtf" => "📘",
            ".xls" or ".xlsx" or ".csv" => "📗",
            ".ppt" or ".pptx" => "📙",
            ".exe" or ".msi" or ".dmg" or ".apk" => "⚙️",
            ".cs" or ".js" or ".ts" or ".py" or ".java" or ".go" or ".rs" or ".cpp" or ".c" or ".h" or ".html" or ".css" or ".json" or ".xml" or ".sh" or ".ps1" or ".sql" => "⌨️",
            ".ttf" or ".otf" or ".woff" or ".woff2" => "🔤",
            _ => "📄"
        };
    }
}

public sealed class FileItem
{
    public string Name = "";
    public string FullPath = "";
    public bool IsDir;
    public long Size;
    public DateTime Modified;
    public int SubCount = -1;
}

public static class NetInfo
{
    public static List<string> GetLanAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var s = ip.ToString();
                if (s.StartsWith("127.") || s.StartsWith("169.254.")) continue;
                if (s.StartsWith("198.18.") || s.StartsWith("198.19.")) continue;
                list.Add(s);
            }
        }
        catch { }

        try
        {
            using var sock = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            sock.Connect("8.8.8.8", 65530);
            if (sock.LocalEndPoint is System.Net.IPEndPoint ep)
            {
                var s = ep.Address.ToString();
                if (!s.StartsWith("127.") && !s.StartsWith("198.18.") && !s.StartsWith("198.19.") && !list.Contains(s))
                    list.Insert(0, s);
            }
        }
        catch { }

        return list;
    }
}
