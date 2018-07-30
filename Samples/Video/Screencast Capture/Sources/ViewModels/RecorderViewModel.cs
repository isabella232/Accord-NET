﻿// Screencast Capture, free screen recorder
// http://screencast-capture.googlecode.com
//
// Copyright © César Souza, 2012-2013
// cesarsouza at gmail.com
//
//    This program is free software; you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation; either version 2 of the License, or
//    (at your option) any later version.
//
//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with this program; if not, write to the Free Software
//    Foundation, Inc., 675 Mass Ave, Cambridge, MA 02139, USA.
// 

namespace ScreenCapture.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.IO;
    using System.Windows.Forms;
    using Accord.Audio;
    using Accord.DirectSound;
    using Accord.Controls;
    using Accord.Imaging.Filters;
    using Accord.Video;
    using Accord.Video.FFMPEG;
    using ScreenCapture.Native;
    using ScreenCapture.Processors;
    using ScreenCapture.Properties;
    using System.Collections.Specialized;
    using Accord.Math;
    using Accord.Imaging;
    using System.Threading.Tasks;

    // TODO: Disable frame preview window if the window is not visible or is minimized

    /// <summary>
    ///   Region capturing modes.
    /// </summary>
    /// 
    public enum CaptureRegionOption
    {
        /// <summary>
        ///   Captures from a fixed region on the screen.
        /// </summary>
        /// 
        Fixed,

        /// <summary>
        ///   Captures only from the primary screen.
        /// </summary>
        /// 
        Primary,

        /// <summary>
        ///   Captures from the current window.
        /// </summary>
        Window
    }

    /// <summary>
    ///   Main ViewModel to control the application.
    /// </summary>
    /// 
    public sealed class RecorderViewModel : INotifyPropertyChanged, IDisposable
    {

        private MainViewModel main;

        private CaptureRegionOption captureMode;
        private ScreenCaptureStream screenStream;
        private VideoFileWriter videoWriter;
        private VideoSourcePlayer videoPlayer;

        private AudioSourceMixer audioMixer;

        private Crop crop = new Crop(Rectangle.Empty);
        private CaptureCursor cursorCapture;
        private CaptureClick clickCapture;
        private CaptureKeyboard keyCapture;
        private Object syncObj = new Object();

        Bitmap croppedImage;
        Bitmap lastFrame;
        Rectangle lastFrameRegion;


        /// <summary>
        ///   Gets the path to the output file generated by
        ///   the recorder. This file is the recorded video.
        /// </summary>
        /// 
        public string OutputPath { get; private set; }


        /// <summary>
        ///   Gets or sets the current capture mode, if the capture area
        ///   should be the whole screen, a fixed region or a fixed window.
        /// </summary>
        /// 
        public CaptureRegionOption CaptureMode
        {
            get { return captureMode; }
            set { OnCaptureModeChanged(value); }
        }

        /// <summary>
        ///   Gets or sets the current capture region.
        /// </summary>
        /// 
        public Rectangle CaptureRegion { get; set; }

        /// <summary>
        ///   Gets or sets the current capture window.
        /// </summary>
        /// 
        public IWin32Window CaptureWindow { get; set; }

        /// <summary>
        ///   Gets the initial recording time.
        /// </summary>
        /// 
        public DateTime RecordingStartTime { get; private set; }

        /// <summary>
        ///   Gets the current recording time.
        /// </summary>
        /// 
        public TimeSpan RecordingDuration { get; private set; }

        /// <summary>
        ///   Gets whether the view-model is waiting for the
        ///   user to select a target window to be recorded.
        /// </summary>
        /// 
        public bool IsWaitingForTargetWindow { get; private set; }

        /// <summary>
        ///   Gets whether the application is recording the screen.
        /// </summary>
        /// 
        public bool IsRecording { get; private set; }

        /// <summary>
        ///   Gets whether the application is grabbing frames from the screen.
        /// </summary>
        /// 
        public bool IsPlaying { get; private set; }

        /// <summary>
        ///   Gets whether the application has already finished recording a video.
        /// </summary>
        /// 
        public bool HasRecorded { get; private set; }

        /// <summary>
        ///   Gets whether the capture region frame should be visible.
        /// </summary>
        /// 
        public bool IsCaptureFrameVisible { get { return IsPlaying && CaptureMode == CaptureRegionOption.Fixed; } }

        /// <summary>
        ///   Gets or sets the current capture audio device. If set
        ///   to null, audio capturing will be disabled.
        /// </summary>
        /// 
        public BindingList<AudioCaptureDeviceViewModel> AudioCaptureDevices { get; private set; }

        /// <summary>
        ///   Gets a list of audio devices available in the system.
        /// </summary>
        /// 
        public static ReadOnlyCollection<AudioDeviceInfo> AudioDevices { get; private set; }


        /// <summary>
        ///   Occurs when the view-model needs a window to be recorded.
        /// </summary>
        /// 
        public event EventHandler ShowTargetWindow;





        /// <summary>
        ///   Initializes a new instance of the <see cref="MainViewModel"/> class.
        /// </summary>
        /// 
        public RecorderViewModel(MainViewModel main, VideoSourcePlayer player)
        {
            this.main = main ?? throw new ArgumentNullException("main");
            this.videoPlayer = player ?? throw new ArgumentNullException("player");
            this.videoPlayer.NewFrameReceived += VideoPlayer_NewFrameReceived;

            this.CaptureMode = CaptureRegionOption.Primary;
            this.CaptureRegion = new Rectangle(0, 0, 640, 480);

            this.clickCapture = new CaptureClick();
            this.cursorCapture = new CaptureCursor();
            this.keyCapture = new CaptureKeyboard();

            this.AudioCaptureDevices = new AudioViewModelCollection(RecorderViewModel.AudioDevices);


            // Search and mark last selected devices
            foreach (AudioCaptureDeviceViewModel dev in AudioCaptureDevices)
                dev.Checked = Settings.Default.LastAudioDevices.Contains(dev.DeviceInfo.Guid.ToString());
        }

        static RecorderViewModel()
        {
            AudioDevices = new ReadOnlyCollection<AudioDeviceInfo>(
                new List<AudioDeviceInfo>(new AudioDeviceCollection(AudioDeviceCategory.Capture)));
        }



        /// <summary>
        ///   Starts playing the preview screen, grabbing
        ///   frames, but not recording to a video file.
        /// </summary>
        /// 
        public void StartPlaying()
        {
            if (IsPlaying)
                return;

            // Checks if we were already waiting for a window
            // to be selected, in case the user had chosen to 
            // capture from a fixed window.

            if (IsWaitingForTargetWindow)
            {
                // Yes, we were. We will not be waiting anymore
                // since the user should have selected one now.
                IsWaitingForTargetWindow = false;
            }

            else
            {
                // No, this is the first time the user starts the
                // frame grabber. Let's check what the user wants

                if (CaptureMode == CaptureRegionOption.Window)
                {
                    // The user wants to capture from a window. So we
                    // need to ask which window we have to keep a look.

                    // We will return here and wait the user to respond; 
                    // when he finishes selecting he should signal back
                    // by calling SelectWindowUnderCursor().
                    IsWaitingForTargetWindow = true;
                    OnTargetWindowRequested();
                    return;
                }
            }

            // All is well. Keep configuring and start
            CaptureRegion = Screen.PrimaryScreen.Bounds;

            double framerate = Settings.Default.FrameRate;
            int interval = (int)Math.Round(1000 / framerate);
            int height = CaptureRegion.Height;
            int width = CaptureRegion.Width;

            clickCapture.Enabled = true;
            keyCapture.Enabled = true;

            screenStream = new ScreenCaptureStream(CaptureRegion, interval);
            screenStream.VideoSourceError += ScreenStream_VideoSourceError;

            videoPlayer.VideoSource = screenStream;
            videoPlayer.Start();

            IsPlaying = true;
        }

        /// <summary>
        ///   Pauses the frame grabber, but keeps recording
        ///   if the software has already started recording.
        /// </summary>
        /// 
        public void PausePlaying()
        {
            if (!IsPlaying)
                return;

            videoPlayer.SignalToStop();
            IsPlaying = false;
        }



        /// <summary>
        ///   Starts recording. Only works if the player has
        ///   already been started and is grabbing frames.
        /// </summary>
        /// 
        public void StartRecording()
        {
            if (IsRecording || !IsPlaying)
                return;

            Rectangle area = CaptureRegion;
            string fileName = NewFileName();

            int height = area.Height;
            int width = area.Width;
            Rational framerate = new Rational(1000, screenStream.FrameInterval);
            int videoBitRate = 1200 * 1000;
            int audioBitRate = 320 * 1000;
            int audioFrameSize = 10 * 4096;

            OutputPath = Path.Combine(main.CurrentDirectory, fileName);
            RecordingStartTime = DateTime.MinValue;
            videoWriter = new VideoFileWriter();
            videoWriter.BitRate = videoBitRate;
            videoWriter.FrameRate = framerate;
            videoWriter.Width = width;
            videoWriter.Height = height;
            videoWriter.VideoCodec = VideoCodec.H264;
            videoWriter.VideoOptions["crf"] = "18"; // visually lossless
            videoWriter.VideoOptions["preset"] = "veryfast";
            videoWriter.VideoOptions["tune"] = "zerolatency";
            videoWriter.VideoOptions["x264opts"] = "no-mbtree:sliced-threads:sync-lookahead=0";

            // Create audio devices which have been checked
            var audioDevices = new List<AudioCaptureDevice>();
            foreach (var audioViewModel in AudioCaptureDevices)
            {
                if (!audioViewModel.Checked)
                    continue;

                var device = new AudioCaptureDevice(audioViewModel.DeviceInfo);
                device.AudioSourceError += Device_AudioSourceError;
                device.Format = SampleFormat.Format32BitIeeeFloat;
                device.SampleRate = Settings.Default.SampleRate;
                device.DesiredFrameSize = audioFrameSize;
                device.Start();

                audioDevices.Add(device);
            }

            if (audioDevices.Count > 0) // Check if we need to record audio
            {
                audioMixer = new AudioSourceMixer(audioDevices);
                audioMixer.AudioSourceError += Device_AudioSourceError;
                audioMixer.NewFrame += AudioDevice_NewFrame;
                audioMixer.Start();

                videoWriter.AudioBitRate = audioBitRate;
                videoWriter.AudioCodec = AudioCodec.Aac;
                videoWriter.AudioLayout = audioMixer.NumberOfChannels == 1 ? AudioLayout.Mono : AudioLayout.Stereo;
                videoWriter.FrameSize = audioFrameSize;
                videoWriter.SampleRate = audioMixer.SampleRate;
            }

            //this.lastFrameTime = DateTime.MinValue;

            videoWriter.Open(OutputPath);

            HasRecorded = false;
            IsRecording = true;
        }


        /// <summary>
        ///   Stops recording.
        /// </summary>
        /// 
        public void StopRecording()
        {
            if (!IsRecording)
                return;

            lock (syncObj)
            {
                IsRecording = false;

                if (videoWriter != null)
                {
                    videoWriter.Close();
                    videoWriter.Dispose();
                    videoWriter = null;
                }

                if (audioMixer != null)
                {
                    audioMixer.Stop();
                    foreach (IAudioSource source in audioMixer.Sources)
                    {
                        source.Stop();
                        source.Dispose();
                    }

                    audioMixer.Dispose();
                    audioMixer = null;
                }

                HasRecorded = true;
            }
        }




        /// <summary>
        ///   Grabs the handle of the window currently under
        ///   the cursor, and if the application is waiting
        ///   for a handle, immediately starts playing.
        /// </summary>
        /// 
        public void SelectWindowUnderCursor()
        {
            CaptureWindow = SafeNativeMethods.WindowFromPoint(Cursor.Position);

            if (IsWaitingForTargetWindow)
                StartPlaying();
        }

        /// <summary>
        ///   Releases resources and prepares
        ///   the application for closing.
        /// </summary>
        /// 
        public void Close()
        {
            // Save last selected audio devices
            Settings.Default.LastAudioDevices.Clear();
            foreach (AudioCaptureDeviceViewModel dev in AudioCaptureDevices)
            {
                if (dev.Checked)
                    Settings.Default.LastAudioDevices.Add(dev.DeviceInfo.Guid.ToString());
            }

            if (videoWriter != null && videoWriter.IsOpen)
            {
                videoWriter.Close();
            }

            if (videoPlayer != null && videoPlayer.IsRunning)
            {
                videoPlayer.SignalToStop();
                videoPlayer.WaitForStop();
            }

            if (audioMixer != null && audioMixer.IsRunning)
            {
                audioMixer.SignalToStop();
                audioMixer.WaitForStop();
            }
        }



        /// <summary>
        ///   Raises a property changed on <see cref="CaptureMode"/>.
        /// </summary>
        /// 
        private void OnCaptureModeChanged(CaptureRegionOption value)
        {
            if (IsRecording)
                return;

            captureMode = value;

            if (value == CaptureRegionOption.Window && IsPlaying)
            {
                IsWaitingForTargetWindow = true;
                OnTargetWindowRequested();
                IsWaitingForTargetWindow = false;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CaptureMode"));
        }

        /// <summary>
        ///   Raises the <see cref="ShowTargetWindow"/> event.
        /// </summary>
        /// 
        private void OnTargetWindowRequested()
        {
            ShowTargetWindow?.Invoke(this, EventArgs.Empty);
        }


        void VideoPlayer_NewFrameReceived(object sender, Accord.Video.NewFrameEventArgs eventArgs)
        {
            DateTime currentFrameTime = eventArgs.CaptureFinished;

            // Encode the last frame at the same time we prepare the new one
            Task.WaitAll(
                Task.Run(() =>
                {
                    lock (syncObj) // Save the frame to the video file.
                    {
                        if (IsRecording)
                        {
                            if (RecordingStartTime == DateTime.MinValue)
                                RecordingStartTime = DateTime.Now;

                            TimeSpan timestamp = currentFrameTime - RecordingStartTime;
                            if (timestamp > TimeSpan.Zero)
                                videoWriter.WriteVideoFrame(this.lastFrame, timestamp, this.lastFrameRegion);
                        }
                    }
                }),

                Task.Run(() =>
                {
                    // Adjust the window according to the current capture
                    // mode. Also adjusts to keep even widths and heights.
                    CaptureRegion = AdjustWindow();

                    // Crop the image if the mode requires it
                    if (CaptureMode == CaptureRegionOption.Fixed ||
                        CaptureMode == CaptureRegionOption.Window)
                    {
                        crop.Rectangle = CaptureRegion;

                        eventArgs.Frame = croppedImage = crop.Apply(eventArgs.Frame, croppedImage);
                        eventArgs.FrameSize = crop.Rectangle.Size;
                    }

                    //// Draw extra information on the screen
                    bool captureMouse = Settings.Default.CaptureMouse;
                    bool captureClick = Settings.Default.CaptureClick;
                    bool captureKeys = Settings.Default.CaptureKeys;

                    if (captureMouse || captureClick || captureKeys)
                    {
                        cursorCapture.CaptureRegion = CaptureRegion;
                        clickCapture.CaptureRegion = CaptureRegion;
                        keyCapture.Font = Settings.Default.KeyboardFont;

                        using (Graphics g = Graphics.FromImage(eventArgs.Frame))
                        {
                            g.CompositingQuality = CompositingQuality.HighSpeed;
                            g.SmoothingMode = SmoothingMode.HighSpeed;

                            float invWidth = 1; // / widthScale;
                            float invHeight = 1; // / heightScale;

                            if (captureMouse)
                                cursorCapture.Draw(g, invWidth, invHeight);

                            if (captureClick)
                                clickCapture.Draw(g, invWidth, invHeight);

                            if (captureKeys)
                                keyCapture.Draw(g, invWidth, invHeight);
                        }
                    }
                })
            );

            // Save the just processed frame and mark 
            // it to be encoded in the next iteration:
            lastFrame = eventArgs.Frame.Copy(lastFrame);
            //lastFrameTime = currentFrameTime;
            lastFrameRegion = new Rectangle(0, 0, eventArgs.FrameSize.Width, eventArgs.Frame.Height);
        }


        private void AudioDevice_NewFrame(object sender, Accord.Audio.NewFrameEventArgs e)
        {
            lock (syncObj) // Save the frame to the video file.
            {
                if (IsRecording)
                {
                    videoWriter.WriteAudioFrame(e.Signal);
                }
            }
        }


        private Rectangle AdjustWindow()
        {
            Rectangle area = CaptureRegion;

            if (CaptureMode == CaptureRegionOption.Window && !IsRecording)
            {
                if (!SafeNativeMethods.TryGetWindowRect(CaptureWindow, out area))
                    area = CaptureRegion;
            }
            else if (CaptureMode == CaptureRegionOption.Primary)
            {
                area = Screen.PrimaryScreen.Bounds;
            }

            if (area.Width % 2 != 0)
                area.Width++;
            if (area.Height % 2 != 0)
                area.Height++;

            return area;
        }

        private string NewFileName()
        {
            string date = DateTime.Now.ToString("yyyy-MM-dd-HH'h'mm'm'ss's'",
                System.Globalization.CultureInfo.CurrentCulture);

            string mode = String.Empty;
            if (CaptureMode == CaptureRegionOption.Primary)
                mode = "Screen_";
            else if (CaptureMode == CaptureRegionOption.Fixed)
                mode = "Region_";
            else if (CaptureMode == CaptureRegionOption.Window)
                mode = "Window_";

            string name = mode + date + "." + Settings.Default.Container;

            return name;
        }


        // Error handling
        private void Device_AudioSourceError(object sender, AudioSourceErrorEventArgs e)
        {
            if (videoPlayer.InvokeRequired)
            {
                videoPlayer.BeginInvoke((Action)((() => Device_AudioSourceError(sender, e))));
                return;
            }

            StopRecording();

            IAudioSource source = sender as IAudioSource;
            source.SignalToStop();

            string msg = String.Format( // TODO: Move the message box code to the view
                CultureInfo.CurrentCulture, Resources.Error_Audio_Source, source.Source);
            MessageBox.Show(msg, source.Source, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1,
                videoPlayer.RightToLeft == RightToLeft.Yes ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
        }

        private void ScreenStream_VideoSourceError(object sender, VideoSourceErrorEventArgs e)
        {
            if (!IsRecording)
                return;

            if (videoPlayer.InvokeRequired)
            {
                videoPlayer.BeginInvoke((Action)((() => ScreenStream_VideoSourceError(sender, e))));
                return;
            }

            IVideoSource source = sender as IVideoSource;
            source.SignalToStop();

            string msg = String.Format( // TODO: Move the message box code to the view
                CultureInfo.CurrentCulture, Resources.Error_Video_Source, source.Source);
            MessageBox.Show(msg, source.Source, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1,
                videoPlayer.RightToLeft == RightToLeft.Yes ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
        }




        #region IDisposable implementation

        /// <summary>
        ///   Performs application-defined tasks associated with freeing, 
        ///   releasing, or resetting unmanaged resources.
        /// </summary>
        /// 
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///   Releases unmanaged resources and performs other cleanup operations 
        ///   before the <see cref="RecorderViewModel"/> is reclaimed by garbage collection.
        /// </summary>
        /// 
        ~RecorderViewModel()
        {
            Dispose(false);
        }

        /// <summary>
        ///   Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// 
        /// <param name="disposing"><c>true</c> to release both managed
        /// and unmanaged resources; <c>false</c> to release only unmanaged
        /// resources.</param>
        ///
        void Dispose(bool disposing)
        {
            if (disposing)
            {
                // free managed resources
                if (clickCapture != null)
                {
                    clickCapture.Dispose();
                    clickCapture = null;
                }

                if (cursorCapture != null)
                {
                    cursorCapture.Dispose();
                    cursorCapture = null;
                }

                if (keyCapture != null)
                {
                    keyCapture.Dispose();
                    keyCapture = null;
                }

                if (audioMixer != null)
                {
                    audioMixer.Dispose();
                    audioMixer = null;
                }

                if (videoWriter != null)
                {
                    videoWriter.Dispose();
                    videoWriter = null;
                }
            }
        }
        #endregion


        // The PropertyChanged event doesn't needs to be explicitly raised
        // from this application. The event raising is handled automatically
        // by the NotifyPropertyWeaver VS extension using IL injection.
        //
#pragma warning disable 0067
        /// <summary>
        /// Occurs when a property value changes.
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;
#pragma warning restore 0067

    }
}