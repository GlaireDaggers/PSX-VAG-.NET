using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PSXVAG;

/// <summary>
/// Class responsible for encoding audio data and writing it to a VAG file
/// </summary>
public class VAGWriter : IDisposable
{
    private static int[] filter_k1 = new int[] { 0, 60, 115, 98, 122 };
    private static int[] filter_k2 = new int[] { 0, 0, -52, -55, -60 };

    private struct EncoderState
    {
        public ulong mse;
        public int prev1, prev2;
    }

    private bool _loopFlags;
    private bool _interleaved;
    private int _chunkSizeBytes;
    private int _samplesPerChunk;
    private long _bytesPerChannelFieldOffset;
    private BinaryWriter _writer;

    private List<short>[] _channels;
    private EncoderState[] _encoderStates;

    private byte[] _preBuf = new byte[28];
    private short[] _dummyFrameSamples = new short[28];
    private byte[] _chunkTmp;
    private byte[] _frameTmp = new byte[Common.BYTES_PER_FRAME];
    
    /// <summary>
    /// Construct a simple mono non interleaved VAG writer
    /// </summary>
    /// <param name="samplerate">The samplerate of the VAG file</param>
    /// <param name="outStream">The output stream to write to</param>
    /// <param name="leaveOpen">Whether to leave the output stream open when disposing this writer</param>
    public VAGWriter(int samplerate, Stream outStream, bool leaveOpen) : this(false, false, samplerate, 1, 0, outStream, leaveOpen)
    {
    }

    /// <summary>
    /// Construct a VAG writer
    /// </summary>
    /// <param name="interleaved">Whether the output file is interleaved or not. Must be true if channelCount > 1</param>
    /// <param name="addLoopFlags">Whether to add loop flags to the end of each chunk (needed by PSn00bSDK for streaming playback)</param>
    /// <param name="samplerate">The samplerate of the VAG file</param>
    /// <param name="channelCount">The number of channels in the VAG file</param>
    /// <param name="chunkSize">The size of each chunk of the VAG file in bytes (must be non-zero and multiple of 2048 if interleaved)</param>
    /// <param name="outStream">The output stream to write to</param>
    /// <param name="leaveOpen">Whether to leave the output stream open when disposing this writer</param>
    public VAGWriter(bool interleaved, bool addLoopFlags, int samplerate, int channelCount, int chunkSize, Stream outStream, bool leaveOpen)
    {
        if (interleaved && (chunkSize <= 0 || chunkSize % 2048 != 0))
        {
            throw new ArgumentException(nameof(chunkSize) + " must be > 0 and multiple of 2048 if interleaved");
        }

        if (channelCount <= 0)
        {
            throw new ArgumentException(nameof(channelCount) + " must be > 0");
        }

        if (samplerate <= 0)
        {
            throw new ArgumentException(nameof(samplerate) + " must be > 0");
        }

        _interleaved = interleaved;
        _loopFlags = addLoopFlags;

        _channels = new List<short>[channelCount];
        _encoderStates = new EncoderState[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            _channels[i] = new List<short>();
        }

        _writer = new BinaryWriter(outStream, Encoding.ASCII, leaveOpen);

        // write VAG header
        if (interleaved)
        {
            // VAGi
            _writer.Write(Encoding.ASCII.GetBytes("VAGi"));
        }
        else
        {
            // VAGp
            _writer.Write(Encoding.ASCII.GetBytes("VAGp"));
        }

        // version (big endian)
        _writer.Write((byte)0x00);
        _writer.Write((byte)0x00);
        _writer.Write((byte)0x00);
        _writer.Write((byte)0x20);

        // interleave chunk size (little endian)
        int framesPerChunk = chunkSize / Common.BYTES_PER_FRAME;
        _samplesPerChunk = framesPerChunk * Common.SAMPLES_PER_FRAME;

        _chunkTmp = new byte[chunkSize];
        _chunkSizeBytes = chunkSize;

        _writer.Write((byte)chunkSize);
        _writer.Write((byte)(chunkSize >> 8));
        _writer.Write((byte)(chunkSize >> 16));
        _writer.Write((byte)(chunkSize >> 24));

        // length of data per channel (big endian)
        // store position so we can go back and rewrite this later
        _bytesPerChannelFieldOffset = _writer.BaseStream.Position;
        _writer.Write((uint)0);

        // samplerate (big endian)
        _writer.Write((byte)(samplerate >> 24));
        _writer.Write((byte)(samplerate >> 16));
        _writer.Write((byte)(samplerate >> 8));
        _writer.Write((byte)samplerate);

        // skip 10 bytes
        _writer.BaseStream.Seek(10, SeekOrigin.Current);

        // number of channels (little-endian)
        _writer.Write((ushort)channelCount);

        // 16 padding bytes
        _writer.BaseStream.Seek(16, SeekOrigin.Current);

        // align to next 2048 byte boundary
        long streamAlign = (2048 - (_writer.BaseStream.Position % 2048)) % 2048;
        _writer.BaseStream.Seek(streamAlign, SeekOrigin.Current);
    }

