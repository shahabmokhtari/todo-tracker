namespace TodoTracker.Core;

public static class ErrorText
{
    /// <summary>User-facing message without framework decorations such as "(Parameter 'x')".</summary>
    public static string Friendly(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ArgumentException { ParamName: { } name } arg
            ? arg.Message.Replace($" (Parameter '{name}')", string.Empty, StringComparison.Ordinal)
            : exception.Message;
    }
}
