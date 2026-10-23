using System.Runtime.InteropServices;

namespace Clyppey.Core.Native;

public sealed record DxgiOutput(int Index, int Left, int Top, int Right, int Bottom, bool Attached)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

/// <summary>
/// Enumerates DXGI outputs in the same order ffmpeg's ddagrab uses for "output_idx".
/// Without this the monitor picker would be guessing: Windows' display enumeration order is
/// not the DXGI adapter output order.
/// </summary>
public static class DxgiOutputs
{
    public static IReadOnlyList<DxgiOutput> Enumerate()
    {
        var results = new List<DxgiOutput>();
        IntPtr factoryPtr = IntPtr.Zero;

        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IID_IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factoryPtr) < 0 || factoryPtr == IntPtr.Zero)
            {
                return results;
            }

            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                for (uint adapterIndex = 0; adapterIndex < 16; adapterIndex++)
                {
                    IDXGIAdapter1? adapter;
                    try
                    {
                        factory.EnumAdapters1(adapterIndex, out adapter);
                    }
                    catch (Exception)
                    {
                        break; // DXGI_ERROR_NOT_FOUND
                    }

                    if (adapter is null)
                    {
                        break;
                    }

                    try
                    {
                        EnumerateAdapterOutputs(adapter, results);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"DXGI 出力の列挙に失敗 (GDI にフォールバックします): {ex.Message}");
            return new List<DxgiOutput>();
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }
        }

        return results;
    }

    private static void EnumerateAdapterOutputs(IDXGIAdapter1 adapter, List<DxgiOutput> results)
    {
        for (uint outputIndex = 0; outputIndex < 16; outputIndex++)
        {
            IDXGIOutput? output;
            try
            {
                adapter.EnumOutputs(outputIndex, out output);
            }
            catch (Exception)
            {
                return; // DXGI_ERROR_NOT_FOUND
            }

            if (output is null)
            {
                return;
            }

            try
            {
                var size = Marshal.SizeOf<DxgiOutputDesc>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (output.GetDesc(buffer) < 0)
                    {
                        continue;
                    }

                    var desc = Marshal.PtrToStructure<DxgiOutputDesc>(buffer);
                    results.Add(new DxgiOutput(
                        results.Count,
                        desc.DesktopCoordinates.Left,
                        desc.DesktopCoordinates.Top,
                        desc.DesktopCoordinates.Right,
                        desc.DesktopCoordinates.Bottom,
                        desc.AttachedToDesktop != 0));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(output);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiOutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public NativeRect DesktopCoordinates;

        public int AttachedToDesktop;

        public int Rotation;

        public IntPtr Monitor;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // Method order below must match the COM vtables exactly; the CLR binds by slot.

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

        void SetPrivateDataInterface(ref Guid name, IntPtr data);

        void GetPrivateData(ref Guid name, out uint dataSize, IntPtr data);

        void GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIFactory
        void EnumAdapters(uint index, out IntPtr adapter);

        void MakeWindowAssociation(IntPtr windowHandle, uint flags);

        void GetWindowAssociation(out IntPtr windowHandle);

        void CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);

        void CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);

        // IDXGIFactory1
        void EnumAdapters1(uint index, out IDXGIAdapter1 adapter);

        [PreserveSig]
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

        void SetPrivateDataInterface(ref Guid name, IntPtr data);

        void GetPrivateData(ref Guid name, out uint dataSize, IntPtr data);

        void GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIAdapter
        void EnumOutputs(uint index, out IDXGIOutput output);

        void GetDesc(IntPtr desc);

        void CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);

        // IDXGIAdapter1
        void GetDesc1(IntPtr desc);
    }

    [ComImport]
    [Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput
    {
        // IDXGIObject
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

        void SetPrivateDataInterface(ref Guid name, IntPtr data);

        void GetPrivateData(ref Guid name, out uint dataSize, IntPtr data);

        void GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIOutput
        [PreserveSig]
        int GetDesc(IntPtr desc);
    }
}
