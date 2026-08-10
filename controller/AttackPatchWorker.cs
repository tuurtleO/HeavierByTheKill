using System.Diagnostics;

namespace HeavierByTheKill.Controller;

internal sealed record AttackPatchSettings(float Damage,float Impact,float Knockback,float Radius);

internal sealed class AttackPatchWorker : IDisposable
{
    readonly LiveGame game;
    readonly Process process;
    readonly Thread thread;
    AttackPatchSettings settings=new(1,1,1,1);
    long generation;
    volatile bool stopping;

    internal AttackPatchWorker(LiveGame game,Process process)
    {
        this.game=game;
        this.process=process;
        thread=new Thread(Run)
        {
            IsBackground=true,
            Name="Heavier by the Kill combat patcher",
            Priority=ThreadPriority.AboveNormal
        };
        thread.Start();
    }

    internal long Generation=>Interlocked.Read(ref generation);

    internal void Update(float damage,float impact,float knockback,float radius)=>
        Volatile.Write(ref settings,new(damage,impact,knockback,radius));

    void Run()
    {
        while(!stopping&&!process.HasExited)
        {
            try
            {
                var desired=Volatile.Read(ref settings);
                if(game.PatchActiveWeaponAttack(desired.Damage,desired.Impact,desired.Knockback,desired.Radius))
                    Interlocked.Increment(ref generation);
            }
            catch(System.ComponentModel.Win32Exception) { }
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        stopping=true;
        if(thread.IsAlive) thread.Join(1000);
    }
}
