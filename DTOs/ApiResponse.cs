namespace PropertyTax.API.DTOs;

public class ApiResponse<T>
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public T? Data { get; init; }
    public IEnumerable<string>? Errors { get; init; }

    public static ApiResponse<T> Ok(T data, string message = "Request completed successfully.") =>
        new()
        {
            Success = true,
            Message = message,
            Data = data,
        };

    public static ApiResponse<T> Fail(string message, IEnumerable<string>? errors = null) =>
        new()
        {
            Success = false,
            Message = message,
            Errors = errors,
        };
}