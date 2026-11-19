using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FmPlayerTalk;

internal static partial class FmNativeAffinity
{
    // Verified Windows x64 ABI: RCX points to embedded Person + 0xA0;
    // RAX returns the existing or fully constructed 0x50-byte holder.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr EnsureRelationsDelegate(IntPtr personSubobject);

    private static RelationList InitializeRelations(RelationList original)
    {
        var profile = FmNativeLayout.Current;
        if (profile.RelationsInitializer is not { } initializer ||
            profile.RelationsConstructor is not { } constructor)
            throw new NativeAffinityException("此 FM 版本尚未验证创建空人物关系列表，亲密度未写入。");
        var module = FmNativePromises.ModuleBase();
        if (module == 0 || FmNativeLayout.Current != profile)
            throw new NativeAffinityException(Unsupported);
        VerifyRoutine(module, initializer);
        VerifyRoutine(module, constructor);
        CheckUnchanged(original);

        var ensure = Marshal.GetDelegateForFunctionPointer<EnsureRelationsDelegate>(
            new IntPtr(checked((long)(module + initializer.Rva))));
        var holder = unchecked((ulong)ensure(new IntPtr(checked((long)(original.Owner.Address + 0xA0)))).ToInt64());
        try
        {
            if (!IsCurrent(original.Manager) || !IsCurrent(original.Owner) || !IsAddress(holder))
                throw new NativeAffinityException(Stale);
            var initialized = ReadRelations(original.Manager, original.Owner);
            // All three native vectors must be empty. The remaining fields are
            // constructor defaults; a zeroed allocation would not be valid.
            var fields = ReadBytes(holder, 0x4D);
            if (initialized.Address != holder || initialized.Affinity != original.Affinity ||
                !fields.AsSpan(0, 0x48).SequenceEqual(new byte[0x48]) ||
                BitConverter.ToUInt32(fields, 0x48) != 0x1FFFF || fields[0x4C] != 0)
                throw new NativeAffinityException(Unreadable);
            return initialized;
        }
        catch (NativeAffinityException)
        {
            // FM has already been called. Do not guess whether a newly owned
            // object can be detached or free it with the CRT allocator.
            throw new NativeAffinityException("未能核实 FM 新建的人物关系列表，请载入修改前的存档。", outcomeUncertain: true);
        }
    }

    private static void VerifyRoutine(ulong module, FmNativeRoutine routine)
    {
        var bytes = ReadBytes(module + routine.Rva, routine.Length);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), routine.Sha256, StringComparison.Ordinal))
            throw new NativeAffinityException("FM 人物关系创建代码与已验证版本不一致，亲密度未写入。");
    }
}
