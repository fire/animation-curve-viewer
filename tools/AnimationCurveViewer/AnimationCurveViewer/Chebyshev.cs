using System;
using System.Collections.Generic;
using System.Linq;

namespace iim.AnimationCurveViewer
{
    public class Chebyshev
    {
        public readonly int count;
        public readonly double[] coefficients;
        public readonly double min_x;
        public readonly double max_x;

        public Chebyshev(ReadOnlySpan<double> p_coefficients, double p_min_x, double p_max_x)
        {
            count = p_coefficients.Length;
            this.coefficients = p_coefficients.ToArray();
            min_x = p_min_x;
            max_x = p_max_x;
        }

        public Chebyshev(IEnumerable<float> p_coefficients, double p_min_x, double p_max_x) : this(p_coefficients.Select(f => (double) f).ToArray(), p_min_x, p_max_x)
        {
        }

        public Chebyshev(Func<double, double> p_func, double p_min_x, double p_max_x, int p_count)
        {
            this.count = p_count;
            coefficients = new double[p_count];
            min_x = p_min_x;
            max_x = p_max_x;

            int count_k, count_j;
            double y;
            double[] f = new double[this.count];
            var bma = 0.5 * (max_x - min_x);
            var bpa = 0.5 * (max_x + min_x);
            for (count_k = 0; count_k < this.count; count_k++)
            {
                y = Math.Cos(Math.PI * (count_k + 0.5) / this.count);
                f[count_k] = p_func(y * bma + bpa);
            }
            var fac = 2.0 / this.count;
            for (count_j = 0; count_j < this.count; count_j++)
            {
                var sum = 0.0;
                for (count_k = 0; count_k < this.count; count_k++)
                {
                    sum += f[count_k] * Math.Cos(Math.PI * count_j * (count_k + 0.5) / this.count);
                }
                coefficients[count_j] = fac * sum;
            }
        }

        public int GetTruncatedCount(double p_threshold)
        {
            for (int m = count; --m >= 1;)
            {
                if (Math.Abs(coefficients[m - 1]) > p_threshold)
                {
                    return m;
                }
            }

            return 1;
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