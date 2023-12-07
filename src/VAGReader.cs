using System.Text;

namespace PSXVAG;

/// <summary>
/// Exception thrown for format errors encountered while parsing a VAG file 
/// </summary>
public class VAGParseException : Exception
{
    public VAGReader reader;

    public VAGParseException(VAGReader reader, string message) : base($"Failed parsing VAG file: ${message}")
    {
        this.reader = reader;
    }
}

/// <summary>
/// Class responsible for opening a VAG file and reading audio data from it
/// </summary>
public class VAGReader : IDisposable
{
    private static float[][] psx_adpcm_coefs = new float[16][] {
        new float[2] {0.0f,         0.0f},
        new float[2] {0.9375f,      0.0f},
        new float[2] {1.796875f,    -0.8125f},
        new float[2] {1.53125f,     -0.859375f},
        new float[2] {1.90625f,     -0.9375f},
        new float[2] {0.46875f,     -0.0f},
        new float[2] {0.8984375f,   -0.40625f},
        new float[2] {0.765625f,    -0.4296875f},
        new float[2] {0.953125f,    -0.46875f},
        new float[2] {0.234375f,    -0.0f},
        new float[2] {0.44921875f,  -0.203125f},
        new float[2] {0.3828125f,   -0.21484375f},
        new float[2] {0.4765625f,   -0.234375f},
        new float[2] {0.5f,         -0.9375f},
        new float[2] {0.234375f,    -0.9375f},
        new float[2] {0.109375f,    -0.9375f},
    };

    private static int[] nibble_to_int = new int[16] {
        0,1,2,3,4,5,6,7,-8,-7,-6,-5,-4,-3,-2,-1
    };

    /// <summary>
    /// Returns true if this is a multi-channel interleaved VAG file, false otherwise
    /// </summary>
    public bool Interleave => _interleave;

    /// <summary>
    /// Returns the number of samples per chunk in this VAG file (if interleaved)
    /// </summary>
    public int ChunkSize => _interleave ? (int)_chunkSize : 0;

    /// <summary>
    /// Returns the total number of samples per channel in this VAG file
    /// </summary>
    public int TotalSamplesPerChannel => (int)(_dataLength / Common.BYTES_PER_FRAME * Common.SAMPLES_PER_FRAME);

    /// <summary>
    /// Returns the total duration of the VAG stream in seconds
    /// </summary>
    public double Duration => TotalSamplesPerChannel / (double)_sampleRate;

    /// <summary>
    /// Returns the sample rate in Hz of this VAG file
    /// </summary>
    public int SampleRate => (int)_sampleRate;

    /// <summary>
    /// Returns the number of channels in this VAG file
    /// </summary>
    public int ChannelCount => (int)_channelCount;

    private bool _interleave;
    private uint _version;
    private uint _chunkSize;
    private uint _dataLength;
    private uint _sampleRate;
    private ushort _channelCount;

    private BinaryReader _reader;

    private byte[] _frame = new byte[0x10];
    private int[] _history1;
    private int[] _history2;

    private uint _framesPerChunk;
    private uint _samplesPerChunk;

    private long _loopPos;
    private bool _endOfStream = false;

    private short[] _tmpChunkBuffer;
    private int _tmpChunkReadPos;
    private int _tmpChunkLen;

