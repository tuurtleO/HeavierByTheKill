using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HeavierByTheKill.Controller;

internal enum MenuPrompt { None,Reforge,BonfireRest }

internal sealed record OverlaySnapshot(
    WeaponClass WeaponClass,
    string Tier,
    Modifiers Modifiers,
    WeaponProgress Progress,
    uint Souls,
    MenuPrompt Prompt,
    bool BonfireRestConsumed,
    float RestDecayFraction,
    string? Toast);

internal sealed class OverlayHost : IDisposable
{
    readonly Process process;
    readonly Thread thread;
    readonly ManualResetEventSlim ready=new(false);
    OverlaySnapshot? snapshot;
    OverlayForm? form;

    internal OverlayHost(Process process)
    {
        this.process=process;
        thread=new Thread(Run){IsBackground=true,Name="Heavier by the Kill overlay"};
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    void Run()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            form=new OverlayForm(process,()=>Volatile.Read(ref snapshot));
            ready.Set();
            Application.Run(form);
        }
        catch(Exception exception)
        {
            Console.Error.WriteLine($"Overlay disabled: {exception.Message}");
            ready.Set();
        }
    }

    internal void Update(OverlaySnapshot value)=>Volatile.Write(ref snapshot,value);

    internal void ClearPrompt()
    {
        var current=Volatile.Read(ref snapshot);
        if(current is not null) Volatile.Write(ref snapshot,current with {Prompt=MenuPrompt.None});
    }

    public void Dispose()
    {
        try
        {
            var current=form;
            if(current is not null&&!current.IsDisposed)
                current.BeginInvoke(current.Close);
            if(thread.IsAlive) thread.Join(1000);
        }
        catch { }
        ready.Dispose();
    }
}

internal sealed class OverlayForm : Form
{
    const int WsExTransparent=0x20,WsExToolWindow=0x80,WsExNoActivate=0x08000000;
    readonly Process process;
    readonly Func<OverlaySnapshot?> getSnapshot;
    readonly System.Windows.Forms.Timer timer;
    readonly Font titleFont=new("Georgia",9.5f,FontStyle.Bold);
    readonly Font tierFont=new("Georgia",20f,FontStyle.Bold);
    readonly Font bodyFont=new("Georgia",9.5f,FontStyle.Regular);
    readonly Font smallFont=new("Georgia",8.5f,FontStyle.Regular);
    readonly Font valueFont=new("Georgia",11f,FontStyle.Bold);

    internal OverlayForm(Process process,Func<OverlaySnapshot?> getSnapshot)
    {
        this.process=process; this.getSnapshot=getSnapshot;
        FormBorderStyle=FormBorderStyle.None;
        ShowInTaskbar=false;
        TopMost=true;
        BackColor=Color.FromArgb(10,10,9);
        Opacity=0.90;
        ClientSize=new Size(430,258);
        StartPosition=FormStartPosition.Manual;
        DoubleBuffered=true;
        timer=new System.Windows.Forms.Timer{Interval=33};
        timer.Tick+=(_,_)=>
        {
            try { TickOverlay(); }
            catch(Exception exception)
            {
                Console.Error.WriteLine($"Overlay frame skipped: {exception.Message}");
                Hide();
            }
        };
        timer.Start();
    }

    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams
    {
        get
        {
            var value=base.CreateParams;
            value.ExStyle|=WsExTransparent|WsExToolWindow|WsExNoActivate;
            return value;
        }
    }

