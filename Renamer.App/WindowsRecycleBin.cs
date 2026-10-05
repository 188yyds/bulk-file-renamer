using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Renamer.Core;

namespace Renamer.App;

// No SHFileOperation fallback: it may silently choose permanent deletion.
// RECYCLEONDELETE requests recycling; the progress sink vetoes any non-recycle delete.
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : IRecycleBin
{
    public bool TryRecycle(string path, out string reason)
    {
        bool success = false; string error = "回收站未完成操作";
        var thread = new Thread(() =>
        {
            IFileOperation? op = null; IntPtr item = IntPtr.Zero;
            try
            {
                PathSafety.NoLinks(path);
                if (!File.Exists(path) || Directory.Exists(path)) throw new UserError("只能回收明确存在的普通文件");
                op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true)!)!;
                // RECYCLEONDELETE | EARLYFAILURE | NOERRORUI | SILENT | NOCONFIRMATION | NO_CONNECTED_ELEMENTS.
                Check(op.SetOperationFlags(0x00080000 | 0x00100000 | 0x400 | 4 | 0x10 | 0x2000));
                var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                Check(SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref iid, out item));
                var sink = new RecycleOnlySink();
                Check(op.DeleteItem(item, sink));
                var result = op.PerformOperations();
                Check(op.GetAnyOperationsAborted(out var aborted));
                GC.KeepAlive(sink);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                success = !aborted && sink.RecycleApproved && sink.Completed && !File.Exists(path);
                error = success ? "" : sink.RefusedPermanentDelete ? "系统将永久删除，程序已阻止；文件继续保留" : "系统没有确认文件已进入回收站";
            }
            catch (Exception ex) { error = BatchEngine.ChineseError(ex); }
            finally { if (item != IntPtr.Zero) Marshal.Release(item); if (op != null) Marshal.FinalReleaseComObject(op); }
        }) { IsBackground = true, Name = "RecycleOnlySTA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        reason = error; return success;
    }
    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, out IntPtr item);
}

[ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IFileOperation
{
    [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOperationFlags(uint flags);
    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    [PreserveSig] int SetProgressDialog(IntPtr dialog);
    [PreserveSig] int SetProperties(IntPtr properties);
    [PreserveSig] int SetOwnerWindow(IntPtr owner);
    [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
    [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
    [PreserveSig] int RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
    [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
    [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
    [PreserveSig] int CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
    [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
    [PreserveSig] int DeleteItem(IntPtr item, IFileOperationProgressSink sink);
    [PreserveSig] int DeleteItems(IntPtr items);
    [PreserveSig] int NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IFileOperationProgressSink sink);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}
[ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
    [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
    [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
    [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
    [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newItem);
    [PreserveSig] int PreNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, uint attributes, int result, IntPtr newItem);
    [PreserveSig] int UpdateProgress(uint total, uint done);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RecycleOnlySink : IFileOperationProgressSink
{
    const int Abort = unchecked((int)0x80004004);
    public bool RecycleApproved { get; private set; }
    public bool RefusedPermanentDelete { get; private set; }
    public bool Completed { get; private set; }
    public int PreDeleteItem(uint flags, IntPtr item)
    {
        if ((flags & 0x80) == 0) { RefusedPermanentDelete = true; return Abort; }
        RecycleApproved = true; return 0;
    }
    public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newItem) { Completed = result >= 0 && RecycleApproved && !RefusedPermanentDelete; return 0; }
    public int StartOperations() => 0;
    public int FinishOperations(int result) => 0;
    public int PreRenameItem(uint flags, IntPtr item, string name) => Abort;
    public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr newItem) => 0;
    public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, string name) => Abort;
    public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr newItem) => 0;
    public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, string name) => Abort;
    public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr newItem) => 0;
    public int PreNewItem(uint flags, IntPtr destination, string name) => Abort;
    public int PostNewItem(uint flags, IntPtr destination, string name, string templateName, uint attributes, int result, IntPtr newItem) => 0;
    public int UpdateProgress(uint total, uint done) => 0;
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
