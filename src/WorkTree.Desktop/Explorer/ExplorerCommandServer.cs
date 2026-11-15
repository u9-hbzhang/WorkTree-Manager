using System.Runtime.InteropServices;

namespace WorkTree.Desktop.Explorer;

internal sealed record ExplorerRequest(bool Create, string[] Folders);

internal sealed class ExplorerCommandServer : IDisposable
{
    private const uint LocalServer = 4;
    private const uint MultipleUse = 1;
    private const uint Suspended = 4;
    private readonly List<uint> _registrations = [];

    public ExplorerCommandServer(Action<ExplorerRequest> receive)
    {
        try
        {
            Register(ExplorerMenu.CreateClass, new ClassFactory(true, receive));
            Register(ExplorerMenu.ViewClass, new ClassFactory(false, receive));
            Marshal.ThrowExceptionForHR(CoResumeClassObjects());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Register(Guid clsid, ClassFactory factory)
    {
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref clsid, factory, LocalServer, MultipleUse | Suspended,
            out var cookie));
        _registrations.Add(cookie);
    }

    public void Dispose()
    {
        foreach (var cookie in _registrations)
            CoRevokeClassObject(cookie);
        _registrations.Clear();
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.Interface)] IClassFactory factory,
        uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoResumeClassObjects();

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ClassFactory(bool create, Action<ExplorerRequest> receive) : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero)
                return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION

            var unknown = Marshal.GetIUnknownForObject(new Command(create, receive));
            try { return Marshal.QueryInterface(unknown, in iid, out instance); }
            finally { Marshal.Release(unknown); }
        }

        public void LockServer([MarshalAs(UnmanagedType.Bool)] bool locked) { }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Command(bool create, Action<ExplorerRequest> receive) : IExecuteCommand, IObjectWithSelection
    {
        private IShellItemArray? _selection;

        public void SetSelection(IShellItemArray selection) => _selection = selection;

        public int GetSelection(ref Guid iid, out IntPtr selection)
        {
            selection = IntPtr.Zero;
            if (_selection is null)
                return unchecked((int)0x80004005); // E_FAIL

            var unknown = Marshal.GetIUnknownForObject(_selection);
            try { return Marshal.QueryInterface(unknown, in iid, out selection); }
            finally { Marshal.Release(unknown); }
        }

        public void Execute()
        {
            if (_selection is null)
                throw new COMException("资源管理器没有传入选中的目录。", unchecked((int)0x80070057));

            _selection.GetCount(out var count);
            var folders = new string[checked((int)count)];
            for (var i = 0; i < folders.Length; i++)
            {
                _selection.GetItemAt((uint)i, out var item);
                item.GetDisplayName(0x80058000, out var path); // SIGDN_FILESYSPATH
                try { folders[i] = Marshal.PtrToStringUni(path) ?? ""; }
                finally { Marshal.FreeCoTaskMem(path); }
            }

            receive(new ExplorerRequest(create, folders));
            _selection = null;
        }

        public void SetKeyState(uint keyState) { }
        public void SetParameters(string? parameters) { }
        public void SetPosition(ShellPoint position) { }
        public void SetShowWindow(int show) { }
        public void SetNoShowUI(bool noUi) { }
        public void SetDirectory(string? directory) { }
    }
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
    void LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}

[StructLayout(LayoutKind.Sequential)]
public struct ShellPoint
{
    public int X;
    public int Y;
}

[ComImport, Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExecuteCommand
{
    void SetKeyState(uint keyState);
    void SetParameters([MarshalAs(UnmanagedType.LPWStr)] string? parameters);
    void SetPosition(ShellPoint position);
    void SetShowWindow(int show);
    void SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool noUi);
    void SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string? directory);
    void Execute();
}

[ComImport, Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSelection
{
    void SetSelection(IShellItemArray selection);
    [PreserveSig] int GetSelection(ref Guid iid, out IntPtr selection);
}

[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
    void GetPropertyStore(int flags, ref Guid iid, out IntPtr result);
    void GetPropertyDescriptionList(IntPtr key, ref Guid iid, out IntPtr result);
    void GetAttributes(int flags, uint mask, out uint attributes);
    void GetCount(out uint count);
    void GetItemAt(uint index, out IShellItem item);
    void EnumItems(out IntPtr enumerator);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint nameType, out IntPtr displayName);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}
