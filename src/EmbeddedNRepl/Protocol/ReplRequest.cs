using System;
using System.Collections.Generic;
using System.Text;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl.Protocol;

internal sealed class ReplRequest
{
    public string Op { get; private set; } = "";
    public string? Id
    {
        get; private set;
    }
    public string? Session
    {
        get; private set;
    }
    public ReplEvaluation? Evaluation
    {
        get; private set;
    }
    public static ReplRequest Parse(IReadOnlyDictionary<string, object> message, ReplLimits limits)
    {
        if (message.TryGetValue("id", out var id) &&
            (!(id is string text) || Encoding.UTF8.GetByteCount(text) > limits.RequestIdBytes))
            throw new ReplRequestException("embedded-nrepl/invalid-request", closeConnection: true);
        var request = new ReplRequest { Id = id as string };
        request.Op = Text(message, "op", required: true)!;
        if (request.Op.Length == 0)
            throw Invalid();
        request.Session = Text(message, "session");
        if (request.Op != "eval")
            return request;
        var printing = Printing(message);
        foreach (var field in message.Keys)
        {
            if (field == "nrepl.middleware.print/quota" || field == "nrepl.middleware.print/print" ||
                field == "nrepl.middleware.print/options")
                continue;
            if (field == "eval" || field == "read" || field == "reader" || field == "readers" ||
                field == "print" || field == "print-fn" || field == "reader-resolver" || field == "default-data-reader-fn" ||
                field.StartsWith("nrepl.middleware.", StringComparison.Ordinal) ||
                field.StartsWith("cider.", StringComparison.Ordinal) || field.StartsWith("cider/", StringComparison.Ordinal))
                throw Unsupported(field);
        }
        var source = Text(message, "code", required: true)!;
        var ns = Text(message, "ns");
        var name = Text(message, "file-name");
        var path = Text(message, "file");
        request.Evaluation = new ReplEvaluation(source, ns, path ?? name ?? "<nrepl-eval>",
            PositiveInteger(message, "line", 1), PositiveInteger(message, "column", 1),
            Math.Min(limits.ValueBytes, PositiveInteger(message, "nrepl.middleware.print/quota", limits.ValueBytes)), printing);
        return request;
    }
    private static ReplPrintOptions? Printing(IReadOnlyDictionary<string, object> message)
    {
        var printer = Text(message, "nrepl.middleware.print/print");
        if (printer == null)
        {
            if (message.ContainsKey("nrepl.middleware.print/options"))
                throw Unsupported("nrepl.middleware.print/options without a printer");
            return null;
        }
        if (printer != "cider.nrepl.pprint/pprint" && printer != "cider.nrepl.pprint/clojure-pprint")
            throw Unsupported("printer " + ReplEvaluationError.Limit(printer, 128));
        if (!message.TryGetValue("nrepl.middleware.print/options", out var raw))
            return new ReplPrintOptions();
        if (!(raw is IReadOnlyDictionary<string, object> options))
            throw Invalid();
        foreach (var option in options.Keys)
            if (option != "right-margin" && option != "length" && option != "level")
                throw Unsupported("print option " + ReplEvaluationError.Limit(option, 128));
        return new ReplPrintOptions(PositiveInteger(options, "right-margin", 80),
            NonNegativeInteger(options, "length"), NonNegativeInteger(options, "level"));
    }
    private static int? NonNegativeInteger(IReadOnlyDictionary<string, object> options, string key)
    {
        if (!options.TryGetValue(key, out var value))
            return null;
        if (!(value is long number) || number < 0 || number > int.MaxValue)
            throw Invalid();
        return (int)number;
    }
    private static ReplRequestException Unsupported(string option) => new ReplRequestException("embedded-nrepl/unsupported-option",
        detail: "EmbeddedNRepl does not support " + option + ". Supported printer: cider.nrepl.pprint/pprint (right-margin, length, level).");
    private static string? Text(IReadOnlyDictionary<string, object> message, string field, bool required = false)
    {
        if (!message.TryGetValue(field, out var value))
        {
            if (required)
                throw Invalid();
            return null;
        }
        return value as string ?? throw Invalid();
    }
    private static int PositiveInteger(IReadOnlyDictionary<string, object> message, string field, int fallback)
    {
        if (!message.TryGetValue(field, out var value))
            return fallback;
        if (!(value is long number) || number <= 0 || number > int.MaxValue)
            throw Invalid();
        return (int)number;
    }
    private static ReplRequestException Invalid() => new ReplRequestException("embedded-nrepl/invalid-request");
}

internal sealed class ReplRequestException : Exception
{
    public ReplRequestException(string code, bool closeConnection = false, string? detail = null)
        : base(detail ?? "Invalid nREPL request: " + code)
    {
        Code = code;
        CloseConnection = closeConnection;
    }
    public string Code
    {
        get;
    }
    public bool CloseConnection
    {
        get;
    }
}
