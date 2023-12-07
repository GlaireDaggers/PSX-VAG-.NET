## PSXVAG .NET

A very basic tiny library for encoding & decoding PSX vag audio files (both non-interleaved mono & interleaved multi-channel).

## VAG FILES?

VAG files are an audio format used by a variety of PSX, PSP, PS2, and even PS3 games. There's a lot of variations & extensions but this library is concerned with a small subset of those: just the standard non-interleaved (vag) and interleaved (vagi) PSX format.

This format encodes audio in a format native to the PSX's SPU chip, as ADPCM-encoded data stored in frames of 16 bytes.

## Decoding 'em

Just open a stream to your file and create a new VAGReader:

```
var stream = File.OpenRead("path/to/your/sound.vag");
using var reader = new VAGReader(stream, false);

short[] audio_data = new short[reader.TotalSamplesPerChannel * reader.ChannelCount];
reader.Read(audio_data);
```

## Encoding 'em

Open an output stream, create a new VAGWriter, append some samples, and finish:

```
var stream = File.OpenWrite("/path/to/output/sound.vag");
using var writer = new VAGWriter(44100, stream, false);

writer.AppendSamples(audio_data);
writer.Finish();
```

## Encoding Options

When encoding with VAGWriter, you have a couple of options.

There's a basic constructor which only takes a samplerate and an output stream. This will output a simple non-interleaved mono audio file.
```
VAGWriter(samplerate, stream, leaveOpen)
```

There's also a more advanced constructor which gives you more options:
```
VAGWriter(interleaved, samplerate, channelCount, chunkSize, stream, leaveOpen);
```

Multi-channel audio files must be interleaved. This will split the audio data into chunks of chunkSize bytes, with each chunk in the output file containing the data for one channel. The chunks for each channel will be interleaved (as LRLRLR... etc).

Additionally, chunkSize must be a multiple of 2048 bytes (2048 or 4096 are generally decent values).

## Examples

Some example CLI tools are provided in the example folder. These are vag2wav and wav2vag. As the name implies these are both tools which can decode VAG to WAV files and encode WAV to VAG files, respectively.