    /// <summary>
    /// Append signed 16 bit samples to the stream
    /// </summary>
    /// <param name="samples">Span containing audio data to write</param>
    public void AppendSamples(Span<short> samples)
    {
        int samplesPerChannel = samples.Length / _channels.Length;

        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i].EnsureCapacity(_channels[i].Count + samplesPerChannel);
        }

        for (int i = 0; i < samples.Length; i++)
        {
            _channels[i % _channels.Length].Add(samples[i]);
        }
    }

    /// <summary>
    /// Encode & write all input samples to disk. Should be called exactly once as the very last step
    /// </summary>
    public void Finish()
    {
        if (_interleaved)
        {
           FinishChunked();
        }
        else
        {
            FinishNonChunked();
        }
    }

    private void FinishNonChunked()
    {
        int frames = _channels[0].Count / Common.SAMPLES_PER_FRAME;

        if (_channels[0].Count % Common.SAMPLES_PER_FRAME != 0)
            frames++;

        uint totalLenPerChannel = (uint)(frames * Common.BYTES_PER_FRAME);

        for (int i = 0; i < frames; i++)
        {
            var srcOffset = i * Common.SAMPLES_PER_FRAME;

            byte flags = 0;

            // last frame has end+mute set
            if (i == frames - 1)
            {
                flags = 1;
            }
            
            if (srcOffset >= _channels[0].Count)
            {
                // encode dummy padding frame
                EncodeFrame(flags, ref _encoderStates[0], _dummyFrameSamples, _frameTmp);
            }
            else
            {
                EncodeFrame(flags, ref _encoderStates[0],
                    CollectionsMarshal.AsSpan(_channels[0]).Slice(srcOffset),
                    _frameTmp);
            }

            _writer.Write(_frameTmp);
        }

        // patch up bytes per channel field in header
        long pos = _writer.BaseStream.Position;
        _writer.BaseStream.Seek(_bytesPerChannelFieldOffset, SeekOrigin.Begin);
        
        _writer.Write((byte)(totalLenPerChannel >> 24));
        _writer.Write((byte)(totalLenPerChannel >> 16));
        _writer.Write((byte)(totalLenPerChannel >> 8));
        _writer.Write((byte)totalLenPerChannel);

        _writer.BaseStream.Seek(pos, SeekOrigin.Begin);
    }

    private void FinishChunked()
    {
        int chunks = _channels[0].Count / _samplesPerChunk;
        
        if (_channels[0].Count % _samplesPerChunk != 0)
            chunks++;

        uint totalLenPerChannel = (uint)(chunks * _chunkSizeBytes);

        for (int i = 0; i < chunks; i++)
        {
            var srcOffset = i * _samplesPerChunk;

            for (int ch = 0; ch < _channels.Length; ch++)
            {
                EncodeChunk(ref _encoderStates[ch], i == chunks - 1, CollectionsMarshal.AsSpan(_channels[ch]).Slice(srcOffset), _chunkTmp);
                _writer.Write(_chunkTmp);
            }
        }

        // patch up bytes per channel field in header
        long pos = _writer.BaseStream.Position;
        _writer.BaseStream.Seek(_bytesPerChannelFieldOffset, SeekOrigin.Begin);
        
        _writer.Write((byte)(totalLenPerChannel >> 24));
        _writer.Write((byte)(totalLenPerChannel >> 16));
        _writer.Write((byte)(totalLenPerChannel >> 8));
        _writer.Write((byte)totalLenPerChannel);

        _writer.BaseStream.Seek(pos, SeekOrigin.Begin);
    }

    private void EncodeChunk(ref EncoderState state, bool lastChunk, Span<short> samples, Span<byte> data)
    {
        int frames = _samplesPerChunk / Common.SAMPLES_PER_FRAME;

        for (int i = 0; i < frames; i++)
        {
            int srcOffset = i * Common.SAMPLES_PER_FRAME;
            int dstOffset = i * Common.BYTES_PER_FRAME;

            byte flags = 0;

            if (i == frames - 1)
            {
                // last frame of last chunk has end flag set
                if (lastChunk)
                    flags |= 1;

                // if enable loop flags, last frame of every chunk has end+repeat flags set
                // this is necessary to enable PSn00bSDK's streaming playback
                // basically it abuses hardware looping by overwriting the loop address to point at the next chunk in SPU RAM,
                // then when the hardware encounters the frame with repeat flag set it will jump to the next chunk to be played
                if (_loopFlags)
                    flags |= 3;
            }
            
            if (srcOffset >= samples.Length)
            {
                // encode dummy padding frame
                EncodeFrame(flags, ref state, _dummyFrameSamples, data.Slice(dstOffset));
            }
            else
            {
                EncodeFrame(flags, ref state,
                    samples.Slice(srcOffset),
                    data.Slice(dstOffset));
            }
        }
    }

    private void EncodeFrame(byte flags, ref EncoderState state, Span<short> samples, Span<byte> data)
    {
        data[0] = EncodeFrame(ref state, samples, _preBuf, 0, 5, 12);
        data[1] = flags;

        for (int i = 0; i < 28; i += 2)
        {
            data[2 + (i >> 1)] = (byte)((_preBuf[i] & 0x0F) | (_preBuf[i + 1] << 4));
        }
    }

    private byte EncodeFrame(ref EncoderState state, Span<short> samples, Span<byte> data, int dataShift, int filterCount, int shiftRange)
    {
        EncoderState proposed;
        ulong bestMse = 1UL << 50;
        int bestFilter = 0;
        int bestSampleShift = 0;

        for (int filter = 0; filter < filterCount; filter++)
        {
            int trueMinShift = FindMinShift(state, samples, filter, shiftRange);

            int minShift = trueMinShift - 1;
            int maxShift = trueMinShift + 1;
            if (minShift < 0) minShift = 0;
            if (maxShift > shiftRange) maxShift = shiftRange;

            for (int sampleShift = minShift; sampleShift <= maxShift; sampleShift++)
            {
                TryEncodeFrame(state, out proposed, samples, data, dataShift, filter, sampleShift, shiftRange);

                if (bestMse > proposed.mse)
                {
                    bestMse = proposed.mse;
                    bestFilter = filter;
                    bestSampleShift = sampleShift;
                }
            }
        }

        // use best encoder settings
        return TryEncodeFrame(state, out state, samples, data, dataShift, bestFilter, bestSampleShift, shiftRange);
    }

    private int FindMinShift(in EncoderState state, Span<short> samples, int filter, int shiftRange)
    {
        int prev1 = state.prev1;
        int prev2 = state.prev2;
        int k1 = filter_k1[filter];
        int k2 = filter_k2[filter];

        int rightShift = 0;

        int min = 0;
        int max = 0;

        for (int i = 0; i < 28; i++)
        {
            int rawSample = i >= samples.Length ? 0 : samples[i];
            int previousValues = (k1 * prev1 + k2 * prev2 + (1 << 5)) >> 6;
            int sample = rawSample - previousValues;
		    
            if (sample < min) { min = sample; }
		    if (sample > max) { max = sample; }

            prev2 = prev1;
            prev1 = rawSample;
        }

        while(rightShift < shiftRange && (max >> rightShift) > (+0x7FFF >> shiftRange)) { rightShift++; };
        while(rightShift < shiftRange && (min >> rightShift) < (-0x8000 >> shiftRange)) { rightShift++; };

	    int minShift = shiftRange - rightShift;
        Debug.Assert(0 <= minShift && minShift <= shiftRange);

        return minShift;
    }

    private byte TryEncodeFrame(in EncoderState inState, out EncoderState outState, Span<short> samples, Span<byte> data, int dataShift, int filter, int sampleShift, int shiftRange)
    {
        byte sampleMask = (byte)(0xFFFF >> shiftRange);
        byte nondataMask = (byte)~(sampleMask << dataShift);

        int minShift = sampleShift;
        int k1 = filter_k1[filter];
        int k2 = filter_k2[filter];

        byte hdr = (byte)((minShift & 0x0F) | (filter << 4));

        outState = inState;
        outState.mse = 0;

        for (int i = 0; i < 28; i++)
        {
            int sample = i >= samples.Length ? 0 : samples[i];
            int previousValues = (k1 * outState.prev1 + k2 * outState.prev2 + (1 << 5)) >> 6;
            int sampleEnc = sample - previousValues;
            sampleEnc <<= minShift;
            sampleEnc += 1 << (shiftRange - 1);
            sampleEnc >>= shiftRange;
		    if(sampleEnc < (-0x8000 >> shiftRange)) { sampleEnc = -0x8000 >> shiftRange; }
		    if(sampleEnc > (+0x7FFF >> shiftRange)) { sampleEnc = +0x7FFF >> shiftRange; }
            sampleEnc &= sampleMask;

            int sampleDec = (short)((sampleEnc & sampleMask) << shiftRange);
		    sampleDec >>= minShift;
            sampleDec += previousValues;
		    if (sampleDec > +0x7FFF) { sampleDec = +0x7FFF; }
		    if (sampleDec < -0x8000) { sampleDec = -0x8000; }
            
            long sampleError = sampleDec - sample;

            Debug.Assert(sampleError < (1 << 30));
            Debug.Assert(sampleError > -(1 << 30));

            data[i] = (byte)((data[i] & nondataMask) | (sampleEnc << dataShift));
            
            outState.mse += (ulong)sampleError * (ulong)sampleError;
            outState.prev2 = outState.prev1;
            outState.prev1 = sampleDec;
        }

        return hdr;
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}