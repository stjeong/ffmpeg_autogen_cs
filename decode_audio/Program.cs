using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using FFmpeg.OSDepends;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace decode_audio
{
    internal unsafe class Program
    {
        const int AUDIO_INBUF_SIZE = 20480;
        const int AUDIO_REFILL_THRESH = 4096;

        static void Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
#endif
            Console.WriteLine();

            Console.WriteLine($"LIBAVFORMAT Version: {ffmpeg.LIBAVFORMAT_VERSION_MAJOR}.{ffmpeg.LIBAVFORMAT_VERSION_MINOR}");

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string outputFilePath = Path.Combine(dirPath, "test.dat");

            string src_filename = Path.Combine(dirPath, "..", "..", "..", "..", "Samples", "sample-12s.mp2");
            decode_audio(src_filename, outputFilePath);
        }

        static unsafe void decode_audio(string inputFileName, string outputFileName)
        {
            AVCodec* codec = null;
            AVPacket* packet = null;
            AVCodecParserContext* parser = null;
            AVCodecContext* c = null;
            AVFrame* decoded_frame = null;

            int ret;

            do
            {
                packet = ffmpeg.av_packet_alloc();

                codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_MP2);
                if (codec == null)
                {
                    Console.WriteLine("Codec not found");
                    break;
                }

                parser = ffmpeg.av_parser_init((int)codec->id);
                if (parser == null)
                {
                    Console.WriteLine("Parser not found");
                    break;
                }

                c = ffmpeg.avcodec_alloc_context3(codec);
                if (c == null)
                {
                    Console.WriteLine("Could not allocate audio codec context");
                    break;
                }

                if (ffmpeg.avcodec_open2(c, codec, null) < 0)
                {
                    Console.WriteLine("Could not open codec");
                    break;
                }

                using FileStream f = File.OpenRead(inputFileName);
                using FileStream outfile = File.OpenWrite(outputFileName);

                byte[] inbuf = new byte[AUDIO_INBUF_SIZE + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE];
                fixed (byte* ptr = &inbuf[0])
                {
                    byte* data = ptr;
                    int data_size = f.Read(inbuf, 0, AUDIO_INBUF_SIZE);

                    while (data_size > 0)
                    {
                        if (decoded_frame == null)
                        {
                            decoded_frame = ffmpeg.av_frame_alloc();
                            if (decoded_frame == null)
                            {
                                Console.WriteLine("Could not allocate audio frame");
                                break;
                            }
                        }

                        ret = ffmpeg.av_parser_parse2(parser, c, &packet->data, &packet->size,
                            data, data_size, ffmpeg.AV_NOPTS_VALUE, ffmpeg.AV_NOPTS_VALUE, 0);

                        if (ret < 0)
                        {
                            Console.WriteLine("Error while parsing");
                            break;
                        }

                        data += ret;
                        data_size -= ret;

                        if (packet->size != 0)
                        {
                            if (decode(c, packet, decoded_frame, outfile) == false)
                            {
                                break;
                            }
                        }

                        if (data_size < AUDIO_REFILL_THRESH)
                        {
                            fixed (byte* temp_ptr = &inbuf[0])
                            {
                                NativeMethods.MoveMemory(temp_ptr, data, data_size);
                                data = temp_ptr;

                                int len = f.Read(inbuf, data_size, AUDIO_INBUF_SIZE - data_size);
                                if (len > 0)
                                {
                                    data_size += len;
                                }
                            }
                        }
                    }
                }

                packet->data = null;
                packet->size = 0;

                if (decode(c, packet, decoded_frame, outfile) == false)
                {
                    break;
                }

                AVSampleFormat sfmt = c->sample_fmt;

                if (ffmpeg.av_sample_fmt_is_planar(sfmt) != 0)
                {
                    string packed = ffmpeg.av_get_sample_fmt_name(sfmt);
                    Console.WriteLine($"Warning: the sample format the decoder produced is planar {0}. This example will output the first channel only.",
                        packed == null ? "?" : packed);
                    sfmt = ffmpeg.av_get_packed_sample_fmt(sfmt);
                }

                int n_channels = c->channels;
                string? fmt = get_format_from_sample_fmt(sfmt);
                if (fmt == null)
                {
                    break;
                }

                Console.WriteLine("Play the output audio file with the command:\n" +
                    $"ffplay -f {fmt} -ac {n_channels} -ar {c->sample_rate} {outputFileName}\n");

            } while (false);

            if (c != null)
            {
                ffmpeg.avcodec_free_context(&c);
            }

            if (parser != null)
            {
                ffmpeg.av_parser_close(parser);
            }

            if (decoded_frame != null)
            {
                ffmpeg.av_frame_free(&decoded_frame);
            }

            if (packet != null)
            {
                ffmpeg.av_packet_free(&packet);
            }
        }

        private unsafe static bool decode(AVCodecContext* dec_ctx, AVPacket* packet, AVFrame* frame, FileStream outfile)
        {
            int i;
            int ch;
            int ret;
            int data_size;

            ret = ffmpeg.avcodec_send_packet(dec_ctx, packet);
            if (ret < 0)
            {
                Console.WriteLine("Error submitting the packet to the decoder");
                return false;
            }

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_frame(dec_ctx, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    return true;
                }
                else if (ret < 0)
                {
                    Console.WriteLine("Error during decoding");
                    return false;
                }

                data_size = ffmpeg.av_get_bytes_per_sample(dec_ctx->sample_fmt);
                if (data_size < 0)
                {
                    Console.WriteLine("Failed to calculate data size");
                    return false;
                }

                for (i = 0; i < frame->nb_samples; i++)
                {
                    for (ch = 0; ch < dec_ctx->channels; ch++)
                    {
                        byte* ptr = frame->data[0];
                        ptr += (data_size * i);

                        ReadOnlySpan<byte> data = new ReadOnlySpan<byte>(ptr, data_size);
                        outfile.Write(data);
                    }
                }
            }

            return true;
        }

        static unsafe string? get_format_from_sample_fmt(AVSampleFormat sample_fmt)
        {
            sample_fmt_entry[] sample_fmt_entries = {
                new sample_fmt_entry(AVSampleFormat.AV_SAMPLE_FMT_U8, "u8", "u8"),
                new sample_fmt_entry(AVSampleFormat.AV_SAMPLE_FMT_S16, "s16be", "s16le"),
                new sample_fmt_entry(AVSampleFormat.AV_SAMPLE_FMT_S32, "s32be", "s32le"),
                new sample_fmt_entry(AVSampleFormat.AV_SAMPLE_FMT_FLT, "f32be", "f32le"),
                new sample_fmt_entry(AVSampleFormat.AV_SAMPLE_FMT_DBL, "f64be", "f64le"),
            };

            foreach (var entry in sample_fmt_entries)
            {
                if (entry.SampleFormat == sample_fmt)
                {
                    if (BitConverter.IsLittleEndian == true)
                    {
                        return entry.FormatLE;
                    }

                    return entry.FoamtBE;
                }
            }

            Console.WriteLine($"sample format {ffmpeg.av_get_sample_fmt_name(sample_fmt)} is not supported as output format");
            return null;
        }
    }

    struct sample_fmt_entry
    {
        AVSampleFormat sample_fmt;
        public AVSampleFormat SampleFormat => sample_fmt;

        string fmt_be;
        public string FoamtBE => fmt_be;

        string fmt_le;
        public string FormatLE => fmt_le;

        public sample_fmt_entry(AVSampleFormat fmt, string be, string le)
        {
            sample_fmt = fmt;
            fmt_be = be;
            fmt_le = le;
        }
    }

}
