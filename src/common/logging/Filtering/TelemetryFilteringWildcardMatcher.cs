using System.Text.RegularExpressions;
using static System.StringSplitOptions;

namespace FlorisDeV.Logging.Filtering;

/// <summary>
///   A instanced used for filtering telemetry operations.
///   The following wildcards are supported: '*', '?'.
/// </summary>
internal record TelemetryFilteringWildcardMatcher
{
    /// <summary>The <see cref="Target"/> converted to a regular expression</summary>
    private Regex Expression { get; }

    /// <summary>A wildcard expression to be matched (e.g. https://*.azure.com/*)</summary>
    public string Target { get; }

    /// <summary>A method name (e.g. GET, POST, OPTIONS)</summary>
    public string? Operation { get; }

    /// <summary>
    ///   A instanced used for filtering telemetry operations.
    ///   The following wildcards are supported: '*', '?'.
    /// </summary>
    private TelemetryFilteringWildcardMatcher(string target, string? operation)
    {
        Target = target;
        Operation = operation;
        Expression = BuildExpression(target);
    }

    /// <summary>
    ///   Creates a new matcher for the given operation (usually an url)
    /// </summary>
    /// <param name="operation">The expression to be matched (e.g. GET http://*.azure.com/*)</param>
    /// <returns>An instance of <see cref="TelemetryFilteringWildcardMatcher" /></returns>
    /// <exception cref="ArgumentException"></exception>
    public static TelemetryFilteringWildcardMatcher Create(string operation)
    {
        if (string.IsNullOrEmpty(operation))
        {
            throw new ArgumentException(
                "Expected '[GET|POST|...] <path or url with wildcards (*,?)>'",
                nameof(operation));
        }

        var parts = operation.Split(' ', 2, TrimEntries | RemoveEmptyEntries);

        if (parts is [var method, var target])
        {
            return new TelemetryFilteringWildcardMatcher(target, method);
        }

        return new TelemetryFilteringWildcardMatcher(parts[0], null);
    }

    /// <summary>
    ///   Determines if the supplied operation matches the current instance
    /// </summary>
    /// <param name="operation">
    ///     A method name (e.g. GET). If the instance <see cref="Operation"/> is
    ///     null, the supplied method is ignored and the match is successful
    /// </param>
    public bool IsOperationMatch(string? operation)
    {
        if (Operation == null)
        {
            return true;
        }

        return string.Equals(Operation, operation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///   Determines if the supplied value matches the current instance
    /// </summary>
    /// <param name="value">Usually an absolute path or url</param>
    public bool IsTargetMatch(string? value)
    {
        // FileSystemName.MatchesSimpleExpression can also be used
        // here but it is between 10 to 100 times slower than regex
        return Expression.IsMatch(value ?? string.Empty);
    }

    private static Regex BuildExpression(string wildcard)
    {
        var pattern = Regex.Escape(wildcard).Replace("\\*", ".*").Replace("\\?", ".?");
        return new Regex('^' + pattern + '$', RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}