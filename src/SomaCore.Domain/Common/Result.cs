namespace SomaCore.Domain.Common;

/// <summary>
/// Lightweight result wrapper for operations that can fail in known ways
/// (token refresh returned 401, signature didn't validate, etc.). Throw
/// exceptions for unexpected failures; return <see cref="Result{T}"/> for
/// expected ones, per <c>docs/conventions.md</c>.
/// </summary>
public readonly record struct Result<T>
{
    public T? Value { get; }
    public string? Error { get; }
    public bool IsSuccess => Error is null;

    private Result(T? value, string? error)
    {
        Value = value;
        Error = error;
    }

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("Error message must be non-empty.", nameof(error));
        }

        return new Result<T>(default, error);
    }
}
