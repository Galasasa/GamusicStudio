using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace MidiKey;

internal static class SharedAudioOutput
{
    private const uint EventCallback = 0x00040000;
    private const uint Silent = 2;
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointEnumerator
    {
        void EnumAudioEndpoints(int flow, uint mask, out nint devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IEndpoint endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpoint
    {
        void Activate(ref Guid iid, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    }

    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3
    {
        // The inherited IAudioClient and IAudioClient2 methods must precede the IAudioClient3 slots.
        void Initialize(int shareMode, uint flags, long duration, long period, nint format, nint session);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        void IsFormatSupported(int shareMode, nint format, out nint closest);
        void GetMixFormat(out nint format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(nint handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        void IsOffloadCapable(int category, out int capable);
        void SetClientProperties(nint properties);
        void GetBufferSizeLimits(nint format, int eventDriven, out long minimum, out long maximum);
        void GetSharedModeEnginePeriod(nint format, out uint normal, out uint fundamental, out uint minimum, out uint maximum);
        void GetCurrentSharedModeEnginePeriod(out nint format, out uint frames);
        void InitializeSharedAudioStream(uint flags, uint frames, nint format, nint session);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out nint data);
        void ReleaseBuffer(uint frames, uint flags);
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics(nint task);

    internal static void Run(WaitHandle stop, Action<float[], int, int> mix)
    {
        using var audioReady = new AutoResetEvent(false);
        var sources = new[] { stop, audioReady };
        var type = Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"))!;
        var enumerator = (IEndpointEnumerator)Activator.CreateInstance(type)!;
        IEndpoint? endpoint = null;
        IAudioClient3? client = null;
        IAudioRenderClient? renderer = null;
        nint format = 0, priority = 0;
        bool started = false;
        try
        {
            enumerator.GetDefaultAudioEndpoint(0, 0, out endpoint);
            client = Activate(endpoint);
            client.GetMixFormat(out format);
            int channels = (ushort)Marshal.ReadInt16(format, 2);
            int rate = Marshal.ReadInt32(format, 4);
            int bits = (ushort)Marshal.ReadInt16(format, 14);
            int tag = (ushort)Marshal.ReadInt16(format);
            Guid subFormat = tag == 0xfffe && Marshal.ReadInt16(format, 16) >= 22
                ? Marshal.PtrToStructure<Guid>(format + 24) : Guid.Empty;
            bool floatingPoint = (tag == 3 || subFormat == FloatSubFormat) && bits == 32;
            bool integer = (tag == 1 || subFormat == PcmSubFormat) && bits is 16 or 24 or 32;
            if (channels == 0 || rate <= 0 || (!floatingPoint && !integer))
                throw new InvalidOperationException("系统播放设备使用了不支持的音频格式");

            try
            {
                client.GetSharedModeEnginePeriod(format, out uint normal, out uint fundamental, out uint minimum, out _);
                uint preferred = (uint)(rate / 200);
                uint frames = Math.Clamp((preferred + fundamental - 1) / fundamental * fundamental, minimum, normal);
                client.InitializeSharedAudioStream(EventCallback, frames, format, 0);
            }
            catch (COMException)
            {
                Marshal.FinalReleaseComObject(client);
                client = null;
                client = Activate(endpoint);
                client.Initialize(0, EventCallback, 0, 0, format, 0);
            }

            client.GetBufferSize(out uint capacity);
            client.SetEventHandle(audioReady.SafeWaitHandle.DangerousGetHandle());
            Guid renderIid = typeof(IAudioRenderClient).GUID;
            client.GetService(ref renderIid, out var renderService);
            renderer = (IAudioRenderClient)renderService;
            var mono = new float[capacity];
            var floats = floatingPoint ? new float[checked((int)capacity * channels)] : Array.Empty<float>();
            var pcm = integer ? new byte[checked((int)capacity * channels * (bits / 8))] : Array.Empty<byte>();

            renderer.GetBuffer(capacity, out _);
            renderer.ReleaseBuffer(capacity, Silent);
            uint taskIndex = 0;
            priority = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            client.Start();
            started = true;
            while (WaitHandle.WaitAny(sources) == 1)
            {
                client.GetCurrentPadding(out uint padding);
                uint available = capacity - padding;
                if (available == 0) continue;
                int frames = (int)available;
                mix(mono, frames, rate);
                renderer.GetBuffer(available, out nint target);
                if (floatingPoint)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        floats[i * channels] = mono[i];
                        if (channels > 1) floats[i * channels + 1] = mono[i];
                    }
                    Marshal.Copy(floats, 0, target, frames * channels);
                }
                else
                {
                    WritePcm(mono, frames, channels, bits, pcm);
                    Marshal.Copy(pcm, 0, target, frames * channels * (bits / 8));
                }
                renderer.ReleaseBuffer(available, 0);
            }
        }
        finally
        {
            if (started)
            {
                try { client!.Stop(); }
                catch (COMException) { }
            }
            if (priority != 0) AvRevertMmThreadCharacteristics(priority);
            if (renderer is not null) Marshal.FinalReleaseComObject(renderer);
            if (format != 0) Marshal.FreeCoTaskMem(format);
            if (client is not null) Marshal.FinalReleaseComObject(client);
            if (endpoint is not null) Marshal.FinalReleaseComObject(endpoint);
            Marshal.FinalReleaseComObject(enumerator);
        }
    }

    private static IAudioClient3 Activate(IEndpoint endpoint)
    {
        Guid iid = typeof(IAudioClient3).GUID;
        endpoint.Activate(ref iid, 23, 0, out var client);
        return (IAudioClient3)client;
    }

    private static void WritePcm(float[] mono, int frames, int channels, int bits, byte[] buffer)
    {
        int bytes = bits / 8;
        for (int i = 0; i < frames; i++)
        {
            int value = (int)(mono[i] * int.MaxValue * 0.999999);
            Span<byte> frame = buffer.AsSpan(i * channels * bytes, channels * bytes);
            for (int ch = 0; ch < Math.Min(channels, 2); ch++)
            {
                var sample = frame.Slice(ch * bytes, bytes);
                if (bits == 16) BinaryPrimitives.WriteInt16LittleEndian(sample, (short)(value >> 16));
                else if (bits == 32) BinaryPrimitives.WriteInt32LittleEndian(sample, value);
                else
                {
                    sample[0] = (byte)(value >> 8);
                    sample[1] = (byte)(value >> 16);
                    sample[2] = (byte)(value >> 24);
                }
            }
        }
    }
}