    void TickOverlay()
    {
        var state=getSnapshot();
        if(process.HasExited||state is null)
        {
            Hide(); return;
        }
        var gameWindow=process.MainWindowHandle;
        if(gameWindow==0||GetForegroundWindow()!=gameWindow||!GetClientRect(gameWindow,out var rect))
        {
            Hide(); return;
        }
        var origin=new PointNative();
        if(!ClientToScreen(gameWindow,ref origin)){Hide();return;}
        var width=rect.Right-rect.Left;
        var hasToast=!string.IsNullOrWhiteSpace(state.Toast);
        var hasPrompt=state.Prompt!=MenuPrompt.None;
        var desiredHeight=hasToast&&hasPrompt?492:hasToast?370:hasPrompt?442:310;
        SetBounds(origin.X+Math.Max(12,width-430-28),origin.Y+28,430,desiredHeight);
        if(!Visible) Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var state=getSnapshot(); if(state is null) return;
        var graphics=e.Graphics;
        graphics.SmoothingMode=SmoothingMode.AntiAlias;
        graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var background=new LinearGradientBrush(ClientRectangle,Color.FromArgb(24,23,20),Color.FromArgb(8,8,7),LinearGradientMode.Horizontal);
        graphics.FillRectangle(background,ClientRectangle);
        using var outerBorder=new Pen(Color.FromArgb(124,105,69));
        using var innerBorder=new Pen(Color.FromArgb(48,43,34));
        graphics.DrawRectangle(outerBorder,0,0,ClientSize.Width-1,ClientSize.Height-1);
        graphics.DrawRectangle(innerBorder,4,4,ClientSize.Width-9,ClientSize.Height-9);
        using var gold=new SolidBrush(Color.FromArgb(194,159,92));
        using var white=new SolidBrush(Color.FromArgb(226,220,203));
        using var muted=new SolidBrush(Color.FromArgb(160,154,140));
        using var separator=new Pen(Color.FromArgb(94,78,50));
        var hasContext=state.Prompt!=MenuPrompt.None||!string.IsNullOrWhiteSpace(state.Toast);
        if(hasContext) graphics.DrawLine(separator,22,306,408,306);
        var tierColor=state.Tier switch
        {
            "Cataclysmic"=>Color.FromArgb(184,72,63),
            "Worldbreaker"=>Color.FromArgb(190,91,62),
            "Devastating"=>Color.FromArgb(197,108,64),
            "Crushing"=>Color.FromArgb(202,132,68),
            "Burdened"=>Color.FromArgb(199,164,91),
            "Tempered"=>Color.FromArgb(164,174,151),
            _=>Color.FromArgb(197,194,181)
        };
        using var tierBrush=new SolidBrush(tierColor);
        graphics.DrawString(state.Tier.ToUpperInvariant(),tierFont,tierBrush,22,12);
        graphics.DrawString("WEAPON TYPE",smallFont,gold,24,57);
        graphics.DrawString(state.WeaponClass.ToString().ToUpperInvariant(),bodyFont,white,24,76);
        graphics.DrawLine(separator,22,103,408,103);

        using var rowA=new SolidBrush(Color.FromArgb(26,24,20));
        using var rowB=new SolidBrush(Color.FromArgb(20,19,16));
        using var right=new StringFormat{Alignment=StringAlignment.Far,LineAlignment=StringAlignment.Center};
        using var center=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center};
        using var noWrap=new StringFormat{FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter};
        var weightRows=new[]{
            ("TOTAL WEIGHT",state.Modifiers.Weight,Color.FromArgb(226,220,203)),
            ("TEMPORARY",state.Progress.Temporary,Color.FromArgb(188,183,169)),
            ("PERMANENT",state.Progress.Permanent,Color.FromArgb(194,159,92))
        };
        for(var index=0;index<weightRows.Length;index++)
        {
            var y=112+index*27;
            graphics.FillRectangle(index%2==0?rowA:rowB,18,y,394,25);
            graphics.DrawString(weightRows[index].Item1,smallFont,muted,30,y+6);
            using var valueBrush=new SolidBrush(weightRows[index].Item3);
            graphics.DrawString(weightRows[index].Item2.ToString("F2"),valueFont,valueBrush,new RectangleF(300,y,98,25),right);
        }
        using var trackBack=new SolidBrush(Color.FromArgb(52,49,42));
        using var temporaryBrush=new SolidBrush(Color.FromArgb(140,136,125));
        using var permanentBrush=new SolidBrush(Color.FromArgb(174,137,70));
        const int compositionBarY=205;
        graphics.FillRectangle(trackBack,18,compositionBarY,394,7);
        var total=Math.Max(0,state.Modifiers.Weight);
        if(total>0)
        {
            var temporaryWidth=394*Math.Clamp(state.Progress.Temporary/total,0,1);
            var permanentWidth=394*Math.Clamp(state.Progress.Permanent/total,0,1);
            graphics.FillRectangle(temporaryBrush,18,compositionBarY,temporaryWidth,7);
            graphics.FillRectangle(permanentBrush,18+temporaryWidth,compositionBarY,Math.Min(permanentWidth,394-temporaryWidth),7);
        }

