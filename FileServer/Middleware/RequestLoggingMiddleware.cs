using FileServer.Services;

namespace FileServer.Middleware
{
    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IServerStatusService statusService)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                await _next(context);
            }
            finally
            {
                var elapsed = DateTime.UtcNow - startTime;
                _logger.LogInformation(
                    "请求: {Method} {Path} - 状态: {StatusCode} - 耗时: {Elapsed}ms",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    elapsed.TotalMilliseconds);
            }
        }
    }
}