using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

using static ReadAloudStudio.UiText;

namespace ReadAloudStudio;

internal sealed record InputDevice(string Id, string Name, bool Default);

internal static class StudioAudio
{
    public const int MaxSeconds = 300;
    public static WaveFormat Format => new(16000, 16, 1);
    public static List<InputDevice> Inputs()
    {
        using var api = new MMDeviceEnumerator();
        string defaultId = "";
        try { using var def = api.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); defaultId = def.ID; }
        catch (COMException) { }
        var result = new List<InputDevice>();
        foreach (var dev in api.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (dev) result.Add(new(dev.ID, dev.FriendlyName, dev.ID == defaultId));
        }
        return result;
    }

    public static void Import(string input, string output)
    {
        using var reader = new WaveFileReader(input);
        if (reader.TotalTime > TimeSpan.FromSeconds(MaxSeconds))
            throw new InvalidDataException(Tr("DiagImportLength"));
        if (reader.Length == 0) throw new InvalidDataException(Tr("DiagEmptyWav"));
        if (reader.WaveFormat.Channels == 1 && reader.WaveFormat.SampleRate == 16000 &&
            reader.WaveFormat.BitsPerSample == 16 && reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm)
            WaveFileWriter.CreateWaveFile(output, reader);
        else
        {
            using var convert = new MediaFoundationResampler(reader, Format) { ResamplerQuality = 60 };
            using var writer = new WaveFileWriter(output, convert.WaveFormat);
            var buffer = new byte[convert.WaveFormat.AverageBytesPerSecond];
            int remaining = MaxSeconds * convert.WaveFormat.AverageBytesPerSecond;
            while (remaining > 0)
            {
                int read = convert.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0) break;
                writer.Write(buffer, 0, read);
                remaining -= read;
            }
        }
    }
}

internal sealed class CaptureSession : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly WaveFileWriter file;
    private readonly TaskCompletionSource<Exception?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int stopped;
    private int bytes;
    private float level;
    public float Level => Volatile.Read(ref level);
    public double Seconds => Volatile.Read(ref bytes) / 32000.0;
    public bool Finished => done.Task.IsCompleted;

    private CaptureSession(MMDevice device, WasapiCapture capture, WaveFileWriter file)
    {
        this.device = device;
        this.capture = capture;
        this.file = file;
        capture.DataAvailable += (_, e) => {
            int count = Math.Min(e.BytesRecorded, 32000 * StudioAudio.MaxSeconds - bytes);
            if (count <= 0) return;
            file.Write(e.Buffer, 0, count);
            int peak = 0;
            foreach (short sample in MemoryMarshal.Cast<byte,short>(e.Buffer.AsSpan(0, count)))
                peak = Math.Max(peak, Math.Abs((int)sample));
            Volatile.Write(ref level, peak / 32768f);
            Interlocked.Add(ref bytes, count);
        };
        capture.RecordingStopped += (_, e) => done.TrySetResult(e.Exception);
    }

    public static CaptureSession Start(string deviceId, string path)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        WasapiCapture? input = null;
        WaveFileWriter? writer = null;
        try
        {
            input = new WasapiCapture(device, true, 50) { WaveFormat = StudioAudio.Format };
            writer = new WaveFileWriter(path, StudioAudio.Format);
            var session = new CaptureSession(device, input, writer);
            input.StartRecording();
            return session;
        }
        catch
        {
            writer?.Dispose(); input?.Dispose(); device.Dispose(); throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped,1) == 0 && !Finished) capture.StopRecording();
        var error = await done.Task.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        if (error != null) throw new IOException(Tr("DiagMicInterrupted"), error);
    }
    public void Dispose()
    {
        capture.Dispose();
        file.Dispose();
        device.Dispose();
    }
}

internal sealed class AudioPlayer(Func<IWavePlayer> createOutput)
{
    public AudioPlayer() : this(()=>new WasapiOut(AudioClientShareMode.Shared,true,80)) { }
    private IWavePlayer? active;
    public void Stop() => active?.Stop();
    public async Task Play(string path, double? start = null, double? end = null, bool pad = true)
    {
        Stop();
        if (!File.Exists(path)) throw new FileNotFoundException(Tr("DiagMissingAudio"), path);
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        long startByte = 0, endByte = reader.Length;
        if (start is double a && end is double b)
        {
            if (b <= a) throw new InvalidDataException(Tr("DiagWordRange"));
            long Align(double t) => Math.Clamp((long)(t * format.AverageBytesPerSecond / format.BlockAlign)
                * format.BlockAlign, 0, reader.Length);
            startByte = Align(Math.Max(0,pad ? a-.045 : a)); endByte = Align(pad ? b+.075 : b);
        }
        reader.Position = startByte;
        var bytes = new byte[checked((int)(endByte - startByte))];
        reader.ReadExactly(bytes);
        await PlayPcm(bytes, format);
    }

    public async Task PlayPcm(byte[] bytes, WaveFormat format)
    {
        Stop();
        using var stream = new RawSourceWaveStream(new MemoryStream(bytes, false), format);
        using var output = createOutput();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) => {
            if (args.Exception != null) done.TrySetException(args.Exception);
            else done.TrySetResult();
        };
        try
        {
            output.Init(stream);
            active = output;
            output.Play();
            await done.Task;
        }
        finally { if (active == output) active = null; }
    }
}
