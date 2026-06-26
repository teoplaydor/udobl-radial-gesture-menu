using System;
using System.Runtime.InteropServices;

namespace Udobl.Render
{
    // Hand-rolled DirectComposition interop (SharpDX has no DComp binding). GUIDs are the dcomp.h values.
    // We only ever call: DCompositionCreateDevice, IDCompositionDevice.{CreateTargetForHwnd,CreateVisual,Commit},
    // IDCompositionTarget.SetRoot, IDCompositionVisual.SetContent. No overloaded methods are invoked; the
    // overlay is positioned by moving the HWND (SetWindowPos), never via SetOffset/SetTransform.
    internal static class DCompIids
    {
        public static readonly Guid IDCompositionDevice = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    }

    [ComImport, Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDCompositionDevice
    {
        [PreserveSig] int Commit();                                       // 0
        [PreserveSig] int WaitForCommitCompletion();                      // 1
        [PreserveSig] int GetFrameStatistics(IntPtr statistics);          // 2 (unused)
        [PreserveSig] int CreateTargetForHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IDCompositionTarget target); // 3
        [PreserveSig] int CreateVisual(out IDCompositionVisual visual);   // 4
        // remaining methods (CreateSurface, ...) omitted — never called.
    }

    [ComImport, Guid("eacdd04c-117e-4e17-88f4-d1b12b0e3d89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDCompositionTarget
    {
        [PreserveSig] int SetRoot(IDCompositionVisual visual);            // 0
    }

    // SetContent is vtable slot 12 (after 4 overloaded setter PAIRS = 8, + SetTransformParent, SetEffect,
    // SetBitmapInterpolationMode, SetBorderMode = 4). The 12 preceding slots are declared as stubs ONLY to
    // place SetContent at the right offset; none are ever called, so the disputed intra-pair order is moot.
    [ComImport, Guid("4d93059d-097b-4651-9a60-f0f25116e2f3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDCompositionVisual
    {
        [PreserveSig] int _s0(IntPtr p);
        [PreserveSig] int _s1(IntPtr p);
        [PreserveSig] int _s2(IntPtr p);
        [PreserveSig] int _s3(IntPtr p);
        [PreserveSig] int _s4(IntPtr p);
        [PreserveSig] int _s5(IntPtr p);
        [PreserveSig] int _s6(IntPtr p);
        [PreserveSig] int _s7(IntPtr p);
        [PreserveSig] int _s8(int m);
        [PreserveSig] int _s9(int m);
        [PreserveSig] int _s10(IntPtr p);
        [PreserveSig] int _s11(IntPtr p);
        [PreserveSig] int SetContent(IntPtr content);                     // 12
    }
}