    /// <summary>
    /// Construct a new VAGReader
    /// </summary>
    /// <param name="input">The input stream to read from. It is assumed to be positioned at the beginning of the VAG file data</param>
    /// <param name="leaveOpen">Whether to leave the stream open upon disposing this reader</param>
    /// <exception cref="VAGParseException"></exception>
    public VAGReader(Stream input, bool leaveOpen)
    {
        _reader = new BinaryReader(input, Encoding.ASCII, leaveOpen);

        byte[] magic = _reader.ReadBytes(4);
        string magic_str = Encoding.ASCII.GetString(magic);

        if (magic_str == "VAGi")
        {
            _interleave = true;
        }
        else if (magic_str == "VAGp")
        {
            _interleave = false;
        }
        else
        {
            throw new VAGParseException(this, "Input is not a valid VAG stream");
        }

        // version (big-endian)
        _version = ((uint)_reader.ReadByte() << 24) |
            ((uint)_reader.ReadByte() << 16) |
            ((uint)_reader.ReadByte() << 8) |
            _reader.ReadByte();

        // interleave (little-endian)
        _chunkSize = _reader.ReadUInt32();

        // length of data per channel (big-endian)
        _dataLength = ((uint)_reader.ReadByte() << 24) |
            ((uint)_reader.ReadByte() << 16) |
            ((uint)_reader.ReadByte() << 8) |
            _reader.ReadByte();

        // sample rate (big-endian)
        _sampleRate = ((uint)_reader.ReadByte() << 24) |
            ((uint)_reader.ReadByte() << 16) |
            ((uint)_reader.ReadByte() << 8) |
            _reader.ReadByte();

        // skip 10 bytes
        _reader.BaseStream.Seek(10, SeekOrigin.Current);

        // number of channels (little-endian)
        _channelCount = _reader.ReadUInt16();

        // 16 padding bytes
        _reader.BaseStream.Seek(16, SeekOrigin.Current);

        _history1 = new int[_channelCount];
        _history2 = new int[_channelCount];

        // align to next 2048 byte boundary
        long streamAlign = (2048 - (_reader.BaseStream.Position % 2048)) % 2048;
        _reader.BaseStream.Seek(streamAlign, SeekOrigin.Current);

        _loopPos = _reader.BaseStream.Position;

        if (!_interleave)
        {
            // honestly using chunked reading just makes everything easier
            // so we're just gonna pick an arbitrary chunk size to use for reading even if input is not interleaved
            _chunkSize = 2048;
        }

        _framesPerChunk = _chunkSize / Common.BYTES_PER_FRAME;
        _samplesPerChunk = _framesPerChunk * Common.SAMPLES_PER_FRAME;
        _tmpChunkBuffer = new short[_samplesPerChunk * _channelCount];

        _tmpChunkReadPos = 0;
        _tmpChunkLen = 0;
    }

    /// <summary>
    /// Read a number of signed 16-bit samples from the stream into the output buffer
    /// </summary>
    /// <param name="outBuf">The output buffer to read samples into</param>
    /// <returns>The actual number of samples read</returns>
    public int Read(Span<short> outBuf)
    {
        unsafe
        {
            fixed (short* ptr = &outBuf[0])
            {
                return ReadSamples(ptr, outBuf.Length);
            }
        }
    }

    /// <summary>
    /// Read a number of samples from the stream, convert into 32-bit floating point, and write into the output buffer
    /// </summary>
    /// <param name="outBuf">The output buffer to read samples into</param>
    /// <returns>The actual number of samples read</returns>
    public int Read(Span<float> outBuf)
    {
        unsafe
        {
            fixed (float* ptr = &outBuf[0])
            {
                return ReadSamples(ptr, outBuf.Length);
            }
        }
    }

    /// <summary>
    /// Read a number of signed 16-bit samples from the stream into the output byte buffer
    /// </summary>
    /// <param name="outBuf">The output byte buffer to read samples into</param>
    /// <returns>The actual number of samples read</returns>
    public int ReadBytes(Span<byte> outBuf)
    {
        unsafe
        {
            fixed (byte* ptr = &outBuf[0])
            {
                return ReadSamples((short*)ptr, outBuf.Length / 2);
            }
        }
    }

    /// <summary>
    /// Reset the stream back to the beginning
    /// </summary>
    public void Reset()
    {
        _endOfStream = false;
        _reader.BaseStream.Seek(_loopPos, SeekOrigin.Begin);
        for (int i = 0; i < _channelCount; i++)
        {
            _history1[i] = 0;
            _history2[i] = 0;
        }
    }

    private unsafe int ReadSamples(short* outBuf, int outBufLength)
    {
        int read_samples = 0;

        while (read_samples < outBufLength)
        {
            if (_tmpChunkLen == 0)
            {
                _tmpChunkReadPos = 0;
                fixed (short* buf = &_tmpChunkBuffer[0])
                {
                    _tmpChunkLen = ReadChunk(buf);
                    if (_tmpChunkLen == 0) break;
                }
            }

            outBuf[read_samples++] = _tmpChunkBuffer[_tmpChunkReadPos++];
            _tmpChunkLen--;
        }

        return read_samples;
    }

