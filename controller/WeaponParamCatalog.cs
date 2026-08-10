using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HeavierByTheKill.Controller;

/// <summary>
/// Reads the installed regulation's weapon rows so runtime patches do not
/// depend on a hard-coded table of weapon signatures.
/// </summary>
internal sealed class WeaponParamCatalog
{
    internal readonly record struct Row(int Id,int DataOffset,byte[] Signature,short ThrowAttackRate);

    readonly Dictionary<int,Row> rows;

    WeaponParamCatalog(Dictionary<int,Row> rows)=>this.rows=rows;

    public static WeaponParamCatalog Load(string gameDirectory)
    {
        var path=Path.Combine(gameDirectory,"param","GameParam","GameParam.parambnd.dcx");
        var bnd=DecompressDsrDcx(File.ReadAllBytes(path));
        var equipParam=ReadEquipParamWeapon(bnd);
        return new WeaponParamCatalog(ReadRows(equipParam));
    }

    public bool TryGet(uint equippedWeapon,out Row row)
    {
        // Upgrade level occupies the final two digits; the preceding hundreds
        // digit selects the normal/Raw/Magic/etc. weapon path.
        var paramId=checked((int)(equippedWeapon-equippedWeapon%100));
        return rows.TryGetValue(paramId,out row);
    }

    static byte[] DecompressDsrDcx(byte[] dcx)
    {
        if(dcx.Length<16||Encoding.ASCII.GetString(dcx,0,4)!="DCX\0")
            throw new InvalidDataException("GameParam is not a DCX archive.");
        var dca=FindAscii(dcx,"DCA\0");
        if(dca<0||dca+8>=dcx.Length) throw new InvalidDataException("Unsupported GameParam DCX layout.");
        using var input=new MemoryStream(dcx,dca+8,dcx.Length-(dca+8),false);
        using var zlib=new ZLibStream(input,CompressionMode.Decompress);
        using var output=new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    static byte[] ReadEquipParamWeapon(byte[] bnd)
    {
        if(bnd.Length<0x20||Encoding.ASCII.GetString(bnd,0,4)!="BND3")
            throw new InvalidDataException("GameParam does not contain a BND3 archive.");
        var count=ReadInt32(bnd,0x10);
        // DSR GameParam uses BND3 format 0x74: flags, size, 32-bit data
        // offset, ID, name offset, and uncompressed size (0x18 bytes).
        for(var i=0;i<count;i++)
        {
            var header=0x20+i*0x18;
            EnsureRange(bnd,header,0x18);
            var size=ReadInt32(bnd,header+4);
            var dataOffset=ReadInt32(bnd,header+8);
            var nameOffset=ReadInt32(bnd,header+0x10);
            var name=ReadNullTerminatedAscii(bnd,nameOffset);
            if(!name.EndsWith("EquipParamWeapon.param",StringComparison.OrdinalIgnoreCase)) continue;
            EnsureRange(bnd,dataOffset,size);
            return bnd.AsSpan(dataOffset,size).ToArray();
        }
        throw new InvalidDataException("EquipParamWeapon.param was not found in GameParam.");
    }

    static Dictionary<int,Row> ReadRows(byte[] param)
    {
        // DSR PARAM format 0x0200 has a 0x30-byte header followed by
        // 12-byte row headers: ID, data offset, and name offset.
        EnsureRange(param,0,0x30);
        var count=BinaryPrimitives.ReadUInt16LittleEndian(param.AsSpan(0x0A,2));
        var result=new Dictionary<int,Row>(count);
        for(var i=0;i<count;i++)
        {
            var header=0x30+i*12;
            EnsureRange(param,header,12);
            var id=ReadInt32(param,header);
            var dataOffset=ReadInt32(param,header+4);
            EnsureRange(param,dataOffset,0xDE);
            var signature=param.AsSpan(dataOffset,12).ToArray();
            var throwRate=BinaryPrimitives.ReadInt16LittleEndian(param.AsSpan(dataOffset+0xDC,2));
            result[id]=new Row(id,dataOffset,signature,throwRate);
        }
        return result;
    }

    static int FindAscii(byte[] bytes,string value)
    {
        var needle=Encoding.ASCII.GetBytes(value);
        for(var i=0;i<=bytes.Length-needle.Length;i++)
        {
            var found=true;
            for(var j=0;j<needle.Length;j++) if(bytes[i+j]!=needle[j]) { found=false; break; }
            if(found) return i;
        }
        return -1;
    }

    static int ReadInt32(byte[] bytes,int offset)
    {
        EnsureRange(bytes,offset,4);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset,4));
    }

    static string ReadNullTerminatedAscii(byte[] bytes,int offset)
    {
        EnsureRange(bytes,offset,1);
        var end=offset;
        while(end<bytes.Length&&bytes[end]!=0) end++;
        return Encoding.ASCII.GetString(bytes,offset,end-offset);
    }

    static void EnsureRange(byte[] bytes,int offset,int length)
    {
        if(offset<0||length<0||offset>bytes.Length-length)
            throw new InvalidDataException("Malformed GameParam archive.");
    }
}
