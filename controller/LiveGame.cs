using System.Diagnostics;

namespace HeavierByTheKill.Controller;

internal sealed class LiveGame : IDisposable
{
    const string WorldPattern="48 8B 05 ? ? ? ? 48 8B 48 68 48 85 C9 0F 84 ? ? ? ? 48 39 5E 10 0F 84 ? ? ? ? 48";
    const string ClassPattern="48 8B 05 ? ? ? ? 48 85 C0 ? ? F3 0F 58 80 AC 00 00 00";
    const string MenuPattern="48 8B 05 ? ? ? ? 89 88 28 08 00 00 85 C9 ? ? C7 80 34 08 00 00 FF FF FF FF C3";
    const string EventFlagsPattern="48 8B 0D ? ? ? ? 99 33 C2 45 33 C0 2B C2 8D 50 F6";
    static readonly int[] BossDefeatFlags=[2,11010901,3,11010902,4,11200900,5,11210000,11210001,11210002,11210005,6,7,9,11410900,11410410,11410901,10,11,12,11510900,13,14,15,16,11810900];
    readonly ProcessMemory memory;
    readonly nuint worldGlobal, classGlobal;
    readonly nuint moduleBase;
    readonly int moduleSize;
    readonly nuint damageManagerGlobal;
    readonly nuint menuGlobal;
    readonly nuint eventFlagsGlobal;
    readonly WeaponParamCatalog weaponParams;
    readonly Dictionary<int,bool> bossFlags=[];
    readonly Dictionary<(nuint Address,uint Id,uint Weapon),AttackPatchBaseline> attackBaselines=[];
    readonly Dictionary<(nuint Address,uint Id,uint Weapon,nuint Capsule),float> capsuleBaselines=[];
    readonly object attackPatchGate=new();
    readonly Dictionary<int,nuint> weaponParamAddresses=[];
    nuint weaponParamBase;
    public uint LastAttackWeapon { get; private set; }
    public DateTime LastAttackCreatedAt { get; private set; }
    nuint speedAddress;
    float originalSpeed=1;
    bool speedTouched;
    nuint criticalRateAddress;
    short criticalRateOriginal;
    int criticalRateParamId=-1;
    public nuint CriticalRateAddress=>criticalRateAddress;
    public short CriticalRateOriginal=>criticalRateOriginal;
    public int CriticalRateParamId=>criticalRateParamId;

    public LiveGame(Process process)
    {
        memory=new(process,true);
        var module=process.MainModule ?? throw new InvalidOperationException("No main module.");
        moduleBase=(nuint)module.BaseAddress; moduleSize=module.ModuleMemorySize;
        worldGlobal=ResolveRip(FindOne(WorldPattern));
        classGlobal=ResolveRip(FindOne(ClassPattern));
        menuGlobal=ResolveRip(FindOne(MenuPattern));
        eventFlagsGlobal=ResolveRip(FindOne(EventFlagsPattern));
        // The global address is stable, but DSR may replace the manager object
        // it points to during a warp. Never cache the object itself.
        damageManagerGlobal=moduleBase+0x1C7A050;
        var gameDirectory=Path.GetDirectoryName(module.FileName) ?? throw new InvalidOperationException("Game directory was not found.");
        weaponParams=WeaponParamCatalog.Load(gameDirectory);
        foreach(var flag in BossDefeatFlags) bossFlags[flag]=ReadEventFlag(flag);
    }

    nuint FindOne(string pattern)
    {
        var hits=memory.Scan(pattern,moduleBase,moduleSize).Take(2).ToArray();
        if(hits.Length!=1) throw new InvalidOperationException($"Expected one signature match, found {hits.Length}.");
        return hits[0];
    }
    nuint ResolveRip(nuint instruction) => instruction+7+(nuint)memory.Read<int>(instruction+3);
    nuint Ptr(nuint address)=>memory.Read<nuint>(address);

