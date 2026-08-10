using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace HeavierByTheKill.Controller;

/// <summary>
/// Loads a tiny input-only helper into DSR so Steam's process-scoped legacy
/// XInput emulation can be observed by the external controller.
/// </summary>
internal sealed class InputBridge : IDisposable
{
    const uint Magic=0x48424b49; // HBKI
    const ushort RightShoulder=0x0200;
    const int ButtonsOffset=16,StopOffset=18;
    readonly MemoryMappedFile mapping;
    readonly MemoryMappedViewAccessor view;
    bool disposed;

    InputBridge(MemoryMappedFile mapping,MemoryMappedViewAccessor view)
    {
        this.mapping=mapping; this.view=view;
    }

    internal bool RightShoulderDown
    {
        get
        {
            try { return !disposed&&(view.ReadUInt16(ButtonsOffset)&RightShoulder)!=0; }
            catch(ObjectDisposedException) { return false; }
        }
    }

    internal static InputBridge? TryAttach(Process game,out string status)
    {
        MemoryMappedFile? mapping=null; MemoryMappedViewAccessor? view=null;
        try
        {
            var dllPath=Path.Combine(AppContext.BaseDirectory,"heavier_by_the_kill_input.dll");
            if(!File.Exists(dllPath)) throw new FileNotFoundException("The Steam Input bridge is missing.",dllPath);
            var mapName=$@"Local\HeavierByTheKill.Input.{game.Id}";
            mapping=MemoryMappedFile.CreateOrOpen(mapName,4096,MemoryMappedFileAccess.ReadWrite);
            view=mapping.CreateViewAccessor(0,4096,MemoryMappedFileAccess.ReadWrite);
            view.Write(0,0u);
            view.Write(4,(uint)Environment.ProcessId);
            view.Write(8,(uint)game.Id);
            view.Write(12,0u);
            view.Write(ButtonsOffset,(ushort)0);
            view.Write(StopOffset,(ushort)0);

            Inject(game,dllPath);
            var deadline=DateTime.UtcNow.AddSeconds(3);
            while(DateTime.UtcNow<deadline&&!game.HasExited)
            {
                if(view.ReadUInt32(0)==Magic&&view.ReadUInt32(12)>0)
                {
                    status="Steam Input bridge active; R1 is available in game menus.";
                    return new InputBridge(mapping,view);
                }
                Thread.Sleep(10);
            }
            throw new TimeoutException("The injected Steam Input bridge did not start.");
        }
        catch(Exception error) when(error is Win32Exception or IOException or UnauthorizedAccessException or TimeoutException)
        {
            view?.Dispose(); mapping?.Dispose();
            status=$"Steam Input bridge unavailable ({error.Message}); F8 remains available.";
            return null;
        }
    }

    static void Inject(Process game,string dllPath)
    {
        const uint createThread=0x0002,queryInformation=0x0400,vmOperation=0x0008,vmWrite=0x0020,vmRead=0x0010;
        const uint memCommit=0x1000,memReserve=0x2000,memRelease=0x8000,pageReadWrite=0x04;
        var process=OpenProcess(createThread|queryInformation|vmOperation|vmWrite|vmRead,false,game.Id);
        if(process==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not open DSR for the input bridge");
        IntPtr remotePath=IntPtr.Zero,thread=IntPtr.Zero;
        try
        {
            var pathBytes=System.Text.Encoding.Unicode.GetBytes(dllPath+'\0');
            remotePath=VirtualAllocEx(process,IntPtr.Zero,(nuint)pathBytes.Length,memCommit|memReserve,pageReadWrite);
            if(remotePath==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not allocate the bridge path");
            if(!WriteProcessMemory(process,remotePath,pathBytes,(nuint)pathBytes.Length,out var written)||written!=(nuint)pathBytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not write the bridge path");
            var kernel=GetModuleHandle("kernel32.dll");
            var loadLibrary=kernel==IntPtr.Zero?IntPtr.Zero:GetProcAddress(kernel,"LoadLibraryW");
            if(loadLibrary==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not resolve LoadLibraryW");
            thread=CreateRemoteThread(process,IntPtr.Zero,0,loadLibrary,remotePath,0,out _);
            if(thread==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not start the input bridge");
            if(WaitForSingleObject(thread,5000)!=0) throw new TimeoutException("Loading the input bridge timed out.");
        }
        finally
        {
            if(thread!=IntPtr.Zero) CloseHandle(thread);
            if(remotePath!=IntPtr.Zero) VirtualFreeEx(process,remotePath,0,memRelease);
            CloseHandle(process);
        }
    }

    public void Dispose()
    {
        if(disposed) return;
        try { view.Write(StopOffset,(ushort)1); } catch(ObjectDisposedException) { }
        disposed=true; view.Dispose(); mapping.Dispose();
    }

    [DllImport("kernel32",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int processId);
    [DllImport("kernel32",SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr process,IntPtr address,nuint size,uint allocationType,uint protection);
    [DllImport("kernel32",SetLastError=true)] static extern bool VirtualFreeEx(IntPtr process,IntPtr address,nuint size,uint freeType);
    [DllImport("kernel32",SetLastError=true)] static extern bool WriteProcessMemory(IntPtr process,IntPtr address,byte[] buffer,nuint size,out nuint written);
    [DllImport("kernel32",SetLastError=true)] static extern IntPtr CreateRemoteThread(IntPtr process,IntPtr attributes,nuint stackSize,IntPtr startAddress,IntPtr parameter,uint flags,out uint threadId);
    [DllImport("kernel32",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr GetModuleHandle(string moduleName);
    [DllImport("kernel32",CharSet=CharSet.Ansi,SetLastError=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
}
