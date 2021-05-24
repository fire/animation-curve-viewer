using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using glTFLoader;
using glTFLoader.Schema;
using Microsoft.Win32;
using Path = System.IO.Path;
using PathShape = System.Windows.Shapes.Path;

namespace iim.AnimationCurveViewer
{
    public partial class MainWindow
    {
        public MainWindow()
        {
            InitializeComponent();

            var args = Environment.GetCommandLineArgs();

            string gltfFilePath = null;

            if (args.Length < 2)
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Select glTF file",
                    Filter = "gltF files|*.gltf|glb files|*.glb|All files|*.*",
                    CheckFileExists = true
                };

                if (dlg.ShowDialog() != true)
                {
                    return;
                }

                gltfFilePath = Path.GetFullPath(dlg.FileName);
            }
            else
            {
                gltfFilePath = args[1];
            }

            Console.WriteLine($"Loading {gltfFilePath}...");

            var gltf = glTFLoader.Interface.LoadModel(gltfFilePath);

            var bufferProvider = gltf.CreateDefaultBufferProvider(gltfFilePath);

            var totalInputByteCount = 0;
            var totalOutputByteCount = 0;

            var fontFamily = new FontFamily("Consolas");

            using var chebyStream = File.Create("chevy.bin");
            using var errorStream = File.Create("errors.bin");

            var curveColors = new[]
            {
                Brushes.OrangeRed,
                Brushes.LimeGreen,
                Brushes.DodgerBlue,
                Brushes.MediumTurquoise
            };

            const float minRange = 1e-6f;
            const int curveHeight = 256;
            const int curveMargin = 10;
            const int pixelsPerFrame = 10;
            const int curveThickness = 2;

            var tabs = new TabControl();

            var keyInfo = new TextBlock
            {
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Foreground = Brushes.Yellow
            };

            keyInfo.SetValue(DockPanel.DockProperty, Dock.Top);

            var curveKindType = typeof(AnimationChannelTarget.PathEnum);
            var curveKinds = Enum.GetValues(curveKindType).Cast<AnimationChannelTarget.PathEnum>();

            var tabBackground = new SolidColorBrush(Color.FromRgb(32, 32, 32));

            var stopAtFirst = false;
            var inputByteCount = 0;
            var outputByteCount = 0;

            var countedAccessors = new HashSet<int>();

            var quantStream = new MemoryStream();
            var zipStream = new GZipStream(quantStream, CompressionLevel.Optimal, true);

            // Create a tab per animation curve kind (rotation, translation,...)
            foreach (var curveKind in curveKinds)
            {
                var tab = new TabItem
                {
                    Header = curveKind
                };

                var curveStack = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Background = tabBackground
                };

                Console.WriteLine($"Processing {gltf.Animations.Length} animations...");

                var parentTable = gltf.Nodes
                    .Where(n => n.Children != null)
                    .SelectMany(parent => parent
                        .Children.Select(c => (child: gltf.Nodes[c], parent)))
                    .ToDictionary(pair => pair.child, pair => pair.parent);