    private unsafe int ReadSamples(float* outBuf, int outBufLength)
    {
        int read_samples = 0;

        while (read_samples < outBufLength)
        {
            if (_tmpChunkLen == 0)
            {
                _tmpChunkReadPos = 0;
                fixed (short* buf = &_tmpChunkBuffer[0])
                {
                    _tmpChunkLen = ReadChunk(buf);
                    if (_tmpChunkLen == 0) break;
                }
            }

            outBuf[read_samples++] = _tmpChunkBuffer[_tmpChunkReadPos++] / 32768.0f;
            _tmpChunkLen--;
        }

        return read_samples;
    }

    private unsafe int ReadChunk(short* outBuf)
    {
        if (_endOfStream) return 0;

        int frames_per_chunk = (int)_chunkSize / Common.BYTES_PER_FRAME;
        int read_samples = 0;

        for (int ch = 0; ch < _channelCount; ch++)
        {
            int h1 = _history1[ch];
            int h2 = _history2[ch];

            for (int i = 0; i < frames_per_chunk; i++)
            {
                int offset = (i * Common.SAMPLES_PER_FRAME * _channelCount) + ch;
                
                if (!ReadFrame(ref h1, ref h2, outBuf, offset, _channelCount, 0, Common.SAMPLES_PER_FRAME))
                {
                    // end
                    _endOfStream = true;
                    return read_samples;
                }
                
                read_samples += Common.SAMPLES_PER_FRAME;

                byte flag = _frame[1];
                if ((flag & 3) == 1)
                {
                    // end + mute
                    _endOfStream = true;
                    return read_samples;
                }
            }

            _history1[ch] = h1;
            _history2[ch] = h2;
        }

        return read_samples;
    }

    private unsafe bool ReadFrame(ref int h1, ref int h2, short* outBuf, int outBufOffset, int channelSpacing, int firstSample, int samplesToRead)
    {
        int i, frames_in, sample_count;
        sample_count = 0;
        byte coef_index, shift_factor, flag;

        frames_in = firstSample / Common.SAMPLES_PER_FRAME;
        firstSample = firstSample % Common.SAMPLES_PER_FRAME;

        // parse frame header
        _reader.BaseStream.Seek(Common.BYTES_PER_FRAME * frames_in, SeekOrigin.Current);
        if (_reader.Read(_frame) < Common.BYTES_PER_FRAME)
        {
            return false;
        }

        coef_index = (byte)((_frame[0] >> 4) & 0xF);
        shift_factor = (byte)((_frame[0] >> 0) & 0xF);
        flag = _frame[1];

        if (coef_index > 5) coef_index = 0;
        if (shift_factor > 12) shift_factor = 9;

        shift_factor = (byte)(20 - shift_factor);

        // decode nibbles
        for (i = firstSample; i < firstSample + samplesToRead; i++)
        {
            int sample = 0;

            if (flag < 0x07)
            {
                byte nibbles = _frame[0x02 + (i/2)];

                sample = ((i & 1) == 1 ?
                    GetHighNibbleSigned(nibbles) : GetLowNibbleSigned(nibbles)) << shift_factor;
                sample += (int)((psx_adpcm_coefs[coef_index][0] * h1 + psx_adpcm_coefs[coef_index][1] * h2) * 256.0f);
                sample >>= 8;
            }

            outBuf[sample_count + outBufOffset] = ClampConvert16(sample);
            sample_count += channelSpacing;

            h2 = h1;
            h1 = sample;
        }

        return true;
    }

    private short ClampConvert16(int value)
    {
        if (value < short.MinValue) return short.MinValue;
        else if (value > short.MaxValue) return short.MaxValue;

        return (short)value;
    }

    private int GetHighNibbleSigned(byte nibbles)
    {
        return nibble_to_int[nibbles >> 4];
    }

    private int GetLowNibbleSigned(byte nibbles)
    {
        return nibble_to_int[nibbles & 0xF];
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}
