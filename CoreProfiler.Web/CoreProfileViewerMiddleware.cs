using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using CoreProfiler.Timings;
using Microsoft.AspNetCore.Http;

namespace CoreProfiler.Web
{
    internal class CoreProfileViewerMiddleware
    {
        private const string ViewUrl = "/coreprofiler/view";
        private const string ViewUrlNano = "/nanoprofiler/view";
        private const string Import = "import";
        private const string Export = "?export";
        private const string CorrelationId = "correlationId";
        private readonly RequestDelegate _next;
        private readonly HttpClient _httpClient;

        public CoreProfileViewerMiddleware(RequestDelegate next, HttpClient httpClient)
        {
            _next = next;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task Invoke(HttpContext context)
        {
            var path = context.Request.Path.ToString().TrimEnd('/');

            // generate baseViewPath
            string baseViewPath = null;
            var posStart = path.IndexOf(ViewUrl, StringComparison.OrdinalIgnoreCase);
            if (posStart < 0)
                posStart = path.IndexOf(ViewUrlNano, StringComparison.OrdinalIgnoreCase);
            if (posStart >= 0)
                baseViewPath = path.Substring(0, posStart) + ViewUrl;

            // prepend pathbase if specified
            baseViewPath = context.Request.PathBase + baseViewPath;

            if (path.EndsWith("/coreprofiler-resources/icons"))
            {
                context.Response.ContentType = "image/png";
                var iconsStream = GetType().GetTypeInfo().Assembly.GetManifestResourceStream("CoreProfiler.Web.icons.png");
                using (var br = new BinaryReader(iconsStream))
                {
                    await context.Response.Body.WriteAsync(br.ReadBytes((int)iconsStream.Length), 0, (int)iconsStream.Length);
                }
                return;
            }

            if (path.EndsWith("/coreprofiler-resources/css"))
            {
                context.Response.ContentType = "text/css";
                var cssStream = GetType().GetTypeInfo().Assembly.GetManifestResourceStream("CoreProfiler.Web.treeview_timeline.css");
                using (var sr = new StreamReader(cssStream))
                {
                    await context.Response.WriteAsync(sr.ReadToEnd());
                }
                return;
            }

            // view index of all latest results: ~/coreprofiler/view
            if (path.EndsWith(ViewUrl, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(ViewUrlNano, StringComparison.OrdinalIgnoreCase))
            {
                // try to handle import/export first
                var import = context.Request.Query[Import];
                if (Uri.IsWellFormedUriString(import, UriKind.Absolute))
                {
                    await ImportSessionsFromUrl(import);
                    return;
                }

                if (context.Request.QueryString.ToString() == Export)
                {
                    context.Response.ContentType = "application/json";

                    await context.Response.WriteAsync(ImportSerializer.SerializeSessions(ProfilingSession.CircularBuffer));
                    return;
                }

                var exportCorrelationId = context.Request.Query[CorrelationId];
                if (!string.IsNullOrEmpty(exportCorrelationId))
                {
                    context.Response.ContentType = "application/json";
                    var result = ProfilingSession.CircularBuffer.FirstOrDefault(
                            r => r.Data != null && r.Data.ContainsKey(CorrelationId) && r.Data[CorrelationId] == exportCorrelationId);
                    if (result != null)
                    {
                        await context.Response.WriteAsync(ImportSerializer.SerializeSessions(new[] { result }));
                        return;
                    }
                }

                // render result list view
                context.Response.ContentType = "text/html";

                var sb = new StringBuilder();
                sb.Append("<head>");
                sb.Append("<title>CoreProfiler Latest Profiling Results</title>");
                sb.Append("<style>th { width: 200px; text-align: left; } .gray { background-color: #eee; } .nowrap { white-space: nowrap;padding-right: 20px; vertical-align:top; } </style>");
                sb.Append("</head");
                sb.Append("<body>");
                sb.Append(CoreProfilerMiddleware.ViewResultIndexHeaderHtml);

                var tagFilter = context.Request.Query["tag"];
                if (!string.IsNullOrWhiteSpace(tagFilter))
                {
                    sb.Append("<div><strong>Filtered by tag:</strong> ");
                    sb.Append(tagFilter);
                    sb.Append("<br/><br /></div>");
                }

                sb.Append("<table>");
                sb.Append("<tr><th class=\"nowrap\">Time (UTC)</th><th class=\"nowrap\">Duration (ms)</th><th>Url</th></tr>");
                var latestResults = ProfilingSession.CircularBuffer.OrderByDescending(r => r.Started);
                var i = 0;
                foreach (var result in latestResults)
                {
                    if (!string.IsNullOrWhiteSpace(tagFilter) &&
                        (result.Tags == null || !result.Tags.Contains<string>(tagFilter, StringComparer.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    sb.Append("<tr");
                    if ((i++) % 2 == 1)
                    {
                        sb.Append(" class=\"gray\"");
                    }
                    sb.Append("><td class=\"nowrap\">");
                    sb.Append(result.Started.ToString("yyyy-MM-ddTHH:mm:ss.FFF"));
                    sb.Append("</td><td class=\"nowrap\">");
                    sb.Append(result.DurationMilliseconds);
                    sb.Append("</td><td><a href=\"");
                    sb.Append(baseViewPath);
                    sb.Append("/");
                    sb.Append(result.Id.ToString());
                    sb.Append("\" target=\"_blank\">");
                    sb.Append(result.Name.Replace("\r\n", " "));
                    sb.Append("</a></td></tr>");
                }
                sb.Append("</table>");

                sb.Append("</body>");

                await context.Response.WriteAsync(sb.ToString());
                return;
            }

            // view specific result by uuid: ~/coreprofiler/view/{uuid}
            if (path.IndexOf(ViewUrl, StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf(ViewUrlNano, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                context.Response.ContentType = "text/html";

                var sb = new StringBuilder();
                sb.Append("<head>");
                sb.Append("<meta charset=\"utf-8\" />");
                sb.Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\" />");
                sb.Append("<title>CoreProfiler Profiling Result</title>");
                sb.Append("<link rel=\"stylesheet\" href=\"./coreprofiler-resources/css\" />");
                sb.Append("</head");
                sb.Append("<body>");
                sb.Append("<h1>CoreProfiler Profiling Result</h1>");

                var uuid = path.Split('/').Last();
                var result = ProfilingSession.CircularBuffer.FirstOrDefault(
                        r => r.Id.ToString().ToLowerInvariant() == uuid.ToLowerInvariant());
                if (result != null)
                {
                    if (CoreProfilerMiddleware.TryToImportDrillDownResult)
                    {
                        // try to import drill down results
                        foreach (var timing in result.Timings)
                        {
                            if (timing.Data == null || !timing.Data.ContainsKey(CorrelationId)) continue;
                            Guid parentResultId;
                            if (!Guid.TryParse(timing.Data[CorrelationId], out parentResultId)
                                || ProfilingSession.CircularBuffer.Any(r => r.Id == parentResultId)) continue;

                            string remoteAddress;
                            if (!timing.Data.TryGetValue("remoteAddress", out remoteAddress))
                                remoteAddress = timing.Name;

                            if (!Uri.IsWellFormedUriString(remoteAddress, UriKind.Absolute)) continue;

                            if (!remoteAddress.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

                            var pos = remoteAddress.IndexOf("?");
                            if (pos > 0) remoteAddress = remoteAddress.Substring(0, pos);
                            if (remoteAddress.Split('/').Last().Contains(".")) remoteAddress = remoteAddress.Substring(0, remoteAddress.LastIndexOf("/"));

                            try
                            {
                                await ImportSessionsFromUrl(remoteAddress + "/coreprofiler/view?" + CorrelationId + "=" + parentResultId.ToString("N"));
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.Write(ex.Message);

                                //ignore exceptions
                            }
                        }
                    }

                    // render result tree
                    sb.Append("<div class=\"css-treeview\">");

                    // print summary
                    sb.Append("<ul>");
                    sb.Append("<li class=\"summary\">");
                    PrintDrillUpLink(sb, result, baseViewPath);
                    sb.Append(result.Name.Replace("\r\n", " "));
                    sb.Append("</li>");
                    sb.Append("<li class=\"summary\">");
                    if (result.Data != null)
                    {
                        foreach (var keyValue in result.Data)
                        {
                            if (string.IsNullOrWhiteSpace(keyValue.Value)) continue;

                            sb.Append("<b>");
                            sb.Append(keyValue.Key);
                            sb.Append(": </b>");
                            var encodedValue = WebUtility.HtmlEncode(keyValue.Value);
                            if (keyValue.Key.EndsWith("Count") || keyValue.Key.EndsWith("Duration"))
                            {
                                sb.Append("<span class=\"");
                                sb.Append(keyValue.Key);
                                sb.Append("\">");
                                sb.Append(encodedValue);
                                sb.Append("</span>");
                            }
                            else
                            {
                                sb.Append(encodedValue);
                            }
                            sb.Append(" &nbsp; ");
                        }
                    }
                    sb.Append("<b>machine: </b>");
                    sb.Append(result.MachineName);
                    sb.Append(" &nbsp; ");
                    if (result.Tags != null && result.Tags.Any())
                    {
                        sb.Append("<b>tags: </b>");
                        sb.Append(string.Join(", ", result.Tags.Select(t => string.Format("<a href=\"{2}?tag={0}\">{1}</a>", HttpUtility.UrlEncode(t), t, baseViewPath))));
                        sb.Append(" &nbsp; ");
                    }
                    sb.Append("</li>");
                    sb.Append("</ul>");

                    var totalLength = result.DurationMilliseconds;
                    if (totalLength == 0)
                    {
                        totalLength = 1;
                    }
                    var factor = 300.0 / totalLength;

                    // print ruler
                    sb.Append("<ul>");
                    sb.Append("<li class=\"ruler\"><span style=\"width:300px\">0</span><span style=\"width:80px\">");
                    sb.Append(totalLength);
                    sb.Append(
                        " (ms)</span><span style=\"width:20px\">&nbsp;</span><span style=\"width:60px\">Start</span><span style=\"width:60px\">Duration</span><span style=\"width:20px\">&nbsp;</span><span>Timing Hierarchy</span></li>");
                    sb.Append("</ul>");

                    // print timings
                    sb.Append("<ul class=\"timing\">");
                    PrintTimings(result, result.Id, sb, factor, baseViewPath);
                    sb.Append("");
                    sb.Append("</ul>");
                    sb.Append("</div>");

                    // print timing data popups
                    foreach (var timing in result.Timings)
                    {
                        if (timing.Data == null || !timing.Data.Any()) continue;

                        sb.Append("<aside id=\"data_");
                        sb.Append(timing.Id.ToString());
                        sb.Append("\" style=\"display:none\" class=\"modal\">");
                        sb.Append("<div>");
                        sb.Append("<h4><code>");
                        sb.Append(timing.Name.Replace("\r\n", " "));
                        sb.Append("</code></h4>");
                        sb.Append("<textarea>");
                        foreach (var keyValue in timing.Data)
                        {
                            if (string.IsNullOrWhiteSpace(keyValue.Value)) continue;

                            sb.Append(keyValue.Key);
                            sb.Append(":\r\n");
                            var value = keyValue.Value.Trim();

                            if (value.StartsWith("<"))
                            {
                                // asuume it is XML
                                // try to format XML with indent
                                var doc = new XmlDocument();
                                try
                                {
                                    doc.LoadXml(value);
                                    var ms = new MemoryStream();
                                    var xwSettings = new XmlWriterSettings
                                    {
                                        Encoding = new UTF8Encoding(false),
                                        Indent = true,
                                        IndentChars = "\t"
                                    };
                                    using (var writer = XmlWriter.Create(ms, xwSettings))
                                    {
                                        doc.Save(writer);
                                        ms.Seek(0, SeekOrigin.Begin);
                                        using (var sr = new StreamReader(ms))
                                        {
                                            value = sr.ReadToEnd();
                                        }
                                    }
                                }
                                catch
                                {
                                    //squash exception
                                }
                            }
                            sb.Append(value);
                            sb.Append("\r\n\r\n");
                        }
                        if (timing.Tags != null && timing.Tags.Any())
                        {
                            sb.Append("tags:\r\n");
                            sb.Append(timing.Tags);
                            sb.Append("\r\n");
                        }
                        sb.Append("</textarea>");
                        sb.Append(
                            "<a href=\"#close\" title=\"Close\" onclick=\"this.parentNode.parentNode.style.display='none'\">Close</a>");
                        sb.Append("</div>");
                        sb.Append("</aside>");
                    }
                }
                else
                {
                    sb.Append("Specified result does not exist!");
                }
                sb.Append("</body>");

                await context.Response.WriteAsync(sb.ToString());
                return;
            }

            await _next(context).ConfigureAwait(false);
        }

        private void PrintTimings(ITimingSession session, Guid parentId, StringBuilder sb, double factor, string baseViewPath)
        {
            var timings = session.Timings.Where(s => s.ParentId == parentId);
            foreach (var timing in timings)
            {
                PrintTiming(session, timing, sb, factor, baseViewPath);
            }
        }

        private void PrintTiming(ITimingSession session, ITiming timing, StringBuilder sb, double factor, string baseViewPath)
        {
            sb.Append("<li><span class=\"timing\" style=\"padding-left: ");
            var start = Math.Floor(timing.StartMilliseconds * factor);
            if (start > 300)
            {
                start = 300;
            }
            sb.Append(start);
            sb.Append("px\"><span class=\"bar ");
            sb.Append(timing.Type);
            sb.Append("\" title=\"");
            sb.Append(WebUtility.HtmlEncode(timing.Name.Replace("\r\n", " ")));
            sb.Append("\" style=\"width: ");
            var width = (int)Math.Round(timing.DurationMilliseconds * factor);
            if (width > 300)
            {
                width = 300;
            }
            else if (width == 0)
            {
                width = 1;
            }
            sb.Append(width);
            sb.Append("px\"></span><span class=\"start\">+");
            sb.Append(timing.StartMilliseconds);
            sb.Append("</span><span class=\"duration\">");
            sb.Append(timing.DurationMilliseconds);
            sb.Append("</span></span>");
            var hasChildTimings = session.Timings.Any(s => s.ParentId == timing.Id);
            if (hasChildTimings)
            {
                sb.Append("<input type=\"checkbox\" id=\"t_");
                sb.Append(timing.Id.ToString());
                sb.Append("\" checked=\"checked\" /><label for=\"t_");
                sb.Append(timing.Id.ToString());
                sb.Append("\">");
                PrintDataLink(sb, timing);
                PrintDrillDownLink(sb, timing, baseViewPath);
                sb.Append(WebUtility.HtmlEncode(timing.Name.Replace("\r\n", " ")));
                sb.Append("</label>");
                sb.Append("<ul>");
                PrintTimings(session, timing.Id, sb, factor, baseViewPath);
                sb.Append("</ul>");
            }
            else
            {
                sb.Append("<span class=\"leaf\">");
                PrintDataLink(sb, timing);
                PrintDrillDownLink(sb, timing, baseViewPath);
                sb.Append(WebUtility.HtmlEncode(timing.Name.Replace("\r\n", " ")));
                sb.Append("</span>");
            }
            sb.Append("</li>");
        }

        private void PrintDataLink(StringBuilder sb, ITiming timing)
        {
            if (timing.Data == null || !timing.Data.Any()) return;

            sb.Append("[<a href=\"#data_");
            sb.Append(timing.Id.ToString());
            sb.Append("\" onclick=\"document.getElementById('data_");
            sb.Append(timing.Id.ToString());
            sb.Append("').style.display='block';\" class=\"openModal\">data</a>] ");
        }

        private void PrintDrillDownLink(StringBuilder sb, ITiming timing, string baseViewPath)
        {
            if (timing.Data == null || !timing.Data.ContainsKey("correlationId")) return;

            var correlationId = timing.Data["correlationId"];

            Guid? drillDownSessionId = null;
            if (CoreProfilerMiddleware.DrillDownHandler == null)
            {
                var drillDownSession = ProfilingSession.CircularBuffer.FirstOrDefault(s => s.Data != null && s.Data.ContainsKey("correlationId") && s.Data["correlationId"] == correlationId);
                if (drillDownSession != null) drillDownSessionId = drillDownSession.Id;
            }
            else
            {
                drillDownSessionId = CoreProfilerMiddleware.DrillDownHandler(correlationId);
            }

            if (!drillDownSessionId.HasValue) return;

            sb.Append("[<a href=\"");
            sb.Append(baseViewPath);
            sb.Append("/");
            sb.Append(drillDownSessionId);
            sb.Append("\">drill down</a>] ");
        }

        private void PrintDrillUpLink(StringBuilder sb, ITimingSession session, string baseViewPath)
        {
            if (session.Data == null || !session.Data.ContainsKey("correlationId")) return;

            var correlationId = session.Data["correlationId"];

            Guid? drillUpSessionId = null;
            if (CoreProfilerMiddleware.DrillUpHandler == null)
            {
                var drillUpSession = ProfilingSession.CircularBuffer.FirstOrDefault(s => s.Timings != null && s.Timings.Any(t => t.Data != null && t.Data.ContainsKey("correlationId") && t.Data["correlationId"] == correlationId));
                if (drillUpSession != null) drillUpSessionId = drillUpSession.Id;
            }
            else
            {
                drillUpSessionId = CoreProfilerMiddleware.DrillUpHandler(correlationId);
            }

            if (!drillUpSessionId.HasValue) return;

            sb.Append("[<a href=\"");
            sb.Append(baseViewPath);
            sb.Append("/");
            sb.Append(drillUpSessionId);
            sb.Append("\">drill up</a>] ");
        }

        private async Task ImportSessionsFromUrl(string importUrl)
        {
            IEnumerable<ITimingSession> sessions = null;

            var response = await _httpClient.GetAsync(importUrl);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync();
                sessions = ImportSerializer.DeserializeSessions(content);
            }

            if (sessions == null)
            {
                return;
            }

            if (ProfilingSession.CircularBuffer == null)
            {
                return;
            }

            var existingIds = ProfilingSession.CircularBuffer.Select(session => session.Id).ToList();
            foreach (var session in sessions)
            {
                if (!existingIds.Contains(session.Id))
                {
                    ProfilingSession.CircularBuffer.Add(session);
                }
            }
        }
    }
}
