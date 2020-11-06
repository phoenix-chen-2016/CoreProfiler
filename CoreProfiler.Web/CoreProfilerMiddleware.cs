using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;

namespace CoreProfiler.Web
{
    public class CoreProfilerMiddleware
    {
        public const string XCorrelationId = "X-ET-Correlation-Id";

        /// <summary>
        /// The default Html of the view-result index page: ~/coreprofiler/view
        /// </summary>
        public static string ViewResultIndexHeaderHtml = "<h1>CoreProfiler Latest Profiling Results</h1>";

        /// <summary>
        /// The default Html of the view-result page: ~/coreprofiler/view/{uuid}
        /// </summary>
        public static string ViewResultHeaderHtml = "<h1>CoreProfiler Profiling Result</h1>";

        /// <summary>
        /// Tries to import drilldown result by remote address of the step
        /// </summary>
        public static bool TryToImportDrillDownResult;

        private readonly RequestDelegate _next;
        private readonly ILogger _logger;

        /// <summary>
        /// The handler to search for child profiling session by correlationId.
        /// </summary>
        public static Func<string, Guid?> DrillDownHandler { get; set; }

        /// <summary>
        /// The handler to search for parent profiling session by correlationId.
        /// </summary>
        public static Func<string, Guid?> DrillUpHandler { get; set; }

        public CoreProfilerMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
        {
            _next = next;
            _logger = loggerFactory.CreateLogger<CoreProfilerMiddleware>();
        }

        public async Task Invoke(HttpContext context)
        {
            // disable view profiling if CircularBuffer is not enabled
            if (ProfilingSession.CircularBuffer == null)
            {
                await _next.Invoke(context);
                return;
            }

            ClearIfCurrentProfilingSessionStopped();

            var url = UriHelper.GetDisplayUrl(context.Request);
            ProfilingSession.Start(url);

            // set correlationId if exists in header
            var correlationId = GetCorrelationIdFromHeaders(context);
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                ProfilingSession.Current.AddField("correlationId", correlationId);
            }

            try
            {
                await _next.Invoke(context);
            }
            catch (System.Exception)
            {
                // stop and save profiling results on error
                using (ProfilingSession.Current.Step("Stop on Error")) { }

                throw;
            }
            finally
            {
                ProfilingSession.Stop();
            }
        }

        private static void ClearIfCurrentProfilingSessionStopped()
        {
            var profilingSession = ProfilingSession.Current;
            if (profilingSession == null)
            {
                return;
            }

            if (profilingSession.Profiler.IsStopped)
            {
                ProfilingSession.ProfilingSessionContainer.Clear();
            }
        }

        private string GetCorrelationIdFromHeaders(HttpContext context)
        {
            if (context.Request.Headers.Keys.Contains(CoreProfilerMiddleware.XCorrelationId))
            {
                var correlationIds = context.Request.Headers.GetCommaSeparatedValues(CoreProfilerMiddleware.XCorrelationId);
                if (correlationIds != null)
                {
                    return correlationIds.FirstOrDefault();
                }
            }

            return null;
        }
    }
}
