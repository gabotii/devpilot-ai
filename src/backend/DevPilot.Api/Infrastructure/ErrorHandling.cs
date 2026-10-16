using System.Diagnostics;
using DevPilot.Application.Ai;
using DevPilot.Application.Common;
using DevPilot.Application.Operations;
using DevPilot.Domain.Common;
using DevPilot.Domain.Organizations;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Api.Infrastructure;

/// <summary>Maps application/domain exceptions to RFC 9457 problem details. Unexpected errors never leak internals.</summary>
public sealed partial class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var problem = exception switch
        {
            ValidationException validation => ValidationProblem(validation),
            NotFoundException notFound => Problem(404, "Not Found", notFound.Message, "not_found"),
            UnauthorizedException unauthorized => Problem(401, "Unauthorized", unauthorized.Message, "auth.unauthorized"),
            ForbiddenException forbidden => Problem(403, "Forbidden", forbidden.Message, "forbidden"),
            ForbiddenDomainException forbidden => Problem(403, "Forbidden", forbidden.Message, "forbidden"),
            ConflictException conflict => Problem(409, "Conflict", conflict.Message, conflict.Code),
            DbUpdateConcurrencyException => Problem(409, "Conflict", "The resource was modified concurrently. Reload and try again.", "concurrency"),
            DomainException domain => Problem(422, "Business rule violated", domain.Message, domain.Code),
            DependencyUnavailableException dependency => DependencyProblem(httpContext, dependency),
            AiServiceException ai => AiProblem(httpContext, ai),
            IngestAuthException ingest => Problem(401, "Unauthorized", ingest.Message, "ingest.unauthorized"),
            System.Security.Cryptography.CryptographicException => Problem(500, "Internal Server Error", "A stored secret could not be decrypted. Check Secrets:EncryptionKey.", "secrets.decrypt_failed"),
            BadHttpRequestException badRequest => Problem(badRequest.StatusCode, "Bad Request", "The request could not be read. Check the JSON body and parameters.", "bad_request"),
            _ => null,
        };

        if (problem is null)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
            problem = Problem(500, "Internal Server Error", "An unexpected error occurred. Use the traceId when reporting it.", "internal");
        }

        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = problem.Status ?? 500;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
    }

    private static ProblemDetails Problem(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        return problem;
    }

    private static HttpValidationProblemDetails ValidationProblem(ValidationException exception)
    {
        var errors = exception.Errors
            .GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? "request" : char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..])
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        var problem = new HttpValidationProblemDetails(errors) { Status = 400, Title = "Validation failed", Detail = "One or more fields are invalid." };
        problem.Extensions["code"] = "validation";
        return problem;
    }

    private static ProblemDetails DependencyProblem(HttpContext context, DependencyUnavailableException exception)
    {
        if (exception.RetryAfter is { } retry)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var problem = Problem(503, "Service Unavailable", exception.Message, "dependency.unavailable");
        problem.Extensions["dependency"] = exception.Dependency;
        return problem;
    }

    private static ProblemDetails AiProblem(HttpContext context, AiServiceException exception)
    {
        if (exception.RetryAfter is { } retry)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var status = exception.StatusCode is 422 or 504 ? exception.StatusCode : 503;
        var problem = Problem(status, status == 422 ? "AI request declined" : "AI unavailable", exception.Message, "ai." + exception.Code);
        problem.Extensions["dependency"] = "ai-service";
        return problem;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);
}
