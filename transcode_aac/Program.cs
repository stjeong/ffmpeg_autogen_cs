using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.Diagnostics;
using System.IO;

namespace transcode_aac
{
    internal unsafe class Program
    {
        public const int OUTPUT_BIT_RATE = 96000;
        public const int OUTPUT_CHANNELS = 2;

        static long pts = 0;

        static unsafe int open_input_file(string filename, AVFormatContext** input_format_context,
            AVCodecContext** input_codec_context)
        {
            AVCodecContext* avctx;
            AVCodec* input_codec;
            int error;

            if ((error = ffmpeg.avformat_open_input(input_format_context, filename, null, null)) < 0)
            {
                Console.WriteLine($"Could not open input file '{filename}' (error '{FFmpegHelper.av_err2str(error)}')");
                *input_format_context = null;
                return error;
            }

            if ((error = ffmpeg.avformat_find_stream_info(*input_format_context, null)) < 0)
            {
                Console.WriteLine($"Could not find stream info (error '{FFmpegHelper.av_err2str(error)}')");
                ffmpeg.avformat_close_input(input_format_context);
                return error;
            }

            uint number_of_streams = (*input_format_context)->nb_streams;
            if (number_of_streams != 1)
            {
                Console.WriteLine($"Expected one audio input stream, but found {number_of_streams}");
                ffmpeg.avformat_close_input(input_format_context);
                return ffmpeg.AVERROR_EXIT;
            }

            AVCodecParameters* codecpar = (*input_format_context)->streams[0]->codecpar;
            if ((input_codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id)) == null)
            {
                Console.WriteLine("Could not find input codec");
                ffmpeg.avformat_close_input(input_format_context);
                return ffmpeg.AVERROR_EXIT;
            }

            avctx = ffmpeg.avcodec_alloc_context3(input_codec);
            if (avctx == null)
            {
                Console.WriteLine("Could not allocate a decoding context");
                ffmpeg.avformat_close_input(input_format_context);
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            error = ffmpeg.avcodec_parameters_to_context(avctx, codecpar);
            if (error < 0)
            {
                ffmpeg.avformat_close_input(input_format_context);
                ffmpeg.avcodec_free_context(&avctx);
                return error;
            }

            if ((error = ffmpeg.avcodec_open2(avctx, input_codec, null)) < 0)
            {
                Console.WriteLine($"Could not open input codec (error '{FFmpegHelper.av_err2str(error)}')");
                ffmpeg.avcodec_free_context(&avctx);
                ffmpeg.avformat_close_input(input_format_context);
                return error;
            }

            *input_codec_context = avctx;
            return 0;
        }

