using System;
using System.Text;

namespace EmbeddedNRepl.Hosting;

/// <summary>Structured, bounded evaluation messages in addition to ordinary output slices.</summary>
public interface IReplEvaluationOutput : IReplOutput
{
    /// <summary>A nonblocking counter callback which must not retain a connection, session or evaluation payload.</summary>
    Action<int>? LateOutput
    {
        get;
    }
    bool TryWriteValue(string value, string ns, bool truncated);
    void ReportError(ReplEvaluationError error);
    void MarkTruncated();
}

public sealed class ReplEvaluationError
{
    public ReplEvaluationError(string code, string phase, string exceptionType, string rootExceptionType,
        string message, string sourceName, long line, long column)
    {
        Code = code == "namespace-not-found" ? code : "eval-error";
        Phase = Limit(phase, 32);
        ExceptionType = Limit(exceptionType, 256);
        RootExceptionType = Limit(rootExceptionType, 256);
        Message = Limit(message, 1024);
        SourceName = Limit(sourceName, 512);
        Line = Math.Max(1, line);
        Column = Math.Max(1, column);
    }
    public string Code
    {
        get;
    }
    public string Phase
    {
        get;
    }
    public string ExceptionType
    {
        get;
    }
    public string RootExceptionType
    {
        get;
    }
    public string Message
    {
        get;
    }
    public string SourceName
    {
        get;
    }
    public long Line
    {
        get;
    }
    public long Column
    {
        get;
    }
    internal static string Limit(string text, int quota)
    {
        if (text == null)
            return "";
        var result = new StringBuilder(Math.Min(text.Length, quota));
        var bytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var size = pair ? 4 : c < 128 ? 1 : c < 2048 ? 2 : 3;
            if (bytes + size > quota)
                break;
            bytes += size;
            if (pair)
            {
                result.Append(c).Append(text[++i]);
            }
            else
                result.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
        }
        return result.ToString();
    }
}
