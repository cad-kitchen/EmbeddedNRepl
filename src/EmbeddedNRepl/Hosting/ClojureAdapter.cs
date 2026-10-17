using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace EmbeddedNRepl.Hosting;

/// <summary>Attaches the embedded language adapter to an initialized, host-owned ClojureCLR runtime.</summary>
public sealed class ClojureAdapter
{
    private static readonly string[] Resources =
    {
        "embedded-nrepl.session.cljr",
        "embedded-nrepl.printing.cljr",
        "embedded-nrepl.evaluation.cljr",
        "embedded-nrepl.runtime.cljr"
    };

    private ClojureAdapter(IReplEvaluator evaluator, IReplSessionStateFactory sessions)
    {
        Evaluator = evaluator;
        Sessions = sessions;
    }

    public IReplEvaluator Evaluator
    {
        get;
    }
    public IReplSessionStateFactory Sessions
    {
        get;
    }

    /// <summary>
    /// Call on the runtime's owner thread, serialized with other language work.
    /// loadString must invoke clojure.core/load-string in the existing runtime.
    /// The host initializes that runtime and the initial namespace before attachment.
    /// Loading preserves thread bindings; existing adapter definitions are reused.
    /// </summary>
    public static ClojureAdapter Attach(string runtimeGroupId, Func<string, object> loadString,
        string initialNamespace = "user")
    {
        if (string.IsNullOrWhiteSpace(runtimeGroupId))
            throw new ArgumentException("Runtime identity is required.", nameof(runtimeGroupId));
        if (loadString == null)
            throw new ArgumentNullException(nameof(loadString));
        if (string.IsNullOrWhiteSpace(initialNamespace))
            throw new ArgumentException("Initial namespace is required.", nameof(initialNamespace));

        var source = new StringBuilder(
            "(clojure.core/let [frame (clojure.lang.Var/getThreadBindingFrame)] (try " +
            "(clojure.core/when-not (clojure.core/when-let [n (clojure.core/find-ns 'embedded-nrepl.runtime)] (clojure.core/ns-resolve n 'create-adapter)) ");
        foreach (var name in Resources)
        {
            using var stream = typeof(ClojureAdapter).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Embedded adapter source is missing: " + name);
            using var reader = new StreamReader(stream);
            source.Append("(clojure.core/load-string ").Append(Quote(reader.ReadToEnd())).Append(") ");
        }
        source.Append(") (clojure.core/to-array ((clojure.core/ns-resolve (clojure.core/find-ns 'embedded-nrepl.runtime) 'create-adapter) ")
            .Append(Quote(runtimeGroupId)).Append(' ').Append(Quote(initialNamespace))
            .Append(")) (finally (clojure.lang.Var/resetThreadBindingFrame frame))))");
        var result = loadString(source.ToString()) as object[];
        if (result == null || result.Length != 2 || !(result[0] is IReplEvaluator evaluator) ||
            !(result[1] is IReplSessionStateFactory sessions))
            throw new InvalidOperationException("The host did not return the embedded ClojureCLR adapter contracts.");
        return new ClojureAdapter(evaluator, sessions);
    }

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character == '\\' || character == '"')
                text.Append('\\').Append(character);
            else if (char.IsControl(character))
                text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else
                text.Append(character);
        }
        return text.Append('"').ToString();
    }
}
