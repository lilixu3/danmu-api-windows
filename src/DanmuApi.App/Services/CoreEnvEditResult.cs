namespace DanmuApi.App.Services;

public enum CoreEnvEditAction
{
    Cancel,
    Set,
    Keep,
    Delete,
}

public sealed record CoreEnvEditResult(CoreEnvEditAction Action, string? Value)
{
    public static CoreEnvEditResult Cancel() => new(CoreEnvEditAction.Cancel, null);

    public static CoreEnvEditResult Set(string value) => new(CoreEnvEditAction.Set, value);

    public static CoreEnvEditResult Keep() => new(CoreEnvEditAction.Keep, null);

    public static CoreEnvEditResult Delete() => new(CoreEnvEditAction.Delete, null);
}
