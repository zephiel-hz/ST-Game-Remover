using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SteamPluginManager.UI.Dialogs
{
    public partial class ImageCropperDialog : Window
    {
        private BitmapSource? _originalBitmap;
        private Point _lastMousePosition;
        private bool _isDragging = false;
        private const double AvatarDiameter = 240.0;

        public byte[]? CroppedResultBytes { get; private set; }

        public ImageCropperDialog()
        {
            InitializeComponent();
            Loaded += ImageCropperDialog_Loaded;
            SizeChanged += ImageCropperDialog_SizeChanged;
        }

        public static byte[]? ShowAndCrop(Window? owner, string initialFilePath)
        {
            try
            {
                var dialog = new ImageCropperDialog();
                if (owner != null) dialog.Owner = owner;
                
                if (!dialog.LoadImage(initialFilePath))
                {
                    return null;
                }

                if (dialog.ShowDialog() == true)
                {
                    return dialog.CroppedResultBytes;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ImageCropperDialog] Error showing cropper: {ex.Message}");
            }
            return null;
        }

        private void ImageCropperDialog_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateCircularMask();
        }

        private void ImageCropperDialog_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCircularMask();
        }

        public bool LoadImage(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                _originalBitmap = bitmap;
                SourceImageDisplay.Source = _originalBitmap;

                // Reset transforms
                ImageScaleTransform.ScaleX = 1.0;
                ImageScaleTransform.ScaleY = 1.0;
                ImageTranslateTransform.X = 0;
                ImageTranslateTransform.Y = 0;
                ZoomSlider.Value = 1.0;

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ImageCropperDialog] Failed to load image {filePath}: {ex.Message}");
                return false;
            }
        }

        private void UpdateCircularMask()
        {
            double width = CropCanvasContainer.ActualWidth;
            double height = CropCanvasContainer.ActualHeight;
            if (width <= 0 || height <= 0) return;

            // Full viewport rectangle
            var fullRect = new RectangleGeometry(new Rect(0, 0, width, height));

            // Central circle
            var center = new Point(width / 2.0, height / 2.0);
            var circle = new EllipseGeometry(center, AvatarDiameter / 2.0, AvatarDiameter / 2.0);

            // Subtract circle from rectangle
            var combined = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, circle);
            CircularMaskPath.Data = combined;
        }

        private void CropCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isDragging = true;
                _lastMousePosition = e.GetPosition(CropCanvasContainer);
                CropCanvasContainer.CaptureMouse();
            }
        }

        private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                var currentPos = e.GetPosition(CropCanvasContainer);
                var deltaX = currentPos.X - _lastMousePosition.X;
                var deltaY = currentPos.Y - _lastMousePosition.Y;

                ImageTranslateTransform.X += deltaX;
                ImageTranslateTransform.Y += deltaY;

                _lastMousePosition = currentPos;
            }
        }

        private void CropCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                CropCanvasContainer.ReleaseMouseCapture();
            }
        }

        private void CropCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomFactor = e.Delta > 0 ? 1.1 : 0.9;
            double newZoom = ZoomSlider.Value * zoomFactor;
            newZoom = Math.Clamp(newZoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
            ZoomSlider.Value = newZoom;
            e.Handled = true;
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ImageScaleTransform != null)
            {
                ImageScaleTransform.ScaleX = e.NewValue;
                ImageScaleTransform.ScaleY = e.NewValue;
            }
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + 0.2);
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - 0.2);
        }

        private void ChooseDifferentPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select Profile Picture",
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All Files (*.*)|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog(this) == true)
            {
                LoadImage(openFileDialog.FileName);
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_originalBitmap == null)
            {
                DialogResult = false;
                Close();
                return;
            }

            try
            {
                // Measure crop bounding box
                double containerW = CropCanvasContainer.ActualWidth;
                double containerH = CropCanvasContainer.ActualHeight;
                double cropRadius = AvatarDiameter / 2.0;
                var cropCenter = new Point(containerW / 2.0, containerH / 2.0);

                var cropRect = new Rect(
                    cropCenter.X - cropRadius,
                    cropCenter.Y - cropRadius,
                    AvatarDiameter,
                    AvatarDiameter
                );

                // Render the cropped area into a 256x256 high-resolution bitmap
                int targetSize = 256;
                var renderBitmap = new RenderTargetBitmap(
                    (int)containerW,
                    (int)containerH,
                    96, 96,
                    PixelFormats.Pbgra32);

                // Hide mask & border temporarily during render
                CircularMaskPath.Visibility = Visibility.Hidden;
                AvatarCircleBorder.Visibility = Visibility.Hidden;

                renderBitmap.Render(CropCanvasContainer);

                // Restore visibility
                CircularMaskPath.Visibility = Visibility.Visible;
                AvatarCircleBorder.Visibility = Visibility.Visible;

                // Crop out the center 240x240 square
                var cropped = new CroppedBitmap(renderBitmap, new Int32Rect(
                    (int)Math.Max(0, cropRect.X),
                    (int)Math.Max(0, cropRect.Y),
                    (int)Math.Min(AvatarDiameter, containerW - cropRect.X),
                    (int)Math.Min(AvatarDiameter, containerH - cropRect.Y)
                ));

                // Scale to 256x256 target
                var finalDrawingVisual = new DrawingVisual();
                using (var dc = finalDrawingVisual.RenderOpen())
                {
                    dc.DrawImage(cropped, new Rect(0, 0, targetSize, targetSize));
                }

                var finalTargetBitmap = new RenderTargetBitmap(targetSize, targetSize, 96, 96, PixelFormats.Pbgra32);
                finalTargetBitmap.Render(finalDrawingVisual);

                // Encode to high-quality JPEG (Quality = 90)
                using var ms = new MemoryStream();
                var encoder = new JpegBitmapEncoder
                {
                    QualityLevel = 90
                };
                encoder.Frames.Add(BitmapFrame.Create(finalTargetBitmap));
                encoder.Save(ms);

                CroppedResultBytes = ms.ToArray();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ImageCropperDialog] Failed to crop image: {ex.Message}");
                ModernMessageBox.Show($"Failed to crop image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

