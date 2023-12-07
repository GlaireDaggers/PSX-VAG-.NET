using CommandLine;
using NAudio.Wave;
using PSXVAG;

public class Options
{
    [Value(0, Required = true, HelpText = "Input VAG file")]
    public string? inputFile { get; set; }

    [Option('v', "verbose", Required = false, HelpText = "Print parsed VAG info")]
    public bool verbose { get; set; }

    [Option('o', "output", Required = false, HelpText = "Output WAV file")]
    public string? outputFile { get; set; }
}

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Parser.Default.ParseArguments<Options>(args)
            .WithParsed(o =>
            {
                string inPath = o.inputFile!;
                Stream inStream = File.OpenRead(inPath);
                using VAGReader reader = new VAGReader(inStream, false);

                if (o.verbose)
                {
                    Console.WriteLine($"SAMPLERATE: {reader.SampleRate}");
                    Console.WriteLine($"CHANNELS: {reader.ChannelCount}");
                    Console.WriteLine($"TOTAL SAMPLES: {reader.TotalSamplesPerChannel}");
                    Console.WriteLine($"DURATION: {reader.Duration}");
                    Console.WriteLine($"INTERLEAVED: {reader.Interleave}");
                    Console.WriteLine($"CHUNK SIZE: {reader.ChunkSize}");
                }

                string outPath = o.outputFile ?? Path.ChangeExtension(inPath, "wav");

                short[] outData = new short[reader.TotalSamplesPerChannel * reader.ChannelCount];
                reader.Read(outData);

                WaveFormat fmt = new WaveFormat(reader.SampleRate, 16, reader.ChannelCount);
                using (var writer = new WaveFileWriter(File.OpenWrite(outPath), fmt))
                {
                    writer.WriteSamples(outData, 0, outData.Length);
                }
            });
    }
}