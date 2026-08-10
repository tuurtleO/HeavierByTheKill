using System.Diagnostics;
using System.Runtime.InteropServices;
using HeavierByTheKill.Controller;

if(args.Length<1 || args[0] is not ("scan" or "dump" or "run" or "undo-rest" or "gamepad-watch" or "damage-watch" or "state-watch" or "weapon-watch" or "bonfire-watch" or "attack-watch" or "critical-rate" or "profile-state" or "camera-test" or "menu-state" or "menu-fingerprint" or "world-references")) { Console.Error.WriteLine("Usage: HeavierByTheKill.Controller run | undo-rest | gamepad-watch | weapon-watch | bonfire-watch | attack-watch | critical-rate | profile-state | camera-test | menu-state | menu-fingerprint | world-references | damage-watch | state-watch | scan \"AA BB ? CC\" | dump ADDRESS [LENGTH]"); return 2; }
var process=Process.GetProcessesByName("DarkSoulsRemastered").SingleOrDefault();
if(process is null){ Console.Error.WriteLine("DarkSoulsRemastered is not running."); return 3; }
Mutex? instanceMutex=null;
if(args[0]=="run")
{
    instanceMutex=new Mutex(true,@"Local\HeavierByTheKill.Controller.Run",out var createdNew);
    if(!createdNew)
    {
        instanceMutex.Dispose();
        Console.Error.WriteLine("Heavier by the Kill is already running. Only one controller instance is allowed.");
        return 8;
    }
}
using var instanceGuard=instanceMutex;
if(args[0]=="gamepad-watch")
{
    using var inputGame=new LiveGame(process);
    Console.WriteLine("Watching XInput, virtual-key, and DSR-internal R1 input for 15 seconds. Press R1 several times.");
    var previous=GamepadInput.Buttons();
    var previousKeys=Enumerable.Range(1,254).ToDictionary(key=>key,Keyboard.Down);
    var previousGameR1=inputGame.R1Down;
    Console.WriteLine($"Initial buttons: 0x{previous:X4}");
    var until=DateTime.UtcNow.AddSeconds(15);
    while(DateTime.UtcNow<until)
    {
        var current=GamepadInput.Buttons();
        var pressed=(ushort)(current&~previous);
        if(pressed!=0) Console.WriteLine($"Pressed: 0x{pressed:X4}");
        previous=current;
        for(var key=1;key<255;key++)
        {
            var down=Keyboard.Down(key);
            if(down&&!previousKeys[key]) Console.WriteLine($"Virtual key: 0x{key:X2}");
            previousKeys[key]=down;
        }
        var gameR1=inputGame.R1Down;
        if(gameR1&&!previousGameR1) Console.WriteLine("DSR internal R1 pressed");
        previousGameR1=gameR1;
        Thread.Sleep(4);
    }
    return 0;
}
if(args[0]=="undo-rest")
{
    CoreNative.Initialize();
    using var undoGame=new LiveGame(process);
    if(!undoGame.IsLoaded||string.IsNullOrWhiteSpace(undoGame.CharacterName))
    {
        Console.Error.WriteLine("No loaded named character was found.");
        return 4;
    }
    if(!CoreNative.SelectProfile(undoGame.CharacterName))
    {
        Console.Error.WriteLine($"Could not load progression profile for {undoGame.CharacterName}.");
        return 10;
    }
    if(!CoreNative.UndoRest()){Console.Error.WriteLine("The configured rest decay cannot be reversed.");return 7;}
    Console.WriteLine($"Reversed one bonfire-rest decay event for {undoGame.CharacterName}.");
    return 0;
}
if(args[0]=="run")
{
    CoreNative.Initialize();
    var balance=CoreNative.GetConfig();
    using var game=new LiveGame(process);
    using var inputBridge=InputBridge.TryAttach(process,out var inputBridgeStatus);
    Console.WriteLine($"Heavier by the Kill attached to PID {process.Id}. Keep the game offline. Press Ctrl+C to stop.");
    Console.WriteLine(inputBridgeStatus);
    Console.WriteLine("Reforge is available on Reinforce Weapon; bonfire weight reduction is available on the bonfire main menu. Both use R1/F8.");
    var stop=false; Console.CancelKeyPress+=(s,e)=>{e.Cancel=true;stop=true;};
    var timedSeconds=args.Length>1 && int.TryParse(args[1],out var parsedSeconds)?parsedSeconds:0;
    var testDamage=args.Length>2 && float.TryParse(args[2],System.Globalization.CultureInfo.InvariantCulture,out var parsedDamage)?parsedDamage:(float?)null;
    var testImpact=args.Length>3 && float.TryParse(args[3],System.Globalization.CultureInfo.InvariantCulture,out var parsedImpact)?parsedImpact:(float?)null;
    var testKnockback=args.Length>4 && float.TryParse(args[4],System.Globalization.CultureInfo.InvariantCulture,out var parsedKnockback)?parsedKnockback:(float?)null;
    var testRadius=args.Length>5 && float.TryParse(args[5],System.Globalization.CultureInfo.InvariantCulture,out var parsedRadius)?parsedRadius:(float?)null;
    var stopAt=timedSeconds>0?DateTime.UtcNow.AddSeconds(timedSeconds):DateTime.MaxValue;
    while(!process.HasExited&&DateTime.UtcNow<stopAt)
    {
        try { if(game.IsLoaded&&!string.IsNullOrWhiteSpace(game.CharacterName)) break; }
        catch(System.ComponentModel.Win32Exception) { }
        Thread.Sleep(50);
    }
    if(process.HasExited||!game.IsLoaded||string.IsNullOrWhiteSpace(game.CharacterName)){Console.Error.WriteLine("No loaded named character was found.");return 4;}
    var activeProfile=game.CharacterName;
    if(!CoreNative.SelectProfile(activeProfile)){Console.Error.WriteLine($"Could not load progression profile for {activeProfile}.");return 10;}
    game.ResetBossDefeatBaseline();
    game.ResetTransientRuntime();
    var activeCharacter=game.Character;
    var activeDamageManager=game.DamageManagerInstance;
    Console.WriteLine($"Progression profile: {activeProfile}.");
    using var overlay=new OverlayHost(process);
    using var obsOverlay=new ObsOverlayHost();
    using var attackPatcher=new AttackPatchWorker(game,process);
    Console.WriteLine(obsOverlay.Status);
    var weapon=game.ActiveWeapon; var souls=game.Souls; var stamina=game.Stamina; var killCooldown=DateTime.MinValue;
    var bonfireSessionActive=game.IsBonfireMainMenu; var bonfireRestConsumed=false; DateTime? menuClosedSince=null;
    var health=game.Health; var deathPending=false; var awaitingBloodstain=false; uint soulsAtDeath=0;
    var bossCreditCooldown=DateTime.MinValue;
    var exhaustionUntil=DateTime.MinValue;
    var forgeKeyHeld=false; var lastControllerActionAt=DateTime.MinValue; var lastVerifiedPrompt=MenuPrompt.None; var lastVerifiedPromptAt=DateTime.MinValue; var previousInternalR1=false;
    var lastStateReadFaultAt=DateTime.MinValue;
    var worldWasUnavailable=false;
    string? overlayToast="Overlay active • stay offline"; var overlayToastUntil=DateTime.UtcNow.AddSeconds(3);
    var recentAttackKind=AttackKind.Quick; var recentAttackIntentAt=DateTime.MinValue;
    var shakeStarted=DateTime.MinValue; var shakeUntil=DateTime.MinValue; float shakeIntensity=0; var shakeX=0; var shakeY=0;
    var lastAttackPatchGeneration=attackPatcher.Generation;
    while(!stop && !process.HasExited && DateTime.UtcNow<stopAt)
    {
      try
      {
        if(!game.IsLoaded){worldWasUnavailable=true;overlay.ClearPrompt();obsOverlay.ClearPrompt();Thread.Sleep(50);continue;}
        var observedProfile=game.CharacterName;
        if(string.IsNullOrWhiteSpace(observedProfile)){worldWasUnavailable=true;overlay.ClearPrompt();obsOverlay.ClearPrompt();Thread.Sleep(50);continue;}
        var observedCharacter=game.Character;
        var observedDamageManager=game.DamageManagerInstance;
        if(!string.Equals(observedProfile,activeProfile,StringComparison.OrdinalIgnoreCase))
        {
            if(!CoreNative.SelectProfile(observedProfile))
            {
                overlay.ClearPrompt();obsOverlay.ClearPrompt();Thread.Sleep(100);continue;
            }
            activeProfile=observedProfile;
            worldWasUnavailable=false;
            activeCharacter=observedCharacter;
            activeDamageManager=observedDamageManager;
            game.ResetTransientRuntime();
            game.ResetBossDefeatBaseline();
            var switchedAt=DateTime.UtcNow;
            weapon=game.ActiveWeapon; souls=game.Souls; stamina=game.Stamina; health=game.Health;
            killCooldown=switchedAt.AddSeconds(3); bossCreditCooldown=switchedAt.AddSeconds(3);
            deathPending=false; awaitingBloodstain=false; soulsAtDeath=0; exhaustionUntil=DateTime.MinValue;
            bonfireSessionActive=game.IsBonfireMainMenu; bonfireRestConsumed=false; menuClosedSince=null;
            forgeKeyHeld=false; previousInternalR1=false; lastVerifiedPrompt=MenuPrompt.None; lastVerifiedPromptAt=DateTime.MinValue;
            recentAttackKind=AttackKind.Quick; recentAttackIntentAt=DateTime.MinValue;
            shakeStarted=shakeUntil=DateTime.MinValue; shakeIntensity=0;
            if((shakeX!=0||shakeY!=0)&&Keyboard.IsForeground(process.MainWindowHandle)) Keyboard.MouseMove(-shakeX,-shakeY);
            shakeX=shakeY=0;
            overlayToast=$"PROFILE • {activeProfile}"; overlayToastUntil=switchedAt.AddSeconds(3);
            Console.WriteLine($"Progression profile switched to {activeProfile}; event baselines refreshed.");
            Thread.Sleep(25); continue;
        }
        if(worldWasUnavailable || observedCharacter!=activeCharacter
            || (observedDamageManager!=0 && observedDamageManager!=activeDamageManager))
        {
            worldWasUnavailable=false;
            activeCharacter=observedCharacter;
            activeDamageManager=observedDamageManager;
            game.ResetTransientRuntime();
            game.ResetBossDefeatBaseline();
            var reloadedAt=DateTime.UtcNow;
            weapon=game.ActiveWeapon; souls=game.Souls; stamina=game.Stamina; health=game.Health;
            killCooldown=reloadedAt.AddSeconds(3); bossCreditCooldown=reloadedAt.AddSeconds(3);
            deathPending=false; awaitingBloodstain=false; soulsAtDeath=0; exhaustionUntil=DateTime.MinValue;
            bonfireSessionActive=game.IsBonfireMainMenu; bonfireRestConsumed=false; menuClosedSince=null;
            forgeKeyHeld=false; previousInternalR1=false; lastVerifiedPrompt=MenuPrompt.None; lastVerifiedPromptAt=DateTime.MinValue;
            recentAttackKind=AttackKind.Quick; recentAttackIntentAt=DateTime.MinValue;
            shakeStarted=shakeUntil=DateTime.MinValue; shakeIntensity=0;
            if((shakeX!=0||shakeY!=0)&&Keyboard.IsForeground(process.MainWindowHandle)) Keyboard.MouseMove(-shakeX,-shakeY);
            shakeX=shakeY=0;
            lastAttackPatchGeneration=attackPatcher.Generation;
            overlayToast="WORLD RELOADED â€¢ MOD ACTIVE"; overlayToastUntil=reloadedAt.AddSeconds(3);
            Console.WriteLine($"World runtime changed (character 0x{activeCharacter:X}, damage manager 0x{activeDamageManager:X}); transient baselines refreshed.");
            Thread.Sleep(25); continue;
        }
        var currentWeapon=game.ActiveWeapon; var currentWeaponClass=game.ActiveWeaponClass; var key=new WeaponKey(currentWeapon,0,0); var mods=CoreNative.GetModifiersFor(key,currentWeaponClass);
        var currentHealth=health;
        // Event-flag and death pointers can briefly fail while menus are open.
        // Keep that failure local so it cannot suppress menu input polling.
        try
        {
        if(game.PollNewBossDefeat() is int bossFlag)
        {
            var creditedWeapon=DateTime.UtcNow-game.LastAttackCreatedAt<TimeSpan.FromSeconds(15)?game.LastAttackWeapon:currentWeapon;
            var creditedKey=new WeaponKey(creditedWeapon,0,0); var legacy=game.BossLegacy(bossFlag); var legacyType=LiveGame.BossLegacyType(legacy);
            CoreNative.OnKillEx(creditedKey,true,LiveGame.WeaponClassFor(creditedWeapon),legacy); bossCreditCooldown=DateTime.UtcNow.AddSeconds(5);
            Console.WriteLine($"{legacyType} legacy from boss flag {bossFlag} credited to {creditedWeapon}; weight is now {CoreNative.GetModifiersFor(creditedKey,LiveGame.WeaponClassFor(creditedWeapon)).Weight:F1}.");
            overlayToast=$"{legacyType} LEGACY • +{balance.BossWeight*balance.BossPermanentFraction:F2} PERMANENT"; overlayToastUntil=DateTime.UtcNow.AddSeconds(4);
        }
        currentHealth=game.Health;
        if(currentHealth==0&&health>0)
        {
            CoreNative.OnDeath(0.5f); deathPending=true; awaitingBloodstain=false; soulsAtDeath=game.Souls;
            Console.WriteLine("Player death detected; 50% of temporary weapon weight moved to the bloodstain.");
            overlayToast="50% temporary weight left at bloodstain"; overlayToastUntil=DateTime.UtcNow.AddSeconds(4);
        }
        if(currentHealth>0&&health==0&&deathPending)
        {
            deathPending=false; awaitingBloodstain=true;
            if(game.Souls>=soulsAtDeath&&soulsAtDeath>0){CoreNative.OnBloodstainRecovered();awaitingBloodstain=false;Console.WriteLine("Protected death detected; weapon weight restored.");overlayToast="Temporary weapon weight restored";overlayToastUntil=DateTime.UtcNow.AddSeconds(3);}
        }
        }
        catch(System.ComponentModel.Win32Exception error)
        {
            if(DateTime.UtcNow-lastStateReadFaultAt>=TimeSpan.FromSeconds(2))
            {
                Console.WriteLine($"Transient boss/death state read failed; menu input remains active: {error.Message}");
                lastStateReadFaultAt=DateTime.UtcNow;
            }
        }
        var now=DateTime.UtcNow;
        var blacksmithReinforce=game.IsBlacksmithReinforceMenu;
        var bonfireMain=game.IsBonfireMainMenu;
        var rawMenu=game.RawMenuState;
        if(bonfireMain)
        {
            menuClosedSince=null;
            if(!bonfireSessionActive){bonfireSessionActive=true;bonfireRestConsumed=false;}
        }
        else if(rawMenu.Primary==1)
        {
            menuClosedSince??=now;
            if(now-menuClosedSince.Value>=TimeSpan.FromMilliseconds(250)){bonfireSessionActive=false;bonfireRestConsumed=false;}
        }
        else menuClosedSince=null;
        var menuPrompt=blacksmithReinforce?MenuPrompt.Reforge:bonfireMain?MenuPrompt.BonfireRest:MenuPrompt.None;
        if(menuPrompt!=MenuPrompt.None){lastVerifiedPrompt=menuPrompt;lastVerifiedPromptAt=now;}
        var latchedPrompt=menuPrompt!=MenuPrompt.None?menuPrompt:now-lastVerifiedPromptAt<=TimeSpan.FromMilliseconds(400)?lastVerifiedPrompt:MenuPrompt.None;
        var foreground=Keyboard.IsForeground(process.MainWindowHandle);
        var actionKeyDown=foreground&&Keyboard.Down(0x77); // F8
        var internalR1=game.R1Down;
        var menuR1=internalR1||(inputBridge?.RightShoulderDown??false)||GamepadInput.MenuActionButtonDown();
        if(menuR1&&!previousInternalR1) Console.WriteLine($"R1 edge detected; current prompt={menuPrompt}, latched prompt={latchedPrompt}.");
        previousInternalR1=menuR1;
        var controllerButton=latchedPrompt!=MenuPrompt.None&&menuR1;
        var controllerPressed=controllerButton&&now-lastControllerActionAt>=TimeSpan.FromMilliseconds(500);
        if(controllerPressed) lastControllerActionAt=now;
        var actionPressed=(actionKeyDown&&!forgeKeyHeld)||controllerPressed;
        // DSR briefly changes menu fields while the menu is animating or
        // refreshing a list. Use the same verified prompt latch for keyboard
        // and controller actions so a one-frame fingerprint gap cannot make
        // the prompt flash or swallow F8.
        var actionPrompt=latchedPrompt;
        if(actionPressed)
        {
            if(actionPrompt==MenuPrompt.Reforge)
            {
                var availableSouls=game.Souls;
                var requestedWeight=ReforgeRules.BatchWeight(CoreNative.GetProgress(key).Temporary);
                var requiredSouls=ReforgeRules.Cost(requestedWeight);
                var receipt=requiredSouls>0&&availableSouls>=requiredSouls?CoreNative.Forge(key,requestedWeight,availableSouls):default;
                if(receipt.ConvertedWeight>0)
                {
                    game.SpendSouls(receipt.SoulCost);
                    Console.WriteLine($"Reforged {receipt.ConvertedWeight:F1} temporary weight on equipped weapon {currentWeapon} permanently for {receipt.SoulCost:N0} souls.");
                    overlayToast=$"REFORGED {receipt.ConvertedWeight:F1} • −{receipt.SoulCost:N0} souls"; overlayToastUntil=DateTime.UtcNow.AddSeconds(3);
                }
                else if(requestedWeight<=0){Console.WriteLine("Reforge failed: this weapon has no temporary weight.");overlayToast="REFORGE FAILED • no temporary weight";overlayToastUntil=DateTime.UtcNow.AddSeconds(3);}
                else {Console.WriteLine($"Reforge failed: {requiredSouls:N0} souls required.");overlayToast=$"REFORGE FAILED • NEED {requiredSouls:N0} SOULS";overlayToastUntil=DateTime.UtcNow.AddSeconds(3);}
            }
            else if(actionPrompt==MenuPrompt.BonfireRest)
            {
                if(!bonfireRestConsumed)
                {
                    CoreNative.OnRest(); bonfireRestConsumed=true; bonfireSessionActive=true;
                    Console.WriteLine("Bonfire weight reduction applied by explicit menu action.");
                    overlayToast=$"BONFIRE • temporary weight reduced {balance.RestDecayFraction:P0}"; overlayToastUntil=now.AddSeconds(3);
                }
                else {overlayToast="BONFIRE • weight already reduced this rest";overlayToastUntil=now.AddSeconds(3);}
            }
            else {Console.WriteLine("Menu action unavailable: open a blacksmith or bonfire main menu.");overlayToast="Open a blacksmith or bonfire main menu";overlayToastUntil=DateTime.UtcNow.AddSeconds(3);}
        }
        forgeKeyHeld=actionKeyDown;
        if(DateTime.UtcNow>=overlayToastUntil) overlayToast=null;
        var currentSouls=game.Souls;
        var promptModifiers=CoreNative.GetModifiersFor(key,currentWeaponClass);
        var promptProgress=CoreNative.GetProgress(key);
        var overlaySnapshot=new OverlaySnapshot(currentWeaponClass,balance.TierFor(promptModifiers.Weight),promptModifiers,promptProgress,currentSouls,latchedPrompt,bonfireRestConsumed,balance.RestDecayFraction,overlayToast);
        overlay.Update(overlaySnapshot);
        obsOverlay.Update(overlaySnapshot);
        if(game.AttackIntent is AttackKind intent){recentAttackKind=intent;recentAttackIntentAt=DateTime.UtcNow;}
        if(game.AnimationAttackKind is AttackKind animationKind){recentAttackKind=animationKind;recentAttackIntentAt=DateTime.UtcNow;}
        var attackKind=DateTime.UtcNow-recentAttackIntentAt<TimeSpan.FromSeconds(recentAttackKind==AttackKind.Critical?5:3)?recentAttackKind:AttackKind.Quick;
        var outcome=CoreNative.Attack(key,currentWeaponClass,attackKind,0,0); var attackMods=outcome.Modifiers;
        var recoverySpeed=attackMods.AttackSpeed/MathF.Max(1,attackMods.Recovery);
        if(DateTime.UtcNow<exhaustionUntil) recoverySpeed*=0.5f;
        recoverySpeed=MathF.Max(balance.MinimumAttackSpeedMultiplier,recoverySpeed);
        var attacking=game.IsAttackAnimation;
        game.SetSpeed(attacking&&attackKind!=AttackKind.Critical?recoverySpeed:1f);
        var radialRadius=attackKind==AttackKind.Heavy?1+outcome.RadialStagger:1;
        game.PatchActiveWeaponCriticalDamage(testDamage??attackMods.Damage);
        attackPatcher.Update(testDamage??attackMods.Damage,testImpact??attackMods.Impact,testKnockback??attackMods.Knockback,testRadius??radialRadius);
        var attackPatchGeneration=attackPatcher.Generation;
        var attackPatched=attackPatchGeneration!=lastAttackPatchGeneration;
        lastAttackPatchGeneration=attackPatchGeneration;
        if(attackPatched&&attackKind==AttackKind.Heavy&&mods.Weight>=balance.CameraShakeStartsAt)
        {
            shakeIntensity=Math.Clamp(Math.Max(outcome.PresentationIntensity,(mods.Weight-balance.CameraShakeStartsAt)/55),0.15f,3f);
            shakeStarted=DateTime.UtcNow; shakeUntil=shakeStarted.AddSeconds(0.16+0.08*shakeIntensity);
        }
        if(DateTime.UtcNow<shakeUntil)
        {
            var elapsed=(DateTime.UtcNow-shakeStarted).TotalSeconds; var duration=(shakeUntil-shakeStarted).TotalSeconds;
            var envelope=(float)Math.Max(0,1-elapsed/duration); var phase=(float)(elapsed*Math.PI*2*32);
            var nextX=(int)MathF.Round(MathF.Sin(phase)*2.5f*shakeIntensity*envelope);
            var nextY=(int)MathF.Round(MathF.Cos(phase*0.73f)*1.5f*shakeIntensity*envelope);
            if(Keyboard.IsForeground(process.MainWindowHandle)){Keyboard.MouseMove(nextX-shakeX,nextY-shakeY);shakeX=nextX;shakeY=nextY;}
        }
        else if((shakeX!=0||shakeY!=0)&&Keyboard.IsForeground(process.MainWindowHandle)){Keyboard.MouseMove(-shakeX,-shakeY);shakeX=shakeY=0;}
        var currentStamina=game.Stamina;
        if(attacking&&currentStamina<stamina)
        {
            var vanilla=stamina-currentStamina; var extra=(uint)MathF.Round(vanilla*MathF.Max(0,attackMods.StaminaCost-1));
            if(extra>0)
            {
                if(extra>=currentStamina) exhaustionUntil=DateTime.UtcNow.AddSeconds(Math.Max(outcome.ExhaustionSeconds,0.35+0.35*(attackMods.Recovery-1)));
                game.SpendExtraStamina(extra);
            }
        }
        currentSouls=game.Souls;
        var recentWeaponAttack=DateTime.UtcNow-game.LastAttackCreatedAt<TimeSpan.FromSeconds(3);
        if(awaitingBloodstain&&currentSouls>souls&&!recentWeaponAttack)
        {
            CoreNative.OnBloodstainRecovered(); awaitingBloodstain=false;
            Console.WriteLine("Bloodstain recovery detected; escrowed weapon weight restored.");
            overlayToast="BLOODSTAIN • temporary weight restored"; overlayToastUntil=DateTime.UtcNow.AddSeconds(3);
        }
        if(currentSouls>souls && (attacking||recentWeaponAttack) && DateTime.UtcNow>=killCooldown && DateTime.UtcNow>=bossCreditCooldown)
        {
            var creditedWeapon=DateTime.UtcNow-game.LastAttackCreatedAt<TimeSpan.FromSeconds(10)?game.LastAttackWeapon:currentWeapon;
            var creditedKey=new WeaponKey(creditedWeapon,0,0);
            var creditedClass=LiveGame.WeaponClassFor(creditedWeapon);
            var beforeKill=CoreNative.GetModifiersFor(creditedKey,creditedClass).Weight;
            CoreNative.OnKillEx(creditedKey,false,LiveGame.WeaponClassFor(creditedWeapon),0); killCooldown=DateTime.UtcNow.AddMilliseconds(350);
            var afterKill=CoreNative.GetModifiersFor(creditedKey,creditedClass).Weight;
            Console.WriteLine($"Kill credited to {creditedWeapon}; weight is now {afterKill:F1}.");
            overlayToast=$"KILL • +{afterKill-beforeKill:F2} • TOTAL {afterKill:F2}"; overlayToastUntil=DateTime.UtcNow.AddSeconds(2);
        }
        if(currentWeapon!=weapon)
        {
            weapon=currentWeapon; var tier=balance.TierFor(mods.Weight);
            Console.WriteLine($"Active weapon: {weapon}, weight {mods.Weight:F1}, tier {tier}.");
            overlayToast=$"{currentWeaponClass.ToString().ToUpperInvariant()} EQUIPPED • {mods.Weight:F2} WEIGHT"; overlayToastUntil=DateTime.UtcNow.AddSeconds(2);
        }
        souls=currentSouls; stamina=game.Stamina; health=currentHealth; Thread.Sleep(12);
      }
      catch(System.ComponentModel.Win32Exception)
      {
        // World/animation pointers are briefly invalid during warps, deaths,
        // and quit-outs. Preserve progression and resume after the transition.
        overlay.ClearPrompt(); obsOverlay.ClearPrompt(); Thread.Sleep(50);
      }
    }
    if((shakeX!=0||shakeY!=0)&&Keyboard.IsForeground(process.MainWindowHandle)) Keyboard.MouseMove(-shakeX,-shakeY);
    CoreNative.Save(); return 0;
}
if(args[0]=="weapon-watch")
{
    using var game=new LiveGame(process);
    Console.WriteLine("Watching character and active weapon for 45 seconds.");
    nuint oldCharacter=nuint.MaxValue; uint oldWeapon=uint.MaxValue;
    var until=DateTime.UtcNow.AddSeconds(45);
    while(DateTime.UtcNow<until&&!process.HasExited)
    {
        try
        {
            var character=game.Character;
            var weapon=character==0?0:game.ActiveWeapon;
            if(character!=oldCharacter||weapon!=oldWeapon)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} character=0x{character:X} weapon={weapon} class={(character==0?"-":game.ActiveWeaponClass)} loaded={game.IsLoaded}");
                oldCharacter=character; oldWeapon=weapon;
            }
        }
        catch(System.ComponentModel.Win32Exception) { }
        Thread.Sleep(8);
    }
    return 0;
}
if(args[0]=="bonfire-watch")
{
    using var game=new LiveGame(process);
    Console.WriteLine("Watching bonfire/menu state for 120 seconds.");
    (int Primary,uint Secondary) oldMenu=(int.MaxValue,uint.MaxValue); var oldAnimation=int.MinValue; var oldAny=false; var oldBonfire=false;
    var until=DateTime.UtcNow.AddSeconds(120);
    while(DateTime.UtcNow<until&&!process.HasExited)
    {
        try
        {
            if(!game.IsLoaded){Thread.Sleep(20);continue;}
            var menu=game.RawMenuState; var animation=game.AnimationId; var any=game.IsAnyMenuOpen; var bonfire=game.IsBonfireMenu;
            if(menu!=oldMenu||animation!=oldAnimation||any!=oldAny||bonfire!=oldBonfire)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} anim={animation} menu=({menu.Primary},0x{menu.Secondary:X8}) any={any} bonfire={bonfire}");
                oldMenu=menu; oldAnimation=animation; oldAny=any; oldBonfire=bonfire;
            }
        }
        catch(System.ComponentModel.Win32Exception){ }
        Thread.Sleep(4);
    }
    return 0;
}
if(args[0]=="attack-watch")
{
    using var game=new LiveGame(process);
    var watchSeconds=args.Length>1&&int.TryParse(args[1],out var requestedWatchSeconds)?Math.Clamp(requestedWatchSeconds,5,120):120;
    Console.WriteLine($"Watching attack and critical state for {watchSeconds} seconds.");
    var oldAnimation=int.MinValue; nuint oldThrow=nuint.MaxValue,oldEntry=nuint.MaxValue; uint oldId=uint.MaxValue; AttackKind? oldIntent=(AttackKind)99;
    var until=DateTime.UtcNow.AddSeconds(watchSeconds);
    while(DateTime.UtcNow<until&&!process.HasExited)
    {
        try
        {
            if(!game.IsLoaded){Thread.Sleep(20);continue;}
            var animation=game.AnimationId; var throwParam=game.ThrowParam; var intent=game.AttackIntent;
            var hasEntry=game.TryGetActiveWeaponAttack(out var entry,out var id);
            if(animation!=oldAnimation||throwParam!=oldThrow||intent!=oldIntent||(hasEntry&&(entry!=oldEntry||id!=oldId)))
            {
                var geometry=hasEntry?game.ReadAttackGeometry(entry):default;
                var shape=hasEntry?game.ReadAttackShapes(entry):default;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} anim={animation} throw=0x{throwParam:X} critical={game.IsCriticalSequence} intent={intent?.ToString()??"-"} attack={(hasEntry?$"0x{entry:X}/0x{id:X8} geo={geometry.A:F3},{geometry.B:F3},{geometry.C:F3},{geometry.D:F3} sphere=0x{shape.Sphere:X}/r{shape.SphereRadius:F3} capsule=0x{shape.Capsule:X}/r{shape.CapsuleRadius:F3}/a{shape.CapsuleA:F3}/b{shape.CapsuleB:F3}":"none")}");
                oldAnimation=animation; oldThrow=throwParam; oldIntent=intent; oldEntry=entry; oldId=id;
            }
        }
        catch(System.ComponentModel.Win32Exception){ }
        Thread.Sleep(2);
    }
    return 0;
}
if(args[0]=="critical-rate")
{
    using var game=new LiveGame(process);
    if(!game.IsLoaded){Console.Error.WriteLine("No loaded character was found.");return 4;}
    if(!game.PatchActiveWeaponCriticalDamage(1))
    {
        Console.Error.WriteLine($"Critical row resolution failed for equipped weapon {game.ActiveWeapon}.");
        return 9;
    }
    Console.WriteLine($"Equipped weapon {game.ActiveWeapon}: param row {game.CriticalRateParamId}, throwAtkRate {game.CriticalRateOriginal}, live address 0x{game.CriticalRateAddress:X}.");
    return 0;
}
if(args[0]=="profile-state")
{
    using var game=new LiveGame(process);
    if(!game.IsLoaded){Console.Error.WriteLine("No loaded character was found.");return 4;}
    Console.WriteLine($"character=0x{game.Character:X} playerData=0x{game.PlayerGameData:X} classBase=0x{game.ClassBase:X} classData=0x{game.ClassData:X} name={game.CharacterName} weapon={game.ActiveWeapon} class={game.ActiveWeaponClass}");
    return 0;
}
if(args[0]=="camera-test")
{
    using var game=new LiveGame(process);
    var intensity=args.Length>1&&float.TryParse(args[1],System.Globalization.CultureInfo.InvariantCulture,out var parsed)?parsed:2f;
    Console.WriteLine($"Testing zero-centered camera shake at intensity {intensity:F1}. Focus the game window.");
    var focusDeadline=DateTime.UtcNow.AddSeconds(15);
    while(!Keyboard.IsForeground(process.MainWindowHandle)&&DateTime.UtcNow<focusDeadline) Thread.Sleep(25);
    if(!Keyboard.IsForeground(process.MainWindowHandle)){Console.Error.WriteLine("The game did not receive focus; test cancelled.");return 5;}
    var start=DateTime.UtcNow; var until=start.AddSeconds(2);
    var lastX=0; var lastY=0;
    while(DateTime.UtcNow<until&&!process.HasExited)
    {
        var elapsed=(DateTime.UtcNow-start).TotalSeconds; var envelope=(float)Math.Sin(Math.PI*Math.Min(1,elapsed/2)); var phase=(float)(elapsed*Math.PI*2*24);
        var nextX=(int)MathF.Round(MathF.Sin(phase)*3f*intensity*envelope); var nextY=(int)MathF.Round(MathF.Cos(phase*0.71f)*2f*intensity*envelope);
        Keyboard.MouseMove(nextX-lastX,nextY-lastY); lastX=nextX; lastY=nextY;
        Thread.Sleep(8);
    }
    Keyboard.MouseMove(-lastX,-lastY); return 0;
}
if(args[0]=="menu-state")
{
    using var game=new LiveGame(process);
    var menu=game.MenuFingerprint;
    Console.WriteLine($"primary={menu.Primary} secondary=0x{menu.Secondary:X8} page={menu.Page} selector={menu.Selector} blacksmithMain={game.IsBlacksmithMainMenu} reinforce={game.IsBlacksmithReinforceMenu} bonfireMain={game.IsBonfireMainMenu}");
    return 0;
}
if(args[0]=="menu-fingerprint")
{
    using var game=new LiveGame(process);
    Console.WriteLine("Baseline captured. Open the target menu and press F9 within 120 seconds.");
    var baseline=game.ReadMenuManager(0x1800); var deadline=DateTime.UtcNow.AddSeconds(120); var held=true;
    while(DateTime.UtcNow<deadline&&!process.HasExited)
    {
        var down=Keyboard.Down(0x78);
        if(down&&!held)
        {
            var current=game.ReadMenuManager(0x1800);
            for(var offset=0;offset+4<=Math.Min(baseline.Length,current.Length);offset+=4)
            {
                var before=BitConverter.ToUInt32(baseline,offset); var after=BitConverter.ToUInt32(current,offset);
                if(before!=after) Console.WriteLine($"+0x{offset:X4}: 0x{before:X8} -> 0x{after:X8}");
            }
            return 0;
        }
        held=down; Thread.Sleep(8);
    }
    Console.Error.WriteLine("Timed out before F9."); return 6;
}
if(args[0]=="world-references")
{
    using var game=new LiveGame(process); var player=game.Character;
    Console.WriteLine($"Player ChrIns 0x{player:X}");
    foreach(var reference in game.FindPointerReferences(player))
    {
        Console.WriteLine($"reference 0x{reference:X}");
        var nearbyCharacters=new List<(nuint Pointer,int Id,uint Hp,uint MaxHp,float X,float Y,float Z)>();
        var seen=new HashSet<nuint>();
        for(var delta=-0x800;delta<=0x800;delta+=8)
        {
            try
            {
                var candidate=game.DebugPointer((nuint)((nint)reference+delta));
                if(candidate<0x10000||!seen.Add(candidate)) continue;
                var id=game.DebugInt32(candidate+0xCC); var hp=game.DebugUInt32(candidate+0x3E8); var maxHp=game.DebugUInt32(candidate+0x3EC);
                var map=game.DebugPointer(candidate+0x68); var position=map==0?0:game.DebugPointer(map+0x28);
                if(id>=0&&id<100000&&maxHp>0&&maxHp<10000000&&hp<=maxHp&&position!=0)
                    nearbyCharacters.Add((candidate,id,hp,maxHp,game.DebugFloat(position+0x10),game.DebugFloat(position+0x14),game.DebugFloat(position+0x18)));
            }
            catch { }
        }
        if(nearbyCharacters.Count>=2)
        {
            Console.WriteLine("  candidate entity table:");
            foreach(var chr in nearbyCharacters) Console.WriteLine($"    ptr=0x{chr.Pointer:X} id={chr.Id} hp={chr.Hp}/{chr.MaxHp} pos=({chr.X:F1},{chr.Y:F1},{chr.Z:F1})");
        }
        for(var delta=-0x20;delta<=0x20;delta+=8)
        {
            try
            {
                var candidate=game.DebugPointer((nuint)((nint)reference+delta));
                if(candidate<0x10000) continue;
                var id=game.DebugInt32(candidate+0xCC); var hp=game.DebugUInt32(candidate+0x3E8); var maxHp=game.DebugUInt32(candidate+0x3EC);
                if(id>=0&&id<100000000&&maxHp>0&&maxHp<10000000) Console.WriteLine($"  {delta,4}: ptr=0x{candidate:X} id={id} hp={hp}/{maxHp}");
            }
            catch { }
        }
    }
    return 0;
}
using var memory=new ProcessMemory(process);
if(args[0]=="state-watch")
{
    var main=process.MainModule ?? throw new InvalidOperationException("No main module.");
    var module=(nuint)main.BaseAddress; var moduleSize=main.ModuleMemorySize;
    const string worldPattern="48 8B 05 ? ? ? ? 48 8B 48 68 48 85 C9 0F 84 ? ? ? ? 48 39 5E 10 0F 84 ? ? ? ? 48";
    var instruction=memory.Scan(worldPattern,module,moduleSize).Single();
    var worldGlobal=(nuint)((nint)instruction+7+memory.Read<int>(instruction+3));
    nuint oldCharacter=0; var oldAnimation=int.MinValue; var oldUpper=int.MinValue; var oldLower=int.MinValue; uint oldHp=0,oldStamina=0;
    Console.WriteLine("Watching character/animation state for 45 seconds. Rest at a bonfire once.");
    var until=DateTime.UtcNow.AddSeconds(45);
    while(DateTime.UtcNow<until)
    {
        try
        {
            var world=memory.Read<nuint>(worldGlobal); var character=world==0?0:memory.Read<nuint>(world+0x68);
            if(character!=oldCharacter){Console.WriteLine($"character 0x{oldCharacter:X} -> 0x{character:X}");oldCharacter=character;oldAnimation=oldUpper=oldLower=int.MinValue;}
            if(character!=0)
            {
                var root=memory.Read<nuint>(character+0x68); var current=root==0?0:memory.Read<nuint>(root+0x48);
                var animation=current==0?-1:memory.Read<int>(current+0x80);
                var hp=memory.Read<uint>(character+0x3E8); var stamina=memory.Read<uint>(character+0x3F8);
                if(animation!=oldAnimation){Console.WriteLine($"animation {oldAnimation} -> {animation}, hp {hp}, stamina {stamina}");oldAnimation=animation;}
                var stayRoot=memory.Read<nuint>(character+0x30); var stay=stayRoot==0?0:memory.Read<nuint>(stayRoot+0x5D0);
                if(stay!=0)
                {
                    var upper=memory.Read<int>(stay+0x690); var lower=memory.Read<int>(stay+0x13B0);
                    if(upper!=oldUpper||lower!=oldLower){Console.WriteLine($"stay upper {oldUpper} -> {upper}, lower {oldLower} -> {lower}");oldUpper=upper;oldLower=lower;}
                }
                if(hp!=oldHp||stamina!=oldStamina){oldHp=hp;oldStamina=stamina;}
            }
        }
        catch(System.ComponentModel.Win32Exception){ }
        Thread.Sleep(5);
    }
    return 0;
}
if(args[0]=="damage-watch")
{
    var module=(nuint)(process.MainModule?.BaseAddress ?? throw new InvalidOperationException("No main module."));
    var moduleSize=process.MainModule!.ModuleMemorySize;
    const string worldPattern="48 8B 05 ? ? ? ? 48 8B 48 68 48 85 C9 0F 84 ? ? ? ? 48 39 5E 10 0F 84 ? ? ? ? 48";
    var worldInstruction=memory.Scan(worldPattern,module,moduleSize).Single();
    var displacement=memory.Read<int>(worldInstruction+3);
    var worldGlobal=(nuint)((nint)worldInstruction+7+displacement);
    var manager=memory.Read<nuint>(module+0x1C7A050); nuint previous=0;
    Console.WriteLine("Watching player weapon damage entries for 30 seconds. Attack several times.");
    var until=DateTime.UtcNow.AddSeconds(30);
    while(DateTime.UtcNow<until)
    {
        var active=memory.Read<nuint>(manager);
        var world=memory.Read<nuint>(worldGlobal); var character=world==0?0:memory.Read<nuint>(world+0x68);
        var weapon=character==0?0:memory.Read<uint>(character+0x1E34);
        if(active!=0 && active!=previous && memory.Read<uint>(active+0x8C)==weapon)
        {
            Console.WriteLine($"DamageEntry 0x{active:X}, id 0x{memory.Read<uint>(active):X8}, weapon {weapon}");
            var bytes=memory.Read(active+0x40,0xE0);
            for(var i=0;i<bytes.Length;i+=16) Console.WriteLine($"+{0x40+i:X3}: {string.Join(' ',bytes.Skip(i).Take(16).Select(b=>$"{b:X2}"))}");
        }
        previous=active; Thread.Sleep(2);
    }
    return 0;
}
if(args.Length<2) { Console.Error.WriteLine("The scan/dump command needs an argument."); return 2; }
Console.WriteLine($"Attached read-only to PID {process.Id}.");
if(args[0]=="dump") {
    var raw=args[1].StartsWith("0x",StringComparison.OrdinalIgnoreCase)?args[1][2..]:args[1];
    var address=nuint.Parse(raw,System.Globalization.NumberStyles.HexNumber); var length=args.Length>2?int.Parse(args[2]):128;
    var bytes=memory.Read(address,length); for(var i=0;i<bytes.Length;i+=16){ var row=bytes.Skip(i).Take(16).ToArray(); Console.WriteLine($"{address+(nuint)i:X}: {string.Join(' ',row.Select(b=>$"{b:X2}"))}"); } return 0;
}
var count=0; foreach(var address in memory.Scan(args[1])) { Console.WriteLine($"0x{address:X}"); count++; if(count>=100){Console.WriteLine("Result limit reached.");break;} }
Console.WriteLine($"Matches: {count}"); return 0;

static class Keyboard
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags,uint dx,uint dy,uint data,nuint extraInfo);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    internal static bool Down(int key)=>(GetAsyncKeyState(key)&0x8000)!=0;
    internal static bool IsForeground(nint window)=>window!=0&&GetForegroundWindow()==window;
    internal static void MouseMove(int x,int y)
    {
        if(x!=0||y!=0) mouse_event(0x0001,unchecked((uint)x),unchecked((uint)y),0,0);
    }
}