    public nuint Character
    {
        get
        {
            var world=Ptr(worldGlobal);
            return world==0?0:Ptr(world+0x68);
        }
    }
    public bool IsLoaded
    {
        get { try { return Character!=0; } catch { return false; } }
    }
    public uint Souls { get { var classBase=Ptr(classGlobal); var data=Ptr(classBase+0x10); return memory.Read<uint>(data+0x94); } }
    public nuint ClassBase=>Ptr(classGlobal);
    public nuint ClassData { get { var classBase=ClassBase; return classBase==0?0:Ptr(classBase+0x10); } }
    public uint Stamina => memory.Read<uint>(Character+0x3F8);
    public uint MaxStamina => memory.Read<uint>(Character+0x3FC);
    public uint Health => memory.Read<uint>(Character+0x3E8);
    public uint ActiveWeapon => memory.Read<uint>(Character+0x1E34);
    public nuint PlayerGameData=>Ptr(Character+0x578);
    public nuint DamageManagerInstance
    {
        get { try { return Ptr(damageManagerGlobal); } catch(System.ComponentModel.Win32Exception) { return 0; } }
    }
    public string CharacterName
    {
        get
        {
            // ClassData is the live, character-bound copy of the selected
            // save slot. Its layout begins 0x60 bytes after the serialized
            // slot layout: save-slot name +0x108 => ClassData +0xa8.
            // ClassBase +0x60 points at a detached display-name cache which
            // can retain the first character's name after loading another.
            var data=ClassData;
            if(data==0) return string.Empty;
            var bytes=memory.Read(data+0xA8,64);
            var end=0;
            while(end+1<bytes.Length&&(bytes[end]!=0||bytes[end+1]!=0)) end+=2;
            return System.Text.Encoding.Unicode.GetString(bytes,0,end).Trim();
        }
    }
    public int AnimationId { get { var root=Ptr(Character+0x68); return memory.Read<int>(Ptr(root+0x48)+0x80); } }
    public AttackKind? AnimationAttackKind
    {
        get
        {
            try
            {
                var animation=AnimationId;
                if(animation<0) return null;
                var handBlock=animation%10000/1000;
                if(handBlock is <3 or >5) return null;
                var action=animation%1000;
                if(action is 201 or 202 or 203 or 400 or 401 or 402 or 403 or 980) return AttackKind.Critical;
                if(action is 300 or 301 or 310 or 600 or 640 or 800 or 801 or 810) return AttackKind.Heavy;
                if(action==500) return AttackKind.Running;
                if(action is 0 or 1 or 2 or 40 or 41 or 42 or 100 or 900 or 940) return AttackKind.Quick;
                return null;
            }
            catch(System.ComponentModel.Win32Exception) { return null; }
        }
    }
    public bool IsAttackAnimation => AnimationAttackKind.HasValue;
    public bool R1Down
    {
        get
        {
            try
            {
                var pad=Ptr(Character+0x70);
                if(pad==0) return false;
                var actions=pad+0x84;
                return memory.Read<byte>(actions)!=0||memory.Read<byte>(actions+7)!=0;
            }
            catch(System.ComponentModel.Win32Exception) { return false; }
        }
    }
    public nuint ThrowParam
    {
        get
        {
            var status=Ptr(Character+0x448);
            return status==0?0:Ptr(status+0x10);
        }
    }
    public bool IsCriticalSequence => AnimationAttackKind==AttackKind.Critical;
    public AttackKind? AttackIntent
    {
        get
        {
            try
            {
                var pad=Ptr(Character+0x70);
                if(pad==0) return null;
                var actions=pad+0x84;
                var r1=memory.Read<byte>(actions)!=0||memory.Read<byte>(actions+7)!=0;
                var r2=memory.Read<byte>(actions+5)!=0;
                if(!r1&&!r2) return null;
                if(r2) return AttackKind.Heavy;
                return memory.Read<byte>(pad+0x1E4)!=0?AttackKind.Running:AttackKind.Quick;
            }
            catch(System.ComponentModel.Win32Exception) { return null; }
        }
    }
    public bool IsBonfireMainMenu
    {
        get
        {
            try
            {
                var menu=Ptr(menuGlobal);
                return menu!=0
                    && memory.Read<int>(menu+0x834)==-1
                    && memory.Read<uint>(menu+0x54)==2
                    && memory.Read<uint>(menu+0x6C)==1
                    && memory.Read<uint>(menu+0x838)==0x3F333333
                    && memory.Read<uint>(menu+0xBB0)==uint.MaxValue;
            }
            catch(System.ComponentModel.Win32Exception) { return false; }
        }
    }
    public bool IsBonfireMenu=>IsBonfireMainMenu;
    public bool IsAnyMenuOpen
    {
        get { var menu=Ptr(menuGlobal); return menu!=0 && memory.Read<int>(menu+0x834)==-1; }
    }
    public bool IsBlacksmithReinforceMenu
    {
        get
        {
            try
            {
                var menu=Ptr(menuGlobal);
                // Live 1.03.1 fingerprints: Reinforce Weapon uses page 2 and
                // selector 3. Repair uses page 4/selector 1, while bonfire and
                // ordinary inventory screens do not match this pair.
                return menu!=0
                    && memory.Read<int>(menu+0x834)==-1
                    && memory.Read<uint>(menu+0xBB0)==2
                    && memory.Read<uint>(menu+0x64)==3;
            }
            catch(System.ComponentModel.Win32Exception) { return false; }
        }
    }
    public bool IsBlacksmithMainMenu
    {
        get
        {
            try
            {
                var menu=Ptr(menuGlobal);
                return menu!=0
                    && memory.Read<int>(menu+0x834)==-1
                    && memory.Read<uint>(menu+0x54)==0
                    && memory.Read<uint>(menu+0x6C)==0
                    && memory.Read<uint>(menu+0x838)==0xB1800000
                    && memory.Read<uint>(menu+0x64)==0
                    && memory.Read<uint>(menu+0xBB0)==0;
            }
            catch(System.ComponentModel.Win32Exception) { return false; }
        }
    }
    public (int Primary,uint Secondary,uint Page,uint Selector) MenuFingerprint
    {
        get
        {
            var menu=Ptr(menuGlobal);
            return menu==0
                ? (int.MinValue,0,0,0)
                : (memory.Read<int>(menu+0x834),memory.Read<uint>(menu+0x838),memory.Read<uint>(menu+0xBB0),memory.Read<uint>(menu+0x64));
        }
    }
    public (int Primary,uint Secondary) RawMenuState
    {
        get
        {
            try
            {
                var menu=Ptr(menuGlobal);
                return menu==0?(int.MinValue,0):(memory.Read<int>(menu+0x834),memory.Read<uint>(menu+0x838));
            }
            catch(System.ComponentModel.Win32Exception) { return (int.MinValue,0); }
        }
    }
    public byte[] ReadMenuManager(int length)
    {
        var menu=Ptr(menuGlobal);
        return menu==0?[]:memory.Read(menu,length);
    }
    public IReadOnlyList<nuint> FindPointerReferences(nuint value,int max=128)
    {
        var pattern=string.Join(' ',BitConverter.GetBytes((ulong)value).Select(b=>$"{b:X2}"));
        return memory.Scan(pattern).Take(max).ToArray();
    }
    public nuint DebugPointer(nuint address)=>Ptr(address);
    public int DebugInt32(nuint address)=>memory.Read<int>(address);
    public uint DebugUInt32(nuint address)=>memory.Read<uint>(address);
    public float DebugFloat(nuint address)=>memory.Read<float>(address);
    public int? PollNewBossDefeat()
    {
        foreach(var flag in BossDefeatFlags)
        {
            var current=ReadEventFlag(flag); var previous=bossFlags[flag]; bossFlags[flag]=current;
            if(current&&!previous) return flag;
        }
        return null;
    }
    public void ResetBossDefeatBaseline()
    {
        foreach(var flag in BossDefeatFlags) bossFlags[flag]=ReadEventFlag(flag);
    }
    public void ResetTransientRuntime()
    {
        lock(attackPatchGate)
        {
            attackBaselines.Clear();
            capsuleBaselines.Clear();
            LastAttackWeapon=0;
            LastAttackCreatedAt=DateTime.MinValue;
        }
        speedAddress=0;
        originalSpeed=1;
        speedTouched=false;
    }
    public uint BossLegacy(int flag)=>(uint)Math.Max(0,Array.IndexOf(BossDefeatFlags,flag));
    public static string BossLegacyType(uint legacy)=>legacy switch
    {
        3 or 6 or 7 or 8 or 23=>"PREDATOR",
        0 or 1 or 10 or 18 or 19 or 24 or 25=>"TITAN",
        _=>"ARCANE"
    };
    public WeaponClass ActiveWeaponClass => WeaponClassFor(ActiveWeapon);
    public static WeaponClass WeaponClassFor(uint weapon)
    {
        // Reinforcement is encoded in the low digits; classify by DSR's
        // weapon-ID families. The DLC IDs are sparse and handled explicitly.
        var baseId=weapon-weapon%1000;
        if(baseId is 9010000 or 9011000) return WeaponClass.Light;
        if(baseId is 9012000 or 9015000 or 9020000) return WeaponClass.Heavy;
        if(baseId is 9016000 or 9019000) return WeaponClass.Standard;
        return baseId switch
        {
            >=100000 and <200000=>WeaponClass.Dagger,
            >=200000 and <300000=>WeaponClass.Light,
            >=300000 and <350000=>WeaponClass.Heavy,
            >=350000 and <400000=>WeaponClass.Colossal,
            >=400000 and <450000=>WeaponClass.Light,
            >=450000 and <500000=>WeaponClass.Heavy,
            >=500000 and <700000=>WeaponClass.Light,
            >=700000 and <750000=>WeaponClass.Standard,
            >=750000 and <800000=>WeaponClass.Heavy,
            >=800000 and <850000=>WeaponClass.Standard,
            >=850000 and <900000=>WeaponClass.Colossal,
            >=900000 and <1000000=>WeaponClass.Light,
            >=1000000 and <1050000=>WeaponClass.Standard,
            >=1050000 and <1200000=>WeaponClass.Heavy,
            >=1600000 and <1700000=>WeaponClass.Light,
            _=>WeaponClass.Standard
        };
    }

