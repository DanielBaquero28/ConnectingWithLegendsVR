using System;
using System.IO;
using System.Text;
using UnityEngine;

public static class SavWav
{
    private const int HeaderSize = 44;

    public static byte[] GetWav(AudioClip clip, out uint length)
    {
        using (var stream = new MemoryStream())
        {
            WriteWavHeader(stream, clip);
            WriteAudioData(stream, clip);

            byte[] bytes = stream.ToArray();
            length = (uint)bytes.Length;
            return bytes;
        }
    }

    private static void WriteWavHeader(MemoryStream stream, AudioClip clip)
    {
        int hz = clip.frequency;
        int channels = clip.channels;
        int samples = clip.samples;

        stream.Seek(0, SeekOrigin.Begin);

        stream.Write(Encoding.UTF8.GetBytes("RIFF"), 0, 4);
        stream.Write(BitConverter.GetBytes(stream.Length - 8), 0, 4);
        stream.Write(Encoding.UTF8.GetBytes("WAVE"), 0, 4);
        stream.Write(Encoding.UTF8.GetBytes("fmt "), 0, 4);
        stream.Write(BitConverter.GetBytes(16), 0, 4); // Subchunk1Size
        stream.Write(BitConverter.GetBytes((ushort)1), 0, 2); // AudioFormat (PCM)
        stream.Write(BitConverter.GetBytes((ushort)channels), 0, 2);
        stream.Write(BitConverter.GetBytes(hz), 0, 4);
        stream.Write(BitConverter.GetBytes(hz * channels * 2), 0, 4); // ByteRate
        stream.Write(BitConverter.GetBytes((ushort)(channels * 2)), 0, 2); // BlockAlign
        stream.Write(BitConverter.GetBytes((ushort)16), 0, 2); // BitsPerSample
        stream.Write(Encoding.UTF8.GetBytes("data"), 0, 4);
        stream.Write(BitConverter.GetBytes(samples * channels * 2), 0, 4); // Subchunk2Size
    }

    private static void WriteAudioData(MemoryStream stream, AudioClip clip)
    {
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        short[] intData = new short[samples.Length];
        byte[] bytesData = new byte[samples.Length * 2];

        for (int i = 0; i < samples.Length; i++)
        {
            // Convert float (-1.0 to 1.0) to short (-32768 to 32767)
            intData[i] = (short)(samples[i] * 32767);
            byte[] byteArr = BitConverter.GetBytes(intData[i]);
            byteArr.CopyTo(bytesData, i * 2);
        }

        stream.Write(bytesData, 0, bytesData.Length);
    }
}