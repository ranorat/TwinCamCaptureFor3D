using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Windows.Media.Imaging;

namespace TwinCamCaptureFor3D
{
    public partial class MainWindow : System.Windows.Window
    {
        private const string AppVersion = "1.0.2";
        private const string AppTitle = "TwinCamCaptureFor3D (3D立体視用デュアルカメラレコーダー)";

        private VideoCapture? _leftCapture;
        private VideoWriter? _leftWriter;
        private CancellationTokenSource? _leftCts;

        private VideoCapture? _rightCapture;
        private VideoWriter? _rightWriter;
        private CancellationTokenSource? _rightCts;

        private bool _isRecording = false;
        private readonly object _lockObject = new object();

        private List<CameraInfo> _allCameras = new List<CameraInfo>();
        private bool _isUpdatingCombos = false;

        private Mat? _latestLeft;
        private Mat? _latestRight;

        public MainWindow()
        {
            InitializeComponent();

            Environment.SetEnvironmentVariable("OPENCV_VIDEOIO_MSMF_ENABLE_HW_TRANSFORMS", "0");

            this.Title = $"{AppTitle} v{AppVersion}";

            InitializeFpsLists();
            SavePathTextBox.Text = AppDomain.CurrentDomain.BaseDirectory;

            StartButton.IsEnabled = false;

            _ = InitializeCameraListsAsync();
        }

        private void InitializeFpsLists()
        {
            var fpsList = new List<double>
            {
                60.0, 59.94, 30.0, 29.98, 24.0, 23.97, 15.0, 14.98, 10.0, 7.5, 5.0
            };

            LeftFpsComboBox.ItemsSource = fpsList;
            LeftFpsComboBox.SelectedIndex = 2;

            RightFpsComboBox.ItemsSource = fpsList;
            RightFpsComboBox.SelectedIndex = 2;
        }

        private async Task InitializeCameraListsAsync()
        {
            LeftCameraComboBox.IsEnabled = false;
            RightCameraComboBox.IsEnabled = false;
            StartButton.IsEnabled = false;

            _allCameras = await Task.Run(async () =>
            {
                var list = new List<CameraInfo>();
                try
                {
                    var videoDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                        Windows.Devices.Enumeration.DeviceClass.VideoCapture);

                    for (int i = 0; i < videoDevices.Count; i++)
                    {
                        var dev = videoDevices[i];
                        string rawName = string.IsNullOrWhiteSpace(dev.Name) ? $"カメラデバイス {i}" : dev.Name;
                        
                        // ★修正：デバイス名の頭に論理番号（インデックス）を付番して区別しやすくする
                        string formattedName = $"[{i}] {rawName}";

                        list.Add(new CameraInfo 
                        { 
                            Index = i, 
                            Name = formattedName,
                            WinRtId = dev.Id 
                        });
                    }
                }
                catch 
                {
                    for (int i = 0; i < 4; i++)
                    {
                        try
                        {
                            using var tempCapture = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                            if (tempCapture.IsOpened())
                            {
                                list.Add(new CameraInfo { Index = i, Name = $"[{i}] カメラデバイス {i}", WinRtId = string.Empty });
                            }
                        }
                        catch { }
                        Thread.Sleep(20);
                    }
                }
                return list;
            });

            _isUpdatingCombos = true;
            LeftCameraComboBox.ItemsSource = _allCameras;
            LeftCameraComboBox.DisplayMemberPath = "Name";
            LeftCameraComboBox.SelectedValuePath = "Index";

            if (_allCameras.Count > 0) LeftCameraComboBox.SelectedIndex = 0;
            _isUpdatingCombos = false;

            UpdateRightComboBox();

            LeftCameraComboBox.IsEnabled = true;
            RightCameraComboBox.IsEnabled = true;
        }

