using Business;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Web.Custom
{
    public class LogActionFilter : IActionFilter
    {
        private readonly LogManager _logManager;

        public LogActionFilter(LogManager logManager)
        {
            _logManager = logManager;
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
            var controller = context.Controller.GetType().Name;
            var action = context.ActionDescriptor.DisplayName;

            //_logManager.Log($"[ACTION END] {controller} → {action} | Result: {context.Result?.ToString()}");
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            var controller = context.Controller.GetType().Name;
            var action = context.ActionDescriptor.DisplayName;
            var parameters = context.ActionArguments
                .Select(kv => $"{kv.Key}={kv.Value}")
                .ToArray();

            //_logManager.Log($"[ACTION] {controller} → {action} | Params: {string.Join(", ", parameters)}");
        }
    }
}
