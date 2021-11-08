﻿using DotNetDetour;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

public static unsafe class HookUtils
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void DelegateFlushICache(void* code, int size); // delegate * unmanaged[Cdecl] <void, byte, uint> native_flush_cache_fun_ptr; // unsupported at C# 8.0

    static DelegateFlushICache flush_icache;
    private static readonly long _Pagesize;

    public static void MemCpy(void* pDst, void* pSrc, int len)
    {
        byte* pDst_ = (byte*)pDst;
        byte* pSrc_ = (byte*)pSrc;

        for (int i = 0; i < len; i++)
            *pDst_++ = *pSrc_++;
    }

    const int PRINT_SPLIT = 4;
    const int PRINT_COL_SIZE = PRINT_SPLIT * 4;
    public static string HexToString(void* ptr, int size, int offset = 0)
    {
        byte* addr = (byte*)ptr;
        
        StringBuilder sb = new StringBuilder(1024);
        sb.AppendLine($"addr:0x{(long)addr:x}");
        
        addr += offset;
        size += Math.Abs(offset);

        int count = 0;
        while(true)
        {
            sb.Append($"\r\n0x{(long)(addr + count):x}: ");
            for(int i = 1;i < PRINT_COL_SIZE + 1; i++)
            {
                if (count >= size)
                    goto END;

                sb.Append($"{*(addr + count):x2}");
                if (i % PRINT_SPLIT == 0)
                    sb.Append(" ");

                count++;
            }
        }
    END:;
        return sb.ToString();
    }

    /// <summary>
    /// set flags of address to `read write execute`
    /// </summary>
    public static void SetAddrFlagsToRWE(IntPtr ptr, int size)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR

        uint oldProtect;
        bool ret = VirtualProtect(ptr, (uint)size, Protection.PAGE_EXECUTE_READWRITE, out oldProtect);
        UnityEngine.Debug.Assert(ret);

#elif UNITY_ANDROID

    SetMemPerms(ptr,(ulong)size,MmapProts.PROT_READ | MmapProts.PROT_WRITE | MmapProts.PROT_EXEC);

#endif
    }

    public static void FlushICache(void* code, int size)
    {
        flush_icache?.Invoke(code, size);

#if ENABLE_HOOK_DEBUG
        Debug.Log($"flush icache at 0x{(ulong)code:X}, size:{size}");
#endif
    }

    static KeyValuePair<long, long> GetPageAlignedAddr(long code, int size)
    {
        long pagesize = _Pagesize;
        long startPage = (code) & ~(pagesize - 1);
        long endPage = (code + size + pagesize - 1) & ~(pagesize - 1);
        return new KeyValuePair<long, long>(startPage, endPage);
    }



#if UNITY_STANDALONE_WIN || UNITY_EDITOR
    [Flags]
    public enum Protection
    {
        PAGE_NOACCESS           = 0x01,
        PAGE_READONLY           = 0x02,
        PAGE_READWRITE          = 0x04,
        PAGE_WRITECOPY          = 0x08,
        PAGE_EXECUTE            = 0x10,
        PAGE_EXECUTE_READ       = 0x20,
        PAGE_EXECUTE_READWRITE  = 0x40,
        PAGE_EXECUTE_WRITECOPY  = 0x80,
        PAGE_GUARD              = 0x100,
        PAGE_NOCACHE            = 0x200,
        PAGE_WRITECOMBINE       = 0x400
    }

    [DllImport("kernel32")]
    public static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, Protection flNewProtect, out uint lpflOldProtect);

    static HookUtils()
    {
        PropertyInfo p_SystemPageSize = typeof(Environment).GetProperty("SystemPageSize");
        if (p_SystemPageSize == null)
            throw new NotSupportedException("Unsupported runtime");
        _Pagesize = (int) p_SystemPageSize.GetValue(null, new object[0]);
    }

