using System.Runtime.InteropServices;

namespace HeavierByTheKill.Controller;

[StructLayout(LayoutKind.Sequential)] internal readonly record struct WeaponKey(uint InventoryId,ushort ReinforceLevel,ushort Infusion);
[StructLayout(LayoutKind.Sequential)] internal readonly record struct Modifiers(float Weight,float Damage,float AttackSpeed,float StaminaCost,float Recovery,float Impact,float Knockback);
[StructLayout(LayoutKind.Sequential)] internal readonly record struct WeaponProgress(float Temporary,float Permanent,uint Kills,uint Bosses,uint Legacies);
[StructLayout(LayoutKind.Sequential)] internal readonly record struct ForgeReceipt(float ConvertedWeight,uint SoulCost);
[StructLayout(LayoutKind.Sequential)] internal readonly record struct AttackOutcome(Modifiers Modifiers,float StaminaAfter,float ExhaustionSeconds,float RadialStagger,float PresentationIntensity);
[StructLayout(LayoutKind.Sequential)] internal readonly record struct BalanceConfig(
    float WeightPerKill,float BossWeight,float BossPermanentFraction,float RestDecayFraction,
    float BaseDamageMultiplier,float BaseAttackSpeedMultiplier,float DamagePerWeight,
    float DamageAccelerationPerWeightSquared,float SpeedLossPerWeight,float StaminaPerWeight,
    float RecoveryPerWeight,float ImpactPerWeight,
    float KnockbackStartsAt,float KnockbackPerWeight,float RadialStaggerStartsAt,
    float RadialRadiusPerWeight,float CameraShakeStartsAt,float TierTemperedAt,
    float TierBurdenedAt,float TierCrushingAt,float TierDevastatingAt,
    float TierWorldbreakerAt,float TierCataclysmicAt,
    float StandardStaminaPenaltyMultiplier,float MinimumAttackSpeedMultiplier,
    float MaximumStaminaCostMultiplier)
{
    internal string TierFor(float weight)=>weight switch
    {
        _ when weight<TierTemperedAt=>"Light",
        _ when weight<TierBurdenedAt=>"Tempered",
        _ when weight<TierCrushingAt=>"Burdened",
        _ when weight<TierDevastatingAt=>"Crushing",
        _ when weight<TierWorldbreakerAt=>"Devastating",
        _ when weight<TierCataclysmicAt=>"Worldbreaker",
        _=>"Cataclysmic"
    };
}
internal enum WeaponClass:uint { Dagger=0,Light=1,Standard=2,Heavy=3,Colossal=4 }
internal enum AttackKind:uint { Quick=0,Running=1,Heavy=2,Critical=3 }

internal static class ReforgeRules
{
    internal const float MaxWeight=20f;
    internal const uint SoulsPerWeight=1000;
    internal static float BatchWeight(float temporary)=>Math.Min(MaxWeight,Math.Max(0,temporary));
    internal static uint Cost(float weight)=>(uint)Math.Ceiling(weight*SoulsPerWeight);
}

internal static class CoreNative
{
    const string Library="heavier_by_the_kill.dll";
    [DllImport(Library,EntryPoint="HBK_initialize")] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool Initialize();
    [DllImport(Library,EntryPoint="HBK_select_profile")]
    [return:MarshalAs(UnmanagedType.I1)]
    internal static extern bool SelectProfile([MarshalAs(UnmanagedType.LPUTF8Str)] string characterName);
    [DllImport(Library,EntryPoint="HBK_modifiers")] internal static extern Modifiers GetModifiers(WeaponKey key);
    [DllImport(Library,EntryPoint="HBK_modifiers_for")] internal static extern Modifiers GetModifiersFor(WeaponKey key,WeaponClass weaponClass);
    [DllImport(Library,EntryPoint="HBK_progress")] internal static extern WeaponProgress GetProgress(WeaponKey key);
    [DllImport(Library,EntryPoint="HBK_config")] internal static extern BalanceConfig GetConfig();
    [DllImport(Library,EntryPoint="HBK_on_kill")] internal static extern void OnKill(WeaponKey key,[MarshalAs(UnmanagedType.I1)] bool boss);
    [DllImport(Library,EntryPoint="HBK_on_kill_ex")] internal static extern void OnKillEx(WeaponKey key,[MarshalAs(UnmanagedType.I1)] bool boss,WeaponClass weaponClass,uint legacy);
    [DllImport(Library,EntryPoint="HBK_attack")] internal static extern AttackOutcome Attack(WeaponKey key,WeaponClass weaponClass,AttackKind attackKind,float baseStamina,float currentStamina);
    [DllImport(Library,EntryPoint="HBK_on_rest")] internal static extern void OnRest();
    [DllImport(Library,EntryPoint="HBK_undo_rest")] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool UndoRest();
    [DllImport(Library,EntryPoint="HBK_on_death")] internal static extern void OnDeath(float lossFraction);
    [DllImport(Library,EntryPoint="HBK_on_bloodstain_recovered")] internal static extern void OnBloodstainRecovered();
    [DllImport(Library,EntryPoint="HBK_forge")] internal static extern ForgeReceipt Forge(WeaponKey key,float requested,uint availableSouls);
    [DllImport(Library,EntryPoint="HBK_save")] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool Save();
}