        var stats=new[]{("DMG",state.Modifiers.Damage),("SPEED",state.Modifiers.AttackSpeed),("STA",state.Modifiers.StaminaCost)};
        for(var index=0;index<stats.Length;index++)
        {
            var x=18+index*133;
            graphics.FillRectangle(rowA,x,237,128,54);
            graphics.DrawRectangle(innerBorder,x,237,128,54);
            graphics.DrawString(stats[index].Item1,smallFont,muted,new RectangleF(x,243,128,15),center);
            graphics.DrawString($"×{stats[index].Item2:F2}",valueFont,white,new RectangleF(x,259,128,25),center);
        }

        var contextY=316;
        if(!string.IsNullOrWhiteSpace(state.Toast))
        {
            using var panel=new SolidBrush(Color.FromArgb(39,34,25));
            graphics.FillRectangle(panel,16,contextY,398,40);
            graphics.DrawString(state.Toast,smallFont,white,new RectangleF(26,contextY+11,378,22));
            contextY+=50;
        }
        if(state.Prompt!=MenuPrompt.None)
        {
            using var panel=new SolidBrush(Color.FromArgb(39,34,25));
            graphics.FillRectangle(panel,16,contextY,398,112);
            graphics.DrawLine(separator,26,contextY+67,404,contextY+67);
            if(state.Prompt==MenuPrompt.Reforge)
            {
                var reforgeWeight=ReforgeRules.BatchWeight(state.Progress.Temporary);
                var soulCost=ReforgeRules.Cost(reforgeWeight);
                var canAfford=reforgeWeight>0&&state.Souls>=soulCost;
                graphics.DrawString(reforgeWeight>0?$"REFORGE {reforgeWeight:F2} WEIGHT":"NO TEMPORARY WEIGHT",titleFont,gold,26,contextY+10);
                if(reforgeWeight>0) graphics.DrawString("Press R1 or F8 to apply",smallFont,white,new RectangleF(26,contextY+37,378,18));
                using var insufficient=new SolidBrush(Color.FromArgb(205,91,77));
                var costBrush=reforgeWeight<=0?muted:canAfford?white:insufficient;
                var costText=reforgeWeight>0?$"COST  {soulCost:N0} SOULS":"COST  0 SOULS";
                graphics.DrawString(costText,smallFont,costBrush,new RectangleF(26,contextY+80,378,20),noWrap);
            }
            else
            {
                graphics.DrawString(state.BonfireRestConsumed?"WEIGHT ALREADY REDUCED":"LIGHTEN EQUIPPED WEAPON",titleFont,gold,26,contextY+10);
                if(!state.BonfireRestConsumed) graphics.DrawString("Press R1 or F8 to apply",smallFont,white,new RectangleF(26,contextY+37,378,18));
                var detail=state.BonfireRestConsumed?"AVAILABLE AGAIN NEXT REST":$"REMOVES {state.RestDecayFraction:P0} TEMPORARY WEIGHT";
                graphics.DrawString(detail,smallFont,muted,new RectangleF(26,contextY+80,378,20),noWrap);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing){timer.Dispose();titleFont.Dispose();tierFont.Dispose();bodyFont.Dispose();smallFont.Dispose();valueFont.Dispose();}
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] struct RectNative { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct PointNative { public int X,Y; }
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetClientRect(nint window,out RectNative rect);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool ClientToScreen(nint window,ref PointNative point);
}
