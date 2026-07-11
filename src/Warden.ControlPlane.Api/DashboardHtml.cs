using System.Net;
using System.Text;

namespace Warden.ControlPlane.Api;

public static class DashboardHtml
{
    public static string Render(IReadOnlyList<DashboardComplianceRow> rows)
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>Warden Compliance</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:32px;color:#1f2937;background:#f8fafc}");
        html.AppendLine("main{max-width:960px;margin:0 auto}");
        html.AppendLine("h1{font-size:24px;font-weight:650;margin:0 0 20px}");
        html.AppendLine("table{width:100%;border-collapse:collapse;background:white;border:1px solid #d1d5db}");
        html.AppendLine("th,td{text-align:left;padding:10px 12px;border-bottom:1px solid #e5e7eb;font-size:14px}");
        html.AppendLine("th{background:#f3f4f6;font-weight:650}");
        html.AppendLine(".status{font-weight:650}");
        html.AppendLine(".ok{color:#047857}.bad{color:#b91c1c}.unknown{color:#6b7280}");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body><main>");
        html.AppendLine("<h1>Warden Compliance</h1>");
        html.AppendLine("<table>");
        html.AppendLine("<thead><tr><th>Device</th><th>Rule</th><th>Status</th><th>Last Check</th></tr></thead>");
        html.AppendLine("<tbody>");

        if (rows.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"4\">No devices have checked in.</td></tr>");
        }
        else
        {
            foreach (var row in rows)
            {
                var statusClass = row.Status switch
                {
                    "Compliant" => "ok",
                    "Non-compliant" => "bad",
                    _ => "unknown"
                };

                html.AppendLine("<tr>");
                html.Append("<td>").Append(Encode(row.Hostname)).Append("</td>");
                html.Append("<td>").Append(Encode(row.Rule)).Append("</td>");
                html.Append("<td class=\"status ").Append(statusClass).Append("\">")
                    .Append(Encode(row.Status)).Append("</td>");
                html.Append("<td>").Append(Encode(row.LastSeen.ToString("u"))).Append("</td>");
                html.AppendLine("</tr>");
            }
        }

        html.AppendLine("</tbody>");
        html.AppendLine("</table>");
        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
