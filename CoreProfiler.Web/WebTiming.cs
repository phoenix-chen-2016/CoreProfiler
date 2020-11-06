using System;
using System.Collections.Generic;
using CoreProfiler.Timings;

namespace CoreProfiler.Web
{
    /// <summary>
    /// Represents a timing for generic web requests
    /// </summary>
    public class WebTiming : Timing
    {
        private const string WebTimingType = "web";
        private const string CorrelationIdKey = "correlationId";
        private readonly IProfiler _profiler;

        /// <summary>
        /// Gets the correlationId of a web timing.
        /// </summary>
        public string CorrelationId
        {
            get { return Data[CorrelationIdKey]; }
        }

        #region Constructors

        /// <summary>
        /// Initializes a <see cref="WebTiming"/>.
        /// </summary>
        /// <param name="profiler"></param>
        /// <param name="url"></param>
        public WebTiming(IProfiler profiler, string url)
            : base(profiler, WebTimingType, ProfilingSession.ProfilingSessionContainer.CurrentSessionStepId, url, null)
        {
            _profiler = profiler;
            StartMilliseconds = (long)_profiler.Elapsed.TotalMilliseconds;
            Sort = profiler.Elapsed.Ticks;
            Data = new Dictionary<string, string>();
            Data[CorrelationIdKey] = Guid.NewGuid().ToString("N");
        }

        #endregion Constructors

        #region Public Methods

        /// <summary>
        /// Stops the timing.
        /// </summary>
        public void Stop()
        {
            DurationMilliseconds = (long)_profiler.Elapsed.TotalMilliseconds - StartMilliseconds;

            _profiler.GetTimingSession().AddTiming(this);
        }

        #endregion Public Methods
    }
}
