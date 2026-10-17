using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.IO;
using System.Text;
using EmbeddedNRepl.Hosting;
using clojure.lang;
using clojure.lang.Runtime;
using NUnit.Framework;

namespace EmbeddedNRepl.ClojureClr.Tests;

[NonParallelizable]
public sealed class EvaluatorTests
{
    private IReplSessionStateFactory sessions = null!;
    private IReplEvaluator evaluator = null!;
    private IReplSessionState state = null!;
    private Namespace initial = null!;
    private object frame = null!;
    private RecordingOutput output = null!;
    private readonly ReplExecutionContext context = new ReplExecutionContext("eval-runtime", null, null, null, ReplExecutionMode.Application);

    [SetUp]
    public void PrepareOwner()
    {
        RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)"));
        frame = Var.getThreadBindingFrame();
        initial = Namespace.findOrCreate(Symbol.intern("embedded-nrepl.eval." + Guid.NewGuid().ToString("N")));
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, initial));
        try
        {
            RT.var("clojure.core", "refer").invoke(Symbol.intern("clojure.core"));
        }
        finally { Var.resetThreadBindingFrame(frame); }
        (evaluator, sessions) = ClojureModule.Create(context.RuntimeGroupId, initial.Name.ToString());
        state = sessions.Create();
        output = new RecordingOutput();
    }
    [TearDown]
    public void RestoreOwner()
    {
        if (frame != null)
            Var.resetThreadBindingFrame(frame);
        if (initial != null)
            Namespace.remove(initial.Name);
    }
    private ReplEvaluationOutcome Run(string code, string? ns = null, string? file = null, int quota = 65536,
        CancellationToken token = default, int line = 1, int column = 1)
        => evaluator.EvaluateAsync(new ReplEvaluation(code, ns, file, line, column, quota), state, context, output, token).GetAwaiter().GetResult();

    [Test]
    public void OutputStreamsDuringExecutionInOrderAndInputIsFiniteEof()
    {
        var ownerOut = new StringWriter();
        var ownerIn = new StringReader("unexpected owner input");
        Var.pushThreadBindings(RT.map(RT.OutVar, ownerOut, RT.ErrVar, ownerOut, RT.InVar, ownerIn));
        try
        {
            var eventsSeenDuringEval = 0;
            initial.intern(Symbol.intern("check-output")).bindRoot(new Callback(() =>
            {
                eventsSeenDuringEval = output.Events.Count;
                Assert.That(string.Concat(output.Events.Select(e => e.Text)), Is.EqualTo("markererror"));
                return null!;
            }));
            Assert.That(Run("(do (print \"marker\") (flush) (binding [*out* *err*] (print \"error\")) (check-output) (throw (System.Exception. \"after output\")))"), Is.EqualTo(ReplEvaluationOutcome.Failed));
            Assert.That(eventsSeenDuringEval, Is.GreaterThanOrEqualTo(2));
            Assert.That(output.Events.First().Kind, Is.EqualTo(ReplOutputKind.Out));
            Assert.That(output.Events.Last().Kind, Is.EqualTo(ReplOutputKind.Error));
            output.Events.Clear();
            Run("(read-line)");
            Assert.That(output.Values, Is.EqualTo(new[] { "nil" }));
            Assert.That(ownerOut.ToString(), Is.Empty);
        }
        finally { Var.resetThreadBindingFrame(frame); }
    }

    [Test]
    public void LargeAndSplitUnicodeWritesUseBoundedChunksAndLateWritersCannotReachAnotherRequest()
    {
        Run("(def saved-out *out*)");
        var oldWriter = (TextWriter)initial.FindInternedVar(Symbol.intern("saved-out")).deref();
        output.Events.Clear();
        initial.intern(Symbol.intern("write-large")).bindRoot(new Callback(() =>
        {
            var writer = (TextWriter)RT.OutVar.deref();
            writer.Write(new string('x', 100000));
            writer.Write('\uD83D');
            writer.Write('\uDE00');
            return null!;
        }));
        Assert.That(Run("(write-large)"), Is.EqualTo(ReplEvaluationOutcome.Succeeded));
        var stdout = output.Events.Where(e => e.Kind == ReplOutputKind.Out).ToArray();
        Assert.That(string.Concat(stdout.Select(e => e.Text)), Is.EqualTo(new string('x', 100000) + "😀"));
        Assert.That(stdout.All(e => new UTF8Encoding(false, true).GetByteCount(e.Text) <= 16384), Is.True);
        output.Events.Clear();
        oldWriter.Write("late previous output");
        oldWriter.Flush();
        Assert.That(output.Events, Is.Empty);
        Assert.That(output.LateCharacters, Is.EqualTo("late previous output".Length));
    }

    private sealed class Callback : AFn
    {
        private readonly Func<object> action;
        public Callback(Func<object> action) => this.action = action;
        public override object invoke() => action();
    }

    [Test]
    public void IntegrationMismatchIsRejectedBeforeUserCodeAndHostileExceptionMessagesAreContained()
    {
        var otherContext = new ReplExecutionContext("another-runtime", null, null, null, ReplExecutionMode.Application);
        Assert.Throws<ArgumentException>(() => evaluator.EvaluateAsync(new ReplEvaluation("(def forbidden 1)"), state, otherContext, output, CancellationToken.None));
        Assert.That(initial.FindInternedVar(Symbol.intern("forbidden")), Is.Null);
        initial.intern(Symbol.intern("throw-hostile")).bindRoot(new Callback(() => throw new HostileException()));
        Assert.That(Run("(throw-hostile)"), Is.EqualTo(ReplEvaluationOutcome.Failed));
        Assert.That(output.Errors.Last().Message, Is.EqualTo("Exception message unavailable."));
        Run("42");
        Assert.That(output.Values.Last(), Is.EqualTo("42"));
    }
    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("unsafe message getter");
    }

    [Test]
    public void CancellationIsCooperativeAndDoesNotRewriteAnIgnoredSignalAsInterruption()
    {
        using var cancellation = new CancellationTokenSource();
        initial.intern(Symbol.intern("cancel-now")).bindRoot(new Callback(() => { cancellation.Cancel(); return 42L; }));
        Assert.That(Run("(cancel-now)", token: cancellation.Token), Is.EqualTo(ReplEvaluationOutcome.Succeeded));
        Assert.That(output.Values.Last(), Is.EqualTo("42"));
        output.Events.Clear();
        Assert.That(Run("(def never-started true)", token: cancellation.Token), Is.EqualTo(ReplEvaluationOutcome.Interrupted));
        Assert.That(initial.FindInternedVar(Symbol.intern("never-started")), Is.Null);
        using var cooperative = new CancellationTokenSource();
        initial.intern(Symbol.intern("cancel-cooperatively")).bindRoot(new Callback(() =>
        {
            cooperative.Cancel();
            RT.var("embedded-nrepl.runtime", "check-cancelled!").invoke();
            return null!;
        }));
        Assert.That(Run("(cancel-cooperatively) (def never-finished true)", token: cooperative.Token), Is.EqualTo(ReplEvaluationOutcome.Interrupted));
        Assert.That(initial.FindInternedVar(Symbol.intern("never-finished")), Is.Null);
        Assert.DoesNotThrow(() => RT.var("embedded-nrepl.runtime", "check-cancelled!").invoke());
        using var failed = new CancellationTokenSource();
        initial.intern(Symbol.intern("cancel-and-fail")).bindRoot(new Callback(() =>
        {
            failed.Cancel();
            throw new InvalidOperationException("failure after cancellation");
        }));
        Assert.That(Run("(cancel-and-fail)", token: failed.Token), Is.EqualTo(ReplEvaluationOutcome.Failed));
        Run("(+ 20 22)");
        Assert.That(output.Values.Last(), Is.EqualTo("42"));
    }

    [Test]
    public void PrintingClampsCollectionDepthLengthAndUtf8BytesWithoutChangingSessionSettings()
    {
        Assert.That(Run("(range 1000)"), Is.EqualTo(ReplEvaluationOutcome.Succeeded));
        Assert.That(output.Values.Single(), Does.EndWith("99 ...)"));
        Assert.That(output.Truncated, Is.True);
        output = new RecordingOutput();
        Run("(set! *print-length* 2) (range 5)");
        Assert.That(output.Values.Last(), Is.EqualTo("(0 1 ...)"));
        output = new RecordingOutput();
        Run("(set! *print-length* nil) (set! *print-level* 2) [[[1]]]");
        Assert.That(output.Values.Last(), Is.EqualTo("[[#]]"));
        Assert.That(output.Truncated, Is.True);
        output = new RecordingOutput();
        Run("[\"...\" \"#\"]");
        Assert.That(output.Truncated, Is.False);
        output = new RecordingOutput();
        Run("(apply str (repeat 10000 \"😀\"))", quota: 31);
        Assert.That(output.Values, Has.Length.EqualTo(1));
        Assert.That(new UTF8Encoding(false, true).GetByteCount(output.Values[0]), Is.LessThanOrEqualTo(31));
        Assert.That(output.Truncated, Is.True);
        output = new RecordingOutput();
        Run("[*print-length* *print-level*]");
        Assert.That(output.Values.Last(), Is.EqualTo("[nil 2]"));
        output = new RecordingOutput();
        Run("(set! *print-level* 100) (set! *print-length* 1000) (nth (iterate vector 1) 12)");
        Assert.That(output.Values.Last(), Is.EqualTo(new string('[', 10) + "#" + new string(']', 10)));
        Assert.That(output.Truncated, Is.True);
    }

    [Test]
    public void PrinterErrorsStopLaterFormsAndRetainTheLeaseUntilThePrinterReturns()
    {
        var callbackObserved = false;
        initial.intern(Symbol.intern("printer-probe")).bindRoot(new Callback(() =>
        {
            callbackObserved = true;
            Assert.Throws<InvalidOperationException>(() => sessions.Release(state));
            throw new InvalidOperationException("printer failed");
        }));
        var code = "(deftype Broken []) (defmethod print-method Broken [v w] (printer-probe)) 42 (Broken.) (def not-executed true)";
        Assert.That(Run(code), Is.EqualTo(ReplEvaluationOutcome.Failed));
        Assert.That(callbackObserved, Is.True);
        Assert.That(output.Errors.Last().Phase, Is.EqualTo("print"));
        Assert.That(output.Values.Last(), Is.EqualTo("42"));
        Assert.That(initial.FindInternedVar(Symbol.intern("not-executed")), Is.Null);
        output.Events.Clear();
        Run("(+ 1 2)");
        Assert.That(output.Values, Is.EqualTo(new[] { "3" }));
    }

    [Test]
    public void SourceDiagnosticsAreBoundedAndUseKnownCompilerPosition()
    {
        Assert.That(Run("\n(missing-function)", file: "diagnostics.clj", line: 12), Is.EqualTo(ReplEvaluationOutcome.Failed));
        Assert.That(output.Errors.Single().Line, Is.EqualTo(13));
        Assert.That(output.Errors.Single().Column, Is.EqualTo(1));
        output.Errors.Clear();
        Run("(throw (System.Exception. (apply str (repeat 10000 \"😀\"))))", file: new string('x', 5000));
        var error = output.Errors.Single();
        Assert.That(Encoding.UTF8.GetByteCount(error.Message), Is.LessThanOrEqualTo(1024));
        Assert.That(Encoding.UTF8.GetByteCount(error.SourceName), Is.LessThanOrEqualTo(512));
        Assert.That(new UTF8Encoding(false, true).GetBytes(error.Message), Is.Not.Empty);
    }

    [TestCase("(+ 1 2) (throw (System.InvalidOperationException. \"failed form\")) (def should-not-exist 1)", "eval")]
    [TestCase("(+ 1 2) (", "read")]
    [TestCase("(+ 1 2) (missing-function 1) (def should-not-exist 1)", "compile")]
    public void ErrorsStopRemainingFormsPreserveEarlierValuesAndAllowTheNextRequest(string code, string phase)
    {
        Assert.That(Run(code, file: "test-source.clj"), Is.EqualTo(ReplEvaluationOutcome.Failed));
        Assert.That(output.Values, Is.EqualTo(new[] { "3" }));
        Assert.That(output.Errors, Has.Count.EqualTo(1));
        Assert.That(output.Errors[0].Phase, Is.EqualTo(phase));
        Assert.That(output.Errors[0].SourceName, Is.EqualTo("test-source.clj"));
        Assert.That(output.Errors[0].RootExceptionType, Is.Not.Empty);
        Assert.That(initial.FindInternedVar(Symbol.intern("should-not-exist")), Is.Null);
        Assert.That(Var.getThreadBindingFrame(), Is.SameAs(frame));
        output.Events.Clear();
        Assert.That(Run("[(some? *e) *1 (+ 20 22)]"), Is.EqualTo(ReplEvaluationOutcome.Succeeded));
        Assert.That(output.Values, Is.EqualTo(new[] { "[true 3 42]" }));
    }

    [Test]
    public void EmptyInputPreservesHistoryAndMissingNamespaceDoesNotCreateOrExecuteAnything()
    {
        Run("42");
        output.Events.Clear();
        Assert.That(Run(" ; comment\n #_99 "), Is.EqualTo(ReplEvaluationOutcome.Succeeded));
        Assert.That(output.Values, Is.Empty);
        var missing = "embedded-nrepl.missing." + Guid.NewGuid().ToString("N");
        Assert.That(Run("(def unexpected 1)", ns: missing), Is.EqualTo(ReplEvaluationOutcome.Failed));
        Assert.That(output.Errors.Last().Code, Is.EqualTo("namespace-not-found"));
        Assert.That(Namespace.find(Symbol.intern(missing)), Is.Null);
        Assert.That(initial.FindInternedVar(Symbol.intern("unexpected")), Is.Null);
        Run("*1");
        Assert.That(output.Values, Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void FormsExecuteSequentiallyAndEachSuccessProducesAValueAndUpdatesHistory()
    {
        Assert.That(Run("(+ 1 2) (def number 41) (inc number) nil \"Привет 😀\""), Is.EqualTo(ReplEvaluationOutcome.Succeeded));
        Assert.That(output.Values, Is.EqualTo(new[] { "3", "#'" + initial.Name + "/number", "42", "nil", "\"Привет 😀\"" }));
        output.Events.Clear();
        Assert.That(Run("[*1 *2 *3]"), Is.EqualTo(ReplEvaluationOutcome.Succeeded));
        Assert.That(output.Values, Is.EqualTo(new[] { "[\"Привет 😀\" nil 42]" }));
        Assert.That(Var.getThreadBindingFrame(), Is.SameAs(frame));
    }

    internal sealed class RecordingOutput : IReplEvaluationOutput
    {
        public int LateCharacters;
        public Action<int>? LateOutput => count => Interlocked.Add(ref LateCharacters, count);
        public List<(ReplOutputKind Kind, string Text)> Events { get; } = new List<(ReplOutputKind, string)>();
        public string[] Values => Events.Where(e => e.Kind == ReplOutputKind.Value).Select(e => e.Text).ToArray();
        public List<ReplEvaluationError> Errors { get; } = new List<ReplEvaluationError>();
        public List<string> Namespaces { get; } = new List<string>();
        public bool Truncated
        {
            get; private set;
        }
        public bool TryWriteValue(string value, string ns, bool truncated)
        {
            Namespaces.Add(ns);
            Truncated |= truncated;
            return TryWrite(ReplOutputKind.Value, value, 0, value.Length);
        }
        public void ReportError(ReplEvaluationError error) => Errors.Add(error);
        public void MarkTruncated() => Truncated = true;
        public bool TryWrite(ReplOutputKind kind, string text, int offset, int count)
        {
            lock (Events)
                Events.Add((kind, text.Substring(offset, count)));
            return true;
        }
        public void Flush()
        {
        }
    }
}
