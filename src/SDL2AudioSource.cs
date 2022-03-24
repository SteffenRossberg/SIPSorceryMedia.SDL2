﻿using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static SDL2.SDL;

namespace SIPSorceryMedia.SDL2
{
    public class SDL2AudioSource: IAudioSource
    {
        static private ILogger logger = SIPSorcery.LogFactory.CreateLogger<SDL2AudioSource>();
        static private List<AudioFormat> _supportedAudioFormats = SIPSorceryMedia.SDL2.SDL2Helper.GetSupportedAudioFormats();

        private String _audioInDeviceName;
        private uint _audioInDeviceId = 0;

        private IAudioEncoder _audioEncoder;
        private MediaFormatManager<AudioFormat> _audioFormatManager;

        private bool _isStarted = false;
        private bool _isPaused = true;
        private bool _isClosed = true;

        private SDL_AudioSpec audioSpec;

        private BackgroundWorker backgroundWorker;

#region EVENT
        public event EncodedSampleDelegate ? OnAudioSourceEncodedSample = null;
        public event RawAudioSampleDelegate ? OnAudioSourceRawSample = null;

        public event SourceErrorDelegate ? OnAudioSourceError = null;
#endregion EVENT

        public SDL2AudioSource(String audioInDeviceName, IAudioEncoder audioEncoder)
        {
            if (audioEncoder == null)
                throw new ApplicationException("Audio encoder provided is null");

            _audioInDeviceName = audioInDeviceName;

            _audioFormatManager = new MediaFormatManager<AudioFormat>(_supportedAudioFormats);
            _audioEncoder = audioEncoder;

            InitRecordingDevice();

            backgroundWorker = new BackgroundWorker();
            backgroundWorker.DoWork += BackgroundWorker_DoWork;
            backgroundWorker.WorkerSupportsCancellation = true;
        }

        private unsafe void BackgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            while (!backgroundWorker.CancellationPending)
            {
                uint size = SDL_GetQueuedAudioSize(_audioInDeviceId);
                if (size > 0)
                {
                    byte[] buf = new byte[size];

                    fixed (byte* ptr = &buf[0])
                    {
                        SDL_DequeueAudio(_audioInDeviceId, (IntPtr)ptr, size);

                        short[] pcm = buf.Take((int)size * 2).Where((x, i) => i % 2 == 0).Select((y, i) => BitConverter.ToInt16(buf, i * 2)).ToArray();
                        OnAudioSourceRawSample?.Invoke(AudioSamplingRatesEnum.Rate8KHz, (uint)pcm.Length, pcm);

                        if (OnAudioSourceEncodedSample != null)
                        {
                            var encodedSample = _audioEncoder.EncodeAudio(pcm, _audioFormatManager.SelectedFormat);
                            OnAudioSourceEncodedSample?.Invoke((uint)encodedSample.Length, encodedSample);
                        }
                    }
                }
                SDL_Delay(16);
            }
        }

        private void InitRecordingDevice()
        {
            try
            {
                // Stop previous recording device
                if (_audioInDeviceId > 0)
                {
                    SDL2Helper.PauseAudioRecordingDevice(_audioInDeviceId);
                    SDL2Helper.CloseAudioRecordingDevice(_audioInDeviceId);
                    _audioInDeviceId = 0;
                }

                // Init recording device.
                audioSpec = SDL2Helper.GetDefaultAudioSpec();

                _audioInDeviceId = SDL2Helper.OpenAudioRecordingDevice(_audioInDeviceName, ref audioSpec);
                if (_audioInDeviceId < 0)
                    throw new ApplicationException("No recording device name found");
            }
            catch (Exception excp)
            {
                logger.LogWarning(excp, "SDLAudioEndPoint failed to initialise recording device.");
                OnAudioSourceError?.Invoke($"SDLAudioEndPoint failed to initialise recording device. {excp.Message}");
            }
        }
  
        public Task PauseAudio()
        {
            if (_isStarted && !_isPaused)
            {
                if (backgroundWorker.IsBusy)
                    backgroundWorker.CancelAsync();

                SDL2Helper.PauseAudioRecordingDevice(_audioInDeviceId, true);
                _isPaused = true;
            }

            return Task.CompletedTask;
        }

        public Task ResumeAudio()
        {
            if (_isStarted && _isPaused)
            {
                if (!backgroundWorker.IsBusy)
                    backgroundWorker.RunWorkerAsync();

                SDL2Helper.PauseAudioRecordingDevice(_audioInDeviceId, false);
                _isPaused = false;
            }

            return Task.CompletedTask;
        }

        public bool IsAudioSourcePaused()
        {
            return _isPaused;
        }

        public Task StartAudio()
        {
            if (!_isStarted)
            {
                //InitRecordingDevice();

                if (_audioInDeviceId > 0)
                {
                    _isStarted = true;
                    _isClosed = false;
                    _isPaused = true;

                    ResumeAudio();
                }
            }

            return Task.CompletedTask;
        }

        public Task CloseAudio()
        {
            if (_isStarted)
            {
                PauseAudio().Wait();
                SDL2Helper.CloseAudioRecordingDevice(_audioInDeviceId);
            }

            _isClosed = true;
            _isStarted = false;

            return Task.CompletedTask;
        }

        public List<AudioFormat> GetAudioSourceFormats()
        {
            if (_audioFormatManager != null)
                return _audioFormatManager.GetSourceFormats();
            return new List<AudioFormat>();
        }
        
        public void SetAudioSourceFormat(AudioFormat audioFormat)
        {
            if (_audioFormatManager != null)
            {
                logger.LogDebug($"Setting audio source format to {audioFormat.FormatID}:{audioFormat.Codec} {audioFormat.ClockRate}.");
                _audioFormatManager.SetSelectedFormat(audioFormat);
            }
        }
        
        public void RestrictFormats(Func<AudioFormat, bool> filter)
        {
            if (_audioFormatManager != null)
                _audioFormatManager.RestrictFormats(filter);
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) => throw new NotImplementedException();

        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
    }
}