        static unsafe int open_output_file(string filename, AVCodecContext* input_codec_context, AVFormatContext** output_format_context,
            AVCodecContext** output_codec_context)
        {
            AVCodecContext* avctx = null;
            AVIOContext* output_io_context = null;
            AVStream* stream = null;
            AVCodec* output_codec = null;
            int error = 0;

            if ((error = ffmpeg.avio_open(&output_io_context, filename, ffmpeg.AVIO_FLAG_WRITE)) < 0)
            {
                Console.WriteLine($"Could not open output file '{filename}' (error '{FFmpegHelper.av_err2str(error)}')");
                return error;
            }

            if ((*output_format_context = ffmpeg.avformat_alloc_context()) == null)
            {
                Console.WriteLine("Could not allocate output format context");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            (*output_format_context)->pb = output_io_context;

            if (((*output_format_context)->oformat = ffmpeg.av_guess_format(null, filename, null)) == null)
            {
                Console.WriteLine("Could not find output file format");
                goto cleanup;
            }

            if (((*output_format_context)->url = ffmpeg.av_strdup(filename)) == null)
            {
                Console.WriteLine("Could not allocate url.");
                error = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto cleanup;
            }

            if ((output_codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC)) == null)
            {
                Console.WriteLine("Could not find an AAC encoder");
                goto cleanup;
            }

            if ((stream = ffmpeg.avformat_new_stream(*output_format_context, null)) == null)
            {
                Console.WriteLine("Could not create new stream");
                error = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto cleanup;
            }

            avctx = ffmpeg.avcodec_alloc_context3(output_codec);
            if (avctx == null)
            {
                Console.WriteLine("Could not allocate an encoding context");
                error = ffmpeg.AVERROR(ffmpeg.ENOMEM);
                goto cleanup;
            }

            avctx->channels = OUTPUT_CHANNELS;
            avctx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(OUTPUT_CHANNELS);
            avctx->sample_rate = input_codec_context->sample_rate;
            avctx->sample_fmt = output_codec->sample_fmts[0];
            avctx->bit_rate = OUTPUT_BIT_RATE;

            avctx->strict_std_compliance = ffmpeg.FF_COMPLIANCE_EXPERIMENTAL;

            stream->time_base.den = input_codec_context->sample_rate;
            stream->time_base.num = 1;

            if (((*output_format_context)->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER)
            {
                avctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            if ((error = ffmpeg.avcodec_open2(avctx, output_codec, null)) < 0)
            {
                Console.WriteLine($"Could not open output codec (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }

            error = ffmpeg.avcodec_parameters_from_context(stream->codecpar, avctx);
            if (error < 0)
            {
                Console.WriteLine("Could not initialize stream parameters");
                goto cleanup;
            }

            *output_codec_context = avctx;
            return 0;

        cleanup:

            ffmpeg.avcodec_free_context(&avctx);
            ffmpeg.avio_closep(&(*output_format_context)->pb);
            ffmpeg.avformat_free_context(*output_format_context);
            *output_format_context = null;

            return error < 0 ? error : ffmpeg.AVERROR_EXIT;
        }

        static unsafe int init_packet(AVPacket** packet)
        {
            if ((*packet = ffmpeg.av_packet_alloc()) == null)
            {
                Console.WriteLine("Could not allocate packet");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            return 0;
        }

        static unsafe int init_input_frame(AVFrame** frame)
        {
            if ((*frame = ffmpeg.av_frame_alloc()) == null)
            {
                Console.WriteLine("Could not allocate input frame");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            return 0;
        }

        static unsafe int init_resampler(AVCodecContext* input_codec_context, AVCodecContext* output_codec_context,
            SwrContext** resample_context)
        {
            int error = 0;

            *resample_context = ffmpeg.swr_alloc_set_opts(null,
                ffmpeg.av_get_default_channel_layout(output_codec_context->channels),
                output_codec_context->sample_fmt,
                output_codec_context->sample_rate,
                ffmpeg.av_get_default_channel_layout(input_codec_context->channels),
                input_codec_context->sample_fmt,
                input_codec_context->sample_rate,
                0, null);

            if (*resample_context == null)
            {
                Console.WriteLine("Could not allocate resample context");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            Trace.Assert(output_codec_context->sample_rate == input_codec_context->sample_rate);

            if ((error = ffmpeg.swr_init(*resample_context)) < 0)
            {
                Console.WriteLine("Could not open resample context");
                ffmpeg.swr_free(resample_context);
                return error;
            }

            return 0;
        }

        static unsafe int init_fifo(AVAudioFifo** fifo, AVCodecContext* output_codec_context)
        {
            if ((*fifo = ffmpeg.av_audio_fifo_alloc(output_codec_context->sample_fmt, output_codec_context->channels, 1)) == null)
            {
                Console.WriteLine("Could not allocate FIFO");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            return 0;
        }

        static unsafe int write_output_file_header(AVFormatContext* output_format_context)
        {
            int error = ffmpeg.avformat_write_header(output_format_context, null);

            if (error < 0)
            {
                Console.WriteLine($"Could not write output file header (error '{FFmpegHelper.av_err2str(error)}'");
                return error;
            }

            return 0;
        }

        static unsafe int decode_audio_frame(AVFrame* frame, AVFormatContext* input_format_context, AVCodecContext* input_codec_context,
            int* data_present, int* finished)
        {
            AVPacket* input_packet;
            int error = init_packet(&input_packet);
            if (error < 0)
            {
                return error;
            }

            if ((error = ffmpeg.av_read_frame(input_format_context, input_packet)) < 0)
            {
                if (error == ffmpeg.AVERROR_EOF)
                {
                    *finished = 1;
                }
                else
                {
                    Console.WriteLine($"Could not raed frame (eror '{FFmpegHelper.av_err2str(error)}')");
                    goto cleanup;
                }
            }

            if ((error = ffmpeg.avcodec_send_packet(input_codec_context, input_packet)) < 0)
            {
                Console.WriteLine($"Could not send packet for decoding (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }

            error = ffmpeg.avcodec_receive_frame(input_codec_context, frame);
            if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                error = 0;
                goto cleanup;
            }
            else if (error == ffmpeg.AVERROR_EOF)
            {
                *finished = 1;
                error = 0;
                goto cleanup;
            }
            else if (error < 0)
            {
                Console.WriteLine($"Could not decode frame (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }
            else
            {
                *data_present = 1;
                goto cleanup;
            }

        cleanup:
            ffmpeg.av_packet_free(&input_packet);
            return error;
        }

        static unsafe int init_converted_samples(byte*** converted_input_samples, AVCodecContext* output_codec_context, int frame_size)
        {
            if ((*converted_input_samples = (byte**)ffmpeg.av_calloc((ulong)output_codec_context->channels, (ulong)IntPtr.Size)) == null)
            {
                Console.WriteLine("Could not allocate converted input sample pointers");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            int error = ffmpeg.av_samples_alloc(*converted_input_samples, null, output_codec_context->channels, frame_size, output_codec_context->sample_fmt, 0);
            if (error < 0)
            {
                Console.WriteLine($"Could not allocate converted input samples (error '{FFmpegHelper.av_err2str(error)}')");
                ffmpeg.av_freep(&(*converted_input_samples)[0]);
                ffmpeg.av_free(*converted_input_samples);
                return error;
            }

            return 0;
        }

        static unsafe int converted_samples(byte** input_data, byte** converted_data, int frame_size, SwrContext* resample_context)
        {
            int error = ffmpeg.swr_convert(resample_context, converted_data, frame_size, input_data, frame_size);
            if (error < 0)
            {
                Console.WriteLine($"Could not convert input samples (error '{FFmpegHelper.av_err2str(error)}')");
                return error;
            }

            return 0;
        }

        static unsafe int add_samples_to_fifo(AVAudioFifo* fifo, byte** converted_input_samples, int frame_size)
        {
            int error = ffmpeg.av_audio_fifo_realloc(fifo, ffmpeg.av_audio_fifo_size(fifo) + frame_size);
            if (error < 0)
            {
                Console.WriteLine("Could not reallocate FIFO");
                return error;
            }

            if (ffmpeg.av_audio_fifo_write(fifo, (void**)converted_input_samples, frame_size) < frame_size)
            {
                Console.WriteLine("Could not write data to FIFO");
                return ffmpeg.AVERROR_EXIT;
            }

            return 0;
        }

        static unsafe int read_decode_convert_and_store(AVAudioFifo* fifo,
            AVFormatContext* input_format_context, AVCodecContext* input_codec_context,
            AVCodecContext* output_codec_context, SwrContext* resampler_context, int* finished)
        {
            AVFrame* input_frame = null;
            byte** converted_input_samples = null;
            int data_present = 0;
            int ret = ffmpeg.AVERROR_EXIT;

            if (init_input_frame(&input_frame) != 0)
            {
                goto cleanup;
            }

            if (decode_audio_frame(input_frame, input_format_context, input_codec_context, &data_present, finished) != 0)
            {
                goto cleanup;
            }

            if (*finished != 0)
            {
                ret = 0;
                goto cleanup;
            }

            if (data_present != 0)
            {
                if (init_converted_samples(&converted_input_samples, output_codec_context, input_frame->nb_samples) != 0)
                {
                    goto cleanup;
                }

                if (converted_samples(input_frame->extended_data, converted_input_samples, input_frame->nb_samples, resampler_context) != 0)
                {
                    goto cleanup;
                }

                if (add_samples_to_fifo(fifo, converted_input_samples, input_frame->nb_samples) != 0)
                {
                    goto cleanup;
                }

                ret = 0;
            }

            ret = 0;

        cleanup:
            if (converted_input_samples != null)
            {
                ffmpeg.av_freep(&converted_input_samples[0]);
                ffmpeg.av_free(converted_input_samples);
            }

            ffmpeg.av_frame_free(&input_frame);

            return ret;
        }

        static unsafe int init_output_frame(AVFrame** frame, AVCodecContext* output_codec_context, int frame_size)
        {
            int error;

            if ((*frame = ffmpeg.av_frame_alloc()) == null)
            {
                Console.WriteLine("Could not allocate output frame");
                return ffmpeg.AVERROR_EXIT;
            }

            (*frame)->nb_samples = frame_size;
            (*frame)->channel_layout = output_codec_context->channel_layout;
            (*frame)->format = (int)output_codec_context->sample_fmt;
            (*frame)->sample_rate = output_codec_context->sample_rate;

            if ((error = ffmpeg.av_frame_get_buffer(*frame, 0)) < 0)
            {
                Console.WriteLine($"Could not allocate output frame samples (error '{FFmpegHelper.av_err2str(error)}')");
                ffmpeg.av_frame_free(frame);
                return error;
            }

            return 0;
        }

        static unsafe int encode_audio_frame(AVFrame* frame, AVFormatContext* output_format_context, AVCodecContext* output_codec_context, int* data_present)
        {
            AVPacket* output_packet;
            int error = init_packet(&output_packet);
            if (error < 0)
            {
                return error;
            }

            if (frame != null)
            {
                frame->pts = pts;
                pts += frame->nb_samples;
            }

            error = ffmpeg.avcodec_send_frame(output_codec_context, frame);
            if (error == ffmpeg.AVERROR_EOF)
            {
                error = 0;
                goto cleanup;
            }
            else if (error < 0)
            {
                Console.WriteLine($"Could not send packet for encoding (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }

            error = ffmpeg.avcodec_receive_packet(output_codec_context, output_packet);
            if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                error = 0;
                goto cleanup;
            }
            else if (error == ffmpeg.AVERROR_EOF)
            {
                error = 0;
                goto cleanup;
            }
            else if (error < 0)
            {
                Console.WriteLine($"Could not encode frame (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }
            else
            {
                *data_present = 1;
            }

            if (*data_present != 0 &&
                (error = ffmpeg.av_write_frame(output_format_context, output_packet)) < 0)
            {
                Console.WriteLine($"Could not write frame (error '{FFmpegHelper.av_err2str(error)}')");
                goto cleanup;
            }


        cleanup:
            ffmpeg.av_packet_free(&output_packet);
            return error;
        }

        static unsafe int load_encode_and_write(AVAudioFifo* fifo, AVFormatContext* output_format_context,
            AVCodecContext* output_codec_context)
        {
            AVFrame* output_frame;
            int frame_size = Math.Min(ffmpeg.av_audio_fifo_size(fifo), output_codec_context->frame_size);
            int data_written;

            if (init_output_frame(&output_frame, output_codec_context, frame_size) != 0)
            {
                return ffmpeg.AVERROR_EXIT;
            }

            void* ptr = &output_frame->data;
            if (ffmpeg.av_audio_fifo_read(fifo, (void**)ptr, frame_size) < frame_size)
            {
                ffmpeg.av_frame_free(&output_frame);
                return ffmpeg.AVERROR_EXIT;
            }

            if (encode_audio_frame(output_frame, output_format_context, output_codec_context, &data_written) != 0)
            {
                ffmpeg.av_frame_free(&output_frame);
                return ffmpeg.AVERROR_EXIT;
            }

            ffmpeg.av_frame_free(&output_frame);
            return 0;
        }

        static unsafe int write_output_file_trailer(AVFormatContext* output_format_context)
        {
            int error = ffmpeg.av_write_trailer(output_format_context);
            if (error < 0)
            {
                Console.WriteLine($"Could not write output file trailer (error '{FFmpegHelper.av_err2str(error)}')");
                return error;
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
            Console.WriteLine();
#endif

            AVFormatContext* input_format_context = null;
            AVFormatContext* output_format_context = null;
            AVCodecContext* input_codec_conetxt = null;
            AVCodecContext* output_codec_conetxt = null;
            SwrContext* resample_context = null;
            AVAudioFifo* fifo = null;
            int ret = ffmpeg.AVERROR_EXIT;

            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";

            // https://samplelib.com/sample-mp3.html
            string input_file_path = Path.Combine(dirPath, "..", "..", "..", "..", "Samples", "sample-12s.mp3");
            string output_file_path = Path.Combine(dirPath, "output.aac");

            if (open_input_file(input_file_path, &input_format_context, &input_codec_conetxt) != 0)
            {
                goto cleanup;
            }

            if (open_output_file(output_file_path, input_codec_conetxt, &output_format_context, &output_codec_conetxt) != 0)
            {
                goto cleanup;
            }

            if (init_resampler(input_codec_conetxt, output_codec_conetxt, &resample_context) != 0)
            {
                goto cleanup;
            }

            if (init_fifo(&fifo, output_codec_conetxt) != 0)
            {
                goto cleanup;
            }

            if (write_output_file_header(output_format_context) != 0)
            {
                goto cleanup;
            }

            while (true)
            {
                int output_frame_size = output_codec_conetxt->frame_size;
                int finished = 0;

                while (ffmpeg.av_audio_fifo_size(fifo) < output_frame_size)
                {
                    if (read_decode_convert_and_store(fifo, input_format_context, input_codec_conetxt, output_codec_conetxt,
                        resample_context, &finished) != 0)
                    {
                        goto cleanup;
                    }

                    if (finished != 0)
                    {
                        break;
                    }
                }

                while (ffmpeg.av_audio_fifo_size(fifo) >= output_frame_size
                    || (finished != 0 && ffmpeg.av_audio_fifo_size(fifo) > 0))
                {
                    if (load_encode_and_write(fifo, output_format_context, output_codec_conetxt) != 0)
                    {
                        goto cleanup;
                    }
                }

                if (finished != 0)
                {
                    int data_written;

                    do
                    {
                        data_written = 0;
                        if (encode_audio_frame(null, output_format_context, output_codec_conetxt, &data_written) != 0)
                        {
                            goto cleanup;
                        }
                    } while (data_written != 0);

                    break;
                }
            }

            if (write_output_file_trailer(output_format_context) != 0)
            {
                goto cleanup;
            }

            ret = 0;

        cleanup:
            if (fifo != null)
            {
                ffmpeg.av_audio_fifo_free(fifo);
            }

            ffmpeg.swr_free(&resample_context);

            if (output_codec_conetxt != null)
            {
                ffmpeg.avio_closep(&output_format_context->pb);
                ffmpeg.avformat_free_context(output_format_context);
            }

            if (input_codec_conetxt != null)
            {
                ffmpeg.avcodec_free_context(&input_codec_conetxt);
            }

            if (input_format_context != null)
            {
                ffmpeg.avformat_close_input(&input_format_context);
            }

            return ret;
        }
    }
}
