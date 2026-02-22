using GenericReader;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UnrealOpusDecoder;

public class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: UnrealOpusDecoder.exe <input_file_or_folder>");
            return;
        }

        var totalTimer = Stopwatch.StartNew();

        string inputPath = args[0];
        if (Directory.Exists(inputPath))
        {
            string[] files = Directory.GetFiles(inputPath, "*.opus", SearchOption.AllDirectories);
            Console.WriteLine($"Found {files.Length} files in directory.");

            foreach (var file in files)
            {
                ProcessOpusFile(file);
            }
        }
        else if (File.Exists(inputPath))
        {
            ProcessOpusFile(inputPath);
        }
        else
        {
            Console.WriteLine("Input path not found!");
        }

        totalTimer.Stop();
        Console.WriteLine($"Total time: {totalTimer.Elapsed.TotalSeconds:F2} seconds");
    }

    static void ProcessOpusFile(string filePath)
    {
        try
        {
            Console.WriteLine($"Processing: {Path.GetFileName(filePath)}");
            byte[] fileData = File.ReadAllBytes(filePath);
            var Ar = new GenericBufferReader(fileData);

            var header = Ar.Read<UEOpusHeader>();
            header.Validate();

            using var decoder = new UnrealOpusAudioDecoder();
            if (!decoder.Initialize(header))
            {
                Console.WriteLine("Failed to initialize Opus decoder");
                return;
            }

            var allPcmData = new List<short>((int)header.ActiveSampleCount * header.NumChannels);

#if DEBUG
            Console.WriteLine($"Starting decode at offset {Ar.Position}. File size: {fileData.Length}");
#endif

            while (Ar.Position < Ar.Length)
            {
                allPcmData.AddRange(decoder.DecodeChunk(Ar));
            }

            if (allPcmData.Count > 0)
            {
                int startTrim = (header.NumSilentSamplesAtBeginning + header.NumPreSkipSamples) * header.NumChannels;
                if (startTrim > 0 && startTrim < allPcmData.Count)
                {
                    allPcmData.RemoveRange(0, startTrim);
                }

                int endTrim = header.NumSilentSamplesAtEnd * header.NumChannels;
                if (endTrim > 0 && endTrim < allPcmData.Count)
                {
                    allPcmData.RemoveRange(allPcmData.Count - endTrim, endTrim);
                }

#if DEBUG
                Console.WriteLine($"Total samples after decoding: {allPcmData.Count / header.NumChannels}. Expected: {header.ActiveSampleCount}");
#endif

                var outputPath = Path.ChangeExtension(filePath, ".wav");
                SaveWav(outputPath, allPcmData, (int)header.SampleRate, header.NumChannels);
                Console.WriteLine($"Saved to: {Path.GetFileName(outputPath)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    public static void SaveWav(string path, List<short> pcmData, int sampleRate, int channels)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);
        int dataSize = pcmData.Count * 2;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        byte[] buffer = MemoryMarshal.Cast<short, byte>(CollectionsMarshal.AsSpan(pcmData)).ToArray();
        writer.Write(buffer);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UEOpusHeader
{
    public const int HeaderSize = 42;
    public const ulong ExpectedIdentifier = 0x5355504f4555; // "UEOPUS" in little endian

    public ulong Identifier;
    public byte Version;
    public byte NumChannels;
    public uint SampleRate;
    public uint EncodedSampleRate;
    public ulong ActiveSampleCount;
    public uint NumEncodedFrames;
    public int NumPreSkipSamples;
    public int NumSilentSamplesAtBeginning;
    public int NumSilentSamplesAtEnd;

    public readonly void Validate()
    {
        if (Identifier != ExpectedIdentifier)
            throw new Exception($"Invalid Identifier: {Identifier:X}");
    }
}

public class UnrealOpusAudioDecoder : IDisposable
{
    private IntPtr _decoder = IntPtr.Zero;
    public UEOpusHeader Header { get; private set; }

    public bool Initialize(UEOpusHeader header)
    {
        Header = header;

        byte[][] mappings = [
            [0],
            [0, 1],
            [0, 1, 2],
            [0, 1, 2, 3],
            [0, 1, 4, 2, 3],
            [0, 1, 4, 5, 2, 3],
            [0, 1, 4, 6, 2, 3, 5],
            [0, 1, 6, 7, 2, 3, 4, 5]
        ];
        int[] streams = [1, 1, 2, 2, 3, 4, 4, 5];
        int[] coupled = [0, 1, 1, 2, 2, 2, 3, 3];

        int channelIndex = header.NumChannels - 1;

        _decoder = OpusNative.opus_multistream_decoder_create(
            (int)header.EncodedSampleRate,
            header.NumChannels,
            streams[channelIndex],
            coupled[channelIndex],
            mappings[channelIndex],
            out int error);

        return error == 0;
    }

    private uint GetMaxFrameSizeSamples()
    {
        const int OPUS_MAX_FRAME_SIZE_MS = 20;
        return Header.EncodedSampleRate * OPUS_MAX_FRAME_SIZE_MS * 2 / 1000;
    }

    public unsafe List<short> DecodeChunk(GenericBufferReader Ar)
    {
        // ReadSeekTable
        var magic = Ar.Read<uint>();
        if (magic != 0x4b454553) // "SEEK" in little endian
            throw new Exception("Expected SEEK table not found");

        var version = Ar.Read<byte>();
        var maxSamplesPerChannel = Ar.Read<ushort>();
        var offset = Ar.Read<uint>();
        var seektable = Ar.ReadArray<ushort>();

        int bufferSize = maxSamplesPerChannel * Header.NumChannels;
        Span<short> decodeBuf = bufferSize <= 8192
            ? stackalloc short[bufferSize]
            : new short[bufferSize];

        var result = new List<short>(seektable.Length * bufferSize);

        int totaldecodedFrames = 0;
        for (int i = 0; i < seektable.Length; i++)
        {
            var decodedFrames = 0;
            var compressedData = Ar.ReadArray<byte>(Ar.Read<ushort>());
            fixed (byte* pData = compressedData)
            fixed (short* pOut = decodeBuf)
            {
                decodedFrames = OpusNative.opus_multistream_decode(_decoder, pData, compressedData.Length, pOut, maxSamplesPerChannel, 0);
            }

            result.AddRange(decodeBuf);
            totaldecodedFrames += decodedFrames;
        }

#if DEBUG
        Console.WriteLine($"Decoded {totaldecodedFrames} frames");
#endif

        return result;
    }

    public void Dispose()
    {
        if (_decoder != IntPtr.Zero) OpusNative.opus_multistream_decoder_destroy(_decoder);
    }
}

internal static class OpusNative
{
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_multistream_decoder_create(int Fs, int channels, int streams, int coupled, byte[] mapping, out int error);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
    public static unsafe extern int opus_multistream_decode(IntPtr st, byte* data, int len, short* pcm, int frame_size, int fec);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_multistream_decoder_destroy(IntPtr st);
}