#elif UNITY_ANDROID
    [Flags]
    public enum MmapProts : int {
        PROT_READ       = 0x1,
        PROT_WRITE      = 0x2,
        PROT_EXEC       = 0x4,
        PROT_NONE       = 0x0,
        PROT_GROWSDOWN  = 0x01000000,
        PROT_GROWSUP    = 0x02000000,
    }

    static HookUtils()
    {
        PropertyInfo p_SystemPageSize = typeof(Environment).GetProperty("SystemPageSize");
        if (p_SystemPageSize == null)
            throw new NotSupportedException("Unsupported runtime");
        _Pagesize = (int) p_SystemPageSize.GetValue(null, new object[0]);

        if(IntPtr.Size == 4)
        {
            // never release, so save GCHandle is unnecessary
            s_ptr_flush_icache_arm32 = GCHandle.Alloc(s_flush_icache_arm32, GCHandleType.Pinned).AddrOfPinnedObject().ToPointer();
            SetAddrFlagsToRWE(new IntPtr(s_ptr_flush_icache_arm32), s_flush_icache_arm32.Length);
            flush_icache = Marshal.GetDelegateForFunctionPointer<DelegateFlushICache>(new IntPtr(s_ptr_flush_icache_arm32));
        }
        else
        {
            s_ptr_flush_icache_arm64 = GCHandle.Alloc(s_flush_icache_arm64, GCHandleType.Pinned).AddrOfPinnedObject().ToPointer();
            SetAddrFlagsToRWE(new IntPtr(s_ptr_flush_icache_arm64), s_flush_icache_arm64.Length);
            flush_icache = Marshal.GetDelegateForFunctionPointer<DelegateFlushICache>(new IntPtr(s_ptr_flush_icache_arm64));
        }

        Debug.Log($"flush_icache is null {flush_icache == null}");
    }
    [DllImport("libc", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mprotect(IntPtr start, IntPtr len, MmapProts prot);
    
    public static unsafe void SetMemPerms(IntPtr start, ulong len, MmapProts prot) {
        var requiredAddr = GetPageAlignedAddr(start.ToInt64(), (int)len);
        long startPage = requiredAddr.Key;
        long endPage = requiredAddr.Value;

        if (mprotect((IntPtr) startPage, (IntPtr) (endPage - startPage), prot) != 0)
            throw new Win32Exception();
    }


    static void* s_ptr_flush_icache_arm32, s_ptr_flush_icache_arm64;
    private static byte[] s_flush_icache_arm32 = new byte[]
    {
        // void cdecl mono_arch_flush_icache (guint8 *code, gint size)
        0x00, 0x48, 0x2D, 0xE9,                             // PUSH            {R11,LR}
        0x0D, 0xB0, 0xA0, 0xE1,                             // MOV             R11, SP
        0x08, 0xD0, 0x4D, 0xE2,                             // SUB             SP, SP, #8
        0x04, 0x00, 0x8D, 0xE5,                             // STR             R0, [SP,#8+var_4]
        0x00, 0x10, 0x8D, 0xE5,                             // STR             R1, [SP,#8+var_8]
        0x04, 0x00, 0x9D, 0xE5,                             // LDR             R0, [SP,#8+var_4]
        0x04, 0x10, 0x9D, 0xE5,                             // LDR             R1, [SP,#8+var_4]
        0x00, 0x20, 0x9D, 0xE5,                             // LDR             R2, [SP,#8+var_8]
        0x02, 0x10, 0x81, 0xE0,                             // ADD             R1, R1, R2
        0x98, 0xFF, 0xFF, 0xEB,                             // BL              j___clear_cache
        0x0B, 0xD0, 0xA0, 0xE1,                             // MOV             SP, R11
        0x00, 0x88, 0xBD, 0xE8
    };
    private static byte[] s_flush_icache_arm64 = new byte[] // X0: code, W1: size
    {
        // void cdecl mono_arch_flush_icache (guint8 *code, gint size)
        0xFF, 0xC3, 0x00, 0xD1,                             // SUB             SP, SP, #0x30
        0xE8, 0x03, 0x7E, 0xB2,                             // MOV             X8, #4
        0xE0, 0x17, 0x00, 0xF9,                             // STR             X0, [SP,#0x30+var_8]
        0xE1, 0x27, 0x00, 0xB9,                             // STR             W1, [SP,#0x30+var_C]
        0xE0, 0x17, 0x40, 0xF9,                             // LDR             X0, [SP,#0x30+var_8]
        0xE9, 0x27, 0x80, 0xB9,                             // LDRSW           X9, [SP,#0x30+var_C]
        0x09, 0x00, 0x09, 0x8B,                             // ADD             X9, X0, X9
        0xE9, 0x0F, 0x00, 0xF9,                             // STR             X9, [SP,#0x30+var_18]
        0xE8, 0x07, 0x00, 0xF9,                             // STR             X8, [SP,#0x30+var_28]
        0xE8, 0x03, 0x00, 0xF9,                             // STR             X8, [SP,#0x30+var_30]
        0xE8, 0x17, 0x40, 0xF9,                             // LDR             X8, [SP,#0x30+var_8]
        0x08, 0xF5, 0x7E, 0x92,                             // AND             X8, X8, #0xFFFFFFFFFFFFFFFC
        0xE8, 0x0B, 0x00, 0xF9,                             // STR             X8, [SP,#0x30+var_20]

        //                              loc_590                                 ; CODE XREF: mono_arch_flush_icache(uchar*, int)+58↓j
        0xE8, 0x0B, 0x40, 0xF9,                             // LDR             X8, [SP,#0x30+var_20]
        0xE9, 0x0F, 0x40, 0xF9,                             // LDR             X9, [SP,#0x30+var_18]
        0x1F, 0x01, 0x09, 0xEB,                             // CMP             X8, X9
        0xE2, 0x00, 0x00, 0x54,                             // B.CS            loc_5B8
        0xE8, 0x0B, 0x40, 0xF9,                             // LDR             X8, [SP,#0x30+var_20]
        0x28, 0x7E, 0x0B, 0xD5,                             // SYS             #3, c7, c14, #1, X8
        0xE8, 0x0B, 0x40, 0xF9,                             // LDR             X8, [SP,#0x30+var_20]
        0x08, 0x11, 0x00, 0x91,                             // ADD             X8, X8, #4
        0xE8, 0x0B, 0x00, 0xF9,                             // STR             X8, [SP,#0x30+var_20]
        0xF7, 0xFF, 0xFF, 0x17,                             // B               loc_590
        //                              ; ---------------------------------------------------------------------------

        //                              loc_5B8                                 ; CODE XREF: mono_arch_flush_icache(uchar *, int)+40↑j
        0x9F, 0x3B, 0x03, 0xD5,                             // DSB             ISH
        0xE8, 0x17, 0x40, 0xF9,                             // LDR             X8, [SP,#0x30+var_8]
        0x08, 0xF5, 0x7E, 0x92,                             // AND             X8, X8, #0xFFFFFFFFFFFFFFFC
        0xE8, 0x0B, 0x00, 0xF9,                             // STR             X8, [SP,#0x30+var_20]

        //                              loc_5C8                                 ; CODE XREF: mono_arch_flush_icache(uchar *, int)+90↓j
        0xE8, 0x0B, 0x40, 0xF9,                             // LDR             X8, [SP,#0x30+var_20]
        0xE9, 0x0F, 0x40, 0xF9,                             // LDR             X9, [SP,#0x30+var_18]
        0x1F, 0x01, 0x09, 0xEB,                             // CMP             X8, X9
        0xE2, 0x00, 0x00, 0x54,                             // B.CS            loc_5F0
        0xE8, 0x0B, 0x40, 0xF9,                             // LDR             X8, [SP,#0x30+var_20]
        0x28, 0x75, 0x0B, 0xD5,                             // SYS             #3, c7, c5, #1, X8
        0xE8, 0x0B, 0x40, 0xF9,                             // LDR             X8, [SP,#0x30+var_20]
        0x08, 0x11, 0x00, 0x91,                             // ADD             X8, X8, #4
        0xE8, 0x0B, 0x00, 0xF9,                             // STR             X8, [SP,#0x30+var_20]
        0xF7, 0xFF, 0xFF, 0x17,                             // B               loc_5C8
        //                               ; ---------------------------------------------------------------------------

        //                               loc_5F0                                 ; CODE XREF: mono_arch_flush_icache(uchar *, int)+78↑j
        0x9F, 0x3B, 0x03, 0xD5,                             // DSB             ISH
        0xDF, 0x3F, 0x03, 0xD5,                             // ISB
        0xFF, 0xC3, 0x00, 0x91,                             // ADD             SP, SP, #0x30 ; '0'
        0xC0, 0x03, 0x5F, 0xD6,                             // RET
    };

#endif

}


