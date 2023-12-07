using CommandLine;
using NAudio.Wave;
using PSXVAG;

public class Options
{
    [Value(0, Required = true, HelpText = "Input WAV file")]
    public string? inputFile { get; set; }

    [Option('v', "verbose", Required = false, HelpText = "Print parsed WAV info")]
    public bool verbose { get; set; }

    [Option('i', "interleaved", Required = false, HelpText = "Output interleaved VAG format (required for >1 channels)")]
    public bool interleaved { get; set; }

    [Option('l', "loopflags", Required = false, HelpText = "Set loop repeat flags at end of each chunk (necessary for streaming playback in PSn00bSDK)")]
    public bool loopflags { get; set; }

    [Option('c', "chunksize", Required = false, HelpText = "Output chunk size in bytes (if interleaved, must be >0 and a multiple of 2048)")]
    public int chunkSize { get; set; }

    [Option('o', "output", Required = false, HelpText = "Output VAG file")]
    public string? outputFile { get; set; }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Parser.Default.ParseArguments<Options>(args)
            .WithParsed(o =>
            {
                string inPath = o.inputFile!;
                using Stream inStream = File.OpenRead(inPath);
                using var wavReader = new WaveFileReader(inStream);

                double duration = wavReader.SampleCount / (double)wavReader.WaveFormat.SampleRate;

                if (o.verbose)
                {
                    Console.WriteLine($"SAMPLERATE: {wavReader.WaveFormat.SampleRate}");
                    Console.WriteLine($"CHANNELS: {wavReader.WaveFormat.Channels}");
                    Console.WriteLine($"DURATION: {duration}");
                    Console.WriteLine($"BIT DEPTH: {wavReader.WaveFormat.BitsPerSample}");
                }

                float[] sampleData = new float[wavReader.SampleCount * wavReader.WaveFormat.Channels];
                wavReader.ToSampleProvider().Read(sampleData, 0, sampleData.Length);

                short[] sampleData16 = new short[sampleData.Length];
                for (int i = 0; i < sampleData.Length; i++)
                {
                    sampleData16[i] = (short)(sampleData[i] * short.MaxValue);
                }

                string outPath = o.outputFile ?? Path.ChangeExtension(inPath, "vag");
                Stream outStream = File.OpenWrite(outPath);
                using var vagWriter = new VAGWriter(o.interleaved, o.loopflags, wavReader.WaveFormat.SampleRate, wavReader.WaveFormat.Channels, o.chunkSize, outStream, false);

                vagWriter.AppendSamples(sampleData16);
                vagWriter.Finish();
            });
    }
}