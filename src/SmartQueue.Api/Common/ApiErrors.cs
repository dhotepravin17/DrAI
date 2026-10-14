using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace SmartQueue.Api.Common;

/// <summary>Thrown by feature code to end a request with a specific problem response.</summary>
public sealed class ApiException(int statusCode, string title, string? detail = null) : Exception(title)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;

    public static ApiException NotFound(string title = "Not found.") => new(StatusCodes.Status404NotFound, title);
    public static ApiException Conflict(string title, string? detail = null) => new(StatusCodes.Status409Conflict, title, detail);
    public static ApiException BadRequest(string title, string? detail = null) => new(StatusCodes.Status400BadRequest, title, detail);
}

public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, detail) = exception switch
        {
            ApiException e => (e.StatusCode, e.Title, e.Detail),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,
                "The record was changed by someone else.", "Reload and try again."),
            _ => (0, "", null),
        };
        if (status == 0) return false;

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}

/// <summary>Collects field errors and produces a 400 validation problem.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public Validator Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
        return this;
    }

    public Validator Required(string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return Add(field, "This field is required.");
        if (value.Trim().Length > maxLength) Add(field, $"Must be {maxLength} characters or fewer.");
        return this;
    }

    public Validator Optional(string field, string? value, int maxLength)
    {
        if (value is not null && value.Trim().Length > maxLength) Add(field, $"Must be {maxLength} characters or fewer.");
        return this;
    }

    public Validator Email(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return this;
        var at = value.IndexOf('@');
        if (at < 1 || at == value.Length - 1 || value.Length > 254 || value.Contains(' ')) Add(field, "Enter a valid email address.");
        return this;
    }

    public IResult ToResult() =>
        Results.ValidationProblem(_errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
}

public static class HttpExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("sub") ?? throw new InvalidOperationException("Missing subject claim."));

    public static string? ClientIp(this HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    public static string? TrimToNull(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