    bool ReadEventFlag(int id)
    {
        var text=id.ToString("D8"); var groups=new Dictionary<char,int>{{'0',0x00000},{'1',0x00500},{'5',0x05F00},{'6',0x0B900},{'7',0x11300}};
        var areas=new Dictionary<string,int>{{"000",0},{"100",1},{"101",2},{"102",3},{"110",4},{"120",5},{"121",6},{"130",7},{"131",8},{"132",9},{"140",10},{"141",11},{"150",12},{"151",13},{"160",14},{"170",15},{"180",16},{"181",17}};
        var group=groups[text[0]]; var area=areas[text.Substring(1,3)]; var section=text[4]-'0'; var number=int.Parse(text.Substring(5,3));
        var offset=group+area*0x500+section*128+(number-number%32)/8; var mask=0x80000000u>>(number%32);
        var container=Ptr(eventFlagsGlobal); var flags=container==0?0:Ptr(container);
        return flags!=0&&(memory.Read<uint>(flags+(nuint)offset)&mask)!=0;
    }

    public void SetSpeed(float multiplier)
    {
        var animRoot=Ptr(Character+0x68); var animData=Ptr(animRoot+0x18); var address=animData+0xA8;
        if(!speedTouched || address!=speedAddress) { speedAddress=address; originalSpeed=memory.Read<float>(address); speedTouched=true; }
        memory.Write(address,multiplier);
    }
    public void SpendExtraStamina(uint amount)
    {
        var address=Character+0x3F8; var current=memory.Read<uint>(address);
        memory.Write(address,current>amount?current-amount:0u);
    }
    public void SpendSouls(uint amount)
    {
        var classBase=Ptr(classGlobal); var data=Ptr(classBase+0x10); var address=data+0x94;
        var current=memory.Read<uint>(address);
        if(amount>current) throw new InvalidOperationException("Not enough souls.");
        memory.Write(address,current-amount);
    }
    public bool PatchActiveWeaponAttack(float damage,float impact,float knockback,float collisionRadius)
    {
        lock(attackPatchGate)
            return PatchActiveWeaponAttackLocked(damage,impact,knockback,collisionRadius);
    }
    bool PatchActiveWeaponAttackLocked(float damage,float impact,float knockback,float collisionRadius)
    {
        var manager=DamageManagerInstance;
        var entry=manager==0?0:Ptr(manager); var weapon=ActiveWeapon; var created=false;
        for(var count=0;entry!=0 && count<128;count++)
        {
            var id=memory.Read<uint>(entry); var token=(entry,id,weapon);
            // DSR materializes player AtkParam corrections into DamageEntry. The
            // reinforced weapon ID at +0x8c excludes enemy/environment hitboxes.
            if(memory.Read<uint>(entry+0x8C)==weapon)
            {
                if(!attackBaselines.TryGetValue(token,out var baseline))
                {
                    baseline=new AttackPatchBaseline(
                        memory.Read<ushort>(entry+0x54),memory.Read<ushort>(entry+0x56),
                        memory.Read<ushort>(entry+0x58),memory.Read<ushort>(entry+0x5A),
                        memory.Read<float>(entry+0x5C),memory.Read<uint>(entry+0x70),
                        memory.Read<float>(entry+0xA0),memory.Read<byte>(entry+0x102));
                    attackBaselines[token]=baseline;
                    LastAttackWeapon=weapon; LastAttackCreatedAt=DateTime.UtcNow;
                    created=true;
                }

                WriteCorrection(entry+0x54,baseline.Damage0,damage);
                WriteCorrection(entry+0x56,baseline.Damage1,damage);
                WriteCorrection(entry+0x58,baseline.Damage2,damage);
                WriteCorrection(entry+0x5A,baseline.Damage3,damage);
                // Runtime AtkParam materialization converts poise damage to a
                // float while retaining guard pressure as an integral field.
                if(float.IsFinite(baseline.Poise) && baseline.Poise>=0 && baseline.Poise<100000)
                    WriteIfChanged(entry+0x5C,baseline.Poise*impact);
                if(baseline.Guard>0 && baseline.Guard<100000)
                    WriteIfChanged(entry+0x70,(uint)Math.Clamp((long)MathF.Round(baseline.Guard*impact),0,uint.MaxValue));
                // DamageEntry +0x90..+0x9c are the four authored hit radii.
                // The following float at +0xa0 is AtkParam's knockbackDist.
                if(float.IsFinite(baseline.KnockbackDistance) && baseline.KnockbackDistance>=0 && baseline.KnockbackDistance<100)
                    WriteIfChanged(entry+0xA0,baseline.KnockbackDistance*knockback);
                // knockbackDist is only consumed when the victim enters a
                // displacement-capable damage reaction. Damage level 10 is
                // DSR's authored "Blown Away" reaction; retain the attack's
                // vanilla reaction until weight actually unlocks knockback.
                WriteIfChanged(entry+0x102,knockback>1.0001f?(byte)10:baseline.DamageLevel);
                // DamageEntry also owns the live Havok capsule used by the
                // current swing. Changing cached AtkParam metadata at +0x94
                // is too late; the instantiated capsule radius is +0x20.
                // DSR rewrites this shared shape for each new attack entry.
                var capsule=Ptr(entry+0x28);
                if(capsule!=0)
                {
                    var capsuleToken=(entry,id,weapon,capsule);
                    if(!capsuleBaselines.TryGetValue(capsuleToken,out var radius))
                    {
                        radius=memory.Read<float>(capsule+0x20);
                        if(float.IsFinite(radius)&&radius>0&&radius<10) capsuleBaselines[capsuleToken]=radius;
                    }
                    if(float.IsFinite(radius)&&radius>0&&radius<10)
                        WriteIfChanged(capsule+0x20,radius*MathF.Max(1,collisionRadius));
                }
            }
            entry=Ptr(entry+0x220);
        }
        return created;
    }

