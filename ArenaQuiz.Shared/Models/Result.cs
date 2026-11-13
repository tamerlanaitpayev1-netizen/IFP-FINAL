namespace ArenaQuiz.Shared.Models;

/// <summary>
/// Immutable functional Result monad for explicit error propagation without exception-driven control flow.
/// </summary>
/// <typeparam name="T">Success payload type.</typeparam>
public sealed record Result<T>(bool IsSuccess, T? Value, string? Error)
{
    public static Result<T> Success(T value) => new(true, value, null);

    public static Result<T> Failure(string error) => new(false, default, error);

    /// <summary>
    /// Transforms the inner value if the result represents success.
    /// </summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> mapper) =>
        IsSuccess && Value is not null
            ? Result<TOut>.Success(mapper(Value))
            : Result<TOut>.Failure(Error ?? "Unknown error.");

    /// <summary>
    /// Exhaustively reduces the Result into a single output value.
    /// </summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<string, TOut> onFailure) =>
        IsSuccess && Value is not null
            ? onSuccess(Value)
            : onFailure(Error ?? "Unknown error.");
}

