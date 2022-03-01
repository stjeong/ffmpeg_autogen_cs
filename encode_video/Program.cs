using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;

namespace encode_video
{
    internal class Program
    {
        static void Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
#endif
            Console.WriteLine();

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string filename = Path.Combine(dirPath, "test.mp4");

            video_encode_example(filename, AVCodecID.AV_CODEC_ID_H264);
        }

        static unsafe void encode(AVCodecContext* enc_ctx, AVFrame* frame, AVPacket* pkt, BinaryWriter output)
        {
            int ret;

            /* send the frame to the encoder */
            if (frame != null)
            {
                Console.WriteLine($"Send frame {frame->pts}");
            }

            ret = ffmpeg.avcodec_send_frame(enc_ctx, frame);
            if (ret < 0)
            {
                Console.WriteLine("Error sending a frame for encoding");
                return;
            }

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_packet(enc_ctx, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    return;
                }
                else if (ret < 0)
                {
                    Console.WriteLine("Error during encoding");
                    return;
                }

                Console.WriteLine($"Write packet {pkt->pts} (size={pkt->size})");
                output.Write(new ReadOnlySpan<byte>(pkt->data, pkt->size));

                ffmpeg.av_packet_unref(pkt);
            }
        }

        static unsafe void video_encode_example(string filename, AVCodecID codec_id)
        {
            AVCodec* _pCodec = null;
            AVCodecContext* _pCodecContext = null;
            AVFrame* frame = null;
            AVPacket* pkt = null;
            byte[] end_code = new byte[] { 0, 0, 1, 0xb7 };

            using BinaryWriter output = new BinaryWriter(new FileStream(filename, FileMode.Create));

            do
            {
                Console.WriteLine($"Encode video file {filename}");
                _pCodec = ffmpeg.avcodec_find_encoder(codec_id);

                if (_pCodec == null)
                {
                    Console.WriteLine($"Codec not found: {codec_id}");
                    break;
                }

                _pCodecContext = ffmpeg.avcodec_alloc_context3(_pCodec);
                if (_pCodecContext == null)
                {
                    Console.WriteLine($"Could not allocate video codec context: {codec_id}");
                    break;
                }

                pkt = ffmpeg.av_packet_alloc();
                if (pkt == null)
                {
                    break;
                }

                _pCodecContext->bit_rate = 400000;
                _pCodecContext->width = 352;
                _pCodecContext->height = 288;
                /* frames per second */
                _pCodecContext->time_base = new AVRational { num = 1, den = 25 };
                _pCodecContext->framerate = new AVRational { num = 25, den = 1 };

                _pCodecContext->gop_size = 10; /* emit one intra frame every ten frames */
                _pCodecContext->max_b_frames = 1;
                _pCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

                if (codec_id == AVCodecID.AV_CODEC_ID_H264)
                {
                    ffmpeg.av_opt_set(_pCodecContext->priv_data, "preset", "slow", 0);
                }

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

                frame->format = (int)_pCodecContext->pix_fmt;
                frame->width = _pCodecContext->width;
                frame->height = _pCodecContext->height;

                int ret = ffmpeg.av_frame_get_buffer(frame, 0);
                if (ret < 0)
                {
                    Console.WriteLine("Could not allocate raw picture buffer");
                    break;
                }

                int x = 0;
                int y = 0;

                for (int i = 0; i < 25 * 10; i++)
                {
                    ret = ffmpeg.av_frame_make_writable(frame);
                    if (ret < 0)
                    {
                        break;
                    }

                    for (y = 0; y < _pCodecContext->height; y++)
                    {
                        for (x = 0; x < _pCodecContext->width; x++)
                        {
                            frame->data[0][y * frame->linesize[0] + x] = (byte)(x + y + i * 3);
                        }
                    }

                    /* Cb and Cr */
                    for (y = 0; y < _pCodecContext->height / 2; y++)
                    {
                        for (x = 0; x < _pCodecContext->width / 2; x++)
                        {
                            frame->data[1][y * frame->linesize[1] + x] = (byte)(128 + y + i * 2);
                            frame->data[2][y * frame->linesize[2] + x] = (byte)(64 + x + i * 5);
                        }
                    }

                    frame->pts = i;

                    encode(_pCodecContext, frame, pkt, output);
                }

                // flush the encoder
                encode(_pCodecContext, null, pkt, output);

            } while (false);

            if (_pCodec->id == AVCodecID.AV_CODEC_ID_MPEG1VIDEO || _pCodec->id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO)
            {
                output.Write(end_code, 0, end_code.Length);
            }

            if (frame != null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (pkt != null)
            {
                ffmpeg.av_packet_free(&pkt);
            }

            if (_pCodecContext != null)
            {
                ffmpeg.avcodec_free_context(&_pCodecContext);
            }
        }
    }
}
