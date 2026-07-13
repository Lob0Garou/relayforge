using System.Diagnostics.CodeAnalysis;

namespace RelayForge.Domain.Endpoints;

public readonly record struct DomainError(string Code, string Description);

public readonly record struct DomainResult<T>
{
    private readonly T? _value;

    private DomainResult(T? value, DomainError error, bool isSuccess)
    {
        _value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    public DomainError Error { get; }
    public bool IsSuccess { get; }

    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value!;
        return IsSuccess;
    }

    public static DomainResult<T> Success(T value) => new(value, default, true);
    public static DomainResult<T> Failure(DomainError error) => new(default, error, false);
}
