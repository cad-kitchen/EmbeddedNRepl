using System;
using System.Runtime.InteropServices;

[assembly: ComVisible(false)]

namespace EmbeddedNRepl.OfficeAddIn;

// Office's shared add-in lifecycle contract, declared here to avoid a VS SDK dependency.
[ComImport, ComVisible(true), Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDTExtensibility2
{
    [DispId(1)]
    void OnConnection([MarshalAs(UnmanagedType.IDispatch)] object application, int connectMode,
        [MarshalAs(UnmanagedType.IDispatch)] object addInInstance,
        [In, Out, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    [DispId(2)]
    void OnDisconnection(int removeMode, [In, Out, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    [DispId(3)]
    void OnAddInsUpdate([In, Out, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    [DispId(4)]
    void OnStartupComplete([In, Out, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    [DispId(5)]
    void OnBeginShutdown([In, Out, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
}

[ComVisible(true), Guid("AFB33272-53B4-44A6-A267-A7D028F64510")]
[ProgId("EmbeddedNRepl.WordAddIn"), ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IDTExtensibility2))]
public sealed class WordAddIn : OfficeAddInBase
{
    public WordAddIn() : base("Word") { }
}

[ComVisible(true), Guid("74205B9F-B51A-4C59-9799-EF621DD61852")]
[ProgId("EmbeddedNRepl.ExcelAddIn"), ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IDTExtensibility2))]
public sealed class ExcelAddIn : OfficeAddInBase
{
    public ExcelAddIn() : base("Excel") { }
}

public abstract class OfficeAddInBase : IDTExtensibility2
{
    private readonly string product;
    private object? application;
    private OfficeReplHost? host;

    protected OfficeAddInBase(string product) => this.product = product;

    public void OnConnection(object application, int connectMode, object addInInstance, ref Array custom)
    {
        OfficeReplHost.Log(product, "Connected; mode " + connectMode);
        this.application = application;
        // ext_cm_Startup = 1: wait until Office has finished starting.
        if (connectMode != 1)
            Start();
    }

    public void OnStartupComplete(ref Array custom) => Start();
    public void OnAddInsUpdate(ref Array custom)
    {
    }
    public void OnBeginShutdown(ref Array custom) => Stop();
    public void OnDisconnection(int removeMode, ref Array custom) => Stop();

    private void Start()
    {
        if (host != null || application == null)
            return;
        try
        {
            host = new OfficeReplHost(product, application);
        }
        catch (Exception error)
        {
            OfficeReplHost.Log(product, error.ToString());
            throw new COMException("EmbeddedNRepl could not start. See the Office sample log.", error);
        }
    }

    private void Stop()
    {
        try
        {
            host?.Dispose();
        }
        catch (Exception error) { OfficeReplHost.Log(product, error.ToString()); }
        finally
        {
            host = null;
            // Office owns this RCW; do not FinalReleaseComObject on the supplied Application.
            application = null;
        }
    }
}