    void WriteCorrection(nuint address,ushort baseline,float multiplier)
    {
        var expected=(ushort)Math.Clamp((int)MathF.Round(baseline*multiplier),0,ushort.MaxValue);
        WriteIfChanged(address,expected);
    }

    void WriteIfChanged<T>(nuint address,T expected) where T:unmanaged,IEquatable<T>
    {
        if(!memory.Read<T>(address).Equals(expected)) memory.Write(address,expected);
    }

    readonly record struct AttackPatchBaseline(
        ushort Damage0,ushort Damage1,ushort Damage2,ushort Damage3,
        float Poise,uint Guard,float KnockbackDistance,byte DamageLevel);
    public bool PatchActiveWeaponCriticalDamage(float damage)
    {
        if(!float.IsFinite(damage)||damage<0) return false;
        if(!weaponParams.TryGet(ActiveWeapon,out var row))
        {
            RestoreCriticalRate();
            return false;
        }

        if(criticalRateParamId!=row.Id)
        {
            RestoreCriticalRate();
            if(!weaponParamAddresses.TryGetValue(row.Id,out var address)||!SignatureMatches(address,row.Signature))
            {
                // Rows preserve their PARAM data offsets in the game's loaded
                // bank. Once one signature has located the bank, weapon swaps
                // resolve by offset without another whole-process scan.
                address=weaponParamBase==0?0:weaponParamBase+(nuint)row.DataOffset;
                if(!SignatureMatches(address,row.Signature))
                {
                    var pattern=string.Join(' ',row.Signature.Select(value=>$"{value:X2}"));
                    // The first 12 bytes are unique across regulation 1.04
                    // weapon rows and are used by established param editors.
                    var hits=memory.Scan(pattern).Take(2).ToArray();
                    if(hits.Length!=1) return false;
                    address=hits[0];
                    weaponParamBase=address-(nuint)row.DataOffset;
                }
                weaponParamAddresses[row.Id]=address;
            }
            criticalRateAddress=address+0xDC;
            // Use the installed regulation as the canonical baseline. A prior
            // controller can be terminated before Dispose restores its live
            // edit; reading that stale value here would make the temporary
            // multiplier permanent for the rest of the game session.
            criticalRateOriginal=row.ThrowAttackRate;
            criticalRateParamId=row.Id;
        }

        // throwAtkRate is a signed percentage bonus: 0 means 1.00x and a
        // dagger's 31 means 1.31x. Scale the complete multiplier so both
        // positive heavy-weapon damage and the initial light-weapon penalty
        // apply proportionally to ripostes and backstabs.
        var scaled=(100+criticalRateOriginal)*damage-100;
        var patched=(short)Math.Clamp((int)MathF.Round(scaled),-100,short.MaxValue);
        memory.Write(criticalRateAddress,patched);
        return true;
    }

