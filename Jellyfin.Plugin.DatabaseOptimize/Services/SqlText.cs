using System.Linq;

namespace Jellyfin.Plugin.DatabaseOptimize.Services;

/// <summary>
/// Helpers to build SQL text from names read from the schema.
/// </summary>
internal static class SqlText
{
    /// <summary>
    /// Quotes an identifier (table or column name).
    /// </summary>
    /// <param name="identifier">The identifier.</param>
    /// <returns>The quoted identifier.</returns>
    public static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", System.StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Builds a string literal.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The quoted literal.</returns>
    public static string Literal(string value)
        => "'" + value.Replace("'", "''", System.StringComparison.Ordinal) + "'";

    /// <summary>
    /// Checks that a name can be safely embedded in a statement. Names with braces or control
    /// characters are skipped, they never occur in the Jellyfin schema.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns><c>true</c> when the name is usable.</returns>
    public static bool IsSafeIdentifier(string name)
        => !string.IsNullOrWhiteSpace(name)
           && !name.Any(c => c is '{' or '}' || char.IsControl(c));
}
