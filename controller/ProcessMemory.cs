using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HeavierByTheKill.Controller;

internal sealed class ProcessMemory : IDisposable
{
    const uint QueryInformation=0x0400, VmRead=0x0010, VmWrite=0x0020, VmOperation=0x0008;
    const uint MemCommit=0x1000, PageGuard=0x100, PageNoAccess=0x01;
    readonly IntPtr handle;
    public Process Process { get; }

    public ProcessMemory(Process process,bool writable=false)
    {
        Process=process; var access=QueryInformation|VmRead|(writable?VmWrite|VmOperation:0);
        handle=OpenProcess(access,false,process.Id);
        if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public byte[] Read(nuint address,int length)
    {
        var data=new byte[length];
        if(!ReadProcessMemory(handle,address,data,(nuint)length,out var read) || read!=(nuint)length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Read failed at 0x{address:X}");
        return data;
    }

    public T Read<T>(nuint address) where T:unmanaged
    {
        var bytes=Read(address,Marshal.SizeOf<T>());
        return MemoryMarshal.Read<T>(bytes);
    }

    public void Write<T>(nuint address,T value) where T:unmanaged
    {
        var bytes=new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        if(!WriteProcessMemory(handle,address,bytes,(nuint)bytes.Length,out var written) || written!=(nuint)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Write failed at 0x{address:X}");
    }

    public IEnumerable<nuint> Scan(string text)
    {
        var pattern=Pattern.Parse(text); nuint cursor=0;
        while(VirtualQueryEx(handle,cursor,out var mbi,(nuint)Marshal.SizeOf<Mbi>())!=0)
        {
            var next=mbi.BaseAddress+mbi.RegionSize;
            if(mbi.State==MemCommit && (mbi.Protect&(PageGuard|PageNoAccess))==0 && mbi.RegionSize>=(nuint)pattern.Length)
            {
                const int chunk=4*1024*1024; var overlap=pattern.Length-1;
                for(nuint offset=0;offset<mbi.RegionSize;offset+=(nuint)(chunk-overlap))
                {
                    var count=(int)Math.Min((nuint)chunk,mbi.RegionSize-offset);
                    byte[]? bytes=null;
                    try { bytes=Read(mbi.BaseAddress+offset,count); } catch(Win32Exception) { }
                    if(bytes is not null) foreach(var hit in pattern.Find(bytes)) yield return mbi.BaseAddress+offset+(nuint)hit;
                    if(count<chunk) break;
                }
            }
            if(next<=cursor) break; cursor=next;
        }
    }

    public IEnumerable<nuint> Scan(string text,nuint start,int length)
    {
        var pattern=Pattern.Parse(text); const int chunk=4*1024*1024; var overlap=pattern.Length-1;
        for(var offset=0;offset<length;offset+=chunk-overlap)
        {
            var count=Math.Min(chunk,length-offset); var bytes=Read(start+(nuint)offset,count);
            foreach(var hit in pattern.Find(bytes)) yield return start+(nuint)offset+(nuint)hit;
            if(count<chunk) break;
        }
    }

    public void Dispose(){ if(handle!=IntPtr.Zero) CloseHandle(handle); }

    readonly record struct Pattern(byte?[] Bytes)
    {
        public int Length=>Bytes.Length;
        public static Pattern Parse(string text)=>new(text.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(x=>x is "?" or "??" ? (byte?)null : Convert.ToByte(x,16)).ToArray());
        public IEnumerable<int> Find(byte[] data){ for(var i=0;i<=data.Length-Bytes.Length;i++){ var ok=true; for(var j=0;j<Bytes.Length;j++) if(Bytes[j] is byte b && data[i+j]!=b){ok=false;break;} if(ok) yield return i; } }
    }

    [StructLayout(LayoutKind.Sequential)] struct Mbi { public nuint BaseAddress,AllocationBase; public uint AllocationProtect; public ushort PartitionId; public nuint RegionSize; public uint State,Protect,Type; }
    [DllImport("kernel32",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32",SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process,nuint address,[Out] byte[] buffer,nuint size,out nuint read);
    [DllImport("kernel32",SetLastError=true)] static extern bool WriteProcessMemory(IntPtr process,nuint address,byte[] buffer,nuint size,out nuint written);
    [DllImport("kernel32")] static extern nuint VirtualQueryEx(IntPtr process,nuint address,out Mbi info,nuint length);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
}
