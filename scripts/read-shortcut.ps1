param([Parameter(Mandatory = $true)][string]$LiteralPath)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$apartment = [Threading.Thread]::CurrentThread.GetApartmentState().ToString()
if ($apartment -ne 'STA') { throw 'Shortcut inspection requires Windows PowerShell -STA.' }
$shortcutPath = [IO.Path]::GetFullPath($LiteralPath)
if (!(Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw 'Shortcut file not found.' }
$interop = @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SimpleScraperVerification
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    public class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
    }

    public sealed class ShortcutInfo
    {
        public string TargetPath { get; set; }
        public string WorkingDirectory { get; set; }
    }

    public static class ShortcutReader
    {
        public static ShortcutInfo Read(string path)
        {
            object shellLink = new ShellLink();
            try
            {
                ((IPersistFile)shellLink).Load(path, 0);
                var link = (IShellLinkW)shellLink;
                var target = new StringBuilder(32768);
                var workingDirectory = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
                return new ShortcutInfo { TargetPath = target.ToString(), WorkingDirectory = workingDirectory.ToString() };
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }
    }
}
'@
Add-Type -TypeDefinition $interop -Language CSharp
$shortcut = [SimpleScraperVerification.ShortcutReader]::Read($shortcutPath)
[ordered]@{
    shortcutPath = $shortcutPath
    shortcutBytes = (Get-Item -LiteralPath $shortcutPath).Length
    shortcutSha256 = (Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash.ToLowerInvariant()
    targetPath = $shortcut.TargetPath
    workingDirectory = $shortcut.WorkingDirectory
    powerShellVersion = $PSVersionTable.PSVersion.ToString()
    powerShellEdition = [string]$PSVersionTable.PSEdition
    apartmentState = $apartment
    reader = 'IShellLinkW/IPersistFile.Load(STGM_READ)'
} | ConvertTo-Json -Compress
