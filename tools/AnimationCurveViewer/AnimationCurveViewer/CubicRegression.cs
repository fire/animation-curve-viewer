﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace iim.AnimationCurveViewer
{
    public static class CubicRegression
    {
        private struct Coefficients
        {
            /// <summary>
            /// Sjk = sum of u[i]^j * v[i]^k where (u[i],v[i]) are the coordinates of sample#i relative to the segment's FirstSample
            /// </summary>
            public double S60;
            public double S50;
            public double S40;
            public double S30;
            public double S20;
            public double S10;
            public double S00;
            public double S31;
            public double S21;
            public double S11;
            public double S01;

            public Coefficients(Vector v)
            {
                S00 = 1;

                S10 = v.X;
                S01 = v.Y;

                S20 = S10 * S10;
                S30 = S20 * S10;
                S40 = S20 * S20;
                S50 = S20 * S30;
                S60 = S30 * S30;

                S11 = S10 * S01;
                S21 = S20 * S01;
                S31 = S30 * S01;
            }

            public void Add(in Coefficients c)
            {
                S60 += c.S60;
                S50 += c.S50;
                S40 += c.S40;
                S30 += c.S30;
                S20 += c.S20;
                S10 += c.S10;
                S00 += c.S00;
                S31 += c.S31;
                S21 += c.S21;
                S11 += c.S11;
                S01 += c.S01;
            }

            public CubicSegment GetStartSegment(Point startPoint, Point endPoint, double epsilon)
            {
                // Need at least 4 points
                if (S00 < 4)
                    return null;

                Mat4D A = new Mat4D(
                    S60, S50, S40, S30,
                    S50, S40, S30, S20,
                    S40, S30, S20, S10,
                    S30, S20, S10, 1);

                double det = A.Determinant();
                if (Math.Abs(det) < epsilon)
                    return null;

                Mat4D M;
                A.Adjoints(out M);

                Vec4D V = new Vec4D(S31 / det, S21 / det, S11 / det, S01 / det);

                Vec4D abcd;
                M.Product(ref V, out abcd);

                return new CubicSegment(abcd.X, abcd.Y, abcd.Z, abcd.W, startPoint, endPoint);
            }

            public CubicSegment GetConnectedSegment(double fixedC, Point startPoint, Point endPoint, double epsilon)
            {
                // Need at least 3 points
                if (S00 < 3)
                    return null;

                // a*S60 + b*S50 + d*S30 = S31 - c*S40
                // a*S50 + b*S40 + d*S20 = S21 - c*S30
                // a*S30 + b*S20 + d     = S01 - c*S10

                var A = new Mat3D(
                    S60, S50, S30,
                    S50, S40, S20,
                    S30, S20, 1);

                double det = A.Determinant();
                if (Math.Abs(det) < epsilon)
                    return null;

                Mat3D M;
                A.Adjoints(out M);

                var V = new Vector3((S31 - fixedC * S40) / det, (S21 - fixedC * S30) / det, (S01 - fixedC * S10) / det);

                Vector3 abd;
                M.Product(ref V, out abd);

                return new CubicSegment(abd.X, abd.Y, fixedC, abd.Z, startPoint, endPoint);
            }
        }

        /*
        private struct State
        {
            public Coefficients Coefficients;
            public CubicSegment Segment;
            public readonly bool IsStart;
            public readonly double FixedC;
            public readonly Point StartPoint;

            public State(Point point, bool isStart, double fixedC) : this()
            {
                StartPoint = point;
                Coefficients.S00 = 1;
                IsStart = isStart;
                FixedC = fixedC;
            }

            public CubicSegment GetNextCubicSegment(Point point, double epsilon)
            {
                Coefficients.Add(new Coefficients(point - StartPoint));

                // Need at least 4 points
                if (Coefficients.S00 < 4)
                    return null;

                // Our cubic segment has the equation f(u) = a*u^3 + b*u^2 + c*u + d
                // Each segment is defined relative to an origin close to its first sample point, to improve floating point precision
                //
                // So 0 <= u < width of segment
                //
                // So to evaluate the absolute y-value at absolute x-position, we compute f(x-x0)+y0,
                // where (x0,y0) is the origin of the segment 
                //
                // In general, to fit a cubic curve to a set of samples, we must fit the solutions of 
                // df/da = 0, df/db = 0, df/dc = 0, df/dd = 0
                // where f = ∑(u,v) (a*u^3 + b*u^2 + c*u + d - v)^2
                // and (u,v) are the relative coordinates of the samples
                //
                // These partial derivative equations are (see Brainstorm.wxmx for wxMaxima)
                // df/da = 0 <=> 0 = 2*a*S60 + 2*b*S50 + 2*c*S40 - 2*S31 + 2*d*S30
                // df/db = 0 <=> 0 = 2*a*S50 + 2*b*S40 + 2*c*S30 - 2*S21 + 2*d*S20
                // df/dc = 0 <=> 0 = 2*a*S40 + 2*b*S30 + 2*c*S20 - 2*S11 + 2*d*S10
                // df/dd = 0 <=> 0 = 2*a*S30 + 2*b*S20 + 2*c*S10 - 2*S01 + 2*d
                //
                // where Sjk = ∑(u,v) u^j * v^k 
                //
                // This simplifies to
                // a*S60 + b*S50 + c*S40 + d*S30 = S31 
                // a*S50 + b*S40 + c*S30 + d*S20 = S21 
                // a*S40 + b*S30 + c*S20 + d*S10 = S11 
                // a*S30 + b*S20 + c*S10 + d	 = S01
                // <=>
                // [S60,S50,S40,S30] [a]   [S31]
                // [S50,S40,S30,S20] [b]   [S21]
                // [S40,S30,S20,S10] [c] = [S11]
                // [S30,S20,S10,  1] [d]   [S01]

                return IsStart
                    ? Coefficients.GetStartSegment(epsilon, StartPoint, point, Segment)
                    : Coefficients.GetConnectedSegment(FixedC, epsilon, StartPoint, point, Segment);
            }
        }
        */

        /// <summary>
        /// Largest squared error
        /// </summary>
        private static double ComputeError(CubicSegment segment, Point[] points, int startIndex, int stopIndex)
        {
            double maxError = 0;

            for (var index = startIndex; index <= stopIndex; index++)
            {
                var point = points[index];
                var u = point.X - segment.FirstPoint.X;
                var v = point.Y - segment.FirstPoint.Y;
                double vPred = segment.RelEvaluate(u);
                double dvPred = v - vPred;
                maxError = Math.Max(maxError, dvPred * dvPred);
            }

            return maxError;
        }

        public static List<CubicSegment> FitCubics(Point[] points, double maxError, double epsilon = float.Epsilon)
        {
            var segments = new List<CubicSegment>();

            Coefficients coefficients = default;
            // CubicSegment fittingSegment = default;
            bool isStart = true;
            double fixedC = 0;
            Point startPoint = default;
            int startIndex = 0;
            bool restart = true;

            var fittingPairs = new List<(CubicSegment, int)>();

            for (var index = 0; index < points.Length;)
            {
                var point = points[index];

                if (restart)
                {
                    startPoint = point;
                    startIndex = index;
                    coefficients = new Coefficients(default);
                    restart = false;
                    fittingPairs.Clear();
                    ++index;
                    continue;
                }

                coefficients.Add(new Coefficients(point - startPoint));

                var nextSegment = isStart
                    ? coefficients.GetStartSegment(startPoint, point, epsilon)
                    : coefficients.GetConnectedSegment(fixedC, startPoint, point, epsilon);

                if (nextSegment == null)
                {
                    // Can't compute yet (e.g less than 4 points)
                    ++index;
                    continue;
                }

                var error = ComputeError(nextSegment, points, startIndex, index);

                if (error <= maxError)
                {
                    // next segment fits, continue.
                    fittingPairs.Add((nextSegment, index));
                    ++index;
                    continue;
                }

                // Next segment doesn't fit anymore, restart
                if (fittingPairs.Count == 0)
                    throw new InvalidOperationException("Expected at least one fitting cubic segment!");

                var (fittingSegment, fittingIndex) = fittingPairs.Last();

                var (zeroX1, zeroX2, zeroCount) = fittingSegment.ZeroAbsDerivatives;


                CubicSegment prevSegment = null;
                int prevIndex = 0;

                var zeroX = zeroX2 > fittingSegment.LastPoint.X ? zeroX1 : zeroX2;

                if (zeroCount > 0 && zeroX > fittingSegment.FirstPoint.X && zeroX <= fittingSegment.LastPoint.X)
                {
                    for (int i = fittingPairs.Count; --i >= 0; )
                    {
                        (prevSegment, prevIndex) = fittingPairs[i];
                        if (prevSegment.LastPoint.X <= zeroX)
                        {
                            fittingPairs.Clear();
                            break;
                        }
                    }
                }

                if (fittingPairs.Count > 0)
                {
                    (prevSegment, prevIndex) = fittingPairs.Last();
                }

                segments.Add(prevSegment);
                index = prevIndex;
                fixedC = prevSegment!.AbsDerivative(prevSegment.LastPoint.X);
                restart = true;
                isStart = false;
                fittingPairs.Clear();
            }

            if (fittingPairs.Count > 0)
            {
                var (fittingSegment, _) = fittingPairs.Last();
                segments.Add(fittingSegment);
            }

            return segments;
        }
    }
}