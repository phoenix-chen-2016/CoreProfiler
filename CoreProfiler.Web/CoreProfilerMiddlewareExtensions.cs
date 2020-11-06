using System.Net.Http;
using Microsoft.AspNetCore.Builder;

namespace CoreProfiler.Web
{
    public static class CoreProfilerMiddlewareExtensions
    {
        public static IApplicationBuilder UseCoreProfiler(this IApplicationBuilder builder, bool drillDown = false)
        {
            return builder
                .UseCoreProfleViewer(drillDown)
                .UseMiddleware<CoreProfilerMiddleware>();
        }

        public static IApplicationBuilder UseCoreProfleViewer(this IApplicationBuilder builder, bool drillDown = false)
        {
            CoreProfilerMiddleware.TryToImportDrillDownResult = drillDown;

            return builder.UseMiddleware<CoreProfileViewerMiddleware>(new HttpClient());
        }
    }
}
