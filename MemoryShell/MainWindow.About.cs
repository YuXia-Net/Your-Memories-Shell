using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var (version, build) = AboutVersion();
        var settings = new UISettings();
        var contrast = new AccessibilitySettings().HighContrast;
        var ink = contrast ? new SolidColorBrush(settings.GetColorValue(UIColorType.Background)) : AboutBrush(0x020C38);
        var paper = contrast ? new SolidColorBrush(settings.GetColorValue(UIColorType.Foreground)) : AboutBrush(0xF5FBFF);
        var aqua = contrast ? paper : AboutBrush(0xA7F5FF);
        var blue = AboutBrush(0x1354FF);
        var width = Math.Clamp(Root.ActualWidth - 32, 300, 950);
        var compact = width < 630;
        var stage = new Grid
        {
            Height = compact ? 330 : 433,
            Background = contrast ? ink : AboutGradient((0, 0xC5FAFF), (.19, 0x39DFFF), (.49, 0x0757F6), (.75, 0x0B238A), (1, 0x020A31)),
            Clip = new RectangleGeometry()
        };
        stage.SizeChanged += (_, e) => ((RectangleGeometry)stage.Clip).Rect =
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

        var water = new Grid { IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
        var cuts = new Grid { IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
        var glint = new Grid { IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
        var bubbles = new Canvas { IsHitTestVisible = false };
        var bubbleDrift = new List<(CompositeTransform Transform, Ellipse Bubble, int Index)>();
        if (!contrast)
        {
            water.Children.Add(WaterRibbon(.19, paper, .61));
            water.Children.Add(WaterRibbon(.31, AboutBrush(0x63E5FF), .66));
            water.Children.Add(WaterRibbon(.43, blue, .82));
            water.Children.Add(WaterRibbon(.58, ink, .96));
            cuts.Children.Add(AboutShard(ink, .86, (0, .20), (.27, 0), (.35, 0), (0, .32)));
            cuts.Children.Add(AboutShard(aqua, .91, (0, .35), (.57, 0), (.59, 0), (0, .39)));
            cuts.Children.Add(AboutShard(paper, .85, (.13, 1), (.97, 0), (1, 0), (.35, 1)));
            cuts.Children.Add(AboutShard(blue, .84, (.27, 1), (1, .06), (1, .16), (.42, 1)));
            cuts.Children.Add(AboutShard(aqua, .76, (.50, 1), (1, .50), (1, .54), (.56, 1)));
            cuts.Children.Add(AboutShard(paper, .88, (.75, 1), (1, .77), (1, .81), (.81, 1)));
            glint.Children.Add(AboutShard(paper, .28, (0, .90), (.48, 0), (.63, 0), (.16, 1)));
            glint.Children.Add(AboutShard(aqua, .53, (.48, 1), (1, .24), (1, .28), (.53, 1)));
            glint.Children.Add(AboutShard(paper, .54, (.07, .77), (.51, .13), (.53, .13), (.09, .78)));
            for (var i = 0; i < 19; i++)
            {
                var size = 3 + i * 7 % 24;
                var transform = new CompositeTransform();
                var bubble = new Ellipse
                {
                    Width = size, Height = size, Stroke = i % 4 == 0 ? paper : aqua,
                    StrokeThickness = i % 4 == 0 ? 2 : 1, Opacity = .42,
                    Fill = i % 5 == 0 ? aqua : null, RenderTransform = transform
                };
                bubbles.Children.Add(bubble);
                bubbleDrift.Add((transform, bubble, i));
            }
            stage.SizeChanged += (_, e) =>
            {
                foreach (var (_, bubble, index) in bubbleDrift)
                {
                    Canvas.SetLeft(bubble, e.NewSize.Width * (index * 37 % 97) / 100);
                    Canvas.SetTop(bubble, e.NewSize.Height * (15 + index * 29 % 70) / 100);
                }
            };
        }
        stage.Children.Add(water);
        stage.Children.Add(cuts);
        stage.Children.Add(glint);
        stage.Children.Add(bubbles);
        var caption = AboutText(L10n.Get("AboutCaption"), compact ? 30 : 40, ink);
        caption.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        caption.CharacterSpacing = 160;
        caption.RenderTransform = new SkewTransform { AngleX = 12 };
        var captionPlate = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 18, 0, 0), RenderTransform = new CompositeTransform { Rotation = -7, SkewX = -12 }
        };
        captionPlate.Children.Add(new Border { Background = blue, Margin = new Thickness(8, 8, -8, -8) });
        captionPlate.Children.Add(new Border { Background = aqua, Margin = new Thickness(4, 4, -4, -4) });
        captionPlate.Children.Add(new Border { Background = paper, Padding = new Thickness(20, 2, 25, 5), Child = caption });
        stage.Children.Add(captionPlate);

        var logoSize = compact ? 115 : 206;
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/MemoryShell.png")),
            Width = logoSize, Height = logoSize, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var sleepingMark = AboutSleepingMark(logoSize);
        var logoHitTarget = new Grid
        {
            Width = logoSize, Height = logoSize,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            ManipulationMode = ManipulationModes.None,
            Children = { logo, sleepingMark }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(logoHitTarget, L10n.Get("S72FACCF0DD74"));
        var logoPlate = new Grid
        {
            Width = logoSize + 28, Height = logoSize + 28,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, compact ? 38 : 33, compact ? 20 : 73, 0),
            RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = new CompositeTransform { Rotation = contrast ? 0 : -12 }
        };
        if (!contrast)
        {
            logoPlate.Children.Add(AboutShard(ink, 1, (.08, .19), (.91, .01), (1, .76), (.28, 1)));
            logoPlate.Children.Add(AboutShard(blue, 1, (0, .12), (.92, 0), (.95, .74), (.20, .93)));
            logoPlate.Children.Add(AboutShard(aqua, 1, (.06, .19), (.88, .02), (1, .70), (.25, .93)));
            logoPlate.Children.Add(AboutShard(paper, 1, (.12, .09), (.87, 0), (.92, .70), (.17, .88)));
        }
        logoPlate.Children.Add(logoHitTarget);
        var logoLayer = new Grid { RenderTransform = new TranslateTransform(), Children = { logoPlate } };
        stage.Children.Add(logoLayer);

        var title = AboutText("Unknown X", compact ? (width < 380 ? 26 : 38) : 62, paper);
        title.FontFamily = new FontFamily("Arial");
        title.FontStyle = Windows.UI.Text.FontStyle.Italic;
        title.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        title.CharacterSpacing = -55;
        var typePlate = new Grid { Children = { title } };
        if (!contrast)
        {
            var shadow = AboutText(title.Text, title.FontSize, AboutBrush(0x1655FF));
            shadow.FontFamily = title.FontFamily;
            shadow.FontStyle = title.FontStyle;
            shadow.FontWeight = title.FontWeight;
            shadow.CharacterSpacing = title.CharacterSpacing;
            shadow.RenderTransform = new TranslateTransform { X = 4, Y = 4 };
            typePlate.Children.Insert(0, shadow);
        }
        var product = AboutText(L10n.Get("S1330FC51C5C3"), compact ? 15 : 20, contrast ? paper : ink);
        product.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        var productPlate = new Border
        {
            Child = product, Background = contrast ? ink : aqua, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(13, 4, 17, 5), RenderTransform = new SkewTransform { AngleX = contrast ? 0 : -14 }
        };
        var nameplate = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom, Spacing = 2,
            Margin = new Thickness(compact ? 22 : 41, 0, 16, compact ? 71 : 83),
            Children = { typePlate, productPlate }, RenderTransform = new CompositeTransform()
        };
        stage.Children.Add(nameplate);

        var release = new Grid
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = contrast ? ink : AboutBrush(0x020B33),
            Padding = new Thickness(compact ? 20 : 41, 11, 18, 12),
            BorderBrush = aqua, BorderThickness = new Thickness(0, 3, 0, 0), ColumnSpacing = 22,
            RenderTransform = new TranslateTransform()
        };
        release.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        release.ColumnDefinitions.Add(new ColumnDefinition());
        release.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ver = AboutText(L10n.Format("S6A6EA06EAF88", version), 13, paper);
        ver.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        var buildLabel = AboutText($"{L10n.Get("BuildLabel")}  {build}", 13, aqua);
        buildLabel.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Grid.SetColumn(buildLabel, 1);
        release.Children.Add(ver);
        release.Children.Add(buildLabel);
        var projectLink = ProjectLink(paper);
        Grid.SetColumn(projectLink, 2); release.Children.Add(projectLink);
        stage.Children.Add(release);

        var dialog = Dialog("", new ScrollViewer
        {
            Width = width - 56, Content = stage,
            MaxHeight = Math.Max(160, Root.ActualHeight - 118),
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        }, L10n.Get("S2D1EC50CAF71"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, L10n.Get("SC33025BAAD42"));
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(width, 320);
        dialog.CloseButtonText = L10n.Get("S3FD47EDCE45B");
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try
            {
                var package = new DataPackage();
                package.SetText(L10n.Format("S76E9AD44A5AC", version, build));
                Clipboard.SetContent(package);
                dialog.PrimaryButtonText = L10n.Get("S8F6F8D979C98");
            }
            catch (Exception) { dialog.PrimaryButtonText = L10n.Get("SC4D07BC23D3D"); }
        };
        var askToClose = false;
        var stopLogoHold = BindAboutLogoHold(logoHitTarget, sleepingMark, () =>
        {
            askToClose = true;
            dialog.Hide();
        });

        Storyboard? entrance = null;
        Storyboard? current = null;
        if (settings.AnimationsEnabled && !contrast)
        {
            dialog.Opened += (_, _) =>
            {
                entrance = new Storyboard();
                var titleMotion = (CompositeTransform)nameplate.RenderTransform;
                AboutMotion(entrance, (TranslateTransform)cuts.RenderTransform, "X", -stage.ActualWidth, 0, 600);
                AboutMotion(entrance, (TranslateTransform)logoLayer.RenderTransform, "X", 270, 0, 690);
                AboutMotion(entrance, (TranslateTransform)logoLayer.RenderTransform, "Y", -145, 0, 690);
                AboutMotion(entrance, titleMotion, "TranslateX", -180, 0, 600);
                AboutMotion(entrance, titleMotion, "TranslateY", 65, 0, 600);
                AboutMotion(entrance, titleMotion, "SkewX", -22, 0, 620);
                AboutMotion(entrance, nameplate, "Opacity", 0, 1, 490);
                AboutMotion(entrance, (TranslateTransform)release.RenderTransform, "Y", 45, 0, 720);
                entrance.Begin();

                current = new Storyboard();
                AboutMotion(current, (TranslateTransform)water.RenderTransform, "Y", -10, 13, 4200, loop: true);
                AboutMotion(current, (TranslateTransform)glint.RenderTransform, "X", -125, 190, 4800, loop: true);
                AboutMotion(current, (CompositeTransform)logoPlate.RenderTransform, "Rotation", -12, -5, 3100, loop: true);
                AboutMotion(current, (CompositeTransform)logoPlate.RenderTransform, "TranslateY", 0, 12, 3100, loop: true);
                foreach (var (transform, bubble, index) in bubbleDrift)
                {
                    AboutMotion(current, transform, "TranslateY", 8, -34 - index % 5 * 10, 2200 + index * 151, loop: true);
                    AboutMotion(current, bubble, "Opacity", .17, .64, 2200 + index * 151, loop: true);
                }
                current.Begin();
            };
        }

        try { await dialog.ShowAsync(); }
        finally
        {
            stopLogoHold();
            entrance?.Stop();
            current?.Stop();
        }
        if (askToClose && !closed)
        {
            var confirm = Dialog(L10n.Get("SB676597E1697"), L10n.Format("S8999438BACFD", Environment.UserName), L10n.Get("S3FD47EDCE45B"));
            confirm.DefaultButton = ContentDialogButton.Close;
            if (await confirm.ShowAsync() == ContentDialogResult.Primary && !closed)
                Close();
        }
    }

    private static Canvas AboutSleepingMark(double size)
    {
        var mark = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var scale = size / 256;
        Add(57, 96, 120, 78, 0x0B162D);
        Add(60, 152.5, 53, 18, 0xF2FAFF);
        Add(116, 152.5, 59, 18, 0x57DCFF);
        return mark;

        void Add(double x, double y, double width, double height, int color)
        {
            var rectangle = new Rectangle { Width = width * scale, Height = height * scale, Fill = AboutBrush(color) };
            Canvas.SetLeft(rectangle, x * scale);
            Canvas.SetTop(rectangle, y * scale);
            mark.Children.Add(rectangle);
        }
    }

    private static Action BindAboutLogoHold(Grid target, UIElement sleepingMark, Action completed)
    {
        var timer = target.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.IsRepeating = false;
        uint? heldPointer = null;
        void CancelHold(bool restoreMark = true)
        {
            var wasHeld = heldPointer.HasValue;
            heldPointer = null;
            timer.Stop();
            if (restoreMark)
                sleepingMark.Visibility = Visibility.Collapsed;
            if (wasHeld)
                target.ReleasePointerCaptures();
        }

        target.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(target);
            var isMouse = args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
            if (heldPointer.HasValue || (isMouse ? !point.Properties.IsLeftButtonPressed : !point.IsInContact))
                return;

            target.CancelDirectManipulations();
            if (!target.CapturePointer(args.Pointer))
                return;

            heldPointer = args.Pointer.PointerId;
            sleepingMark.Visibility = Visibility.Visible;
            timer.Start();
            args.Handled = true;
        };
        target.PointerMoved += (_, args) =>
        {
            if (heldPointer != args.Pointer.PointerId)
                return;
            var position = args.GetCurrentPoint(target).Position;
            if (position.X < 0 || position.Y < 0 || position.X >= target.ActualWidth || position.Y >= target.ActualHeight)
                CancelHold();
            args.Handled = true;
        };
        void EndHold(object sender, PointerRoutedEventArgs args)
        {
            if (heldPointer != args.Pointer.PointerId)
                return;
            CancelHold();
            args.Handled = true;
        }
        target.PointerReleased += EndHold;
        target.PointerCanceled += EndHold;
        target.PointerCaptureLost += EndHold;
        target.Unloaded += (_, _) => CancelHold();
        timer.Tick += (_, _) =>
        {
            if (!heldPointer.HasValue)
                return;
            CancelHold(restoreMark: false);
            completed();
        };
        return () => CancelHold();
    }

    private static (string Version, string Build) AboutVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3) ?? "—";
        var build = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "Build")?.Value;
        if (string.IsNullOrWhiteSpace(build))
        {
            var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            build = Version.TryParse(fileVersion, out var parsed) && parsed.Revision >= 0
                ? parsed.Revision.ToString(CultureInfo.InvariantCulture) : "—";
        }
        return (version, build);
    }

    private static Color AboutColor(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static SolidColorBrush AboutBrush(int rgb) => new(AboutColor(rgb));

    private static LinearGradientBrush AboutGradient(params (double Offset, int Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(.9, 0), EndPoint = new Point(.19, 1) };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = AboutColor(color) });
        return brush;
    }

    private static TextBlock AboutText(string text, double size, Brush brush) =>
        new() { Text = text, FontSize = size, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis };

    private static Microsoft.UI.Xaml.Shapes.Path AboutShard(Brush fill, double opacity, params (double X, double Y)[] points)
    {
        var figure = new PathFigure { StartPoint = new Point(points[0].X, points[0].Y), IsClosed = true };
        foreach (var point in points.Skip(1))
            figure.Segments.Add(new LineSegment { Point = new Point(point.X, point.Y) });
        var geometry = new GeometryGroup();
        geometry.Children.Add(new PathGeometry { Figures = { figure } });
        geometry.Children.Add(new LineGeometry { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) });
        return new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = fill, Stretch = Stretch.Fill, Opacity = opacity };
    }

    private static Microsoft.UI.Xaml.Shapes.Path WaterRibbon(double depth, Brush fill, double opacity)
    {
        var figure = new PathFigure { StartPoint = new Point(0, depth + .18), IsClosed = true };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(.31, depth + .26), Point2 = new Point(.65, depth - .20), Point3 = new Point(1, depth - .12)
        });
        figure.Segments.Add(new LineSegment { Point = new Point(1, 1) });
        figure.Segments.Add(new LineSegment { Point = new Point(0, 1) });
        var geometry = new GeometryGroup();
        geometry.Children.Add(new PathGeometry { Figures = { figure } });
        geometry.Children.Add(new LineGeometry { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) });
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry, Fill = fill, Stretch = Stretch.Fill, Opacity = opacity,
            Margin = new Thickness(0, -12, 0, -12)
        };
    }

    private static void AboutMotion(Storyboard storyboard, DependencyObject target, string property,
        double from, double to, int milliseconds, bool loop = false)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = loop ? EasingMode.EaseInOut : EasingMode.EaseOut }
        };
        if (loop)
        {
            animation.AutoReverse = true;
            animation.RepeatBehavior = RepeatBehavior.Forever;
        }
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}

