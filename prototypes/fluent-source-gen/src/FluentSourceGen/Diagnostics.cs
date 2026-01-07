using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Verbosity level for diagnostic reporting.
/// </summary>
public enum DiagnosticVerbosity
{
    /// <summary>Only report errors.</summary>
    Quiet,

    /// <summary>Report errors and warnings (default).</summary>
    Normal,

    /// <summary>Report all diagnostics including info (e.g., "file generated").</summary>
    Verbose
}

/// <summary>
/// Configuration options for diagnostic reporting.
/// </summary>
public sealed class DiagnosticOptions
{
    /// <summary>
    /// Default diagnostic options with "FSG" prefix and Normal verbosity.
    /// </summary>
    public static DiagnosticOptions Default { get; } = new();

    /// <summary>
    /// The prefix for diagnostic IDs (e.g., "FSG" produces "FSG001").
    /// </summary>
    public string IdPrefix { get; init; } = "FSG";

    /// <summary>
    /// The category for diagnostics shown in the IDE.
    /// </summary>
    public string Category { get; init; } = "FluentSourceGen";

    /// <summary>
    /// The verbosity level controlling which diagnostics are reported.
    /// </summary>
    public DiagnosticVerbosity Verbosity { get; init; } = DiagnosticVerbosity.Normal;
}

/// <summary>
/// Logger for reporting diagnostics during source generation.
/// Create one instance per generator and use <see cref="For"/> to create scoped loggers for callbacks.
/// </summary>
public sealed class DiagnosticLogger
{
    readonly DiagnosticOptions _options;

    /// <summary>
    /// Creates a new diagnostic logger with the specified options.
    /// </summary>
    public DiagnosticLogger(DiagnosticOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Gets the diagnostic options.
    /// </summary>
    public DiagnosticOptions Options => _options;

    /// <summary>
    /// Creates a scoped logger bound to the specified source production context.
    /// </summary>
    public ScopedLogger For(SourceProductionContext spc) => new(spc, _options);

    /// <summary>
    /// Formats a numeric ID with the configured prefix (e.g., 1 becomes "FSG001").
    /// </summary>
    internal string FormatId(int id) => $"{_options.IdPrefix}{id:D3}";
}

/// <summary>
/// A logger scoped to a specific <see cref="SourceProductionContext"/>.
/// Provides ILogger-style methods for reporting diagnostics.
/// </summary>
public readonly struct ScopedLogger
{
    readonly SourceProductionContext _spc;
    readonly DiagnosticOptions _options;

    internal ScopedLogger(SourceProductionContext spc, DiagnosticOptions options)
    {
        _spc = spc;
        _options = options;
    }

    #region Info

    /// <summary>
    /// Reports an informational diagnostic. Only reported when verbosity is Verbose.
    /// </summary>
    /// <param name="id">The numeric diagnostic ID (e.g., 1 becomes "FSG001").</param>
    /// <param name="message">The message with named placeholders (e.g., "Generated {FileName}").</param>
    /// <param name="args">The arguments to substitute into placeholders.</param>
    public void Info(int id, string message, params object?[] args)
        => Info(Location.None, id, message, args);

    /// <summary>
    /// Reports an informational diagnostic at a specific location. Only reported when verbosity is Verbose.
    /// </summary>
    /// <param name="location">The source location for the diagnostic.</param>
    /// <param name="id">The numeric diagnostic ID (e.g., 1 becomes "FSG001").</param>
    /// <param name="message">The message with named placeholders (e.g., "Generated {FileName}").</param>
    /// <param name="args">The arguments to substitute into placeholders.</param>
    public void Info(Location? location, int id, string message, params object?[] args)
    {
        if (_options.Verbosity < DiagnosticVerbosity.Verbose)
            return;

        Report(DiagnosticSeverity.Info, location, id, message, args);
    }

    #endregion

    #region Warning

    /// <summary>
    /// Reports a warning diagnostic. Only reported when verbosity is Normal or higher.
    /// </summary>
    /// <param name="id">The numeric diagnostic ID (e.g., 1 becomes "FSG001").</param>
    /// <param name="message">The message with named placeholders (e.g., "Type {TypeName} should be partial").</param>
    /// <param name="args">The arguments to substitute into placeholders.</param>
    public void Warning(int id, string message, params object?[] args)
        => Warning(Location.None, id, message, args);

    /// <summary>
    /// Reports a warning diagnostic at a specific location. Only reported when verbosity is Normal or higher.
    /// </summary>
    /// <param name="location">The source location for the diagnostic.</param>
    /// <param name="id">The numeric diagnostic ID (e.g., 1 becomes "FSG001").</param>
    /// <param name="message">The message with named placeholders (e.g., "Type {TypeName} should be partial").</param>
    /// <param name="args">The arguments to substitute into placeholders.</param>
    public void Warning(Location? location, int id, string message, params object?[] args)
    {
        if (_options.Verbosity < DiagnosticVerbosity.Normal)
            return;

        Report(DiagnosticSeverity.Warning, location, id, message, args);
    }

    #endregion

    #region Error

    /// <summary>
    /// Reports an error diagnostic. Always reported regardless of verbosity.
    /// </summary>
    /// <param name="id">The numeric diagnostic ID (e.g., 1 becomes "FSG001").</param>
    /// <param name="message">The message with named placeholders (e.g., "Failed to generate {TypeName}").</param>
    /// <param name="args">The arguments to substitute into placeholders.</param>
    public void Error(int id, string message, params object?[] args)
        => Error(Location.None, id, message, args);

    /// <summary>
    /// Reports an error diagnostic at a specific location. Always reported regardless of verbosity.
    /// </summary>
    /// <param name="location">The source location for the diagnostic.</param>
    /// <param name="id">The numeric diagnostic ID (e.g., 1 becomes "FSG001").</param>
    /// <param name="message">The message with named placeholders (e.g., "Failed to generate {TypeName}").</param>
    /// <param name="args">The arguments to substitute into placeholders.</param>
    public void Error(Location? location, int id, string message, params object?[] args)
    {
        // Errors are always reported regardless of verbosity
        Report(DiagnosticSeverity.Error, location, id, message, args);
    }

    #endregion

    #region Internal

    void Report(DiagnosticSeverity severity, Location? location, int id, string message, object?[] args)
    {
        var formattedId = $"{_options.IdPrefix}{id:D3}";
        var (title, messageFormat) = ConvertNamedPlaceholders(message);

        var descriptor = new DiagnosticDescriptor(
            formattedId,
            title,
            messageFormat,
            _options.Category,
            severity,
            isEnabledByDefault: true);

        _spc.ReportDiagnostic(Diagnostic.Create(descriptor, location ?? Location.None, args));
    }

    /// <summary>
    /// Converts named placeholders like {TypeName} to positional {0}, {1}, etc.
    /// Also extracts a title from the message.
    /// </summary>
    static (string Title, string MessageFormat) ConvertNamedPlaceholders(string message)
    {
        // Extract title: take first sentence or up to first placeholder
        var titleEnd = message.IndexOfAny(['.', '{']);
        var title = titleEnd > 0 ? message[..titleEnd].Trim() : message;
        if (title.Length > 50)
            title = title[..47] + "...";

        // Convert {Name} to {0}, {1}, etc.
        var index = 0;
        var messageFormat = Regex.Replace(message, @"\{[A-Za-z_]\w*\}", _ => $"{{{index++}}}");

        return (title, messageFormat);
    }

    #endregion
}
