using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.IO;

namespace demuxing_decoding
{
    internal unsafe class Program
    {
        static AVFormatContext* fmt_ctx = null;
        static AVCodecContext* video_dec_ctx = null;
        static AVCodecContext* audio_dec_ctx = null;
        static int width, height;
        static AVPixelFormat pix_fmt;
        static AVStream* video_stream;
        static AVStream* audio_stream;
        static string src_filename = "";
        static string video_dst_filename = "";
        static string audio_dst_filename = "";
        static FileStream? video_dst_file;
        static FileStream? audio_dst_file;

        static byte_ptrArray4 video_dst_data;
        static int_array4 video_dst_linesize;
        static int video_dst_bufsize;

        static int video_stream_idx = -1;
        static int audio_stream_idx = -1;
        static AVFrame* frame = null;
        static AVPacket* pkt = null;
        static int video_Frame_count = 0;
        static int audio_frame_count = 0;

        static unsafe int output_video_frame(AVFrame* frame)
        {
            if (frame->width != width || frame->height != height || frame->format != (int)pix_fmt)
            {
                Console.WriteLine("Error: Width, height and pixel format have to be constant in an rawvideo file, but the width, height or pixel format of the input video changed:\n" +
                    $"old: width = {width}, height = {height}, format = {ffmpeg.av_get_pix_fmt_name(pix_fmt)}\n" +
                    $"new: width = {frame->width}, height = {frame->height}, format = {ffmpeg.av_get_pix_fmt_name((AVPixelFormat)frame->format)}");
                return -1;
            }

            Console.WriteLine($"video_frame n: {video_Frame_count++} coded: {frame->coded_picture_number}\n");

            byte_ptrArray4 tempData = new byte_ptrArray4();
            tempData.UpdateFrom(frame->data);

            int_array4 tempLinesize = new int_array4();
            tempLinesize.UpdateFrom(frame->linesize);
            ffmpeg.av_image_copy(ref video_dst_data, ref video_dst_linesize, ref tempData, tempLinesize, pix_fmt, width, height);

            video_dst_file?.Write(new ReadOnlySpan<byte>(video_dst_data[0], video_dst_bufsize));
            return 0;
        }

        static int output_audio_frame(AVFrame* frame)
        {
            int unpadded_linesize = frame->nb_samples * ffmpeg.av_get_bytes_per_sample((AVSampleFormat)frame->format);
            Console.WriteLine($"audio_frame n: {audio_frame_count++} nb_samples: {frame->nb_samples} pts: {FFmpegHelper.av_ts2timestr(frame->pts, in audio_dec_ctx->time_base)}");

            audio_dst_file?.Write(new ReadOnlySpan<byte>(frame->extended_data[0], unpadded_linesize));

            return 0;
        }

        static unsafe int decode_packet(AVCodecContext* dec, AVPacket* pkt)
        {
            int ret = ffmpeg.avcodec_send_packet(dec, pkt);
            if (ret < 0)
            {
                Console.WriteLine($"Error submitting a packet for decoding {ret}");
                return ret;
            }

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_frame(dec, frame);
                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR(ffmpeg.AVERROR_EOF) || ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        return 0;
                    }

                    Console.WriteLine($"Error during decoding ({FFmpegHelper.av_err2str(ret)})");
                    return ret;
                }

                if (dec->codec->type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    ret = output_video_frame(frame);
                }
                else
                {
                    ret = output_audio_frame(frame);
                }

                ffmpeg.av_frame_unref(frame);

