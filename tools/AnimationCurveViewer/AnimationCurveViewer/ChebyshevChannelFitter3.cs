using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using glTFLoader.Schema;

namespace iim.AnimationCurveViewer
{
    public class ChebyshevChannelFitter3
    {
        private readonly BinaryWriter _chevy_stream;
        private readonly BinaryWriter _count_stream;

        public int input_byte_count;
        public int output_byte_count;

        public ChebyshevChannelFitter3(Stream r_chevy_stream, Stream r_error_stream)
        {
            _chevy_stream = new BinaryWriter(r_chevy_stream);
            _count_stream = new BinaryWriter(r_error_stream);
        }

        public void Process(Gltf p_gltf,
            Animation p_animation, AnimationChannel p_channel,
            Span<float> r_times, Span<float> r_input_values, bool p_is_root)
        {
            var path_kind = p_channel.Target.Path;

            var accessor = p_gltf.GetChannelOutputAccessor(p_animation, p_channel);
            var dimension = accessor.GetComponentDimension();

            var sample_count = r_times.Length;

            var values = r_input_values;

            if (p_channel.Target.Path == AnimationChannelTarget.PathEnum.rotation)
            {
                // Convert to log of quaternion, assuming quaternions are unit length
                dimension = 3;

                // 4 -> 3
                input_byte_count += 4 * sample_count;

                values = new float[sample_count * 3];

                for (int sample_i = 0; sample_i < sample_count; ++sample_i)
                {
                    var q_x = r_input_values[sample_i * 4 + 0];
                    var q_y = r_input_values[sample_i * 4 + 1];
                    var q_z = r_input_values[sample_i * 4 + 2];
                    var q_w = r_input_values[sample_i * 4 + 3];

                    var q_1 = new Quaternion(q_x, q_y, q_z, q_w);
                    var v = q_1.Log();

                    values[sample_i * 3 + 0] = v.X;
                    values[sample_i * 3 + 1] = v.Y;
                    values[sample_i * 3 + 2] = v.Z;
                }
            }

            var bucket_length = 256;

            var x_points = new double[bucket_length];
            var y_points = new double[bucket_length];

            var abs_max_error = path_kind switch
            {
                AnimationChannelTarget.PathEnum.translation => 0.1,
                AnimationChannelTarget.PathEnum.rotation => 1D / 1024,
                AnimationChannelTarget.PathEnum.scale => 0.01,
                AnimationChannelTarget.PathEnum.weights => 0.01,
                _ => throw new ArgumentOutOfRangeException()
            };

            var coef_scales = Enumerable.Range(0, 100).Select(i => Math.Exp(i) / abs_max_error).ToArray();

            const int bytes_per_coef = 2;

            for (int axis_i = 0; axis_i < dimension; ++axis_i)
            {
                for (int point_start_i = 0; point_start_i < sample_count; point_start_i += bucket_length)
                {
                    var point_count = Math.Min(bucket_length, sample_count - point_start_i);

                    if (point_count < 2)
                    {
                        // Not enough points in bucket to do compression.
                        output_byte_count += 1 + 4 * point_count;
                        input_byte_count += 4 * point_count;
                        continue;
                    }

                    for (int point_i = 0; point_i < point_count; ++point_i)
                    {
                        var j = (point_start_i + point_i) * dimension + axis_i;
                        var t = r_times[point_start_i + point_i];
                        var v = values[j];
                        x_points[point_i] = t;
                        y_points[point_i] = v;
                    }

                    // Build a spline through all the points
                    alglib.spline1dbuildakima(x_points, y_points, point_count, out var spline);

                    (int best_cheby, int best_end, double best_compression)[] FitChevy(int start_index, int max_cheby_count)
                    {
                        var fits = Enumerable.Range(1, max_cheby_count + 1).AsParallelInRelease().Select(chebyCount =>
                          {
                              var pick_cheby_count = 1;
                              var pick_compression = 0.0;
                              var pick_end = start_index;

                              for (int end = point_count - start_index < max_cheby_count ? point_count - 1 : start_index + 1; end < point_count; ++end)
                              {
                                  var cheby = new ChebyshevF(x => alglib.spline1dcalc(spline, x),
                                      x_points[start_index], x_points[end], chebyCount, coef_scales);

                                  // See if the curve fits.
                                  bool fits = true;

                                  for (int i = start_index + 1; i <= end; ++i)
                                  {
                                      var p_y = cheby.Evaluate(x_points[i], chebyCount);
                                      var d_y = y_points[i] - p_y;
                                      if (Math.Abs(d_y) > abs_max_error)
                                      {
                                          fits = false;
                                          break;
                                      }
                                  }

                                  if (!fits)
                                      break;

                                  double input_byte_length = (end - start_index + 1) * 4;
                                  double output_byte_length = 1 + 1 + chebyCount * bytes_per_coef;
                                  double compression = input_byte_length / output_byte_length;

                                  if (pick_compression <= compression)
                                  {
                                      pick_compression = compression;
                                      pick_cheby_count = chebyCount;
                                      pick_end = end;
                                  }
                              }

                              return (pick_cheby_count, pick_end, pickCompression:pick_compression);
                          })
                        .AsSequentialInRelease()
                        .OrderByDescending(pair => pair.pickCompression)
                        .ToArray();

                        return fits;
                    }

                    IEnumerable<int> cheby_counts = new[] { 4, 8, 16, 24, 32 };

                    var trials = cheby_counts
                        .AsParallelInRelease()
                        .Select(max_cheby_count =>
                        {
                            int start = 0;
                            int input_byte_count = 0;
                            int output_byte_count = 0;

                            var entries = new List<(int cheby_count, int start, int end, double compression)>();

                            while (start < point_count - 2)
                            {
                                // Find the best fitting Chebyshev polynomial 
                                var fits = FitChevy(start, max_cheby_count);

                                var (cheby_count, end, compression) = fits.First();

                                entries.Add((cheby_count, start, end, compression));

                                input_byte_count += (end - start + 1) * 4;

                                // 1 byte for Cheby #coefficients, 1 byte for #samples
                                output_byte_count += 1 + 1 + cheby_count * bytes_per_coef;

                                start = end + 1;
                            }

                            return (input_byte_count, output_byte_count, entries);
                        })
                        .AsSequentialInRelease()
                        .ToArray();

                    var best_trial = trials.OrderBy(trial => trial.output_byte_count).First();

                    input_byte_count += best_trial.input_byte_count;
                    output_byte_count += best_trial.output_byte_count;

                    foreach (var (cheby_count, start, end, _) in best_trial.entries)
                    {
                        var cheby = new ChebyshevF(x => alglib.spline1dcalc(spline, x),
                            x_points[start], x_points[end], cheby_count, coef_scales);

                        _count_stream.Write((byte)cheby.Count);
                        _count_stream.Write((byte)(start - end + 1));
                        foreach (var coef in cheby.FixedPoints)
                        {
                            _chevy_stream.Write(coef);
                        }

                        for (int i = start; i <= end; ++i)
                        {
                            var j = (point_start_i + i) * dimension + axis_i;
                            var py = cheby.Evaluate(x_points[i], cheby_count);
                            values[j] = (float)py;
                        }
                    }
                }
            }

            if (p_channel.Target.Path == AnimationChannelTarget.PathEnum.rotation)
            {
                // Convert back to quaternions
                for (int sample_i = 0; sample_i < sample_count; ++sample_i)
                {
                    var n_x = values[sample_i * 3 + 0];
                    var n_y = values[sample_i * 3 + 1];
                    var n_z = values[sample_i * 3 + 2];

                    var q = new Vector3(n_x, n_y, n_z).Exp();

                    r_input_values[sample_i * 4 + 0] = q.X;
                    r_input_values[sample_i * 4 + 1] = q.Y;
                    r_input_values[sample_i * 4 + 2] = q.Z;
                    r_input_values[sample_i * 4 + 3] = q.W;
                }
            }
        }
    }
}