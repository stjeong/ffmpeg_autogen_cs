using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;
using System.Text;

namespace decode_video
{
    internal class Program
    {
        const int INBUF_SIZE = 4096;

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
            string src_filename = Path.Combine(dirPath, "..", "..", "..", "..", "Samples", "mpeg1video_q0.m1v");

            video_decode_example(src_filename, AVCodecID.AV_CODEC_ID_MPEG1VIDEO, dirPath);
        }

        static unsafe void video_decode_example(string filename, AVCodecID codec_id, string outfileDirPath)
        {
            AVCodec* _pCodec = null;
            AVCodecParserContext* _parser = null;
            AVCodecContext* _pCodecContext = null;
            AVFrame* frame = null;
            AVPacket* pkt = null;
            byte[] inbuf = new byte[INBUF_SIZE + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE];
            int ret = 0;

            using BinaryReader inputFile = new BinaryReader(new FileStream(filename, FileMode.Open));

            do
            {
                pkt = ffmpeg.av_packet_alloc();
                if (pkt == null)
                {
                    break;
                }

                /* find the MPEG-1 video decoder */
                Console.WriteLine($"Decode video file {filename}");
                _pCodec = ffmpeg.avcodec_find_decoder(codec_id);

                if (_pCodec == null)
                {
                    Console.WriteLine($"Codec not found: {codec_id}");
                    break;
                }

                _parser = ffmpeg.av_parser_init((int)_pCodec->id);


                _pCodecContext = ffmpeg.avcodec_alloc_context3(_pCodec);
                if (_pCodecContext == null)
                {
                    Console.WriteLine($"Could not allocate video codec context: {codec_id}");
                    break;
                }

                bool headerRead = false;

                /* open it */
                if (ffmpeg.avcodec_open2(_pCodecContext, _pCodec, null) < 0)
                {
                    Console.WriteLine("Could not open codec");
                    break;
                }

                frame = ffmpeg.av_frame_alloc();

                if (frame == null)
                {
                    Console.WriteLine("Could not allocate video frame");
                    break;
                }

                bool parse_succeed = true;

                while (parse_succeed)
                {
                    int data_size = inputFile.Read(inbuf, 0, INBUF_SIZE);

                    if (data_size == 0)
                    {
                        break;
                    }

                    fixed (byte* ptr = inbuf)
                    {
                        byte* data = ptr;

                        while (data_size > 0)
                        {
                            ret = ffmpeg.av_parser_parse2(_parser, _pCodecContext,
                                &pkt->data, &pkt->size, data, data_size, ffmpeg.AV_NOPTS_VALUE, ffmpeg.AV_NOPTS_VALUE, 0);

                            if (ret < 0)
                            {
                                break;
                            }

                            if (headerRead == false && _pCodecContext->pix_fmt != AVPixelFormat.AV_PIX_FMT_NONE)
                            {
                                Console.WriteLine();
                                Console.WriteLine($"width: {_pCodecContext->width}");
                                Console.WriteLine($"height: {_pCodecContext->height}");
                                Console.WriteLine($"time_base num: {_pCodecContext->time_base.num}, den: {_pCodecContext->time_base.den}");
                                Console.WriteLine($"framerate num: {_pCodecContext->framerate.num}, den: {_pCodecContext->framerate.den}");
                                Console.WriteLine($"gop_size: {_pCodecContext->gop_size}");
                                Console.WriteLine($"max_b_frames: {_pCodecContext->max_b_frames}");
                                Console.WriteLine($"pix_fmt: {_pCodecContext->pix_fmt}");
                                Console.WriteLine($"bit_rate: {_pCodecContext->bit_rate}");
                                Console.WriteLine();
                                headerRead = true;
                            }

                            data += ret;
                            data_size -= ret;

                            if (pkt->size != 0)
                            {
                                parse_succeed = decode(_pCodecContext, frame, pkt, outfileDirPath);
                                if (parse_succeed == false)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }

                // flush the decoder
                decode(_pCodecContext, frame, null, outfileDirPath);

            } while (false);

            if (_parser != null)
            {
                ffmpeg.av_parser_close(_parser);
            }

            if (_pCodecContext != null)
            {
                ffmpeg.avcodec_free_context(&_pCodecContext);
            }

            if (frame != null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (pkt != null)
            {
                ffmpeg.av_packet_free(&pkt);
            }
        }

        private static unsafe bool decode(AVCodecContext* pCodecContext, AVFrame* frame, AVPacket* pkt, string outfileDirPath)
        {
            int ret = ffmpeg.avcodec_send_packet(pCodecContext, pkt);
            if (ret < 0)
            {
                Console.WriteLine("Error sending a packet for decoding");
                return false;
            }

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_frame(pCodecContext, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    return true;
                }
                else if (ret < 0)
                {
                    Console.WriteLine("Error during decoding");
                    return false;
                }

                Console.WriteLine($"saving frame {pCodecContext->frame_number}");

                string outputFile = Path.Combine(outfileDirPath, "noname_" + pCodecContext->frame_number + ".pgm");
                FFmpegHelper.pgm_save(frame->data[0], frame->linesize[0], frame->width, frame->height, outputFile);
            }

            return true;
        }
    }
}