                if (ret < 0)
                {
                    return ret;
                }
            }

            return 0;
        }

        static unsafe int open_codec_context(int* stream_idx, AVCodecContext** dec_ctx, AVFormatContext* fmt_ctx, AVMediaType type)
        {
            int ret, stream_index;
            AVStream* st;
            AVCodec* dec = null;

            ret = ffmpeg.av_find_best_stream(fmt_ctx, type, -1, -1, null, 0);
            if (ret < 0)
            {
                Console.WriteLine($"Could not find {ffmpeg.av_get_media_type_string(type)} stream in input file '{src_filename}'");
                return ret;
            }
            else
            {
                stream_index = ret;
                st = fmt_ctx->streams[stream_index];

                dec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
                if (dec == null)
                {
                    Console.WriteLine($"Failed to find {ffmpeg.av_get_media_type_string(type)} codec");
                    return ffmpeg.AVERROR(ffmpeg.EINVAL);
                }

                *dec_ctx = ffmpeg.avcodec_alloc_context3(dec);
                if (*dec_ctx == null)
                {
                    Console.WriteLine($"Failed to allocate the {ffmpeg.av_get_media_type_string(type)} codec context");
                    return ffmpeg.AVERROR(ffmpeg.ENOMEM);
                }

                if ((ret = ffmpeg.avcodec_parameters_to_context(*dec_ctx, st->codecpar)) < 0)
                {
                    Console.WriteLine($"Failed to copy {ffmpeg.av_get_media_type_string(type)} codec parameters to decoder context");
                    return ret;
                }

                if ((ret = ffmpeg.avcodec_open2(*dec_ctx, dec, null)) < 0)
                {
                    Console.WriteLine($"Failed to open {ffmpeg.av_get_media_type_string(type)} codec");
                    return ret;
                }

                *stream_idx = stream_index;
            }

            return 0;
        }

        static unsafe int Main(string[] args)
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
#endif
            Console.WriteLine();

            int ret = 0;

            // https://samplelib.com/sample-mp4.html
            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            src_filename = Path.Combine(dirPath, "..", "..", "..", "..", "Samples", "sample-10s.mp4");

            video_dst_filename = Path.Combine(dirPath, "video.out");
            audio_dst_filename = Path.Combine(dirPath, "audio.out");

            fixed (AVFormatContext** pfmt_ctx = &fmt_ctx)
            fixed (AVCodecContext** pvideo_dec_ctx = &video_dec_ctx)
            fixed (AVCodecContext** paudio_dec_ctx = &audio_dec_ctx)
            fixed (int* pvideo_stream_index = &video_stream_idx)
            fixed (int* paudio_stream_index = &audio_stream_idx)
            fixed (AVPacket** ppkt = &pkt)
            fixed (AVFrame** pframe = &frame)
            {
                if (ffmpeg.avformat_open_input(pfmt_ctx, src_filename, null, null) < 0)
                {
                    Console.WriteLine($"Could not open source file {src_filename}");
                    return 1;
                }

                if (ffmpeg.avformat_find_stream_info(fmt_ctx, null) < 0)
                {
                    Console.WriteLine($"Could not find stream information");
                    return 1;
                }

                if (open_codec_context(pvideo_stream_index, pvideo_dec_ctx, fmt_ctx, AVMediaType.AVMEDIA_TYPE_VIDEO) >= 0)
                {
                    video_stream = fmt_ctx->streams[video_stream_idx];

                    video_dst_file = File.OpenWrite(video_dst_filename);
                    if (video_dst_file == null)
                    {
                        Console.WriteLine($"Could not open destination file {video_dst_filename}");
                        ret = 1;
                        goto end;
                    }

                    width = video_dec_ctx->width;
                    height = video_dec_ctx->height;
                    pix_fmt = video_dec_ctx->pix_fmt;
                    ret = ffmpeg.av_image_alloc(ref video_dst_data, ref video_dst_linesize, width, height, pix_fmt, 1);
                    if (ret < 0)
                    {
                        Console.WriteLine("Could not allocate raw video buffer");
                        goto end;
                    }

                    video_dst_bufsize = ret;
                }

                if (open_codec_context(paudio_stream_index, paudio_dec_ctx, fmt_ctx, AVMediaType.AVMEDIA_TYPE_AUDIO) >= 0)
                {
                    audio_stream = fmt_ctx->streams[audio_stream_idx];
                    audio_dst_file = File.OpenWrite(audio_dst_filename);
                    if (audio_dst_file == null)
                    {
                        Console.WriteLine($"Could not open destination file {audio_dst_filename}");
                        ret = 1;
                        goto end;
                    }
                }

                ffmpeg.av_dump_format(fmt_ctx, 0, src_filename, 0);

                if (audio_stream == null && video_stream == null)
                {
                    Console.WriteLine("Could not find audio or video stream in the input, aborting");
                    ret = 1;
                    goto end;
                }

                frame = ffmpeg.av_frame_alloc();
                if (frame == null)
                {
                    Console.WriteLine("Could not allocate frame");
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    goto end;
                }

                pkt = ffmpeg.av_packet_alloc();
                if (pkt == null)
                {
                    Console.WriteLine("Could not allocate packet");
                    ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                    goto end;
                }

                if (video_stream != null)
                {
                    Console.WriteLine($"Demuxing video from file '{src_filename}' into '{video_dst_filename}'");
                }

                if (audio_stream != null)
                {
                    Console.WriteLine($"Demuxing audio from file '{src_filename}' into '{audio_dst_filename}'");
                }

                while (ffmpeg.av_read_frame(fmt_ctx, pkt) >= 0)
                {
                    if (pkt->stream_index == video_stream_idx)
                    {
                        ret = decode_packet(video_dec_ctx, pkt);
                    }
                    else if (pkt->stream_index == audio_stream_idx)
                    {
                        ret = decode_packet(audio_dec_ctx, pkt);
                    }

                    ffmpeg.av_packet_unref(pkt);
                    if (ret < 0)
                    {
                        break;
                    }
                }

                if (video_dec_ctx != null)
                {
                    decode_packet(video_dec_ctx, null);
                }

                if (audio_dec_ctx != null)
                {
                    decode_packet(audio_dec_ctx, null);
                }

                Console.WriteLine("Demuxing succeeded.");

                if (video_stream != null)
                {
                    Console.WriteLine("Play the output video file with the command: \n" +
                        $"ffplay -autoexit -f rawvideo -pix_fmt {ffmpeg.av_get_pix_fmt_name(pix_fmt)} -video_size {width}x{height} {video_dst_filename}");
                }

                if (audio_stream != null)
                {
                    AVSampleFormat sfmt = audio_dec_ctx->sample_fmt;
                    int n_channels = audio_dec_ctx->channels;
                    string fmt;

                    if (ffmpeg.av_sample_fmt_is_planar(sfmt) == 1)
                    {
                        string packed = ffmpeg.av_get_sample_fmt_name(sfmt);
                        Console.WriteLine("Warning: the sample format the decoder produced is planar " +
                            $"({(packed != null ? packed : "?")}). This example will output the first channel only.");
                        sfmt = ffmpeg.av_get_packed_sample_fmt(sfmt);
                        n_channels = 1;
                    }

                    if ((ret = FFmpegHelper.get_format_from_sample_fmt(out fmt, sfmt)) < 0)
                    {
                        goto end;
                    }

                    Console.WriteLine("Play the output audio file with the command:\n" +
                        $"ffplay -autoexit -f {fmt} -ac {n_channels} -ar {audio_dec_ctx->sample_rate} {audio_dst_filename}");
                }

            end:

                ffmpeg.avcodec_free_context(pvideo_dec_ctx);
                ffmpeg.avcodec_free_context(paudio_dec_ctx);
                ffmpeg.avformat_close_input(pfmt_ctx);

                if (video_dst_file != null)
                {
                    video_dst_file.Close();
                }

                if (audio_dst_file != null)
                {
                    audio_dst_file.Close();
                }

                ffmpeg.av_packet_free(ppkt);
                ffmpeg.av_frame_free(pframe);
                ffmpeg.av_free(video_dst_data[0]);

            }

            return 0;
        }
    }
}
