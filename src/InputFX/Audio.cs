using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace InputFX;

/// <summary>Listens to whatever the PC is playing (WASAPI loopback) and turns it into 19 frequency bands plus a beat flag.</summary>
public sealed class AudioAnalyzer : IDisposable
{
    public static AudioAnalyzer Instance { get; } = new();

    WasapiLoopbackCapture? cap;
    readonly float[] ring = new float[2048];
    int ringPos;
    readonly object gate = new();
    Thread? worker;
    volatile bool running;
    public string Status { get; private set; } = "off";

    // results
    readonly double[] bands = new double[19];
    double level, bass;
    volatile bool beatPending;
    readonly double[] peak = new double[19];
    double bassAvg;
    DateTime lastBeat = DateTime.MinValue;
    int refs;

    public void Acquire() { if (Interlocked.Increment(ref refs) == 1) Start(); }
    public void Release() { if (Interlocked.Decrement(ref refs) <= 0) { refs = 0; Stop(); } }

    void Start()
    {
        if (running) return;
        try
        {
            cap = new WasapiLoopbackCapture();
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, _) => { };
            fmtChannels = cap.WaveFormat.Channels; sampleRate = cap.WaveFormat.SampleRate; bits = cap.WaveFormat.BitsPerSample; isFloat = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || cap.WaveFormat.Encoding == WaveFormatEncoding.Extensible;
            cap.StartRecording();
            running = true;
            worker = new Thread(Analyze) { IsBackground = true, Name = "audio-fft" };
            worker.Start();
            Status = $"listening ({sampleRate} Hz)";
            Log.Write("audio loopback started " + sampleRate);
        }
        catch (Exception e) { Status = "audio unavailable: " + e.Message; Log.Write("audio start: " + e.Message); running = false; }
    }

    void Stop()
    {
        running = false;
        try { cap?.StopRecording(); cap?.Dispose(); } catch { }
        cap = null; Status = "off";
    }

    int fmtChannels = 2, sampleRate = 48000, bits = 32; bool isFloat = true;

    void OnData(object? s, WaveInEventArgs e)
    {
        int bytesPer = bits / 8, frame = bytesPer * fmtChannels;
        lock (gate)
        {
            for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
            {
                float sum = 0;
                for (int ch = 0; ch < fmtChannels; ch++)
                {
                    int o = i + ch * bytesPer;
                    sum += isFloat && bits == 32 ? BitConverter.ToSingle(e.Buffer, o) : bits == 16 ? BitConverter.ToInt16(e.Buffer, o) / 32768f : 0;
                }
                ring[ringPos] = sum / fmtChannels;
                ringPos = (ringPos + 1) % ring.Length;
            }
        }
    }

    void Analyze()
    {
        const int N = 1024;
        var re = new double[N]; var im = new double[N];
        var win = new double[N];
        for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
        // log-spaced band edges 40 Hz .. 14 kHz
        var edges = new int[20];
        for (int b = 0; b <= 19; b++) edges[b] = Math.Max(1, (int)(40 * Math.Pow(14000.0 / 40, b / 19.0) * N / sampleRate));
        for (int b = 1; b <= 19; b++) if (edges[b] <= edges[b - 1]) edges[b] = edges[b - 1] + 1;
        while (running)
        {
            Thread.Sleep(22);
            lock (gate)
                for (int i = 0; i < N; i++) { re[i] = ring[(ringPos - N + i + ring.Length * 2) % ring.Length] * win[i]; im[i] = 0; }
            Fft(re, im);
            double rms = 0;
            for (int b = 0; b < 19; b++)
            {
                double m = 0; int n = 0;
                for (int k = edges[b]; k < Math.Min(edges[b + 1], N / 2); k++) { m += Math.Sqrt(re[k] * re[k] + im[k] * im[k]); n++; }
                m = n > 0 ? m / n : 0;
                m = Math.Log10(1 + m * 40) ; // compress
                peak[b] = Math.Max(m, peak[b] * 0.995);
                double norm = peak[b] > 0.02 ? m / peak[b] : 0;
                if (norm > bands[b]) bands[b] = bands[b] * 0.3 + norm * 0.7; else bands[b] = bands[b] * 0.86;
                rms += norm * norm;
            }
            level = Math.Sqrt(rms / 19);
            double bn = (bands[0] + bands[1] + bands[2]) / 3;
            bass = bn;
            bassAvg = bassAvg * 0.93 + bn * 0.07;
            if (bn > 0.35 && bn > bassAvg * 1.35 && (DateTime.UtcNow - lastBeat).TotalMilliseconds > 220) { lastBeat = DateTime.UtcNow; beatPending = true; }
        }
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }

    public double CurrentLevel => level;
    public double CurrentBass => bass;

    public AudioFrame Snapshot()
    {
        var f = new AudioFrame { Level = level, Bass = bass };
        Array.Copy(bands, f.Bands, 19);
        if (beatPending) { f.Beat = true; beatPending = false; }
        return f;
    }

    public void Dispose() => Stop();
}
