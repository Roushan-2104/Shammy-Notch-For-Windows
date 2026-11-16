using System.Text;

namespace Shammy.Core.PhoneSend;

/// <summary>The pages the phone sees. Plain HTML with inline CSS so it works offline and on any browser.</summary>
public static class PhonePage
{
    const string Style = """
        <style>
        :root{color-scheme:dark}
        *{box-sizing:border-box}
        body{margin:0;min-height:100vh;background:#000;color:#fff;font:16px/1.4 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;
          display:flex;flex-direction:column;align-items:center;justify-content:center;padding:28px 20px;text-align:center}
        .card{width:100%;max-width:420px}
        .brand{color:#8e8e93;font-size:13px;letter-spacing:.08em;text-transform:uppercase;margin-bottom:28px}
        .icon{width:72px;height:72px;border-radius:20px;background:#1c1c1e;margin:0 auto 18px;display:flex;align-items:center;justify-content:center;font-size:34px}
        h1{font-size:24px;font-weight:600;margin:0 0 6px;word-break:break-word}
        .meta{color:#8e8e93;margin:0 0 28px}
        .btn{display:block;width:100%;padding:17px;border:0;border-radius:16px;font-family:inherit;font-weight:600;font-size:18px;line-height:1;text-decoration:none;cursor:pointer}
        .primary{background:#0a84ff;color:#fff}
        .secondary{background:#1c1c1e;color:#fff;margin-top:12px}
        .divider{height:1px;background:#2c2c2e;margin:30px 0 22px}
        .hint{color:#8e8e93;font-size:14px;margin:0 0 14px}
        input[type=file]{display:none}
        .ok{color:#30d158}
        .err{color:#ff453a}
        #status{min-height:22px;margin-top:12px;color:#8e8e93;font-size:14px}
        </style>
        """;

    /// <summary>The landing page: big download button, and a form to send files back to the PC.</summary>
    public static string Transfer(string token, string? fileName, long size, bool uploads = true, string? note = null)
    {
        var sb = Begin(fileName ?? "Shammy");
        if (fileName is not null)
        {
            sb.Append("<div class=\"icon\">&#128196;</div>")
              .Append("<h1>").Append(Encode(fileName)).Append("</h1>")
              .Append("<p class=\"meta\">").Append(Encode(ByteSize.Format(size))).Append(" &middot; from your PC</p>")
              .Append("<a class=\"btn primary\" href=\"/").Append(token).Append("/file\" download>Download</a>");
            if (note is not null) sb.Append("<p class=\"hint\" style=\"margin-top:18px;word-break:break-all\">").Append(Encode(note)).Append("</p>");
            if (uploads) sb.Append("<div class=\"divider\"></div>");
        }
        if (!uploads) return End(sb);
        sb.Append("<p class=\"hint\">Send photos or files to this PC&rsquo;s Downloads folder</p>")
          .Append("<form id=\"up\" method=\"post\" enctype=\"multipart/form-data\" action=\"/").Append(token).Append("/upload\">")
          .Append("<label class=\"btn secondary\" for=\"f\">Upload to PC</label>")
          .Append("<input id=\"f\" type=\"file\" name=\"file\" multiple>")
          .Append("</form><div id=\"status\"></div>")
          // Submitting as soon as files are picked saves a tap; the message is just feedback while it sends.
          .Append("<script>var f=document.getElementById('f');f.onchange=function(){if(!f.files.length)return;")
          .Append("document.getElementById('status').textContent='Uploading '+f.files.length+(f.files.length>1?' files':' file')+'…';")
          .Append("document.getElementById('up').submit();};</script>");
        return End(sb);
    }

    /// <summary>Shown after an upload; names what arrived and links back to the transfer page.</summary>
    public static string Uploaded(string token, IReadOnlyList<string> savedNames, string? error)
    {
        var sb = Begin("Shammy");
        if (savedNames.Count > 0)
        {
            sb.Append("<div class=\"icon ok\">&#10003;</div><h1>Sent to PC</h1><p class=\"meta\">");
            sb.Append(string.Join("<br>", savedNames.Select(Encode)));
            sb.Append("</p>");
        }
        if (error is not null) sb.Append("<p class=\"err\">").Append(Encode(error)).Append("</p>");
        sb.Append("<a class=\"btn secondary\" href=\"/").Append(token).Append("\">Back</a>");
        return End(sb);
    }

    static StringBuilder Begin(string title) => new StringBuilder()
        .Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
        .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">")
        .Append("<meta name=\"referrer\" content=\"no-referrer\">")
        .Append("<title>").Append(Encode(title)).Append("</title>").Append(Style)
        .Append("</head><body><div class=\"card\"><div class=\"brand\">Shammy</div>");

    static string End(StringBuilder sb) => sb.Append("</div></body></html>").ToString();

    // Only the markup-significant characters: the page is UTF-8, so names stay readable in the source.
    static string Encode(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
}