        private void LeftCameraComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isUpdatingCombos) return;
            UpdateRightComboBox();
        }

        private void RightCameraComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
        }


        private void LeftResolutionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ValidateAndUpdateStartButtonState();
        }

        private void RightResolutionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ValidateAndUpdateStartButtonState();
        }


        private void UpdateRightComboBox()
        {
            if (_isUpdatingCombos) return;
            _isUpdatingCombos = true;

            try
            {
                int leftIndex = LeftCameraComboBox.SelectedValue is int lIdx ? lIdx : -1;
                int currentRightIndex = RightCameraComboBox.SelectedValue is int rIdx ? rIdx : -1;

                var rightList = _allCameras.FindAll(c => c.Index != leftIndex);

                RightCameraComboBox.ItemsSource = null;
                RightCameraComboBox.ItemsSource = rightList;
                RightCameraComboBox.DisplayMemberPath = "Name";
                RightCameraComboBox.SelectedValuePath = "Index";

                if (rightList.Count > 0)
                {
                    if (rightList.Exists(c => c.Index == currentRightIndex))
                    {
                        RightCameraComboBox.SelectedValue = currentRightIndex;
                    }
                    else
                    {
                        RightCameraComboBox.SelectedIndex = 0;
                    }
                }
                else
                {
                    RightCameraComboBox.SelectedIndex = -1;
                }
            }
            finally
            {
                _isUpdatingCombos = false;
            }
        }

        private async void RefreshCameras_Click(object sender, RoutedEventArgs e)
        {
            LeftResolutionComboBox.ItemsSource = null;
            RightResolutionComboBox.ItemsSource = null;
            StartButton.IsEnabled = false;
            CaptureButton.IsEnabled = false;

            await InitializeCameraListsAsync();
            StreamInfoText.Text = "ステータス: デバイスを更新しました。「解像度を取得」を押してください。";
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_leftCapture != null || _rightCapture != null || _isRecording)
            {
                MessageBox.Show("プレビュー中または録画中は保存先を変更できません。一度プレビューを停止してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "保存先フォルダを選択",
                InitialDirectory = SavePathTextBox.Text
            };

            if (dialog.ShowDialog() == true)
            {
                SavePathTextBox.Text = dialog.FolderName;
            }
        }

        private async void FetchResolutions_Click(object sender, RoutedEventArgs e)
        {
            int? leftIndex = LeftCameraComboBox.SelectedValue as int?;
            int? rightIndex = RightCameraComboBox.SelectedValue as int?;

            if (!leftIndex.HasValue && !rightIndex.HasValue)
            {
                MessageBox.Show("有効なカメラが選択されていません。");
                return;
            }

            StartButton.IsEnabled = false;
            CaptureButton.IsEnabled = false;
            StreamInfoText.Text = "ステータス: WinRT APIで仕様を一括取得中...";

            if (leftIndex.HasValue)
            {
                var camInfo = _allCameras.Find(c => c.Index == leftIndex.Value);
                if (camInfo != null)
                {
                    await LoadSpecificationsViaWinRtAsync(camInfo, LeftResolutionComboBox);
                }
            }

            if (rightIndex.HasValue)
            {
                var camInfo = _allCameras.Find(c => c.Index == rightIndex.Value);
                if (camInfo != null)
                {
                    await LoadSpecificationsViaWinRtAsync(camInfo, RightResolutionComboBox);
                }
            }

            ValidateAndUpdateStartButtonState();
            StreamInfoText.Text = "ステータス: 仕様取得完了";
        }

        private async Task LoadSpecificationsViaWinRtAsync(CameraInfo camInfo, System.Windows.Controls.ComboBox targetResolutionComboBox)
        {
            targetResolutionComboBox.IsEnabled = false;

            var resolutions = await Task.Run(async () =>
            {
                var list = new List<ResolutionInfo>();

                if (string.IsNullOrEmpty(camInfo.WinRtId))
                {
                    list.Add(new ResolutionInfo { Width = 640, Height = 480, Fourcc = 0, CodecName = "DEFAULT", DisplayName = "640 x 480 (DEFAULT)" });
                    return list;
                }

                try
                {
                    using var tempCapture = new Windows.Media.Capture.MediaCapture();
                    var settings = new Windows.Media.Capture.MediaCaptureInitializationSettings
                    {
                        VideoDeviceId = camInfo.WinRtId,
                        StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Video
                    };

                    await tempCapture.InitializeAsync(settings);
                    var vFormats = tempCapture.VideoDeviceController.GetAvailableMediaStreamProperties(
                        Windows.Media.Capture.MediaStreamType.VideoRecord);

                    foreach (var f in vFormats.OfType<Windows.Media.MediaProperties.VideoEncodingProperties>())
                    {
                        uint w = f.Width;
                        uint h = f.Height;
                        double fps = Math.Round((double)f.FrameRate.Numerator / f.FrameRate.Denominator, 2);

                        string subtype = f.Subtype.ToUpper();
                        string codecName = subtype.Length > 4 ? subtype.Substring(0, 4) : subtype;

                        if (f.Subtype.Equals("3432564e-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase)) codecName = "NV12";
                        if (f.Subtype.Equals("32595559-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase)) codecName = "YUY2";
                        if (f.Subtype.Equals("47504a4d-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase)) codecName = "MJPG";
                        if (f.Subtype.Equals("34363268-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase)) codecName = "H264";

                        int fourcc = 0;
                        if (codecName == "MJPG") fourcc = VideoWriter.FourCC('M', 'J', 'P', 'G');
                        else if (codecName == "YUY2") fourcc = VideoWriter.FourCC('Y', 'U', 'Y', '2');
                        else if (codecName == "NV12") fourcc = VideoWriter.FourCC('N', 'V', '1', '2');

                        if (fourcc != 0)
                        {
                            string label = $"{codecName} | {w} x {h}";
                            if (!list.Exists(r => r.Width == (int)w && r.Height == (int)h && r.Fourcc == fourcc))
                            {
                                list.Add(new ResolutionInfo 
                                { 
                                    Width = (int)w, 
                                    Height = (int)h, 
                                    Fourcc = fourcc, 
                                    CodecName = codecName, 
                                    DisplayName = label 
                                });
                            }
                        }
                    }
                }
                catch { }

                if (list.Count == 0)
                {
                    list.Add(new ResolutionInfo { Width = 640, Height = 480, Fourcc = VideoWriter.FourCC('Y', 'U', 'Y', '2'), CodecName = "YUY2", DisplayName = "YUY2 | 640 x 480" });
                }

                return list;
            });

            targetResolutionComboBox.ItemsSource = resolutions;
            targetResolutionComboBox.DisplayMemberPath = "DisplayName";
            if (resolutions.Count > 0) targetResolutionComboBox.SelectedIndex = 0;

            targetResolutionComboBox.IsEnabled = true;
        }

        private void ValidateAndUpdateStartButtonState()
        {
            bool hasLeftRes = LeftResolutionComboBox.HasItems && LeftResolutionComboBox.SelectedItem != null;
            bool hasRightRes = RightResolutionComboBox.HasItems && RightResolutionComboBox.SelectedItem != null;

            if (hasLeftRes || hasRightRes)
            {
                StartButton.IsEnabled = true;
                StreamInfoText.Text = "ステータス: 準備完了 (プレビュー開始できます)";
            }
            else
            {
                StartButton.IsEnabled = false;
                StreamInfoText.Text = "ステータス: 解像度を選択してください";
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (LeftCameraComboBox.SelectedValue is int leftIndex && LeftResolutionComboBox.SelectedItem is ResolutionInfo leftRes)
            {
                StartCameraPreview(leftIndex, leftRes, true);
            }

            if (RightCameraComboBox.SelectedValue is int rightIndex && RightResolutionComboBox.SelectedItem is ResolutionInfo rightRes)
            {
                StartCameraPreview(rightIndex, rightRes, false);
            }

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            RecordButton.IsEnabled = true;
            CaptureButton.IsEnabled = true;
            LeftCameraComboBox.IsEnabled = false;
            RightCameraComboBox.IsEnabled = false;
            LeftResolutionComboBox.IsEnabled = false;
            RightResolutionComboBox.IsEnabled = false;
            
            if (BrowseButton != null) BrowseButton.IsEnabled = false;

            Dispatcher.Invoke(() =>
            {
                DrawGridOverlay(LeftGridOverlayCanvas);
                DrawGridOverlay(RightGridOverlayCanvas);
            });
        }

        private void StartCameraPreview(int cameraIndex, ResolutionInfo selectedRes, bool isLeft)
        {
            Task.Run(() =>
            {
                VideoCapture? capture = null;
                try
                {
                    Thread.Sleep(600);
                    capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);

                    if (!capture.IsOpened())
                    {
                        Dispatcher.Invoke(() => MessageBox.Show($"カメラ {(isLeft ? "左" : "右")} を開けませんでした。"));
                        capture.Dispose();
                        return;
                    }

                    capture.Set(VideoCaptureProperties.FrameWidth, selectedRes.Width);
                    capture.Set(VideoCaptureProperties.FrameHeight, selectedRes.Height);
                    if (selectedRes.Fourcc != 0)
                    {
                        capture.Set(VideoCaptureProperties.FourCC, selectedRes.Fourcc);
                    }

                    lock (_lockObject)
                    {
                        if (isLeft) _leftCapture = capture;
                        else _rightCapture = capture;
                    }

                    var cts = new CancellationTokenSource();
                    if (isLeft) _leftCts = cts;
                    else _rightCts = cts;

                    var token = cts.Token;
                    using var frame = new Mat();
                    using var lastValidFrame = new Mat();

                    var recordStopwatch = Stopwatch.StartNew();
                    long nextRecordTimeMs = 0;

                    while (!token.IsCancellationRequested)
                    {
                        bool readSuccess = false;
                        lock (_lockObject)
                        {
                            var cap = isLeft ? _leftCapture : _rightCapture;
                            if (cap != null && cap.IsOpened())
                            {
                                readSuccess = cap.Read(frame);
                            }
                        }

                        if (token.IsCancellationRequested) break;

                        if (readSuccess && !frame.Empty())
                        {
                            System.Windows.Media.Imaging.WriteableBitmap? bmp = null;
                            try
                            {
                                bmp = WriteableBitmapConverter.ToWriteableBitmap(frame);
                                bmp.Freeze();
                            }
                            catch { }

                            if (bmp != null)
                            {
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (!token.IsCancellationRequested)
                                    {
                                        if (isLeft) LeftCamImage.Source = bmp;
                                        else RightCamImage.Source = bmp;
                                    }
                                }, System.Windows.Threading.DispatcherPriority.Render);
                            }

                            lock (_lockObject)
                            {
                                frame.CopyTo(lastValidFrame);
                                if (isLeft)
                                {
                                    if (_latestLeft == null) _latestLeft = new Mat();
                                    frame.CopyTo(_latestLeft);
                                }
                                else
                                {
                                    if (_latestRight == null) _latestRight = new Mat();
                                    frame.CopyTo(_latestRight);
                                }
                            }
                        }
                        else
                        {
                            Thread.Sleep(10);
                        }

                        lock (_lockObject)
                        {
                            var writer = isLeft ? _leftWriter : _rightWriter;
                            if (_isRecording && writer != null)
                            {
                                double targetFps = 30.0;
                                Dispatcher.Invoke(() =>
                                {
                                    if (isLeft && LeftFpsComboBox.SelectedItem is double fps1) targetFps = fps1;
                                    else if (!isLeft && RightFpsComboBox.SelectedItem is double fps2) targetFps = fps2;
                                });

                                double intervalMs = 1000.0 / targetFps;
                                long currentElapsed = recordStopwatch.ElapsedMilliseconds;

                                while (currentElapsed >= nextRecordTimeMs && !lastValidFrame.Empty())
                                {
                                    writer.Write(lastValidFrame);
                                    nextRecordTimeMs += (long)intervalMs;
                                    if (nextRecordTimeMs < currentElapsed - 1000) nextRecordTimeMs = currentElapsed;
                                }
                            }
                            else
                            {
                                recordStopwatch.Restart();
                                nextRecordTimeMs = 0;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MessageBox.Show($"エラー ({(isLeft ? "左" : "右")}): {ex.Message}"));
                }
                finally
                {
                    capture?.Dispose();
                }
            });
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopPreview();
        }

        private void StopPreview()
        {
            if (_isRecording) StopRecording();

            Dispatcher.Invoke(() =>
            {
                LeftCamImage.Source = null;
                RightCamImage.Source = null;
                
                StopButton.IsEnabled = false;
                RecordButton.IsEnabled = false;
                CaptureButton.IsEnabled = false;
                RecordButton.Content = "両方同時に録画開始";
                LeftCameraComboBox.IsEnabled = true;
                RightCameraComboBox.IsEnabled = true;
                
                if (BrowseButton != null) BrowseButton.IsEnabled = true;

                ValidateAndUpdateStartButtonState();
                StreamInfoText.Text = "ステータス: プレビュー停止";
            });

            _leftCts?.Cancel();
            _rightCts?.Cancel();

            lock (_lockObject)
            {
                if (_leftCapture != null)
                {
                    try { _leftCapture.Release(); } catch { }
                    _leftCapture.Dispose();
                    _leftCapture = null;
                }

                if (_rightCapture != null)
                {
                    try { _rightCapture.Release(); } catch { }
                    _rightCapture.Dispose();
                    _rightCapture = null;
                }
                
                _latestLeft?.Dispose(); 
                _latestLeft = null;
                _latestRight?.Dispose(); 
                _latestRight = null;
            }

            Thread.Sleep(1200);
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording)
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string saveDir = SavePathTextBox.Text;
                if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir))
                {
                    saveDir = AppDomain.CurrentDomain.BaseDirectory;
                }

                lock (_lockObject)
                {
                    if (LeftCameraComboBox.SelectedValue is int leftIdx && LeftResolutionComboBox.SelectedItem is ResolutionInfo leftRes)
                    {
                        double fps = LeftFpsComboBox.SelectedItem is double f1 ? f1 : 30.0;
                        int w = _leftCapture != null ? (int)_leftCapture.Get(VideoCaptureProperties.FrameWidth) : leftRes.Width;
                        int h = _leftCapture != null ? (int)_leftCapture.Get(VideoCaptureProperties.FrameHeight) : leftRes.Height;
                        
                        string fpsStr = fps.ToString("0.##").Replace(".", "_");
                        string fileName = $"{timestamp}_Cam{leftIdx}_{leftRes.CodecName}_{w}x{h}_{fpsStr}fps.mp4";
                        string path = Path.Combine(saveDir, fileName);

                        _leftWriter = new VideoWriter(path, VideoWriter.FourCC(@"X264"), fps, new OpenCvSharp.Size(w, h));
                    }

                    if (RightCameraComboBox.SelectedValue is int rightIdx && RightResolutionComboBox.SelectedItem is ResolutionInfo rightRes)
                    {
                        double fps = RightFpsComboBox.SelectedItem is double f2 ? f2 : 30.0;
                        int w = _rightCapture != null ? (int)_rightCapture.Get(VideoCaptureProperties.FrameWidth) : rightRes.Width;
                        int h = _rightCapture != null ? (int)_rightCapture.Get(VideoCaptureProperties.FrameHeight) : rightRes.Height;
                        
                        string fpsStr = fps.ToString("0.##").Replace(".", "_");
                        string fileName = $"{timestamp}_Cam{rightIdx}_{rightRes.CodecName}_{w}x{h}_{fpsStr}fps.mp4";
                        string path = Path.Combine(saveDir, fileName);

                        _rightWriter = new VideoWriter(path, VideoWriter.FourCC(@"X264"), fps, new OpenCvSharp.Size(w, h));
                    }
                }

                _isRecording = true;
                RecordButton.Content = "録画停止 (両方録画中...)";
                CaptureButton.IsEnabled = false;

                StreamInfoText.Text = "ステータス: 2カメラ同時録画中...";
            }
            else
            {
                StopRecording();
                RecordButton.Content = "両方同時に録画開始";
                CaptureButton.IsEnabled = true;

                StreamInfoText.Text = "ステータス: プレビュー中";
            }
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string saveDir = SavePathTextBox.Text;
            if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir))
            {
                saveDir = AppDomain.CurrentDomain.BaseDirectory;
            }

            lock (_lockObject)
            {
                if (_latestLeft != null && !_latestLeft.Empty() && LeftCameraComboBox.SelectedValue is int leftIdx && LeftResolutionComboBox.SelectedItem is ResolutionInfo leftRes)
                {
                    int w = (int)_latestLeft.Width;
                    int h = (int)_latestLeft.Height;
                    string fileName = $"{timestamp}_Cam{leftIdx}_{leftRes.CodecName}_{w}x{h}.png";
                    string path = Path.Combine(saveDir, fileName);
                    Cv2.ImWrite(path, _latestLeft);
                }

                if (_latestRight != null && !_latestRight.Empty() && RightCameraComboBox.SelectedValue is int rightIdx && RightResolutionComboBox.SelectedItem is ResolutionInfo rightRes)
                {
                    int w = (int)_latestRight.Width;
                    int h = (int)_latestRight.Height;
                    string fileName = $"{timestamp}_Cam{rightIdx}_{rightRes.CodecName}_{w}x{h}.png";
                    string path = Path.Combine(saveDir, fileName);
                    Cv2.ImWrite(path, _latestRight);
                }
            }

            StreamInfoText.Text = $"ステータス: 静止画を保存しました ({timestamp})";
        }

        private void StopRecording()
        {
            _isRecording = false;
            lock (_lockObject)
            {
                if (_leftWriter != null) { _leftWriter.Dispose(); _leftWriter = null; }
                if (_rightWriter != null) { _rightWriter.Dispose(); _rightWriter = null; }
            }
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            string authorName = "ranorat";

            string licenseText = 
                $"{AppTitle} v{AppVersion}\n" +
                $"Copyright (c) 2026 {authorName}\n\n" +
                $"【使用ライブラリおよびライセンス情報】\n\n" +
                $"1. OpenCvSharp (OpenCV wrapper for .NET)\n" +
                $"   License: Apache License 2.0\n" +
                $"   Copyright (c) shimat\n\n" +
                $"2. OpenCV (Open Source Computer Vision Library)\n" +
                $"   License: Apache License 2.0\n\n" +
                $"本ソフトウエアはフリーソフトウエアとして無償でご利用いただけます。";

            MessageBox.Show(licenseText, "バージョン情報 / ライセンス", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawGridOverlay(LeftGridOverlayCanvas);
            DrawGridOverlay(RightGridOverlayCanvas);
        }

        private void DrawGridOverlay(System.Windows.Controls.Canvas canvas)
        {
            canvas.Children.Clear();
            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;

            if (width <= 0 || height <= 0) return;

            var strokeBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 255, 255));

            for (int i = 1; i <= 2; i++)
            {
                double x = width * i / 3.0;
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = height,
                    Stroke = strokeBrush, StrokeThickness = 1.0,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 }
                });

                double y = height * i / 3.0;
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0, Y1 = y, X2 = width, Y2 = y,
                    Stroke = strokeBrush, StrokeThickness = 1.0,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 }
                });
            }

            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = width / 2, Y1 = height / 2 - 10, X2 = width / 2, Y2 = height / 2 + 10,
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red), StrokeThickness = 1.5
            });
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = width / 2 - 10, Y1 = height / 2, X2 = width / 2 + 10, Y2 = height / 2,
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red), StrokeThickness = 1.5
            });
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            StopPreview();
            
            _leftCts?.Dispose();
            _rightCts?.Dispose();

            Thread.Sleep(300);
        }
    }

    public class CameraInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string WinRtId { get; set; } = string.Empty;
    }

    public class ResolutionInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fourcc { get; set; }
        public string CodecName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }
}