    bool SignatureMatches(nuint address,byte[] signature)
    {
        if(address==0) return false;
        try { return memory.Read(address,signature.Length).AsSpan().SequenceEqual(signature); }
        catch(System.ComponentModel.Win32Exception) { return false; }
    }

    void RestoreCriticalRate()
    {
        if(criticalRateAddress!=0)
        {
            try { memory.Write(criticalRateAddress,criticalRateOriginal); } catch(System.ComponentModel.Win32Exception) { }
        }
        criticalRateAddress=0;
        criticalRateOriginal=0;
        criticalRateParamId=-1;
    }
    public bool TryGetActiveWeaponAttack(out nuint address,out uint id)
    {
        var manager=DamageManagerInstance;
        var entry=manager==0?0:Ptr(manager); var weapon=ActiveWeapon;
        for(var count=0;entry!=0&&count<128;count++)
        {
            if(memory.Read<uint>(entry+0x8C)==weapon){address=entry;id=memory.Read<uint>(entry);return true;}
            entry=Ptr(entry+0x220);
        }
        address=0; id=0; return false;
    }
    public (float A,float B,float C,float D) ReadAttackGeometry(nuint address)=>(
        memory.Read<float>(address+0x90),memory.Read<float>(address+0x94),
        memory.Read<float>(address+0x98),memory.Read<float>(address+0x9C));
    public (nuint Sphere,nuint Capsule,float SphereRadius,float CapsuleRadius,float CapsuleA,float CapsuleB) ReadAttackShapes(nuint address)
    {
        var sphere=Ptr(address+0x20); var capsule=Ptr(address+0x28);
        return (sphere,capsule,
            sphere==0?float.NaN:memory.Read<float>(sphere+0x20),
            capsule==0?float.NaN:memory.Read<float>(capsule+0x20),
            capsule==0?float.NaN:memory.Read<float>(capsule+0x30),
            capsule==0?float.NaN:memory.Read<float>(capsule+0x40));
    }
    public void Dispose()
    {
        try { RestoreCriticalRate(); } catch { }
        try { if(speedTouched && !Process.GetProcessById(memory.Process.Id).HasExited) memory.Write(speedAddress,originalSpeed); } catch { }
        memory.Dispose();
    }
}