                foreach (var animation in gltf.Animations)
                {
                    for (var iChannel = 0; iChannel < animation.Channels.Length; iChannel++)
                    {
                        var channel = animation.Channels[iChannel];
                        if (channel.Target.Path != curveKind)
                            continue;

                        var targetNode = channel.Target.Node.HasValue
                            ? gltf.Nodes[channel.Target.Node.Value]
                            : null;

                        var targetNodeName = targetNode?.Name;

                        var channelTitle = $"{targetNodeName ?? "(null)"}/{animation.Name}/{curveKind}";

                        Console.WriteLine($"Processing {channelTitle}...");

                        var sampler = animation.Samplers[channel.Sampler];

                        var timesAccessor = gltf.Accessors[sampler.Input];
                        var valuesAccessor = gltf.Accessors[sampler.Output];

                        if (!timesAccessor.BufferView.HasValue || !valuesAccessor.BufferView.HasValue)
                            continue;

                        var timesSpan = gltf.GetComponentSpan(timesAccessor, bufferProvider);
                        var valuesSpan = gltf.GetComponentSpan(valuesAccessor, bufferProvider);

                        var dimension = valuesAccessor.GetComponentDimension();

                        if (countedAccessors.Add(sampler.Input))
                            inputByteCount += timesAccessor.Count * timesAccessor.GetComponentByteLength();

                        if (countedAccessors.Add(sampler.Output))
                            inputByteCount += valuesAccessor.Count * valuesAccessor.GetComponentByteLength() * dimension;

                        // Add a curve for each dimension to the canvas
                        var pointCount = timesAccessor.Count;

                        if (timesAccessor.ComponentType != Accessor.ComponentTypeEnum.FLOAT)
                            throw new NotSupportedException($"{channelTitle} has non-float time accessor");

                        if (valuesAccessor.ComponentType != Accessor.ComponentTypeEnum.FLOAT)
                            throw new NotSupportedException($"{channelTitle} has non-float values accessor");

                        var timesFloats = MemoryMarshal.Cast<byte, float>(timesSpan);
                        var valueFloats = MemoryMarshal.Cast<byte, float>(valuesSpan);

                        // Compute y starts (visual min) and scales.
                        var yStarts = new double[dimension];
                        var yScales = new double[dimension];

                        var yMin = valuesAccessor.Min;
                        var yMax = valuesAccessor.Max;

                        for (int axis = 0; axis < dimension; ++axis)
                        {
                            var center = (yMax[axis] + yMin[axis]) / 2;
                            var range = yMax[axis] - yMin[axis];

                            var isConst = Math.Abs(range) < minRange;

                            if (isConst)
                            {
                                yStarts[axis] = 0;
                                yScales[axis] = 0;
                            }
                            else
                                switch (curveKind)
                                {
                                    case AnimationChannelTarget.PathEnum.translation:
                                        yStarts[axis] = yMin[axis];
                                        yScales[axis] = 1 / range;
                                        break;
                                    case AnimationChannelTarget.PathEnum.rotation:
                                        yStarts[axis] = -1.0;
                                        yScales[axis] = 0.5;
                                        break;
                                    case AnimationChannelTarget.PathEnum.scale:
                                        yStarts[axis] = yMin[axis];
                                        yScales[axis] = 0.25;
                                        break;
                                    case AnimationChannelTarget.PathEnum.weights:
                                        yStarts[axis] = yMin[axis];
                                        yScales[axis] = 0.5;
                                        break;
                                    default:
                                        throw new ArgumentOutOfRangeException();
                                }
                        }

                        Point GetNormalizedPoint(Span<float> times, Span<float> values, int axis, int index)
                        {
                            var t = times[index];
                            var v = values[index * dimension + axis];
                            var y = (v - yStarts![axis]) * yScales![axis];
                            return new Point(t, y);
                        }

                        // Compute visual points
                        double xOffset = curveThickness;
                        double yOffset = curveThickness;
                        double yHeight = curveHeight - 2 * curveThickness;

                        Point GetVisualPoint(Span<float> times, Span<float> values, int axis, int index)
                        {
                            Point p = GetNormalizedPoint(times, values, axis, index);
                            var y = p.Y;
                            p.X = xOffset + index * pixelsPerFrame;
                            p.Y = yOffset + y * yHeight;
                            return p;
                        }

                        var curvesCanvas = new Canvas
                        {
                            Width = Math.Ceiling(xOffset + timesFloats.Length * pixelsPerFrame + 2 * curveThickness),
                            Height = (curveHeight + 2 * curveMargin),
                            Margin = new Thickness(0, curveMargin, 0, curveMargin),
                            Background = Brushes.Transparent,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            IsHitTestVisible = true
                        };

                        const int textBlockMargin = 20;

                        var animationHeader = new DockPanel
                        {
                            LastChildFill = true,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                        };

                        // Create range text blocks
                        for (int axis = 0; axis < dimension; ++axis)
                        {
                            var text = $"{(yScales[axis] == 0 ? '_' : '~')} min:{yMin[axis]:F6} max:{yMax[axis]:F6} range:{(yMax[axis] - yMin[axis]):F6}";

                            var textBlock = new TextBlock
                            {
                                FontFamily = fontFamily,
                                Foreground = curveColors[axis],
                                Text = text,
                                Margin = new Thickness(textBlockMargin, 0, textBlockMargin, 0),
                                HorizontalAlignment = HorizontalAlignment.Left
                            };
                            textBlock.SetValue(DockPanel.DockProperty, Dock.Right);
                            animationHeader.Children.Insert(0, textBlock);
                        }

                        for (int axis = 0; axis < dimension; ++axis)
                        {
                            var visualPoints = new Point[pointCount];
                            for (int i = 0; i < pointCount; ++i)
                                visualPoints[i] = GetVisualPoint(timesFloats, valueFloats, axis, i);

                            var line = Curve.ToLineGeometry(visualPoints);

                            curvesCanvas.Children.Add(new PathShape
                            {
                                Data = line,
                                Height = curveHeight,
                                Stroke = curveColors[axis],
                                StrokeThickness = curveThickness,
                                ClipToBounds = false,
                                IsHitTestVisible = false
                            });
                        }

                        // Process animation channel
                        var fitter = new ChebyshevChannelFitter3(chebyStream, errorStream);
                        fitter.Process(gltf, animation, channel, timesFloats, valueFloats, false);

                        for (int axis = 0; axis < dimension; ++axis)
                        {
                            var visualPoints = new Point[pointCount];
                            for (int i = 0; i < pointCount; ++i)
                                visualPoints[i] = GetVisualPoint(timesFloats, valueFloats, axis, i);

                            var line = Curve.ToLineGeometry(visualPoints);

                            curvesCanvas.Children.Add(new PathShape
                            {
                                Data = line,
                                Height = curveHeight,
                                Stroke = curveColors[axis],
                                StrokeThickness = curveThickness * 3,
                                ClipToBounds = false,
                                IsHitTestVisible = false,
                                Opacity = 0.5
                            });
                        }

                        animationHeader.Children.Add(new TextBlock
                        {
                            Text = $"{targetNodeName}/{animation.Name}#{iChannel} ({fitter.input_byte_count } -> { fitter.output_byte_count} ({ 1.0 * fitter.input_byte_count / fitter.output_byte_count:0.00})",
                            Foreground = Brushes.Yellow,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Margin = new Thickness(textBlockMargin, 0, textBlockMargin, 0),
                        });

                        totalInputByteCount += fitter.input_byte_count;
                        totalOutputByteCount += fitter.output_byte_count;

                        var curvesScroller = new ScrollViewer
                        {
                            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                            Content = curvesCanvas
                        };

                        var animationGroup = new StackPanel
                        {
                            Orientation = Orientation.Vertical,
                            Children =
                            {
                                animationHeader,
                                curvesScroller
                            }
                        };

                        var frameMarker = new Line
                        {
                            Stroke = Brushes.Yellow,
                            StrokeThickness = 1,
                            Visibility = Visibility.Hidden,
                            Y1 = 0,
                            Y2 = curvesCanvas.Height
                        };

                        curvesCanvas.Children.Add(frameMarker);
                        curveStack.Children.Add(animationGroup);

                        if (stopAtFirst)
                            break;
                    }

                    if (stopAtFirst)
                        break;
                }

                tab.Content = new ScrollViewer
                {
                    Content = curveStack
                };

                tabs.Items.Add(tab);

                if (stopAtFirst)
                    break;
            }

            Content = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    keyInfo,
                    tabs,
                }
            };

            zipStream.Close();

            outputByteCount += (int)quantStream.Length;

            Title = $"{totalInputByteCount} -> {totalOutputByteCount} ({1.0 * totalInputByteCount / totalOutputByteCount:0.00}x)";

            foreach (var path in Directory.GetFiles(Path.GetDirectoryName(gltfFilePath)))
            {
                File.Copy(path, Path.GetFileName(path), true);
            }

            for (var index = 0; index < gltf.Buffers.Length; index++)
            {
                var buffer = gltf.Buffers[index];
                File.WriteAllBytes(buffer.Uri, bufferProvider(buffer, index));
            }
        }
    }
}
