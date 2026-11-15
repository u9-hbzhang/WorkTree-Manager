using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WorkTree.Desktop.Explorer;

internal static class ExplorerMenu
{
    internal static readonly Guid CreateClass = new("BD2F25A8-B35C-4B20-8894-87C17FCC538E");
    internal static readonly Guid ViewClass = new("E18DC454-24DF-4813-97B6-21B26ACFFDBA");

    private const string MenuKey = @"Software\Classes\Directory\shell\WorkTreeManager";
    private const string CommandGroup = "WorkTreeManager.Commands";
    private const string CommandGroupKey = @"Software\Classes\" + CommandGroup;
    private const string ClassesKey = @"Software\Classes\CLSID\";

    public static (bool Enabled, bool NeedsRepair) GetState()
    {
        using var menu = Registry.CurrentUser.OpenSubKey(MenuKey);
        if (menu is null)
            return (false, false);

        return (true, !Equals(menu.GetValue("ExtendedSubCommandsKey"), CommandGroup) ||
            !PointsToCurrentProgram(CreateClass) || !PointsToCurrentProgram(ViewClass));
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            RemoveRegistration();
            NotifyExplorer();
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null || !string.Equals(Path.GetFileName(exe), "WorkTree.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请直接运行 WorkTree.exe 后启用资源管理器菜单。");

        var wasRegistered = GetState().Enabled;
        try
        {
            RegisterServer(CreateClass, exe);
            RegisterServer(ViewClass, exe);
            using var commands = Registry.CurrentUser.CreateSubKey(CommandGroupKey + @"\shell");
            RegisterCommand(commands, "01Create", "从所选目录创建稀疏工作区…", CreateClass);
            RegisterCommand(commands, "02View", "查看此仓库的工作区", ViewClass);
            using var menu = Registry.CurrentUser.CreateSubKey(MenuKey);
            menu.SetValue("MUIVerb", "WorkTree");
            menu.SetValue("Icon", $"\"{exe}\",0");
            menu.SetValue("MultiSelectModel", "Player");
            menu.SetValue("ExtendedSubCommandsKey", CommandGroup);
            menu.DeleteSubKeyTree("ExtendedSubCommandsKey", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!wasRegistered)
            {
                try { RemoveRegistration(); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException) { }
            }
            throw;
        }
        finally
        {
            NotifyExplorer();
        }
    }

    private static bool PointsToCurrentProgram(Guid clsid)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClassesKey + clsid.ToString("B") + @"\LocalServer32");
        return string.Equals(key?.GetValue(null) as string, $"\"{Environment.ProcessPath}\"",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void RegisterServer(Guid clsid, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ClassesKey + clsid.ToString("B") + @"\LocalServer32");
        key.SetValue(null, $"\"{exe}\"");
        key.SetValue("ServerExecutable", exe);
    }

    private static void RegisterCommand(RegistryKey parent, string keyName, string title, Guid clsid)
    {
        using var verb = parent.CreateSubKey(keyName);
        verb.SetValue("MUIVerb", title);
        verb.SetValue("MultiSelectModel", "Player");
        using var command = verb.CreateSubKey("command");
        command.SetValue("DelegateExecute", clsid.ToString("B"));
    }

    private static void RemoveRegistration()
    {
        Registry.CurrentUser.DeleteSubKeyTree(MenuKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(CommandGroupKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClassesKey + CreateClass.ToString("B"), throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClassesKey + ViewClass.ToString("B"), throwOnMissingSubKey: false);
    }

    private static void NotifyExplorer() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
