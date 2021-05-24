using System;
using System.Collections.Generic;
using System.Linq;

namespace iim.AnimationCurveViewer
{
    public class ChebyshevF
    {
        public readonly int count;
        public readonly double[] coefficients;
        public readonly short[] fixed_points;
        public readonly double min_x;
        public readonly double max_x;

        public ChebyshevF(Func<double, double> p_func, double p_min_x, double p_max_x, int p_count, double[] p_scales)
        {
            this.count = p_count;
            coefficients = new double[p_count];
            fixed_points = new short[p_count];
            min_x = p_min_x;
            max_x = p_max_x;

            int k, j;
            double y;
            double[] f = new double[this.count];
            var bma = 0.5 * (max_x - min_x);
            var bpa = 0.5 * (max_x + min_x);
            for (k = 0; k < this.count; k++)
            {
                y = Math.Cos(Math.PI * (k + 0.5) / this.count);
                f[k] = p_func(y * bma + bpa);
            }
            var fac = 2.0 / this.count;
            for (j = 0; j < this.count; j++)
            {
                var sum = 0.0;
                for (k = 0; k < this.count; k++)
                    sum += f[k] * Math.Cos(Math.PI * j * (k + 0.5) / this.count);

                // We store coefficients as fixed points.
                var c = fac * sum;
                fixed_points[j] = (short)Math.Round(c * p_scales[j]);
                coefficients[j] = fixed_points[j] / p_scales[j];
            }
        }

        public double Evaluate(double p_x, int p_m)
        {
            double d = 0.0, dd = 0.0, y;
            int j;
            if ((p_x - min_x) * (p_x - max_x) > 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(p_x));
            }

            var y2 = 2.0 * (y = (2.0 * p_x - min_x - max_x) / (max_x - min_x));

            for (j = p_m - 1; j > 0; j--)
            {
                var sv = d;
                d = y2 * d - dd + coefficients[j];
                dd = sv;
            }
            return y * d - dd + 0.5 * coefficients[0];
        }

        public double Evaluate(double p_x) => Evaluate(p_x, count);
    }
}