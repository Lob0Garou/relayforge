namespace RelayForge.Domain.Endpoints;

public readonly record struct DomainError(string Code, string Description);

public readonly record struct DomainResult<T>
{
    private DomainResult(T? value, DomainError error, bool isSuccess)
    {
        Value = value!;
        Error = error;
        IsSuccess = isSuccess;
    }

    public T Value { get; }
    public DomainError Error { get; }
    public bool IsSuccess { get; }

    public static DomainResult<T> Success(T value) => new(value, default, true);
    public static DomainResult<T> Failure(DomainError error) => new(default, error, false);
}
