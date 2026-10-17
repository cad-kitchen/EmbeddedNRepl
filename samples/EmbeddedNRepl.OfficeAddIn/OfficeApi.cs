using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace EmbeddedNRepl.OfficeAddIn;

// Return managed values rather than COM objects: REPL printing/history must not retain Office RCWs.
public abstract class OfficeApi
{
    private object? application;
    protected OfficeApi(object application)
    {
        this.application = application;
        OwnerThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    public int OwnerThreadId
    {
        get;
    }
    protected dynamic Application
    {
        get
        {
            CheckAccess();
            return application!;
        }
    }

    public void CheckAccess()
    {
        if (Thread.CurrentThread.ManagedThreadId != OwnerThreadId)
            throw new InvalidOperationException("Office access requires the add-in's owner thread. Do not use future or Task.Run for Office calls.");
        if (application == null)
            throw new ObjectDisposedException(GetType().Name);
    }

    internal void Detach() => application = null;

    protected static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }
}

public sealed class WordApi : OfficeApi
{
    public WordApi(object application) : base(application) { }

    public string NewDocument()
    {
        dynamic documents = Application.Documents;
        object? document = null;
        try
        {
            document = documents.Add();
            return (string)((dynamic)document).Name;
        }
        finally { Release(document); Release(documents); }
    }

    public string Text()
    {
        dynamic document = Application.ActiveDocument;
        object? range = null;
        try
        {
            range = document.Content;
            return (string)((dynamic)range).Text;
        }
        finally { Release(range); Release(document); }
    }

    public void Append(string text)
    {
        dynamic document = Application.ActiveDocument;
        object? range = null;
        try
        {
            range = document.Content;
            ((dynamic)range).InsertAfter(text);
        }
        finally { Release(range); Release(document); }
    }
}

public sealed class ExcelApi : OfficeApi
{
    public ExcelApi(object application) : base(application) { }

    public string NewWorkbook()
    {
        dynamic workbooks = Application.Workbooks;
        object? workbook = null;
        try
        {
            workbook = workbooks.Add();
            return (string)((dynamic)workbook).Name;
        }
        finally { Release(workbook); Release(workbooks); }
    }

    public object? Value(string address) => WithCell(address, cell => (object?)cell.Value2);

    public void SetValue(string address, object? value)
    {
        WithCell(address, cell => { cell.Value2 = value; return null; });
    }

    public void SetFormula(string address, string formula)
    {
        WithCell(address, cell => { cell.Formula = formula; cell.Calculate(); return null; });
    }

    private object? WithCell(string address, Func<dynamic, object?> action)
    {
        dynamic sheet = Application.ActiveSheet;
        if (sheet == null)
            throw new InvalidOperationException("Create or activate a worksheet first.");
        object? cell = null;
        object? cells = null;
        try
        {
            cell = sheet.Range[address];
            cells = ((dynamic)cell).Cells;
            if ((double)((dynamic)cells).CountLarge != 1)
                throw new ArgumentException("Use one cell address, for example A1.", nameof(address));
            return action(cell);
        }
        finally { Release(cells); Release(cell); Release(sheet); }
    }
}
