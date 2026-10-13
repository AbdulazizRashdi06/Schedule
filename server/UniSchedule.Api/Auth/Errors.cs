namespace UniSchedule.Api.Auth;

/// <summary>Errors the app shows as-is: { "error": "…" }.</summary>
public static class Errors
{
    public static IResult BadRequest(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);
    public static IResult Unauthorized(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status401Unauthorized);
    public static IResult Forbidden(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);
    public static IResult NotFound(string message = "Not found.") => Results.Json(new { error = message }, statusCode: StatusCodes.Status404NotFound);
    public static IResult Conflict(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status409Conflict);
    public static IResult TooMany(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status429TooManyRequests);
    public static IResult Unavailable(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status503ServiceUnavailable);
}
